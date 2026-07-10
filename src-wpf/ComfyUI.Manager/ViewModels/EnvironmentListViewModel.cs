using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentListViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WsClient _ws;

    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    public EnvironmentListViewModel(ApiClient api, WsClient ws)
    {
        _api = api;
        _ws = ws;
        RefreshCommand = new RelayCommand(async _ => await LoadAsync());
        StartCommand = new RelayCommand(
            async p => await StartEnvAsync(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "stopped");
        StopCommand = new RelayCommand(
            async p => await StopEnvAsync(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "running");

        _ws.OnMessage += async msg =>
        {
            if (msg.Channel == "envListChanged"
                || msg.Channel == "envStatusChanged"
                || msg.Channel == "envStarted"
                || msg.Channel == "envStopped")
            {
                await DispatcherHelper.RunOnUiAsync(() => _ = LoadAsync());
            }
        };
        _ = LoadAsync();
    }

    private Environment? _selected;
    public Environment? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private async Task LoadAsync()
    {
        var env = await _api.PostAsync<List<Environment>>(
            "env/list", new { });
        if (env.Ok && env.Value is not null)
        {
            Environments.Clear();
            foreach (var e in env.Value) Environments.Add(e);
        }
    }

    private async Task StartEnvAsync(Environment? env)
    {
        if (env is null) return;
        var r = await _api.PostAsync<object>(
            "process/start-env", new { env_id = env.Id });
        if (!r.Ok && r.Error is not null)
            MessageBox.Show(r.Error.Message, "启动失败");
    }

    private async Task StopEnvAsync(Environment? env)
    {
        if (env is null) return;
        var r = await _api.PostAsync<object>(
            "process/stop-env", new { env_id = env.Id, timeout = 3 });
        if (!r.Ok && r.Error is not null)
            MessageBox.Show(r.Error.Message, "停止失败");
    }
}
