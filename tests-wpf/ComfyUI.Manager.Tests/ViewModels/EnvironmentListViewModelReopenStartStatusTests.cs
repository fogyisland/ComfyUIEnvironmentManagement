using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.17:env 行启动控制台入口 — env 跑起来后,让用户重新打开启动状态面板
/// (3 阶段 + stdout 日志)查看启动时发生了什么。v0.6.17.1:UI 入口从独立按钮
/// 改成 port 9000 旁的小图标(更紧凑),VM/Command 行为不变 — 这些测试仍
/// 覆盖 dict 缓存 + Reopen + per-env 隔离逻辑。
///
/// 关键 invariant:
/// - 启动成功后 StartStatus 不再 auto-hide(以前 await 2s + Hide())— 面板留
///   在 dict 里供用户 reopen。
/// - 面板 ✕ 按钮设 StartStatus = null 但 dict 条目留着(reopen 重新显示)。
/// - 每个 env 独立的 VM(per-env dict),切换 env 不丢日志。
/// - 没启动过的 env → ReopenStartStatusCommand.CanExecute = false。
///
/// ProcessLauncher sealed → 用 <see cref="EnvironmentListViewModel.StartEnvForTest"/>
/// 拦截启动回调,模拟成功 / 失败 路径。
/// </summary>
public class EnvironmentListViewModelReopenStartStatusTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelReopenStartStatusTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistvm-reopen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status)
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tempRoot, id),
            ComfyuiLayout = "isolated",
            Status = status,
        };
        Directory.CreateDirectory(env.RootPath);
        _repo.Upsert(env);
        return env;
    }

    private EnvironmentListViewModel NewVm() =>
        new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot, null!,
            null!, null!, null!, null!, null!, null!,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            new NodeRepository(_db.Factory),
            new NodeVersionRepository(new CatalogCacheStore(_db.Path)));

    [Fact]
    public async Task StartEnvAsync_SuccessfulStart_StatusStaysVisible_NoAutoHide()
    {
        var env = SeedEnv("env-A", "stopped");
        var vm = NewVm();
        vm.StartEnvForTest = (_, _, _, _) =>
        {
            env.Status = "running";
            return Task.CompletedTask;
        };

        await InvokeStartAsync(vm, env);

        // 关键 invariant:v0.6.17 之前成功启动后会等 2s 再 Hide,现在面板持续可见。
        Assert.NotNull(vm.StartStatus);
        Assert.True(vm.StartStatus!.IsVisible,
            "成功启动后面板应保持可见(不再 auto-hide),让用户能手动 ✕ 关");
        Assert.True(vm.StartStatus.IsComplete);
        Assert.Null(vm.StartStatus.Error);
    }

    [Fact]
    public async Task StartEnvAsync_Failure_StatusStillVisible_WithErrorMessage()
    {
        var env = SeedEnv("env-fail", "stopped");
        var vm = NewVm();
        vm.StartEnvForTest = (_, _, _, _) =>
            throw new InvalidOperationException("端口被占用");

        await InvokeStartAsync(vm, env);

        Assert.NotNull(vm.StartStatus);
        Assert.True(vm.StartStatus!.IsVisible,
            "失败后面板持续可见,用户能看到 Error 提示");
        Assert.NotNull(vm.StartStatus.Error);
        Assert.Contains("端口被占用", vm.StartStatus.Error);
    }

    [Fact]
    public void ReopenStartStatusCommand_NoPriorStart_ShowsInfoDialog_NoPanelShown()
    {
        // v0.6.17.1:常驻图标 CanExecute 永远 true(env 非 null)。点击没启动过的 env
        // → ShowInfoDialog 提示用户先启动,而不是静默 no-op(用户原话"点了也不能
        // 开启面板"反馈)。StartStatus 仍 null(没东西可显示)。
        // v0.6.17.1+:env.Status == "stopped" → 引导"请先点启动"。
        SeedEnv("env-never", "stopped");
        var vm = NewVm();
        string? dialogMessage = null;
        string? dialogTitle = null;
        vm.ShowMessageBoxOverride = (msg, title) =>
        {
            dialogMessage = msg;
            dialogTitle = title;
        };

        Assert.True(vm.ReopenStartStatusCommand.CanExecute(vm.Environments[0]),
            "常驻图标:CanExecute 必须 true(env 非 null 即允许点)");

        vm.ReopenStartStatusCommand.Execute(vm.Environments[0]);

        Assert.NotNull(dialogMessage);
        Assert.Contains("env-never", dialogMessage);
        Assert.Contains("还未启动", dialogMessage);
        Assert.Contains("请先点", dialogMessage);
        Assert.Equal("启动控制台", dialogTitle);
        Assert.Null(vm.StartStatus);
        Assert.Null(vm.ActiveStartStatusEnvId);
    }

    [Fact]
    public void ReopenStartStatusCommand_RunningEnvButNoSessionHistory_PointsToLogViewer()
    {
        // v0.6.17.1+:env.Status == "running" 但本会话没启动过(典型场景 = manager
        // 重启前的运行实例,_startStatuses dict 空了)。点击 → 提示用「查看日志」
        // 按钮看实时 stdout,而不是错误地说"未启动"(用户原话:env 实际在跑,
        // 但弹"未启动"很困惑)。
        SeedEnv("env-running", "running");
        var vm = NewVm();
        string? dialogMessage = null;
        vm.ShowMessageBoxOverride = (msg, _) => dialogMessage = msg;

        vm.ReopenStartStatusCommand.Execute(vm.Environments[0]);

        Assert.NotNull(dialogMessage);
        Assert.Contains("env-running", dialogMessage);
        Assert.Contains("正在运行", dialogMessage);
        Assert.Contains("查看日志", dialogMessage);
        Assert.DoesNotContain("还未启动", dialogMessage);  // 不应误导说没启动
        Assert.Null(vm.StartStatus);
    }

    [Fact]
    public async Task ReopenStartStatusCommand_AfterSuccessfulStart_CanExecute_ShowsPanel()
    {
        var env = SeedEnv("env-A", "stopped");
        var vm = NewVm();
        vm.StartEnvForTest = (_, _, _, _) => Task.CompletedTask;

        await InvokeStartAsync(vm, env);
        Assert.True(vm.StartStatus!.IsVisible);

        // 用户 ✕ 关掉面板
        vm.CloseStartStatusPanel();
        Assert.Null(vm.StartStatus);

        // 但 Reopen 命令仍然可以执行(dict 条目还在)
        Assert.True(vm.ReopenStartStatusCommand.CanExecute(env));

        // 重新打开
        vm.ReopenStartStatusCommand.Execute(env);
        Assert.NotNull(vm.StartStatus);
        Assert.True(vm.StartStatus!.IsVisible);
        // 同一 VM,数据未丢(3 阶段完成 + 之前的 LogLines)
        Assert.True(vm.StartStatus.IsComplete);
    }

    [Fact]
    public async Task CloseStartStatusPanel_HidesPanelButDictEntryPreserved()
    {
        var env = SeedEnv("env-A", "stopped");
        var vm = NewVm();
        vm.StartEnvForTest = (_, _, _, _) => Task.CompletedTask;

        await InvokeStartAsync(vm, env);
        var originalVm = vm.StartStatus;

        vm.CloseStartStatusPanel();
        Assert.Null(vm.StartStatus);

        // dict 里还留着 — Reopen 拿出同一个 instance
        vm.ReopenStartStatusCommand.Execute(env);
        Assert.Same(originalVm, vm.StartStatus);
        Assert.True(vm.StartStatus!.IsVisible,
            "Reopen 必须返回同一个 VM instance(数据保留),不能新建一个空 VM");
    }

    [Fact]
    public async Task ReopenStartStatusCommand_PerEnvIsolation_ShowsCorrectEnv()
    {
        // 关键 invariant:每个 env 独立 VM。启动 A 后再启动 B,B 的日志不会覆盖 A 的。
        var envA = SeedEnv("env-A", "stopped");
        var envB = SeedEnv("env-B", "stopped");
        var vm = NewVm();

        var vmA = new EnvStartStatusViewModel { Title = "启动状态 — env-A" };
        var vmB = new EnvStartStatusViewModel { Title = "启动状态 — env-B" };
        // 直接通过 _startStatuses 字典注入(测试 seam — 用 reflection 拿 private field)
        var dictField = typeof(EnvironmentListViewModel).GetField(
            "_startStatuses",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.Generic.Dictionary<string, EnvStartStatusViewModel>)
            dictField.GetValue(vm)!;
        dict[envA.Id] = vmA;
        dict[envB.Id] = vmB;

        // 当前显示 B 的面板(模拟刚启动完 B)— StartStatus setter 是 private,
        // 测试里走 reflection 注入(等同于 ReopenStartStatusCommand 内部做的事)。
        var startStatusProp = typeof(EnvironmentListViewModel).GetProperty(
            nameof(EnvironmentListViewModel.StartStatus))!;
        startStatusProp.SetValue(vm, vmB);

        // 用户点 A 的启动控制台图标(port 旁) → 应该切回 A 的 VM,不是新建空 VM
        vm.ReopenStartStatusCommand.Execute(envA);
        Assert.Same(vmA, vm.StartStatus);
        Assert.Equal("启动状态 — env-A", vm.StartStatus!.Title);

        // 切回 B
        vm.ReopenStartStatusCommand.Execute(envB);
        Assert.Same(vmB, vm.StartStatus);
        Assert.Equal("启动状态 — env-B", vm.StartStatus!.Title);
    }

    [Fact]
    public async Task StartEnvAsync_RepeatedStartOnSameEnv_ReplacesStoredVm()
    {
        // 同一 env 启动两次(中间 stop + start 之类)→ dict 里要换成最新 VM,
        // 旧 VM 的日志不再被 reopen 拿到(它已 stale)。
        var env = SeedEnv("env-A", "stopped");
        var vm = NewVm();

        var firstStart = true;
        vm.StartEnvForTest = (_, _, _, _) =>
        {
            env.Status = firstStart ? "running" : "running";
            firstStart = false;
            return Task.CompletedTask;
        };

        await InvokeStartAsync(vm, env);
        var vm1 = vm.StartStatus;
        Assert.NotNull(vm1);

        // 模拟第二次启动 — env 已经 running,user 走重启路径(自动重启 / 手动 stop+start)。
        // 为简化直接调 StartCommand(它会检查 env.Status == "stopped" 才允许,这里手动触发)
        // 实际生产路径是 RestartEnvInternalAsync,下面单独测。
        env.Status = "stopped";  // simulate stop
        await InvokeStartAsync(vm, env);
        var vm2 = vm.StartStatus;
        Assert.NotNull(vm2);
        Assert.NotSame(vm1, vm2);
        Assert.True(vm2!.IsVisible,
            "同一 env 二次启动应该换新 VM,旧 VM 不再被 reopen 拿到");
    }

    [Fact]
    public async Task RestartEnvInternal_Success_StatusStaysVisible_NoAutoHide()
    {
        // 镜像 StartEnvAsync 不 auto-hide 的 invariant — RestartEnvInternalAsync
        // 之前也 await 2s + Hide,现在面板留 dict。
        var env = SeedEnv("env-A", "running");
        var vm = NewVm();
        vm.StopEnvForTest = e => { e.Status = "stopped"; return Task.CompletedTask; };
        vm.StartEnvForTest = (_, _, _, _) =>
        {
            env.Status = "running";
            return Task.CompletedTask;
        };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.NotNull(vm.StartStatus);
        Assert.True(vm.StartStatus!.IsVisible,
            "重启成功后启动面板应保持可见(不再 auto-hide)");
        Assert.True(vm.ReopenStartStatusCommand.CanExecute(env));
    }

    [Fact]
    public void ReopenStartStatusCommand_EnvNull_CannotExecute()
    {
        var vm = NewVm();
        Assert.False(vm.ReopenStartStatusCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActiveStartStatusEnvId_FollowsStartStatus_SwitchAndClear()
    {
        // v0.6.17.1:ActiveStartStatusEnvId 必须跟 StartStatus 同步 — env 行的
        // 🪵 图标用这个值切深/浅色 brush。StartStatus = null → null,
        // reopen env A → A.Id,reopen env B → B.Id(切走 A 的图变浅,B 的变深)。
        var envA = SeedEnv("env-A", "stopped");
        var envB = SeedEnv("env-B", "stopped");
        var vm = NewVm();

        vm.StartEnvForTest = (_, _, _, _) => Task.CompletedTask;
        await InvokeStartAsync(vm, envA);

        Assert.Equal(envA.Id, vm.ActiveStartStatusEnvId);
        Assert.NotNull(vm.StartStatus);

        // 关掉面板 → ActiveStartStatusEnvId 也清掉
        vm.CloseStartStatusPanel();
        Assert.Null(vm.StartStatus);
        Assert.Null(vm.ActiveStartStatusEnvId);

        // 启动 B → ActiveStartStatusEnvId 切到 B
        await InvokeStartAsync(vm, envB);
        Assert.Equal(envB.Id, vm.ActiveStartStatusEnvId);
    }

    /// <summary>
    /// 调 StartCommand → 内部 async StartEnvAsync,等任务跑完再 assert。
    /// StartCommand.RelayCommand 异步触发,Task 不返回,所以要等出 env-busy
    /// 释放(Load() + RaiseCommandsChanged 之后才完事)。
    /// </summary>
    private static async Task InvokeStartAsync(EnvironmentListViewModel vm, Environment env)
    {
        vm.StartCommand.Execute(env);
        // 等到 per-env mutex 释放 — StartEnvAsync finally 块调 UnmarkEnvBusy。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.StartStatus is null || !vm.StartStatus.IsVisible && vm.StartStatus.Error is null)
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException("Start did not finish within 5s");
            await Task.Delay(20);
        }
        // 等到 task 真的跑完(start 路径 finally 走 Load + RaiseCommandsChanged)
        // 简单做法:轮询 IsEnvBusy — 这是 BusyKind.Start 被移除的标志。
        sw.Restart();
        // IsEnvBusy 是 private;改用 StartCommand.CanExecute 反推(它看 IsEnvBusy)。
        // 但成功路径后 env.Status == "running",CanExecute 还是 false,所以这条路不通。
        // 直接轮询 RaiseCommandsChanged 副作用:StartEnvForTest 跑了 + finally 跑了
        // 我们用 StartStatus 的存在性做信号 — 等到 StartStatus 不是 null 即可。
        while (vm.StartStatus is null)
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException("StartStatus never set");
            await Task.Delay(20);
        }
        // 最后再 sleep 50ms 让 finally 块(Load + RaiseCommandsChanged)跑完
        await Task.Delay(50);
    }
}