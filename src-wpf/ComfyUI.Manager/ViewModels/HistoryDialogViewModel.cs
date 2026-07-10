using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class HistoryDialogViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly string _envId;
    private readonly string _package;

    public ObservableCollection<VersionHistoryEntry> Entries { get; } = new();
    public RelayCommand RollbackCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public HistoryDialogViewModel(ApiClient api, string envId, string package)
    {
        _api = api; _envId = envId; _package = package;
        RollbackCommand = new RelayCommand(
            async p => await RollbackAsync(p as VersionHistoryEntry ?? Selected),
            p => (p as VersionHistoryEntry ?? Selected)?.VersionBefore is not null);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = LoadAsync();
    }

    private VersionHistoryEntry? _selected;
    public VersionHistoryEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

    private async Task LoadAsync()
    {
        var r = await _api.PostAsync<List<VersionHistoryEntry>>(
            "node/list-version-history",
            new { env_id = _envId, package = _package, limit = 50 });
        if (r.Ok && r.Value is not null)
        {
            Entries.Clear();
            foreach (var e in r.Value) Entries.Add(e);
        }
    }

    private async Task RollbackAsync(VersionHistoryEntry? e)
    {
        if (e?.VersionBefore is null) return;
        await _api.PostAsync<object>("node/rollback-version",
            new { env_id = _envId, package = _package, history_id = e.Id });
        CloseRequested?.Invoke();
    }
}