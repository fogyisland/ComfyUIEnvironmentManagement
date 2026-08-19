using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ComfyUITemplateUpdaterTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;
    private readonly GitRunner _git;
    private readonly ComfyUITemplateUpdater _updater;

    public ComfyUITemplateUpdaterTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "comfyui-template-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);

        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new EnvironmentRepository(_factory);

        // Test fixture uses "git" exe — tests that don't trigger git clone (empty
        // ComfyuiSource path) won't actually exec it. The EmptyComfyuiPath test
        // never invokes git (fail-fast before clone).
        _git = new GitRunner("git");
        _updater = new ComfyUITemplateUpdater(_git, _repo, logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateAsync_EmptyComfyuiPath_ReturnsFail()
    {
        // v0.6.22 T5: env.ComfyuiSource empty/missing → Fail (no exception).
        var env = new Environment
        {
            Id = "test-env",
            Name = "TestEnv",
            ComfyuiSource = "",
        };

        var result = await _updater.UpdateAsync(env);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Contains("ComfyUI 目录不存在", result.Reason);
    }

    [Fact]
    public async Task UpdateAsync_MissingComfyuiDir_ReturnsFail()
    {
        // v0.6.22 T5: env.ComfyuiSource points to non-existent directory → Fail
        // (no exception).
        var env = new Environment
        {
            Id = "test-env",
            Name = "TestEnv",
            ComfyuiSource = Path.Combine(_rootDir, "does-not-exist"),
        };

        var result = await _updater.UpdateAsync(env);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Contains("ComfyUI 目录不存在", result.Reason);
    }

    [Fact]
    public async Task UpdateAsync_NullEnv_ReturnsFail()
    {
        // v0.6.22 T5: null env → Fail (no exception).
        var result = await _updater.UpdateAsync(env: null!);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
    }
}