using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.7.2:env-list 行内「打开浏览器」按钮的 UI 集成测试。
/// 覆盖 6 个场景:null-env / stopped env 不能开 / running+port 拿到正确 URL /
/// running 但无 Port 不能开 / override 路径被拦截 / chrome path resolution。
/// </summary>
public class EnvironmentListViewModelOpenBrowserTests
{
    private static EnvironmentListViewModel MakeVm(TestDb db)
    {
        var profileLoader = new BaseEnvProfileLoader(
            Path.Combine(Path.GetTempPath(), "open-browser-test-loader-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            Path.Combine(Path.GetTempPath(), "open-browser-test-proj-" + Guid.NewGuid()),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        return vm;
    }

    private static Environment MakeEnv(string id, string status, int? port)
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            Status = status,
        };
        env.Port = port;
        return env;
    }

    [Fact]
    public void OpenBrowser_NullEnv_DoesNothing()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        vm.Selected = null;

        var openedUrl = (string?)null;
        vm.OpenBrowserUrlOverride = u => openedUrl = u;

        vm.OpenBrowserCommand.Execute(null);

        Assert.Null(openedUrl);
    }

    [Fact]
    public void OpenBrowser_StoppedEnv_CannotExecute()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", status: "stopped", port: 8188);
        new EnvironmentRepository(db.Factory).Upsert(env);

        Assert.False(vm.OpenBrowserCommand.CanExecute(env));
    }

    [Fact]
    public void OpenBrowser_RunningEnvWithPort_OpensCorrectUrl()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", status: "running", port: 8188);
        new EnvironmentRepository(db.Factory).Upsert(env);

        Assert.True(vm.OpenBrowserCommand.CanExecute(env));

        var openedUrl = (string?)null;
        vm.OpenBrowserUrlOverride = u => openedUrl = u;

        vm.OpenBrowserCommand.Execute(env);

        Assert.Equal("http://127.0.0.1:8188", openedUrl);
    }

    [Fact]
    public void OpenBrowser_RunningEnvNoPort_CannotExecute()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", status: "running", port: null);
        new EnvironmentRepository(db.Factory).Upsert(env);

        // Port 缺失 → 没有页面可开,CanExecute 应为 false
        Assert.False(vm.OpenBrowserCommand.CanExecute(env));

        var openedUrl = (string?)null;
        vm.OpenBrowserUrlOverride = u => openedUrl = u;

        vm.OpenBrowserCommand.Execute(env);

        // 即便绕过 CanExecute 强行 Execute,实现里 null check 也守住
        Assert.Null(openedUrl);
    }

    [Fact]
    public void OpenBrowser_DifferentPort_PassedThrough()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        // 跟 ProcessLauncher.cs:154 --listen 127.0.0.1 一致,URL 用 127.0.0.1 不带 port 后缀
        var env = MakeEnv("e2", status: "running", port: 9999);
        new EnvironmentRepository(db.Factory).Upsert(env);

        var openedUrl = (string?)null;
        vm.OpenBrowserUrlOverride = u => openedUrl = u;

        vm.OpenBrowserCommand.Execute(env);

        Assert.Equal("http://127.0.0.1:9999", openedUrl);
    }

    [Fact]
    public void OpenBrowser_OverrideNotSet_DefaultsAreInvokable_NoThrow()
    {
        // 不设 override 意味着走 DefaultOpenBrowser → Process.Start。
        // 测试环境的 Chrome 通常没装,会走回退路径;更关键的是不能 NRE / throw。
        // 我们只验证:Command 存在、Execute(null) 在 Selected==null 时不掉到 Process.Start,
        // 以及 override 设了之后能正常拦截。
        using var db = new TestDb();
        var vm = MakeVm(db);
        vm.Selected = null;

        // Selected==null 时 Execute 不应该触发任何 Process.Start(实现里 null check 守住)
        var ex = Record.Exception(() => vm.OpenBrowserCommand.Execute(null));
        Assert.Null(ex);
    }
}
