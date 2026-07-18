using System;
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
    private const int SearchLimit = 50;
    private readonly CatalogRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogFetcher _fetcher;
    private readonly Settings _settings;

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }

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
        set => SetField(ref _errorMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public CatalogViewModel(
        CatalogRepository repo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogFetcher fetcher,
        Settings settings)
    {
        _repo = repo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _fetcher = fetcher;
        _settings = settings;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        Search();
    }

    private void Search()
    {
        Entries.Clear();
        foreach (var e in _repo.Search(_query, SearchLimit)) Entries.Add(e);
    }

    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        var active = _settings.ActiveQuerySourceName;
        var src = _settings.QuerySources.FirstOrDefault(s => s.Name == active);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            ErrorMessage = "未配置查询源,请先在 Settings 添加";
            return;
        }

        IsBusy = true;
        try
        {
            var entries = await _fetcher.FetchAsync(src.Url);
            foreach (var e in entries)
            {
                e.SourceUrl = src.Url;
                _repo.Upsert(e);
            }
            Search();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"拉取失败: {ex.Message}(本地缓存仍可用)";
            Search();
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
        if (!result.Success)
        {
            ErrorMessage = $"安装失败:{result.Reason}";
        }
        else
        {
            ErrorMessage = $"已安装 {entry.Package} → version={result.Version}";
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }
}