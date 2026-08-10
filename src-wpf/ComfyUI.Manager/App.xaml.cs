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
    // v0.6.8: Splash 画面引用 — OnStartup 立即 Show,MainWindow 加载好后
    // NotifyMainWindowReady 触发 fade;FadeCompleted 由 Window self-close raise。
    private SplashWindow? _splash;
    private SplashViewModel? _splashVm;

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

        var dbFactory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(dbFactory);

        // v0.6.5.13: 集中日志 — 所有 subsystem 写到 projectRoot/Logs/YYYY-MM-DD.log
        var logger = new AppLogger(projectRoot);
        // 启动时清理 >30 天的日志(用户原话:30 天保留)
        int cleaned = AppLogger.CleanupOlderThan(projectRoot, 30);
        logger.Info("app-startup", $"App 启动 cleaned={cleaned} projectRoot={projectRoot}");

        // v0.6.5.8: 启动 reconciliation — 把上次未装完的 "installing" 行翻成
        // "failed" + "上次未完成"。必须先于 MainViewModel.Load(),否则 UI 看到
        // ⏳ 装中 几秒后变 ❌ 闪烁。
        BaseEnvInstaller.ReconcileStaleOnStartup(envRepo);

        var nodeRepo = new NodeRepository(dbFactory);
        var processStateRepo = new ProcessStateRepository(dbFactory);

        // v0.6.7.1: 在 launcher 构造前先 Load settings — 让 startupTimeoutSeconds 可读。
        // SettingsDefaults.Apply 还在 launcher 构造之后,但 Apply 只动 path 类字段,
        // 不会改 ComfyUiStartupTimeoutSeconds,所以顺序安全。
        var settingsRepo = new SettingsRepository();
        var settings = settingsRepo.Load();

        // v0.6.9 T2:Apply stored theme BEFORE MainWindow.Show,避免先 Dark(默认)再切 Light 闪屏。
        // 必须早于 _mainVm 构造(MainViewModel 接收 IThemeService 引用),且 Application.Current.Resources
        // 此时已合并 Theme.xaml + Palette.Dark.xaml(slot 默认 Dark;Settings 存 "light" 时需 Apply)。
        var themeService = new ThemeService(Application.Current.Resources, logger);
        themeService.Apply(SettingsViewModel.ParseThemeMode(settings.ThemeMode));
        // v0.6.9 T9:暴露给 MainWindow 订阅 ThemeChanging。首次 Apply 时 resolved 跟 Current 都是 Dark
        // (ThemeService 内部 default),所以 ThemeChanging 不会被 fire — 避免启动期无意义 fade。
        ThemeService = themeService;

        _launcher = new ProcessLauncher(
            projectRoot, dbFactory, envRepo, processStateRepo, logger,
            settings.ComfyUiStartupTimeoutSeconds,
            settings.ComfyUiLocale,
            settings.SharedModelsDirectory);

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
        // 共享同一份 GitProxyConfig,SettingsViewModel 改它会立即影响下一次 git 调用。
        var gitProxy = GitProxyConfig.From(settings);
        var gitRunner = new GitRunner(gitExe, gitProxy);
        // v0.6.7.5: 节点安装前的 pip diff check — NodeInstallDiffService 跑 pip list JSON,
        // 由 NodeOperations.InstallAsync 在 clone 前调。先于 nodeOps 构造,因为 ctor 要拿。
        var diffService = new NodeInstallDiffService(
            (exe, args, timeout, ct) => RunProcessForDiffAsync(exe, args, timeout, ct),
            logger);
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings, diffService, logger: logger);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes, logger);
        var catalogCacheStore = new CatalogCacheStore();
        var catalogRepo = new CatalogRepository(catalogCacheStore);
        var githubVersionService = new GitHubVersionService(http);
        var nodeVersionRepo = new NodeVersionRepository(catalogCacheStore);
        var catalogRefreshService = new CatalogRefreshService(
            catalogFetcher, catalogRepo, settings, githubVersionService, nodeVersionRepo, logger);
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
        // v0.6.5.1: BaseEnvProfileLoader 运行时拉取真实 PyTorch stable 版本。
        // cache 目录 = %APPDATA%/ComfyUI-Manager(PyTorchVersionCache 直接在此存
        // pytorch_versions_cache.json);复用共享 http(15s 超时)。拉取失败静默回退。
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComfyUI-Manager");
        var profileLoader = new BaseEnvProfileLoader(projectRoot, appDataDir, http);
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
        var dashboardService = new DashboardService(envRepo, nodeRepo, logger, http);
        // v0.6.9 T7:全局搜索 service(跨 4 kind 索引:env / node / settings section / command)。
        // 复用 envRepo + nodeRepo;首次 OpenSpotlight 时 BuildAsync,后续键入仅走内存(G7)。
        var globalSearchService = new GlobalSearchService(envRepo, nodeRepo);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, envDeleter, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller,
            profileLoader, BuildPyTorchVersionDirectory(appDataDir, http), appDataDir, projectRoot,
            requirementsInstaller, systemInfoCollector, uiPreferencesService,
            _baseEnvUninstaller, _requirementsUninstaller,
            themeService, dashboardService, globalSearchService,
            // v0.6.10 T2:组件报告 + OpenBrowser 共享 Chrome 优先 fallback。
            new BrowserLauncher(),
            // v0.6.11+ T4:ComfyUI Manager toggle 安装器 — 传给 EnvListVM
            // ToggleComfyUiManagerCommand(显示 inline 状态面板)。
            comfyUiManagerInstaller);

        var main = new MainWindow { DataContext = _mainVm };
        main.ApplyStartupPreferences(uiPrefs);
        // v0.6.9.1 修复:Splash 先于 MainWindow Show,WPF 默认把第一个 Show 的窗口
        // 当作 MainWindow → Splash 3s 后 fade close → ShutdownMode=OnMainWindowClose 触发
        // → 应用直接退出。显式指 MainWindow=main 让 splash close 不影响应用生命周期。
        main.Show();
        Application.Current.MainWindow = main;

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
    /// cache 走 <paramref name="appDataDir"/> 永久落盘。
    /// </summary>
    /// <remarks>
    /// <c>internal</c> 而非 <c>private</c>:<c>AppWiringTests</c> 需要在不启动
    /// WPF / 不发真实网络请求的前提下验证组装链路(csproj 已声明
    /// <c>InternalsVisibleTo("ComfyUI.Manager.Tests")</c>)。
    /// </remarks>
    internal static PyTorchVersionDirectory BuildPyTorchVersionDirectory(string appDataDir, HttpClient http)
    {
        var catalog = new PyTorchVersionCatalog(http);
        var cache = new PyTorchVersionCatalogCache(appDataDir);
        return new PyTorchVersionDirectory(catalog, cache);
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
