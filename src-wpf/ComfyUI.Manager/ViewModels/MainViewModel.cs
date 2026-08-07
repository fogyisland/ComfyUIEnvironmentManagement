using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using Microsoft.Win32;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly ProcessLauncher _launcher;
    private readonly BulkUpdateOrchestrator _orchestrator;
    private readonly NodeOperations _nodeOps;
    private readonly EnvCreatorService _envCreator;
    private readonly EnvDeleterService _envDeleter;
    private readonly SettingsRepository _settingsRepo;
    private readonly GitProxyConfig _gitProxy;
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
    private readonly CatalogRefreshService _catalogRefreshService;
    private readonly CatalogCacheStore _catalogCacheStore;
    private readonly BaseEnvInstaller _baseEnvInstaller;
    private readonly BaseEnvProfileLoader _profileLoader;
    private readonly PyTorchVersionDirectory _pytorchVersionDirectory;
    private readonly string _appDataDir;
    private readonly string _projectRoot;
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly SystemInfoCollector _systemInfoCollector;
    private readonly UiPreferencesService _uiPreferencesService;
    // v0.6.5.22: 卸载 service — 跟 BaseEnvInstaller / RequirementsInstaller 同生命周期。
    // 传 EnvListVM 给行内"卸载基础环境" / "卸载依赖"按钮 + per-env mutex 用。
    // 字段类型可空:测试可不传(EnvListVM 自己有 null-fallback ?? new);
    // App.xaml.cs 总是传非 null 实例。
    private readonly BaseEnvUninstaller? _baseEnvUninstaller;
    private readonly RequirementsUninstaller? _requirementsUninstaller;

    public ErrorBannerViewModel ErrorBanner { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    // v0.6.5.20 hotfix:缓存环境页 VM/View,离开"环境"再回来时复用同一实例,
    // 这样进行中的装依赖状态(RequirementsStatus)不会随页面销毁丢失。
    // 之前每次 ShowEnvironments 都 new 一个新 VM,InstallRequirementsAsync 仍
    // 在后台跑 pip,但前台的 RequirementsStatus 已被新实例覆盖,用户回到"环境"
    // 时看到空面板,再点"装依赖"就并发触发第二次 pip。
    // Catalog / BaseEnv / Settings / SystemStatus 暂不缓存(它们是无状态的目录)。
    private EnvironmentListViewModel? _environmentsViewModel;
    private EnvironmentListView? _environmentsView;

    /// <summary>
    /// 测试 seam:构造 <see cref="EnvironmentListView"/> 的工厂 hook。
    /// 默认 new 真实 View;测试可注入 null 或 stub,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<EnvironmentListViewModel, object?>? EnvironmentsViewFactory { get; set; }

    /// <summary>
    /// 测试用:获取当前缓存的"环境"页 VM(若有)。用于断言 ShowEnvironments
    /// 复用同一实例。
    /// </summary>
    internal EnvironmentListViewModel? CurrentEnvironmentsViewModel => _environmentsViewModel;

    /// <summary>
    /// 测试用:获取当前缓存的"环境"页 View 引用,验证 CurrentView 切回去时
    /// 仍是同一份(否则 XAML ContentControl 重新解析会丢绑定的状态)。
    /// </summary>
    internal object? CurrentEnvironmentsView => _environmentsView;

    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowBaseEnvCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenBulkUpdateCommand { get; }
    public RelayCommand ShowSystemStatusCommand { get; }
    public RelayCommand SaveUiPreferencesCommand { get; }
    public RelayCommand LoadUiPreferencesCommand { get; }
    public RelayCommand OpenProjectFolderCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    // v0.6.7.3 T5:打开当前选中 env 的 ComfyUI 配置文件
    public RelayCommand OpenComfySettingsJsonCommand { get; }
    public RelayCommand OpenExtraModelPathsYamlCommand { get; }
    public RelayCommand ExitAppCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand ShowDonateQrCommand { get; }   // v0.6.5.21 hotfix:菜单直接打开赞助二维码独立窗口

    internal Action<string>? OpenFolderOverride { get; set; }  // test seam
    internal Action? ExitAppOverride { get; set; }            // test seam
    internal Func<string, UiPreferences, bool>? SaveUiPreferencesDialogOverride { get; set; }
    internal Func<string, bool>? LoadUiPreferencesDialogOverride { get; set; }
    internal Action? ShowDonateQrOverride { get; set; }       // v0.6.5.21 hotfix test seam
    // v0.6.7.3 T5 test seams
    internal Action<string>? ProcessStartOverride { get; set; }
    internal Action<string>? EnsureFileExistsOverride { get; set; }

    public MainViewModel(
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator,
        EnvDeleterService envDeleter,
        SettingsRepository settingsRepo,
        GitProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogRefreshService catalogRefreshService,
        CatalogCacheStore catalogCacheStore,
        BaseEnvInstaller baseEnvInstaller,
        BaseEnvProfileLoader profileLoader,
        PyTorchVersionDirectory pytorchVersionDirectory,
        string appDataDir,
        string projectRoot,
        RequirementsInstaller requirementsInstaller,
        SystemInfoCollector systemInfoCollector,
        UiPreferencesService uiPreferencesService,
        BaseEnvUninstaller? baseEnvUninstaller = null,
        RequirementsUninstaller? requirementsUninstaller = null)
    {
        _dbFactory = dbFactory;
        _launcher = launcher;
        _orchestrator = orchestrator;
        _nodeOps = nodeOps;
        _envCreator = envCreator;
        _envDeleter = envDeleter;
        _settingsRepo = settingsRepo;
        _gitProxy = gitProxy;
        _settings = settings;
        _catalogFetcher = catalogFetcher;
        _catalogRefreshService = catalogRefreshService;
        _catalogCacheStore = catalogCacheStore;
        _baseEnvInstaller = baseEnvInstaller;
        _profileLoader = profileLoader;
        _pytorchVersionDirectory = pytorchVersionDirectory;
        _appDataDir = appDataDir;
        _projectRoot = projectRoot;
        _requirementsInstaller = requirementsInstaller;
        _systemInfoCollector = systemInfoCollector;
        _uiPreferencesService = uiPreferencesService
            ?? throw new ArgumentNullException(nameof(uiPreferencesService));
        _baseEnvUninstaller = baseEnvUninstaller;
        _requirementsUninstaller = requirementsUninstaller;

        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
        ShowBaseEnvCommand = new RelayCommand(_ => ShowBaseEnv());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
        ShowSystemStatusCommand = new RelayCommand(_ => ShowSystemStatus());
        SaveUiPreferencesCommand = new RelayCommand(_ => SaveUiPreferences(_uiPreferencesService));
        LoadUiPreferencesCommand = new RelayCommand(_ => LoadUiPreferences(_uiPreferencesService));
        OpenProjectFolderCommand = new RelayCommand(_ => OpenFolder(_projectRoot));
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(Path.Combine(_projectRoot, "Logs")));
        // v0.6.7.3 T5:打开 ComfyUI 配置文件 — CanExecute 始终 true,
        // Selected==null 时 helper 直接 return(no-op)。按钮总可点。
        OpenComfySettingsJsonCommand = new RelayCommand(_ => OpenComfyConfigFile("comfy.settings.json"));
        OpenExtraModelPathsYamlCommand = new RelayCommand(_ => OpenComfyConfigFile("extra_model_paths.yaml"));
        ExitAppCommand = new RelayCommand(_ => DoExit());
        ShowAboutCommand = new RelayCommand(_ =>
        {
            var owner = Application.Current?.MainWindow;
            if (owner is null) return;
            AboutDialog.Show(owner, _projectRoot);
        });
        ShowDonateQrCommand = new RelayCommand(_ => ShowDonateQr());
    }

    private void ShowEnvironments()
    {
        if (_environmentsViewModel is null)
        {
            var envRepo = new EnvironmentRepository(_dbFactory);
            _environmentsViewModel = new EnvironmentListViewModel(
                envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader,
                _envDeleter, _nodeOps, _projectRoot, _requirementsInstaller,
                _baseEnvUninstaller, _requirementsUninstaller);
            _environmentsView = EnvironmentsViewFactory is null
                ? new EnvironmentListView { DataContext = _environmentsViewModel }
                : EnvironmentsViewFactory(_environmentsViewModel) as EnvironmentListView;
        }
        CurrentView = _environmentsView;
    }

    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_catalogCacheStore);
        var versionRepo = new NodeVersionRepository(_catalogCacheStore);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot),
        };
    }

    private void ShowBaseEnv()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new BaseEnvView
        {
            DataContext = new BaseEnvViewModel(_profileLoader, envRepo, _baseEnvInstaller, _pytorchVersionDirectory, _appDataDir),
        };
    }

    private void ShowSettings()
    {
        CurrentView = new SettingsView
        {
            DataContext = new SettingsViewModel(_settingsRepo, _gitProxy, new PythonInterpreterValidator(), _settings),
        };
    }

    private void OpenBulkUpdate()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        var nodeRepo = new NodeRepository(_dbFactory);

        // 把 env 列表一次填进 EnvRows(LoadEnvs 会先 Clear),
        // 每个 EnvRow 下面挂它扫到的 node 列表(nodeId = dir name)。
        var vm = new BulkUpdateDialogViewModel(_orchestrator);
        var envRows = envRepo.ListAll().Select(env =>
        {
            var envRow = new EnvRow(env.Id, env.Name);
            foreach (var node in nodeRepo.ListByEnv(env.Id))
            {
                envRow.Nodes.Add(new NodeSelectRow(node.Id, node.Package ?? node.Id));
            }
            return envRow;
        }).ToList();
        vm.LoadEnvs(envRows);
        BulkUpdateDialog.Show(vm);
    }

    private void ShowSystemStatus()
    {
        // SystemStatusViewModel 构造时自动 RefreshAsync()(用户进 tab 立即看到数据)
        CurrentView = new SystemStatusView
        {
            DataContext = new SystemStatusViewModel(_systemInfoCollector),
        };
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);  // 目录不存在先建(OpenLogFolder 适用)
            if (OpenFolderOverride is not null) { OpenFolderOverride(path); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // log 到 ErrorBanner 不抛(用户原话没要弹窗)
            ErrorBanner.Add("open-folder", $"打开文件夹失败:{ex.Message}", ErrorSeverity.Warn);
        }
    }

    /// <summary>
    /// 打开 ComfyUI 配置文件。filename: "comfy.settings.json" 或 "extra_model_paths.yaml"。
    /// 路径 = &lt;env-root&gt;/ComfyUI/{user/default/comfy.settings.json 或 extra_model_paths.yaml}。
    /// shared layout 时 ComfyuiSource 是源目录(ComfyUI 就在那)。
    /// </summary>
    private void OpenComfyConfigFile(string filename)
    {
        var env = CurrentEnvironmentsViewModel?.Selected;
        if (env is null) return;
        var comfyuiRoot = env.ComfyuiLayout == "shared" && env.ComfyuiSource is not null
            ? env.ComfyuiSource
            : Path.Combine(env.RootPath, "ComfyUI");
        string path = filename == "comfy.settings.json"
            ? Path.Combine(comfyuiRoot, "user", "default", "comfy.settings.json")
            : Path.Combine(comfyuiRoot, filename);

        if (EnsureFileExistsOverride is not null)
        {
            EnsureFileExistsOverride(path);
        }
        else
        {
            EnsureFileExists(path);
        }

        if (ProcessStartOverride is not null)
        {
            ProcessStartOverride(path);
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
    }

    /// <summary>
    /// 确保文件存在。comfy.settings.json 不存在 → 写 "{}"。
    /// extra_model_paths.yaml 不存在 → 写 placeholder。
    /// </summary>
    private void EnsureFileExists(string path)
    {
        if (File.Exists(path)) return;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (path.EndsWith("comfy.settings.json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, "{}");
        }
        else
        {
            File.WriteAllText(path, "# ComfyUI Models 路径配置\n# 编辑 base_directory 指向共享 Models 目录(配合 Settings.SharedModelsDirectory)\n");
        }
    }

    private void DoExit()
    {
        if (ExitAppOverride is not null) { ExitAppOverride(); return; }
        Application.Current?.Shutdown();
    }

    private void ShowDonateQr()
    {
        if (ShowDonateQrOverride is not null) { ShowDonateQrOverride(); return; }
        var owner = Application.Current?.MainWindow;
        if (owner is null) return;
        DonateQrWindow.Show(owner, _projectRoot);  // 非模态独立窗口
    }

    private void SaveUiPreferences(UiPreferencesService svc)
    {
        // 收集当前 prefs(Window 尺寸 / LastSelectedEnvId 在 MainWindow code-behind 维护 —
        // 这里简化为只写 prefs.LastViewName,MainWindow.Closing 覆盖完整版)
        var prefs = new UiPreferences { LastViewName = ResolveCurrentViewName() };
        string path;
        if (SaveUiPreferencesDialogOverride is not null)
        {
            path = svc.DefaultPath;
            if (!SaveUiPreferencesDialogOverride(path, prefs)) return;
        }
        else
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "ui-preferences.json",
                InitialDirectory = Path.GetDirectoryName(svc.DefaultPath),
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        svc.SaveToFile(path, prefs);
    }

    private void LoadUiPreferences(UiPreferencesService svc)
    {
        string path;
        if (LoadUiPreferencesDialogOverride is not null)
        {
            path = svc.DefaultPath;
            if (!LoadUiPreferencesDialogOverride(path)) return;
        }
        else
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                InitialDirectory = Path.GetDirectoryName(svc.DefaultPath),
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        svc.LoadFromFile(path);  // 触发 Loaded 事件,订阅者应用
    }

    private string? ResolveCurrentViewName()
    {
        if (CurrentView is null) return null;
        var t = CurrentView.GetType().Name;
        return t switch
        {
            "EnvironmentListView" => "Environments",
            "CatalogView"         => "Catalog",
            "BaseEnvView"         => "BaseEnv",
            "SettingsView"        => "Settings",
            "SystemStatusView"    => "SystemStatus",
            _                     => t,
        };
    }
}
