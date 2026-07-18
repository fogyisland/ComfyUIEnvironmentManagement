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
    }
}