using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// ProcessLauncher:直接 Process.Start 拉 ComfyUI,把 stdout/stderr 写到
/// logs/&lt;env-id&gt;.log 文件,并维护 process_state + environments 状态表。
///
/// 替代了 M5.1 的 PythonLauncher / ServiceConnection 思路 —— WPF 不再依赖
/// 任何 Python control service,每个 env 各自独立启停。
/// </summary>
public sealed class ProcessLauncher : IDisposable
{
    private readonly string _projectRoot;
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly EnvironmentRepository _envRepo;
    private readonly ProcessStateRepository _processStateRepo;
    private readonly Dictionary<string, ProcessEntry> _running = new();
    private readonly object _runningLock = new();
    private bool _disposed;

    public ProcessLauncher(
        string projectRoot,
        SqliteConnectionFactory dbFactory,
        EnvironmentRepository envRepo,
        ProcessStateRepository processStateRepo)
    {
        _projectRoot = projectRoot;
        _dbFactory = dbFactory;
        _envRepo = envRepo;
        _processStateRepo = processStateRepo;
    }

    public string ProjectRoot => _projectRoot;

    /// <summary>
    /// log 文件路径:&lt;projectRoot&gt;/logs/&lt;env-id&gt;.log。
    /// </summary>
    public string LogFilePath(string envId)
    {
        return Path.Combine(_projectRoot, "logs", $"{envId}.log");
    }

    public bool IsRunning(Environment env)
    {
        lock (_runningLock)
        {
            return _running.ContainsKey(env.Id);
        }
    }

    public IReadOnlyList<string> RunningEnvIds
    {
        get
        {
            lock (_runningLock)
            {
                return _running.Keys.ToArray();
            }
        }
    }


