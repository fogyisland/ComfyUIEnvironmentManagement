using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// SqliteConnectionFactory:用户数据表 db (environments / scanned_nodes /
/// process_state / version_history / nodes 等)。位于 %APPDATA%/ComfyUI-Manager/state.db。
///
/// 升级兼容:首次 v0.6.4 启动时,如果旧的 catalog.db 存在且 state.db 不存在,
/// 自动 File.Move(catalog.db → state.db),把旧 db 里残留的 user 表带过去。
/// 旧 db 里的 catalog_cache 会被丢弃(用户主动去 Settings 重新刷新)。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _dbPath;

    public string DbPath => _dbPath;

    public SqliteConnectionFactory()
    {
        _dbPath = ResolveDbPath();
    }

    /// <summary>
    /// Constructor used by tests to inject an explicit db path.
    /// </summary>
    public SqliteConnectionFactory(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Resolves the user-db path. If a legacy <c>catalog.db</c> is present
    /// and <c>state.db</c> is not, renames it. Caller should not rename the
    /// file out from under running SQLite connections.
    /// </summary>
    private static string ResolveDbPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("COMFY_MGR_DB_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "ComfyUI-Manager");
        Directory.CreateDirectory(dir);

        var newPath = Path.Combine(dir, "state.db");
        var legacyPath = Path.Combine(dir, "catalog.db");
        if (!File.Exists(newPath) && File.Exists(legacyPath))
        {
            // 一次性升级迁移:旧 catalog.db 含混合表,移到 state.db
            // 后旧 db 的 catalog_cache 会被丢弃(用户从 Settings 重新拉)。
            try { File.Move(legacyPath, newPath); }
            catch { /* 容错:rename 失败时仍用旧 db(下次启动再试) */ }
        }
        return newPath;
    }

    /// <summary>
    /// Opens a new SqliteConnection with user-table schema ensured.
    /// Caller owns disposal.
    /// </summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        InitSchemaIfMissing(conn);
        return conn;
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS for all user tables WPF reads from.
    /// Mirrors the schema in <c>tests-wpf/.../Fakes/TestDb.cs</c>.
    /// </summary>
    private static void InitSchemaIfMissing(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS environments (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                root_path TEXT NOT NULL,
                comfyui_layout TEXT NOT NULL,
                comfyui_source TEXT,
                venv_path TEXT,
                python_executable TEXT,
                custom_nodes_path TEXT,
                extra_model_paths_yaml TEXT,
                port INTEGER,
                enabled_node_ids_json TEXT DEFAULT '[]',
                status TEXT DEFAULT 'stopped',
                base_python_path TEXT NOT NULL DEFAULT '',
                python_version TEXT NOT NULL DEFAULT '',
                pid INTEGER
            );
            CREATE TABLE IF NOT EXISTS scanned_nodes (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                package_path TEXT NOT NULL,
                version TEXT,
                author TEXT,
                description TEXT,
                class_mappings TEXT NOT NULL DEFAULT '[]',
                status TEXT NOT NULL DEFAULT 'enabled',
                scan_meta TEXT NOT NULL DEFAULT '{}',
                last_scanned_at TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'env',
                UNIQUE(env_id, package)
            );
            CREATE TABLE IF NOT EXISTS version_history (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                action TEXT NOT NULL,
                version_before TEXT,
                version_after TEXT,
                pkg_version TEXT,
                result TEXT NOT NULL,
                error_message TEXT,
                performed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dep_records (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                source TEXT NOT NULL,
                dep_name TEXT NOT NULL,
                dep_version_spec TEXT,
                scanned_at TEXT NOT NULL,
                UNIQUE(env_id, package, source, dep_name)
            );
            CREATE TABLE IF NOT EXISTS process_state (
                env_id TEXT PRIMARY KEY,
                pid INTEGER NOT NULL,
                port INTEGER NOT NULL,
                started_at TIMESTAMP NOT NULL
            );";
        cmd.ExecuteNonQuery();

        // 增量升级:旧 db 没有 base_python_path / python_version 列 → ALTER TABLE ADD COLUMN。
        // PRAGMA table_info 返回每一列一行,检查列名是否已存在。
        EnsureColumn(conn, "environments", "base_python_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "environments", "python_version", "TEXT NOT NULL DEFAULT ''");
        // BED 列(无 NOT NULL DEFAULT '' — null = "未装",UI BedDisplay 走 default 分支)
        EnsureColumn(conn, "environments", "bed_profile_id", "TEXT");
        EnsureColumn(conn, "environments", "bed_status", "TEXT");
        EnsureColumn(conn, "environments", "bed_failed_reason", "TEXT");
        // v0.6.7.2:用户备注(在 CreateEnvDialog 输入,默认空)
        EnsureColumn(conn, "environments", "notes", "TEXT");
        // v0.6.11:scanned_nodes.source(老 db backfill 为 'env';新唯一索引支持 download 行)
        EnsureColumn(conn, "scanned_nodes", "source", "TEXT NOT NULL DEFAULT 'env'");
        // v0.6.15.1 hotfix:节点 git URL(本地下载行才有,env 装行 NULL 即可)
        EnsureColumn(conn, "scanned_nodes", "repository_url", "TEXT");

        // v0.6.11:支持 (env_id, package, source) 三元组唯一 — 让 download(env_id='', source='download')
        // 不与 env 装(env_id='env-1', source='env')同名包冲突,两个 download 同包也能独立存在。
        // CREATE TABLE 里 UNIQUE(env_id, package) 保留(老 DB 兼容),不破坏既有数据。
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_scanned_nodes_env_pkg_source " +
                "ON scanned_nodes(env_id, package, source)";
            idx.ExecuteNonQuery();
        }

        // v0.6.15.3 hotfix: scanned_nodes.env_id 上有 FK 到 environments.id(老 migration 加的,
        // 当前 source 的 CREATE TABLE 没体现 → 新 DB 没 FK,老 DB 有)。DownloadAsync 写
        // EnvId="" 作 local-download sentinel,但 environments 表若没 id="" 行 → FK 失败,
        // app 崩(SQLite Error 19)。这里 INSERT OR IGNORE 一行 sentinel 让 FK 通过。
        // 不影响 env 装路径(env_id 是真值本来就匹配),也不被 EnvironmentListView 显示
        // (ListAsync 等不查 id='')。name='(local download)' 唯一不冲突。
        using (var sentinel = conn.CreateCommand())
        {
            sentinel.CommandText = @"
                INSERT OR IGNORE INTO environments
                    (id, name, root_path, comfyui_layout, base_python_path, python_version)
                VALUES
                    ('', '(local download)', '', 'standalone', '', '')";
            sentinel.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            bool exists = false;
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (exists) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }
}
