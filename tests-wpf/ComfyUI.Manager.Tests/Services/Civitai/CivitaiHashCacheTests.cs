using System;
using System.IO;
using Xunit;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CivitaiHashCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"civitai-hash-test-{Guid.NewGuid():N}.sqlite");
    private readonly CivitaiHashCache _cache;

    public CivitaiHashCacheTests()
    {
        _cache = new CivitaiHashCache(_dbPath);
    }

    public void Dispose()
    {
        _cache.Dispose();
        // Microsoft.Data.Sqlite can leave file handles open on Windows; clear pools + sweep
        // wal/shm sidecars to allow File.Delete. Mirrors CatalogCacheStoreV614MigrationTests pattern.
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { /* teardown-only — never fail the test on cleanup */ }
    }

    [Fact]
    public void Store_ThenLookup_WithSameKey_ReturnsHash()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC123DEF456");
        var result = _cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000000);
        Assert.Equal("ABC123DEF456", result);
    }

    [Fact]
    public void Lookup_WithDifferentMtime_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000001));
    }

    [Fact]
    public void Lookup_WithDifferentSize_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12346, 1700000000000));
    }

    [Fact]
    public void Lookup_WithDifferentPath_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\bar.safetensors", 12345, 1700000000000));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        _cache.Clear();
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000000));
    }
}
