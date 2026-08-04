using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Data;

public class EnvironmentRepositoryBedColumnsTests
{
    [Fact]
    public void Upsert_RoundTripsAllThreeBedColumns()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = "env-bed",
            Name = "alpha",
            RootPath = @"C:\envs\alpha",
            ComfyuiLayout = "isolated",
            BedProfileId = "pytorch-2.5.0-cu121-stable",
            BedStatus = "done",
            BedFailedReason = null,
        };
        repo.Upsert(env);

        var fresh = repo.Get("env-bed");
        Assert.NotNull(fresh);
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("done", fresh.BedStatus);
        Assert.Null(fresh.BedFailedReason);

        // ListAll 也读得到
        var all = repo.ListAll();
        Assert.Single(all);
        Assert.Equal("pytorch-2.5.0-cu121-stable", all[0].BedProfileId);
    }

    [Fact]
    public void Upsert_OverwritesBedColumns_OnConflict()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = "env-rerun",
            Name = "beta",
            RootPath = @"C:\envs\beta",
            ComfyuiLayout = "isolated",
            BedProfileId = "pytorch-2.5.0-cu121-stable",
            BedStatus = "done",
        };
        repo.Upsert(env);

        // 重跑选不同 profile
        env.BedProfileId = "pytorch-nightly-cu126";
        env.BedStatus = "failed";
        env.BedFailedReason = "pip 退出码 1";
        repo.Upsert(env);

        var fresh = repo.Get("env-rerun");
        Assert.Equal("pytorch-nightly-cu126", fresh!.BedProfileId);
        Assert.Equal("failed", fresh.BedStatus);
        Assert.Equal("pip 退出码 1", fresh.BedFailedReason);
    }
}
