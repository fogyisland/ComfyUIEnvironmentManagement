using System;
using System.IO;
using System.Linq;
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
/// EnvDeleterService 单元测试:验证"stopped env → 删目录 + 删 SQLite 行"主路径
/// + running env 走 launcher stop + missing rootpath 容错。
///
/// 用真实的 ProcessLauncher — 它的 StopEnvAsync 对"不在 _running map 里的 env"
/// 走 cleanup no-op 路径(process_state.Delete + env.Upsert 全部 try/catch 吞),
/// 测试侧不需要 mock。
/// </summary>
public class EnvDeleterServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly TestDb _db;

    public EnvDeleterServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "env-deleter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
        _db = new TestDb();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private (EnvDeleterService deleter, EnvironmentRepository repo, string rootPath) MakeDeleter(
        string envId, string status)
    {
        var repo = new EnvironmentRepository(_db.Factory);
        var processStateRepo = new ProcessStateRepository(_db.Factory);
        var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);

        var rootPath = Path.Combine(_projectRoot, envId);
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "marker.txt"), "marker");

        repo.Upsert(new Environment
        {
            Id = envId,
            Name = envId,
            RootPath = rootPath,
            ComfyuiLayout = "isolated",
            Status = status,
        });

        var deleter = new EnvDeleterService(repo, launcher);
        return (deleter, repo, rootPath);
    }

    [Fact]
    public async Task DeleteAsync_StoppedEnv_RemovesDirectoryAndSqliteRow()
    {
        var (deleter, repo, rootPath) = MakeDeleter("env-stopped", "stopped");

        await deleter.DeleteAsync(repo.ListAll()[0]);

        Assert.False(Directory.Exists(rootPath));
        Assert.Empty(repo.ListAll());
    }

    [Fact]
    public async Task DeleteAsync_RunningEnv_CallsLauncherStopAndStillDeletes()
    {
        // 关键:running env 走 launcher.StopEnvAsync(那是个 no-op,因为 env 不在
        // launcher 的 _running map 里),走完后照常删目录 + 删行 — 验证主路径
        // 不抛 + 最终清理到位。
        var (deleter, repo, rootPath) = MakeDeleter("env-running", "running");
        var env = repo.ListAll()[0];

        await deleter.DeleteAsync(env);

        Assert.False(Directory.Exists(rootPath));
        Assert.Empty(repo.ListAll());
    }

    [Fact]
    public async Task DeleteAsync_NoDirectory_StillRemovesSqliteRow()
    {
        // 容错:RootPath 目录不存在(用户手动删了 / 路径在共享盘上已断)→ 不抛,
        // SQLite 行依然清掉。
        var (deleter, repo, _) = MakeDeleter("env-nodir", "stopped");
        var env = repo.ListAll()[0];
        Directory.Delete(env.RootPath, recursive: true);

        await deleter.DeleteAsync(env);

        Assert.False(Directory.Exists(env.RootPath));
        Assert.Empty(repo.ListAll());
    }

    [Fact]
    public async Task DeleteAsync_LeavesSiblingEnvsAlone()
    {
        // 多 env 共存,删其中一个 → 另一个保留。
        var (deleter, _, _) = MakeDeleter("env-keep", "stopped");
        var (deleter2, repo, rootPathToDelete) = MakeDeleter("env-delete", "stopped");

        await deleter2.DeleteAsync(repo.ListAll().Single(e => e.Id == "env-delete"));

        Assert.Single(repo.ListAll());
        Assert.Equal("env-keep", repo.ListAll()[0].Id);
        Assert.False(Directory.Exists(rootPathToDelete));
    }
}
