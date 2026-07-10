using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentDetailViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WsClient _ws;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand RescanCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public EnvironmentDetailViewModel(ApiClient api, WsClient ws, string envId)
    {
        _api = api; _ws = ws; _envId = envId;
        RescanCommand = new RelayCommand(async _ => await RescanAsync());
        ToggleCommand = new RelayCommand(
            async p => await ToggleAsync(p as ScannedNode ?? Selected),
            p => (p as ScannedNode ?? Selected) is not null);

        _ws.OnMessage += async msg =>
        {
            if (msg.Channel == "nodeListChanged"
                && msg.Args.Length >= 1
                && msg.Args[0].GetString() == _envId)
            {
                await DispatcherHelper.RunOnUiAsync(() => _ = LoadAsync());
            }
        };
        _ = LoadAsync();
    }

    private ScannedNode? _selected;
    public ScannedNode? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    private async Task LoadAsync()
    {
        var r = await _api.PostAsync<List<ScannedNode>>(
            "node/node-list", new { env_id = _envId });
        if (r.Ok && r.Value is not null)
        {
            Nodes.Clear();
            foreach (var n in r.Value) Nodes.Add(n);
        }
    }

    private async Task RescanAsync()
    {
        Busy = true;
        try
        {
            await _api.PostAsync<object>(
                "node/request-scan", new { env_id = _envId });
        }
        finally { Busy = false; }
    }

    private async Task ToggleAsync(ScannedNode? node)
    {
        if (node is null) return;
        var route = node.Status == "enabled"
            ? "node/disable-in-env" : "node/enable-in-env";
        await _api.PostAsync<object>(route,
            new { env_id = _envId, node_id = node.Id });
    }
}
