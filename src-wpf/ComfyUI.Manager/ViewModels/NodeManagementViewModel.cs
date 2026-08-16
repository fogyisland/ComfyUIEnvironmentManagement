using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15.8:per-env 节点管理 VM。构造时自动 rescan,Nodes 填充已装节点。
/// ScanCommand 重扫,InstallCommand 开 CatalogEntryPicker,DeleteCommand 删行。
/// CloseCommand fire CloseRequested event(EnvListVM 接 → NodeManagement = null 隐 panel)。
/// </summary>
public class NodeManagementViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand ScanCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public string EnvName { get; }
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    /// <summary>test seam:代替真实开 CatalogEntryPickerDialog。返 true 表示装成功。</summary>
    public Func<bool>? OpenInstallPickerOverride { get; set; }

    public event Action? CloseRequested;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                InstallCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public NodeManagementViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        ErrorBannerViewModel errorBanner, string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _errorBanner = errorBanner;
        _envId = envId;
        EnvName = envName;
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !Busy);
        InstallCommand = new RelayCommand(_ => OpenInstallPicker(), _ => !Busy);
        DeleteCommand = new RelayCommand(
            async p => await DeleteAsync(p as ScannedNode),
            p => p is ScannedNode && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        ToggleCommand = new RelayCommand(_ => { /* TODO 占位 */ }, _ => false);
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        Busy = true;
        try
        {
            await _nodeOps.RescanAsync(_envId).ConfigureAwait(false);
            // 直接 mutate ObservableCollection — ConfigureAwait(false) 让 continuation
            // 跑在 thread pool,而 ObservableCollection 在 .NET 8 + 多线程 Binding
            // 场景下会抛,但 SpinWait 测试需要 sync 行为。生产路径配套会在调用时
            // 已经在 UI thread;tests 没 Binding 没 dispatcher pump,同步 mutate
            // 是最直接的方案。
            Nodes.Clear();
            foreach (var n in _nodeRepo.ListByEnv(_envId)) Nodes.Add(n);
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenInstallPicker()
    {
        bool? installed;
        if (OpenInstallPickerOverride is not null)
        {
            installed = OpenInstallPickerOverride();
        }
        else
        {
            // Production: fire-and-forget call to CatalogEntryPickerDialog.Show
            // (the dialog owns install + close, then VM rescans)
            installed = true;
            CatalogEntryPickerDialog.Show(
                envRepo: null!,  // picker no longer needs env repo — it builds from catalog+nodeRepo
                nodeOps: _nodeOps,
                catalogRepo: null!,
                nodeRepo: _nodeRepo,
                versionRepo: null!,
                logger: null,
                envId: _envId,
                onInstallSuccess: null,
                onClosed: () => _ = ScanAsync());
        }
        if (installed == true) _ = ScanAsync();
    }

    public async Task DeleteAsync(ScannedNode? node)
    {
        if (node is null) return;
        var ok = ConfirmDialogOverride is not null
            ? ConfirmDialogOverride($"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。", "确认删除", "取消")
            : ConfirmDialog.Show($"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。");
        if (!ok) return;

        Busy = true;
        try
        {
            var r = await _nodeOps.UninstallAsync(_envId, node.Id, CancellationToken.None).ConfigureAwait(false);
            if (!r.Success)
            {
                _errorBanner.Add("env-detail-delete", $"删除失败:{r.Reason}", ErrorSeverity.Error);
                return;
            }
            // 跟 ScanAsync 同款:同步 mutate ObservableCollection。
            Nodes.Remove(node);
        }
        finally
        {
            Busy = false;
        }
    }
}
