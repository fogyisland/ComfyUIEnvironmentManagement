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
                latest_version TEXT,
                UNIQUE(source_url, package)
            );";
        cmd.ExecuteNonQuery();

        // 增量升级:旧 db 没有 latest_version 列 → ALTER TABLE ADD COLUMN。
        // PRAGMA table_info 返回每一列一行,检查 latest_version 是否已存在。
        EnsureColumn(conn, "catalog_cache", "latest_version", "TEXT");

        // v0.6.4+:节点历史 release 列表(tag + 发布时间 + 是否 prerelease)
        using (var tbl = conn.CreateCommand())
        {
            tbl.CommandText = @"
                CREATE TABLE IF NOT EXISTS node_versions (
                    node_id TEXT NOT NULL,
                    tag_name TEXT NOT NULL,
                    published_at TEXT NOT NULL,
                    is_prerelease INTEGER NOT NULL DEFAULT 0,
                    fetched_at TEXT NOT NULL,
                    PRIMARY KEY(node_id, tag_name)
                )";
            tbl.ExecuteNonQuery();
        }
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_node_versions_node ON node_versions(node_id, published_at DESC)";
            idx.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        bool exists = false;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = $"PRAGMA table_info({table})";
            using var reader = probe.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();
        }
    }
}
