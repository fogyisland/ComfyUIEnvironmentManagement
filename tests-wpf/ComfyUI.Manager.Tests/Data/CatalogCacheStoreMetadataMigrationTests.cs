using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>v0.6.13-B: catalog_cache 表 +11 metadata 列 + 3 索引的迁移测试。</summary>
public class CatalogCacheStoreMetadataMigrationTests : IDisposable
{
    private readonly string _dbPath;

    public CatalogCacheStoreMetadataMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"comfy-meta-mig-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // WAL 模式下主 db 文件被 wal/shm 句柄持有,直接删会 IOException。
        // ClearAllPools 释放池中连接,跟 CatalogCacheStoreTests 同模式。
        try
        {
            SqliteConnection.ClearAllPools();
            var wal = _dbPath + "-wal";
            var shm = _dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { /* best-effort cleanup */ }
    }

    private static readonly string[] ExpectedNewColumns =
    {
        "license", "tags_json", "stars", "downloads", "last_commit",
        "readme_markdown", "latest_changelog", "deprecated",
        "python_compat_json", "os_compat_json", "metadata_fetched_at",
    };

    private static readonly string[] ExpectedNewIndexes =
    {
        "idx_catalog_cache_stars", "idx_catalog_cache_downloads", "idx_catalog_cache_deprecated",
    };

    [Fact]
    public void CatalogCacheStore_NewSchema_AllColumnsPresent()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        var cols = GetColumns(conn, "catalog_cache");
        foreach (var c in ExpectedNewColumns)
        {
            Assert.Contains(c, cols);
        }
    }

    [Fact]
    public void CatalogCacheStore_NewSchema_AllIndexesPresent()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        var idxs = GetIndexes(conn);
        foreach (var i in ExpectedNewIndexes)
        {
            Assert.Contains(i, idxs);
        }
    }

    [Fact]
    public void CatalogCacheStore_OldSchema_AddsNewColumnsOnReopen()
    {
        // 1. 模拟老 schema(只有 v0.6.7.4 加的列,没新列)
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE catalog_cache (
                    id TEXT PRIMARY KEY,
                    source_url TEXT NOT NULL,
                    package TEXT NOT NULL,
                    raw_metadata TEXT NOT NULL,
                    cached_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    latest_version TEXT,
                    author TEXT, description TEXT, install_type TEXT,
                    reference TEXT, last_update TEXT, pip_json TEXT,
                    UNIQUE(source_url, package)
                );";
            cmd.ExecuteNonQuery();
        }
        // 2. 用 CatalogCacheStore.Open() 触发迁移
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            var cols = GetColumns(conn, "catalog_cache");
            foreach (var c in ExpectedNewColumns)
            {
                Assert.Contains(c, cols);
            }
        }
    }

    private static System.Collections.Generic.List<string> GetColumns(SqliteConnection conn, string table)
    {
        var result = new System.Collections.Generic.List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(1));
        }
        return result;
    }

    private static System.Collections.Generic.List<string> GetIndexes(SqliteConnection conn)
    {
        var result = new System.Collections.Generic.List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}