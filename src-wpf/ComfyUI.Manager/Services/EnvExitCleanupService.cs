using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.14 T6: 退出清理服务 — 用户点 X 关闭主窗口时,graceful 停掉所有 running env
/// 并把 SQLite 状态翻成 "stopped"。替代 <c>App.OnExit</c> 的 force-kill,
/// 让 ComfyUI 进程有机会 CloseMainWindow() 干净退出(写 workflow / save state)。
///
/// 设计:
/// - 顺序停止(parallel 会撞 port / file lock),每个 env 最多等 5s
///   (StopEnvAsync 的 CloseMainWindow + kill tree);超时的 env 由
///   <c>App.OnExit → _launcher.Dispose()</c> force-kill 兜底
/// - 每次 StopEnvAsync 之后 Upsert env.Status="stopped",idempotent —— 即使
///   launcher 内部已经写过一遍,这步保证 status 一定翻成 stopped
/// - 测试 seam <see cref="ConfirmShutdown"/> 让测试侧弹 confirm 不阻塞 STA
/// - v0.6.14 R1: 测试 seam <see cref="Stopper"/> 让 OneStopFails 等失败路径
///   测试可注入 throwing fake(默认走真实 _launcher.StopEnvAsync)
/// </summary>
public sealed class EnvExitCleanupService
{
    /// <summary>
    /// v0.6.14 R1: 单方法 delegate seam —— 让测试可注入会抛异常的 fake,
    /// 验证 ShutdownRunningEnvsAsync 在单 env StopEnvAsync 抛时仍能:
    /// 1) catch 异常不 rethrow(OCE 例外)
    /// 2) 继续处理剩余 env
    /// 3) 仍把每个 env Status 翻成 stopped
    /// 默认 = <c>_launcher.StopEnvAsync(env, 5, ct)</c>;null = 同默认(显式重置)。
    /// </summary>
    internal Func<Environment, int, CancellationToken, Task>? Stopper { get; set; }

    private readonly EnvironmentRepository _envRepo;
    private readonly ProcessLauncher _launcher;
    private readonly AppLogger? _logger;

    public EnvExitCleanupService(
        EnvironmentRepository envRepo,
        ProcessLauncher launcher,
        AppLogger? logger = null)
    {
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger;
        Stopper = (env, timeout, ct) => _launcher.StopEnvAsync(env, timeout, ct);
    }

    /// <summary>
    /// v0.6.14 T6 test seam: confirm dialog. Returns true = proceed (shut down and exit),
    /// false = cancel exit. When null, default impl uses <see cref="MessageBox.Show"/>
    /// with YesNo buttons. Production: nullable; tests inject override.
    /// </summary>
    public Func<int, bool>? ConfirmShutdown { get; set; }

    /// <summary>
    /// v0.6.14 R1: 暴露内部 logger,让 <c>MainWindow.OnClosing</c> 异步 cleanup
    /// 跑异常时能用同一份 logger 写 <c>[env-exit-failed]</c> 标签(不再静默吞)。
    /// </summary>
    public AppLogger? Logger => _logger;

    /// <summary>
    /// 顺序停掉所有 <c>status="running"</c> 的 env,Upsert 成 stopped。
    /// 返回成功"尝试过"的 env 数(包括 stop 失败的 — 都会翻 status)。
    /// CancellationToken 由调用方控制;<c>App.OnExit</c> force-kill 兜底超时,
    /// 不在这里加额外超时(避免双重超时语义)。
    /// </summary>
    public async Task<int> ShutdownRunningEnvsAsync(CancellationToken ct = default)
    {
        var running = _envRepo.ListAll()
            .Where(e => string.Equals(e.Status, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (running.Count == 0) return 0;

        _logger?.Info("env-exit", $"开始退出清理,共 {running.Count} 个 running env");
        var stopper = Stopper ?? ((env, timeout, c) => _launcher.StopEnvAsync(env, timeout, c));
        var count = 0;
        foreach (var env in running)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await stopper(env, 5, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.Warn("env-exit", $"env='{env.Name}' 退出取消,后续不再处理");
                throw;
            }
            catch (Exception ex)
            {
                // stopper 失败(process 已死 / 信号失败)— 静默继续,下一个 env。
                // 我们仍然要把 status 翻成 stopped(DB 一致性)。
                _logger?.Warn("env-exit", $"env='{env.Name}' StopEnvAsync 异常:{ex.GetType().Name}: {ex.Message}");
            }

            // StopEnvAsync 内部已经翻 status="stopped" 了,这里再翻一次是 idempotent
            // + 兜底(stop 失败的 env 也保证 status 翻对)。
            try
            {
                var fresh = _envRepo.Get(env.Id) ?? env;
                fresh.Status = "stopped";
                _envRepo.Upsert(fresh);
            }
            catch (Exception ex)
            {
                _logger?.Warn("env-exit", $"env='{env.Name}' 状态写回失败:{ex.GetType().Name}: {ex.Message}");
            }
            _logger?.Info("env-exit", $"shutdown {env.Name}");
            count++;
        }
        _logger?.Info("env-exit", $"退出清理完成,处理 {count} 个 env");
        return count;
    }

    /// <summary>
    /// 默认 confirm dialog 实现: 弹 <see cref="MessageBox"/> YesNo,Yes=proceed。
    /// 测试通过 <see cref="ConfirmShutdown"/> override 绕过。
    /// </summary>
    internal bool DefaultConfirm(int runningCount)
    {
        var owner = Application.Current?.MainWindow;
        var text = runningCount == 1
            ? "有 1 个环境正在运行,关闭主窗口时一并停止该进程并退出?"
            : $"有 {runningCount} 个环境正在运行,关闭主窗口时一并停止这些进程并退出?";
        var result = owner is not null
            ? MessageBox.Show(owner, text, "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }
}