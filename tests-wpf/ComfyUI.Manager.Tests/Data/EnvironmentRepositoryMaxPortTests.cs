using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class EnvironmentRepositoryMaxPortTests
{
    [Fact]
    public void GetMaxPort_EmptyDb_ReturnsNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var max = repo.GetMaxPort();

        Assert.Null(max);
    }

    [Fact]
    public void GetMaxPort_AllPortsNull_ReturnsNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = null,
        });

        var max = repo.GetMaxPort();

        Assert.Null(max);
    }

    [Fact]
    public void GetMaxPort_Mixed_ReturnsMaxOfNonNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = 8188,
        });
        repo.Upsert(new Environment
        {
            Id = "env-2", Name = "second", RootPath = "/tmp/second",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = null,
        });

        var max = repo.GetMaxPort();

        Assert.Equal(8188, max);
    }

    [Fact]
    public void GetMaxPort_MultipleEnvs_ReturnsHighest()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-1", "first", 8188));
        repo.Upsert(MakeEnv("env-2", "second", 8200));
        repo.Upsert(MakeEnv("env-3", "third", 8189));

        var max = repo.GetMaxPort();

        Assert.Equal(8200, max);
    }

    private static Environment MakeEnv(string id, string name, int? port) => new()
    {
        Id = id, Name = name, RootPath = $"/tmp/{name}",
        ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
        PythonVersion = "3.10", Port = port,
    };
}