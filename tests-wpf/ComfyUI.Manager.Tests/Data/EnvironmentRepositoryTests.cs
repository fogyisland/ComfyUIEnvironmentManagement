using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
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
}
