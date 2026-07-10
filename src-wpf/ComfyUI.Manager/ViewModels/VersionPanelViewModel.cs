using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class VersionPanelViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WsClient _ws;
    private readonly string _envId;

    public ObservableCollection<VersionStatus> Versions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand ToggleLockCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public event Func<string, Task>? HistoryRequested;

    public VersionPanelViewModel(ApiClient api, WsClient ws, string envId)
    {
        _api = api; _ws = ws; _envId = envId;
        RefreshCommand = new RelayCommand(async _ => await RefreshAllAsync());
        UpgradeCommand = new RelayCommand(
            async p => await UpgradeAsync(p as VersionStatus ?? Selected),
            p => (p as VersionStatus ?? Selected)?.HasUpdate == true
                && (p as VersionStatus ?? Selected)?.Locked == false);
        ToggleLockCommand = new RelayCommand(
            async p => await ToggleLockAsync(p as VersionStatus ?? Selected));
        ShowHistoryCommand = new RelayCommand(
            async p => {
                var v = p as VersionStatus ?? Selected;
                if (v is not null && HistoryRequested is not null)
                    await HistoryRequested(v.Package);
            });

        _ws.OnMessage += async msg =>
        {
            if (msg.Channel == "versionChanged"
                && msg.Args.Length >= 2
                && msg.Args[0].GetString() == _envId)
            {
                await DispatcherHelper.RunOnUiAsync(() => _ = RefreshAllAsync());
            }
        };
        _ = RefreshAllAsync();
    }

    public List<ScannedNode> Packages { get; } = new();
    private string? _selectedPackage;
    public string? SelectedPackage { get => _selectedPackage; set { if (SetField(ref _selectedPackage, value)) _ = RefreshVersionsAsync(); } }

    private VersionStatus? _selected;
    public VersionStatus? Selected { get => _selected; set => SetField(ref _selected, value); }

    private async Task RefreshAllAsync()
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

    private async Task RefreshVersionsAsync()
    {
        if (SelectedPackage is null) return;
        var r = await _api.PostAsync<List<VersionStatus>>(
            "node/list-versions",
            new { env_id = _envId, package = SelectedPackage });
        if (r.Ok && r.Value is not null)
        {
            Versions.Clear();
            foreach (var v in r.Value) Versions.Add(v);
        }
    }

    private async Task UpgradeAsync(VersionStatus? v)
    {
        if (v is null) return;
        await _api.PostAsync<object>("node/upgrade-node",
            new { env_id = _envId, package = v.Package });
        await RefreshVersionsAsync();
    }

    private async Task ToggleLockAsync(VersionStatus? v)
    {
        if (v is null) return;
        var route = v.Locked ? "node/unlock-version" : "node/lock-version";
        await _api.PostAsync<object>(route,
            new { env_id = _envId, package = v.Package });
        await RefreshVersionsAsync();
    }
}
