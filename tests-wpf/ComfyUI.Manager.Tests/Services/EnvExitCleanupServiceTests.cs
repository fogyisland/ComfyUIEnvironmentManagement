using System;
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
/// v0.6.14 T6: 退出清理 service 测试。验证:
/// - 空 DB → 不做事,返 0
/// - 多个 running env → 顺序停 + 翻 status
/// - 单个 stop 失败 → 仍然翻 status(兜底 DB 一致性)
/// - Cancellation → 当前 env 之后不再处理
///
/// 跟 EnvDeleterServiceTests 同 pattern:用真 ProcessLauncher(env 不在 _running map 时
/// StopEnvAsync 走 no-op 路径,不会真起进程)。
/// </summary>
public class EnvExitCleanupServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly TestDb _db;

    public EnvExitCleanupServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "env-exit-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
        _db = new TestDb();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private (EnvExitCleanupService svc, EnvironmentRepository repo, ProcessLauncher launcher) MakeService()
    {
        var repo = new EnvironmentRepository(_db.Factory);
        var processStateRepo = new ProcessStateRepository(_db.Factory);
        var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
        var svc = new EnvExitCleanupService(repo, launcher);
        return (svc, repo, launcher);
    }

    private static Environment MakeRunningEnv(string id) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = "running",
        };

    private static Environment MakeStoppedEnv(string id) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = "stopped",
        };

    [Fact]
    public async Task ShutdownRunningEnvsAsync_NoRunningEnvs_ReturnsZero()
    {
        var (svc, repo, _) = MakeService();
        // 2 个 stopped env — 不该被处理
        repo.Upsert(MakeStoppedEnv("env-stopped-1"));
        repo.Upsert(MakeStoppedEnv("env-stopped-2"));

        var count = await svc.ShutdownRunningEnvsAsync();

        Assert.Equal(0, count);
        // 确认两个 env 仍为 stopped,没被翻状态
        Assert.All(repo.ListAll(), e => Assert.Equal("stopped", e.Status));
    }

    [Fact]
    public async Task ShutdownRunningEnvsAsync_TwoRunningEnvs_StopsBoth_MarksStopped()
    {
        var (svc, repo, _) = MakeService();
        repo.Upsert(MakeRunningEnv("env-a"));
        repo.Upsert(MakeRunningEnv("env-b"));

        var count = await svc.ShutdownRunningEnvsAsync();

        Assert.Equal(2, count);
        var rows = repo.ListAll().OrderBy(e => e.Name).ToList();
        Assert.All(rows, e => Assert.Equal("stopped", e.Status));
    }

    [Fact]
    public async Task ShutdownRunningEnvsAsync_OneStopFails_ContinuesToNext_StillUpdatesStopped()
    {
        // v0.6.14 R1: 用 Stopper seam 注入会抛的 fake —— 默认 _launcher.StopEnvAsync
        // 不会 throw(env 不在 _running map 里走 no-op),没法测真实的失败路径。
        // 注入让 env-x 抛 InvalidOperationException、env-y 走正常路径,验证:
        // 1) service 不 rethrow
        // 2) 两个 env 都翻成 stopped(StopEnvAsync 失败那个也翻)
        // 3) 两个 env 都被处理(throw 没中断循环)
        var (svc, repo, _) = MakeService();
        repo.Upsert(MakeRunningEnv("env-x"));
        repo.Upsert(MakeRunningEnv("env-y"));

        svc.Stopper = (env, _, _) =>
        {
            if (env.Id == "env-x")
                throw new InvalidOperationException("simulated launcher failure");
            return Task.CompletedTask;
        };

        var count = await svc.ShutdownRunningEnvsAsync();

        Assert.Equal(2, count);
        var rows = repo.ListAll().OrderBy(e => e.Name).ToList();
        Assert.All(rows, e => Assert.Equal("stopped", e.Status));
    }

    [Fact]
    public async Task ShutdownRunningEnvsAsync_CancellationRequested_StopsAfterCurrent()
    {
        // 取消 token 在第一个 StopEnvAsync 之前 cancel → service 应该走 OperationCanceledException
        // 路径并 throw。Status 不应该被翻(在 catch 之前已经过 StopEnvAsync,StopEnvAsync
        // 内部 try/catch 吞了 cancellation,但 service 外层 throw 前不会再写 status)。
        //
        // 关键不变量:cancellation 应被正确 propagate。
        var (svc, repo, _) = MakeService();
        repo.Upsert(MakeRunningEnv("env-cancel-1"));
        repo.Upsert(MakeRunningEnv("env-cancel-2"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.ShutdownRunningEnvsAsync(cts.Token));
    }
}