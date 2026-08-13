using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public bool Force304 { get; set; }  // v0.6.14

        /// <summary>v0.6.14: 记录 service 传下来的 conditional headers,验证 HTTP cache 读到了。</summary>
        public string? LastEtagSeen { get; private set; }
        public string? LastLastModifiedSeen { get; private set; }

        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }

        public override Task<CatalogFetchResult> FetchAsync(
            string url, string? etag, string? lastModified, CancellationToken ct = default)
        {
            LastEtagSeen = etag;
            LastLastModifiedSeen = lastModified;
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            if (Force304)
                return Task.FromResult(new CatalogFetchResult(
                    Is304: true, Entries: null, NewEtag: null, NewLastModified: null));
            return Task.FromResult(new CatalogFetchResult(
                Is304: false, Entries: EntriesToReturn,
                NewEtag: "\"v1\"", NewLastModified: null));
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

    /// <summary>
    /// v0.6.13-B.2 hotfix:version service 抛 RateLimitException(模拟 60/h 触发
    /// 后 GitHubVersionService 真正抛的异常),CatalogRefreshService 顶层 catch
    /// fail-soft,refresh 仍然 success,version_count=0,后续 metadata step 继续跑。
    /// 之前路径是"等 9 分钟让 5883 个 entry 全静默 403 失败",这里改成 ~60s 报错并
    /// 保留部分结果(虽然 versions 在抛 RateLimitException 时被丢弃,但 catalog
    /// 本身 entry 仍写入 DB)。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_VersionServiceThrowsRateLimit_SucceedsWithZeroCount()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new()
                {
                    Id = "node-c",
                    Package = "ComfyUI-C",
                    RawMetadata = new Dictionary<string, object?>
                    {
                        ["reference"] = "https://github.com/ownerC/repoC",
                    },
                },
            },
        };
        var settings = new Settings
        {
            GitHubToken = "",  // 无 token 触发 60/h 限流
            FetchNodeVersionsOnRefresh = true,
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var rateLimitedSvc = new RateLimitedThrowingVersionService();
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settings,
            versionService: rateLimitedSvc);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"expected success despite rate limit, got: {result.Error}");
        Assert.Equal(1, result.EntryCount);
        Assert.Equal(0, result.VersionCount);
        Assert.Equal(1, rateLimitedSvc.CallCount);
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

    /// <summary>
    /// v0.6.13-B.2 hotfix:模拟无 token + 60/h 触发后 GitHubVersionService
    /// 抛 RateLimitException,验证 CatalogRefreshService 顶层 catch 不再让
    /// refresh 整个失败(之前会通过 catch (Exception ex) → Fail 路径)。
    /// </summary>
    private sealed class RateLimitedThrowingVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public RateLimitedThrowingVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new RateLimitException();
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

    /// <summary>
    /// v0.6.14: 真 fake http cache store — 内存存 etag/lastModified,refresh 测试用。
    /// 基类的 GetAsync/PutAsync 是 virtual,这里 override(不能用 new —— service
    /// 持的是基类引用,new 不会被虚派发,fake 就形同虚设)。
    /// </summary>
    private sealed class FakeCatalogHttpCacheStore : CatalogHttpCacheStore
    {
        public Dictionary<string, (string? etag, string? lastMod)> Store { get; } = new();
        public bool ThrowOnGet { get; set; }

        public FakeCatalogHttpCacheStore()
            : base(Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.db")) { }

        public override Task<(string? Etag, string? LastModified)> GetAsync(
            string url, CancellationToken ct = default)
        {
            if (ThrowOnGet) throw new InvalidOperationException("corrupted");
            return Task.FromResult(Store.TryGetValue(url, out var v) ? v : (null, null));
        }

        public override Task PutAsync(string url, string? etag, string? lastModified,
            CancellationToken ct = default)
        {
            Store[url] = (etag, lastModified);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// v0.6.14: HTTP cache 304 — RefreshAsync 短路返回 SkippedCount = 现有 rows。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_304NotModified_ShortCircuitsReturnsZeroChanges()
    {
        var fetcher = new FakeCatalogFetcher { Force304 = true };
        var httpCache = new FakeCatalogHttpCacheStore();
        // 预存 etag 让 fetcher 发 If-None-Match
        var url = _settings.QuerySources[0].Url;
        await httpCache.PutAsync(url, "\"v1\"", null);

        // 预填 DB 一行
        var pre = new CatalogCacheStore(_db.Path);
        new CatalogRepository(pre).UpsertBatch(new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-pre",
                RawMetadata = new Dictionary<string, object?>(),
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            }
        });

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: httpCache);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.EntryCount);
        Assert.Equal(1, result.SkippedCount);  // pre-filled row 是 unchanged
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.DeletedCount);
        // conditional header 真被读出来传给 fetcher 了
        Assert.Equal("\"v1\"", fetcher.LastEtagSeen);
    }

    /// <summary>
    /// v0.6.14: 200 fetch 后新 ETag/Last-Modified 要写回 http cache store。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_Success_SavesNewEtagToHttpCache()
    {
        var url = _settings.QuerySources[0].Url;
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-etag" },
            }
        };
        var httpCache = new FakeCatalogHttpCacheStore();
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: httpCache);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.True(httpCache.Store.ContainsKey(url));
        Assert.Equal("\"v1\"", httpCache.Store[url].etag);
    }

    /// <summary>
    /// v0.6.14: 旧 DB 首次 refresh — 所有 entry content_hash='' 视为 "added"。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FirstRefreshWithOldDb_AllEntriesAdded()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-a" },
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-b" },
            }
        };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.DeletedCount);
    }

    /// <summary>
    /// v0.6.14: DB 已有 entries,refresh 后 hash 不变 → 全部 skipped。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_HashUnchanged_AllEntriesSkipped()
    {
        var url = _settings.QuerySources[0].Url;
        // 预填 DB 2 行
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var preEntries = new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = "x2", SourceUrl = url, Package = "pkg-b",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-b" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        };
        pre.UpsertBatch(preEntries);
        // hash 已写入 DB(走 UpsertBatch 自动算)

        var fetcher = new FakeCatalogFetcher { EntriesToReturn = preEntries.ToList() };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(2, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: DB 已有 entry,JSON 改了 title → hash 变 → 走 Updated 路径。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_HashChanged_EntryUpdated()
    {
        var url = _settings.QuerySources[0].Url;
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var original = new CatalogEntry {
            Id = "x1", SourceUrl = url, Package = "pkg-a",
            RawMetadata = new Dictionary<string, object?> {
                ["id"] = "pkg-a", ["title"] = "Old Title"
            },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        pre.UpsertBatch(new[] { original });

        var modified = new CatalogEntry {
            Id = "x2", SourceUrl = url, Package = "pkg-a",
            RawMetadata = new Dictionary<string, object?> {
                ["id"] = "pkg-a", ["title"] = "New Title"  // ← 改了
            },
            CachedAt = "2026-08-13T00:00:01Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() { modified } };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: catalog JSON 加 1 条新 entry → AddedCount=1。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NewEntry_Added()
    {
        var url = _settings.QuerySources[0].Url;
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        pre.UpsertBatch(new[] { new CatalogEntry {
            Id = "x1", SourceUrl = url, Package = "pkg-existing",
            RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-existing" },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        }});

        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() {
            new CatalogEntry {
                Id = Guid.NewGuid().ToString(), SourceUrl = url, Package = "pkg-existing",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-existing" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = Guid.NewGuid().ToString(), SourceUrl = url, Package = "pkg-new",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-new" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        }};
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: catalog JSON 删 1 条 → 硬删 catalog_cache + node_versions,DeletedCount=1。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_RemovedEntry_CascadeDeletesNodeVersions()
    {
        var url = _settings.QuerySources[0].Url;
        var store = new CatalogCacheStore(_db.Path);
        var repo = new CatalogRepository(store);

        // 预填 2 entry
        repo.UpsertBatch(new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = "x2", SourceUrl = url, Package = "pkg-b",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-b" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        });
        // 预填 node_versions 给 x1 和 x2(x2 会被 cascade 删,x1 保留)
        // v0.6.14: UpsertBatch 现在接 (source_url, package, VersionInfo) —— catalog_cache 里
        // (url, "pkg-a") 的 node_id 是 "x1",(url, "pkg-b") 的 node_id 是 "x2"
        var versionRepo = new NodeVersionRepository(store);
        versionRepo.UpsertBatch(new[] {
            (url, "pkg-a", new VersionInfo {
                Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }),
            (url, "pkg-b", new VersionInfo {
                Tag = "v2.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }),
        });

        // refresh 只返回 pkg-a,pkg-b 被删
        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            }
        }};
        var svc = new CatalogRefreshService(
            fetcher, repo, _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.DeletedCount);
        // pkg-b 硬删
        Assert.DoesNotContain(repo.Search("pkg-b", 10), e => e.Package == "pkg-b");
        // x1 的 node_versions 仍在,x2 的被 cascade 删
        Assert.Single(versionRepo.ListByNode("x1"));
        Assert.Empty(versionRepo.ListByNode("x2"));
    }

    /// <summary>
    /// v0.6.14 hotfix: 增量 refresh 时 Updated entry 拿到 CatalogFetcher 给的新
    /// GUID("new-guid"),version service 返回 "v2.0.0" 给这个 entry —— update
    /// latest_version 必须写到 catalog_cache 里 pkg-x 的现有 row(老 GUID "old-guid-A"),
    /// 不能因为 GUID 不匹配而静默漏更新。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_UpdatedEntryGetsNewGuid_LatestVersionStillWrittenToExistingRow()
    {
        var url = _settings.QuerySources[0].Url;
        var store = new CatalogCacheStore(_db.Path);
        var repo = new CatalogRepository(store);

        // 预填老 GUID 的 catalog row + 一条最新版本 v1.0.0(模拟上次 refresh 写过)
        repo.UpsertBatch(new[] {
            new CatalogEntry {
                Id = "old-guid-A", SourceUrl = url, Package = "pkg-x",
                RawMetadata = new Dictionary<string, object?> {
                    ["id"] = "pkg-x",
                    ["title"] = "Old Title",  // ← 故意造 hash 不同
                    ["reference"] = "https://github.com/foo/bar",
                },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
                LatestVersion = "v1.0.0",
            },
        });

        // refresh 模拟:CatalogFetcher 给 pkg-x 分配全新 GUID + 改了 title
        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() {
            new CatalogEntry {
                Id = "new-guid-B", SourceUrl = url, Package = "pkg-x",
                RawMetadata = new Dictionary<string, object?> {
                    ["id"] = "pkg-x",
                    ["title"] = "New Title",  // hash 变了 → Updated 路径
                    ["reference"] = "https://github.com/foo/bar",
                },
                CachedAt = "2026-08-13T01:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            }
        }};

        // version service 模拟 GitHub 返 v2.0.0 给 pkg-x
        // 注意 FakeVersionServiceForPackage 仍然按 entry.Id 键返回(模拟 GitHubVersionService 行为)
        var versions = new Dictionary<string, List<VersionInfo>> {
            ["new-guid-B"] = new() {
                new() { Tag = "v2.0.0", PublishedAt = "2026-08-13T01:00:00Z", IsPrerelease = false },
            },
        };
        var versionSvc = new CountingVersionService(versions);
        var versionRepo = new NodeVersionRepository(store);

        // 把 settings 改成开 FetchNodeVersionsOnRefresh(默认是 false,跟既有测试一致)
        var settingsWithVersions = new Settings
        {
            QuerySources = _settings.QuerySources,
            ActiveQuerySourceName = _settings.ActiveQuerySourceName,
            GitHubToken = _settings.GitHubToken,
            FetchNodeVersionsOnRefresh = true,
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(settingsWithVersions, @"D:\ToolDevelop\ComfyUI");

        var svc = new CatalogRefreshService(
            fetcher, repo, settingsWithVersions,
            versionService: versionSvc,
            versionRepo: versionRepo,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);   // hash 变了 → Updated
        // 版本数 = UpdateLatestVersions 写到现有 row 的数(1)
        Assert.Equal(1, result.VersionCount);

        // 关键 assertion:existing row 的 latest_version 被更新到 v2.0.0
        // —— 哪怕 entry.Id 现在是 new-guid-B,但原 row 仍按 (source_url, package) 寻址被命中
        var existing = repo.Search("pkg-x", 10).Single();
        Assert.Equal(url, existing.SourceUrl);
        Assert.Equal("pkg-x", existing.Package);
        Assert.Equal("v2.0.0", existing.LatestVersion);
    }
}