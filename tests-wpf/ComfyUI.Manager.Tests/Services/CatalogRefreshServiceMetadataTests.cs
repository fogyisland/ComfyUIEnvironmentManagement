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

/// <summary>v0.6.13-B: CatalogRefreshService 接 metadata service — toggle 控制 + fail-soft。</summary>
public class CatalogRefreshServiceMetadataTests : IDisposable
{
    private readonly TestDb _db;

    public CatalogRefreshServiceMetadataTests()
    {
        _db = new TestDb();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeFetcher : CatalogFetcher
    {
        public List<CatalogEntry> Entries { get; } = new()
        {
            new()
            {
                Id = "test-1", Package = "Pkg",
                SourceUrl = "https://example.com/catalog.json",
                RawMetadata = new Dictionary<string, object?> { ["reference"] = "https://github.com/foo/bar" },
                CachedAt = "2026-08-12T00:00:00", ExpiresAt = "2026-08-13T00:00:00",
            },
        };
        public FakeFetcher() : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }
        public override Task<CatalogFetchResult> FetchAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new CatalogFetchResult(false, Entries, null, null));
    }

    /// <summary>
    /// 测试 double:继承 GitHubCatalogMetadataService override EnrichAsync。
    /// Mock 在 AppLogger? optional + sealed ctor 场景下 Castle proxy 拒识,
    /// 用 Fake 更直接(跟 sibling ThrowingVersionService 同模式)。
    /// </summary>
    private sealed class FakeMetadataService : GitHubCatalogMetadataService
    {
        public int CallCount { get; private set; }
        public Action<IList<CatalogEntry>>? OnEnrich { get; set; }
        public Exception? ThrowOnEnrich { get; set; }

        public FakeMetadataService(Settings settings)
            : base(
                new HttpClient(new Mock<HttpMessageHandler>().Object),
                new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json")),
                settings)
        { }

        public override Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            if (ThrowOnEnrich is not null) throw ThrowOnEnrich;
            OnEnrich?.Invoke(entries);
            return Task.FromResult(entries.Count);
        }
    }

    private static Settings MakeSettings(bool fetchMetadata)
        => new()
        {
            GitHubToken = "",
            FetchCatalogMetadata = fetchMetadata,
        };

    [Fact]
    public async Task RefreshAsync_MetadataDisabled_DoesNotCallMetadataService()
    {
        var settings = MakeSettings(fetchMetadata: false);
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var fakeMeta = new FakeMetadataService(settings)
        {
            ThrowOnEnrich = new Exception("should not be called"),
        };

        var svc = new CatalogRefreshService(fetcher, repo, settings,
            metadataService: fakeMeta);
        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(0, result.MetadataCount);
        Assert.Equal(0, fakeMeta.CallCount);  // 关键:开关 OFF 时根本不会调
    }

    [Fact]
    public async Task RefreshAsync_MetadataEnabled_EnrichesAndUpdatesRepo()
    {
        var settings = MakeSettings(fetchMetadata: true);
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var fakeMeta = new FakeMetadataService(settings)
        {
            OnEnrich = entries =>
            {
                foreach (var e in entries)
                {
                    e.License = "MIT";
                    e.Stars = 42;
                }
            },
        };

        var svc = new CatalogRefreshService(fetcher, repo, settings,
            metadataService: fakeMeta);
        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(1, result.MetadataCount);
        Assert.Equal(1, fakeMeta.CallCount);

        var rows = repo.Search("", 10);
        Assert.Equal("MIT", rows[0].License);
        Assert.Equal(42, rows[0].Stars);
    }

    [Fact]
    public async Task RefreshAsync_MetadataThrows_DoesNotFailWholeRefresh()
    {
        var settings = MakeSettings(fetchMetadata: true);
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var fakeMeta = new FakeMetadataService(settings)
        {
            ThrowOnEnrich = new RateLimitException(),
        };

        var svc = new CatalogRefreshService(fetcher, repo, settings,
            metadataService: fakeMeta);
        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"reason={result.Error}");
        Assert.Equal(0, result.MetadataCount);
        // catalog rows 仍在
        var rows = repo.Search("", 10);
        Assert.Single(rows);
    }
}