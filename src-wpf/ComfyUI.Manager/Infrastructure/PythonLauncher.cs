using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public class PythonLauncher : IDisposable
{
    private Process? _service;
    private readonly int _port;
    private readonly string _bind;
    private readonly string _projectRoot;
    private bool _disposed;

    public PythonLauncher(string projectRoot,
        int port = 7800, string bind = "127.0.0.1")
    {
        _projectRoot = projectRoot;
        _port = port;
        _bind = bind;
        PythonExe = Path.Combine(projectRoot, "python", "python.exe");
    }

    public int Port => _port;
    public string PythonExe { get; }

    public async Task LaunchAsync(CancellationToken ct = default)
    {
        if (IsPortInUse(_bind, _port))
            throw new ServiceLaunchException(
                $"端口 {_port} 已被占用,可能已有 ComfyUI Manager 实例在运行");

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = _projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("comfy_mgr.cli");
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_port.ToString());
        psi.ArgumentList.Add("--bind");
        psi.ArgumentList.Add(_bind);
        psi.EnvironmentVariables["PYTHONPATH"] =
            $"{_projectRoot};{Path.Combine(_projectRoot, "src")}";

        _service = Process.Start(psi)
            ?? throw new ServiceLaunchException("无法启动 Python 子进程");

        await WaitForHealthAsync(ct);
    }

    private async Task WaitForHealthAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await http.GetAsync(
                    $"http://{_bind}:{_port}/healthz", ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new ServiceLaunchException(
            $"Python service {_port} 端口 /healthz 超时(10s)");
    }

    public static bool IsPortInUse(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromMilliseconds(500))
                && client.Connected;
        }
        catch { return false; }
    }

    public async Task ShutdownAsync()
    {
        if (_service is null or { HasExited: true }) return;
        try
        {
            if (!_service.WaitForExit(3000))
                _service.Kill(entireProcessTree: true);
        }
        catch { }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ShutdownAsync().GetAwaiter().GetResult(); }
        catch { }
        _service?.Dispose();
    }
}
