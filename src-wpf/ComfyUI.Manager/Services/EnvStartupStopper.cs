using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.17.2: 启动时停掉所有 running + 进程活着的 env — <see cref="EnvExitCleanupService"/>
/// 的对称(退出 graceful 停,启动 also 停)。
///
/// 设计动机:用户原话"环境管理之前应该中止运行环境,然后开启不会自动启动,需要手动
/// 启动才可以"。Manager 是 env 生命周期的"拥有者",每次重启应该回到 clean slate —
/// 不该有上次会话漏出来的运行实例(用户也看不到它们的启动日志)。
///
/// 跟 <see cref="EnvStartupReconciler"/> 的差异:
/// - Reconciler 只标 stale(running 但进程已死 → stopped),**不动活着的进程**
/// - Stopper **主动停活着的进程**(同 EnvExitCleanupService.ShutdownRunningEnvsAsync
///   的 graceful 路径:CloseMainWindow + 5s timeout)
/// - 顺序:Reconciler 先跑(廉价 no-I/O 标记 stale)→ Stopper 再跑(可能停进程)
///   — 避免 Stopper 停完后又让 Reconciler 看到新死的进程导致二次 Upsert
///
/// 测试 seam:
/// - <see cref="IsAliveOverride"/> — 默认 <c>Process.GetProcessById</c>,测试注入 fake
/// - <see cref="Stopper"/> — 默认 <c>_launcher.StopEnvAsync</c>,测试注入 fake
/// </summary>
public sealed class EnvStartupStopper
{
    private readonly IEnvironmentRepository _envRepo;
    private readonly ProcessLauncher _launcher;
    private readonly AppLogger? _logger;

    public EnvStartupStopper(
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
    /// 测试 seam: 进程存活检查。null → 走默认 <see cref="Process.GetProcessById"/>。
    /// </summary>
    internal Func<int?, bool>? IsAliveOverride { get; set; }

    /// <summary>
    /// 测试 seam: 单 env stop delegate。null → 走 <see cref="ProcessLauncher.StopEnvAsync"/>。
    /// </summary>
    internal Func<Environment, int, CancellationToken, Task>? Stopper { get; set; }

    /// <summary>
    /// 顺序停掉所有 <c>status="running"</c> + 进程活着的 env,翻 status="stopped"。
    /// 进程已死的(env.Status="running" 但 pid 已 null 或进程不存在)会被跳过 —
    /// 这种情况 <see cref="EnvStartupReconciler"/> 已经处理过。
    /// 返回成功"尝试 stop"过的 env 数(stop 失败的也算 — 仍翻 stopped)。
    /// </summary>
    public async Task<int> StopRunningOnStartupAsync(CancellationToken ct = default)
    {
        var running = _envRepo.ListAll()
            .Where(e => string.Equals(e.Status, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (running.Count == 0) return 0;

        _logger?.Info("env-startup-stop",
            $"检测到 {running.Count} 个 running env,启动时主动停掉(保持 clean slate)");
        var stopper = Stopper ?? ((env, timeout, c) => _launcher.StopEnvAsync(env, timeout, c));
        var stopped = 0;
        foreach (var env in running)
        {
            ct.ThrowIfCancellationRequested();

            // 进程已死 → 不调 StopEnvAsync(stale path,留给 Reconciler 处理或
            // 这里顺手翻 stopped)。Stale env 的 status 翻 stopped 即可。
            if (!IsProcessAlive(env.Pid))
            {
                try
                {
                    var fresh = _envRepo.Get(env.Id) ?? env;
                    fresh.Status = "stopped";
                    _envRepo.Upsert(fresh);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("env-startup-stop",
                        $"env='{env.Name}' stale running 状态写回失败:{ex.GetType().Name}: {ex.Message}");
                }
                continue;
            }

            // 进程活着 → 主动停。
            try
            {
                await stopper(env, 5, ct).ConfigureAwait(false);
                _logger?.Info("env-startup-stop", $"启动时停掉运行中 env='{env.Name}' pid={env.Pid}");
            }
            catch (OperationCanceledException)
            {
                _logger?.Warn("env-startup-stop", $"env='{env.Name}' 启动时停止取消");
                throw;
            }
            catch (Exception ex)
            {
                // stop 失败 — 静默继续 + 仍翻 stopped(stop 失败的 env 也保证 DB 一致)。
                _logger?.Warn("env-startup-stop",
                    $"env='{env.Name}' 启动时停止失败:{ex.GetType().Name}: {ex.Message}");
            }

            // 翻 status=stopped(idempotent + 兜底 stop 失败的)。
            try
            {
                var fresh = _envRepo.Get(env.Id) ?? env;
                fresh.Status = "stopped";
                _envRepo.Upsert(fresh);
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-startup-stop",
                    $"env='{env.Name}' 启动时状态写回失败:{ex.GetType().Name}: {ex.Message}");
            }
            stopped++;
        }
        if (stopped > 0)
        {
            _logger?.Info("env-startup-stop", $"启动清理完成,主动停了 {stopped} 个运行中 env");
        }
        return stopped;
    }

    private bool IsProcessAlive(int? pid)
    {
        if (IsAliveOverride is not null) return IsAliveOverride(pid);
        if (!pid.HasValue) return false;
        try
        {
            var p = Process.GetProcessById(pid.Value);
            p.Dispose();
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}