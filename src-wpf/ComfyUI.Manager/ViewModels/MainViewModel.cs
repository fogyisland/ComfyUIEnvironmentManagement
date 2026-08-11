using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Search;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using Microsoft.Win32;

namespace ComfyUI.Manager.ViewModels;

public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    Settings,
    BulkUpdate,
    SystemStatus
}

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
    // v0.6.11+ T4:ComfyUI Manager toggle 安装器 — 传给 EnvironmentListViewModel 用
    // ToggleComfyUiManagerCommand。App.xaml.cs 总是传非 null;null = 测试 ctor 不传走
    // EnvironmentListViewModel 内部 default ComfyUIManagerInstaller(new RequirementsFileInstaller())。
    private readonly ComfyUIManagerInstaller? _comfyUiManagerInstaller;
    // v0.6.5.22: 卸载 service — 跟 BaseEnvInstaller / RequirementsInstaller 同生命周期。
    // 传 EnvListVM 给行内"卸载基础环境" / "卸载依赖"按钮 + per-env mutex 用。
    // 字段类型可空:测试可不传(EnvListVM 自己有 null-fallback ?? new);
    // App.xaml.cs 总是传非 null 实例。
    private readonly BaseEnvUninstaller? _baseEnvUninstaller;
    private readonly RequirementsUninstaller? _requirementsUninstaller;
    // v0.6.9 T2:接 IThemeService,传给 SettingsViewModel 让 ThemeMode setter 调 Apply。
    private readonly IThemeService? _themeService;
    // v0.6.9 T5:接 IDashboardService(由 T4 完成数据聚合),ShowDashboard 调
    // DashboardViewModel.RefreshAsync。可空保持 12+ MainViewModel 测试兼容 —
    // App.xaml.cs 总是传非 null,ShowDashboard 内 ?? throw 兜底。
    private readonly IDashboardService? _dashboardService;
    // Dashboard VM/View 缓存:ShowDashboard 复用同一实例,避免重复构造 + 保留
    // 上次 LastSnapshot(用户切走再回来直接看旧数据,后台继续 refresh)。
    private DashboardViewModel? _dashboardViewModel;
    private DashboardView? _dashboardView;
    // v0.6.9 T7:接 IGlobalSearchService,让 SpotlightSearchViewModel 构建跨 4
    // kind 的搜索索引。OpenSpotlightCommand 触发首次 BuildAsync,后续键入仅走内存(G7)。
    // 可空保持 12+ MainViewModel 测试兼容 — App.xaml.cs 总是传非 null。
    private readonly IGlobalSearchService? _globalSearchService;
    // v0.6.10 T2:组件报告 + OpenBrowser 共享的 Chrome 优先 fallback service。
    // 默认 null 保留旧测试 ctor;生产 DI 在 App.xaml.cs 注入 new BrowserLauncher()。
    private readonly IBrowserLauncher? _browserLauncher;
    // Spotlight VM 懒构造(只第一次 OpenSpotlight 时建一次 + 注入 navigator)。
    private SpotlightSearchViewModel? _spotlightVm;
    // v0.6.9 T7:SettingsViewModel 缓存 — 之前每次 ShowSettings 都 new 一个新实例,
    // Spotlight SearchTarget.Kind=SettingsSection 时无法 ScrollToSection(新 VM 无 SectionScrollRequested
    // 订阅者)。T7 改成 ShowSettings 复用同一份 VM,ScrollToSection 才能找到 view 端订阅。
    private SettingsViewModel? _settingsViewModel;
    // v0.6.9 T7:CatalogViewModel 缓存 — 跟 SettingsVM 同模式,Spotlight 选中 node 后
    // CatalogViewModel.Selected 必须真的绑上,ShowCatalog 才能命中同一份 VM。
    private CatalogViewModel? _catalogViewModel;
    private CatalogView? _catalogView;

    public ErrorBannerViewModel ErrorBanner { get; } = new();
    public StatusBarViewModel StatusBar { get; }

    private MainSection _currentSection = MainSection.Environments;
    public MainSection CurrentSection
    {
        get => _currentSection;
        private set => SetField(ref _currentSection, value);
    }

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

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
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
    // v0.6.9 T7:Spotlight 全局搜索。MainWindow.xaml.cs 在 OnLoaded 把 Ctrl+K 绑到
    // OpenSpotlightCommand(等 DataContext 就绪)。CloseSpotlightCommand 给 Esc 键用。
    public RelayCommand OpenSpotlightCommand { get; }
    public RelayCommand CloseSpotlightCommand { get; }

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
        RequirementsUninstaller? requirementsUninstaller = null,
        IThemeService? themeService = null,
        IDashboardService? dashboardService = null,
        IGlobalSearchService? globalSearchService = null,
        IBrowserLauncher? browserLauncher = null,
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null)
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
        _themeService = themeService;
        _dashboardService = dashboardService;
        _globalSearchService = globalSearchService;
        // v0.6.10 T2:组件报告 + OpenBrowser 共享 Chrome fallback。
        _browserLauncher = browserLauncher;
        // v0.6.11+ T4:ComfyUI Manager toggle 安装器 — 传给 EnvListVM 的
        // ToggleComfyUiManagerCommand(显示 inline 状态面板)。可空让测试 ctor 不传。
        _comfyUiManagerInstaller = comfyUiManagerInstaller;

        ShowDashboardCommand = new RelayCommand(_ => ShowDashboard());
        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
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
        // v0.6.9 T7:Ctrl+K 打开 Spotlight popup。MainWindow.xaml.cs 在 OnLoaded 后注入 KeyBinding。
        OpenSpotlightCommand = new RelayCommand(_ => OpenSpotlight());
        CloseSpotlightCommand = new RelayCommand(_ => Spotlight?.Close());
        StatusBar = new StatusBarViewModel(this);
    }

    // v0.6.9 T5:Dashboard 页 VM/View 缓存复用(同 ShowEnvironments 模式),
    // 首次进入自动 refresh,后续进入不重复构造 — VM 内部 SemaSlim 二次去重。
    private void ShowDashboard()
    {
        CurrentSection = MainSection.Dashboard;
        if (_dashboardViewModel is null)
        {
            // App.xaml.cs 总是传非 null dashboardService;null → 测试或极端 wiring 漏接,
            // 抛 InvalidOperationException 让问题立刻显形,而不是 NRE 在后台跑。
            var svc = _dashboardService
                ?? throw new InvalidOperationException(
                    "DashboardService not wired — App.xaml.cs 未在 MainViewModel ctor 传 IDashboardService");
            // v0.6.11+ T3:传 browserLauncher 给「下载地址」区块的「浏览器打开」按钮
            // (Chrome 优先 fallback 默认浏览器,跟组件报告 / OpenBrowser 同一套)。
            // T3 fix:再传 ErrorBanner — clipboard / explorer 失败走主窗口 ErrorBanner 反馈
            // (spec §G8 + §Error Handling)。AppLogger 从 service.Logger 拿(无需重 inject)。
            _dashboardViewModel = new DashboardViewModel(svc, _browserLauncher, ErrorBanner);
            _dashboardView = new DashboardView { DataContext = _dashboardViewModel };
        }
        CurrentView = _dashboardView;
        // fire-and-forget:用户进 tab 立刻看到旧数据(若有)+ 后台拉新。
        // DashboardViewModel.RefreshAsync 内部 try/catch cover 住失败语义(G8 partial failure)。
        _ = _dashboardViewModel.RefreshAsync();
    }

    private void ShowEnvironments()
    {
        CurrentSection = MainSection.Environments;
        if (_environmentsViewModel is null)
        {
            var envRepo = new EnvironmentRepository(_dbFactory);
            _environmentsViewModel = new EnvironmentListViewModel(
                envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader,
                _envDeleter, _nodeOps, _projectRoot, _requirementsInstaller,
                _baseEnvUninstaller, _requirementsUninstaller,
                _browserLauncher, ErrorBanner, _comfyUiManagerInstaller);
            _environmentsView = EnvironmentsViewFactory is null
                ? new EnvironmentListView { DataContext = _environmentsViewModel }
                : EnvironmentsViewFactory(_environmentsViewModel) as EnvironmentListView;
        }
        CurrentView = _environmentsView;
    }

    private void ShowCatalog()
    {
        CurrentSection = MainSection.Catalog;
        if (_catalogViewModel is null)
        {
            var catRepo = new CatalogRepository(_catalogCacheStore);
            var versionRepo = new NodeVersionRepository(_catalogCacheStore);
            _catalogViewModel = new CatalogViewModel(
                catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot);
            _catalogView = new CatalogView { DataContext = _catalogViewModel };
        }
        CurrentView = _catalogView;
    }

    private void ShowSettings()
    {
        CurrentSection = MainSection.Settings;
        // v0.6.9 T7:缓存 VM — Spotlight 切到 SettingsSection 时 ScrollToSection 必须命中
        // 当前 View 的 DataContext(同一份 VM),否则新 new 的 VM 没人订阅 SectionScrollRequested。
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(
                _settingsRepo, _gitProxy, new PythonInterpreterValidator(), _settings, _themeService);
            CurrentView = new SettingsView { DataContext = _settingsViewModel };
        }
        else
        {
            CurrentView = new SettingsView { DataContext = _settingsViewModel };
        }
    }

    private void OpenBulkUpdate()
    {
        CurrentSection = MainSection.BulkUpdate;
        var envRepo = new EnvironmentRepository(_dbFactory);

        // v0.6.11 T8:BulkUpdate 不再按节点维度,而是按 env × {ComfyUI, ComfyUI-Manager}
        // 跑 git pull。EnvRow 只挂 env 选择,不再填 Nodes。
        var vm = new BulkUpdateDialogViewModel(_orchestrator);
        var envRows = envRepo.ListAll()
            .Select(env => new EnvRow(env.Id, env.Name))
            .ToList();
        vm.LoadEnvs(envRows);
        BulkUpdateDialog.Show(vm);
    }

    private void ShowSystemStatus()
    {
        CurrentSection = MainSection.SystemStatus;
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
    /// 路径解析按文件名分支:
    /// - comfy.settings.json 永远在 ComfyUI 根目录下 user/default/(shared layout → env.ComfyuiSource,否则 → env-root/ComfyUI)。
    /// - extra_model_paths.yaml 永远在 env-root/(跟 layout 无关;DB 列 Environment.ExtraModelPathsYaml 记精确路径)。
    /// </summary>
    private void OpenComfyConfigFile(string filename)
    {
        var env = CurrentEnvironmentsViewModel?.Selected;
        if (env is null) return;

        string path;
        if (filename == "comfy.settings.json")
        {
            // comfy.settings.json 永远在 ComfyUI 根目录下 user/default/:
            // - isolated layout → <env-root>/ComfyUI/user/default/comfy.settings.json
            // - shared layout → <env.ComfyuiSource>/user/default/comfy.settings.json
            var comfyuiRoot = env.ComfyuiLayout == "shared" && env.ComfyuiSource is not null
                ? env.ComfyuiSource
                : Path.Combine(env.RootPath, "ComfyUI");
            path = Path.Combine(comfyuiRoot, "user", "default", "comfy.settings.json");
        }
        else
        {
            // extra_model_paths.yaml 永远在 <env-root>/,跟 layout 无关。
            // DB 列 Environment.ExtraModelPathsYaml 已经记录了精确路径。
            path = env.ExtraModelPathsYaml
                ?? Path.Combine(env.RootPath, "extra_model_paths.yaml");
        }

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
    /// extra_model_paths.yaml 不存在 → 写 placeholder(UTF-8 BOM,Windows Notepad 才能正常显示中文注释)。
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
            File.WriteAllText(path, "# ComfyUI Models 路径配置\n# 编辑 base_directory 指向共享 Models 目录(配合 Settings.SharedModelsDirectory)\n", System.Text.Encoding.UTF8);
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
            "DashboardView"       => "Dashboard",
            "EnvironmentListView" => "Environments",
            "CatalogView"         => "Catalog",
            "SettingsView"        => "Settings",
            "SystemStatusView"    => "SystemStatus",
            _                     => t,
        };
    }

    // ============ v0.6.9 T7 Spotlight UI 集成 ============

    /// <summary>
    /// 懒构造的 Spotlight VM。XAML 侧 <c>SpotlightSearchBox.DataContext</c>
    /// 绑这个属性,首次访问时才 new(避免 App 启动时同步 BuildAsync 阻塞)。
    /// </summary>
    public SpotlightSearchViewModel? Spotlight
    {
        get
        {
            if (_spotlightVm is null && _globalSearchService is not null)
            {
                _spotlightVm = new SpotlightSearchViewModel(
                    _globalSearchService,
                    target => NavigateToTargetAsync(target));
            }
            return _spotlightVm;
        }
    }

    /// <summary>测试用:获取当前缓存的 Spotlight VM(若有)。</summary>
    internal SpotlightSearchViewModel? CurrentSpotlightViewModel => _spotlightVm;

    /// <summary>测试用:获取当前缓存的 Catalog VM(若有)。</summary>
    internal CatalogViewModel? CurrentCatalogViewModel => _catalogViewModel;

    /// <summary>测试用:获取当前缓存的 Settings VM(若有)。</summary>
    internal SettingsViewModel? CurrentSettingsViewModel => _settingsViewModel;

    private void OpenSpotlight()
    {
        var vm = Spotlight;
        if (vm is null) return;
        _ = vm.OpenAsync();
    }

    /// <summary>
    /// v0.6.9 T7:Spotlight 选中条目后的导航分发(4 kind)。
    /// Environment → ShowEnvironments + EnvironmentListVM.SelectEnvironment
    /// Node        → ShowCatalog + CatalogVM.SelectNode(self-fix:env-list 不显示节点)
    /// SettingsSection → ShowSettings + SettingsVM.ScrollToSection
    /// Command     → reflection 找对应 RelayCommand.Execute
    /// </summary>
    public async Task NavigateToTargetAsync(SearchTarget target)
    {
        switch (target.Kind)
        {
            case TargetKind.Environment:
                ShowEnvironments();
                if (target.EnvId is not null && _environmentsViewModel is not null)
                {
                    _environmentsViewModel.SelectEnvironment(target.EnvId);
                }
                break;

            case TargetKind.Node:
                // self-fix (brief §4.4):节点在 Catalog tab 里显示,不在 env-list。
                ShowCatalog();
                if (target.NodeId is not null && _catalogViewModel is not null)
                {
                    _catalogViewModel.SelectNode(target.NodeId);
                }
                break;

            case TargetKind.SettingsSection:
                ShowSettings();
                if (target.SectionKey is not null && _settingsViewModel is not null)
                {
                    _settingsViewModel.ScrollToSection(target.SectionKey);
                }
                break;

            case TargetKind.Command:
                if (target.CommandName is not null)
                {
                    ExecuteCommand(target.CommandName);
                }
                break;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// v0.6.9 T7:用 reflection 在自己的 RelayCommand property 里找 <paramref name="commandName"/>
    /// + 触发。CommandName 可能带或不带 "Command" 后缀,自动补齐。
    /// <para>
    /// 已知例外:T6 implementer 识别两个 property 名已经带 "Command" 后缀 —
    ///   <c>OpenComfySettingsJsonCommand</c> / <c>OpenExtraModelPathsYamlCommand</c>。
    /// 传 "OpenComfySettingsJson" 或 "OpenComfySettingsJsonCommand" 都行,函数自动判别。
    /// </para>
    /// </summary>
    private void ExecuteCommand(string commandName)
    {
        if (string.IsNullOrEmpty(commandName)) return;
        var propName = commandName.EndsWith("Command", StringComparison.Ordinal)
            ? commandName
            : commandName + "Command";
        var prop = GetType().GetProperty(
            propName,
            BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(this) is RelayCommand rc && rc.CanExecute(null))
        {
            rc.Execute(null);
        }
    }
}
