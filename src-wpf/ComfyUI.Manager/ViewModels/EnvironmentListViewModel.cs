using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentListViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly ProcessLauncher _launcher;
    private readonly EnvCreatorService _envCreator;
    private readonly BaseEnvInstaller _baseEnvInstaller;
    private readonly Settings _settings;
    private readonly BaseEnvProfileLoader _profileLoader;
    private readonly string _projectRoot;
    private readonly string? _initialRecentBasePythonPath;

    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand BaseEnvCommand { get; }

    public string? RecentBasePythonPath { get; private set; }

    public EnvironmentListViewModel(
        EnvironmentRepository repo,
        ProcessLauncher launcher,
        EnvCreatorService envCreator,
        BaseEnvInstaller baseEnvInstaller,
        Settings settings,
        BaseEnvProfileLoader profileLoader,
        string projectRoot,
        string? recentBasePythonPath = null)
    {
        _repo = repo;
        _launcher = launcher;
        _envCreator = envCreator;
        _baseEnvInstaller = baseEnvInstaller;
        _settings = settings;
        _profileLoader = profileLoader;
        _projectRoot = projectRoot;
        _initialRecentBasePythonPath = recentBasePythonPath;
        RecentBasePythonPath = recentBasePythonPath;
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
        CreateCommand = new RelayCommand(_ => CreateEnv());
        BaseEnvCommand = new RelayCommand(
            _ => OpenBaseEnvProgress(),
            _ => Environments.Count > 0);
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
        RecomputeRecentBasePythonPath();
    }

    private void RecomputeRecentBasePythonPath()
    {
        if (Environments.Count == 0)
        {
            RecentBasePythonPath = _initialRecentBasePythonPath;
            return;
        }
        // Pick the env with the latest RootPath mtime (when the directory exists),
        // falling back to descending Id lexicographic order when no mtime is available.
        var latest = Environments
            .OrderByDescending(e =>
            {
                try
                {
                    return Directory.Exists(e.RootPath)
                        ? new DirectoryInfo(e.RootPath).LastWriteTimeUtc.Ticks
                        : 0;
                }
                catch { return 0; }
            })
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
        RecentBasePythonPath = latest?.BasePythonPath;
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

    private void CreateEnv()
    {
        var created = Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, RecentBasePythonPath);
        if (created is not null) Load();
    }

    // Test seam — unit tests set this to intercept the dialog launch (which calls
    // Application.Current.MainWindow + ShowDialog() and would throw in test context).
    public Action<IReadOnlyList<string>, BaseEnvProfile, BaseEnvInstaller>? ShowProgressDialogOverride { get; set; }

    private void OpenBaseEnvProgress()
    {
        if (Selected is null && Environments.Count == 0) return;
        var envIds = Selected is not null
            ? new List<string> { Selected.Id }
            : Environments.Select(e => e.Id).ToList();
        if (envIds.Count == 0) return;

        var profile = _profileLoader.GetHardcodedDefaults().FirstOrDefault();
        if (profile is null) return;

        if (ShowProgressDialogOverride is not null)
        {
            ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
            return;
        }
        Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
    }
}
