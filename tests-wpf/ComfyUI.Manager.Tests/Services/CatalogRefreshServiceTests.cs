using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogRefreshServiceTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;

    public CatalogRefreshServiceTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }

        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }

        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        {
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            return Task.FromResult(EntriesToReturn);
        }
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_ReturnsFailure()
    {
        var svc = new CatalogRefreshService(
            new FakeCatalogFetcher(),
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            new Settings
            {
                QuerySources = new(),  // 空列表 → 无 active source
                ActiveQuerySourceName = "nonexistent",
            });

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("未配置查询源", result.Error);
        Assert.Equal(0, result.EntryCount);
    }

    [Fact]
    public async Task RefreshAsync_Success_UpsertsEntriesAndReturnsCount()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-x" },
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-y" },
            },
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.EntryCount);
        Assert.Null(result.Error);

        var entries = new CatalogRepository(new CatalogCacheStore(_db.Path)).Search("", 10);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Package == "pkg-x");
    }

    [Fact]
    public async Task RefreshAsync_FetcherThrows_ReturnsFailureWithLocalCacheStillUsable()
    {
        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("拉取失败", result.Error);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task RefreshAsync_SetsSourceUrlOnEachEntry()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-z" },
            },
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        await svc.RefreshAsync();

        var entries = new CatalogRepository(new CatalogCacheStore(_db.Path)).Search("", 10);
        Assert.Equal(_settings.QuerySources[0].Url, entries[0].SourceUrl);
    }

    [Fact]
    public async Task RefreshAsync_StreamsEachEntry_ViaProgress()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "stream-a" },
                new() { Id = Guid.NewGuid().ToString(), Package = "stream-b" },
                new() { Id = Guid.NewGuid().ToString(), Package = "stream-c" },
            },
        };
        var reported = new List<string>();
        var progress = new Progress<CatalogEntry>(e => reported.Add(e.Package));
        // give Progress<T> a sync context so callbacks fire before the awaiter returns
        var prevCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var svc = new CatalogRefreshService(
                fetcher,
                new CatalogRepository(new CatalogCacheStore(_db.Path)),
                _settings);
            var result = await svc.RefreshAsync(progress);
            // drain pending Progress<T> posts on the test sync context
            await Task.Delay(50);
            Assert.True(result.Success);
            Assert.Equal(3, result.EntryCount);
            Assert.Equal(new[] { "stream-a", "stream-b", "stream-c" }, reported);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }

    // v0.6.11 T3: gate 改用 FetchNodeVersionsOnRefresh 开关(而非 token)。
    // 老逻辑:配 token 就拉;新逻辑:开关 ON 就拉(token 仅决定是否鉴权)。

    [Fact]
    public async Task RefreshAsync_FetchNodeVersionsOff_EvenWithToken_DoesNotCallVersionService()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
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
            },
        };
        var settings = new Settings
        {
            GitHubToken = "ghp_test_token_xxx",  // 配 token
            FetchNodeVersionsOnRefresh = false,  // 但开关 OFF
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var throwingSvc = new ThrowingVersionService();
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settings,
            versionService: throwingSvc);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.EntryCount);
        Assert.Equal(0, result.VersionCount);
        Assert.Equal(0, throwingSvc.CallCount);  // ← 关键:开关 OFF 时根本不会调
    }

    [Fact]
    public async Task RefreshAsync_FetchNodeVersionsOn_NoToken_GitHubRateLimited_VersionCountZeroSuccessTrue()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new()
                {
                    Id = "node-a",
                    Package = "ComfyUI-A",
                    RawMetadata = new Dictionary<string, object?>
                    {
                        ["reference"] = "https://github.com/ownerA/repoA",
                    },
                },
            },
        };
        var settings = new Settings
        {
            GitHubToken = "",  // 无 token
            FetchNodeVersionsOnRefresh = true,
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        // 模拟 GitHub 401/403/429 限流:versionSvc 返回空 dict(类似
        // GitHubVersionService.GetReleasesAsync 失败时返回 List<VersionInfo>())
        var rateLimitedSvc = new EmptyVersionService();
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settings,
            versionService: rateLimitedSvc);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"expected success despite rate limit, got: {result.Error}");
        Assert.Equal(1, result.EntryCount);
        Assert.Equal(0, result.VersionCount);
        Assert.Equal(1, rateLimitedSvc.CallCount);  // 调到了,但返回 0
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RefreshAsync_FetchNodeVersionsOn_WithToken_VersionCountPositive()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new()
                {
                    Id = "node-b",
                    Package = "ComfyUI-B",
                    RawMetadata = new Dictionary<string, object?>
                    {
                        ["reference"] = "https://github.com/ownerB/repoB",
                    },
                },
            },
        };
        var settings = new Settings
        {
            GitHubToken = "ghp_test_token_xxx",
            FetchNodeVersionsOnRefresh = true,
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var versions = new Dictionary<string, List<VersionInfo>>
        {
            ["node-b"] = new()
            {
                new() { Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false },
                new() { Tag = "v0.9.0", PublishedAt = "2025-12-01T00:00:00Z", IsPrerelease = false },
            },
        };
        var countingSvc = new CountingVersionService(versions);
        var cacheStore = new CatalogCacheStore(_db.Path);
        var versionRepo = new NodeVersionRepository(cacheStore);
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(cacheStore),
            settings,
            versionService: countingSvc,
            versionRepo: versionRepo);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.EntryCount);
        // VersionCount = UpdateLatestVersions 返回的"被 UPDATE 的行数",每个 node 1 行,
        // 不是版本数。所以 1 个 node → 1。
        Assert.Equal(1, result.VersionCount);
        Assert.Equal("ghp_test_token_xxx", countingSvc.LastTokenSeen);  // token 仍传入(鉴权)

        // 2 个版本都被 upsert 到 node_versions(测试此机制通过检查总数)
        var allVersions = versionRepo.ListByNode("node-b");
        Assert.Equal(2, allVersions.Count);
        Assert.Contains(allVersions, v => v.Tag == "v1.0.0");
        Assert.Contains(allVersions, v => v.Tag == "v0.9.0");

        var entries = new CatalogRepository(cacheStore).Search("", 10);
        Assert.Single(entries, e => e.LatestVersion == "v1.0.0");
    }

    private sealed class ThrowingVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public ThrowingVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("version service should not be called when gate is OFF");
        }
    }

    private sealed class EmptyVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public EmptyVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new Dictionary<string, List<VersionInfo>>());
        }
    }

    private sealed class CountingVersionService : GitHubVersionService
    {
        public string? LastTokenSeen { get; private set; }
        private readonly Dictionary<string, List<VersionInfo>> _result;
        public CountingVersionService(Dictionary<string, List<VersionInfo>> result)
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object))
        {
            _result = result;
        }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            LastTokenSeen = token;
            return Task.FromResult(_result);
        }
    }
}