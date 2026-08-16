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
///
/// R1 fix: ctor 加 EnvironmentRepository / CatalogRepository / NodeVersionRepository
/// 三个参数(从 5 参 → 8 参),让生产路径能传真值给 CatalogEntryPickerDialog.Show,
/// 不再传 null! 占位。Ruling 3 计划的"T5 plumb"前移到 T2。
/// </summary>
public class NodeManagementViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly EnvironmentRepository _envRepo;
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _envId;
    private readonly SynchronizationContext? _uiContext;

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
        ErrorBannerViewModel errorBanner,
        EnvironmentRepository envRepo,
        CatalogRepository catalogRepo,
        NodeVersionRepository versionRepo,
        string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _errorBanner = errorBanner;
        _envRepo = envRepo;
        _catalogRepo = catalogRepo;
        _versionRepo = versionRepo;
        _envId = envId;
        EnvName = envName;
        // Capture UI SynchronizationContext so collection mutations in ScanAsync /
        // DeleteAsync marshal back to the dispatcher in production. Filter to
        // DispatcherSynchronizationContext only (WPF UI thread): other test
        // SyncContexts (e.g. TestSynchronizationContext in EnvStartStatus / Catalog
        // Refresh tests) leak between xUnit parallel tests, and we don't want a
        // custom test Post to defer mutations asynchronously.
        var ctx = SynchronizationContext.Current;
        _uiContext = ctx is System.Windows.Threading.DispatcherSynchronizationContext ? ctx : null;
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
            // R1 fix Important 3: Continuation runs on thread pool after
            // ConfigureAwait(false). ObservableCollection mutation must marshal
            // back to UI thread in production (WPF binding throws on cross-thread
            // mutation). Use captured SyncContext.Post so tests stay sync
            // (xUnit has no SyncContext → Post is skipped, mutations are direct).
            var snapshot = _nodeRepo.ListByEnv(_envId);
            if (_uiContext is not null)
            {
                _uiContext.Post(_ =>
                {
                    Nodes.Clear();
                    foreach (var n in snapshot) Nodes.Add(n);
                }, null);
            }
            else
            {
                Nodes.Clear();
                foreach (var n in snapshot) Nodes.Add(n);
            }
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
            // Override path: no onClosed callback, so rely on installed==true to
            // trigger the rescan.
            if (installed == true) _ = ScanAsync();
        }
        else
        {
            // Production: dialog owns install + close; onClosed is the SOLE rescan
            // trigger (R1 fix Critical 2 — was previously double-fired by onClosed
            // AND the installed==true check below).
            CatalogEntryPickerDialog.Show(
                envRepo: _envRepo,
                nodeOps: _nodeOps,
                catalogRepo: _catalogRepo,
                nodeRepo: _nodeRepo,
                versionRepo: _versionRepo,
                logger: null,
                envId: _envId,
                onInstallSuccess: null,
                onClosed: () => _ = ScanAsync());
        }
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
            // R1 fix Important 3: same sync-context pattern as ScanAsync.
            if (_uiContext is not null)
            {
                _uiContext.Post(_ => Nodes.Remove(node), null);
            }
            else
            {
                Nodes.Remove(node);
            }
        }
        finally
        {
            Busy = false;
        }
    }
}
