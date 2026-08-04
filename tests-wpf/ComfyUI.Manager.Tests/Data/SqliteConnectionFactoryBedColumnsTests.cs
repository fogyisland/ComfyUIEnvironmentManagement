using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// SqliteConnectionFactory 启动时自动 ALTER TABLE 加 bed_profile_id / bed_status /
/// bed_failed_reason 三列(老 db schema 没这些列)。两次调用 idempotent。
/// </summary>
public class SqliteConnectionFactoryBedColumnsTests
{
    [Fact]
    public void EnsureBedColumns_AddsThreeColumnsToOldSchema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "bed-cols-" + Path.GetRandomFileName() + ".db");
        try
        {
            // 模拟 v0.6.5.6 老 schema(没 BED 列)
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE environments (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, root_path TEXT NOT NULL,
                        comfyui_layout TEXT NOT NULL, comfyui_source TEXT, venv_path TEXT,
                        python_executable TEXT, custom_nodes_path TEXT,
                        extra_model_paths_yaml TEXT, port INTEGER,
                        enabled_node_ids_json TEXT DEFAULT '[]',
                        status TEXT DEFAULT 'stopped',
                        base_python_path TEXT NOT NULL DEFAULT '',
                        python_version TEXT NOT NULL DEFAULT '', pid INTEGER
                    )";
                cmd.ExecuteNonQuery();
            }

            var factory = new SqliteConnectionFactory(dbPath);
            using var conn2 = factory.Open();  // 触发 InitSchemaIfMissing

            // PRAGMA table_info 验证 3 列已加
            using var info = conn2.CreateCommand();
            info.CommandText = "PRAGMA table_info(environments)";
            using var reader = info.ExecuteReader();
            var names = new System.Collections.Generic.List<string>();
            while (reader.Read()) names.Add(reader.GetString(1));

            Assert.Contains("bed_profile_id", names);
            Assert.Contains("bed_status", names);
            Assert.Contains("bed_failed_reason", names);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void EnsureBedColumns_IsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "bed-cols-idem-" + Path.GetRandomFileName() + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using var c1 = factory.Open();  // 第一次:加列
            using var c2 = factory.Open();  // 第二次:不抛

            using var info = c2.CreateCommand();
            info.CommandText = "PRAGMA table_info(environments)";
            using var reader = info.ExecuteReader();
            int bedCount = 0;
            while (reader.Read())
            {
                var n = reader.GetString(1);
                if (n == "bed_profile_id" || n == "bed_status" || n == "bed_failed_reason") bedCount++;
            }
            Assert.Equal(3, bedCount);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}