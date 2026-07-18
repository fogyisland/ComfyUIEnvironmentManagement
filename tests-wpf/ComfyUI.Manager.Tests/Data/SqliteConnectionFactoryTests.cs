using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteConnectionFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"user-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Open_CreatesDbFileAndUserTables_WhenMissing()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var factory = new SqliteConnectionFactory(dbPath);

        using var conn = factory.Open();

        Assert.True(File.Exists(dbPath));
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' " +
            "AND name IN ('environments','scanned_nodes','version_history'," +
            "'dep_records','process_state') ORDER BY name";
        var tables = new System.Collections.Generic.List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        Assert.Equal(5, tables.Count);
    }

    [Fact]
    public void Constructor_WithPath_ExposesDbPath()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "x.db"));
        Assert.EndsWith("x.db", factory.DbPath);
    }
}
