using System;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Tests.Fakes;

/// <summary>
/// TestDb:creates a throwaway SQLite file with the M4 schema so repository
/// reads have real tables to hit. Mirrors the subset of
/// <c>src/comfy_mgr/db/connection.py</c> the WPF repositories query.
/// Dispose deletes the temp file.
/// </summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; }
    public SqliteConnectionFactory Factory { get; }

    public TestDb()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"comfy-mgr-test-{Guid.NewGuid():N}.db");
        Factory = new SqliteConnectionFactory(Path);
        InitSchema();
    }

    private void InitSchema()
    {
        using var conn = new SqliteConnection($"Data Source={Path}");
        conn.Open();
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
                pid INTEGER
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
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch { /* best-effort temp cleanup */ }
    }
}
