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

public class CatalogViewModel : ViewModelBase
{
    private readonly CatalogRepository _repo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogRefreshService _refreshService;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;

    private List<CatalogEntry> _allEntries = new();

    public ObservableCollection<CatalogEntry> PagedEntries { get; } = new();
    public ObservableCollection<VersionInfo> SelectedVersions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
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
                RaisePropertyChanged(nameof(SelectedInstallType));
                RaisePropertyChanged(nameof(SelectedDescription));
                RaisePropertyChanged(nameof(SelectedAuthor));
                RaisePropertyChanged(nameof(SelectedTitle));
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
        RaisePropertyChanged(nameof(InstallButtonLabel));
        if (_selected is null) return;
        var versions = _versionRepo.ListByNode(_selected.Id);
        foreach (var v in versions) SelectedVersions.Add(v);
        // 默认选中最新(第一个,已按 published_at DESC)
        if (SelectedVersions.Count > 0)
        {
            SelectedVersion = SelectedVersions[0];
        }
    }

    public bool HasSelected => _selected is not null;
    public bool HasVersions => SelectedVersions.Count > 0;
    public string? SelectedTitle => _selected?.RawMetadata?.TryGetValue("title", out var t) == true ? t?.ToString() : _selected?.Package;
    public string? SelectedAuthor => _selected?.RawMetadata?.TryGetValue("author", out var a) == true ? a?.ToString() : null;
    public string? SelectedDescription => _selected?.RawMetadata?.TryGetValue("description", out var d) == true ? d?.ToString() : null;
    public string? SelectedReference => _selected?.RawMetadata?.TryGetValue("reference", out var r) == true ? r?.ToString() : null;
    public string SelectedReferenceUrl => SelectedReference ?? "";
    public string? SelectedInstallType => _selected?.RawMetadata?.TryGetValue("install_type", out var i) == true ? i?.ToString() : null;
    public string? SelectedLatestVersion => string.IsNullOrEmpty(_selected?.LatestVersion) ? "未知" : _selected!.LatestVersion;

    private VersionInfo? _selectedVersion;
    public VersionInfo? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                RaisePropertyChanged(nameof(SelectedVersionDate));
                RaisePropertyChanged(nameof(InstallButtonLabel));
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

    public string InstallButtonLabel =>
        _selectedVersion is null ? "安装" : $"安装 {_selectedVersion.Tag}";

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

    public RelayCommand CancelRefreshCommand { get; }

    private CancellationTokenSource? _refreshCts;

    public CatalogViewModel(
        CatalogRepository repo,
        NodeVersionRepository versionRepo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRefreshService refreshService,
        Settings settings,
        SettingsRepository settingsRepo)
    {
        _repo = repo;
        _versionRepo = versionRepo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _refreshService = refreshService;
        _settings = settings;
        _settingsRepo = settingsRepo;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        CancelRefreshCommand = new RelayCommand(_ => _refreshCts?.Cancel(), _ => IsBusy);
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
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
        CurrentPage = 1;
        ApplyPage();
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
        IsBusy = true;
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _allEntries.Clear();
        ApplyPage();
        try
        {
            // Progress<T> 在构造时捕获 SynchronizationContext(UI 线程),回调自动 marshal 回来。
            var progress = new Progress<CatalogEntry>(e => OnEntryArrived(e));
            var versionProgress = new Progress<VersionFetchProgress>(vp =>
            {
                if (vp.Total <= 0) return;
                RefreshPercent = (int)(100.0 * vp.Completed / vp.Total);
                ProgressMessage = $"正在拉取版本 {vp.Completed}/{vp.Total}";
            });
            var result = await _refreshService.RefreshAsync(progress, versionProgress, ct);
            if (result.Success)
            {
                CurrentPage = 1;
                ApplyPage();
                var msg = $"刷新成功,共 {result.EntryCount} 个条目";
                if (result.VersionCount > 0)
                    msg += $",其中 {result.VersionCount} 个已获取版本号";
                InfoMessage = msg;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
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

    private async Task InstallAsync(CatalogEntry? entry)
    {
        if (entry is null) return;
        var templateUrl = ExtractRepoUrl(entry);
        if (string.IsNullOrWhiteSpace(templateUrl))
        {
            ErrorMessage = "catalog 条目缺 repository url";
            return;
        }
        var envs = _envRepo.ListAll();
        if (envs.Count == 0)
        {
            ErrorMessage = "没有 env 可安装,先创建一个";
            return;
        }
        var env = envs[0];
        var result = await _nodeOps.InstallAsync(
            env.Id, entry.Package, templateUrl,
            SelectedVersion?.Tag);
        if (!result.Success) ErrorMessage = $"安装失败:{result.Reason}";
        else ErrorMessage = $"已安装 {entry.Package} → version={result.Version}";
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        return null;
    }
}