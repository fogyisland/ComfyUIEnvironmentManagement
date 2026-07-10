using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class DepPanelViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WsClient _ws;
    private readonly string _envId;

    public ObservableCollection<DepRecord> Deps { get; } = new();
    public ObservableCollection<string> Conflicts { get; } = new();
    public List<ScannedNode> Packages { get; } = new();
    public RelayCommand ScanCommand { get; }
    public RelayCommand LocalCheckCommand { get; }
    public RelayCommand GlobalCheckCommand { get; }

    public DepPanelViewModel(ApiClient api, WsClient ws, string envId)
    {
        _api = api; _ws = ws; _envId = envId;
        ScanCommand = new RelayCommand(async _ => await ScanAsync());
        LocalCheckCommand = new RelayCommand(async _ => await LocalCheckAsync());
        GlobalCheckCommand = new RelayCommand(async _ => await GlobalCheckAsync());
        _ws.OnMessage += async msg =>
        {
            if (msg.Channel == "depsChanged"
                && msg.Args.Length >= 2
                && msg.Args[0].GetString() == _envId)
                await DispatcherHelper.RunOnUiAsync(() => _ = LoadPackagesAsync());
        };
        _ = LoadPackagesAsync();
    }

    private string? _selectedPackage;
    public string? SelectedPackage { get => _selectedPackage; set { if (SetField(ref _selectedPackage, value)) _ = LoadDepsAsync(); } }

    private async Task LoadPackagesAsync()
    {
        var r = await _api.PostAsync<List<ScannedNode>>(
            "node/node-list", new { env_id = _envId });
        if (r.Ok && r.Value is not null)
        {
            Packages.Clear();
            foreach (var n in r.Value) Packages.Add(n);
            if (Packages.Count > 0 && SelectedPackage is null)
                SelectedPackage = Packages[0].Package;
        }
    }

    private async Task ScanAsync()
    {
        if (SelectedPackage is null) return;
        await _api.PostAsync<object>("node/scan-deps",
            new { env_id = _envId, package = SelectedPackage });
        await LoadDepsAsync();
    }

    private async Task LoadDepsAsync()
    {
        if (SelectedPackage is null) return;
        var r = await _api.PostAsync<List<DepRecord>>(
            "node/list-deps",
            new { env_id = _envId, package = SelectedPackage });
        if (r.Ok && r.Value is not null)
        {
            Deps.Clear();
            foreach (var d in r.Value) Deps.Add(d);
        }
    }

    private async Task LocalCheckAsync()
    {
        var r = await _api.PostAsync<List<object>>(
            "node/detect-dep-conflicts", new { env_id = _envId });
        Conflicts.Clear();
        if (r.Ok && r.Value is not null)
            foreach (var c in r.Value)
                Conflicts.Add(c.ToString() ?? "");
    }

    private async Task GlobalCheckAsync()
    {
        var r = await _api.PostAsync<List<object>>(
            "node/check-global-compat", new { env_id = _envId });
        Conflicts.Clear();
        if (r.Ok && r.Value is not null)
            foreach (var c in r.Value)
                Conflicts.Add(c.ToString() ?? "");
    }
}