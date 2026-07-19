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
}