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

        var dbFactory = new SqliteConnectionFactory();
        _mainVm = new MainViewModel(dbFactory);

        var main = new MainWindow { DataContext = _mainVm };
        main.Show();
    }
}