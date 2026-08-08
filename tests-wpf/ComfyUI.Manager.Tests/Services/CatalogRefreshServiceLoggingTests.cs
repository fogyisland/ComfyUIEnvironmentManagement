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

public class CatalogRefreshServiceLoggingTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _tempRoot;
    private readonly Settings _settings;

    public CatalogRefreshServiceLoggingTests()
    {
        _db = new TestDb();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"catalog-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _settings = new Settings
        {
            QuerySources = new List<NodeSource>
            {
                new() { Name = "src", Url = "https://example.com/catalog.json" },
            },
            ActiveQuerySourceName = "src",
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

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
    public async Task RefreshAsync_Success_Logs_Info_WithCounts()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-a" },
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-b" },
            },
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("INFO ") && l.Contains("完成") && l.Contains("upsert_count=2"));
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_Logs_Warn()
    {
        var settingsNoSource = new Settings
        {
            QuerySources = new(),
            ActiveQuerySourceName = "nonexistent",
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            new FakeCatalogFetcher(),
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settingsNoSource,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("WARN ") && l.Contains("未配置查询源"));
    }

    [Fact]
    public async Task RefreshAsync_FetcherThrows_Logs_Error()
    {
        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("ERROR") && l.Contains("dns fail"));
    }
}