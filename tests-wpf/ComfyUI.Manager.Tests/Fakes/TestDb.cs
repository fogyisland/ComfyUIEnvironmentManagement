using System;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Tests.Fakes;

/// <summary>
/// TestDb:creates throwaway SQLite files with the v1.0.0.x T45 split schemas so repository
/// reads have real tables to hit. T45 exposes TWO factories:
/// - <see cref="Factory"/> → SchemaKind.State (state.db content: 8 state 表 + tests 测的
///   environments / scanned_nodes / catalog_cache 等)
/// - <see cref="ModelFactory"/> → SchemaKind.Model (model.db content: 3 model 表,给
///   LocalModel*Repository / CivitaiCardCacheRepository 用)
/// Dispose deletes both temp files.
/// </summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; }
    public string ModelPath { get; }
    public SqliteConnectionFactory Factory { get; }
    public SqliteConnectionFactory ModelFactory { get; }

    public TestDb()
    {
        var baseName = $"comfy-mgr-test-{Guid.NewGuid():N}";
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{baseName}.db");
        ModelPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{baseName}-model.db");

        // v1.0.0.x T45:state factory 默认走 SchemaKind.State —— 跟老 wire 一致,
        // 现有 env/node/catalog 测试不动 factory 类型。
        Factory = new SqliteConnectionFactory(Path, SchemaKind.State);
        // model factory 走 model schema —— LocalModelFilesRepository 等用它。
        ModelFactory = new SqliteConnectionFactory(ModelPath, SchemaKind.Model);

        InitStateSchema();
        // Model factory 的 InitSchema 在 SqliteConnectionFactory.Open() 内自跑,
        // 不用这里手动建表(保持跟生产路径一致 —— 让 factory 自己负责 schema)。
    }

    private void InitStateSchema()
    {
        using var conn = new SqliteConnection($"Data Source={Path}");
        conn.Open();
        // v1.0.0.x (2026-09-03) T33:设 PRAGMA journal_mode=WAL 跟 production
        // SqliteConnectionFactory.Open 一致,否则 TestDb raw conn (journal=DELETE 默认)
        // 写 + Factory.Open (journal=WAL) 读可能跨 mode 不可见。
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE environments (
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
                pid INTEGER,
                bed_profile_id TEXT,
                bed_status TEXT,
                bed_failed_reason TEXT,
                notes TEXT,
                template_kind TEXT NOT NULL DEFAULT 'ComfyUI',
                template_config_snapshot TEXT
            );
            CREATE TABLE scanned_nodes (
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
            CREATE TABLE catalog_cache (
                id TEXT PRIMARY KEY,
                source_url TEXT NOT NULL,
                package TEXT NOT NULL,
                raw_metadata TEXT NOT NULL,
                cached_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                UNIQUE(source_url, package)
            );
            CREATE TABLE version_history (
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
            CREATE TABLE dep_records (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                source TEXT NOT NULL,
                dep_name TEXT NOT NULL,
                dep_version_spec TEXT,
                scanned_at TEXT NOT NULL,
                UNIQUE(env_id, package, source, dep_name)
            );
            CREATE TABLE process_state (
                env_id TEXT PRIMARY KEY,
                pid INTEGER NOT NULL,
                port INTEGER NOT NULL,
                started_at TIMESTAMP NOT NULL
            );";
        cmd.ExecuteNonQuery();

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_scanned_nodes_env_pkg_source " +
                "ON scanned_nodes(env_id, package, source)";
            idx.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
        }
        catch { /* best-effort temp cleanup */ }
    }
}