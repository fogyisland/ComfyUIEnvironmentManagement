using System;
using System.IO;
using System.Net.Http;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class App : Application
{
    private MainViewModel? _mainVm;
    private ProcessLauncher? _launcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');

        // M5.2: WPF 完全独立 —— 不启动任何 Python control service。
        // 直连 SQLite + 直 Process.Start ComfyUI + 直调 git。
        // venv 检查只在 EnvCreatorService 创建 env 时做(那时 python.exe
        // 路径用户已指定),启动时拦着太烦。

        var dbFactory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(dbFactory);

        // v0.6.5.8: 启动 reconciliation — 把上次未装完的 "installing" 行翻成
        // "failed" + "上次未完成"。必须先于 MainViewModel.Load(),否则 UI 看到
        // ⏳ 装中 几秒后变 ❌ 闪烁。
        BaseEnvInstaller.ReconcileStaleOnStartup(envRepo);

        var nodeRepo = new NodeRepository(dbFactory);
        var processStateRepo = new ProcessStateRepository(dbFactory);
        _launcher = new ProcessLauncher(
            projectRoot, dbFactory, envRepo, processStateRepo);

        var settingsRepo = new SettingsRepository();
        var settings = settingsRepo.Load();

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
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes);
        var catalogCacheStore = new CatalogCacheStore();
        var catalogRepo = new CatalogRepository(catalogCacheStore);
        var githubVersionService = new GitHubVersionService(http);
        var nodeVersionRepo = new NodeVersionRepository(catalogCacheStore);
        var catalogRefreshService = new CatalogRefreshService(
            catalogFetcher, catalogRepo, settings, githubVersionService, nodeVersionRepo);
        var bulkOrchestrator = new BulkUpdateOrchestrator(
            projectRoot, gitExe, envRepo, nodeRepo, gitProxy);
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings, projectRoot);
        var baseEnvInstaller = new BaseEnvInstaller(envRepo);
        // v0.6.5.x hotfix:Env 删除跑腿 service(stop running + 删目录 + 删 SQLite 行)。
        // 复用 envRepo 跟 _launcher,跟 EnvironmentListView 共一份。
        var envDeleter = new EnvDeleterService(envRepo, _launcher);
        // v0.6.5.12: requirements.txt 装依赖(runs `pip install -r <env-root>/requirements.txt`,
        // 跳过 torch 行 — torch 版本由 BED profile 锁)
        var requirementsInstaller = new RequirementsInstaller();
        // v0.6.5.1: BaseEnvProfileLoader 运行时拉取真实 PyTorch stable 版本。
        // cache 目录 = %APPDATA%/ComfyUI-Manager(PyTorchVersionCache 直接在此存
        // pytorch_versions_cache.json);复用共享 http(15s 超时)。拉取失败静默回退。
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComfyUI-Manager");
        var profileLoader = new BaseEnvProfileLoader(projectRoot, appDataDir, http);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, envDeleter, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller,
            profileLoader, BuildPyTorchVersionDirectory(appDataDir, http), appDataDir, projectRoot,
            requirementsInstaller);

        var main = new MainWindow { DataContext = _mainVm };
        main.Show();
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
}
