using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.17.2: 启动时停掉运行中 env 测试 — EnvExitCleanupService(graceful 退出)
/// 的对称测试。
///
/// 验证:
/// - 空 DB / 全 stopped → 不做事
/// - running + alive → 调 Stopper + 翻 stopped
/// - running + dead(pid 已死或 null)→ 只翻 stopped,不调 Stopper
/// - 混合 → 只 stop alive 的
/// - Stopper 抛异常 → catch + 仍翻 stopped
/// - CancellationToken 取消 → OCE 抛出
/// - 没显式 IsAliveOverride 注入的单元测试走 <c>Process.GetProcessById</c>(pid
///   必须不存活于测试机器,默认无 env 进程 → 自然 false)
///
/// 跟 EnvExitCleanupServiceTests 同 pattern:用真 ProcessLauncher(env 不在 _running
/// map 时 StopEnvAsync 走 no-op 路径)— 本服务 Stopper seam 让测试侧完全控制。
/// </summary>
public sealed class EnvStartupStopperTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly TestDb _db;

    public EnvStartupStopperTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"env-startup-stopper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _db = new TestDb();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private (EnvStartupStopper svc, EnvironmentRepository repo) MakeService()
    {
        var repo = new EnvironmentRepository(_db.Factory);
        var processStateRepo = new ProcessStateRepository(_db.Factory);
        var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
        var svc = new EnvStartupStopper(repo, launcher);
        return (svc, repo);
    }

    private static Environment MakeEnv(string id, string status, int? pid = null) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = status,
            Pid = pid,
        };

    /// <summary>
    /// 空 DB → 不做事,return 0。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_NoEnvs_ReturnsZero()
    {
        var (svc, _) = MakeService();

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(0, stopped);
    }

    /// <summary>
    /// 全 stopped → 不做事。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_AllStopped_ReturnsZero()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "stopped"));
        repo.Upsert(MakeEnv("env-2", "stopped"));

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(0, stopped);
    }

    /// <summary>
    /// running + alive → 调 Stopper + 翻 stopped。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_RunningAlive_CallsStopper_AndFlipsStopped()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: 99999));
        svc.IsAliveOverride = pid => pid == 99999;
        var stopCalls = new List<string>();
        svc.Stopper = (env, _, _) =>
        {
            stopCalls.Add(env.Name);
            return Task.CompletedTask;
        };

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(1, stopped);
        Assert.Equal(new[] { "env-1" }, stopCalls);
        Assert.Equal("stopped", repo.Get("env-1")!.Status);
    }

    /// <summary>
    /// running + 进程已死(pid 99999 在测试机不存在)→ 跳过 Stopper 调用,
    /// 但仍翻 status=stopped(DB 一致性)。IsAliveOverride = null → 走默认
    /// Process.GetProcessById(99999)抛 ArgumentException → false。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_RunningDead_FlipsStoppedWithoutCallingStopper()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: 99999));  // 不存在
        var stopCalls = new List<string>();
        svc.Stopper = (env, _, _) =>
        {
            stopCalls.Add(env.Name);
            return Task.CompletedTask;
        };

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(0, stopped);  // 没真正 stop 任何 env
        Assert.Empty(stopCalls);  // Stopper 不被调
        Assert.Equal("stopped", repo.Get("env-1")!.Status);  // 但 status 翻 stopped
    }

    /// <summary>
    /// running + null pid(脏数据)→ 视为 stale,翻 stopped,不调 Stopper。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_RunningNullPid_FlipsStoppedWithoutCallingStopper()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: null));
        var stopCalls = new List<string>();
        svc.Stopper = (env, _, _) =>
        {
            stopCalls.Add(env.Name);
            return Task.CompletedTask;
        };

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(0, stopped);
        Assert.Empty(stopCalls);
        Assert.Equal("stopped", repo.Get("env-1")!.Status);
    }

    /// <summary>
    /// 混合:2 alive + 1 dead → 只停 alive 的 2 个,翻全部 3 个 status=stopped。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_Mixed_StopsAliveOnly_FlipsAll()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("alive-1", "running", pid: 11111));
        repo.Upsert(MakeEnv("alive-2", "running", pid: 22222));
        repo.Upsert(MakeEnv("dead-1", "running", pid: 99999));  // 不存在
        repo.Upsert(MakeEnv("stopped-1", "stopped"));
        svc.IsAliveOverride = pid => pid is 11111 or 22222;
        var stopCalls = new List<string>();
        svc.Stopper = (env, _, _) =>
        {
            stopCalls.Add(env.Name);
            return Task.CompletedTask;
        };

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(2, stopped);
        Assert.Equal(2, stopCalls.Count);
        var stoppedNames = stopCalls.ToHashSet();
        Assert.Contains("alive-1", stoppedNames);
        Assert.Contains("alive-2", stoppedNames);
        Assert.DoesNotContain("dead-1", stoppedNames);
        // 全部 status 翻 stopped
        Assert.Equal("stopped", repo.Get("alive-1")!.Status);
        Assert.Equal("stopped", repo.Get("alive-2")!.Status);
        Assert.Equal("stopped", repo.Get("dead-1")!.Status);
        Assert.Equal("stopped", repo.Get("stopped-1")!.Status);  // 已 stopped 的不变
    }

    /// <summary>
    /// Stopper 抛异常 → catch + 仍翻 stopped(不 rethrow)。保证 DB 一致性 + 不阻塞启动。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_StopperThrows_StillFlipsStopped()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: 11111));
        svc.IsAliveOverride = pid => pid == 11111;
        svc.Stopper = (_, _, _) => throw new InvalidOperationException("stop 失败模拟");

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(1, stopped);  // 算"尝试过"
        Assert.Equal("stopped", repo.Get("env-1")!.Status);  // 仍翻 stopped
    }

    /// <summary>
    /// CancellationToken 在循环里 cancel → OperationCanceledException 抛出(不吞)。
    /// 让调用方知道启动清理被中断。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_Cancelled_Throws()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: 11111));
        repo.Upsert(MakeEnv("env-2", "running", pid: 22222));
        svc.IsAliveOverride = _ => true;
        svc.Stopper = (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.StopRunningOnStartupAsync(cts.Token));
    }

    /// <summary>
    /// 没显式 IsAliveOverride + pid 不存在 → 走 Process.GetProcessById 抛 → false。
    /// 端到端确认默认进程检查路径不会被吞。
    /// </summary>
    [Fact]
    public async Task StopRunningOnStartupAsync_DefaultIsAliveCheck_UsesGetProcessById()
    {
        var (svc, repo) = MakeService();
        repo.Upsert(MakeEnv("env-1", "running", pid: 99999));
        var stopCalls = new List<string>();
        svc.Stopper = (env, _, _) =>
        {
            stopCalls.Add(env.Name);
            return Task.CompletedTask;
        };

        var stopped = await svc.StopRunningOnStartupAsync();

        Assert.Equal(0, stopped);
        Assert.Empty(stopCalls);
        Assert.Equal("stopped", repo.Get("env-1")!.Status);
    }
}