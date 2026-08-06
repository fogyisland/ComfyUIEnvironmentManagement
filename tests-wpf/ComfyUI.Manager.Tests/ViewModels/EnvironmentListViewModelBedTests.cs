using System;
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
            repo, null!, null!, null!, null!, null!, null!, null!, Path.GetTempPath(), null!);
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
        Assert.Contains("上次基础环境部署失败", vm.StartTooltip);
        Assert.Contains("pip 退出码 1", vm.StartTooltip);
    }

    [Fact]
    public void OpenBaseEnvProgress_AfterDialogCloses_TriggersReload()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        // 初始 env:BedStatus=null — all-done 短路不命中,走 dialog
        var env = MakeEnv("env-bed", "stopped", bedStatus: null);
        repo.Upsert(env);

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, profileLoader, null!, null!, Path.GetTempPath(), null!);
        Assert.Single(vm.Environments);
        Assert.Null(vm.Environments[0].BedStatus);

        // 用 ShowProgressDialogOverride 拦截,跳过真实 dialog
        bool overrideCalled = false;
        vm.ShowProgressDialogOverride = (_, _, _) => overrideCalled = true;
        vm.BaseEnvCommand.Execute(null);

        Assert.True(overrideCalled);  // BedStatus null → 走 dialog(不是 all-done 短路)
        // override 路径不会自动 reload(G10),我们手动验 Load() 也能重读
        // 模拟 BED 跑完:改 repo 行 + reload
        env.BedProfileId = "pytorch-2.5.0-cu121-stable";
        env.BedStatus = "done";
        repo.Upsert(env);
        vm.RefreshCommand.Execute(null);
        Assert.Equal("done", vm.Environments[0].BedStatus);
        Assert.Equal("pytorch-2.5.0-cu121-stable", vm.Environments[0].BedProfileId);
    }

    [Fact]
    public void OpenBaseEnvProgress_SelectedEnvAlreadyDone_ShowsMessageBoxAndSkipsDialog()
    {
        // v0.6.5.19.1 hotfix: 用户从 env-list 工具栏点"基础环境部署"时,
        // 若 Selected env 已装 → 弹"已安装"消息,不弹 install dialog。
        // (昨天 v0.6.5.19 只修了 BaseEnv tab 的"开始部署"按钮,
        // 漏了这个 env-list 工具栏入口。)
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var env = MakeEnv("env-done", "stopped", bedStatus: "done");
        repo.Upsert(env);

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, profileLoader, null!, null!, Path.GetTempPath(), null!);
        vm.Selected = vm.Environments[0];

        bool dialogCalled = false;
        string? messageShown = null;
        vm.ShowProgressDialogOverride = (_, _, _) => dialogCalled = true;
        vm.MessageBoxOverride = msg => messageShown = msg;

        vm.BaseEnvCommand.Execute(null);

        Assert.False(dialogCalled);
        Assert.NotNull(messageShown);
        Assert.Contains("已安装", messageShown);
        Assert.Contains("env-done", messageShown);
    }

    [Fact]
    public void OpenBaseEnvProgress_AllEnvsAlreadyDoneNoSelection_ShowsMessageBoxAndSkipsDialog()
    {
        // v0.6.5.19.1 hotfix: 无 Selected 时按"全部 env"处理,
        // 全部 done → 也走已装短路。
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-a", "stopped", bedStatus: "done"));
        repo.Upsert(MakeEnv("env-b", "stopped", bedStatus: "done"));

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, profileLoader, null!, null!, Path.GetTempPath(), null!);
        Assert.Null(vm.Selected);

        bool dialogCalled = false;
        string? messageShown = null;
        vm.ShowProgressDialogOverride = (_, _, _) => dialogCalled = true;
        vm.MessageBoxOverride = msg => messageShown = msg;

        vm.BaseEnvCommand.Execute(null);

        Assert.False(dialogCalled);
        Assert.NotNull(messageShown);
        Assert.Contains("已安装", messageShown);
    }

    [Fact]
    public void OpenBaseEnvProgress_SelectedNotDone_ProceedsToDialog_EvenIfOtherDone()
    {
        // v0.6.5.19.1 hotfix: Selected 未装(其他 env 已装)→ 走 dialog。
        // 当前 OpenBaseEnvProgress 只装 Selected(envIds 单个),所以 all-done
        // 短路看的就是 Selected 自己。如果选了已装的 → 走"已装"短路;
        // 选了未装的 → 走 dialog(由 dialog 内 BaseEnvInstaller 跑 pip)。
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-done", "stopped", bedStatus: "done"));
        repo.Upsert(MakeEnv("env-pending", "stopped", bedStatus: null));

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, profileLoader, null!, null!, Path.GetTempPath(), null!);
        // 选未装的(Environments[1])→ 走 dialog
        vm.Selected = vm.Environments[1];

        bool dialogCalled = false;
        bool msgCalled = false;
        vm.ShowProgressDialogOverride = (_, _, _) => dialogCalled = true;
        vm.MessageBoxOverride = _ => msgCalled = true;

        vm.BaseEnvCommand.Execute(null);

        Assert.True(dialogCalled);
        Assert.False(msgCalled);
    }
}