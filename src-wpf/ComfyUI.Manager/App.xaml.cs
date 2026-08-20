using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager;

public partial class App : Application
{
    private MainViewModel? _mainVm;
    private ProcessLauncher? _launcher;
    // v0.6.5.22: 卸载 service(BED reset + requirements pip uninstall)。
    // 在 OnStartup 构造后传给 MainViewModel,跟 BaseEnvInstaller / RequirementsInstaller
    // 同一份 _logger;测试可注入自己 derived 类的实例。
    private BaseEnvUninstaller? _baseEnvUninstaller;
    private RequirementsUninstaller? _requirementsUninstaller;
    // v0.6.14 T6: 退出清理 service —— 用户点 X 关闭主窗口时,graceful 停掉所有
    // running env 并把 SQLite status 翻成 stopped。构造后传给 MainViewModel,
    // MainWindow.OnClosing 在 settings 检查之前调它。
    private EnvExitCleanupService? _envExitCleanup;
    // v0.6.8: Splash 画面引用 — OnStartup 立即 Show,MainWindow 加载好后
    // NotifyMainWindowReady 触发 fade;FadeCompleted 由 Window self-close raise。
    private SplashWindow? _splash;
    private SplashViewModel? _splashVm;
    // v0.6.15: 进程级 rate limit 单例 —— 所有 stage 的 IsBlocked/MarkBlocked
    // 共享。生命周期 = 进程生命周期;无需 dispose, GC 兜底。传给 MainViewModel
    // → CatalogViewModel。RateLimitBannerViewModel 共享此 state 显示历史
    // banner 状态。
    private IRateLimitState? _rateLimitState;

    /// <summary>
    /// v0.6.5.21:挂在静态以便 MainWindow.OnClosing 写回(G7)— 无主项目别的地方
    /// 需要直接读 svc,从 ctor 注入到 MainViewModel 已足够。
    /// </summary>
    public static UiPreferencesService? UiPreferencesService { get; private set; }

