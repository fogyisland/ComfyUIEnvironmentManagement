using System;
using System.Diagnostics;
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
/// v1.0.0.x hotfix:EXE 不在 env.RootPath 下时,也用 shipped-portable + CommandLine
/// 启发式兜底(见 <see cref="EnvPortProbe.IsEnvProcessOwned"/> 规则 2)。
/// 不破坏现有 <see cref="EnvOwnerCheck"/> seam,新增 <see cref="ExePathLookup"/> +
/// <see cref="CommandLineLookup"/> seam,Reaper 在 ownerCheck 循环外 try/finally 临时
/// 注入到 <see cref="EnvPortProbe"/> static seam,循环结束还原(避免把 EnvPortProbe 重构
/// 为 instance class 触发全调用链改动;启动期单次调用,static mutable 风险低)。
///
/// 测试 seam(全部可注入 Func,默认走真实实现):
/// - <see cref="ListeningPidLookup"/>: int port → int? pid(默认 <see cref="EnvPortProbe.GetListeningPidByPort"/>)
/// - <see cref="EnvOwnerCheck"/>: (int pid, string envRootPath) → bool(默认 <see cref="EnvPortProbe.IsEnvProcessOwned"/>)
/// - <see cref="ExePathLookup"/>: int pid → string? exe 路径(默认 <see cref="EnvPortProbe.GetExePathByPid"/> 默认实现)
/// - <see cref="CommandLineLookup"/>: int pid → string? cmdline(默认 <see cref="EnvPortProbe.GetProcessCommandLine"/> WMI)
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
    /// v1.0.0.x 测试 seam: pid → EXE 路径(注入到 <see cref="EnvPortProbe.ExePathLookup"/>)。
    /// null → EnvPortProbe 走默认 <c>Process.GetProcessById + MainModule</c> 实现。
    /// </summary>
    public Func<int, string?>? ExePathLookup { get; set; }

    /// <summary>
    /// v1.0.0.x 测试 seam: pid → CommandLine(注入到 <see cref="EnvPortProbe.CommandLineLookup"/>)。
    /// null → EnvPortProbe 走默认 WMI <c>Win32_Process.CommandLine</c> 实现。
    /// </summary>
    public Func<int, string?>? CommandLineLookup { get; set; }

    /// <summary>
    /// 测试 seam: 单 env stop delegate。null → 走 <see cref="ProcessLauncher.StopEnvAsync"/>。
    /// </summary>
    public Func<Environment, int, CancellationToken, Task>? Stopper { get; set; }

    /// <summary>
    /// 跑一次启动期孤儿扫描 + 杀进程。返回"实际尝试过 stop"的 env 数(成功失败都算)。
    /// 不抛 — try/catch 在内部所有路径,只 warn 写日志。
    ///
    /// v1.0.0.x: 在循环外 try/finally 临时把 <see cref="ExePathLookup"/> +
    /// <see cref="CommandLineLookup"/> 注入到 <see cref="EnvPortProbe"/> 的 static seam,
    /// 让 <see cref="EnvPortProbe.IsEnvProcessOwned"/> 走 shipped-portable + CommandLine
    /// 启发式(规则 2);finally 还原 prev 值,避免污染下一次调用 / 其他 caller。
    /// </summary>
    public async Task<int> ReapOrphansAsync(CancellationToken ct = default)
    {
        var envs = _envRepo.ListAll().Where(e => e.Port.HasValue).ToList();
        if (envs.Count == 0) return 0;

        var pidLookup = ListeningPidLookup ?? EnvPortProbe.GetListeningPidByPort;
        var stopper = Stopper ?? ((env, t, c) => _launcher.StopEnvAsync(env, t, c));

        // v1.0.0.x:try/finally 临时注入 EnvPortProbe static seam — 兜底 shipped-portable
        // 启发式。不破坏 EnvOwnerCheck 直接注入的旧路径:EnvOwnerCheck 非 null 时直接用,
        // 根本不读 static seam(避免浪费 WMI 调用 + 防止既有测试被新逻辑影响)。
        var prevExe = EnvPortProbe.ExePathLookup;
        var prevCmd = EnvPortProbe.CommandLineLookup;
        int reaped = 0;
        try
        {
            if (EnvOwnerCheck is null)
            {
                // 只在 Reaper 自己被注入 lookup 时才覆盖 EnvPortProbe 的 static seam — production
                // 无 test injection 时这两个都是 null,**绝不能覆盖** EnvPortProbe 的默认实现
                // (尤其 CommandLineLookup 静态初始化时绑定 DefaultGetProcessCommandLine — null
                // 覆盖会导致 GetProcessCommandLine 内 lookup(pid) NRE,catch 后返 null,rule 2 失效)。
                if (ExePathLookup is not null) EnvPortProbe.ExePathLookup = ExePathLookup;
                if (CommandLineLookup is not null) EnvPortProbe.CommandLineLookup = CommandLineLookup;
            }

            var ownerCheck = EnvOwnerCheck ?? EnvPortProbe.IsEnvProcessOwned;

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

                // owned by env → 上次会话孤儿。优先 graceful stopper(<c>ProcessLauncher.StopEnvAsync</c>
                // — 仅当 entry 在 _running map 里才有效,即 launcher 启动的 env);对**外部进程**启的
                // orphan(stopped 状态、entry 不在 map),stopper 只清 DB 不杀进程,所以**必须** fallback
                // hard-kill pid 本身 — 不然 Reaper 跑完 PID 37732 仍活着。
                bool stopped = false;
                try
                {
                    await stopper(env, 5, ct).ConfigureAwait(false);
                    // 即使 stopper 走 DB cleanup 路径没真杀,我们也走 hard-kill 兜底(下面 kill 失败时
                    // 才会落到"已被 launcher kill"分支)— orphan 进程必须确定终止,不能光靠 DB 状态。
                    stopped = TryHardKill(pid.Value, env.Name, _logger);
                    _logger?.Info("env-orphan-reap",
                        $"env='{env.Name}' port={port} pid={pid} 启动期孤儿处理完毕(stopper+hard-kill)");
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
                _ = stopped; // 当前 caller 不基于 stopped 走分支 — log 已记录。

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
        }
        finally
        {
            EnvPortProbe.ExePathLookup = prevExe;
            EnvPortProbe.CommandLineLookup = prevCmd;
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

    /// <summary>
    /// v1.0.0.x hotfix: 启动期 orphan hard-kill — <see cref="ProcessLauncher.StopEnvAsync"/> 对
    /// 不在 _running map 里的 orphan 进程只清 DB 不杀进程(它的设计假设是 launcher 启动的进程,
    /// entry 在 _running 里),所以 Reaper 必须自己 hard-kill 兜底,否则 PID 37732 一直活着。
    /// <para>策略:Process.GetProcessById(pid).Kill() + WaitForExit(2s)— orphan 是 dev / portable
    /// 启动的,不需要走 graceful(graceful shutdown 也走不到 — 它们没注册本 app 的 graceful handler)。</para>
    /// <para>失败一律返 false 不上抛(进程已退 / 权限不足 / Win32Exception)— 启动期必须 fail-safe。</para>
    /// </summary>
    internal static bool TryHardKill(int pid, string envName, AppLogger? logger)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                logger?.Info("env-orphan-reap", $"env='{envName}' pid={pid} 已退出,跳过 hard-kill");
                return true;
            }
            proc.Kill(entireProcessTree: true);
            if (proc.WaitForExit(2000))
            {
                logger?.Info("env-orphan-reap", $"env='{envName}' pid={pid} hard-kill 成功");
                return true;
            }
            logger?.Warn("env-orphan-reap", $"env='{envName}' pid={pid} hard-kill 后 2s 未退");
            return false;
        }
        catch (ArgumentException)
        {
            // 进程不存在 — 已被 stopper / OS 自动退。
            logger?.Info("env-orphan-reap", $"env='{envName}' pid={pid} 已被外部 stop,跳过 hard-kill");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn("env-orphan-reap",
                $"env='{envName}' pid={pid} hard-kill 失败:{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}