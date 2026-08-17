using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
/// v0.6.15.9:把原 UpgradeNodesViewModel 的 outdated 计算 + UpgradeCommand 迁到这里。
/// 每个 ScannedNode.ScanMeta["installed_tag"] 跟 catalog.LatestVersion 比对,不一致 → IsOutdated=true,
/// 行内显示"升级"按钮(DataTrigger via BoolToVisibility)。删独立的 UpgradeNodesViewModel /
/// UpgradeNodesView / UpgradeNodes bottom-popup 面板 / env-list 行内"升级节点"按钮。
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
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly string _envId;
    private readonly SynchronizationContext? _uiContext;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();

    private ScannedNode? _selectedNode;
    public ScannedNode? SelectedNode
    {
        get => _selectedNode;
        set => SetField(ref _selectedNode, value);
    }

    public RelayCommand ScanCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ToggleCommand { get; }
    /// <summary>v0.6.15.9:行内升级按钮(过时才 enabled,DataTrigger via IsOutdated)。</summary>
    public RelayCommand UpgradeCommand { get; }

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
                // v0.6.15.9.2 hotfix:Busy setter 在 ScanAsync / UpgradeAsync / DeleteAsync
                // 末尾被调,这些方法都 ConfigureAwait(false) → setter 跑在线程池。
                // RaiseCanExecuteChanged → WPF CommandBinding → UpdateCanExecute → 读
                // Button.Command → 抛 InvalidOperationException("调用线程无法访问此对象")。
                // 跟 collection mutation 同款修法:captured DispatcherSynchronizationContext
                // .Post 回去;测试无 SyncContext → Post 跳过 → 同步直调(行为不变)。
                RaiseCanExecuteChangedOnUi();
            }
        }
    }

    /// <summary>Busy 改了之后通知 4 个命令 UI 重算 CanExecute。
    /// 生产路径(DispatcherSynchronizationContext 在 ctor 捕获)→ Post 回 UI 线程;
    /// 测试路径(无 SyncContext)→ 同步直调,跟原来行为一致。</summary>
    private void RaiseCanExecuteChangedOnUi()
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ =>
            {
                ScanCommand.RaiseCanExecuteChanged();
                InstallCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                UpgradeCommand.RaiseCanExecuteChanged();
            }, null);
        }
        else
        {
            ScanCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            UpgradeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>v0.6.15.9:catalog Package → LatestVersion。ScanAsync 末尾 refresh,
    /// UpgradeAsync 后 rebuild。空 catalog / 节点 package 不在 catalog → 节点 IsOutdated=false。</summary>
    private Dictionary<string, string> _latestByPackage = new();

    public NodeManagementViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        ErrorBannerViewModel errorBanner,
        EnvironmentRepository envRepo,
        CatalogRepository catalogRepo,
        NodeVersionRepository versionRepo,
        RequirementsInstaller requirementsInstaller,
        string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _errorBanner = errorBanner;
        _envRepo = envRepo;
        _catalogRepo = catalogRepo;
        _versionRepo = versionRepo;
        _requirementsInstaller = requirementsInstaller;
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
        // v0.6.15.9 P0:scan/install 永远可点 — 用户首扫完想再点 scan 把漏的自定义节点
        // 放出来(原 !Busy gate 锁住,实测按钮一直灰,ScanCommand.CanExecute 一返回
        // false WPF 就 disable)。并发 scan 由 ConfigureAwait(false) + 各扫独立
        // snapshot 替换保证 last-writer-wins 不冲突 ObservableCollection。
        ScanCommand = new RelayCommand(async _ => await ScanAsync());
        InstallCommand = new RelayCommand(_ => OpenInstallPicker());
        DeleteCommand = new RelayCommand(
            async p => await DeleteAsync(p as ScannedNode),
            p => p is ScannedNode && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        ToggleCommand = new RelayCommand(_ => { /* TODO 占位 */ }, _ => false);
        UpgradeCommand = new RelayCommand(
            async p => await UpgradeAsync(p as ScannedNode),
            p => p is ScannedNode n && IsOutdated(n) && !Busy);
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        // v0.6.15.9 P0:scan/install 永远可点 — ScanCommand 无 !Busy gate(按钮一直
        // enabled)。用户连续点击 → 多次 RescanAsync 并发跑;Nodes collection 的
        // 重置由各次 rescan 各自的 snapshot 替换(last writer wins)。配置 false
        // continuation 不依赖 UI thread,所以并发 scan 之间不会互相 inflate
        // ObservableCollection 抛 cross-thread。
        //
        // 不在 ScanAsync 内部 `if (Busy) return` — upgrade-triggered rescan
        // (UpgradeAsync 内部 await ScanAsync) 走同一方法,Busy=true 时会被短路,
        // 升完不刷新。
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
            // v0.6.15.9:refresh catalog latest version cache 后给每行填 IsOutdated,
            // 让行内"升级"按钮 visibility 跟上数据状态。空 catalog / node 不在 catalog →
            // IsOutdated=false(不主动报"过时",避免误判)。
            RefreshLatestByPackage();
            foreach (var n in snapshot)
            {
                n.IsOutdated = IsOutdated(n);
            }
            if (_uiContext is not null)
            {
                _uiContext.Post(_ =>
                {
                    Nodes.Clear();
                    foreach (var n in snapshot) Nodes.Add(n);
                    UpgradeCommand.RaiseCanExecuteChanged();
                }, null);
            }
            else
            {
                Nodes.Clear();
                foreach (var n in snapshot) Nodes.Add(n);
                UpgradeCommand.RaiseCanExecuteChanged();
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>v0.6.15.9:扫一遍 catalog,GroupBy(Package) 拿 LatestVersion,
    /// 空 LatestVersion 跳过。失败(catalog 抛异常)→ 留空 dict,所有 IsOutdated=false。</summary>
    private void RefreshLatestByPackage()
    {
        try
        {
            _latestByPackage = _catalogRepo.Search("", 5000)
                .Where(e => !string.IsNullOrEmpty(e.LatestVersion))
                .GroupBy(e => e.Package, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().LatestVersion!, StringComparer.Ordinal);
        }
        catch
        {
            _latestByPackage = new();
        }
    }

    /// <summary>v0.6.15.9:行内升级的过时判定。三个前提缺一不可:
    /// (a) <c>ScanMeta["installed_tag"]</c> 非空(老节点没装 tag 时不报过时),
    /// (b) catalog 有这个 package 且 LatestVersion 非空,
    /// (c) tag != latest。任一不满足 → false。</summary>
    private bool IsOutdated(ScannedNode node)
    {
        if (node.ScanMeta is null) return false;
        if (!node.ScanMeta.TryGetValue("installed_tag", out var tag)
            || string.IsNullOrEmpty(tag)) return false;
        if (!_latestByPackage.TryGetValue(node.Package, out var latest)
            || string.IsNullOrEmpty(latest)) return false;
        return !string.Equals(tag, latest, StringComparison.Ordinal);
    }

    /// <summary>v0.6.15.9:行内 UpgradeCommand handler。调
    /// <c>NodeOperations.UpgradeAsync(envId, nodeId, null, ct)</c>,成功后 ScanAsync
    /// rebuild(节点的 installed_tag 现在跟 latest 一致 → IsOutdated=false → 行内按钮消失)。
    /// 失败 → ErrorBanner(同 DeleteAsync 模式)。</summary>
    public async Task UpgradeAsync(ScannedNode? node)
    {
        if (node is null) return;
        Busy = true;
        try
        {
            var r = await _nodeOps.UpgradeAsync(_envId, node.Id, progress: null, CancellationToken.None)
                .ConfigureAwait(false);
            if (!r.Success)
            {
                _errorBanner.Add("node-mgmt-upgrade", $"升级 {node.Package} 失败:{r.Reason}", ErrorSeverity.Error);
                return;
            }
            await ScanAsync().ConfigureAwait(false);
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
                requirementsInstaller: _requirementsInstaller,
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
