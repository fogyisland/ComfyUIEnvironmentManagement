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

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore);

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
}
