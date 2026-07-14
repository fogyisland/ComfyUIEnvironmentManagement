using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public enum BulkUpdateMode { SelectEnv, Running, Summary }

public class BulkUpdateDialogViewModel : ViewModelBase
{
    private readonly BulkUpdateOrchestrator _orchestrator;
    private CancellationTokenSource _runCts = new();

    private BulkUpdateSummary? _summary;
    private BulkUpdateMode _mode = BulkUpdateMode.SelectEnv;
    private string? _bulkId;
    private string? _errorMessage;
    private bool _isBusy;

    public ObservableCollection<EnvRow> EnvRows { get; } = new();
    public ObservableCollection<BulkUpdateRow> Rows { get; } = new();
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }

    public BulkUpdateSummary? Summary
    {
        get => _summary;
        set { _summary = value; RaisePropertyChanged(); }
    }

    public BulkUpdateMode Mode
    {
        get => _mode;
        set { _mode = value; RaisePropertyChanged(); }
    }

    public string? BulkId
    {
        get => _bulkId;
        set { _bulkId = value; RaisePropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            RaisePropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    public BulkUpdateDialogViewModel(BulkUpdateOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart());
        CancelCommand = new RelayCommand(
            _ => Cancel(),
            _ => IsBusy && Mode == BulkUpdateMode.Running);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll());

        _orchestrator.Progress += OnProgress;
        _orchestrator.Completed += OnCompleted;
        _orchestrator.Cancelled += OnCancelled;
    }

    public void LoadEnvs(IEnumerable<EnvRow> envs)
    {
        EnvRows.Clear();
        foreach (var e in envs) EnvRows.Add(e);
    }

    private void ToggleSelectAll()
    {
        var allSelected = EnvRows.All(e => e.Selected);
        foreach (var e in EnvRows) e.Selected = !allSelected;
        StartCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart() =>
        !IsBusy
        && EnvRows.Any(e => e.Selected)
        && EnvRows.Where(e => e.Selected)
            .SelectMany(e => e.Nodes)
            .Any(n => n.Selected);

    public List<string> SelectedEnvIds() =>
        EnvRows.Where(e => e.Selected).Select(e => e.EnvId).ToList();

    public List<string> SelectedNodeIds() =>
        EnvRows.Where(e => e.Selected)
            .SelectMany(e => e.Nodes)
            .Where(n => n.Selected)
            .Select(n => n.NodeId)
            .Distinct()
            .ToList();

    private void Start()
    {
        var envIds = SelectedEnvIds();
        var nodeIds = SelectedNodeIds();
        if (envIds.Count == 0 || nodeIds.Count == 0) return;

        // 预填 Rows —— 一个 (env, node) 一条 "pending"。Orchestrator 的 Progress
        // 事件从背景任务发,我们用索引直接更新对应 row 而无需每次都遍历查找。
        Rows.Clear();
        for (int i = 0; i < envIds.Count; i++)
        {
            for (int j = 0; j < nodeIds.Count; j++)
            {
                Rows.Add(new BulkUpdateRow(envIds[i], nodeIds[j], "pending", null, 0));
            }
        }

        // 旧 CTS 释放 —— 上一轮如果意外没释放,以这里为权威源。
        try { _runCts.Dispose(); } catch { }
        _runCts = new CancellationTokenSource();

        Mode = BulkUpdateMode.Running;
        IsBusy = true;
        ErrorMessage = null;
        BulkId = _orchestrator.CurrentBulkId; // StartAsync 前为空,Orchestrator 启动后才填

        _ = _orchestrator.StartAsync(envIds, nodeIds, _runCts.Token)
            .ContinueWith(t => DispatcherHelper.RunOnUiAsync(() => OnRunFinished(t)));
    }

    private void Cancel()
    {
        if (!IsBusy) return;
        _orchestrator.CancelAsync();
        try { _runCts.Cancel(); } catch { }
    }

    // -------- Orchestrator event handlers (called from background task) --------

    private void OnProgress(BulkUpdateRow row)
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            // 找到现有的 pending / running 行,直接替换 —— 其它字段(env/node)不变,
            // 只更新 Status/Reason/LatencyMs。
            for (int i = 0; i < Rows.Count; i++)
            {
                var existing = Rows[i];
                if (existing.EnvId == row.EnvId && existing.NodeId == row.NodeId
                    && existing.Status is "pending" or "running")
                {
                    Rows[i] = row;
                    return;
                }
            }
            // 兜底:没找到就 append(不应该发生 —— 我们 Start 时已预填)
            Rows.Add(row);
        });
    }

    private void OnCompleted(BulkUpdateSummary summary)
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            BulkId ??= summary.Rows.Count > 0 ? "(已完成)" : null;
            Summary = summary;
            Mode = BulkUpdateMode.Summary;
            IsBusy = false;
            StartCommand.RaiseCanExecuteChanged();
        });
    }

    private void OnCancelled()
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            ErrorMessage = "已取消";
        });
    }

    private void OnRunFinished(Task<BulkUpdateSummary> task)
    {
        // Orchestrator 的 Completed 事件已经把 Summary / Mode 设好了。
        // 这里只处理异常 + 最终收尾(IsBusy 在 OnCompleted 里已设 false,这里冗余也无害)。
        if (task.IsFaulted)
        {
            var msg = task.Exception?.GetBaseException().Message
                ?? "未知错误";
            ErrorMessage = $"运行失败:{msg}";
            IsBusy = false;
            Mode = BulkUpdateMode.Summary;
        }
    }
}

public class EnvRow : ViewModelBase
{
    private bool _selected;
    public string EnvId { get; }
    public string DisplayName { get; }
    public ObservableCollection<NodeSelectRow> Nodes { get; } = new();
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public EnvRow(string envId, string displayName)
    {
        EnvId = envId;
        DisplayName = displayName;
    }
}

public class NodeSelectRow : ViewModelBase
{
    private bool _selected;
    public string NodeId { get; }
    public string DisplayName { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public NodeSelectRow(string nodeId, string displayName)
    {
        NodeId = nodeId;
        DisplayName = displayName;
    }
}
