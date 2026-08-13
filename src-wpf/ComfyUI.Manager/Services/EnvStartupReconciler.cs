using System;
using System.Diagnostics;
using System.Linq;
using ComfyUI.Manager.Data;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.14 T7: 启动时 stale-running reconcile — 处理上次 crash / hard-kill / OOM
/// / 断电留下的脏状态(env.Status = "running" 但进程已死)。
///
/// 用户原话"启动的时候节点不自动启动":只 reconcile,绝不 auto-start。
/// 这是 T6(<see cref="EnvExitCleanupService"/>)的对称: T6 走 graceful 关闭,
/// 本 service 走 dirty crash 后的清理。
///
/// 设计:
/// - 同步(无 async / 无 subprocess)—<c>Process.GetProcessById</c> 立即返结果
/// - 不依赖 <c>ProcessLauncher</c>(它管的是"启动中"的 running map;reconcile
///   时 launcher 还没起 / DB 状态可能跟 launcher 内部不一致)
/// - <see cref="IsAliveOverride"/> 是测试 seam(同 T6 R1 的 Stopper pattern)
///   — 默认走 <c>Process.GetProcessById</c>,测试注入 fake alive/dead
/// </summary>
public sealed class EnvStartupReconciler
{
    private readonly IEnvironmentRepository _envRepo;
    private readonly AppLogger? _logger;

    public EnvStartupReconciler(IEnvironmentRepository envRepo, AppLogger? logger = null)
    {
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _logger = logger;
    }

    /// <summary>
    /// v0.6.14 T7 test seam: 让测试侧注入 fake alive/dead 决策,无需真起进程。
    /// 默认实现走 <see cref="Process.GetProcessById(int)"/>;null = 同默认。
    /// 跟 <see cref="EnvExitCleanupService.Stopper"/> 同 pattern。
    /// </summary>
    public Func<int?, bool>? IsAliveOverride { get; set; }

    /// <summary>
    /// 把 <c>status="running"</c> 但进程已死的 env 翻成 <c>"stopped"</c> + 清 pid。
    /// 返回 reconcile 过的 env 数。
    /// </summary>
    public int ReconcileStaleRunning()
    {
        var running = _envRepo.ListAll().Where(e => e.Status == "running").ToList();
        int reconciled = 0;
        foreach (var env in running)
        {
            if (!IsProcessAlive(env.Pid))
            {
                _logger?.Warn("env-reconcile",
                    $"env='{env.Name}' pid={env.Pid?.ToString() ?? "null"} 状态为 running 但进程已死 → 标 stopped");
                env.Status = "stopped";
                env.Pid = null;
                _envRepo.Upsert(env);
                reconciled++;
            }
        }
        if (reconciled > 0)
        {
            _logger?.Info("env-reconcile", $"启动 reconcile 完成,{reconciled} 个 stale running env 标 stopped");
        }
        return reconciled;
    }

    /// <summary>
    /// v0.6.14 T7: 进程存活检查。<see cref="IsAliveOverride"/> 非 null 时走 override;
    /// null pid → false(null 视为 stale,reconcile 时翻 stopped);
    /// 否则 <see cref="Process.GetProcessById(int)"/> 抛 ArgumentException /
    /// InvalidOperationException 即视为 dead。
    /// </summary>
    private bool IsProcessAlive(int? pid)
    {
        if (IsAliveOverride is not null) return IsAliveOverride(pid);
        if (!pid.HasValue) return false;
        try
        {
            var p = Process.GetProcessById(pid.Value);
            // GetProcessById 抛 ArgumentException 表明进程不存在;到了这里 = alive。
            // 立即 Dispose 释放 handle,避免长期挂着的 process snapshot。
            p.Dispose();
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
