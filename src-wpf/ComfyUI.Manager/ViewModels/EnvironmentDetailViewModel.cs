using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentDetailViewModel : ViewModelBase
{
    private readonly NodeRepository _repo;
    private readonly Func<string, string, CancellationToken, Task<NodeOperationResult>> _deleteFunc;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand RescanCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand DeleteCommand { get; }

    /// <summary>test seam:替代真弹 ConfirmDialog。返 true = 确认删。</summary>
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public EnvironmentDetailViewModel(
        NodeRepository repo,
        ErrorBannerViewModel errorBanner,
        Func<string, string, CancellationToken, Task<NodeOperationResult>> deleteFunc,
        string envId)
    {
        _repo = repo;
        _errorBanner = errorBanner;
        _deleteFunc = deleteFunc ?? throw new ArgumentNullException(nameof(deleteFunc));
        _envId = envId;
        RescanCommand = new RelayCommand(_ => Rescan());
        ToggleCommand = new RelayCommand(
            p => Toggle(p as ScannedNode ?? Selected),
            p => (p as ScannedNode ?? Selected) is not null);
        DeleteCommand = new RelayCommand(
            async p => await DeleteAsync(p as ScannedNode ?? Selected),
            p => (p as ScannedNode ?? Selected) is not null);
        Load();
    }

    private ScannedNode? _selected;
    public ScannedNode? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    private void Load()
    {
        Nodes.Clear();
        foreach (var n in _repo.ListByEnv(_envId)) Nodes.Add(n);
    }

    private void Rescan()
    {
        // TODO(M5.2-T7): trigger local node rescan via NodeOperations.
        System.Windows.MessageBox.Show(
            "TODO(M5.2-T7): rescan nodes", "重新扫描");
    }

    private void Toggle(ScannedNode? node)
    {
        if (node is null) return;
        // TODO(M5.2-T7): enable/disable node in env via NodeOperations.
        System.Windows.MessageBox.Show(
            $"TODO(M5.2-T7): toggle node '{node.Package}'", "启用/禁用");
    }

    /// <summary>
    /// v0.6.15.7:从 env 删除节点。Public — 测试直接 await(同 LocalNodeListViewModel.DeleteAsync 模式)。
    /// DeleteCommand 把 Execute(parameter) → DeleteAsync(parameter) 串起来。
    /// </summary>
    public async Task DeleteAsync(ScannedNode? node)
    {
        if (node is null) return;
        var ok = ConfirmDialogOverride is not null
            ? ConfirmDialogOverride(
                $"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。",
                "确认删除", "取消")
            : Views.ConfirmDialog.Show(
                $"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。");
        if (!ok) return;

        Busy = true;
        try
        {
            var r = await _deleteFunc(_envId, node.Package, CancellationToken.None);
            if (!r.Success)
            {
                _errorBanner.Add("env-detail-delete", $"删除失败:{r.Reason}", ErrorSeverity.Error);
                return;
            }
            Nodes.Remove(node);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// v0.6.15.7:把 ISO-8601 UTC 时间戳(ScannedNode.LastScannedAt)格式化成"刚刚 / N 分钟前 / N 小时前 / N 天前"。
    /// null 或解析失败 → "未知"。Used by EnvironmentDetailView's LastScannedAt column.
    /// </summary>
    public static string FormatRelative(string? isoTimestamp)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp)) return "未知";
        if (!System.DateTime.TryParse(
                isoTimestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return "未知";
        }
        var delta = DateTime.UtcNow - dt;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} 天前";
        return dt.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
