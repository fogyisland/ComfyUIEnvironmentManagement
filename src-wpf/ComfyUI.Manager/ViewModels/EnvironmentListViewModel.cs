using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentListViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;

    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    public EnvironmentListViewModel(EnvironmentRepository repo)
    {
        _repo = repo;
        RefreshCommand = new RelayCommand(_ => Load());
        StartCommand = new RelayCommand(
            p => StartEnv(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "stopped");
        StopCommand = new RelayCommand(
            p => StopEnv(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "running");
        Load();
    }

    private Environment? _selected;
    public Environment? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private void Load()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
    }

    private void StartEnv(Environment? env)
    {
        if (env is null) return;
        // TODO(M5.2-T5): launch ComfyUI process via ProcessLauncher.
        MessageBox.Show(
            $"TODO(M5.2-T5): start env '{env.Name}'",
            "启动环境");
    }

    private void StopEnv(Environment? env)
    {
        if (env is null) return;
        // TODO(M5.2-T5): stop ComfyUI process via ProcessLauncher.
        MessageBox.Show(
            $"TODO(M5.2-T5): stop env '{env.Name}'",
            "停止环境");
    }
}
