using System;
using System.IO;
using System.Windows;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager;

public partial class App : Application
{
    private ServiceConnection? _connection;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');
        var launcher = new PythonLauncher(projectRoot);

        try
        {
            await launcher.LaunchAsync();
        }
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
        var main = new MainWindow();
        // T30 加: main.DataContext = new MainViewModel(api, ws, launcher);
        main.Show();

        Exit += (_, _) =>
        {
            _connection?.Dispose();
        };
    }
}
