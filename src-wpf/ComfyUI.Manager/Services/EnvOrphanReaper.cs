using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x: 启动期 orphan env 清理 — 用户原话"界面开启后,检查环境,如果配置的环境
/// 端口与我们系统端口一致,就检查是不是当前目录启动的服务,如果是则关闭环境"。
///
/// 跟 <see cref="EnvStartupStopper"/> 的分工(都跑在启动期):
/// - <see cref="EnvStartupStopper"/> 走 <c>env.Status="running"</c>(DB 记录)— 处理上次
///   app 优雅退出失败的 env
/// - 本服务走 port→pid→cwd 探测 — 处理上次 app 崩溃 / hard-kill / 断电 导致
///   <c>env.Status</c> 没翻 stopped 但进程仍监听着 port 的孤儿
///
/// 设计:
/// - 跑在 SettingsDefaults.Apply 之后、MainViewModel 构造之前 — 让 UI 看到 clean state
/// - 对每个 env.Port != null:
///   1) port 无人监听 → skip(交给 EnvStartupReconciler 处理 stale DB)
///   2) port 有人监听但 EXE 不在 env.RootPath 下 → skip(不是我们的 env,可能用户启了别的服务)
///   3) port 有人监听且 EXE 在 env.RootPath 下 → graceful stop(env 是上次会话的孤儿)
/// - graceful stop 走 <see cref="ProcessLauncher.StopEnvAsync"/>(同 EnvStartupStopper + EnvExitCleanupService)
/// - status 翻 stopped + pid null(idempotent + 兜底 stop 失败)
/// - 失败只 warn,不阻断启动 — 同 <see cref="EnvStartupStopper"/> 容错模式
///
/// 测试 seam(全部可注入 Func,默认走真实实现):
/// - <see cref="ListeningPidLookup"/>: int port → int? pid(默认 <see cref="EnvPortProbe.GetListeningPidByPort"/>)
/// - <see cref="EnvOwnerCheck"/>: (int pid, string envRootPath) → bool(默认 <see cref="EnvPortProbe.IsEnvProcessOwned"/>)
/// - <see cref="Stopper"/>: graceful stop delegate(默认 <c>_launcher.StopEnvAsync</c>)
/// </summary>
public sealed class EnvOrphanReaper
{
    private readonly IEnvironmentRepository _envRepo;
    private readonly ProcessLauncher _launcher;
    private readonly AppLogger? _logger;

    public EnvOrphanReaper(
        IEnvironmentRepository envRepo,
        ProcessLauncher launcher,
        AppLogger? logger = null)
    {
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger;
        Stopper = (env, timeout, ct) => _launcher.StopEnvAsync(env, timeout, ct);
    }

    /// <summary>
    /// 测试 seam: port → pid 查表。null → 走默认 <see cref="EnvPortProbe.GetListeningPidByPort"/>。
    /// </summary>
    public Func<int, int?>? ListeningPidLookup { get; set; }

    /// <summary>
    /// 测试 seam: 判定 pid 进程是否属于 envRootPath 这个 env。null → 走默认 <see cref="EnvPortProbe.IsEnvProcessOwned"/>。
    /// </summary>
    public Func<int, string, bool>? EnvOwnerCheck { get; set; }

    /// <summary>
    /// 测试 seam: 单 env stop delegate。null → 走 <see cref="ProcessLauncher.StopEnvAsync"/>。
    /// </summary>
    public Func<Environment, int, CancellationToken, Task>? Stopper { get; set; }

    /// <summary>
    /// 跑一次启动期孤儿扫描 + 杀进程。返回"实际尝试过 stop"的 env 数(成功失败都算)。
    /// 不抛 — try/catch 在内部所有路径,只 warn 写日志。
    /// </summary>
    public async Task<int> ReapOrphansAsync(CancellationToken ct = default)
    {
        var envs = _envRepo.ListAll().Where(e => e.Port.HasValue).ToList();
        if (envs.Count == 0) return 0;

        var pidLookup = ListeningPidLookup ?? EnvPortProbe.GetListeningPidByPort;
        var ownerCheck = EnvOwnerCheck ?? EnvPortProbe.IsEnvProcessOwned;
        var stopper = Stopper ?? ((env, t, c) => _launcher.StopEnvAsync(env, t, c));

        int reaped = 0;
        foreach (var env in envs)
        {
            ct.ThrowIfCancellationRequested();

            int port = env.Port!.Value;
            int? pid;
            try
            {
                pid = pidLookup(port);
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-orphan-reap",
                    $"env='{env.Name}' port={port} 查 pid 失败:{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (pid is null)
            {
                // 端口无人监听 — 不是 port-based orphan。留给 EnvStartupReconciler 标 stale。
                continue;
            }

            bool ownedByEnv;
            try
            {
                ownedByEnv = ownerCheck(pid.Value, env.RootPath);
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-orphan-reap",
                    $"env='{env.Name}' pid={pid} 查 EXE 路径失败:{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (!ownedByEnv)
            {
                _logger?.Info("env-orphan-reap",
                    $"env='{env.Name}' port={port} pid={pid} EXE 不在 env.RootPath='{env.RootPath}' 下,跳过(非本 app 启动)");
                continue;
            }

            // owned by env → 上次会话孤儿,graceful stop。
            try
            {
                await stopper(env, 5, ct).ConfigureAwait(false);
                _logger?.Info("env-orphan-reap",
                    $"env='{env.Name}' port={port} pid={pid} 启动期孤儿已停");
            }
            catch (OperationCanceledException)
            {
                _logger?.Warn("env-orphan-reap", $"env='{env.Name}' 启动期停止取消");
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-orphan-reap",
                    $"env='{env.Name}' 启动期停止失败:{ex.GetType().Name}: {ex.Message}");
                // 仍翻 status=stopped(stop 失败的 env 也保证 DB 一致)。
            }

            try
            {
                var fresh = _envRepo.Get(env.Id) ?? env;
                fresh.Status = "stopped";
                fresh.Pid = null;
                _envRepo.Upsert(fresh);
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-orphan-reap",
                    $"env='{env.Name}' 状态写回失败:{ex.GetType().Name}: {ex.Message}");
            }
            reaped++;
        }

        if (reaped > 0)
        {
            _logger?.Info("env-orphan-reap", $"启动期孤儿清理完成,共 {reaped} 个 env");
        }
        return reaped;
    }

    /// <summary>
    /// 路径前缀判定:归一化(absolute + 末尾 separator trim)+ Windows 大小写不敏感 + boundary match。
    /// 任一为 null/empty → false。Path 操作抛异常 → false。
    /// </summary>
    internal static bool IsPathUnder(string path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix)) return false;
        try
        {
            var np = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var nq = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (np.Length < nq.Length) return false;
            if (!np.StartsWith(nq, StringComparison.OrdinalIgnoreCase)) return false;
            if (np.Length == nq.Length) return true;
            char next = np[nq.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }
        catch
        {
            return false;
        }
    }
}