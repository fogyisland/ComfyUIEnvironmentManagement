using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.16+:banner 自动 dismiss + 颜色按 severity 分。
/// - TTL 默认 Info=5s / Warn=8s / Error=12s / Critical=null(不自动关)
/// - 注入 scheduler 让测试同步验证 dismiss 行为
/// - Dismiss(手动 / Close 按钮) + HasErrors 不变
/// </summary>
public class ErrorBannerViewModelTests
{
    /// <summary>
    /// 测试用可控 scheduler:不直接 fire,记下 (delay, action),
    /// 让测试在需要时手动 RunAll 触发。
    /// </summary>
    private sealed class CapturedScheduler
    {
        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = new();

        public Func<TimeSpan, Action, IDisposable> Scheduler => (delay, action) =>
        {
            Scheduled.Add((delay, action));
            return new NoopDisposable();
        };

        public void RunAll()
        {
            foreach (var (_, action) in Scheduled.ToList())
            {
                action();
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    // ──────────────── DefaultDismissAfter ────────────────

    [Fact]
    public void DefaultDismissAfter_Info_Is5Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(5), ErrorBannerViewModel.DefaultDismissAfter(ErrorSeverity.Info));

    [Fact]
    public void DefaultDismissAfter_Warn_Is8Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(8), ErrorBannerViewModel.DefaultDismissAfter(ErrorSeverity.Warn));

    [Fact]
    public void DefaultDismissAfter_Error_Is12Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(12), ErrorBannerViewModel.DefaultDismissAfter(ErrorSeverity.Error));

    [Fact]
    public void DefaultDismissAfter_Critical_IsNull_ManualCloseOnly()
        => Assert.Null(ErrorBannerViewModel.DefaultDismissAfter(ErrorSeverity.Critical));

    // ──────────────── Add + auto-dismiss ────────────────

    [Fact]
    public void Add_Info_AppendsEntry_AndSchedulesDismiss()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("test", "msg", ErrorSeverity.Info);

        Assert.Single(vm.Entries);
        Assert.Equal(ErrorSeverity.Info, vm.Entries[0].Severity);
        Assert.Equal(TimeSpan.FromSeconds(5), vm.Entries[0].DismissAfter);
        Assert.Single(sched.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(5), sched.Scheduled[0].Delay);
    }

    [Fact]
    public void Add_Warn_Error_HaveDefaultTtl()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);

        vm.Add("w", "w", ErrorSeverity.Warn);
        vm.Add("e", "e", ErrorSeverity.Error);

        Assert.Equal(TimeSpan.FromSeconds(8), vm.Entries.First(e => e.Code == "w").DismissAfter);
        Assert.Equal(TimeSpan.FromSeconds(12), vm.Entries.First(e => e.Code == "e").DismissAfter);
        Assert.Equal(2, sched.Scheduled.Count);
    }

    [Fact]
    public void Add_Critical_DoesNotSchedule()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("c", "c", ErrorSeverity.Critical);

        Assert.Single(vm.Entries);
        Assert.Null(vm.Entries[0].DismissAfter);
        Assert.Empty(sched.Scheduled);
    }

    [Fact]
    public void Add_CustomDismissAfter_OverridesDefault()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("i", "i", ErrorSeverity.Info, dismissAfter: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), vm.Entries[0].DismissAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), sched.Scheduled[0].Delay);
    }

    [Fact]
    public void Add_NullDismissAfter_DoesNotSchedule()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("i", "i", ErrorSeverity.Info, dismissAfter: null);

        Assert.Single(vm.Entries);
        Assert.Empty(sched.Scheduled);
    }

    // ──────────────── Dismiss (manual + auto) ────────────────

    [Fact]
    public void Dismiss_RemovesEntry()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("a", "a", ErrorSeverity.Info);
        var entry = vm.Entries[0];

        vm.Dismiss(entry);

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void DismissCommand_FiresDismiss()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("a", "a", ErrorSeverity.Info);
        var entry = vm.Entries[0];

        vm.DismissCommand.Execute(entry);

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void AutoDismiss_AfterSchedulerFires_RemovesEntry()
    {
        // 这是关键测试:模拟 TTL 到期后,banner 应该自动消失
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("a", "a", ErrorSeverity.Info);

        Assert.Single(vm.Entries);
        sched.RunAll();  // 模拟时间到了

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Dismiss_NotInEntries_NoOp()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        var ghost = new ErrorBannerEntry("ghost", "x", ErrorSeverity.Info, DateTime.Now);

        vm.Dismiss(ghost);

        Assert.Empty(vm.Entries);
    }

    // ──────────────── Concurrent Add + AutoDismiss ────────────────

    [Fact]
    public void AutoDismiss_OnlyRemovesOwnEntry_DoesNotAffectOthers()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        vm.Add("a", "a", ErrorSeverity.Info);  // dismissable
        vm.Add("c", "c", ErrorSeverity.Critical);  // not dismissable
        Assert.Equal(2, vm.Entries.Count);

        // 只 fire Info 的 dismiss action
        sched.Scheduled[0].Action();

        Assert.Single(vm.Entries);
        Assert.Equal("c", vm.Entries[0].Code);
    }

    // ──────────────── 现有契约:HasErrors / 容量 ────────────────

    [Fact]
    public void HasErrors_True_WhenErrorOrCritical()
    {
        var vm = new ErrorBannerViewModel(new CapturedScheduler().Scheduler);
        vm.Add("i", "i", ErrorSeverity.Info);
        Assert.False(vm.HasErrors);
        vm.Add("e", "e", ErrorSeverity.Error);
        Assert.True(vm.HasErrors);
    }

    [Fact]
    public void Add_CapsAt20Entries()
    {
        var sched = new CapturedScheduler();
        var vm = new ErrorBannerViewModel(sched.Scheduler);
        for (int i = 0; i < 25; i++) vm.Add($"c{i}", "x", ErrorSeverity.Info);

        Assert.Equal(20, vm.Entries.Count);
        // 最新的在 index 0(Insert(0, ...))
        Assert.Equal("c24", vm.Entries[0].Code);
    }

    // ──────────────── DefaultSchedule 在测试线程上 no-op(不抛) ────────────────

    [Fact]
    public void DefaultSchedule_OnTestThread_DoesNotThrow()
    {
        // xUnit 默认测试线程没有 Dispatcher,DispatcherTimer.Start() 会抛
        // InvalidOperationException。DefaultSchedule 必须 graceful no-op
        // 这样现有 9 个 new ErrorBannerViewModel() 测试不会被打破。
        var vm = new ErrorBannerViewModel();  // 用 default scheduler
        var ex = Record.Exception(() => vm.Add("t", "t", ErrorSeverity.Info));
        Assert.Null(ex);
        Assert.Single(vm.Entries);  // entry 写进去了,只是没人 fire dismiss
    }
}
