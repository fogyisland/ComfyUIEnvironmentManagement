using System;
using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Views;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentListViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly ProcessLauncher _launcher;

    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ShowLogCommand { get; }

    public EnvironmentListViewModel(EnvironmentRepository repo, ProcessLauncher launcher)
    {
        _repo = repo;
        _launcher = launcher;
        RefreshCommand = new RelayCommand(_ => Load());
        StartCommand = new RelayCommand(
            async p => await StartEnvAsync(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "stopped");
        StopCommand = new RelayCommand(
            async p => await StopEnvAsync(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "running");
        ShowLogCommand = new RelayCommand(
            p => ShowLog(p as Environment ?? Selected),
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

    private async System.Threading.Tasks.Task StartEnvAsync(Environment? env)
    {
        if (env is null) return;
        try
        {
            await _launcher.StartEnvAsync(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动 env '{env.Name}' 失败:\n{ex.Message}",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 不论成败都 reload —— start 失败可能已经 partial 改了 status,
            // start 成功也拿到新的 pid/status
            Load();
            RaiseCommandsChanged();
        }
    }

    private async System.Threading.Tasks.Task StopEnvAsync(Environment? env)
    {
        if (env is null) return;
        try
        {
            await _launcher.StopEnvAsync(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"停止 env '{env.Name}' 失败:\n{ex.Message}",
                "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Load();
            RaiseCommandsChanged();
        }
    }

    private void ShowLog(Environment? env)
    {
        if (env is null) return;
        var logPath = _launcher.LogFilePath(env.Id);
        LogViewerDialog.Show(env.Id, logPath);
    }

    private void RaiseCommandsChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ShowLogCommand.RaiseCanExecuteChanged();
    }
}
