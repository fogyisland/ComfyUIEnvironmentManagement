using System;
using System.Collections.Generic;
using System.IO;
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
/// v1.0.0.x: EnvOrphanReaper 单元测试 — 跟 EnvStartupStopperTests 同 pattern:
/// 用真 SQLite + 真 ProcessLauncher(env 不在 _running map 时 StopEnvAsync 走
/// no-op 路径),但 ListeningPidLookup / EnvOwnerCheck / Stopper 全 seam 注入。
///
/// **Repository identity note**:EnvironmentRepository 是 stateless(没 in-memory cache),
/// 每次 Get/ListAll 都从 DB materialize 新实例。所以测试不能直接 assert env.Status
/// (Upsert 写到 DB 后,调用方手里的 env 引用仍是旧值)— 必须 assert via repo.Get(env.Id)。
///
/// 覆盖:
/// - 空 DB → 0
/// - env 没 Port → skip
/// - port 没人监听 → skip
/// - port 有人监听但 EXE 不在 env.RootPath 下 → skip
/// - port + EXE 都匹配 → stopper 被调 + DB stopped + pid null
/// - stopper 抛异常 → DB 仍 stopped(idempotent 兜底)
/// - Windows 大小写不敏感 + trailing slash 容忍 + 边界 boundary check
/// - 多 env 同 port → 各自独立
/// - ListeningPidLookup 抛 → 不阻断下一个 env
/// </summary>
public sealed class EnvOrphanReaperTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly TestDb _db;

    public EnvOrphanReaperTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"env-orphan-reaper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _db = new TestDb();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private (EnvOrphanReaper svc, EnvironmentRepository repo) MakeService()
    {
        var repo = new EnvironmentRepository(_db.Factory);
        var processStateRepo = new ProcessStateRepository(_db.Factory);
        var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
        var svc = new EnvOrphanReaper(repo, launcher);
        return (svc, repo);
    }

    private static Environment MakeEnv(string id, string rootPath, int? port,
        string status = "running", int? pid = null)
    {
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = rootPath,
            Port = port,
            Status = status,
            Pid = pid,
        };
    }

    [Fact]
    public async Task Reap_NoEnvs_Returns0()
    {
        var (svc, _) = MakeService();

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(0, reaped);
    }

    [Fact]
    public async Task Reap_EnvWithoutPort_Skipped()
    {
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\Envs\e1", port: null);
        repo.Upsert(env);

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(0, reaped);
        Assert.Equal("running", repo.Get("e1")!.Status);
    }

    [Fact]
    public async Task Reap_PortNotListening_Skipped()
    {
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\Envs\env1", port: 7000);
        repo.Upsert(env);
        svc.ListeningPidLookup = _ => null;
        svc.EnvOwnerCheck = (_, _) => true;

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(0, reaped);
        Assert.Equal("running", repo.Get("e1")!.Status);
    }

    [Fact]
    public async Task Reap_PortListeningButExeOutsideRoot_Skipped()
    {
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\Envs\env1", port: 7000);
        repo.Upsert(env);
        svc.ListeningPidLookup = _ => 1234;
        svc.EnvOwnerCheck = (_, _) => false;   // EXE 不在 env1 下

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(0, reaped);
        Assert.Equal("running", repo.Get("e1")!.Status);
    }

    [Fact]
    public async Task Reap_PortAndExeMatch_StopperCalled_MarkedStopped()
    {
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\Envs\env1", port: 7000, pid: 1234);
        repo.Upsert(env);
        svc.ListeningPidLookup = _ => 1234;
        svc.EnvOwnerCheck = (_, _) => true;

        int stoppedPid = 0;
        string? stoppedEnvId = null;
        svc.Stopper = (e, _, _) =>
        {
            stoppedEnvId = e.Id;
            stoppedPid = e.Pid ?? -1;
            return Task.CompletedTask;
        };

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        Assert.Equal(1234, stoppedPid);
        Assert.Equal("e1", stoppedEnvId);
        var fresh = repo.Get("e1")!;
        Assert.Equal("stopped", fresh.Status);
        Assert.Null(fresh.Pid);
    }

    [Fact]
    public async Task Reap_PortablePythonWithEnvMainPy_StopperCalled()
    {
        // v1.0.0.x: 真场景重现 — PID 37732 = portable python + env main.py
        // MainModule 给的是 <projectRoot>/python/python.exe,不在 envRoot 下(规则 1 漏判);
        // 规则 2 启发式(EXE 是 shipped-portable + CommandLine 引用 envRoot)兜底识别为本 app。
        // Reaper 走全 seam:ListeningPidLookup 给 pid,ExePathLookup 给 portable,
        // CommandLineLookup 给 env cmdline,Stopper 验证被调。
        var envRoot = @"D:\Envs\faceswap";
        var (svc, repo) = MakeService();
        var env = MakeEnv("faceswap", envRoot, port: 7000, pid: 1234);
        repo.Upsert(env);

        svc.ListeningPidLookup = _ => 1234;
        svc.ExePathLookup = _ => @"D:\ToolDevelop\ComfyUI\python\python.exe";
        svc.CommandLineLookup = _ =>
            $@"""D:\ToolDevelop\ComfyUI\python\python.exe"" {envRoot}\main.py --port 7000";

        int stoppedPid = -1;
        svc.Stopper = (e, _, _) =>
        {
            stoppedPid = e.Pid ?? -1;
            return Task.CompletedTask;
        };

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        Assert.Equal(1234, stoppedPid);
        Assert.Equal("stopped", repo.Get("faceswap")!.Status);
        Assert.Null(repo.Get("faceswap")!.Pid);
    }

    [Fact]
    public async Task Reap_StopperThrows_StillMarkedStopped()
    {
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\Envs\env1", port: 7000);
        repo.Upsert(env);
        svc.ListeningPidLookup = _ => 1234;
        svc.EnvOwnerCheck = (_, _) => true;
        svc.Stopper = (_, _, _) => throw new InvalidOperationException("graceful stop failed");

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        var fresh = repo.Get("e1")!;
        Assert.Equal("stopped", fresh.Status);
        Assert.Null(fresh.Pid);
    }

    [Fact]
    public async Task Reap_PathCasingAndTrailingSlash_TreatedAsEqual()
    {
        // Windows NTFS case-insensitive + trailing separator 容忍。
        // 见 feedback_windows_case_insensitive_fs。
        var (svc, repo) = MakeService();
        var env = MakeEnv("e1", @"D:\envs\env1", port: 7000);
        repo.Upsert(env);
        svc.ListeningPidLookup = _ => 1234;
        svc.EnvOwnerCheck = (_, root) => EnvOrphanReaper.IsPathUnder(@"D:\Envs\Env1\.venv\Scripts\python.exe", root);
        svc.Stopper = (_, _, _) => Task.CompletedTask;

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        Assert.Equal("stopped", repo.Get("e1")!.Status);
    }

    [Fact]
    public async Task Reap_MultipleEnvs_HandledIndependently()
    {
        var (svc, repo) = MakeService();
        var env1 = MakeEnv("e1", @"D:\Envs\env1", port: 7000);
        var env2 = MakeEnv("e2", @"D:\Envs\env2", port: 7001);  // 不在用
        var env3 = MakeEnv("e3", @"D:\Envs\env3", port: 7002);  // EXE 不匹配
        repo.Upsert(env1);
        repo.Upsert(env2);
        repo.Upsert(env3);

        var stopCalls = new List<string>();
        svc.ListeningPidLookup = port => port switch
        {
            7000 => 1001,
            7002 => 1003,
            _ => null,
        };
        // 对 env1 EXE 在其下 → true;env3 EXE 在别处 → false。
        svc.EnvOwnerCheck = (pid, root) => (pid, root) switch
        {
            (1001, @"D:\Envs\env1") => true,
            (1003, @"D:\Envs\env3") => false,
            _ => false,
        };
        svc.Stopper = (e, _, _) =>
        {
            stopCalls.Add(e.Id);
            return Task.CompletedTask;
        };

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        Assert.Equal(new[] { "e1" }, stopCalls);
        Assert.Equal("stopped", repo.Get("e1")!.Status);
        Assert.Equal("running", repo.Get("e2")!.Status);  // 7001 不在用
        Assert.Equal("running", repo.Get("e3")!.Status);  // EXE 不匹配
    }

    [Fact]
    public async Task Reap_ListeningPidLookupThrows_ContinuesToNextEnv()
    {
        var (svc, repo) = MakeService();
        var env1 = MakeEnv("e1", @"D:\Envs\env1", port: 7000);
        var env2 = MakeEnv("e2", @"D:\Envs\env2", port: 7001);
        repo.Upsert(env1);
        repo.Upsert(env2);

        svc.ListeningPidLookup = port => port switch
        {
            7000 => throw new InvalidOperationException("iphlpapi 抽风"),
            7001 => 2002,
            _ => null,
        };
        svc.EnvOwnerCheck = (_, root) => root == @"D:\Envs\env2";
        svc.Stopper = (_, _, _) => Task.CompletedTask;

        var reaped = await svc.ReapOrphansAsync();

        Assert.Equal(1, reaped);
        Assert.Equal("running", repo.Get("e1")!.Status);  // 异常时 skip
        Assert.Equal("stopped", repo.Get("e2")!.Status);
    }

    [Fact]
    public void IsPathUnder_NullsAndEmpty_ReturnsFalse()
    {
        Assert.False(EnvOrphanReaper.IsPathUnder(null, @"C:\foo"));
        Assert.False(EnvOrphanReaper.IsPathUnder(@"C:\foo", null));
        Assert.False(EnvOrphanReaper.IsPathUnder(null, null));
        Assert.False(EnvOrphanReaper.IsPathUnder("", @"C:\foo"));
        Assert.False(EnvOrphanReaper.IsPathUnder(@"C:\foo", ""));
    }

    [Fact]
    public void IsPathUnder_DifferentSiblings_ReturnsFalse()
    {
        Assert.False(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1", @"D:\Envs\env2"));
    }

    [Fact]
    public void IsPathUnder_BoundaryMatch_ExactPathPrefix_ReturnsTrue()
    {
        Assert.True(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1\.venv\Scripts\python.exe",
                                              @"D:\Envs\env1"));
    }

    [Fact]
    public void IsPathUnder_NoBoundaryMatch_ReturnsFalse()
    {
        // D:\Envs\env1extra 名字以 env1 开头但不是 boundary,必须 false。
        Assert.False(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1extra\foo", @"D:\Envs\env1"));
    }

    [Fact]
    public void IsPathUnder_TrailingSlashVariants_TreatedEqual()
    {
        Assert.True(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1\.venv", @"D:\Envs\env1\"));
        Assert.True(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1\.venv", @"D:\Envs\env1"));
    }

    [Fact]
    public void IsPathUnder_CaseInsensitive_TreatedEqual()
    {
        Assert.True(EnvOrphanReaper.IsPathUnder(@"D:\ENVS\ENV1\.venv", @"D:\envs\env1"));
    }

    [Fact]
    public void IsPathUnder_SamePath_ReturnsTrue()
    {
        Assert.True(EnvOrphanReaper.IsPathUnder(@"D:\Envs\env1", @"D:\Envs\env1"));
    }
}
