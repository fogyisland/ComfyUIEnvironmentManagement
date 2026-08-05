using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// BaseEnvInstaller:跨 env × BaseEnvProfile 跑 pip install,emit Progress 事件。
///
/// 设计要点:
/// - 单 env 失败不中断,继续下个 env(G7)
/// - CancellationToken 触发 → 立即 kill 当前 pip 进程(G8)
/// - RunPipAsync 是 protected virtual,测试可 override(G12)
/// - 不依赖 ProcessLauncher 的 private 解析方法(G11)
/// </summary>
public class BaseEnvInstaller
{
    // pip 输出 percent 匹配的两种模式:
    //   "  5%|▍                   | 0.00M/0.00M [00:00<?, ?B/s]"
    //   "Downloading (5.0/245.3 MB)"
    //   "Progress (5.0/245)"
    private static readonly Regex PercentPattern = new(
        @"(?x)
        Progress \s* \( (?<p1>\d+\.?\d*)  |
        (?<p2>\d+\.?\d*) \s* / \s* \d |
        (?<p3>\d+) %",
        RegexOptions.Compiled);

    private readonly IEnvironmentRepository _envRepo;
    private readonly AppLogger? _logger;

    public BaseEnvInstaller(IEnvironmentRepository envRepo, AppLogger? logger = null)
    {
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _logger = logger;
    }

    /// <summary>
    /// 顺序跑 base env install 跨多个 env。
    /// 每个 env:
    ///   1) emit Running
    ///   2) 调 RunPipAsync(env python + BuildPipArgs() + onLine/onPercent)
    ///   3) emit Succeeded / Failed
    /// </summary>
    public virtual async Task<BaseEnvInstallResult> InstallAsync(
        IReadOnlyList<string> envIds,
        BaseEnvProfile profile,
        IProgress<BaseEnvProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (envIds is null || envIds.Count == 0)
        {
            return new BaseEnvInstallResult(false, 0, 0,
                new Dictionary<string, string>());
        }

        _logger?.Info("bed-install",
            $"开始安装 profile={profile.Id} cuda={profile.CudaVersion} torch={profile.TorchVersion} envs={envIds.Count}");

        var failures = new Dictionary<string, string>();
        var succeeded = 0;
        var failed = 0;
        var cancelled = false;
        var total = envIds.Count;
        var completed = 0;

        var pipArgs = profile.BuildPipArgs();

        foreach (var envId in envIds)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            Environment env;
            try
            {
                env = _envRepo.Get(envId)
                    ?? throw new InvalidOperationException(
                        $"env '{envId}' 不存在");
            }
            catch (Exception ex)
            {
                failures[envId] = ex.Message;
                failed++;
                completed++;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed, total,
                    envId, null, null, null, ex.Message));
                continue;
            }

