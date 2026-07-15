using System;
using System.IO;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');

        // M5.2: WPF no longer launches a Python control service. It only
        // verifies the bundled venv can import comfy_mgr; the UI drives
        // everything else (env creation, config) against local SQLite +
        // direct process control, and listens on each env's port only when
        // that env is started.
        var verifier = new VenvVerifier(projectRoot);
        var result = await verifier.VerifyAsync();
        if (!result.Ok)
        {
            var msg = "Venv 验证失败,WPF 仍可启动但功能受限。\n\n"
                + result.ErrorMessage;
            MessageBox.Show(msg, "Venv 验证失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var dbFactory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(dbFactory);
        var nodeRepo = new NodeRepository(dbFactory);
        var processStateRepo = new ProcessStateRepository(dbFactory);
        _launcher = new ProcessLauncher(
            projectRoot, dbFactory, envRepo, processStateRepo);

        var settingsRepo = new SettingsRepository();
        var settings = settingsRepo.Load();

        // 首次启动:把 path 类字段默认填到程序目录下的子文件夹,UI 一眼可见。
        // 只填空,不覆盖用户已填值。Save 后 settings.json 自带绝对路径。
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
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo);
        var bulkOrchestrator = new BulkUpdateOrchestrator(
            projectRoot, gitExe, envRepo, nodeRepo, gitProxy);
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy);

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
