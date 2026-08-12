using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>v0.6.13-B: MetadataCache 本地 24h TTL cache 测试。</summary>
public class MetadataCacheTests : IDisposable
{
    private readonly string _filePath;

    public MetadataCacheTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"meta-cache-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    private static CachedMetadata MakeData(DateTime fetchedAt) => new(
        License: "MIT",
        Tags: new[] { "a", "b" },
        Stars: 100,
        Downloads: 50,
        LastCommit: "2026-08-10T12:00:00Z",
        ReadmeMarkdown: "# hi",
        LatestChangelog: "## v1",
        Deprecated: false,
        PythonCompat: new[] { "3.10" },
        OsCompat: new[] { "windows" },
        FetchedAt: fetchedAt);

    [Fact]
    public async Task TryGet_FreshEntry_ReturnsCachedData()
    {
        var cache = new MetadataCache(_filePath);
        var now = DateTime.UtcNow;
        await cache.SaveAsync("foo/bar", MakeData(now), CancellationToken.None);
        var got = await cache.TryGetAsync("foo/bar", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("MIT", got!.License);
        Assert.Equal(100, got.Stars);
        Assert.Equal(new[] { "a", "b" }, got.Tags);
    }

    [Fact]
    public async Task TryGet_StaleEntry_ReturnsNull()
    {
        var cache = new MetadataCache(_filePath);
        var staleTime = DateTime.UtcNow.AddHours(-25);
        await cache.SaveAsync("foo/bar", MakeData(staleTime), CancellationToken.None);
        var got = await cache.TryGetAsync("foo/bar", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task TryGet_MissingFile_ReturnsNull()
    {
        var cache = new MetadataCache(_filePath);
        var got = await cache.TryGetAsync("foo/bar", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task SaveAsync_AtomicWrite_NoTempFileLeftBehind()
    {
        var cache = new MetadataCache(_filePath);
        await cache.SaveAsync("foo/bar", MakeData(DateTime.UtcNow), CancellationToken.None);
        Assert.True(File.Exists(_filePath));
        var tempPath = _filePath + ".tmp";
        Assert.False(File.Exists(tempPath), "temp file should be renamed, not left behind");
    }
}