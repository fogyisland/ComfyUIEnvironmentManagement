using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private readonly CatalogRepository _repo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogRefreshService _refreshService;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;
    private readonly string _projectRoot;
    private readonly NodeRepository? _nodeRepo;

    private List<CatalogEntry> _allEntries = new();

    public ObservableCollection<CatalogEntry> PagedEntries { get; } = new();
    public ObservableCollection<VersionInfo> SelectedVersions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public RelayCommand SetTileViewCommand { get; }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetField(ref _currentPage, value))
            {
                RaisePropertyChanged(nameof(CanPrevPage));
                RaisePropertyChanged(nameof(CanNextPage));
            }
        }
    }

    private int _totalPages = 1;
    public int TotalPages
    {
        get => _totalPages;
        private set => SetField(ref _totalPages, value);
    }

    public int PageSize => _settings.CatalogPageSize;

    public CatalogViewMode ViewMode => _settings.CatalogViewMode;
    public bool IsListMode => ViewMode == CatalogViewMode.List;
    public bool IsTileMode => ViewMode == CatalogViewMode.Tile;

    public bool HasEntries => _allEntries.Count > 0;
    public bool CanPrevPage => CurrentPage > 1;
    public bool CanNextPage => CurrentPage < TotalPages;

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) Search(); }
    }

    private CatalogEntry? _selected;
    public CatalogEntry? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RaisePropertyChanged(nameof(HasSelected));
                RaisePropertyChanged(nameof(SelectedReference));
                RaisePropertyChanged(nameof(SelectedReferenceUrl));
                RaisePropertyChanged(nameof(SelectedLatestVersion));
                RaisePropertyChanged(nameof(SelectedRepositoryUrl));
                RaisePropertyChanged(nameof(SelectedInstallType));
                RaisePropertyChanged(nameof(SelectedDescription));
                RaisePropertyChanged(nameof(SelectedAuthor));
                RaisePropertyChanged(nameof(SelectedTitle));
                RaisePropertyChanged(nameof(SelectedLastUpdate));
                RaisePropertyChanged(nameof(SelectedPipRequirements));
                RaisePropertyChanged(nameof(HasPipRequirements));
                LoadVersionsForSelected();
            }
        }
    }

    private void LoadVersionsForSelected()
    {
        SelectedVersions.Clear();
        SelectedVersion = null;
        RaisePropertyChanged(nameof(HasVersions));
        RaisePropertyChanged(nameof(SelectedVersionDate));
        RaisePropertyChanged(nameof(DownloadButtonLabel));
        if (_selected is null) return;
        var versions = _versionRepo.ListByNode(_selected.Id);
        foreach (var v in versions) SelectedVersions.Add(v);
        // 默认选中最新(第一个,已按 published_at DESC)
        if (SelectedVersions.Count > 0)
        {
            SelectedVersion = SelectedVersions[0];
        }
    }

    /// <summary>
    /// v0.6.9 T7:Spotlight 选中 Node target 后,MainViewModel 调这里把
    /// CatalogView.Selected 切到对应 entry。优先在当前页的 PagedEntries 里找;
    /// 找不到(节点可能在其他页)则 fallback 到 _allEntries,设 Selected 让 UI 通过
    /// DataGrid/ListBox SelectedItem binding 自动 scroll 进来。
    /// </summary>
    public void SelectNode(string nodeId)
    {
        var entry = PagedEntries.FirstOrDefault(e => e.Id == nodeId)
                    ?? _allEntries.FirstOrDefault(e => e.Id == nodeId);
        if (entry is null) return;
        Selected = entry;
    }

    public bool HasSelected => _selected is not null;
    public bool HasVersions => SelectedVersions.Count > 0;
    public string? SelectedTitle => _selected?.RawMetadata?.TryGetValue("title", out var t) == true ? t?.ToString() : _selected?.Package;
    public string? SelectedAuthor => _selected?.Author;
    public string? SelectedDescription => _selected?.Description;
    public string? SelectedReference => _selected?.Reference;
    public string SelectedReferenceUrl => SelectedReference ?? "";
    public string? SelectedInstallType => _selected?.InstallType;
    public string? SelectedLastUpdate => _selected?.LastUpdate;
    public IReadOnlyList<PipRequirement> SelectedPipRequirements
        => _selected?.PipRequirements ?? Array.Empty<PipRequirement>();
    public bool HasPipRequirements => SelectedPipRequirements.Count > 0;
    public string? SelectedLatestVersion => string.IsNullOrEmpty(_selected?.LatestVersion) ? "未知" : _selected!.LatestVersion;

    /// <summary>
    /// v0.6.15.2 hotfix:从 raw_metadata 抽出实际仓库 URL(优先 id / repository / url / files[0] / reference),
    /// 详情面板显示 + 兜底给 <see cref="DownloadAsync"/>。空串表示"无仓库 URL"(下载会用模板兜底)。
    /// </summary>
    public string SelectedRepositoryUrl => _selected is null ? "" : (ExtractRepoUrl(_selected) ?? "");

    private VersionInfo? _selectedVersion;
    public VersionInfo? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                RaisePropertyChanged(nameof(SelectedVersionDate));
                RaisePropertyChanged(nameof(DownloadButtonLabel));
            }
        }
    }
    public string SelectedVersionDate
    {
        get
        {
            if (_selectedVersion is null) return "—";
            var pub = _selectedVersion.PublishedAt;
            return pub.Length >= 10 ? pub[..10] : pub;
        }
    }

    public string DownloadButtonLabel =>
        _selectedVersion is null ? "下载" : $"下载 {_selectedVersion.Tag}";

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    private string? _infoMessage;
    public string? InfoMessage
    {
        get => _infoMessage;
        private set => SetField(ref _infoMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    private int _refreshPercent;
    public int RefreshPercent
    {
        get => _refreshPercent;
        private set => SetField(ref _refreshPercent, value);
    }

    private string? _progressMessage;
    public string? ProgressMessage
    {
        get => _progressMessage;
        private set => SetField(ref _progressMessage, value);
    }

    private string _readProgress = "";
    public string ReadProgress
    {
        get => _readProgress;
        private set => SetField(ref _readProgress, value);
    }

    private string _writeProgress = "";
    public string WriteProgress
    {
        get => _writeProgress;
        private set => SetField(ref _writeProgress, value);
    }

    private string _versionProgress = "";
    public string VersionProgress
    {
        get => _versionProgress;
        private set => SetField(ref _versionProgress, value);
    }

    private string _metadataProgress = "";
    public string MetadataProgress
    {
        get => _metadataProgress;
        private set => SetField(ref _metadataProgress, value);
    }

    public RateLimitBannerViewModel RateLimitBanner { get; } = new();

    public RelayCommand CancelRefreshCommand { get; }

    private CancellationTokenSource? _refreshCts;
    private readonly IRateLimitState? _rateLimitState;

    public CatalogViewModel(
        CatalogRepository repo,
        NodeVersionRepository versionRepo,
        NodeOperations nodeOps,
        CatalogRefreshService refreshService,
        Settings settings,
        SettingsRepository settingsRepo,
        string projectRoot,
        IRateLimitState? rateLimitState = null,
        NodeRepository? nodeRepo = null)
    {
        _repo = repo;
        _versionRepo = versionRepo;
        _nodeOps = nodeOps;
        _refreshService = refreshService;
        _settings = settings;
        _settingsRepo = settingsRepo;
        _projectRoot = projectRoot;
        _rateLimitState = rateLimitState;
        _nodeRepo = nodeRepo;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        CancelRefreshCommand = new RelayCommand(_ => _refreshCts?.Cancel(), _ => IsBusy);
        DownloadCommand = new RelayCommand(
            async p => await DownloadAsync(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        NextPageCommand = new RelayCommand(_ => GoToPage(CurrentPage + 1), _ => CanNextPage);
        PrevPageCommand = new RelayCommand(_ => GoToPage(CurrentPage - 1), _ => CanPrevPage);
        SetListViewCommand = new RelayCommand(_ => SetViewMode(CatalogViewMode.List));
        SetTileViewCommand = new RelayCommand(_ => SetViewMode(CatalogViewMode.Tile));

        Search();
    }

    private void Search()
    {
        _allEntries = _repo.Search(_query, limit: 0);
        PopulateIsInLocalNodeDb();
        CurrentPage = 1;
        ApplyPage();
    }

    /// <summary>
    /// v0.6.15:把 <c>IsInLocalNodeDb</c> 写回每条 <see cref="CatalogEntry"/>。
    /// XAML 绑 CatalogEntry.IsInLocalNodeDb 控制"下载"按钮 disabled + "已下载" badge。
    /// 无 <see cref="NodeRepository"/> 时跳过(向后兼容老 ctor 路径)。
    /// </summary>
    private void PopulateIsInLocalNodeDb()
    {
        if (_nodeRepo is null) return;
        var downloaded = _nodeRepo.ListDownloadedNodes()
            .Select(n => n.Package)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _allEntries)
        {
            entry.IsInLocalNodeDb = downloaded.Contains(entry.Package);
        }
    }

    /// <summary>
    /// v0.6.15:查 <paramref name="package"/> (nodeId) 是否已下载到本地节点目录
    /// (看 <c>scanned_nodes</c> 中 <c>EnvId="" + Source="download"</c> 的 sentinel 行)。
    /// 供 VM 端代码(测试、code-behind)直接调;XAML binding 走 <see cref="CatalogEntry.IsInLocalNodeDb"/>
    /// (由 <see cref="PopulateIsInLocalNodeDb"/> 写回)。
    /// </summary>
    public bool IsInLocalNodeDbFor(string package)
    {
        if (_nodeRepo is null) return false;
        return _nodeRepo.ListDownloadedNodes().Any(n => n.Package == package);
    }

    private void ApplyPage()
    {
        PagedEntries.Clear();
        var size = PageSize;
        var skip = (CurrentPage - 1) * size;
        foreach (var e in _allEntries.Skip(skip).Take(size)) PagedEntries.Add(e);
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_allEntries.Count / size));
        RaisePropertyChanged(nameof(HasEntries));
        RaisePropertyChanged(nameof(CanPrevPage));
        RaisePropertyChanged(nameof(CanNextPage));
    }

    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages) return;
        CurrentPage = page;
        ApplyPage();
    }

    private void SetViewMode(CatalogViewMode mode)
    {
        if (_settings.CatalogViewMode == mode) return;
        _settings.CatalogViewMode = mode;
        _settingsRepo.Save(_settings);
        RaisePropertyChanged(nameof(ViewMode));
        RaisePropertyChanged(nameof(IsListMode));
        RaisePropertyChanged(nameof(IsTileMode));
    }

    public async Task RefreshAsync()
    {
        ErrorMessage = null;
        InfoMessage = null;
        ProgressMessage = "拉取 catalog...";
        RefreshPercent = 0;
        // v0.6.15: 入口清 stale state —— 上次 refresh 撞的 limit banner
        // 用户没手动 dismiss 也得在本次 refresh 开始时清掉(避免 banner
        // 永远挂在那误导用户认为当前还在限流)
        RateLimitBanner.Hide();
        ReadProgress = "";
        WriteProgress = "";
        VersionProgress = "";
        MetadataProgress = "";
        IsBusy = true;
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _allEntries.Clear();
        ApplyPage();
        try
        {
            // Progress<T> 在构造时捕获 SynchronizationContext(UI 线程),回调自动 marshal 回来。
            var progress = new Progress<CatalogEntry>(e =>
            {
                OnEntryArrived(e);
                ReadProgress = $"拉取 catalog: {_allEntries.Count} entries";
            });
            var versionProgress = new Progress<VersionFetchProgress>(vp =>
            {
                if (vp.Total <= 0) return;
                RefreshPercent = (int)(100.0 * vp.Completed / vp.Total);
                ProgressMessage = $"正在拉取版本 {vp.Completed}/{vp.Total}";
                VersionProgress = $"拉取版本: {vp.Completed}/{vp.Total}";
            });
            var metadataProgress = new Progress<MetadataFetchProgress>(mp =>
                MetadataProgress = $"拉取 metadata: {mp.Done}/{mp.Total}");
            var rateLimitProgress = new Progress<RateLimitInfo>(info =>
                RateLimitBanner.Show(info, DateTimeOffset.Now));
            var result = await _refreshService.RefreshAsync(
                progress, versionProgress, rateLimitProgress,
                metadataProgress, _rateLimitState, ct);
            if (result.Success)
            {
                // v0.6.15: WriteProgress 直接 populate result 4 计数,
                // InfoMessage 沿用既有 4 计数格式(用户已习惯)
                WriteProgress =
                    $"写库: +{result.AddedCount} ~{result.UpdatedCount} " +
                    $"⟳{result.SkippedCount} -{result.DeletedCount}";
                var msg = $"刷新成功 +{result.AddedCount} ~{result.UpdatedCount} ⟳{result.SkippedCount} -{result.DeletedCount}";
                if (result.VersionCount > 0)
                    msg += $",其中 {result.VersionCount} 个已获取版本号";
                if (result.MetadataCount > 0)
                    msg += $",{result.MetadataCount} 个已拉取 metadata";
                InfoMessage = msg;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            // 流式 progress 只是 refresh 期间的实时反馈,不是权威数据。
            // 命中 304 / 取消 / 出错时一条都不会 report,列表会留空。
            // 结束时统一从 DB 重读,顺带让当前 Query 过滤生效。
            Search();
            IsBusy = false;
            RefreshPercent = 0;
            ProgressMessage = null;
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }

    private void OnEntryArrived(CatalogEntry e)
    {
        _allEntries.Add(e);
        // 频繁 ApplyPage(每条都刷)会让 PagedEntries Clear+Add 3000 次,UI 重新布局过载。
        // 分批:满一页或最后一个时刷一次。
        if (_allEntries.Count <= PageSize || _allEntries.Count % PageSize == 0)
        {
            ApplyPage();
        }
        RaisePropertyChanged(nameof(HasEntries));
    }

    private async Task DownloadAsync(CatalogEntry? entry)
    {
        if (entry is null) return;
        var repoUrl = ExtractRepoUrl(entry);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            ErrorMessage = "catalog 条目缺 repository url";
            return;
        }
        // 本地节点目录为空 / 全空白 → 提示用户去 Settings 配置,直接 return 不调 NodeOperations。
        // 这里先看原始字段,不要 Path.Combine 之后再判断(Path.Combine 单边空只会
        // 返回另一边,localDir 永远不会 IsNullOrWhiteSpace,达不到短路效果)。
        if (string.IsNullOrWhiteSpace(_settings.LocalNodeDirectory))
        {
            ErrorMessage = "本地节点目录为空,请先在 Settings 配置";
            return;
        }
        var localDir = Path.Combine(_projectRoot, _settings.LocalNodeDirectory);
        var result = await _nodeOps.DownloadAsync(
            localDir, entry.Package, repoUrl,
            SelectedVersion?.Tag);
        if (!result.Success)
        {
            ErrorMessage = $"下载失败:{result.Reason}";
        }
        else
        {
            InfoMessage = $"已下载 {entry.Package} → version={result.Version}";
        }
    }

    /// <summary>
    /// v0.6.15.2 hotfix:多 fallback 抽 repo URL。上游 ltdrdata custom-node-list.json
    /// 的字段差异很大:多数 entry 没有 <c>repository</c> / <c>url</c>,而是
    /// <c>files[]</c>(第一个就是 GitHub URL)或 <c>reference</c>(GitHub URL)或
    /// <c>id</c>(形如 <c>owner/repo</c>)。优先级:
    /// <list type="number">
    ///   <item>显式 <c>repository</c> 字段(权威)</item>
    ///   <item>显式 <c>url</c> 字段</item>
    ///   <item><c>files</c> 数组首元素(ltdrdata 主流)</item>
    ///   <item><c>reference</c> 字段(若已是 github.com URL)</item>
    ///   <item><c>id</c> 字段(<c>owner/repo</c> 形式补 <c>https://github.com/</c>)</item>
    /// </list>
    /// 返回 null 表示 catalog 条目无任何仓库信息,下载走模板兜底。
    /// </summary>
    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;

        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;

        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;

        if (entry.RawMetadata.TryGetValue("files", out var fVal))
        {
            var first = FirstStringElement(fVal);
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }

        if (entry.RawMetadata.TryGetValue("reference", out var refr) && refr is string refs
            && !string.IsNullOrWhiteSpace(refs) && refs.Contains("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return refs;
        }

        if (entry.RawMetadata.TryGetValue("id", out var id) && id is string ids
            && !string.IsNullOrWhiteSpace(ids))
        {
            // owner/repo 形式(owner/repo 含 1 个 / 且不是 URL)→ 补全成 github URL
            if (ids.Contains('/') && !ids.Contains("://"))
            {
                return $"https://github.com/{ids}";
            }
            return ids;
        }

        return null;
    }

    /// <summary>
    /// 从 <see cref="CatalogEntry.RawMetadata"/> 的 <c>files</c> 字段抽第一个 string 元素。
    /// 字段值可能是:
    /// - <see cref="List{T}"/> of <see cref="object"/> —— ParseRawMetadata 的内存构造路径
    /// - <see cref="System.Text.Json.JsonElement"/>(Array kind) —— SQLite 往返路径(JsonSerializer.Deserialize&lt;Dictionary&lt;string,object?&gt;&gt; 反序列化数组成 JsonElement)
    /// - 单个 <see cref="System.Text.Json.JsonElement"/>(String kind) —— 边界情况
    /// </summary>
    private static string? FirstStringElement(object? fVal)
    {
        if (fVal is null) return null;
        if (fVal is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        return item.GetString();
                }
            }
            else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return je.GetString();
            }
            return null;
        }
        if (fVal is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s)) return s;
                if (item is System.Text.Json.JsonElement innerJe
                    && innerJe.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return innerJe.GetString();
                }
            }
        }
        return null;
    }
}