            string pythonExe;
            try
            {
                pythonExe = GetVenvPythonPath(env);
            }
            catch (Exception ex)
            {
                failures[envId] = ex.Message;
                failed++;
                completed++;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed, total,
                    envId, env.Name, null, null, ex.Message));
                continue;
            }

            // G1: 进入 pip 之前立刻写 installing,UI 立刻看到 ⏳ 装中,
            // 同一行 StartCommand 立即 disabled(已有 v0.6.5.7 门控)。
            // 单 env 写失败不致命(envRepo 不可写概率几乎 0,跟终态回写 try/catch 一致)。
            try
            {
                var live = _envRepo.Get(envId);
                if (live is not null)
                {
                    live.BedStatus = "installing";
                    _envRepo.Upsert(live);
                }
            }
            catch
            {
                // 写失败不致命,继续装(终态回写仍会写 done/failed)
            }

            progress?.Report(new BaseEnvProgress(
                BaseEnvStatus.Running, completed, total,
                envId, env.Name, 0, $"开始安装 ({env.Name})", null));

            try
            {
                var result = await RunPipAsync(
                    pythonExe, pipArgs,
                    line => progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Running, completed, total,
                        envId, env.Name, null, line, null)),
                    pct => progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Running, completed, total,
                        envId, env.Name, pct, null, null)),
                    ct);

                if (result.WasCancelled || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    failures[envId] = "用户取消";
                    failed++;
                    LogTerminal(envId, env.Name, "cancelled", "用户取消");
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Cancelled, completed + 1, total,
                        envId, env.Name, null, null, "用户取消"));
                    completed++;
                    break;
                }

                if (result.ExitCode == 0)
                {
                    succeeded++;
                    LogTerminal(envId, env.Name, "succeeded", null);
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Succeeded, completed + 1, total,
                        envId, env.Name, 100, null, null));
                }
                else
                {
                    failed++;
                    var reason = $"pip 退出码 {result.ExitCode}";
                    failures[envId] = reason;
                    LogTerminal(envId, env.Name, "failed", reason);
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Failed, completed + 1, total,
                        envId, env.Name, null, null,
                        $"pip 退出码 {result.ExitCode}"));
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                failures[envId] = "用户取消";
                failed++;
                LogTerminal(envId, env.Name, "cancelled", "用户取消");
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Cancelled, completed + 1, total,
                    envId, env.Name, null, null, "用户取消"));
                completed++;
                break;
            }
            catch (Exception ex)
            {
                failed++;
                failures[envId] = ex.Message;
                LogTerminal(envId, env.Name, "failed", ex.Message);
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed + 1, total,
                    envId, env.Name, null, null, ex.Message));
            }

            completed++;
        }

        // 终态回写:每个 envId 写 BedProfileId + BedStatus + BedFailedReason
        // 用户取消 / 失败 / 成功 三种状态都覆盖(失败 dict 里有 envId 即 "failed",
        // 没有即 "done")。用户在 EnvListVM 看 BED 列即知状态。
        foreach (var envId in envIds)
        {
            try
            {
                var envRow = _envRepo.Get(envId);
                if (envRow is null) continue;
                envRow.BedProfileId = profile.Id;
                if (failures.TryGetValue(envId, out var reason))
                {
                    envRow.BedStatus = "failed";
                    envRow.BedFailedReason = reason;
                }
                else
                {
                    envRow.BedStatus = "done";
                    envRow.BedFailedReason = null;
                }
                _envRepo.Upsert(envRow);
            }
            catch
            {
                // 单 env 写失败不致命(可能被并发删除);不影响整体结果返回
            }
        }

        if (_logger is not null)
        {
            var summary = cancelled
                ? $"安装被取消 succeeded={succeeded} failed={failed}"
                : $"安装完成 succeeded={succeeded} failed={failed}";
            if (cancelled || failed > 0) _logger.Error("bed-install", summary);
            else _logger.Info("bed-install", summary);
        }

        return new BaseEnvInstallResult(
            cancelled, succeeded, failed, failures);
    }

    private void LogTerminal(string envId, string envName, string status, string? reason)
    {
        if (_logger is null) return;
        var msg = reason is null
            ? $"env='{envName}' {status}"
            : $"env='{envName}' {status} — {reason}";
        if (status == "succeeded") _logger.Info("bed-install", msg);
        else _logger.Error("bed-install", msg);
    }

    /// <summary>
    /// 跑 `&lt;pythonExe&gt; -m pip &lt;pipArgs&gt;` 一个 env。
    /// 默认实现:Process.Start + 重定向 stdout/stderr,onLine/onPercent 回调。
    /// 测试 override 模拟 cancel / exit code / percent 输出。
    /// </summary>
    protected virtual Task<PipResult> RunPipAsync(
        string pythonExe,
        IReadOnlyList<string> pipArgs,
        Action<string> onLine,
        Action<int?> onPercent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pythonExe))
        {
            throw new ArgumentException("pythonExe 不能为空", nameof(pythonExe));
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        foreach (var a in pipArgs)
        {
            psi.ArgumentList.Add(a);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"启动 pip 失败: {ex.Message}", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("Process.Start 返回 null");
        }

        var tcs = new TaskCompletionSource<PipResult>();
        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine(line);
                    var pct = TryParsePercent(line);
                    if (pct.HasValue) onPercent((int)pct.Value);
                }
            }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine("[stderr] " + line);
                }
            }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { await Task.WhenAll(stdoutDone.Task, stderrDone.Task); } catch { }
                if (ct.IsCancellationRequested)
                {
                    TryKill(process);
                    tcs.TrySetResult(new PipResult(-1, true));
                }
                else
                {
                    int code;
                    try { code = process.ExitCode; } catch { code = -1; }
                    tcs.TrySetResult(new PipResult(code, false));
                }
                try { process.Dispose(); } catch { }
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// public static 方便测试调用:解析 env 的 venv python 路径。
    /// 优先级:
    ///   1) env.PythonExecutable 非空 + File.Exists
    ///   2) &lt;VenvPath&gt;/Scripts/python.exe (Windows) 或 &lt;VenvPath&gt;/bin/python (其他)
    /// </summary>
    public static string GetVenvPythonPath(Environment env)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (!string.IsNullOrWhiteSpace(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }

        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            throw new ArgumentException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath");
        }

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

    private static int? TryParsePercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = PercentPattern.Match(line);
        if (!m.Success) return null;
        for (var i = 1; i < m.Groups.Count; i++)
        {
            if (m.Groups[i].Success)
            {
                if (double.TryParse(
                    m.Groups[i].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d))
                {
                    if (d < 0) return 0;
                    if (d > 100) return 100;
                    return (int)d;
                }
            }
        }
        return null;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>
    /// 启动 reconciliation:把所有 BedStatus == "installing" 的 env 翻成
    /// "failed" + BedFailedReason = "上次未完成"。
    ///
    /// WPF 重启后没有跨进程 job 持久化,这些行只能来自:
    ///   1) 上次 WPF 强杀(任务管理器 / 断电 / OS 重启),pip 进程已死
    ///   2) 上次 WPF 正常退出但 pip 还在跑(理论上 OnExit 应 cancel + drain,
    ///      但 v0.6.5.6 之前没这个保证)
    /// 不做更细的判断(无法知道 venv 是否真的有 torch),统一标 failed 让
    /// 用户重跑,启动按钮 enabled + tooltip 提示 "上次未完成"。
    /// </summary>
    public static int ReconcileStaleOnStartup(EnvironmentRepository envRepo)
    {
        if (envRepo is null) throw new ArgumentNullException(nameof(envRepo));

        var stale = 0;
        foreach (var env in envRepo.ListAll())
        {
            if (env.BedStatus == "installing")
            {
                env.BedStatus = "failed";
                env.BedFailedReason = "上次未完成";
                try
                {
                    envRepo.Upsert(env);
                    stale++;
                }
                catch
                {
                    // 单行写失败不致命,下次启动再翻
                }
            }
        }
        return stale;
    }
}
