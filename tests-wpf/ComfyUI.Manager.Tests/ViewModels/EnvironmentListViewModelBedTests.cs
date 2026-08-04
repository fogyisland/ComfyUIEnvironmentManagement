using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelBedTests
{
    private static EnvironmentListViewModel NewVm(TestDb db, Environment? seedEnv = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        if (seedEnv is not null) repo.Upsert(seedEnv);
        return new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, null!, null!, null!, Path.GetTempPath());
    }

    private static Environment MakeEnv(string id, string status, string? bedStatus) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = status,
            BedStatus = bedStatus,
        };

    [Fact]
    public void StartCommand_DisabledWhenBedStatusNull()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-x", "stopped", bedStatus: null);
        var vm = NewVm(db, env);
        Assert.False(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_EnabledWhenBedStatusDone()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-done", "stopped", bedStatus: "done");
        var vm = NewVm(db, env);
        Assert.True(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_DisabledWhenBedStatusInstalling()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-running", "stopped", bedStatus: "installing");
        var vm = NewVm(db, env);
        Assert.False(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_EnabledWhenBedStatusFailed()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-failed", "stopped", bedStatus: "failed");
        var vm = NewVm(db, env);
        Assert.True(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartTooltip_ShowsBedNotInstalled_WhenSelectedBedStatusNull()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-x", "stopped", bedStatus: null);
        var vm = NewVm(db, env);
        vm.Selected = vm.Environments[0];
        Assert.Equal("基础环境未安装", vm.StartTooltip);
    }

    [Fact]
    public void StartTooltip_ShowsBedFailed_WithReason()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-f", "stopped", bedStatus: "failed");
        env.BedFailedReason = "pip 退出码 1";
        var vm = NewVm(db, env);
        vm.Selected = vm.Environments[0];
        Assert.Contains("上次 BED 失败", vm.StartTooltip);
        Assert.Contains("pip 退出码 1", vm.StartTooltip);
    }
}