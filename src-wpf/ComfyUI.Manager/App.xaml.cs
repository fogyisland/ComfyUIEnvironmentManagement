using System;
using System.IO;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class App : Application
{
    private MainViewModel? _mainVm;

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

        // M5.2: the UI reads metadata directly from local SQLite via
        // repositories. The connection factory locates catalog.db under
        // %APPDATA%\ComfyUI-Manager; each repository is a thin reader.
        var factory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(factory);
        var nodeRepo = new NodeRepository(factory);
        var catalogRepo = new CatalogRepository(factory);
        var versionRepo = new VersionRepository(factory);
        var depRepo = new DepRepository(factory);
        var processRepo = new ProcessStateRepository(factory);
        var settingsRepo = new SettingsRepository();

        _mainVm = new MainViewModel(
            envRepo, nodeRepo, catalogRepo, versionRepo,
            depRepo, processRepo, settingsRepo);

        var main = new MainWindow { DataContext = _mainVm };
        main.Show();
    }
}