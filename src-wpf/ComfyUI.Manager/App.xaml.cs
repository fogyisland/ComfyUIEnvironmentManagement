using System;
using System.IO;
using System.Windows;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class App : Application
{
    private ServiceConnection? _connection;
    private MainViewModel? _mainVm;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');
        var launcher = new PythonLauncher(projectRoot);

        try { await launcher.LaunchAsync(); }
        catch (ServiceLaunchException ex)
        {
            MessageBox.Show(ex.Message, "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var api = new ApiClient($"http://127.0.0.1:{launcher.Port}");
        var ws = new WsClient($"ws://127.0.0.1:{launcher.Port}/ws/events");
        try { await ws.ConnectAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show($"WS 连接失败: {ex.Message}", "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _connection = new ServiceConnection(launcher, api, ws);
        _mainVm = new MainViewModel(api, ws, launcher);
        // P6 接入: _mainVm.Environments = new EnvironmentListViewModel(api, ws);

        var main = new MainWindow { DataContext = _mainVm };
        main.Show();

        Exit += (_, _) => _connection?.Dispose();
    }
}