using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) _ = SearchAsync(); }
    }

    public CatalogViewModel(ApiClient api)
    {
        _api = api;
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        _ = SearchAsync();
    }

    private CatalogEntry? _selected;
    public CatalogEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

    private async Task SearchAsync()
    {
        var r = await _api.PostAsync<List<CatalogEntry>>(
            "node/search-catalog", new { query = _query, page = 0 });
        if (r.Ok && r.Value is not null)
        {
            Entries.Clear();
            foreach (var e in r.Value) Entries.Add(e);
        }
    }

    private async Task RefreshAsync()
    {
        await _api.PostAsync<int>("node/refresh-catalog", new { });
        await SearchAsync();
    }

    private async Task InstallAsync(CatalogEntry? entry)
    {
        if (entry is null) return;
        await _api.PostAsync<object>("node/install-from-catalog",
            new { package = entry.Id, target_env_id = (string?)null });
    }
}