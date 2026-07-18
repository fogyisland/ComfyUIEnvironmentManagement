using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private readonly CatalogRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogRefreshService _refreshService;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;

    private List<CatalogEntry> _allEntries = new();

    public ObservableCollection<CatalogEntry> PagedEntries { get; } = new();
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
    public CatalogEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

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

    public CatalogViewModel(
        CatalogRepository repo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRefreshService refreshService,
        Settings settings,
        SettingsRepository settingsRepo)
    {
        _repo = repo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _refreshService = refreshService;
        _settings = settings;
        _settingsRepo = settingsRepo;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
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
        IsBusy = true;
        try
        {
            var result = await _refreshService.RefreshAsync();
            if (result.Success)
            {
                Search();
                CurrentPage = 1;
                ApplyPage();
                InfoMessage = $"刷新成功,共 {result.EntryCount} 个条目";
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
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
        var result = await _nodeOps.InstallAsync(env.Id, entry.Package, templateUrl);
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