using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15.8 T3:per-env 升级节点 VM。构造时自动 rescan + 拉 catalog,过滤
/// outdated(节点 <c>ScanMeta["installed_tag"]</c> 非空 + 与 catalog LatestVersion
/// 不一致)。Per-row UpgradeCommand 调 <c>NodeOperations.UpgradeAsync</c>,完成后
/// <c>LoadAsync</c> 重过滤(节点可能已对齐,自动从列表移除)。
///
/// Ruling 1(已采用):catalog 走 <c>Func&lt;string,int,IEnumerable&lt;CatalogEntry&gt;&gt;</c>
/// delegate,不需要新 interface 或 Fake adapter。
///
/// Ruling 3(T3 spec 引入,跟 brief 偏离):VM 不暴露 <c>LatestVersion(node)</c> 方法 —
/// WPF XAML 不能绑方法,需要 <see cref="UpgradeCandidate.LatestVersion"/> 属性
/// 直接绑。OutdatedNodes 元素类型为 <see cref="UpgradeCandidate"/>,不是裸
/// <c>ScannedNode</c>。
/// </summary>
public class UpgradeNodesViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly Func<string, int, IEnumerable<CatalogEntry>> _catalogSearch;
    private readonly string _envId;
    private readonly SynchronizationContext? _uiContext;
    private Dictionary<string, string> _latestByPackage = new();

    public ObservableCollection<UpgradeCandidate> OutdatedNodes { get; } = new();
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand CloseCommand { get; }
    public string EnvName { get; }
    public event Action? CloseRequested;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                UpgradeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public UpgradeNodesViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        Func<string, int, IEnumerable<CatalogEntry>> catalogSearch,
        string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _catalogSearch = catalogSearch;
        _envId = envId;
        EnvName = envName;
        // Filter to DispatcherSynchronizationContext only (WPF UI thread). Tests
        // run without a SyncContext so Post is skipped, mutations stay sync.
        // Other test SyncContexts (TestSynchronizationContext) would leak
        // between parallel xUnit tests.
        var ctx = SynchronizationContext.Current;
        _uiContext = ctx is System.Windows.Threading.DispatcherSynchronizationContext ? ctx : null;
        // v0.6.15.8 T4 fix: XAML CommandParameter="{Binding}" in DataGridTemplateColumn
        // resolves to the row item (UpgradeCandidate), not its .Node. So param
        // must be UpgradeCandidate not ScannedNode, or CanExecute is always false
        // and the Upgrade button is dead in production.
        UpgradeCommand = new RelayCommand(
            async p => await UpgradeAsync(p as UpgradeCandidate),
            p => p is UpgradeCandidate && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            await _nodeOps.RescanAsync(_envId).ConfigureAwait(false);
            var scanned = _nodeRepo.ListByEnv(_envId);
            var catalogEntries = _catalogSearch("", 5000).ToList();

            _latestByPackage = catalogEntries
                .Where(e => !string.IsNullOrEmpty(e.LatestVersion))
                .GroupBy(e => e.Package)
                .ToDictionary(g => g.Key, g => g.First().LatestVersion!);

            var outdated = scanned.Where(s =>
                s.ScanMeta.TryGetValue("installed_tag", out var tag)
                && !string.IsNullOrEmpty(tag)
                && _latestByPackage.TryGetValue(s.Package, out var latest)
                && !string.IsNullOrEmpty(latest)
                && tag != latest)
                .Select(s => new UpgradeCandidate { Node = s, LatestVersion = _latestByPackage[s.Package] })
                .ToList();

            if (_uiContext is not null)
            {
                _uiContext.Post(_ =>
                {
                    OutdatedNodes.Clear();
                    foreach (var c in outdated) OutdatedNodes.Add(c);
                }, null);
            }
            else
            {
                OutdatedNodes.Clear();
                foreach (var c in outdated) OutdatedNodes.Add(c);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UpgradeAsync(UpgradeCandidate? candidate)
    {
        if (candidate is null) return;
        Busy = true;
        try
        {
            // Real NodeOperations.UpgradeAsync takes (envId, nodeId, progress, ct).
            // candidate.Node.Id is the ScannedNode row id (DB primary key), NOT Package.
            await _nodeOps.UpgradeAsync(_envId, candidate.Node.Id, progress: null, CancellationToken.None).ConfigureAwait(false);
            // Reload filter: node's installed_tag may now match latest, so it
            // drops out of OutdatedNodes.
            await LoadAsync().ConfigureAwait(false);
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>
/// v0.6.15.8 T3 (Ruling 3):wrapper for XAML binding. WPF can't bind to VM
/// methods, so <see cref="LatestVersion"/> must be a property on a bindable
/// item. T4's XAML binds <c>Candidate.LatestVersion</c> directly.
/// </summary>
public class UpgradeCandidate
{
    public ScannedNode Node { get; init; } = null!;
    public string LatestVersion { get; init; } = "";
}
