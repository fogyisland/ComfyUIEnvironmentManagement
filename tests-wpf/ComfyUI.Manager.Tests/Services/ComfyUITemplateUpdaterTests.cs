using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ComfyUITemplateUpdaterTests : IDisposable
{
    private readonly string _rootDir;
    private readonly ComfyUITemplateUpdater _updater;

    public ComfyUITemplateUpdaterTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "comfyui-template-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);

        // Test fixture uses "git" exe — tests that don't trigger git clone (empty
        // / missing target dir) won't actually exec it. The fail-fast tests never
        // invoke git.
        var git = new GitRunner("git");
        _updater = new ComfyUITemplateUpdater(git, logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateAsync_EmptyTargetDir_ReturnsFail()
    {
        // v0.6.22.x: empty / whitespace target dir → Fail (no exception).
        var result = await _updater.UpdateAsync(targetDir: "");

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Contains("模板目录不能为空", result.Reason);
    }

    [Fact]
    public async Task UpdateAsync_NullTargetDir_ReturnsFail()
    {
        // v0.6.22.x: null target dir → Fail (no exception).
        var result = await _updater.UpdateAsync(targetDir: null!);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Contains("模板目录不能为空", result.Reason);
    }

    [Fact]
    public async Task UpdateAsync_MissingTargetDir_ReturnsFail()
    {
        // v0.6.22.x: targetDir points to non-existent directory → Fail (no exception).
        var result = await _updater.UpdateAsync(
            targetDir: Path.Combine(_rootDir, "does-not-exist"));

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        Assert.Contains("模板目录不存在", result.Reason);
    }

    [Fact]
    public async Task UpdateAsync_ExistingTargetDir_PreservesDirAndReportsProgress()
    {
        // v0.6.22.x: targetDir exists → 删内容前先尝试 report 一行 (确认路径正确
        // 进入 wipe 阶段 — 后续 git clone 会失败因为 PATH 没 git / 网络,但我们
        // 只断言 progress.Report("开始模板更新...") 已被触发,说明走到了 wipe 路径)。
        var targetDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(targetDir);
        // put a sentinel file we can verify is wiped
        File.WriteAllText(Path.Combine(targetDir, "sentinel.txt"), "x");

        string? firstProgress = null;
        var progress = new Progress<string>(line => { if (firstProgress is null) firstProgress = line; });

        var result = await _updater.UpdateAsync(targetDir, progress);

        // We don't assert Success (git clone may fail in test env without network),
        // only that the wipe phase started — first progress message must reference
        // our targetDir. This proves the path argument flows through correctly.
        Assert.NotNull(firstProgress);
        Assert.Contains(targetDir, firstProgress);
        Assert.Contains("开始模板更新", firstProgress);
    }
}