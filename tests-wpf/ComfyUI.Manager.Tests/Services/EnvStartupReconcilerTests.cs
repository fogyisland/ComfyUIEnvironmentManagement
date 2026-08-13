using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.14 T7: 启动 reconcile stale-running envs 测试。
///
/// 验证:
/// - 空 DB / 全 stopped → 不做事
/// - stale-running (Process.GetProcessById 死) → 翻 stopped + 清 pid
/// - alive-running → 不动
/// - 混合 → 只翻 stale
/// - null pid → 视为 stale
/// - 每个 reconcile 都打 [env-reconcile] warn 日志
/// </summary>
public sealed class EnvStartupReconcilerTests : IDisposable
{
    private readonly string _projectRoot;

    public EnvStartupReconcilerTests()
    {
        // 给 AppLogger 真实路径,这样 ReadLines() 能拿到 warn 行
        _projectRoot = Path.Combine(Path.GetTempPath(), $"env-startup-reconciler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
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

    [Fact]
    public void ReconcileStaleRunning_NoEnvs_ReturnsZero()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var svc = new EnvStartupReconciler(repo);

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(0, reconciled);
        Assert.Empty(repo.ListAll());
    }

    [Fact]
    public void ReconcileStaleRunning_AllEnvsStopped_NoChanges()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-a", "stopped"));
        repo.Upsert(MakeEnv("env-b", "stopped"));
        repo.Upsert(MakeEnv("env-c", "stopped"));
        var svc = new EnvStartupReconciler(repo);

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(0, reconciled);
        Assert.All(repo.ListAll(), e =>
        {
            Assert.Equal("stopped", e.Status);
            Assert.Null(e.Pid);
        });
    }

    [Fact]
    public void ReconcileStaleRunning_OneStaleRunning_MarksStopped_ReturnsOne()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-a", "running", pid: 99999));
        var svc = new EnvStartupReconciler(repo)
        {
            // 99999 在测试环境基本不可能是活进程 — 但为防极端情况,显式 override 死。
            IsAliveOverride = _ => false,
        };

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(1, reconciled);
        var env = repo.Get("env-a")!;
        Assert.Equal("stopped", env.Status);
        Assert.Null(env.Pid);
    }

    [Fact]
    public void ReconcileStaleRunning_OneAliveRunning_LeavesAlone_ReturnsZero()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-a", "running", pid: 12345));
        var svc = new EnvStartupReconciler(repo)
        {
            IsAliveOverride = _ => true,
        };

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(0, reconciled);
        var env = repo.Get("env-a")!;
        Assert.Equal("running", env.Status);
        Assert.Equal(12345, env.Pid);
    }

    [Fact]
    public void ReconcileStaleRunning_MixedAliveAndStale_MarksOnlyStale()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-alive", "running", pid: 111));
        repo.Upsert(MakeEnv("env-stale", "running", pid: 222));
        var svc = new EnvStartupReconciler(repo)
        {
            // 仅 env-alive 视为 alive,env-stale 视为 dead
            IsAliveOverride = pid => pid == 111,
        };

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(1, reconciled);
        var alive = repo.Get("env-alive")!;
        Assert.Equal("running", alive.Status);
        Assert.Equal(111, alive.Pid);
        var stale = repo.Get("env-stale")!;
        Assert.Equal("stopped", stale.Status);
        Assert.Null(stale.Pid);
    }

    [Fact]
    public void ReconcileStaleRunning_NullPid_TreatedAsStale()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        // status="running" 但 pid=null — 应视为 stale(no override 走到 default,默认 null → false → stale)
        repo.Upsert(MakeEnv("env-a", "running", pid: null));
        var svc = new EnvStartupReconciler(repo);
        // 注意: IsAliveOverride 故意不设,验证 default impl 把 null 视为 stale

        var reconciled = svc.ReconcileStaleRunning();

        Assert.Equal(1, reconciled);
        var env = repo.Get("env-a")!;
        Assert.Equal("stopped", env.Status);
        Assert.Null(env.Pid);
    }

    [Fact]
    public void ReconcileStaleRunning_LogsWarnPerReconciledEnv()
    {
        // 用真实 AppLogger + 临时目录(同 AppLoggerTests pattern)验证 warn 行真的写出。
        // FakeAppLogger 不存在(项目目前没建过 fake),所以用真 logger + ReadLines() 验内容。
        using var logger = new AppLogger(_projectRoot);
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-a", "running", pid: 99999));
        var svc = new EnvStartupReconciler(repo, logger)
        {
            IsAliveOverride = _ => false,
        };

        svc.ReconcileStaleRunning();

        var lines = logger.ReadLines();
        // 至少 1 行 [env-reconcile] warn(每个 reconciled env 一行)+ 1 行 info 总结
        var warnLines = lines.Where(l => l.Contains("[env-reconcile]") && l.Contains("[WARN")).ToList();
        Assert.NotEmpty(warnLines);
        Assert.Contains(warnLines, l => l.Contains("env='env-a'") && l.Contains("标 stopped"));
        // 总结行:info + "启动 reconcile 完成"
        Assert.Contains(lines, l =>
            l.Contains("[env-reconcile]") && l.Contains("[INFO") && l.Contains("启动 reconcile 完成"));
    }
}
