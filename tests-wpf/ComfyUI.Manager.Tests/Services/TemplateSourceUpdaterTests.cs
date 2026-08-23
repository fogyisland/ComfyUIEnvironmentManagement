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

    [Fact]
    public void CloneAsync_EmptyRepoUrl_Validates()
    {
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.CloneAsync(
            repoUrl: "",
            targetDir: Path.Combine(_workRoot, "fresh-clone"),
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Contains("repoUrl", result.Reason);
    }

    [Fact]
    public void CloneAsync_TargetDirExists_Fails()
    {
        // Reject cloning into existing non-empty dir to avoid silent overwrite.
        // UpdateAsync wipes; CloneAsync refuses (use UpdateAsync to refresh).
        var existing = Path.Combine(_workRoot, "already-exists");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "marker.txt"), "x");

        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.CloneAsync(
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            targetDir: existing,
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Contains("已存在", result.Reason);
        // marker file must still exist (no destructive side effect)
        Assert.True(File.Exists(Path.Combine(existing, "marker.txt")));
    }

    [Fact]
    public void CloneAsync_ValidInputs_ReturnsResult()
    {
        // Smoke: doesn't actually clone (no network), but verifies no throw
        // and result object is well-formed.
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.CloneAsync(
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            targetDir: Path.Combine(_workRoot, "template-clone"),
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.NotNull(result);
        // Reason may be null (success path, Ok(null)) or non-null (git network fail).
        // The intent is "method doesn't throw, returns a well-formed result record".
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }
}
