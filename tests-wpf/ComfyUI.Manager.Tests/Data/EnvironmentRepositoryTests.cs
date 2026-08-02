using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Data;

public sealed class EnvironmentRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvironmentRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            "env-repo-test-" + Path.GetRandomFileName() + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static Environment MakeEnv(string id, string name) => new()
    {
        Id = id,
        Name = name,
        RootPath = $"/tmp/envs/{name}",
        ComfyuiLayout = "shared",
        ComfyuiSource = "/tmp/ComfyUI",
        VenvPath = $"/tmp/envs/{name}/venv",
        PythonExecutable = $"/tmp/envs/{name}/venv/Scripts/python.exe",
        CustomNodesPath = $"/tmp/envs/{name}/custom_nodes",
        ExtraModelPathsYaml = $"/tmp/envs/{name}/extra_model_paths.yaml",
        Port = 8188,
        EnabledNodeIdsJson = "[]",
        Status = "stopped",
    };

    [Fact]
    public void BasePythonPath_RoundTrips()
    {
        var env = MakeEnv("env-1", "alpha");
        env.BasePythonPath = "/tmp/python/3.10/python.exe";
        env.PythonVersion = "3.10.18";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("/tmp/python/3.10/python.exe", list[0].BasePythonPath);
        Assert.Equal("3.10.18", list[0].PythonVersion);
    }

    [Fact]
    public void BasePythonPath_FallsBackToPythonExecutable_WhenColumnEmpty()
    {
        var env = MakeEnv("env-2", "beta");
        env.BasePythonPath = "";
        env.PythonExecutable = "/tmp/envs/beta/venv/Scripts/python.exe";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("/tmp/envs/beta/venv/Scripts/python.exe", list[0].BasePythonPath);
    }

    [Fact]
    public void PythonVersion_RoundTrips()
    {
        var env = MakeEnv("env-3", "gamma");
        env.PythonVersion = "3.11.13 (tags/v3.11.13:...)";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("3.11.13 (tags/v3.11.13:...)", list[0].PythonVersion);
    }

    [Fact]
    public void PythonVersion_FallsBackToUnknown_WhenColumnEmpty()
    {
        var env = MakeEnv("env-4", "delta");
        env.PythonVersion = "";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("<unknown>", list[0].PythonVersion);
    }

    [Fact]
    public void Open_MigratesLegacyEnvironmentsTable_AddingBasePythonPathAndPythonVersion()
    {
        // Simulate a pre-v0.6.5.5 state.db whose `environments` table predates the
        // base_python_path / python_version columns. We craft that schema manually,
        // close it (release the SQLite WAL lock), then exercise the real
        // SqliteConnectionFactory.Open() so EnsureColumn has to ALTER TABLE us.
        var legacy = new SqliteConnection($"Data Source={_dbPath}");
        legacy.Open();
        try
        {
            using (var cmd = legacy.CreateCommand())
            {
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
                    );";
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            legacy.Close();
            legacy.Dispose();
        }

        // Re-open through the factory; this triggers CreateSchemaIfMissing + EnsureColumn.
        using (var conn = _factory.Open())
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(environments)";
            using var reader = pragma.ExecuteReader();
            var columnNames = new System.Collections.Generic.List<string>();
            while (reader.Read())
            {
                // PRAGMA table_info: 0=cid, 1=name, 2=type, 3=notnull, 4=dflt, 5=pk
                columnNames.Add(reader.GetString(1));
            }

            Assert.Contains("base_python_path", columnNames);
            Assert.Contains("python_version", columnNames);
        }
    }
}