    /// <summary>
    /// v0.6.9 T9:MainWindow 订阅 ThemeChanging 触发 cross-fade overlay — 暴露
    /// instance property(G7)让 MainWindow.OnLoaded 拿到,而不是再走 DI 链路。
    /// OnStartup 中构造后即赋值,MainWindow OnLoaded 时一定可用。
    /// </summary>
    public IThemeService? ThemeService { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // v0.6.8: Splash 立即显示 — 必须先于所有服务初始化,保证用户 0ms 看到
        // (G1)。失败静默,不阻断程序启动(G6 错误处理)。
        try
        {
            _splashVm = new SplashViewModel(
                title: "ComfyUI 多环境管理系统",
                tagline: "智能管理 ComfyUI 环境、节点、依赖",
                version: AppVersionInfo.Current);
            _splash = new SplashWindow(_splashVm);
            _splash.Show();
            // v0.6.11+ dashboard/splash polish:Stage 1 Init 完成(splash 已上屏)。
            // 此时 _logger 还没构造(logger 需要 projectRoot,在下面才算出),
            // 所以 4 个 stage report 都不写日志 — splash 进度纯 UI 反馈。
            _splashVm.ReportStageProgress(Stage.Init, 100);
        }
        catch (Exception ex)
        {
            _splash = null;
            _splashVm = null;
            // Splash 创建失败极端罕见(XAML 错 / VM ctor 错),此时 _logger
            // 还没建,直接 Debug 输出 — 失败后无 splash 直接走主流程。
            System.Diagnostics.Debug.WriteLine($"splash failed: {ex.Message}");
        }

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');

        // M5.2: WPF 完全独立 —— 不启动任何 Python control service。
        // 直连 SQLite + 直 Process.Start ComfyUI + 直调 git。
        // venv 检查只在 EnvCreatorService 创建 env 时做(那时 python.exe
        // 路径用户已指定),启动时拦着太烦。

        // v0.6.16: 所有持久化数据从 %APPDATA%/ComfyUI-Manager/ 搬到 <projectRoot>/.manager/。
        // LocalDataPaths 构造时自动 .CreateDirectory(.manager) — 即使迁移失败,
        // 新路径也能保证存在。
        var localPaths = new LocalDataPaths(projectRoot);

        // v0.6.16 hotfix: 迁移必须在 SettingsRepository.Load() **之前** 跑 ——
        // 否则 Load() 读到空 .manager/settings.json → 回退到 defaults → in-memory settings
        // 是 defaults,后续第一次 Save 覆盖 .manager/settings.json → 即使迁移后来真把旧文件
        // 复制过来了,也被默认 settings 盖回去。Bug 现象:用户原本设的 theme / GitHub token /
        // python 路径等全丢。
        new LocalDataMigrationService(localPaths, logger: null).RunIfNeeded();

        // v0.6.7.1 + v0.6.12: 在 logger / launcher 构造前先 Load settings —
        // logger 读 LogDirectory(决定 Logs 父目录),launcher 读 startupTimeoutSeconds / locale / models。
        // SettingsDefaults.Apply 还在 launcher 构造之后,但 Apply 只动 path 类字段,
        // 不会改 ComfyUiStartupTimeoutSeconds,所以顺序安全。
        // v0.6.16: 走 LocalDataPaths 注入,settings.json 现在落 <projectRoot>/.manager/。
        var settingsRepo = new SettingsRepository(localPaths);
        var settings = settingsRepo.Load();

        // v0.6.16: db path 也走 LocalDataPaths 注入 —— state.db 落 <projectRoot>/.manager/。
        var dbFactory = new SqliteConnectionFactory(localPaths);
        var envRepo = new EnvironmentRepository(dbFactory);

        // v0.6.11+ dashboard/splash polish:Stage 2 LoadDatabase 完成。
        _splashVm?.ReportStageProgress(Stage.LoadDatabase, 100);

        // v0.6.12:Settings.LogDirectory 注入 — 计算 Logs 父目录(parent of Logs/)。
        // 非空 + 绝对 → 直接用;非空 + 相对 → 相对 projectRoot 解析;空 → 回退 projectRoot。
        var logsDir = !string.IsNullOrWhiteSpace(settings.LogDirectory)
            ? (Path.IsPathRooted(settings.LogDirectory)
                ? settings.LogDirectory
                : Path.Combine(projectRoot, settings.LogDirectory))
            : projectRoot;

        // v0.6.5.13: 集中日志 — 所有 subsystem 写到 projectRoot/Logs/YYYY-MM-DD.log
        // v0.6.12: AppLogger 接受 logsDir(parent of Logs/)— Settings.LogDirectory 决定。
        var logger = new AppLogger(projectRoot, logsDir);
        // 启动时清理 >30 天的日志(用户原话:30 天保留)
        int cleaned = AppLogger.CleanupOlderThan(logsDir, 30);
        logger.Info("app-startup", $"App 启动 cleaned={cleaned} logsDir={logsDir}");

        // v0.6.5.8: 启动 reconciliation — 把上次未装完的 "installing" 行翻成
        // "failed" + "上次未完成"。必须先于 MainViewModel.Load(),否则 UI 看到
        // ⏳ 装中 几秒后变 ❌ 闪烁。
        BaseEnvInstaller.ReconcileStaleOnStartup(envRepo);

        // v0.6.14 T7: 启动 reconcile stale-running envs — 处理上次 crash / hard-kill
        // 留下的脏状态(env.Status="running" 但进程已死)。只 reconcile 不 auto-start
        // (用户原话"启动的时候节点不自动启动")。同 BED reconcile: 先于 MainViewModel
        // 构造,让 MVM 第一次 Load() 看到 clean 状态。
        new EnvStartupReconciler(envRepo, logger).ReconcileStaleRunning();

        // v0.6.15: 进程级 rate limit 单例 (无依赖,纯 in-memory lock dict)。
        var rateLimitState = new RateLimitState();
        _rateLimitState = rateLimitState;

        var nodeRepo = new NodeRepository(dbFactory);
        var processStateRepo = new ProcessStateRepository(dbFactory);

        // v0.6.9 T2:Apply stored theme BEFORE MainWindow.Show,避免先 Dark(默认)再切 Light 闪屏。
        // 必须早于 _mainVm 构造(MainViewModel 接收 IThemeService 引用),且 Application.Current.Resources
        // 此时已合并 Theme.xaml + Palette.Dark.xaml(slot 默认 Dark;Settings 存 "light" 时需 Apply)。
        var themeService = new ThemeService(Application.Current.Resources, logger);
        themeService.Apply(SettingsViewModel.ParseThemeMode(settings.ThemeMode));
        // v0.6.9 T9:暴露给 MainWindow 订阅 ThemeChanging。首次 Apply 时 resolved 跟 Current 都是 Dark
        // (ThemeService 内部 default),所以 ThemeChanging 不会被 fire — 避免启动期无意义 fade。
        ThemeService = themeService;

        // v0.6.11+ dashboard/splash polish:Stage 3 LoadTheme 完成。
        _splashVm?.ReportStageProgress(Stage.LoadTheme, 100);

        _launcher = new ProcessLauncher(
            projectRoot, dbFactory, envRepo, processStateRepo, logger,
            settings.ComfyUiStartupTimeoutSeconds,
            settings.ComfyUiLocale,
            settings.DefaultModelsDirectory,
            linker: null,
            logsDir: logsDir,  // v0.6.12: 末参 Settings.LogDirectory (Logs parent) or projectRoot fallback
            startupErrorDetector: new NodeStartupErrorDetector(),  // v0.6.15.7: 5s grace 后扫描 stdout/stderr 找加载失败的 custom node
            nodeRepo: nodeRepo);  // v0.6.15.7: 写 ScanMeta["load_error"] 让 env-detail 看到红 badge

        // v0.6.17.2: 启动时主动停掉所有 running + 进程活着的 env — 跟
        // EnvExitCleanupService(graceful 退出)对称。用户原话"环境管理之前应该
        // 中止运行环境,然后开启不会自动启动,需要手动启动才可以"。先于
        // MainViewModel.Load() 让 UI 看到 clean slate(否则会先显示 running
        // 几秒后才变 stopped 闪烁)。Launcher 已构造所以可调 StopEnvAsync。
        // 顺序:EnvStartupReconciler 先标 stale → 本服务再停活着的(分工不重叠)。
        new EnvStartupStopper(envRepo, _launcher, logger).StopRunningOnStartupAsync()
            .GetAwaiter().GetResult();

        // 首次启动:把 path 类字段默认填为相对子目录名 + 迁移旧的绝对路径。
        // 1) 空字段 → 默认子目录名(相对)
        // 2) 已经在 projectRoot 下的绝对路径 → 转相对(跨机器/跨盘符时
        //    settings.json 不需重新生成)
        // 3) 用户故意选的别处绝对路径 → 保留
        SettingsDefaults.Apply(settings, projectRoot);
        settingsRepo.Save(settings);

        // v0.6.5.9: 首次启动预创建本地节点目录,失败静默(用户运行期 DownloadAsync 还会再兜底 CreateDirectory)。
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, settings.LocalNodeDirectory));
        }
        catch
        {
            // 权限/盘满/路径非法 → 静默,运行时再 CreateDirectory 兜底
        }

        // M5.2-T6: bulk update 在 WPF 端直接跑 git pull,git exe 优先用
        // bin/git-portable/cmd/git.exe(portable),找不到则回落到 PATH。
        // settings.GitExe 优先,settings 是空则走默认。
        var gitExe = !string.IsNullOrWhiteSpace(settings.GitExe)
            ? settings.GitExe
            : ResolveGitExe(projectRoot);
        // 共享同一份 HttpProxyConfig,SettingsViewModel 改它会立即影响下一次 git 调用 / HTTP 拉取。
        var gitProxy = HttpProxyConfig.From(settings);
        var gitRunner = new GitRunner(gitExe, gitProxy);
        // v0.6.7.5: 节点安装前的 pip diff check — NodeInstallDiffService 跑 pip list JSON,
        // 由 NodeOperations.InstallAsync 在 clone 前调。先于 nodeOps 构造,因为 ctor 要拿。
        var diffService = new NodeInstallDiffService(
            (exe, args, timeout, ct) => RunProcessForDiffAsync(exe, args, timeout, ct),
            logger);
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings, diffService, logger: logger);
        // v0.6.15:LocalNodeService + LocalNodeCopyInstaller 由 MainViewModel.ShowLocalNodes()
        // 懒构造(避免 App 启动期同步拉 GitHub repo)。
        // v0.6.15.4: 网关代理走 HttpProxyConfig.Built HttpClient.BuildHttpClient test seam。
        var http = BuildHttpClient(gitProxy);
        // v0.6.22+:per-source HttpClient builder — 传给 MainViewModel → ModelSourceFactory,
        // 让每个 source 拿自己的 HttpClient(per-source proxy toggle 在此生效)。
        // 共享 singleton `http` 仍给 ModelDownloader / metadata / GitHub API 复用 — 单一 client 60s
        // timeout + 共享 User-Agent header 是这些共享场景的好处。Factory 内部 source 自己拿
        // client 时也用同一个 builder → 同样的 User-Agent/Accept 头,避免被 CivitAI/HF
        // Cloudflare 反爬当作 bot 拦截(用户 2026-08-20 报告:开启代理后搜索 "face" 返回 HTML
        // 而非 JSON,加 User-Agent + Accept 后 .NET HttpClient 表现跟 curl 一致)。
        // v0.6.13-B: GitHub API 要求 User-Agent header,否则 403 — 现已统一在 BuildHttpClient 注入。
        Func<HttpProxyConfig?, HttpClient> httpBuilder = BuildHttpClient;
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes, logger);
        var catalogCacheStore = new CatalogCacheStore();
        var catalogRepo = new CatalogRepository(catalogCacheStore);
        var githubVersionService = new GitHubVersionService(http);
        var nodeVersionRepo = new NodeVersionRepository(catalogCacheStore);
        // v0.6.13-B: GitHub metadata 抓取 service(2-round polling) +
        // v0.6.16: MetadataCache(24h TTL, <projectRoot>/.manager/catalog_metadata_cache.json)。
        // 复用共享 http + CatalogRefreshService 内部 settings.FetchCatalogMetadata 开关 gate。
        var metadataCache = new MetadataCache(localPaths);
        var metadataService = new GitHubCatalogMetadataService(http, metadataCache, settings, logger);
        // v0.6.14: HTTP conditional-request 缓存(ETag / Last-Modified per source URL)。
        // 跟 catalog_cache 同一个 DB 文件 —— 表由 CatalogCacheStore.Open() 建(T3)。
        var catalogHttpCacheStore = new CatalogHttpCacheStore(catalogCacheStore.DbPath, logger);
        var catalogRefreshService = new CatalogRefreshService(
            catalogFetcher, catalogRepo, settings, githubVersionService, nodeVersionRepo, logger,
            metadataService,                            // v0.6.13-B: 7th param
            httpCacheStore: catalogHttpCacheStore);     // v0.6.14: 8th param
        var bulkOrchestrator = new BulkUpdateOrchestrator(
            projectRoot, gitExe, envRepo, nodeRepo, gitProxy, logger);
        var baseEnvInstaller = new BaseEnvInstaller(envRepo, logger);
        // v0.6.5.x hotfix:Env 删除跑腿 service(stop running + 删目录 + 删 SQLite 行)。
        // 复用 envRepo 跟 _launcher,跟 EnvironmentListView 共一份。
        var envDeleter = new EnvDeleterService(envRepo, _launcher);
        // v0.6.5.12 + v0.6.11+: 装依赖 helper(过滤 torch 行 + 写 filtered + 跑 pip)。
        // 抽出 helper 给 RequirementsInstaller(ComfyUI 依赖)和 ComfyUIManagerInstaller
        // (ComfyUI-Manager 自己的依赖)两边复用,避免 30 行过滤逻辑复制。
        // v0.6.11++:注入 lazy mirror 解析器 → 每次 InstallAsync 调用时重新求值,
        // Settings 改值后下次 pip 调用立即生效(G3)。
        var reqFileInstaller = new RequirementsFileInstaller(
            resolveIndexUrl: () => PipMirrorResolver.ResolveIndexUrl(settings));
        // v0.6.11+ T2: ComfyUI Manager 装/卸 service(env-list toggle 按钮 + 装依赖末尾自动装)。
        // 复用 reqFileInstaller 跑 Manager 自己的 requirements.txt;git 走共享的 gitExe + GitRunner。
        var comfyUiManagerInstaller = new ComfyUIManagerInstaller(reqFileInstaller, gitExe, gitProxy, logger);
        // v0.6.22 T5:ComfyUI 模板更新 service — env-list 行"模板更新"按钮触发。
        // 复用共享 gitRunner + envRepo,跟其他 service 同生命周期。
        var comfyUiTemplateUpdater = new ComfyUITemplateUpdater(gitRunner, envRepo, logger);
        // v0.6.11++:常用节点自动装 service(env-create 末尾 + 装依赖末尾触发)。
        // 走注入的 git clone func(包 GitRunner.RunAsync)— 测试可换 fake func。
        // 共享 reqExe + GitRunner,先于 EnvCreatorService / RequirementsInstaller 构造。
        var commonNodeInstaller = new CommonNodeInstaller(
            settings,
            (id, args) => gitRunner.RunAsync(".", args).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result.ExitCode != 0)
                    return NodeOperationResult.Fail(
                        t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "git 异常"
                                    : $"git exit={t.Result.ExitCode}; stderr={t.Result.Stderr.Trim()}");
                return NodeOperationResult.Ok("cloned");
            }),
            logger);
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings, projectRoot,
            commonNodeInstaller: commonNodeInstaller);
        var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller, comfyUiManagerInstaller, commonNodeInstaller);
        // v0.6.5.22: 卸载 service(BED reset 跟 requirements pip uninstall)。
        // EnvListVM 行内"卸载基础环境" / "卸载依赖"按钮 + 互斥 mutex 用这两份。
        _baseEnvUninstaller = new BaseEnvUninstaller(logger);
        _requirementsUninstaller = new RequirementsUninstaller(logger);
        // v0.6.14 T6: 退出清理 service —— MainWindow.OnClosing 在 settings 检查之前
        // 调它(graceful 停 + status 翻 stopped);App.OnExit 仍然 force-kill 兜底。
        // 跟其他 service 共用同一份 _logger,ConfirmShutdown 默认弹 MessageBox。
        _envExitCleanup = new EnvExitCleanupService(envRepo, _launcher, logger);
        // v0.6.5.1: BaseEnvProfileLoader 运行时拉取真实 PyTorch stable 版本。
        // v0.6.16: cache 目录 = <projectRoot>/.manager (PyTorchVersionCache 直接在此存
        // pytorch_versions_cache.json);复用共享 http(15s 超时)。拉取失败静默回退。
        var profileLoader = new BaseEnvProfileLoader(projectRoot, localPaths.Directory, http);
        // v0.6.5.x: 系统状态 tab 数据收集器(进入 tab 时拉一次 OS/CPU/Mem/Disk/GPU/CUDA)
        var systemInfoCollector = new SystemInfoCollector(logger);
        // v0.6.5.21: UI 偏好持久化(<projectRoot>/config/ui-preferences.json)— Menu 的
        // Save/Load UI 偏好命令 + MainWindow Window 尺寸 / LastViewName 应用都靠它。
        var uiPreferencesService = new UiPreferencesService(projectRoot, logger);
        // 挂到静态属性 → MainWindow.OnClosing 写回时(G7)用;App 进程内单例 OK。
        UiPreferencesService = uiPreferencesService;
        // 启动加载:先 LoadFromFile 再 instance MainWindow — MainWindow.OnSourceInitialized
        // 根据 _startupPrefs 应用位置 / 尺寸(G6:ApplyStartupPreferences 必须在 Show() 之前)。
        var uiPrefs = uiPreferencesService.LoadFromFile(uiPreferencesService.DefaultPath);
        // v0.6.9 T5:Dashboard 数据聚合 service(接 T4,4 个并行 task:
        // envRepo.ListAll / nodeRepo.CountAllAsync / AppLogger 最近 5 行 / GitHub latest release)。
        // 复用共享 http(15s 超时)+ envRepo/nodeRepo/logger(跟其他 service 同生命周期)。
        // v0.6.11+ T3:再挂 GitHubReleaseService(releases 全 list + 24h cache,
        // cache 走 LocalDataPaths 落 <projectRoot>/.manager/release_cache.json)
        // + ChangelogParser(读 AppContext.BaseDirectory/CHANGELOG.md,
        // 解析不出内容时回退 HardcodedFallback)。两者失败都只降级不阻断 dashboard。
        var dashboardService = new DashboardService(
            envRepo, nodeRepo, logger, http,
            new GitHubReleaseService(http, localPaths, logger),
            new ChangelogParser());
        // v0.6.9 T7:全局搜索 service(跨 4 kind 索引:env / node / settings section / command)。
        // 复用 envRepo + nodeRepo;首次 OpenSpotlight 时 BuildAsync,后续键入仅走内存(G7)。
        var globalSearchService = new GlobalSearchService(envRepo, nodeRepo);

        // v0.6.19 T10: 工作流市场相关 service —— 复用共享 http(60s timeout,跟
        // catalog / dashboard 同一份);JunctionLinker 跟 envCreator 内部是独立实例
        // (WorkflowSymlinker 自己持有,不共用避免 lifetime 耦合)。
        // WorkflowFilesystemScanner:Settings.WorkflowsDirectory 扫描已下载 workflows。
        // WorkflowSymlinker:env-start 成功后 fire-and-forget 把每个 subfolder symlink
        // 到 <env.ComfyuiSource>/user/default/workflows/,失败 WARN 不抛。
        var workflowScanner = new WorkflowFilesystemScanner(logger: logger);
        var workflowSymlinker = new WorkflowSymlinker(
            settings, new JunctionLinker(), workflowScanner, logger: logger);

        // v0.6.20 T9: 模型市场 service —— 复用共享 http(60s timeout,跟 workflow / catalog
        // / dashboard 同一份 singleton)。JunctionLinker 跟 WorkflowSymlinker / envCreator
        // 都是独立实例(各自 lifetime,不耦合)。
        // ModelFilesystemScanner:扫描 Settings.DefaultModelsDirectory 找已下载 models,递归读
        // <ModelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/meta.json。
        // ModelSymlinker:env-start 成功后 fire-and-forget 把每个 version symlink 到
        // <env.ComfyuiSource>/models/<kind>/<slug>__<vid8>,失败 WARN 不抛。
        // CivitAiModelSource:full CivitAI Models API fetcher + pagination(nsfw=true 全部)。
        // HuggingFaceModelSource:v0.6.20 placeholder(stub),SearchAsync 永远返 empty,
        // 默认 IsEnabled=false → T4 aggregator 内部自动 skip。
        var modelScanner = new ModelFilesystemScanner(logger: logger);
        var modelSymlinker = new ModelSymlinker(
            settings, modelScanner, new JunctionLinker(), logger: logger);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, envDeleter, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore, githubVersionService,
            baseEnvInstaller,
            profileLoader, BuildPyTorchVersionDirectory(localPaths.Directory, http), localPaths.Directory, projectRoot,
            requirementsInstaller, systemInfoCollector, uiPreferencesService,
            _baseEnvUninstaller, _requirementsUninstaller,
            themeService, dashboardService, globalSearchService,
            // v0.6.10 T2:组件报告 + OpenBrowser 共享 Chrome 优先 fallback。
            new BrowserLauncher(),
            // v0.6.11+ T4:ComfyUI Manager toggle 安装器 — 传给 EnvListVM
            // ToggleComfyUiManagerCommand(显示 inline 状态面板)。
            comfyUiManagerInstaller,
            // v0.6.11+ SDD D1:AppLogger — 跟其他 service 共享同一份 logger,
            // RestartEnvAsync 在 env-not-found / EnvListVM-未构造时打 WARN。
            logger: logger,
            // v0.6.14 T6: 退出清理 service —— MainWindow.OnClosing 调它。
            envExitCleanup: _envExitCleanup,
            // v0.6.14 R1:EnvironmentRepository —— GetRunningEnvCount 走 COUNT(*)
            // 而不是全表 ListAll().Where().Count()。
            envRepo: envRepo,
            // v0.6.15: 进程级 rate limit 单例 —— MainViewModel 透传给
            // CatalogViewModel,触发入口 stage-skip + banner 状态共享。
            rateLimitState: rateLimitState,
            // v0.6.19 T10: 共享 HttpClient — ShowWorkflows 用它构造 3 个 IWorkflowSource
            // + WorkflowDownloader。同一份 60s timeout http,singleton 进程级。
            http: http,
            // v0.6.19 T10: WorkflowSymlinker — 传给 EnvironmentListViewModel 让
            // env-start 成功后 fire-and-forget sync 已下载 workflows 到 env。
            workflowSymlinker: workflowSymlinker,
            // v0.6.20 T9: ModelSymlinker — 传给 EnvironmentListViewModel 让
            // env-start 成功后 fire-and-forget sync 已下载 models 到 env。同 workflow hook 模式,
            // 各自独立 try/catch,失败互不干扰。
            modelSymlinker: modelSymlinker,
            // v0.6.22 T5: ComfyUI 模板更新 service — 传给 EnvironmentListViewModel
            // 让 UpdateTemplateCommand 触发 wipe + git clone。
            templateUpdater: comfyUiTemplateUpdater,
            // v0.6.22+:per-source HttpClient builder — 传给 ModelSourceFactory 让每个 source
            // 拿自己的 HttpClient(per-source proxy toggle 在此生效)。同 BuildHttpClient 静态方法
            // 引用 — 复用同样的 handler 配置 + 60s timeout + Proxy=null/UseProxy=false fallback。
            httpBuilder: httpBuilder);

        var main = new MainWindow { DataContext = _mainVm };
        main.ApplyStartupPreferences(uiPrefs);
        // v0.6.9.1 修复:Splash 先于 MainWindow Show,WPF 默认把第一个 Show 的窗口
        // 当作 MainWindow → Splash 3s 后 fade close → ShutdownMode=OnMainWindowClose 触发
        // → 应用直接退出。显式指 MainWindow=main 让 splash close 不影响应用生命周期。
        main.Show();
        Application.Current.MainWindow = main;

        // v0.6.16: --auto-refresh-catalog CLI flag — 启动后后台触发 catalog 刷新
        // (含 GitHub metadata enrichment 如果 settings.FetchCatalogMetadata=true)。
        // fire-and-forget,不阻塞 UI;异常由 CatalogRefreshService 内部处理 + AppLogger。
        if (Array.IndexOf(e.Args, "--auto-refresh-catalog") >= 0 && _mainVm is not null)
        {
            logger?.Info("app-startup", "--auto-refresh-catalog: 触发后台 catalog 刷新");
            _ = _mainVm.RefreshCatalogAsync();
        }

        // v0.6.11+ dashboard/splash polish:Stage 4 Ready(MainWindow 已 Show)。
        // 必须在 NotifyMainWindowReady() 之前 — 后者启动 fade 计时,fade 完
        // VM 就 _disposed,late report 会被静默丢掉。
        _splashVm?.ReportStageProgress(Stage.Ready, 100);

        // v0.6.8: MainWindow 显示后通知 splash VM 启动最少 3s 计时 + fade
        _splashVm?.NotifyMainWindowReady();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        // kill all env processes we started
        try { _launcher?.Dispose(); } catch { }
    }

    private static string ResolveGitExe(string projectRoot)
    {
        var portable = Path.Combine(projectRoot, "bin", "git-portable", "cmd", "git.exe");
        if (File.Exists(portable)) return portable;
        return "git"; // fallback to PATH
    }

    /// <summary>
    /// 组装 <see cref="PyTorchVersionDirectory"/>:catalog 拉 PyPI + pytorch.org,
    /// cache 走 <paramref name="localDataDir"/> 永久落盘。
    /// v0.6.16: localDataDir = &lt;projectRoot&gt;/.manager/。
    /// </summary>
    /// <remarks>
    /// <c>internal</c> 而非 <c>private</c>:<c>AppWiringTests</c> 需要在不启动
    /// WPF / 不发真实网络请求的前提下验证组装链路(csproj 已声明
    /// <c>InternalsVisibleTo("ComfyUI.Manager.Tests")</c>)。
    /// </remarks>
    internal static PyTorchVersionDirectory BuildPyTorchVersionDirectory(string localDataDir, HttpClient http)
    {
        var catalog = new PyTorchVersionCatalog(http);
        var cache = new PyTorchVersionCatalogCache(localDataDir);
        return new PyTorchVersionDirectory(catalog, cache);
    }

    /// <summary>
    /// v0.6.15.4: 构建带代理的 HttpClient。HttpProxyConfig.Enabled=true → WebProxy(http://url:port);
    /// 否则显式 Proxy=null/UseProxy=false (不走 WinHTTP default system proxy, R2 mitigation)。
    /// v0.6.22+: 同时给所有 client 注入 User-Agent + Accept 头 — 避免 CivitAI/HF 等
    /// Cloudflare 反爬把空 User-Agent 的 .NET HttpClient 当 bot 拦截(2026-08-20 用户报告
    /// 开启代理后 CivitAI 返回 HTML 而非 JSON,加头后表现与 curl 一致)。User-Agent 跟
    /// 之前 singleton 用的字符串保持一致(App.xaml.cs:224 旧显式 ParseAdd 现在幂等冗余)。
    /// <c>internal</c> 而非 <c>private</c>:<c>AppHttpProxyWiringTests</c> 验证 (csproj 已声明
    /// <c>InternalsVisibleTo("ComfyUI.Manager.Tests")</c>)。
    /// </summary>
    internal const string DefaultUserAgent = "ComfyUI-Manager/0.6.13";

    internal static HttpClient BuildHttpClient(HttpProxyConfig? proxy)
    {
        var handler = new HttpClientHandler();
        if (proxy is not null)
        {
            proxy.ApplyTo(handler);
        }
        else
        {
            // Disabled 默认: 显式不走 system proxy
            handler.Proxy = null;
            handler.UseProxy = false;
        }
        // v0.6.16 hotfix: 15s 太短 — catalog JSON 是 ~3MB,在慢网络(代理/跨地区)下
        // 经常 >15s,被 Timeout 切 → refresh 已取消 + 后续 metadata enrichment 不跑。
        // 60s 足够大多数情况,极端慢的网络可以再调。
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // v0.6.22+: User-Agent + Accept 头注入 — 减少 Cloudflare 反爬 false-positive。
        // Per-request 头不污染全局集合 — 不影响 caller 自己再覆盖(详见 v0.6.13-B GitHub API)。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    /// <summary>
    /// v0.6.7.5: NodeInstallDiffService 跑 pip list 用的进程执行器。
    /// 走 inline Process.Start(没共享的 ProcessLauncher.RunProcessAsync —
    /// ProcessLauncher 是 instance class,这里只跑轻量级 pip list,不值得抽 instance)。
    /// </summary>
    private static Task<ProcessResult> RunProcessForDiffAsync(
        string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ProcessResult(false, -1, "", ex.Message));
        }
        if (process is null)
        {
            return Task.FromResult(new ProcessResult(false, -1, "", "Process.Start 返回 null"));
        }

        return Task.Run(async () =>
        {
            var stdoutT = process.StandardOutput.ReadToEndAsync();
            var stderrT = process.StandardError.ReadToEndAsync();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(false, -1, "", "timeout/cancel");
            }
            var stdout = "";
            var stderr = "";
            try { stdout = await stdoutT; } catch { }
            try { stderr = await stderrT; } catch { }
            return new ProcessResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        });
    }
}
