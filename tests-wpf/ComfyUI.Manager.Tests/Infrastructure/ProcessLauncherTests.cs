using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// Regression coverage for the M5.2-T5 review Critical bug: StopEnvAsync's
/// grace timeout was wired to the caller token instead of shutdownCts, so a
/// process that ignores CloseMainWindow() would hang WPF for the child's full
/// lifetime. This test launches a real Python process that binds the port then
/// sleeps 60s and asserts StopEnvAsync(timeoutSeconds=2) returns promptly.
/// </summary>
public sealed class ProcessLauncherTests
{
    private static string? FindPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "-c \"import sys; print(sys.executable)\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.HasExited && p.ExitCode == 0)
                {
                    var exe = stdout.Trim();
                    // ResolvePythonExecutable checks File.Exists, so we need the
                    // absolute interpreter path, not the bare PATH command name.
                    if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
            catch { /* try next candidate */ }
        }
        return null;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StopEnvAsync_TimesOutAndKills_WhenProcessIgnoresClose()
    {
        var python = FindPython();
        if (python is null)
        {
            // No Python on PATH — cannot exercise a real process. Build-only
            // verification still covers the fix; skip rather than fail.
            return;
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-mgr-pl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // A console process (no window) ignores CloseMainWindow(), so the only
        // way StopEnvAsync can return is via the grace-timeout -> kill path.
        var mainPy = Path.Combine(tempRoot, "main.py");
        File.WriteAllText(mainPy, """
import sys, socket, time
host, port = "127.0.0.1", 0
a = sys.argv[1:]
for i, v in enumerate(a):
    if v == "--port":
        port = int(a[i + 1])
    elif v == "--listen":
        host = a[i + 1]
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind((host, port))
s.listen(5)
time.sleep(60)
""");

        var port = FreeTcpPort();
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var stateRepo = new ProcessStateRepository(db.Factory);

        var env = new Environment
        {
            Id = "env-stop-timeout",
            Name = "stop-timeout",
            RootPath = tempRoot,
            ComfyuiLayout = "isolated",
            PythonExecutable = python,
            Port = port,
            Status = "stopped",
        };
        envRepo.Upsert(env);

        using var launcher = new ProcessLauncher(
            tempRoot, db.Factory, envRepo, stateRepo);

        await launcher.StartEnvAsync(env);
        Assert.True(launcher.IsRunning(env), "env should be running after start");

        var sw = Stopwatch.StartNew();
        await launcher.StopEnvAsync(env, timeoutSeconds: 2);
        sw.Stop();

        // Pre-fix, StopEnvAsync would await WaitForExitAsync(callerToken) and
        // block for the process's full 60s sleep. With the fix it should fall
        // through to kill within a few seconds of the 2s grace window.
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(20),
            $"StopEnvAsync took {sw.Elapsed.TotalSeconds:0.0}s — timeout token not honored");
        Assert.False(launcher.IsRunning(env), "env should be removed after stop");

        try { Directory.Delete(tempRoot, recursive: true); } catch { }
    }
}
