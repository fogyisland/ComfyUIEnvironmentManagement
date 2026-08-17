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

/// <summary>
/// v0.6.18:批量更新 inline VM(替代原 <c>BulkUpdateDialogViewModel</c>)。
/// 跟 dialog VM 比,只少了 dialog 状态机相关的 <c>Mode</c>/<c>Summary</c>/<c>BulkId</c> 字段 —
/// inline UI 永远可见,summary 直接渲染在底部 Border,不需要 dialog 的 SelectEnv / Running / Summary
/// 模式切换。Run / Cancel / Env 选择 / target 选择 / toggle-all 行为完全保留 — <c>BulkUpdateOrchestrator</c>
/// 一行未动。
///
/// 命名沿用 <c>BulkUpdateDialogViewModel</c> 大部分 API,只是:
/// - 类名 <c>BulkUpdateViewModel</c> (反映 inline 用途)
/// - 类放新文件 <c>BulkUpdateViewModel.cs</c> (原 <c>BulkUpdateDialogViewModel.cs</c> 已删)
/// - 移除 <c>BulkUpdateMode</c> enum(原 dialog 才用)
/// </summary>
public class BulkUpdateViewModel : ViewModelBase
{
    private readonly BulkUpdateOrchestrator _orchestrator;
    private CancellationTokenSource _runCts = new();

    private string? _errorMessage;
    private bool _isBusy;

    /// <summary>v0.6.11 T8:env 选择列表(checkbox 驱动)。</summary>
    public ObservableCollection<EnvRow> EnvRows { get; } = new();

    /// <summary>每个 (env, target) 一行进度。Orchestrator Progress 事件实时更新。</summary>
    public ObservableCollection<BulkUpdateRow> Rows { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }

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
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>v0.6.11 T8:是否更新 ComfyUI 源。默认勾上。</summary>
    private bool _updateComfyUi = true;
    public bool UpdateComfyUi
    {
        get => _updateComfyUi;
        set
        {
            if (_updateComfyUi == value) return;
            _updateComfyUi = value;
            RaisePropertyChanged();
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>v0.6.11 T8:是否更新 ComfyUI-Manager。默认勾上。</summary>
    private bool _updateComfyUiManager = true;
    public bool UpdateComfyUiManager
    {
        get => _updateComfyUiManager;
        set
        {
            if (_updateComfyUiManager == value) return;
            _updateComfyUiManager = value;
            RaisePropertyChanged();
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    public BulkUpdateViewModel(BulkUpdateOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart());
        CancelCommand = new RelayCommand(
            _ => Cancel(),
            _ => IsBusy);
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
        && (UpdateComfyUi || UpdateComfyUiManager);

    public List<string> SelectedEnvIds() =>
        EnvRows.Where(e => e.Selected).Select(e => e.EnvId).ToList();

    public List<BulkUpdateTargetKind> SelectedTargetKinds()
    {
        var kinds = new List<BulkUpdateTargetKind>(2);
        if (UpdateComfyUi) kinds.Add(BulkUpdateTargetKind.ComfyUi);
        if (UpdateComfyUiManager) kinds.Add(BulkUpdateTargetKind.ComfyUiManager);
        return kinds;
    }

    /// <summary>summary 计数 — 绑底部 inline Border,实时跟 orchestrator Completed 更新。</summary>
    public BulkUpdateSummary? Summary { get; private set; }

    private void Start()
    {
        var envIds = SelectedEnvIds();
        var targetKinds = SelectedTargetKinds();
        if (envIds.Count == 0 || targetKinds.Count == 0) return;

        // 预填 Rows —— 一个 (env, target) 一条 "pending"。Orchestrator 的 Progress
        // 事件从背景任务发,我们用索引直接更新对应 row 而无需每次都遍历查找。
        Rows.Clear();
        for (int i = 0; i < envIds.Count; i++)
        {
            for (int j = 0; j < targetKinds.Count; j++)
            {
                Rows.Add(new BulkUpdateRow(envIds[i], targetKinds[j], "pending", null, 0));
            }
        }

        // 旧 CTS 释放 —— 上一轮如果意外没释放,以这里为权威源。
        try { _runCts.Dispose(); } catch { }
        _runCts = new CancellationTokenSource();

        IsBusy = true;
        ErrorMessage = null;
        Summary = null;
        RaisePropertyChanged(nameof(Summary));

        _ = _orchestrator.StartAsync(envIds, targetKinds, _runCts.Token)
            .ContinueWith(t => DispatcherHelper.RunOnUiAsync(() => OnRunFinished(t)));
    }

    private void Cancel()
    {
        if (!IsBusy) return;
        CancelRun();
    }

    /// <summary>
    /// 由 View (dialog Closing / tab 切换 / app 退出) 调用,确保 user 主动离开时 run 也会被取消,
    /// 而不是默默在后台跑完。MainViewModel 切走 section 时若 <c>IsBusy</c>,通过这里取消。
    /// </summary>
    public void CancelRun()
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
            // 找到现有的 pending / running 行,直接替换 —— 其它字段(env/target)不变,
            // 只更新 Status/Reason/LatencyMs/Percent。
            for (int i = 0; i < Rows.Count; i++)
            {
                var existing = Rows[i];
                if (existing.EnvId == row.EnvId && existing.TargetKind == row.TargetKind
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
            Summary = summary;
            RaisePropertyChanged(nameof(Summary));
            IsBusy = false;
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
        // Orchestrator 的 Completed 事件已经把 Summary 设好。
        // 这里只处理异常 + 最终收尾(IsBusy 在 OnCompleted 里已设 false,这里冗余也无害)。
        if (task.IsFaulted)
        {
            var msg = task.Exception?.GetBaseException().Message
                ?? "未知错误";
            ErrorMessage = $"运行失败:{msg}";
            IsBusy = false;
        }
    }
}

public class EnvRow : ViewModelBase
{
    private bool _selected;
    public string EnvId { get; }
    public string DisplayName { get; }
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
