using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    /// <summary>
    /// 测试桩 <see cref="IDashboardService"/> — 控制 snapshot 内容 / 抛异常 / 延迟
    /// / 计数调用次数。Dashboard 4 卡片 Grid 只需要 NodeCount + EnvironmentCounts
    /// + RecentOperations + LatestRelease 几个字段。
    /// </summary>
    private sealed class StubDashboardService : IDashboardService
    {
        public DashboardSnapshot? NextSnapshot { get; set; }
        public bool ShouldThrow { get; set; }
        public TimeSpan Delay { get; set; }
        public int CallCount { get; private set; }

        public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            CallCount++;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (ShouldThrow) throw new InvalidOperationException("stubbed failure");
            return NextSnapshot ?? new DashboardSnapshot(
                new EnvironmentCounts(1, 2, 3), 42,
                Array.Empty<RecentOperation>(), null, false, DateTimeOffset.Now);
        }
    }

    [Fact]
    public void Ctor_InitialState_IsRefreshingFalse_LastSnapshotNull()
    {
        var svc = new StubDashboardService();
        var vm = new DashboardViewModel(svc);

        Assert.False(vm.IsRefreshing);
        Assert.Null(vm.LastSnapshot);
    }

    [Fact]
    public async Task RefreshAsync_LoadsSnapshot_SetsIsRefreshingFalse()
    {
        var svc = new StubDashboardService();
        var vm = new DashboardViewModel(svc);

        await vm.RefreshAsync();

        Assert.NotNull(vm.LastSnapshot);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(42, vm.LastSnapshot!.NodeCount);
        Assert.Equal(new EnvironmentCounts(1, 2, 3), vm.LastSnapshot!.EnvironmentCounts);
    }

    [Fact]
    public async Task RefreshAsync_GitHubFailed_SnapshotHasNullRelease()
    {
        var svc = new StubDashboardService
        {
            NextSnapshot = new DashboardSnapshot(
                new EnvironmentCounts(0, 0, 0), 0,
                Array.Empty<RecentOperation>(), null, true, DateTimeOffset.Now),
        };
        var vm = new DashboardViewModel(svc);

        await vm.RefreshAsync();

        Assert.True(vm.LastSnapshot!.GitHubFailed);
        Assert.Null(vm.LastSnapshot!.LatestRelease);
    }

    [Fact]
    public async Task RefreshAsync_PartialFailure_RetainsLastSnapshot()
    {
        // First call success, second call throws → LastSnapshot stays as first
        var svc = new StubDashboardService();
        var vm = new DashboardViewModel(svc);

        await vm.RefreshAsync();
        var firstSnapshot = vm.LastSnapshot;
        Assert.NotNull(firstSnapshot);

        svc.ShouldThrow = true;
        await vm.RefreshAsync(); // should not throw, retain
        Assert.Same(firstSnapshot, vm.LastSnapshot);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task RefreshCommand_TriggersRefresh()
    {
        var svc = new StubDashboardService();
        var vm = new DashboardViewModel(svc);

        Assert.True(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null);

        // Give the fire-and-forget RefreshAsync a chance to complete.
        // DashboardViewModel.RefreshAsync is async — Execute just kicks it off.
        await WaitFor(() => vm.LastSnapshot is not null, TimeSpan.FromSeconds(2));

        Assert.NotNull(vm.LastSnapshot);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(1, svc.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCalls_Deduplicates()
    {
        // SemaSlim(1, 1):第二次调用 wait 0 → 锁已被 task1 持有 → 返回 → no-op。
        // 关键断言:CallCount == 1(只跑了 1 次,不是 2 次)。
        var svc = new StubDashboardService { Delay = TimeSpan.FromMilliseconds(200) };
        var vm = new DashboardViewModel(svc);

        var task1 = vm.RefreshAsync();
        await Task.Delay(50); // 让 task1 抢到锁
        var task2 = vm.RefreshAsync();

        await Task.WhenAll(task1, task2);

        Assert.Equal(1, svc.CallCount); // dedupe 成功
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }
}