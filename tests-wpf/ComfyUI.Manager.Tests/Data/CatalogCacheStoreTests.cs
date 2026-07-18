using System;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogCacheStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public CatalogCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"catalog-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "catalog-cache.db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Open_CreatesDbFileAndCatalogCacheTable_WhenMissing()
    {
        var store = new CatalogCacheStore(_dbPath);

        using var conn = store.Open();

        Assert.True(File.Exists(_dbPath));
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_cache'";
        var result = cmd.ExecuteScalar();
        Assert.Equal("catalog_cache", (string?)result);
    }

    [Fact]
    public void Open_IsIdempotent_CalledTwice_DoesNotFail()
    {
        var store = new CatalogCacheStore(_dbPath);
        store.Open();
        using var conn = store.Open();
        Assert.NotNull(conn);
    }

    [Fact]
    public void Constructor_WithPath_ExposesDbPath()
    {
        var store = new CatalogCacheStore(_dbPath);
        Assert.Equal(_dbPath, store.DbPath);
    }
}
