using System;
using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class HistoryDialogViewModel : ViewModelBase
{
    private readonly VersionRepository _repo;
    private readonly string _envId;
    private readonly string _package;

    public ObservableCollection<VersionHistoryEntry> Entries { get; } = new();
    public RelayCommand RollbackCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public HistoryDialogViewModel(
        VersionRepository repo, string envId, string package)
    {
        _repo = repo;
        _envId = envId;
        _package = package;
        RollbackCommand = new RelayCommand(
            p => Rollback(p as VersionHistoryEntry ?? Selected),
            p => (p as VersionHistoryEntry ?? Selected)?.VersionBefore is not null);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        Load();
    }

    private VersionHistoryEntry? _selected;
    public VersionHistoryEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

    private void Load()
    {
        Entries.Clear();
        foreach (var e in _repo.ListHistoryByEnvAndPackage(_envId, _package))
            Entries.Add(e);
    }

    private void Rollback(VersionHistoryEntry? e)
    {
        if (e?.VersionBefore is null) return;
        // TODO(M5.2-T7): roll back version via NodeOperations + GitRunner.
        MessageBox.Show(
            $"TODO(M5.2-T7): rollback '{_package}' to {e.VersionBefore}",
            "回滚版本");
    }
}
