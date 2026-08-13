using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15: CatalogRefreshService 的 rate limit 通道 —— service 撞 limit 时
/// 沿 IProgress&lt;RateLimitInfo&gt; 上报给 UI banner + MarkBlocked 到
/// IRateLimitState,下次 refresh 入口凭 IsBlocked 跳过整个 stage。
/// 既有 CatalogRefreshServiceTests / MetadataTests / NoTokenTests 走 happy path,
/// 本文件专注这条通道。
/// </summary>
public class CatalogRefreshServiceProgressTests : IDisposable
{
    private readonly TestDb _db;

    public CatalogRefreshServiceProgressTests()
    {
        _db = new TestDb();
    }

    public void Dispose() => _db.Dispose();

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }

    private sealed class FakeFetcher : CatalogFetcher
    {
        public List<CatalogEntry> Entries { get; } = new()
        {
            new()
            {
                Id = "node-1",
                Package = "ComfyUI-Foo",
                RawMetadata = new Dictionary<string, object?>
                {
                    ["reference"] = "https://github.com/foo/bar",
                },
            },
        };
        public FakeFetcher() : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }
        public override Task<CatalogFetchResult> FetchAsync(
            string url, string? etag, string? lastModified, CancellationToken ct = default)
            => Task.FromResult(new CatalogFetchResult(false, Entries, null, null));
    }

    /// <summary>
    /// 模拟 GitHubVersionService 撞 rate limit:report RateLimitInfo(Version)
    /// + MarkBlocked + return partial(v0.6.14.1 起不抛)。
    /// </summary>
    private sealed class RateLimitReportingVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public long ResetUnix { get; } = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();

        public RateLimitReportingVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }

        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
        {
            CallCount++;
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Version, Remaining: 0, ResetUnix: ResetUnix,
                PartialCount: 5, TotalCount: 10));
            rateLimitState?.MarkBlocked(RateLimitStage.Version, ResetUnix,
                partialCount: 5, totalCount: 10);
            return Task.FromResult(new Dictionary<string, List<VersionInfo>>());
        }
    }

    /// <summary>
    /// 模拟 GitHubCatalogMetadataService 撞 rate limit:report + MarkBlocked
    /// + throw(EnrichAsync 不 swallow,由 CatalogRefreshService 顶层 catch)。
    /// 注意这里传真的 ResetUnix —— RateLimitState.MarkBlocked 对 null resetUnix
    /// 直接早退不记录(T1 设计)。
    /// </summary>
    private sealed class RateLimitReportingMetadataService : GitHubCatalogMetadataService
    {
        public int CallCount { get; private set; }
        public long ResetUnix { get; } = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();

        public RateLimitReportingMetadataService(Settings settings)
            : base(
                new HttpClient(new Mock<HttpMessageHandler>().Object),
                new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json")),
                settings)
        { }

        public override Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            CallCount++;
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Metadata, Remaining: 0, ResetUnix: ResetUnix,
                PartialCount: 3, TotalCount: 8));
            rateLimitState?.MarkBlocked(RateLimitStage.Metadata, ResetUnix,
                partialCount: 3, totalCount: 8);
            throw new RateLimitException();
        }
    }

    /// <summary>调到就炸 —— 用来断言 stage-skip 真的没让它跑。</summary>
    private sealed class ThrowingVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public ThrowingVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException(
                "version service should not be called while rate limit is cooling down");
        }
    }

    private static Settings MakeSettings(bool fetchVersions = false, bool fetchMetadata = false)
    {
        var s = new Settings
        {
            GitHubToken = "ghp_test",
            FetchNodeVersionsOnRefresh = fetchVersions,
            FetchCatalogMetadata = fetchMetadata,
        };
        SettingsDefaults.Apply(s, @"D:\ToolDevelop\ComfyUI");
        return s;
    }

    [Fact]
    public async Task RefreshAsync_VersionRateLimit_ReportsRateLimitInfoAndMarksState()
    {
        var settings = MakeSettings(fetchVersions: true);
        var cacheStore = new CatalogCacheStore(_db.Path);
        var versionSvc = new RateLimitReportingVersionService();
        var state = new RateLimitState();
        var rateLimitProgress = new CapturingProgress<RateLimitInfo>();

        var svc = new CatalogRefreshService(
            new FakeFetcher(),
            new CatalogRepository(cacheStore),
            settings,
            versionService: versionSvc,
            versionRepo: new NodeVersionRepository(cacheStore));

        var result = await svc.RefreshAsync(
            rateLimitProgress: rateLimitProgress, rateLimitState: state);

        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(1, versionSvc.CallCount);

        // 1) RateLimitInfo 沿 IProgress 上报给 UI banner
        var report = Assert.Single(rateLimitProgress.Reports);
        Assert.Equal(RateLimitStage.Version, report.Stage);
        Assert.Equal(0, report.Remaining);
        Assert.Equal(versionSvc.ResetUnix, report.ResetUnix);
        Assert.Equal(5, report.PartialCount);
        Assert.Equal(10, report.TotalCount);

        // 2) state 被 mark 且**没被本轮结束的 Clear 抹掉**(撞了 limit 的 stage 不清)
        Assert.True(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.NotNull(info);
        Assert.Equal(5, info!.PartialCount);
        Assert.Equal(10, info.TotalCount);
    }

    [Fact]
    public async Task RefreshAsync_MetadataRateLimit_ReportsRateLimitInfoAndMarksState()
    {
        var settings = MakeSettings(fetchMetadata: true);
        var metadataSvc = new RateLimitReportingMetadataService(settings);
        var state = new RateLimitState();
        var rateLimitProgress = new CapturingProgress<RateLimitInfo>();

        var svc = new CatalogRefreshService(
            new FakeFetcher(),
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settings,
            metadataService: metadataSvc);

        var result = await svc.RefreshAsync(
            rateLimitProgress: rateLimitProgress, rateLimitState: state);

        // RateLimitException 被 CatalogRefreshService 吞成 fail-soft,refresh 仍成功
        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(0, result.MetadataCount);
        Assert.Equal(1, metadataSvc.CallCount);

        var report = Assert.Single(rateLimitProgress.Reports);
        Assert.Equal(RateLimitStage.Metadata, report.Stage);
        Assert.Equal(0, report.Remaining);
        Assert.Equal(metadataSvc.ResetUnix, report.ResetUnix);
        Assert.Equal(3, report.PartialCount);
        Assert.Equal(8, report.TotalCount);

        Assert.True(state.IsBlocked(RateLimitStage.Metadata, out var info));
        Assert.NotNull(info);
        Assert.Equal(3, info!.PartialCount);
        Assert.Equal(8, info.TotalCount);
    }

    [Fact]
    public async Task RefreshAsync_VersionStateBlocked_SkipsVersionFetch()
    {
        var settings = MakeSettings(fetchVersions: true);
        var cacheStore = new CatalogCacheStore(_db.Path);
        var throwingSvc = new ThrowingVersionService();
        var state = new RateLimitState();
        state.MarkBlocked(RateLimitStage.Version,
            DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds(),
            partialCount: 5, totalCount: 10);

        var svc = new CatalogRefreshService(
            new FakeFetcher(),
            new CatalogRepository(cacheStore),
            settings,
            versionService: throwingSvc,
            versionRepo: new NodeVersionRepository(cacheStore));

        var result = await svc.RefreshAsync(rateLimitState: state);

        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(0, throwingSvc.CallCount);   // ← 关键:整个 stage 被跳过
        Assert.Equal(0, result.VersionCount);
        // skip 路径不动 state —— 冷却状态保留到 reset 时间自然过期
        Assert.True(state.IsBlocked(RateLimitStage.Version, out _));
    }
}
