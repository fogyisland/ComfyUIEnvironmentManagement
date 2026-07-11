using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public enum BulkUpdateMode { SelectEnv, Running, Summary }

public class BulkUpdateDialogViewModel : ViewModelBase
{
    private readonly BulkUpdateApiClient _api;
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
        set { _errorMessage = value; RaisePropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); }
    }

    public BulkUpdateDialogViewModel(BulkUpdateApiClient api)
    {
        _api = api;
        StartCommand = new RelayCommand(
            async _ => await StartAsync(),
            _ => CanStart());
        CancelCommand = new RelayCommand(
            async _ => await CancelAsync(),
            _ => BulkId != null && Mode == BulkUpdateMode.Running);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll());
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

    public async Task StartAsync()
    {
        var envIds = SelectedEnvIds();
        var nodeIds = SelectedNodeIds();
        if (!envIds.Any() || !nodeIds.Any())
        {
            ErrorMessage = "请至少选择 1 个 env 和 1 个节点";
            return;
        }
        IsBusy = true;
        ErrorMessage = null;
        var resp = await _api.StartAsync(envIds, nodeIds);
        IsBusy = false;
        if (!resp.Ok)
        {
            ErrorMessage = resp.Error?.Message ?? "unknown";
            return;
        }
        BulkId = resp.Value!.BulkId;
        Rows.Clear();
        foreach (var e in envIds)
        {
            foreach (var n in nodeIds)
            {
                Rows.Add(new BulkUpdateRow(e, n, "pending", null, 0));
            }
        }
        Mode = BulkUpdateMode.Running;
    }

    public async Task CancelAsync()
    {
        if (BulkId == null) return;
        await _api.CancelAsync(BulkId);
    }

    /// <summary>由 WS handler 调用,更新单行状态。</summary>
    public void UpdateRow(string envId, string nodeId, string status,
        string? reason = null, int latencyMs = 0)
    {
        var row = Rows.FirstOrDefault(
            r => r.EnvId == envId && r.NodeId == nodeId);
        if (row == null) return;
        var idx = Rows.IndexOf(row);
        Rows[idx] = new BulkUpdateRow(envId, nodeId, status, reason, latencyMs);
    }

    /// <summary>由 WS handler 调用,bulk 完成后切到 summary。</summary>
    public void SetSummary(BulkUpdateSummary summary)
    {
        Summary = summary;
        Mode = BulkUpdateMode.Summary;
    }

    public async Task RefreshAsync(string bulkId)
    {
        var resp = await _api.GetStatusAsync(bulkId);
        if (!resp.Ok)
        {
            ErrorMessage = resp.Error?.Message ?? "unknown";
            Mode = BulkUpdateMode.SelectEnv;
            return;
        }
        var s = resp.Value!;
        if (s.IsRunning)
        {
            BulkId = bulkId;
            Mode = BulkUpdateMode.Running;
            // 重建 Rows from 推断(无 rows 字段;按 selected 重置 pending)
        }
        else
        {
            // completed / cancelled → 显示 summary 占位,真实 summary 由后续 WS push
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