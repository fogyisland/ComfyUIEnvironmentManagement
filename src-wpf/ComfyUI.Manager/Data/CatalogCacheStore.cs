using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// CatalogCacheStore:窄化的 SQLite 连接工厂,只服务 <c>catalog_cache</c> 表。
/// db 文件位于 &lt;AppBaseDir&gt;/Data/catalog-cache.db,随包发布走,不混入
/// %APPDATA% 的用户数据。
/// v1.0.0:目录重构 data/ → Data/(PascalCase 跟其它顶层目录一致)。
/// </summary>
public sealed class CatalogCacheStore
{
    public string DbPath { get; }

    public CatalogCacheStore()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, "Data");
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

        // v0.6.7.4:从 raw_metadata 抽出的 6 个 typed 列 — 加速查询 + UI 展示
        // raw_metadata 仍完整保留作 fallback(G6)。
        EnsureColumn(conn, "catalog_cache", "author", "TEXT");
        EnsureColumn(conn, "catalog_cache", "description", "TEXT");
        EnsureColumn(conn, "catalog_cache", "install_type", "TEXT");
        EnsureColumn(conn, "catalog_cache", "reference", "TEXT");
        EnsureColumn(conn, "catalog_cache", "last_update", "TEXT");
        EnsureColumn(conn, "catalog_cache", "pip_json", "TEXT");

        // v0.6.13-B: GitHub metadata 11 列(License/Tags/Stars/Downloads/LastCommit/
        // Readme/Changelog/Deprecated/PythonCompat/OsCompat/FetchedAt)。JSON-array
        // 字段用 *_json 后缀,跟 v0.6.7.4 pip_json 同模式。
        EnsureColumn(conn, "catalog_cache", "license", "TEXT");
        EnsureColumn(conn, "catalog_cache", "tags_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "stars", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "downloads", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "last_commit", "TEXT");
        EnsureColumn(conn, "catalog_cache", "readme_markdown", "TEXT");
        EnsureColumn(conn, "catalog_cache", "latest_changelog", "TEXT");
        EnsureColumn(conn, "catalog_cache", "deprecated", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "python_compat_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "os_compat_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "metadata_fetched_at", "TEXT");

        // v0.6.14: 增量刷新 — content_hash(SHA256 of canonical entry JSON)
        // + 8 个新 GitHub 字段(html_url/homepage/language/forks_count/
        // open_issues_count/release_tag/subscribers_count/created_at)
        // content_hash NOT NULL DEFAULT '' 让旧 row 自动回填空串
        EnsureColumn(conn, "catalog_cache", "content_hash",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "catalog_cache", "html_url", "TEXT");
        EnsureColumn(conn, "catalog_cache", "homepage", "TEXT");
        EnsureColumn(conn, "catalog_cache", "language", "TEXT");
        EnsureColumn(conn, "catalog_cache", "forks_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "open_issues_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "release_tag", "TEXT");
        EnsureColumn(conn, "catalog_cache", "subscribers_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "created_at", "TEXT");

        // v0.6.14: HTTP cache 表 — per source URL 存 ETag/Last-Modified
        // 同 DB(不开 JSON 文件,原子事务)。FetchedAt 用于 debug / 排查过期。
        using (var hc = conn.CreateCommand())
        {
            hc.CommandText = @"
                CREATE TABLE IF NOT EXISTS catalog_http_cache (
                    url TEXT PRIMARY KEY,
                    etag TEXT,
                    last_modified TEXT,
                    fetched_at TEXT NOT NULL
                );";
            hc.ExecuteNonQuery();
        }

        // 3 个排序/过滤索引(stars/downloads 降序 + deprecated 0 过滤)
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_stars ON catalog_cache(stars DESC);
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_downloads ON catalog_cache(downloads DESC);
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_deprecated ON catalog_cache(deprecated);";
            idx.ExecuteNonQuery();
        }

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
