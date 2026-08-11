using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Data;
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

    // ==================== v0.6.11+ T3:欢迎首页扩展 ====================

    private static DashboardSnapshot NewSnapshot() => new(
        new EnvironmentCounts(1, 2, 3), 42,
        Array.Empty<RecentOperation>(), "v0.6.11", false, DateTimeOffset.Now);

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public bool OpenCalled { get; private set; }
        public string? LastUrl { get; private set; }

        public void OpenWithChromeFallback(
            string path, Action<string, string, ErrorSeverity>? errorReporter = null)
        {
            OpenCalled = true;
            LastUrl = path;
        }
    }

    [Fact]
    public async Task RefreshAsync_FetchesBothGitHubAndChangelog_InParallel()
    {
        var svc = new StubDashboardService
        {
            NextSnapshot = NewSnapshot() with
            {
                Releases = new[]
                {
                    new GitHubRelease("v0.6.11", "v0.6.11", DateTime.UtcNow,
                        "https://example.invalid/r", false),
                },
                Changelog = new[]
                {
                    new ChangelogEntry("v0.6.11", DateTime.UtcNow, new[] { "test bullet" }),
                },
            },
        };
        var vm = new DashboardViewModel(svc);

        await vm.RefreshAsync();

        Assert.Single(vm.Releases);
        Assert.Equal("v0.6.11", vm.Releases[0].TagName);
        Assert.Single(vm.Changelog);
        Assert.Equal("v0.6.11", vm.Changelog[0].Version);
    }

    [Fact]
    public async Task RefreshAsync_GitHubFail_PreservesCachedChangelog()
    {
        // 第一次成功拿到 changelog,第二次 GitHub 挂了且 snapshot 里 changelog 为空 —
        // VM 应保留上一次的内容,而不是把卡片刷成空白。
        var svc = new StubDashboardService
        {
            NextSnapshot = NewSnapshot() with
            {
                Changelog = new[] { new ChangelogEntry("v0.6.10", null, new[] { "fallback" }) },
            },
        };
        var vm = new DashboardViewModel(svc);
        await vm.RefreshAsync();

        svc.NextSnapshot = NewSnapshot() with
        {
            GitHubFailed = true,
            Releases = Array.Empty<GitHubRelease>(),
            Changelog = Array.Empty<ChangelogEntry>(),
        };
        await vm.RefreshAsync();

        Assert.True(vm.LastSnapshot?.GitHubFailed);
        Assert.Single(vm.Changelog);
        Assert.Equal("v0.6.10", vm.Changelog[0].Version);
    }

    /// <summary>
    /// CF-T1-A:ChangelogParser.Parse 对缺失 / 空文件返回空 list(不是 fallback),
    /// 回退到 HardcodedFallback 是 DashboardService 的责任。这里接**真实**
    /// DashboardService(指向不存在的 changelog 路径)验证这条链真的接上了 ——
    /// 用 stub 断言只会测到 stub 自己。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ChangelogMissing_UsesHardcodedFallback()
    {
        using var fixture = new RealServiceFixture();
        var vm = new DashboardViewModel(
            fixture.CreateService(changelogPath: Path.Combine(
                Path.GetTempPath(), $"no-such-changelog-{Guid.NewGuid():N}.md")));

        await vm.RefreshAsync();

        Assert.NotEmpty(vm.Changelog);
        Assert.Equal(new ChangelogParser().HardcodedFallback.Count, vm.Changelog.Count);
    }

    /// <summary>
    /// 生产路径守护:仓库根的 CHANGELOG.md(csproj 拷到输出目录)必须真的能解析出条目。
    /// 有人改 CHANGELOG 格式(比如把 '## v0.6.11' 写成 '### v0.6.11')就会静默退回
    /// HardcodedFallback —— 卡片还有内容,但永远停在旧版本,不测就发现不了。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_RealChangelogFile_ParsesEntries()
    {
        var repoChangelog = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        Assert.True(File.Exists(repoChangelog),
            $"CHANGELOG.md 未拷到输出目录({repoChangelog})—— 检查 csproj 的 None/CopyToOutputDirectory");

        using var fixture = new RealServiceFixture();
        var vm = new DashboardViewModel(fixture.CreateService(changelogPath: repoChangelog));

        await vm.RefreshAsync();

        Assert.NotEmpty(vm.Changelog);
        // 解析结果应来自文件而不是 fallback:文件里每段都带日期。
        Assert.All(vm.Changelog, e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
        Assert.Contains(vm.Changelog, e => e.BulletPoints.Count > 0);
    }

    [Fact]
    public void CopyStagingPathCommand_ResolvesStagingPath()
    {
        // RefreshAsync 之前就要有值 —— 「下载地址」区块首屏即可复制。
        var vm = new DashboardViewModel(new StubDashboardService());

        Assert.False(string.IsNullOrEmpty(vm.StagingPath));
        Assert.EndsWith("ComfyUI.Manager.exe", vm.StagingPath);

        // 走 test seam,避免单测碰真实剪贴板(需要 STA + 会污染用户剪贴板)。
        string? copied = null;
        vm.ClipboardSetTextOverride = s => copied = s;
        vm.CopyStagingPathCommand.Execute(null);
        Assert.Equal(vm.StagingPath, copied);
    }

    [Fact]
    public void ToggleChangelogExpandCommand_TogglesIsChangelogExpanded()
    {
        var vm = new DashboardViewModel(new StubDashboardService());

        Assert.False(vm.IsChangelogExpanded);
        vm.ToggleChangelogExpandCommand.Execute(null);
        Assert.True(vm.IsChangelogExpanded);
        vm.ToggleChangelogExpandCommand.Execute(null);
        Assert.False(vm.IsChangelogExpanded);
    }

    [Fact]
    public void OpenReleaseUrlCommand_UsesBrowserLauncher()
    {
        var launcher = new FakeBrowserLauncher();
        var vm = new DashboardViewModel(new StubDashboardService(), launcher);

        vm.OpenReleaseUrlCommand.Execute(null);

        Assert.True(launcher.OpenCalled);
        Assert.Equal(vm.ReleaseUrl, launcher.LastUrl);
    }

    /// <summary>
    /// 最小 fixture:构造一个真实 <see cref="DashboardService"/>(空 repo + 断网 http),
    /// 只为验证 changelog 回退链。GitHub 请求失败是预期的,不影响断言。
    /// </summary>
    private sealed class RealServiceFixture : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"dash-vm-{Guid.NewGuid():N}");

        public RealServiceFixture() => Directory.CreateDirectory(_root);

        public DashboardService CreateService(string changelogPath) => new(
            new EmptyEnvRepo(), new EmptyNodeRepo(), new AppLogger(_root),
            new HttpClient(new OfflineHandler()),
            releaseService: null,
            changelogParser: new ChangelogParser(),
            changelogPath: changelogPath);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private sealed class EmptyEnvRepo : IEnvironmentRepository
        {
            public List<ComfyUI.Manager.Models.Environment> ListAll() => new();
            public ComfyUI.Manager.Models.Environment? Get(string envId) => null;
            public void Upsert(ComfyUI.Manager.Models.Environment env) { }
            public int? GetMaxPort() => null;
        }

        private sealed class EmptyNodeRepo : INodeRepository
        {
            public Task<long> CountAllAsync(CancellationToken ct = default) => Task.FromResult(0L);
            public List<ScannedNode> ListByEnv(string envId) => new();
            public ScannedNode? Get(string nodeId) => null;
        }

        private sealed class OfflineHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("offline");
        }
    }
}