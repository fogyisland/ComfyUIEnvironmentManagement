using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.16+:用户点详情面板时,如果 node_versions 表空,自动触发
/// <see cref="GitHubVersionService.FetchVersionsAsync"/> 单节点拉取,落库
/// + 填 ComboBox。本测试覆盖自动拉取的 happy path + 错误路径 + cancel 路径。
/// </summary>
public class CatalogViewModelVersionFetchTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;
    private readonly FakeRefreshService _refreshService;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;

    public CatalogViewModelVersionFetchTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _settingsRepo = new SettingsRepository(Path.Combine(
            Path.GetTempPath(), $"cat-fetch-{Guid.NewGuid():N}.json"));
        _refreshService = new FakeRefreshService();
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
    }

    public void Dispose() => _db.Dispose();

    private CatalogViewModel NewVm(GitHubVersionService? versionService = null) =>
        new CatalogViewModel(_catRepo, _versionRepo,
            new NoopNodeOps(new EnvironmentRepository(_db.Factory),
                            new NodeRepository(_db.Factory), _settings),
            _refreshService, _settings, _settingsRepo, @"D:\ToolDevelop\ComfyUI",
            versionService: versionService);

    private CatalogEntry Seed(string package, string? reference,
                              string? latestVersion = null, string? htmlUrl = null,
                              string? createdAt = null)
    {
        // Reference 是 [JsonIgnore],从 raw_metadata["reference"] 抽出。
        // Upsert 只把 raw_metadata 落库 + ExtractTypedFields 时再抽。
        var rawMetadata = new Dictionary<string, object?>();
        if (reference is not null) rawMetadata["reference"] = reference;

        var e = new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            CachedAt = "2026-08-17T00:00:00",
            ExpiresAt = "2027-08-17T00:00:00",
            LatestVersion = latestVersion,
            HtmlUrl = htmlUrl,
            CreatedAt = createdAt,
            RawMetadata = rawMetadata,
        };
        _catRepo.Upsert(e);
        return e;
    }

    private void SeedNodeVersion(string nodeId, string tag, string publishedAt)
    {
        using var conn = _db.Factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO node_versions
                (node_id, tag_name, published_at, is_prerelease, fetched_at)
            VALUES (@nid, @tag, @pub, 0, @fetch)";
        cmd.Parameters.AddWithValue("@nid", nodeId);
        cmd.Parameters.AddWithValue("@tag", tag);
        cmd.Parameters.AddWithValue("@pub", publishedAt);
        cmd.Parameters.AddWithValue("@fetch", "2026-08-17T00:00:00Z");
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 等 VM in-flight fetch 完成:轮询 <c>IsLoadingVersions</c> until false,
    /// 或 timeout 抛。简单可靠,避免 hand-rolled TaskCompletionSource 的
    /// scheduler 死锁风险。
    /// </summary>
    private static async Task WaitForFetchToFinish(CatalogViewModel vm, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.IsLoadingVersions)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Fetch did not finish within " + timeoutMs + "ms");
            await Task.Delay(20);
        }
    }

    // ─────────── Happy path ───────────

    [Fact]
    public async Task OnSelect_EmptyNodeVersions_TriggersFetch_AndPopulatesCombo()
    {
        var entry = Seed("pkg-fetch-happy", "https://github.com/ltdrdata/foo");
        var versionService = new ConfigurableVersionService(
            ("pkg-fetch-happy", new List<VersionInfo>
            {
                new() { Tag = "v1.0.0", PublishedAt = "2026-08-01T00:00:00Z" },
                new() { Tag = "v0.9.0", PublishedAt = "2026-07-01T00:00:00Z" },
            }));

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-fetch-happy");
        await WaitForFetchToFinish(vm);

        Assert.Null(vm.LoadVersionsError);
        Assert.Equal(2, vm.SelectedVersions.Count);
        Assert.Equal("v1.0.0", vm.SelectedVersion?.Tag);
        Assert.Equal(1, versionService.CallCount);
        // 落库:再次 select 应该走 DB 拿到这两条
        vm.Selected = null;
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-fetch-happy");
        Assert.Equal(2, vm.SelectedVersions.Count);
    }

    [Fact]
    public async Task OnSelect_VersionServiceNull_DoesNotCrash()
    {
        Seed("pkg-no-service", "https://github.com/ltdrdata/foo");
        var vm = NewVm(versionService: null);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-no-service");
        await Task.Delay(100);  // 等一下,确保 VM 没有隐藏 async fetch
        Assert.Empty(vm.SelectedVersions);
        Assert.False(vm.IsLoadingVersions);
        Assert.Null(vm.LoadVersionsError);
    }

    [Fact]
    public async Task OnSelect_ReferenceEmpty_DoesNotTriggerFetch()
    {
        Seed("pkg-no-ref", reference: null);
        var versionService = new ConfigurableVersionService(
            ("pkg-no-ref", new List<VersionInfo> { new() { Tag = "v1" } }));
        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-no-ref");
        await Task.Delay(100);
        Assert.False(vm.IsLoadingVersions);
        Assert.Empty(vm.SelectedVersions);
        Assert.Equal(0, versionService.CallCount);
    }

    [Fact]
    public async Task OnSelect_AlreadyHasVersions_DoesNotTriggerFetch()
    {
        var entry = Seed("pkg-cached", "https://github.com/ltdrdata/foo");
        SeedNodeVersion(entry.Id, "v2.0.0", "2026-06-15T00:00:00Z");
        var versionService = new ConfigurableVersionService(
            ("pkg-cached", new List<VersionInfo> { new() { Tag = "v1.0.0" } }));

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-cached");
        await Task.Delay(100);  // 给任何潜在的 async fetch 一点时间跑

        Assert.Single(vm.SelectedVersions);
        Assert.Equal("v2.0.0", vm.SelectedVersion?.Tag);
        Assert.False(vm.IsLoadingVersions);
        Assert.Equal(0, versionService.CallCount);
    }

    // ─────────── Error paths ───────────

    [Fact]
    public async Task OnSelect_FetchThrowsRateLimit_LoadVersionsErrorSet()
    {
        Seed("pkg-rl", "https://github.com/ltdrdata/foo");
        var versionService = new ConfigurableVersionService() { ThrowRateLimit = true };

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-rl");
        await WaitForFetchToFinish(vm);

        Assert.True(vm.HasLoadVersionsError);
        Assert.Contains("限流", vm.LoadVersionsError);
    }

    [Fact]
    public async Task OnSelect_FetchReturnsEmpty_LoadVersionsErrorSet()
    {
        Seed("pkg-empty", "https://github.com/ltdrdata/foo");
        var versionService = new ConfigurableVersionService(
            ("pkg-empty", new List<VersionInfo>()));

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-empty");
        await WaitForFetchToFinish(vm);

        Assert.True(vm.HasLoadVersionsError);
        Assert.Contains("未找到", vm.LoadVersionsError);
    }

    [Fact]
    public async Task OnSelect_FetchThrowsGeneric_LoadVersionsErrorSet()
    {
        Seed("pkg-fail", "https://github.com/ltdrdata/foo");
        var versionService = new ConfigurableVersionService() { ThrowGeneric = "网络断开" };

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-fail");
        await WaitForFetchToFinish(vm);

        Assert.True(vm.HasLoadVersionsError);
        Assert.Contains("网络断开", vm.LoadVersionsError);
    }

    // ─────────── Switching / cancellation ───────────

    [Fact]
    public async Task OnSelect_RapidSwitch_FirstFetchCancelledSecondWins()
    {
        // 两个节点,设置 first node fetch 阻塞,用户立即切到 second。
        // first fetch 应该被 cancel(cts.Cancel),UI 应该只反映 second 的状态。
        Seed("first", "https://github.com/a/one");
        Seed("second", "https://github.com/a/two");

        var versionService = new ConfigurableVersionService(
            ("first", new List<VersionInfo> { new() { Tag = "v1.0.0" } }),
            ("second", new List<VersionInfo>
            {
                new() { Tag = "v2.0.0" },
                new() { Tag = "v1.0.0" },
            }));
        // 加点延迟让 cancel 能跑在 fetch 完成之前
        versionService.Delay = TimeSpan.FromMilliseconds(200);

        var vm = NewVm(versionService);
        vm.Selected = vm.PagedEntries.First(e => e.Package == "first");
        // 短暂延迟让 first fetch 进入 in-flight
        await Task.Delay(50);
        // 立即切到 second
        vm.Selected = vm.PagedEntries.First(e => e.Package == "second");
        await WaitForFetchToFinish(vm);

        Assert.False(vm.IsLoadingVersions);
        // ComboBox 应该显示 second 的版本
        Assert.Equal(2, vm.SelectedVersions.Count);
        Assert.Equal("v2.0.0", vm.SelectedVersion?.Tag);
    }

    // ─────────── Test helpers ───────────

    /// <summary>
    /// 配置化的 GitHubVersionService:固定返回配置值,可选 throw 异常 + 延迟,
    /// 完全同步(返回 Task.FromResult 或者 Task&lt;Exception&gt;)。测试用
    /// <see cref="WaitForFetchToFinish"/> 轮询 VM 状态确认 in-flight 完成。
    /// </summary>
    private sealed class ConfigurableVersionService : GitHubVersionService
    {
        private readonly Dictionary<string, List<VersionInfo>> _byNode = new();
        public int CallCount { get; private set; }
        public bool ThrowRateLimit { get; init; }
        public string? ThrowGeneric { get; init; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public ConfigurableVersionService(
            params (string NodeId, List<VersionInfo> Versions)[] setups)
            : base(new System.Net.Http.HttpClient(
                new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object))
        {
            foreach (var (id, vs) in setups) _byNode[id] = vs;
        }

        public override async Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
        {
            CallCount++;
            if (Delay > TimeSpan.Zero)
            {
                try { await Task.Delay(Delay, ct); }
                catch (OperationCanceledException)
                {
                    // 用户切节点触发 cancel,直接吞掉 — VM 当作"未完成"
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (ThrowRateLimit) throw new RateLimitException();
            if (ThrowGeneric is not null) throw new Exception(ThrowGeneric);
            var result = new Dictionary<string, List<VersionInfo>>();
            foreach (var n in nodes)
            {
                if (_byNode.TryGetValue(n.Id, out var versions))
                {
                    result[n.Id] = versions;
                }
            }
            return result;
        }
    }

    /// <summary>FakeRefreshService — extend selected-meta test's stub for parity。</summary>
    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
            => Task.FromResult(RefreshResult.Ok(0));

        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override Task<CatalogFetchResult> FetchAsync(
                string url, CancellationToken ct = default)
                => throw new NotImplementedException();
        }
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")))) { }
    }
}
