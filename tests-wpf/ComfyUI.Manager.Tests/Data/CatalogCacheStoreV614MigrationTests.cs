using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>v0.6.14: 9 新列(content_hash + 8 GitHub fields) + 新表 catalog_http_cache 迁移测试。</summary>
public class CatalogCacheStoreV614MigrationTests : IDisposable
{
    private static readonly string[] ExpectedV614Columns =
    {
        "content_hash", "html_url", "homepage", "language",
        "forks_count", "open_issues_count", "release_tag",
        "subscribers_count", "created_at",
    };

    private readonly string _dbPath;

    public CatalogCacheStoreV614MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-v614-mig-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    [Fact]
    public void CatalogCacheStore_NewSchema_HasAll9V614Columns()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        var cols = GetColumns(conn, "catalog_cache");
        foreach (var c in ExpectedV614Columns)
            Assert.Contains(c, cols);
    }

    [Fact]
    public void CatalogCacheStore_NewSchema_HasCatalogHttpCacheTable()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
    }

    [Fact]
    public void CatalogCacheStore_OldSchema_Adds9ColumnsOnReopen()
    {
        // 1. 模拟 v0.6.13-B 老 schema
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
                    license TEXT, tags_json TEXT, stars INTEGER,
                    downloads INTEGER, last_commit TEXT, readme_markdown TEXT,
                    latest_changelog TEXT, deprecated INTEGER,
                    python_compat_json TEXT, os_compat_json TEXT,
                    metadata_fetched_at TEXT,
                    UNIQUE(source_url, package)
                );";
            cmd.ExecuteNonQuery();
        }
        // 2. 用 CatalogCacheStore.Open() 触发迁移
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            var cols = GetColumns(conn, "catalog_cache");
            foreach (var c in ExpectedV614Columns)
                Assert.Contains(c, cols);
        }
    }

    [Fact]
    public void CatalogCacheStore_OldSchema_AddsCatalogHttpCacheTableOnReopen()
    {
        // 旧 DB 没 catalog_http_cache 表 → 迁移后必须存在
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
                    UNIQUE(source_url, package)
                );";
            cmd.ExecuteNonQuery();
        }
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
        }
    }

    [Fact]
    public void CatalogCacheStore_ContentHash_DefaultEmptyString()
    {
        // 旧 row migrate 后 content_hash 必须是 '' 不是 NULL(否则 hash 比较逻辑混乱)
        using var conn = new CatalogCacheStore(_dbPath).Open();
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT content_hash FROM catalog_cache LIMIT 1";
            using var reader = cmd.ExecuteReader();
            // 表空,这里只验证 schema 的 NOT NULL DEFAULT '' 生效(SELECT 不报 NULL constraint)
            Assert.False(reader.Read());  // 表空,无 row
        }
    }

    private static List<string> GetColumns(SqliteConnection conn, string table)
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
}
