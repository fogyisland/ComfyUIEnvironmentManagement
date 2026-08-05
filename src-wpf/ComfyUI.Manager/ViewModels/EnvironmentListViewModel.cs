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
    private readonly EnvDeleterService _envDeleter;
    private readonly NodeOperations _nodeOps;
    private readonly string _projectRoot;

    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand BaseEnvCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand InstallNodeCommand { get; }

    public string? RecentBasePythonPath { get; private set; }

    /// <summary>
    /// Test seam — unit tests set this to intercept the confirmation dialog (which would
    /// call MessageBox.Show and hang in test context). Returns true = user confirmed, false = cancelled.
    /// </summary>
    public Func<Environment, bool>? ConfirmDeleteOverride { get; set; }

    public EnvironmentListViewModel(
        EnvironmentRepository repo,
        ProcessLauncher launcher,
        EnvCreatorService envCreator,
        BaseEnvInstaller baseEnvInstaller,
        Settings settings,
        BaseEnvProfileLoader profileLoader,
        EnvDeleterService envDeleter,
        NodeOperations nodeOps,
        string projectRoot)
    {
        _repo = repo;
        _launcher = launcher;
        _envCreator = envCreator;
        _baseEnvInstaller = baseEnvInstaller;
        _settings = settings;
        _profileLoader = profileLoader;
        _envDeleter = envDeleter;
        _nodeOps = nodeOps;
        _projectRoot = projectRoot;
        RecentBasePythonPath = null;
        RefreshCommand = new RelayCommand(_ => Load());
        StartCommand = new RelayCommand(
            async p => await StartEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (env.Status != "stopped") return false;
                // BED 未装 / 装中 → 禁用
                if (env.BedStatus is null or "installing") return false;
                return true;
            });
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
        DeleteCommand = new RelayCommand(
            async p => await DeleteEnvAsync(p as Environment ?? Selected),
            p => (p as Environment ?? Selected) is not null);
        InstallNodeCommand = new RelayCommand(
            p => OpenInstallNodePicker(p as Environment ?? Selected),
            p => (p as Environment ?? Selected) is not null);
        Load();
    }

    private Environment? _selected;
    public Environment? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                RaisePropertyChanged(nameof(StartTooltip));
        }
    }

    /// <summary>
    /// 启动按钮 tooltip 文本:基于 Selected env 的 BED 状态。
    /// - BedStatus null   → "基础环境未安装"
    /// - BedStatus "installing" → "基础环境安装中,请稍候"
    /// - BedStatus "failed" → "上次基础环境部署失败:{BedFailedReason};运行可能也失败"
    /// - BedStatus "done"  → ""(BED OK,不需要提示)
    /// - env is null       → ""
    /// </summary>
    public string StartTooltip
    {
        get
        {
            var env = Selected;
            if (env is null) return "";
            return env.BedStatus switch
            {
                null => "基础环境未安装",
                "installing" => "基础环境安装中,请稍候",
                "failed" => $"上次基础环境部署失败:{env.BedFailedReason};运行可能也失败",
                _ => "",
            };
        }
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
            RecentBasePythonPath = null;
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
        // BED dialog 关窗后 reload:Installer 末尾已写 env.BedStatus,
        // UI 立即重读反映新状态(否则用户看到行还是旧的 "未装")
        Load();
        RaiseCommandsChanged();
    }

    /// <summary>
    /// DeleteEnvAsync:确认 → 调 EnvDeleterService(stop running + 删目录 + 删 SQLite 行)
    /// → 失败弹 MessageBox,成功 reload + RaiseCommandsChanged。
    /// </summary>
    private async System.Threading.Tasks.Task DeleteEnvAsync(Environment? env)
    {
        if (env is null) return;

        // Test seam 优先:设了 override 就走它(不论 true/false),不再 fall through
        // 到 MessageBox — 测试环境没有 UI dispatcher,弹框会挂死。
        bool confirmed;
        if (ConfirmDeleteOverride is not null)
        {
            confirmed = ConfirmDeleteOverride(env);
        }
        else
        {
            var result = MessageBox.Show(
                $"确认删除 env '{env.Name}' 吗?\n此操作会删除 env 目录及所有数据,不可撤销。",
                "删除环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            confirmed = result == MessageBoxResult.Yes;
        }
        if (!confirmed) return;

        try
        {
            await _envDeleter.DeleteAsync(env);
            Load();
            RaiseCommandsChanged();
        }
        catch (EnvDeleterService.DeleteException ex)
        {
            MessageBox.Show(
                $"删除 env '{env.Name}' 失败 ({ex.Code}):\n{ex.Message}",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"删除 env '{env.Name}' 异常:\n{ex.Message}",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// OpenInstallNodePicker:从 env 行点"安装节点" → 弹 CatalogEntryPickerDialog 选条目
    /// → 弹 InstallDialog(预填 env) → 用户选则 install。
    /// </summary>
    private void OpenInstallNodePicker(Environment? env)
    {
        if (env is null) return;

        if (OpenInstallPickerOverride is not null)
        {
            OpenInstallPickerOverride(env);
            return;
        }

        var entry = Views.CatalogEntryPickerDialog.Show();
        if (entry is null) return;

        Views.InstallDialog.Show(_repo, _nodeOps, entry, preselectedEnvId: env.Id);
    }

    /// <summary>
    /// Test seam — unit tests set this to intercept the picker + InstallDialog launch
    /// (both call Application.Current.MainWindow and would throw in test context).
    /// Receives the env the command was bound to.
    /// </summary>
    public Action<Environment>? OpenInstallPickerOverride { get; set; }

    private void RaiseCommandsChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ShowLogCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        InstallNodeCommand.RaiseCanExecuteChanged();
    }
}
