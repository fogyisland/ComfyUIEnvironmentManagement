using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// CatalogCacheStore:窄化的 SQLite 连接工厂,只服务 <c>catalog_cache</c> 表。
/// db 文件位于 &lt;AppBaseDir&gt;/data/catalog-cache.db,随包发布走,不混入
/// %APPDATA% 的用户数据。
/// </summary>
public sealed class CatalogCacheStore
{
    public string DbPath { get; }

    public CatalogCacheStore()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, "data");
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "catalog-cache.db");
    }

    /// <summary>
    /// Test 注入用。
    /// </summary>
    public CatalogCacheStore(string dbPath)
    {
        DbPath = dbPath;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        InitSchemaIfMissing(conn);
        return conn;
    }

    private static void InitSchemaIfMissing(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS catalog_cache (
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
}
