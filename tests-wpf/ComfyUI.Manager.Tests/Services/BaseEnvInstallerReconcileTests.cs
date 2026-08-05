using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class BaseEnvInstallerReconcileTests
{
    private static void SeedEnv(TestDb db, string id, string? bedStatus, string? reason = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = "stopped",
            BedProfileId = bedStatus is null ? null : "pytorch-2.5.0-cu121-stable",
            BedStatus = bedStatus,
            BedFailedReason = reason,
        });
    }

    [Fact]
    public void ReconcileStaleOnStartup_FlipsInstallingToFailed_LeavesOtherStatesAlone()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-installing", "installing");
        SeedEnv(db, "env-done", "done");
        SeedEnv(db, "env-failed", "failed", reason: "pip 退出码 1");
        SeedEnv(db, "env-null", null);
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(1, stale);
        Assert.Equal("failed", repo.Get("env-installing")!.BedStatus);
        Assert.Equal("上次未完成", repo.Get("env-installing")!.BedFailedReason);
        Assert.Equal("done", repo.Get("env-done")!.BedStatus);
        Assert.Null(repo.Get("env-done")!.BedFailedReason);
        Assert.Equal("failed", repo.Get("env-failed")!.BedStatus);
        Assert.Equal("pip 退出码 1", repo.Get("env-failed")!.BedFailedReason);
        Assert.Null(repo.Get("env-null")!.BedStatus);
    }

    [Fact]
    public void ReconcileStaleOnStartup_NullEnvRepo_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => BaseEnvInstaller.ReconcileStaleOnStartup(null!));
    }

    [Fact]
    public void ReconcileStaleOnStartup_EmptyDb_ReturnsZero()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(0, stale);
    }

    [Fact]
    public void ReconcileStaleOnStartup_AllStale_CountsEach()
    {
        using var db = new TestDb();
        for (var i = 0; i < 5; i++)
        {
            SeedEnv(db, $"env-{i}", "installing");
        }
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(5, stale);
        for (var i = 0; i < 5; i++)
        {
            var env = repo.Get($"env-{i}")!;
            Assert.Equal("failed", env.BedStatus);
            Assert.Equal("上次未完成", env.BedFailedReason);
        }
    }
}
