using System;
using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class InstallDialogViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    public CatalogEntry Entry { get; }
    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand InstallCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public InstallDialogViewModel(EnvironmentRepository repo, CatalogEntry entry)
    {
        _repo = repo;
        Entry = entry;
        InstallCommand = new RelayCommand(
            _ => Install(),
            _ => SelectedEnv is not null && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        LoadEnvs();
    }

    private Environment? _selectedEnv;
    public Environment? SelectedEnv { get => _selectedEnv; set => SetField(ref _selectedEnv, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { if (SetField(ref _busy, value)) InstallCommand.RaiseCanExecuteChanged(); } }

    private string? _progress;
    public string? Progress { get => _progress; set => SetField(ref _progress, value); }

    private void LoadEnvs()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
        if (Environments.Count > 0) SelectedEnv = Environments[0];
    }

    private void Install()
    {
        if (SelectedEnv is null) return;
        // TODO(M5.2-T7): install node into env via NodeOperations + GitRunner.
        MessageBox.Show(
            $"TODO(M5.2-T7): install '{Entry.Package}' into '{SelectedEnv.Name}'",
            "安装节点");
    }
}
