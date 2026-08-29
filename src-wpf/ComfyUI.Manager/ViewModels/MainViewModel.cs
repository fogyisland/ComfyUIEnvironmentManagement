using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Search;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;
using ComfyUI.Manager.Services.ModelSources;
using ComfyUI.Manager.Views;
using ComfyUI.Manager.Views.TemplateManagement;
using Microsoft.Win32;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    LocalNodes,  // v0.6.15
    // v0.6.19: 工作流市场 — between LocalNodes and Settings
    Workflows,
    // v1.0.0 本地模型: 已下载模型纯查看页 — Kind 分类 + 切走再回来保留 sort/filter。
    // 紧邻 Workflows(同类"内容源"分区,UI 连排便于切换)。
    LocalModels,
    // v1.0.0 multi-template T8: 模板管理 — 紧邻 Workflows(同类"内容源"分区,
    // UI 上连排便于切换)。
    Templates,
    // v0.6.20 T9: 模型市场 — 紧邻 Workflows(同类"市场"分区,UI 上连排便于切换)。
    Models,
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
    private readonly HttpProxyConfig _gitProxy;
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
    private readonly CatalogRefreshService _catalogRefreshService;
    private readonly GitHubVersionService _githubVersionService;
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
    // v1.0.0.x:Forge 「安装基础环境」installer — 传给 EnvironmentListViewModel 让 Forge env
    // 跳过 BaseEnvProfilePickerDialog + BaseEnvProgressDialog,直接 dispatch ForgeBaseEnvInstaller
    // 跑 0-5 全套,inline 面板显示进度。App.xaml.cs 总是传非 null;null = 测试 ctor 不传走
    // EnvListVM 内部 default ForgeBaseEnvInstaller()(跑不出状态但构造 OK)。
    private readonly ForgeBaseEnvInstaller? _forgeBaseEnvInstaller;
    // v1.0.0.x #577:本地常用节点批量 installer — 传给 EnvListVM 的 InstallLocalNodesCommand。
    private readonly LocalNodeBulkInstaller? _localNodeBulkInstaller;
    // v1.0.0.x #589:env → localnodes 反向 sync service — 传给 SettingsViewModel
    // SyncNodesFromEnvCommand 把 ComfyUI-Manager 装的节点补到本地源目录。可空让测试 ctor
    // 不传(SettingsViewModel 的 SyncNodesFromEnvCommand CanExecute 返 false,按钮 disabled)。
    private readonly LocalNodeSyncService? _localNodeSyncService;
    // v1.0.0.x:SettingsView「下载到本地节点目录」按钮依赖 — App.xaml.cs 注入共享实例,
    // 让 SettingsVM 的 DownloadCommonNodesCommand 调 InstallEnabledToAsync 把 enabled
    // common_nodes git clone 到 settings.LocalNodesDirectory。可空:测试 ctor 不传
    // (DownloadCommonNodesCommand CanExecute 返 false,按钮 disabled)。
    private readonly CommonNodeInstaller? _commonNodeInstaller;
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
    // v0.6.14 T6: 退出清理 service —— MainWindow.OnClosing 调它(graceful stop
    // + 翻 status=stopped);MainWindow 通过 GetExitCleanupService() 拿到。
    // 测试侧可不传(App.xaml.cs 总是传非 null)。
    private readonly EnvExitCleanupService? _envExitCleanup;
    // v0.6.14 R1:EnvironmentRepository —— GetRunningEnvCount 走 SELECT COUNT(*)
    // 不再 ListAll().Where().Count()。可空:测试 ctor 不传走 null fallback。
    private readonly EnvironmentRepository? _envRepo;
    // v0.6.15: 进程级 rate limit 单例 —— 透传给 CatalogViewModel。可空保留旧测试 ctor。
    private readonly IRateLimitState? _rateLimitState;
    // v0.6.19 T10: 共享 HttpClient(singleton, App.xaml.cs 注入)— ShowWorkflows 构造
    // 3 个 IWorkflowSource (CommunityJson / CivitAi / OpenArt) + WorkflowDownloader
    // 都用同一个 _http。YAGNI: 默认 null 保留旧测试 ctor 兼容。
    private readonly HttpClient? _http;
    // v0.6.22+:per-source HttpClient builder — 传给 ModelSourceFactory 让每个 source 拿自己的
    // HttpClient(per-source proxy toggle 才生效)。null → ShowModels 退回到包 _http 的简单 lambda。
    // 可空保留旧测试 ctor 兼容。
    private readonly Func<HttpProxyConfig?, HttpClient>? _httpBuilder;
    // v0.6.19 T10: WorkflowSymlinker — ShowEnvironments 时传给 EnvironmentListViewModel,
    // 让 env-start 成功后 fire-and-forget 把已下载 workflow subfolder symlink 到
    // <env.ComfyuiSource>/user/default/workflows/。可空保留旧测试 ctor 兼容。
    private readonly WorkflowSymlinker? _workflowSymlinker;
    // v0.6.20 T9: ModelSymlinker — 透传给 EnvironmentListViewModel 让 env-start 成功后
    // fire-and-forget sync 已下载 models 到 env。可空保留旧测试 ctor 兼容。
    private readonly ModelSymlinker? _modelSymlinker;
    // v1.0.0 T11: 通用 template source updater —— 由 App.xaml.cs 注入, 透传给
    // TemplateManagementViewModel(ShowTemplateManagement), 让每张模板卡
    // "更新源码" 按钮调用 TemplateSourceUpdater.UpdateAsync(targetDir, repoUrl)。
    private readonly TemplateSourceUpdater? _templateSourceUpdater;
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
    // v0.6.15:本地节点页 VM/View 缓存复用(同 Catalog/Settings 模式),
    // 首次进入构造 LocalNodeListViewModel(走 LocalNodeService.ListAsync 拉本地),
    // 后续进入复用同一份 VM,保留 busy 状态 + Items 内容。
    private LocalNodeListViewModel? _localNodesViewModel;
    private LocalNodeListView? _localNodesView;
    // v0.6.18:批量更新 inline VM/View 缓存(替代原 BulkUpdateDialog 弹窗模式)。
    // 同 ShowCatalog / ShowLocalNodes 懒构造模式:首次进入构造 VM+View,
    // 后续进入复用同一份,保留 IsBusy + Rows + Summary。切走 section 时
    // 若 IsBusy,VM.CancelRun() 兜底取消,避免 Task.Run 漏掉。
    private BulkUpdateViewModel? _bulkUpdateViewModel;
    private BulkUpdateView? _bulkUpdateView;
    // v0.6.19 T10: 工作流市场 VM/View 缓存(同 ShowCatalog / ShowLocalNodes /
    // OpenBulkUpdate 懒构造模式)。首次进入构造 VM + 触发 LoadAsync(后台拉 3 个 source),
    // 后续进入复用同一份 VM,保留 IsBusy + Workflows + Selected + ConsoleLog 状态。
    private WorkflowMarketplaceViewModel? _workflowMarketplaceViewModel;
    private WorkflowMarketplaceView? _workflowMarketplaceView;
    // v0.6.20 T9: 模型市场 VM/View 缓存(同 ShowWorkflows 模式 — 首次进入构造 +
    // 后台触发 LoadAsync,后续进入复用同一份 VM 保留 IsBusy / Models / SelectedVersions)。
    private ModelMarketplaceViewModel? _modelMarketplaceViewModel;
    private ModelMarketplaceView? _modelMarketplaceView;
    // v1.0.0 T8: 模板管理页 VM/View 缓存(同 ShowLocalNodes 懒构造模式)。
    // 首次进入 new VM(从 Settings.Templates 拷贝列表),View 由 T9 factory 注入;
    // 切走再回来复用同一份 VM,保留编辑状态(选中/滚动位置)。
    private TemplateManagementViewModel? _templateManagementViewModel;
    private object? _templateManagementView;
    // v1.0.0 T3: 本地模型页 VM/View 缓存(同 ShowTemplateManagement 懒构造模式)。
    // 首次进入 new VM(注入 Settings + scanner + logger),View 由 T3 factory 注入;
    // 切走再回来复用同一份 VM 保留 kind chip 选中 / sort / 滚动位置。
    private LocalModelsViewModel? _localModelsViewModel;
    private object? _localModelsView;
    // v1.0.0 T13-7:hash cache + matcher orchestrator — ShowLocalModels 时构造一次,透传给
    // LocalModelsViewModel 的 ScanContext(走 hash + 4 策略 match + cover download 路径)。
    // 不放字段 → 每次 ShowLocalModels 重新 new → SQLite 重新打开 + 4 matchers 重新构造 → 浪费。
    // null 表示 CivitAI disabled 或 _httpBuilder 缺失 → 走 legacy 纯 enumeration 路径。
    private CivitaiHashCache? _civitaiHashCache;
    private CivitaiMatcherOrchestrator? _civitaiMatcherOrchestrator;

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
    // v0.6.11+ SDD D1:AppLogger — RestartEnvAsync 在 env 找不到 / EnvListVM 未构造
    // 时打 WARN。跟 EnvListVM 内部 _logger 同一份,nullable ctor,生产 DI 在
    // App.xaml.cs 注入(已有 var logger = new AppLogger(projectRoot);)。
    private readonly AppLogger? _logger;

    /// <summary>
    /// 测试 seam:构造 <see cref="EnvironmentListView"/> 的工厂 hook。
    /// 默认 new 真实 View;测试可注入 null 或 stub,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<EnvironmentListViewModel, object?>? EnvironmentsViewFactory { get; set; }

    /// <summary>
    /// 测试 seam:构造 <see cref="SettingsView"/> 的工厂 hook。默认 new 真实 View;
    /// 测试可注入 null(stub 返回),绕开 WPF STA 初始化,直接拿到缓存的 VM。
    /// v0.6.11+ SDD B T3:用于无 STA 的单元测试触发 CurrentSettingsViewModel 缓存,
    /// 跑 ConfirmDiscardUnsavedSettings 逻辑。
    /// </summary>
    internal Func<SettingsViewModel, object?>? SettingsViewFactory { get; set; }

    /// <summary>
    /// v0.6.18:构造 <see cref="BulkUpdateView"/> 的工厂 hook。默认 new 真实 View;
    /// 测试可注入 stub 返回,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<BulkUpdateViewModel, object?>? BulkUpdateViewFactory { get; set; }

    /// <summary>
    /// v0.6.19 T10: 构造 <see cref="WorkflowMarketplaceView"/> 的工厂 hook。
    /// 默认 new 真实 View;测试可注入 stub 返回,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<WorkflowMarketplaceViewModel, object?>? WorkflowMarketplaceViewFactory { get; set; }

    /// <summary>
    /// v0.6.20 T9: 构造 <see cref="ModelMarketplaceView"/> 的工厂 hook。
    /// 默认 new 真实 View;测试可注入 stub 返回,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<ModelMarketplaceViewModel, object?>? ModelMarketplaceViewFactory { get; set; }

    /// <summary>
    /// v1.0.0 T8: 构造模板管理 View 的工厂 hook。
    /// 默认 new <see cref="TemplateManagementView"/>(T9 落地的 UserControl);
    /// 测试可注入 stub 返回,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<TemplateManagementViewModel, object?>? TemplateManagementViewFactory { get; set; }

    /// <summary>
    /// v1.0.0 T3: 构造本地模型 View 的工厂 hook。
    /// 默认 new <see cref="LocalModelsView"/>;测试可注入 stub 返回,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<LocalModelsViewModel, object?>? LocalModelsViewFactory { get; set; }

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

    /// <summary>
    /// v0.6.16:触发 catalog 刷新(含 GitHub metadata enrichment 如果
    /// <c>settings.FetchCatalogMetadata=true</c>)。
    /// <para>
    /// <c>--auto-refresh-catalog</c> CLI flag 用 — 启动后后台跑,不阻塞 UI。
    /// 跟 <c>RestartEnvAsync</c> 一样 fire-and-forget,异常由
    /// CatalogRefreshService 内部处理。
    /// </para>
    /// </summary>
    public Task RefreshCatalogAsync() => _catalogRefreshService.RefreshAsync();

    /// <summary>
    /// v0.6.11+ SDD D1: InstallDialog 装成功回调,触发 env 重启(Stop if running
    /// + Start)。envId = 装成功的 env 标识;实现 = 切到 env-list tab → 在
    /// EnvListVM.Environments 找 env → 委托给 EnvListVM.RestartEnvInternalAsync。
    /// 失败(异常)由 EnvListVM 内部 catch + status.Fail + AppLogger.Error 处理,
    /// 节点不撤;本方法不 rethrow(节点装回调是 fire-and-forget,异常会丢)。
    /// 跳过条件:env 找不到 / EnvListVM 未构造(ShowEnvironments 还没调过)/
    /// env 已在 busy 状态(EnvListVM 内部 per-env 互斥锁)。
    /// </summary>
    public async Task RestartEnvAsync(string envId)
    {
        // test seam 优先 — 单元测试可注入只记录不真跑 stop+start 的函数,
        // 避免 ProcessLauncher / STA / 进程启动副作用。
        if (RestartEnvOverride is not null)
        {
            await RestartEnvOverride(envId);
            return;
        }

        // 先切到 env-list tab — 用户立刻看到进度面板
        // (MVM 端 CurrentSection + 触发 ShowEnvironments 让 EnvListVM 构造)。
        // 如果 EnvListVM 已存在,直接复用(ShowEnvironments 是幂等的)。
        ShowEnvironmentsCommand.Execute(null);

        var envListVm = _environmentsViewModel;
        if (envListVm is null)
        {
            _logger?.Warn("auto-restart-env",
                $"EnvListVM 未构造,跳过重启 env {envId}");
            return;
        }

        var env = envListVm.Environments.FirstOrDefault(e => e.Id == envId);
        if (env is null)
        {
            _logger?.Warn("auto-restart-env",
                $"env {envId} 不存在,跳过重启");
            return;
        }

        // EnvListVM 内部 per-env 互斥锁 + EnvStartStatusViewModel 反馈 + 失败 catch
        await envListVm.RestartEnvInternalAsync(env, CancellationToken.None);
    }

    /// <summary>
    /// v0.6.11+ SDD D1 test seam:替代默认的 envListVm.RestartEnvInternalAsync 调用。
    /// 单元测试可注入只记录不真跑 stop+start 的函数,避免 STA / 进程启动副作用。
    /// null = 走默认路径(envListVm.RestartEnvInternalAsync)。
    /// </summary>
    internal Func<string, Task>? RestartEnvOverride { get; set; }

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowLocalNodesCommand { get; }   // v0.6.15
    public RelayCommand ShowWorkflowsCommand { get; }    // v0.6.19 T10: 侧栏 8th "工作流市场"
    public RelayCommand ShowModelsCommand { get; }      // v0.6.20 T9: 侧栏 9th "模型市场"
    public RelayCommand ShowTemplateManagementCommand { get; }  // v1.0.0 T8: 模板管理
    public RelayCommand ShowLocalModelsCommand { get; }       // v1.0.0 T3: 本地模型
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
    // v1.0.0 T11: removed UpdateTemplateCommand — 工具菜单 → 模板更新 入口删除,
    // 改到 TemplateManagementView 的每张模板卡 "更新源码" 按钮。
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand ShowCoursesCommand { get; }       // v1.0.0:顶级 dropdown "ComfyUI 课程" → ComfyUICoursesWindow
    public RelayCommand ShowComfyUIGroupCommand { get; }   // v1.0.0:顶级 dropdown "ComfyUI 群组" → ComfyUIGroupQrWindow
    public RelayCommand ShowDonateQrCommand { get; }       // v0.6.5.21 hotfix:菜单直接打开赞助二维码独立窗口
    // v0.6.9 T7:Spotlight 全局搜索。MainWindow.xaml.cs 在 OnLoaded 把 Ctrl+K 绑到
    // OpenSpotlightCommand(等 DataContext 就绪)。CloseSpotlightCommand 给 Esc 键用。
    public RelayCommand OpenSpotlightCommand { get; }
    public RelayCommand CloseSpotlightCommand { get; }

    internal Action<string>? OpenFolderOverride { get; set; }  // test seam
    internal Action? ExitAppOverride { get; set; }            // test seam
    internal Func<string, UiPreferences, bool>? SaveUiPreferencesDialogOverride { get; set; }
    internal Func<string, bool>? LoadUiPreferencesDialogOverride { get; set; }
    internal Action? ShowDonateQrOverride { get; set; }       // v0.6.5.21 hotfix test seam
    internal Action? ShowCoursesOverride { get; set; }        // v1.0.0 test seam
    internal Action? ShowComfyUIGroupOverride { get; set; }   // v1.0.0 test seam
    // v1.0.0 T11: removed ConfirmDangerousOverride — 工具菜单 → 模板更新 删除,
    // TemplateManagementView 每张卡的 "更新源码" 按钮自己 confirm。
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
        HttpProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogRefreshService catalogRefreshService,
        CatalogCacheStore catalogCacheStore,
        GitHubVersionService githubVersionService,
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
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
        AppLogger? logger = null,
        // v0.6.14 T6: 退出清理 service —— MainWindow.OnClosing 通过 GetExitCleanupService()
        // 拿到它来 graceful 停 running env。可空保留旧测试 ctor 兼容。
        EnvExitCleanupService? envExitCleanup = null,
        // v0.6.14 R1:EnvironmentRepository —— GetRunningEnvCount 走 COUNT(*) 查询;
        // 可空保持旧测试 ctor 兼容。生产 DI(App.xaml.cs)总是传。
        EnvironmentRepository? envRepo = null,
        // v0.6.15: rate limit 单例 — 透传给 CatalogViewModel。
        IRateLimitState? rateLimitState = null,
        // v0.6.19 T10: 共享 HttpClient — ShowWorkflows 用它构造 3 个 IWorkflowSource
        // + WorkflowDownloader。可空保留旧测试 ctor 兼容(传 null 时 ShowWorkflows
        // 抛 InvalidOperationException — App.xaml.cs 总是传非 null)。
        HttpClient? http = null,
        // v0.6.19 T10: WorkflowSymlinker — 传给 EnvironmentListViewModel 让
        // env-start 成功后 fire-and-forget sync workflows 到 env 的 user/default/workflows/。
        // 可空保留旧测试 ctor 兼容。
        WorkflowSymlinker? workflowSymlinker = null,
        // v0.6.20 T9: ModelSymlinker — 传给 EnvironmentListViewModel 让 env-start 成功后
        // fire-and-forget sync 已下载 models 到 env 的 models/<kind>/。可空保留旧测试 ctor 兼容。
        ModelSymlinker? modelSymlinker = null,
        // v1.0.0 T11: 通用 template source updater —— 由 ShowTemplateManagement
        // 透传给 TemplateManagementViewModel 让 "更新源码" 按钮触发 wipe + git clone。
        // 可空保留测试 ctor 兼容(null 时 UpdateSourceCommand 走 no-op)。
        TemplateSourceUpdater? templateSourceUpdater = null,
        // v0.6.22+:per-source HttpClient builder — 传给 ModelSourceFactory 让每个 source
        // 拿自己的 HttpClient(per-source proxy toggle 才生效)。可空保留旧测试 ctor 兼容
        // (null 时 ShowModels 退回到包 _http 的简单 lambda,共享 singleton,无 per-source proxy)。
        Func<HttpProxyConfig?, HttpClient>? httpBuilder = null,
        // v1.0.0.x #577:本地常用节点批量 installer — 传给 EnvironmentListViewModel 的
        // InstallLocalNodesCommand(显示 inline 状态面板)。复用 reqFileInstaller + logger。
        // 可空让测试 ctor 不传(EnvListVM fallback 自己 new 一份)。
        LocalNodeBulkInstaller? localNodeBulkInstaller = null,
        // v1.0.0.x #589:env → localnodes sync service。可空让测试不传。
        LocalNodeSyncService? localNodeSyncService = null,
        // v1.0.0.x:SettingsView「下载到本地节点目录」按钮依赖 — 透传给 SettingsViewModel。
        // 共享 App.xaml.cs 已构造的实例(同 gitRunner + gitProxy + logger),避免重复创建。
        CommonNodeInstaller? commonNodeInstaller = null,
        // v1.0.0.x:Forge 「安装基础环境」installer — 透传给 EnvListVM 让 Forge env 跳过
        // BaseEnvProfilePickerDialog + BaseEnvProgressDialog,inline panel 显示进度。可空让
        // 测试 ctor 不传(EnvListVM fallback 自己 new 一份)。
        ForgeBaseEnvInstaller? forgeBaseEnvInstaller = null)
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
        _githubVersionService = githubVersionService;
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
        // v1.0.0.x #577:本地常用节点批量 installer — 传给 EnvListVM InstallLocalNodesCommand。
        _localNodeBulkInstaller = localNodeBulkInstaller;
        // v1.0.0.x #589:env → localnodes sync service — ShowSettings 里传给 SettingsViewModel。
        _localNodeSyncService = localNodeSyncService;
        _commonNodeInstaller = commonNodeInstaller;
        // v1.0.0.x:Forge BED installer — 透传给 EnvListVM(见 _forgeBaseEnvInstaller 字段注释)。
        _forgeBaseEnvInstaller = forgeBaseEnvInstaller;
        // v0.6.11+ SDD D1:AppLogger — RestartEnvAsync 的 env-not-found / EnvListVM-未构造
        // 诊断日志。nullable ctor(测试 ctor 不传走 _logger?.Warn 安全路径);生产 DI 在
        // App.xaml.cs 注入(已有 var logger = new AppLogger(projectRoot);)。
        _logger = logger;
        // v0.6.14 T6: 退出清理 service —— MainWindow.OnClosing 拿它停 env。
        _envExitCleanup = envExitCleanup;
        // v0.6.14 R1:EnvironmentRepository —— GetRunningEnvCount 走 COUNT(*) 而不是
        // ListAll().Where().Count() 的全表扫。可空 ctor 让旧测试不传也 compile。
        _envRepo = envRepo;
        // v0.6.15: rate limit 单例 透传给 CatalogViewModel(ShowCatalog 内用)。
        _rateLimitState = rateLimitState;
        // v0.6.19 T10: 共享 HttpClient + WorkflowSymlinker — ShowWorkflows + env-start
        // 同步 workflow junction 都用这俩。
        _http = http;
        _httpBuilder = httpBuilder;
        _workflowSymlinker = workflowSymlinker;
        // v0.6.20 T9: ModelSymlinker — 透传给 EnvironmentListViewModel env-start 同步 model junction。
        _modelSymlinker = modelSymlinker;
        // v1.0.0 T11: 通用 template source updater — 由 ShowTemplateManagement 透传给
        // TemplateManagementViewModel(每张模板卡 "更新源码" 按钮)。
        _templateSourceUpdater = templateSourceUpdater;

        ShowDashboardCommand = new RelayCommand(_ => ShowDashboard());
        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
        // v0.6.15:本地节点页命令。ShowLocalNodes 懒构造 LocalNodeListViewModel。
        ShowLocalNodesCommand = new RelayCommand(_ => ShowLocalNodes());
        // v0.6.19 T10:工作流市场命令。ShowWorkflows 懒构造 WorkflowMarketplaceViewModel。
        ShowWorkflowsCommand = new RelayCommand(_ => ShowWorkflows());
        // v0.6.20 T9:模型市场命令。ShowModels 懒构造 ModelMarketplaceViewModel + 后台 LoadAsync。
        ShowModelsCommand = new RelayCommand(_ => ShowModels());
        // v1.0.0 T8:模板管理命令。ShowTemplateManagement 懒构造 TemplateManagementViewModel。
        ShowTemplateManagementCommand = new RelayCommand(_ => ShowTemplateManagement());
        // v1.0.0 T3:本地模型命令。ShowLocalModels 懒构造 LocalModelsViewModel + 触发 Initialize。
        ShowLocalModelsCommand = new RelayCommand(_ => ShowLocalModels());
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
        // v1.0.0 T11: removed UpdateTemplateCommand init — 工具菜单 → 模板更新 删除,
        // 改到 TemplateManagementView 每张模板卡 "更新源码" 按钮。
        ShowAboutCommand = new RelayCommand(_ =>
        {
            var owner = Application.Current?.MainWindow;
            if (owner is null) return;
            AboutDialog.Show(owner, _projectRoot);
        });
        ShowCoursesCommand = new RelayCommand(_ => ShowCourses());
        ShowComfyUIGroupCommand = new RelayCommand(_ => ShowComfyUIGroup());
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
            // v0.6.14 picker redesign:EnvListVM.OpenInstallNodePicker 弹
            // CatalogEntryPickerDialog 需要 catalogRepo(查 catalog 全表) +
            // nodeRepo(按 env 拉 scanned_nodes)+ versionRepo(查 node_versions per-row
            // version dropdown)。跟 CatalogViewModel 共用同一份 CatalogCacheStore /
            // 同一 SqliteConnectionFactory → picker 看到的 catalog 跟 Catalog tab 同步,
            // scanned_nodes 跟 NodeOperations 读同一份 db。
            var catalogRepo = new CatalogRepository(_catalogCacheStore);
            var nodeRepo = new NodeRepository(_dbFactory);
            var versionRepo = new NodeVersionRepository(_catalogCacheStore);
            _environmentsViewModel = new EnvironmentListViewModel(
                envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader,
                _envDeleter, _nodeOps, _projectRoot, _requirementsInstaller,
                _baseEnvUninstaller, _requirementsUninstaller,
                _browserLauncher, ErrorBanner, _comfyUiManagerInstaller,
                logger: _logger,                          // v0.6.11+ SDD D1
                catalogRepo: catalogRepo,                 // v0.6.14 picker
                nodeRepo: nodeRepo,                       // v0.6.14 picker
                versionRepo: versionRepo,                 // v0.6.14 T4 per-row version dropdown
                workflowSymlinker: _workflowSymlinker,   // v0.6.19 T10: env-start 后异步 sync workflows
                modelSymlinker: _modelSymlinker,     // v0.6.20 T9: env-start 后异步 sync models
                localNodeBulkInstaller: _localNodeBulkInstaller,  // v1.0.0.x #577
                forgeBaseEnvInstaller: _forgeBaseEnvInstaller);    // v1.0.0.x:Forge BED 跳过 PickerDialog
            // v0.6.22.x:removed templateUpdater arg — 模板更新改到 MainViewModel 上
            // v0.6.11+ SDD D1:wire MainViewModel 反向引用,让 EnvListVM.OpenInstallNodePicker
            // 能拿 _mvm.RestartEnvAsync 当 onInstallSuccess 回调 — 节点装成功时 fire-and-forget
            // 触发 env 重启。T2 加 wiring(T3 才会让 RestartEnvAsync 真正实现重启)。
            _environmentsViewModel.SetMainViewModel(this);
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
            // v0.6.15: 传 nodeRepo 让 CatalogViewModel 在 Search() 后 populate
            // 每条 CatalogEntry.IsInLocalNodeDb → XAML "下载"按钮 disabled + "已下载" badge。
            var catalogNodeRepo = new NodeRepository(_dbFactory);
            _catalogViewModel = new CatalogViewModel(
                catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot,
                rateLimitState: _rateLimitState, nodeRepo: catalogNodeRepo,
                versionService: _githubVersionService);
            _catalogView = new CatalogView { DataContext = _catalogViewModel };
        }
        CurrentView = _catalogView;
    }

    // v0.6.15:本地节点页 — 跟 ShowCatalog 同款懒构造模式。复用 _dbFactory + _nodeOps +
    // _settings + ErrorBanner;envRepo/nodeRepo 每次 Show 时 new(无状态,无需缓存)。
    private void ShowLocalNodes()
    {
        CurrentSection = MainSection.LocalNodes;
        if (_localNodesViewModel is null)
        {
            var envRepo = new EnvironmentRepository(_dbFactory);
            var nodeRepo = new NodeRepository(_dbFactory);
            var localNodeSvc = new LocalNodeService(
                _settings, nodeRepo, envRepo, _nodeOps, logger: _logger);
            var installer = new LocalNodeCopyInstaller(
                envRepo, nodeRepo, _nodeOps, logger: _logger);
            _localNodesViewModel = new LocalNodeListViewModel(
                localNodeSvc, installer, envRepo, nodeRepo, _requirementsInstaller, ErrorBanner);
            _localNodesView = new LocalNodeListView { DataContext = _localNodesViewModel };
        }
        CurrentView = _localNodesView;
    }

    // v0.6.19 T10: 工作流市场页 — 侧栏 8th entry "工作流市场"。
    // 懒构造 WorkflowMarketplaceViewModel(注入共享 HttpClient + 3 个 IWorkflowSource
    // + WorkflowDownloader + WorkflowFilesystemScanner),首次进入触发后台 LoadAsync
    // 并行拉 3 个 source,失败仅 log 不抛。复用同一份 _logger 跟 Settings,让
    // Settings.WorkflowsDirectory 改动下次刷新立刻生效。
    private void ShowWorkflows()
    {
        CurrentSection = MainSection.Workflows;
        if (_workflowMarketplaceViewModel is null)
        {
            // App.xaml.cs 总是传非 null http;null → 测试或极端 wiring 漏接,
            // 抛 InvalidOperationException 让问题立刻显形,而不是 NRE 在后台跑。
            var http = _http
                ?? throw new InvalidOperationException(
                    "HttpClient not wired — App.xaml.cs 未在 MainViewModel ctor 传 HttpClient");
            // YAGNI:3 个 source 全开(默认 IsEnabled=true);Settings 后续如加 toggle 再 gate。
            // 每个 source 注入同一份 http + logger;baseUrl 走默认。
            var marketplace = new WorkflowMarketplaceService(
                new IWorkflowSource[]
                {
                    new CommunityJsonSource(http, logger: _logger),
                    new CivitAiSource(http, logger: _logger),
                    new OpenArtSource(http, logger: _logger),
                },
                logger: _logger,
                httpClient: http);   // v0.6.22 T3: share http with sources for JSON preview fetch
            var downloader = new WorkflowDownloader(http, logger: _logger);
            var scanner = new WorkflowFilesystemScanner(logger: _logger);
            _workflowMarketplaceViewModel = new WorkflowMarketplaceViewModel(
                _settings, marketplace, downloader, scanner, logger: _logger);
            _workflowMarketplaceView = WorkflowMarketplaceViewFactory is null
                ? new WorkflowMarketplaceView { DataContext = _workflowMarketplaceViewModel }
                : WorkflowMarketplaceViewFactory(_workflowMarketplaceViewModel) as WorkflowMarketplaceView;
            // fire-and-forget:首次进入立刻构造 + 后台拉 3 个 source;后续进入复用 VM。
            // LoadAsync 内部 try/catch cover 失败语义 + ErrorBanner 反馈。
            _ = _workflowMarketplaceViewModel.LoadAsync();
        }
        CurrentView = _workflowMarketplaceView;
    }

    // v0.6.20 T9: 模型市场页 — 侧栏 9th entry "模型市场"。
    // 懒构造 ModelMarketplaceViewModel(注入共享 HttpClient + ModelMarketplaceService
    // + ModelDownloader + ModelFilesystemScanner)。DI 注册的所有 service 都是 singleton,
    // 但 ViewModel 本身 lazy 首次构造(同 WorkflowMarketplaceViewModel 模式)— 切走再回来
    // 复用同一份 VM 保留 IsBusy / Models / SelectedVersions 状态。
    // CivitAI/HF sources 状态由 T4 aggregator 内部 IsEnabled filter(t4 spec 设计)——
    // 保持 v0.6.20 单一 source 真活的体验;若以后加 settings toggle,需把
    // CivitAI/HF 改为 IsEnabled 由 settings 控制(此处不动 spec)。
    private void ShowModels()
    {
        CurrentSection = MainSection.Models;
        if (_modelMarketplaceViewModel is null)
        {
            var http = _http
                ?? throw new InvalidOperationException(
                    "HttpClient not wired — App.xaml.cs 未在 MainViewModel ctor 传 HttpClient");
            // v0.6.22+:per-source builder — App.xaml.cs 注入 _httpBuilder 时 factory 拿每个 source 自己的
            // HttpClient(per-source proxy toggle 在此生效)。null 退回 lambda 包 _http — 老测试 / 未注入
            // 时仍用共享 singleton(单 source = 单 client,factory 内的 ResolveProxy 决策只控制"是否
            // 再创建带 proxy 的新 client",不重启时全部 source 共享同 proxy 设置,跟 v0.6.21 行为一致)。
            var builder = _httpBuilder ?? (_ => http);
            // v0.6.21: 通过 ModelSourceFactory 构造所有启用的源(基于 Settings 6 个新字段 +
            // per-source mirror 解析)。Factory 内部 skip disabled source → aggregator 永远只看 enabled。
            // v0.6.22+:builder 决定 per-source HttpClient + proxy 应用。
            var marketplace = new ModelMarketplaceService(
                ModelSourceFactory.CreateAll(_settings, builder, logger: _logger),
                logger: _logger);
            var downloader = new ModelDownloader(http, logger: _logger, civitaiToken: _settings.CivitAiApiToken);
            var scanner = new ModelFilesystemScanner(logger: _logger);
            // v0.6.22+:注入 SettingsRepository 让 model marketplace view 中的 proxy toggle
            // 勾选时立即 Save 到 .manager/settings.json(用户期待持久化)。HttpClient 仍用
            // 共享 singleton (60s timeout + 共享 User-Agent header)。
            _modelMarketplaceViewModel = new ModelMarketplaceViewModel(
                marketplace, downloader, scanner, _settings, logger: _logger,
                settingsRepo: _settingsRepo);
            _modelMarketplaceView = ModelMarketplaceViewFactory is null
                ? new ModelMarketplaceView { DataContext = _modelMarketplaceViewModel }
                : ModelMarketplaceViewFactory(_modelMarketplaceViewModel) as ModelMarketplaceView;
            // fire-and-forget 首次进入后台拉所有启用的 source;后续进入复用 VM。RefreshAsync 内部
            // try/catch cover 失败语义 + ConsoleLog 反馈。VM 端 await *不* 用
            // ConfigureAwait(false) — UI-bound ObservableCollection 需 UI SynchronizationContext。
            _ = _modelMarketplaceViewModel.RefreshAsync();
        }
        CurrentView = _modelMarketplaceView;
    }

    // v1.0.0 T8: 模板管理页 — 侧栏 9th entry "模板管理"。跟 ShowLocalNodes
    // 同款懒构造模式。首次进入 new VM(从 Settings.Templates 拷贝列表),
    // View 由 T9 factory 注入(默认 new TemplateManagementView);
    // 切走再回来复用同一份 VM 保留编辑状态/选中行/滚动位置。
    // editTemplateFactory = null → VM ctor 自建 EditTemplateDialogViewModel 替身;
    // updater 由 App.xaml.cs 注入 templateSourceUpdater(T11 wiring)。
    private void ShowTemplateManagement()
    {
        CurrentSection = MainSection.Templates;
        if (_templateManagementViewModel is null)
        {
            _templateManagementViewModel = new TemplateManagementViewModel(
                _settings,
                editTemplateFactory: null,
                updater: _templateSourceUpdater,
                logger: _logger);
            _templateManagementView = TemplateManagementViewFactory is null
                ? new TemplateManagementView { DataContext = _templateManagementViewModel }
                : TemplateManagementViewFactory(_templateManagementViewModel);
        }
        CurrentView = _templateManagementView;
    }

    // v1.0.0 T3: 本地模型页 — 侧栏新 entry "本地模型"。跟 ShowTemplateManagement
    // 同款懒构造模式。首次进入 new VM(注入 Settings + scanner + logger),
    // View 由 T3 factory 注入(默认 new LocalModelsView);
    // 切走再回来复用同一份 VM 保留 kind chip 选中 / sort / 滚动位置。
    // Initialize() 触发后台 ReloadAsync(scanner 扫 DefaultModelsDirectory),
    // 失败由 VM 内部 try/catch cover 并设 EmptyMessage。MainViewModel 没持
    // _modelScanner 字段(跟 ShowModels 在方法内 new ModelFilesystemScanner 一致),
    // 单例化无收益 — 每次扫都是一次性 IO。
    internal Func<ModelFilesystemScanner>? LocalModelsScannerFactoryOverride { get; set; }   // v1.0.0:ShowLocalModels 不再每次重 reload → 测试要用 counting scanner 验证"无自动 reload"

    // v1.0.0.x: 用户覆盖本地路径 repo factory — App.xaml.cs 注入 SqliteConnectionFactory 包装的
    // LocalModelOverridesRepository。nullable 兼容老测试 ctor 路径(直接传 null 不注入)。
    internal Func<LocalModelOverridesRepository>? _localModelOverridesFactory;
    internal void SetLocalModelOverridesFactory(Func<LocalModelOverridesRepository> factory)
        => _localModelOverridesFactory = factory;

    // v1.0.0.x: CivitAI 详情缓存 repo factory — 同样模式。
    internal Func<CivitaiCardCacheRepository>? _civitaiCacheRepoFactory;
    internal void SetCivitaiCacheRepoFactory(Func<CivitaiCardCacheRepository> factory)
        => _civitaiCacheRepoFactory = factory;

    // v1.0.0.x: 本地模型 scan 结果 per-file cache repo factory — LocalModelsViewModel 读 DB
    // 出卡走这条线,不再每次 view 打开都 full scan。
    internal Func<LocalModelFilesRepository>? _localModelFilesRepoFactory;
    internal void SetLocalModelFilesRepoFactory(Func<LocalModelFilesRepository> factory)
        => _localModelFilesRepoFactory = factory;

    private void ShowLocalModels()
    {
        CurrentSection = MainSection.LocalModels;
        // v1.0.0.x:用户反馈 "本地模型默认情况刷新操作不自动启动,只有手动启动才去进行刷新操作。
        // 刷新也是后台执行,不要让前台冻住"。
        // 修复:VM ctor 设 EmptyMessage placeholder 提醒点「🔄 刷新」;ShowLocalModels **不**
        // fire-and-forget 触发 ReloadAsync。后续切回(VM 已 cache)也直接显示缓存数据,零 IO。
        // 用户点 toolbar 「🔄 刷新」按钮 → _reloadCommand → ReloadAsync → Task.Run 后台 scanner,
        // toolbar 显示 "刷新中…" 细指示,前台 Grid 仍可滚动/点击(loading overlay 只在
        // IsBusy && 已有数据为空时才挡,本次首屏已有数据是空 → 不会有 loading 圈)。
        bool isFirstVisit = _localModelsViewModel is null;
        if (isFirstVisit)
        {
            // v1.0.0 T11:构造 CivitAiLookupService 透传给 LocalModelsViewModel。
            // 用 _httpBuilder (App.xaml.cs 注入) 复用 per-source proxy + ApiToken +
            // model-source 决策 — 跟 ModelSourceFactory.CreateCivitAi 同模式构造 HttpClient,
            // 但不 new CivitAiModelSource(那个是 marketplace aggregator 用的,本任务要的是
            // 独立 lookup service)。Lookup service 注入失败(无 builder / CivitAI disabled
            // 等)— 传 null,VM 端 _lookup is null → button 隐藏(canExecute false)。
            // v1.0.0 T13-7:TryCreateCivitAiLookupService 同时构造 + cache hash cache + orchestrator
            // 到 instance 字段(_civitaiHashCache / _civitaiMatcherOrchestrator),这里一起透传。
            // 若 lookup service 注入失败(_httpBuilder is null),cache + orchestrator 也保持 null。
            var lookupService = TryCreateCivitAiLookupService();
            var scanner = LocalModelsScannerFactoryOverride is not null
                ? LocalModelsScannerFactoryOverride()
                : new ModelFilesystemScanner(_logger);
            _localModelsViewModel = new LocalModelsViewModel(
                _settings,
                scanner,
                _logger,
                lookupService,
                _civitaiHashCache,
                _civitaiMatcherOrchestrator,
                // v1.0.0.x: 用户覆盖本地路径 repo — 持久化到 SQLite local_model_overrides。
                // 测试 ctor 不传 factory → null → 「改路径」命令 graceful disable。
                _localModelOverridesFactory?.Invoke(),
                // v1.0.0.x: CivitAI 详情缓存 repo — 持久化到 SQLite civitai_card_cache。
                // null → 「🔎 CivitAI 查询」走内存 only,关窗即丢(测试路径)。
                _civitaiCacheRepoFactory?.Invoke(),
                // v1.0.0.x: scan 结果 per-file cache repo — view 打开立即读 DB 出卡,
                // 不再每次切 sidebar 都重跑 scanner。
                _localModelFilesRepoFactory?.Invoke());
            _localModelsView = LocalModelsViewFactory is null
                ? new LocalModelsView { DataContext = _localModelsViewModel }
                : LocalModelsViewFactory(_localModelsViewModel);
        }
        // v1.0.0.x:删 fire-and-forget `_ = _localModelsViewModel.ReloadAsync()` — 默认不刷。
        // 用户手动点 toolbar 「🔄 刷新」按钮 → _reloadCommand → ReloadAsync → 后台 Task.Run。
        // v1.0.0.x: 首次构造 VM 后,顺手 LoadFromDb — DB 有数据 → 立即填充卡片(无 loading 圈);
        // DB 空 → 维持 placeholder,等用户手动刷新。后续切回 sidebar(isFirstVisit=false)走
        // 已 cache 的 _localModelsViewModel,数据仍在(不再触发任何 IO)。
        _localModelsViewModel.LoadFromDb();
        CurrentView = _localModelsView;
    }

    /// <summary>v1.0.0 T11:为本地模型 sidebar 构造 CivitAiLookupService。
    /// 复用 _httpBuilder(per-source proxy 决策) + _settings.CivitAiApiToken,跟
    /// ModelSourceFactory.CreateCivitAi 走同一 HttpProxy 决策路径。
    /// 返回 null 当 CivitAI 在 Settings 中 disabled(用户主动关)或 _httpBuilder 未注入
    /// (老测试 ctor 兼容路径)— 此时 VM 端 _lookup is null → button canExecute false。
    /// 不缓存 service:每次 ShowLocalModels 都 new,跟 scanner 同款"一次性 IO"模式 ——
    /// HttpClient 复用 _httpBuilder 内部的 HttpClientHandler 实例(per-source proxy 应用
    /// 在 ctor 期一次性配置,运行期 token 改动需要重启,跟 model marketplace 行为一致)。
    /// v1.0.0 T13-7:同时构造 + 缓存 hash cache + matcher orchestrator,透给 LocalModelsViewModel
    /// 让 ReloadAsync 走 ScanContext(hash + match + cover download 路径)。两个字段懒初始化一次
    /// (ShowLocalModels 单例缓存),后续 reuse 同一份 cache(避免每次重开 SQLite)。</summary>
    private CivitAiLookupService? TryCreateCivitAiLookupService()
    {
        if (!_settings.ModelSourceCivitAiEnabled) return null;
        if (_httpBuilder is null) return null;

        var proxy = ModelSourceProxyDecision.Resolve(
            _settings.HttpProxyMode,
            _settings.ModelSourceCivitAiProxyMode,
            _settings);
        var http = _httpBuilder(proxy);

        // v1.0.0 T13-5:Build base service first(matchers depend on it),then wire the
        // 4 IModelMatcher strategies into a CivitaiMatcherOrchestrator via the 9-arg ctor.
        // All 4 matchers share the same HttpClient (token/proxy already applied).
        var baseService = new CivitAiLookupService(
            http,
            ModelSourceFactory.CivitAiOfficial,
            _settings.CivitAiApiToken,
            _logger,
            proxy);

        // v1.0.0 T13-7:Build hash cache at %APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite。
        // 跨启动复用 — 第二次启动时 scanner 直接 cache hit 跳过 SHA256 compute(5s/model → 0s)。
        var hashCachePath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "ComfyUI.Manager", "civitai-hash-cache.sqlite");
        var hashCache = new CivitaiHashCache(hashCachePath, _logger);

        var hashMatcher = new CivitaiHashMatcher(baseService, _logger);
        var metadataMatcher = new SafetensorsMetadataMatcher(baseService, _logger);
        var companionMatcher = new CompanionJsonMatcher(baseService, _logger);
        var filenameMatcher = new FilenameMatcher(baseService, _logger);

        // Store in fields for reuse across ShowLocalModels calls(scanner 复用同一 cache)。
        _civitaiHashCache = hashCache;
        _civitaiMatcherOrchestrator = new CivitaiMatcherOrchestrator(
            hashMatcher, metadataMatcher, companionMatcher, filenameMatcher, _logger);

        return new CivitAiLookupService(
            http,
            ModelSourceFactory.CivitAiOfficial,
            _settings.CivitAiApiToken,
            _logger,
            proxy,
            hashMatcher,
            metadataMatcher,
            companionMatcher,
            filenameMatcher);
    }

    /// <summary>
    /// v0.6.21 T4: 强制刷新模型市场 — 用户在 Settings 改完 source 启用 / 镜像 URL / token 后
    /// 通过 [立即刷新模型市场] 按钮触发,丢弃缓存的 VM/View,跳到模型市场 tab + 重新构造
    /// VM 并触发后台 RefreshAsync。
    ///
    /// 不重用现有 _modelMarketplaceViewModel 实例(避免缓存的 ShowOnlyCivitai /
    /// ShowOnlyHuggingFace 状态粘性),改用 lazy-cache 模式:丢弃旧 VM 引用,下次 ShowModels
    /// 构造新的。
    /// </summary>
    public void RefreshModelMarketplace()
    {
        _modelMarketplaceViewModel = null;  // force re-construct on next ShowModels call
        _modelMarketplaceView = null;
        ShowModelsCommand.Execute(null);
        _ = _modelMarketplaceViewModel?.RefreshAsync();
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):Forge env 创建成功后,弹「去设置」提示框用户点选「去设置」时调用 —
    /// 切换到 Settings section + 等 SettingsView Loaded 后调
    /// <see cref="SettingsView.JumpToForgePaths"/> scroll/highlight Forge 模型目录 +
    /// 重写刚创建 env 的 extra_model_paths.yaml(用当前 Settings,可能含用户刚编辑的 LoRA/VAE 路径)。
    ///
    /// <para>
    /// 由 <see cref="EnvironmentListViewModel"/> 在用户选「去设置」时调。
    /// 关键时序:
    /// </para>
    /// <list type="number">
    /// <item>ShowSettings() 构造 SettingsView + 设 CurrentView → MainWindow ContentControl 切到 SettingsView</item>
    /// <item>SettingsView 加载完成后触发 Loaded event → JumpToForgePaths</item>
    /// <item>JumpToForgePaths 内部 scroll + 2s 高亮 + afterShown 回调</item>
    /// <item>afterShown 回调里调 ForgeExtraModelPathsYamlGenerator.EnsureWritten 用当前 Settings 重写 yaml</item>
    /// </list>
    ///
    /// <para>
    /// 重写 yaml 的语义:env-create step 7.5 用 DefaultModelsDirectory 派生写了初始 yaml;
    /// 此时 _settings 已反映用户在 TextBox 里的实时编辑(SettingsViewModel 6 个 ForgePaths setter
    /// 直接写 _settings.ForgePaths.*),所以 EnsureWritten 拿到的是「用户实际想要的最新值」。
    /// 如果用户没编辑就关掉 Settings → 重写出来跟 step 7.5 一样,无副作用(idempotent)。
    /// </para>
    /// </summary>
    /// <param name="env">刚创建成功的 Forge env,需要其 RootPath 重写 yaml。</param>
    internal void OpenSettingsAndJumpToForgePaths(Environment env)
    {
        if (env is null) return;
        // ShowSettings 切 CurrentSection + 构造 / 复用 SettingsView。
        ShowSettings();
        // CurrentView 在 ShowSettings 末尾被设 — 拿到刚构造(或复用)的 SettingsView 实例。
        if (CurrentView is SettingsView sv)
        {
            // 用 local handler + 自身引用 self-unsubscribe 模式 — 闭包捕获 env 参数,
            // 避免 field 持有 + 多重触发隐患。Loaded 触发一次立即 unsubscribe。
            RoutedEventHandler? handler = null;
            handler = (s, e) =>
            {
                sv.Loaded -= handler;
                sv.JumpToForgePaths(afterShown: () =>
                {
                    try
                    {
                        // 闭包捕获本方法的 env 参数 — 每次调用独立,无 field 共享隐患。
                        ForgeExtraModelPathsYamlGenerator.EnsureWritten(env.RootPath, _settings);
                    }
                    catch (Exception ex)
                    {
                        // yaml 重写失败不影响 Settings UI;启动时 ProcessLauncher.StartEnvAsync
                        // EnsureWritten 会再写一次,这里 fail-soft。
                        System.Diagnostics.Debug.WriteLine(
                            $"[MainViewModel] ForgePostCreate yaml 重写失败(env 启动时 ProcessLauncher 重试): {ex.Message}");
                    }
                });
            };
            sv.Loaded += handler;
        }
    }

    private void ShowSettings()
    {
        CurrentSection = MainSection.Settings;
        // v0.6.9 T7:缓存 VM — Spotlight 切到 SettingsSection 时 ScrollToSection 必须命中
        // 当前 View 的 DataContext(同一份 VM),否则新 new 的 VM 没人订阅 SectionScrollRequested。
        if (_settingsViewModel is null)
        {
            _settingsViewModel = new SettingsViewModel(
                _settingsRepo, _gitProxy, new PythonInterpreterValidator(), _settings, _themeService,
                // v1.0.0.x:用户改 Settings.EnvsDir 后,scan 新目录 auto-import marker-based envs。
                // EnvsDir 是相对路径,跟 EnvCreatorService 一致锚到 _projectRoot。
                onEnvsDirSaved: async envsDirRel =>
                {
                    var envsDirAbs = string.IsNullOrWhiteSpace(envsDirRel)
                        ? ""
                    : Path.Combine(_projectRoot, envsDirRel);
                    if (_envRepo is null) return;
                    _ = await new EnvDirectoryScanner(_envRepo).ScanAsync(envsDirAbs);
                },
                // v1.0.0.x #589:env → localnodes sync — 共享注入的 sync service + env repo。
                envRepo: _envRepo,
                syncService: _localNodeSyncService,
                // v1.0.0.x:SettingsView「下载到本地节点目录」按钮依赖 — 共享 App 注入的实例。
                commonNodeInstaller: _commonNodeInstaller);
            CurrentView = SettingsViewFactory is null
                ? new SettingsView { DataContext = _settingsViewModel }
                : SettingsViewFactory(_settingsViewModel) as SettingsView;
        }
        else
        {
            CurrentView = SettingsViewFactory is null
                ? new SettingsView { DataContext = _settingsViewModel }
                : SettingsViewFactory(_settingsViewModel) as SettingsView;
        }
    }

    private void OpenBulkUpdate()
    {
        CurrentSection = MainSection.BulkUpdate;
        if (_bulkUpdateViewModel is null)
        {
            // v0.6.18:inline 模式(替代原 BulkUpdateDialog 弹窗)。VM 在 lazy 路径上构造一次,
            // 后续进入复用同一份,保留 IsBusy + Rows + Summary。EnvRows 每次进入刷新一次
            // —— 用户可能新建 / 删除 env,缓存的 list 不会自动跟。
            // v0.6.18.1:VM 现在也拉每个 env 的 scanned_nodes 填 AvailableNodes,
            // 所以 ctor 需要 NodeRepository;两次进入(Lazy / Reuse)都新建一份 NodeRepository,
            // 因为它本身只是 thin wrapper over SqliteConnectionFactory,无状态。
            var envRepo = new EnvironmentRepository(_dbFactory);
            var nodeRepo = new NodeRepository(_dbFactory);
            _bulkUpdateViewModel = new BulkUpdateViewModel(_orchestrator, nodeRepo);
            var envRows = envRepo.ListAll()
                .Select(env => new EnvRow(env.Id, env.Name, env.Status ?? "stopped"))
                .ToList();
            _bulkUpdateViewModel.LoadEnvs(envRows, nodeRepo);
            _bulkUpdateView = BulkUpdateViewFactory is null
                ? new BulkUpdateView { DataContext = _bulkUpdateViewModel }
                : BulkUpdateViewFactory(_bulkUpdateViewModel) as BulkUpdateView;
        }
        else
        {
            // 复用 VM 时刷新 env 列表 —— 用户在 env 页新建 / 删除后回到这里应该看到最新。
            // AvailableNodes 也会自动重算(env 选中状态变化触发)。
            var envRepo = new EnvironmentRepository(_dbFactory);
            var nodeRepo = new NodeRepository(_dbFactory);
            var envRows = envRepo.ListAll()
                .Select(env => new EnvRow(env.Id, env.Name, env.Status ?? "stopped"))
                .ToList();
            _bulkUpdateViewModel.LoadEnvs(envRows, nodeRepo);
        }
        CurrentView = _bulkUpdateView;
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

    // v1.0.0 T11: removed UpdateTemplateAsync — 工具菜单 → 模板更新 删除,
    // TemplateManagementViewModel.UpdateSourceCommand 直接调 TemplateSourceUpdater。

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
            File.WriteAllText(path, "# ComfyUI Models 路径配置\n# 编辑 base_directory 指向全局默认 Models 目录(配合 Settings.DefaultModelsDirectory)\n", System.Text.Encoding.UTF8);
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

    private void ShowCourses()
    {
        if (ShowCoursesOverride is not null) { ShowCoursesOverride(); return; }
        var owner = Application.Current?.MainWindow;
        if (owner is null) return;
        ComfyUICoursesWindow.Show(owner);  // v1.0.0:顶级 dropdown "ComfyUI 课程" 独立窗口
    }

    private void ShowComfyUIGroup()
    {
        if (ShowComfyUIGroupOverride is not null) { ShowComfyUIGroupOverride(); return; }
        var owner = Application.Current?.MainWindow;
        if (owner is null) return;
        ComfyUIGroupQrWindow.Show(owner, _projectRoot);  // v1.0.0:顶级 dropdown "ComfyUI 群组" 独立窗口
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

    /// <summary>测试用:获取当前缓存的 LocalModels VM(若有)— ShowLocalModels 懒构造后赋值;
    /// 验证只有首次构造时 fire ReloadAsync,后续 click 不重 reload。</summary>
    internal LocalModelsViewModel? CurrentLocalModelsViewModel => _localModelsViewModel;

    /// <summary>测试用:获取当前缓存的 Catalog VM(若有)。</summary>
    internal CatalogViewModel? CurrentCatalogViewModel => _catalogViewModel;

    /// <summary>测试用:获取当前缓存的 Settings VM(若有)。</summary>
    internal SettingsViewModel? CurrentSettingsViewModel => _settingsViewModel;

    /// <summary>测试用:获取当前缓存的 BulkUpdate VM(若有),验证 OpenBulkUpdate 复用同一实例。</summary>
    internal BulkUpdateViewModel? CurrentBulkUpdateViewModel => _bulkUpdateViewModel;

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

    // ============ v0.6.11+ SDD B T3:主窗口关闭 guard ============

    /// <summary>
    /// 用户在"未保存的设置"三按钮框里的选择。
    /// 映射 MessageBoxButton.YesNoCancel → 用户语义选择。
    /// </summary>
    internal enum UnsavedChoice { Save, Discard, Cancel }

    /// <summary>
    /// 测试 seam:STA 测试环境外不能弹真 MessageBox。生产路径为 null,走 <see cref="PromptUnsaved"/>。
    /// </summary>
    internal Func<int, UnsavedChoice>? UnsavedPromptOverride { get; set; }

    /// <summary>
    /// v0.6.14 T6: 暴露 <see cref="EnvExitCleanupService"/> 给 <c>MainWindow.OnClosing</c>
    /// (在 ConfirmDiscardUnsavedSettings 之前调)。测试 ctor 不传时为 null,
    /// MainWindow.OnClosing 走 if (svc is null) 短路。
    /// </summary>
    public EnvExitCleanupService? GetExitCleanupService() => _envExitCleanup;

    /// <summary>
    /// v0.6.14 R1: 同步取 running env 数(给 OnClosing 的 confirm dialog)。
    /// 单条 <c>SELECT COUNT(*)</c> via <see cref="IEnvironmentRepository.CountByStatus"/>
    /// — 替代 v0.6.14 T6 的 <c>ListAll().Where().Count()</c> 全表扫。
    /// Cleanup service / repo 不存在时返 0(MainWindow 短路,不弹 confirm)。
    /// </summary>
    public int GetRunningEnvCount() => _envRepo?.CountByStatus("running") ?? 0;

    /// <summary>
    /// 检查当前缓存的 SettingsViewModel 是否有未保存改动,有则弹三按钮框。
    /// 返回 true = 可以继续关闭,false = 用户选了"取消"。
    /// <para>
    /// G1 约束:Save / Discard 走 vm 的 Command,不直接动 <c>_repo.Save</c> —
    /// 这样 dirty 状态清理由 SettingsViewModel 自己负责,MainViewModel 不持有
    /// 任何 settings 持久化细节。
    /// </para>
    /// </summary>
    internal bool ConfirmDiscardUnsavedSettings()
    {
        var vm = CurrentSettingsViewModel;
        if (vm is null || !vm.HasUnsavedChanges) return true;

        var choice = (UnsavedPromptOverride ?? PromptUnsaved)(vm.UnsavedCount);
        switch (choice)
        {
            case UnsavedChoice.Save:
                vm.SaveCommand.Execute(null);
                return true;
            case UnsavedChoice.Discard:
                vm.DiscardCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private static UnsavedChoice PromptUnsaved(int count)
    {
        var r = MessageBox.Show(
            $"您有 {count} 项设置尚未保存。\n\n是 = 保存并退出\n否 = 丢弃并退出\n取消 = 返回",
            "未保存的设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return r switch
        {
            MessageBoxResult.Yes => UnsavedChoice.Save,
            MessageBoxResult.No  => UnsavedChoice.Discard,
            _                    => UnsavedChoice.Cancel,
        };
    }
}
