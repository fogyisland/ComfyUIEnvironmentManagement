using System;
using System.IO;
using System.Threading;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class TemplateSourceUpdaterTests : IDisposable
{
    private readonly string _workRoot;

    public TemplateSourceUpdaterTests()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "cmgr-tplsrd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workRoot);
    }

    [Fact]
    public void Ctor_AcceptsCustomTargetDir()
    {
        // v1.0.0 T11 generalization: ctor no longer hardcoded to projectRoot/ComfyUITemplate —
        // takes primitives (gitExe, gitProxy, logger) and constructs GitRunner internally.
        var updater = new TemplateSourceUpdater(gitExe: "git", gitProxy: null, logger: null);
        Assert.NotNull(updater);
    }

    [Fact]
    public void UpdateAsync_EmptyTargetDir_Validates()
    {
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: "",
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Contains("targetDir", result.Reason);
    }

    [Fact]
    public void UpdateAsync_EmptyRepoUrl_Validates()
    {
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: Path.Combine(_workRoot, "x"),
            repoUrl: "",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Contains("repoUrl", result.Reason);
    }

    [Fact]
    public void UpdateAsync_ValidInputs_ReturnsResult()
    {
        // Smoke test: doesn't actually clone (no network in test), but verifies the
        // method doesn't throw and returns a result object.
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: Path.Combine(_workRoot, "template"),
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.NotNull(result);
        Assert.NotNull(result.Reason);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }
}