    /// <summary>
    /// 启动一个 env。返回时进程已就绪(port 已 listen)、process_state 已写入、
    /// environments.status 已被设为 "running"。
    ///
    /// 抛出:
    /// - ArgumentException:env 缺关键字段(VenvPath / Port 等)
    /// - InvalidOperationException:env 已运行、main.py 找不到
    /// - TimeoutException:30s 内 port 未 listen(进程会被 kill)
    /// - ServiceLaunchException:Process.Start 失败 / 返回 null
    /// </summary>
    public async Task StartEnvAsync(Environment env, CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLauncher));

        lock (_runningLock)
        {
            if (_running.ContainsKey(env.Id))
            {
                throw new InvalidOperationException(
                    $"env '{env.Name}' 已在运行中");
            }
        }

        var pythonExe = ResolvePythonExecutable(env);
        var mainPy = ResolveMainPy(env);
        if (!File.Exists(mainPy))
        {
            throw new InvalidOperationException(
                $"找不到 main.py(已尝试:{mainPy})");
        }

        var port = env.Port
            ?? throw new ArgumentException(
                $"env '{env.Name}' 未配置 Port", nameof(env));

        if (IsPortInUse("127.0.0.1", port))
        {
            throw new ServiceLaunchException(
                $"端口 {port} 已被占用,无法启动 env '{env.Name}'");
        }

        var logPath = LogFilePath(env.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = Path.GetDirectoryName(mainPy)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(mainPy);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add("127.0.0.1");
        psi.EnvironmentVariables["PYTHONPATH"] =
            $"{_projectRoot};{Path.Combine(_projectRoot, "src")}";

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new ServiceLaunchException(
                $"无法启动 python 进程: {ex.Message}", ex);
        }

        if (process is null)
        {
            throw new ServiceLaunchException(
                $"Process.Start 返回 null(env '{env.Name}')");
        }

        var entry = new ProcessEntry(process, logPath);
        lock (_runningLock)
        {
            _running[env.Id] = entry;
        }

        // 后台 reader + Exited 监听 —— 必须在 WaitForPort 之前挂上,
        // 否则端口 listen 后 stdout 早就写到 pipe 里会丢。
        AttachStdoutReader(entry);
        AttachStderrReader(entry);
        AttachExitedHandler(env.Id, env.Name, entry);

        try
        {
            await WaitForPortAsync("127.0.0.1", port, TimeSpan.FromSeconds(30), ct);
        }
        catch
        {
            // 端口没起来:kill 进程、清空状态、清理 _running
            TryKillProcessTree(process);
            lock (_runningLock)
            {
                _running.Remove(env.Id);
            }
            throw;
        }

        // 成功路径:写 process_state + environments
        var now = DateTime.UtcNow;
        _processStateRepo.Upsert(new ProcessState
        {
            EnvId = env.Id,
            Pid = process.Id,
            Port = port,
            StartedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });

        // 更新 env row(用最新状态,避免覆盖其它字段)
        var fresh = _envRepo.Get(env.Id) ?? env;
        fresh.Status = "running";
        fresh.Pid = process.Id;
        try
        {
            _envRepo.Upsert(fresh);
        }
        catch
        {
            // env row 写失败不致命 —— 进程已启动,后续 reload 也能查到 process_state
        }
    }

    /// <summary>
    /// 停止一个 env。先 CloseMainWindow 优雅退出,等待 timeoutSeconds,
    /// 超时则 kill 整棵进程树。
    ///
    /// 即使 env 在 _running 中找不到(可能进程已意外退出),也会清掉
    /// process_state 行。
    /// </summary>
    public async Task StopEnvAsync(Environment env, int timeoutSeconds = 3,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLauncher));

        ProcessEntry? entry;
        lock (_runningLock)
        {
            _running.TryGetValue(env.Id, out entry);
            _running.Remove(env.Id);
        }

        if (entry is not null)
        {
            var process = entry.Process;
            try
            {
                if (!process.HasExited)
                {
                    try { process.CloseMainWindow(); } catch { }
                    using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    shutdownCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        await process.WaitForExitAsync(shutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 超时,fall through to kill
                    }
                }
            }
            catch { }

            if (!process.HasExited)
            {
                TryKillProcessTree(process);
            }
            try { process.Dispose(); } catch { }
        }

        // 清理状态:process_state + env row
        try { _processStateRepo.Delete(env.Id); } catch { }
        try
        {
            var fresh = _envRepo.Get(env.Id) ?? env;
            fresh.Status = "stopped";
            fresh.Pid = null;
            _envRepo.Upsert(fresh);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<ProcessEntry> toKill;
        lock (_runningLock)
        {
            toKill = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var entry in toKill)
        {
            try { TryKillProcessTree(entry.Process); } catch { }
            try { entry.Process.Dispose(); } catch { }
        }
    }

    // -------- internals --------

    private string ResolvePythonExecutable(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }

        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            throw new ArgumentException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath",
                nameof(env));
        }

        // Scripts/python.exe on Windows, bin/python on Linux/macOS —
        // 显式 OS 判定,避免在 WSL / macOS 开发时静默失败。
        var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                $"venv python 找不到: {exe}");
        }
        return exe;
    }

    private string ResolveMainPy(Environment env)
    {
        // 优先级 1:<root>/ComfyUI/main.py
        var nested = Path.Combine(env.RootPath, "ComfyUI", "main.py");
        if (File.Exists(nested)) return nested;

        // 优先级 2:<root>/main.py(env 直接就是 ComfyUI)
        var flat = Path.Combine(env.RootPath, "main.py");
        if (File.Exists(flat)) return flat;

        // 都不在:返回 nested,File.Exists 检查会失败,start 时抛清晰错误
        return nested;
    }

    private void AttachStdoutReader(ProcessEntry entry)
    {
        var process = entry.Process;
        var logPath = entry.LogFilePath;
        var pid = process.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                {
                    AutoFlush = true,
                };
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    await writer.WriteLineAsync($"[{ts}] [pid {pid}] OUT: {line}");
                }
            }
            catch
            {
                // 进程退出 / reader 取消,忽略
            }
        });
    }

    private void AttachStderrReader(ProcessEntry entry)
    {
        var process = entry.Process;
        var logPath = entry.LogFilePath;
        var pid = process.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                {
                    AutoFlush = true,
                };
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    await writer.WriteLineAsync($"[{ts}] [pid {pid}] ERR: {line}");
                }
            }
            catch
            {
                // 进程退出 / reader 取消,忽略
            }
        });
    }

    private void AttachExitedHandler(string envId, string envName, ProcessEntry entry)
    {
        var process = entry.Process;
        var logPath = entry.LogFilePath;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // 意外退出 / Stop 调用之后的退出都会触发。
            // 清掉 _running + process_state + env row,append exit code 到 log。
            lock (_runningLock)
            {
                // StopEnvAsync 会先移除 _running 再等待退出;若已不在表里,
                // 说明 Stop 正在接管清理,避免 DB double-write / clobber 并发重启。
                if (!_running.ContainsKey(envId)) return;
                _running.Remove(envId);
            }
            try
            {
                _processStateRepo.Delete(envId);
            }
            catch { }
            try
            {
                var fresh = _envRepo.Get(envId);
                if (fresh is not null)
                {
                    fresh.Status = "stopped";
                    fresh.Pid = null;
                    _envRepo.Upsert(fresh);
                }
            }
            catch { }

            try
            {
                using var writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                {
                    AutoFlush = true,
                };
                var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                int? exitCode = null;
                try { exitCode = process.ExitCode; } catch { }
                writer.WriteLine(
                    $"[{ts}] [pid {process.Id}] EXIT: env '{envName}' exit code {exitCode?.ToString() ?? "?"}");
            }
            catch { }
        };
    }

    private static async Task WaitForPortAsync(string host, int port,
        TimeSpan timeout, CancellationToken ct)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);
        while (!deadlineCts.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, deadlineCts.Token);
                return; // 连上了,端口已 listen
            }
            catch (OperationCanceledException)
            {
                // caller 取消 或 deadline 到期
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                throw new TimeoutException(
                    $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
            }
            catch
            {
                // connection refused / 端口未起,重试
            }

            try
            {
                await Task.Delay(500, deadlineCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                throw new TimeoutException(
                    $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
            }
        }
        throw new TimeoutException(
            $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
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
        catch
        {
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private sealed record ProcessEntry(Process Process, string LogFilePath);
}
