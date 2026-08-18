using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowSymlinkerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workflowsDir;
    private readonly string _envComfyuiSrc;
    private readonly Settings _settings;
    private readonly JunctionLinker _linker = new();
    private readonly WorkflowFilesystemScanner _scanner = new(logger: null);
    private readonly WorkflowSymlinker _symlinker;

    public WorkflowSymlinkerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFSym_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _workflowsDir = Path.Combine(_tempRoot, "workflows");
        _envComfyuiSrc = Path.Combine(_tempRoot, "env-comfyui");
        Directory.CreateDirectory(_workflowsDir);
        Directory.CreateDirectory(_envComfyuiSrc);

        _settings = new Settings { WorkflowsDirectory = _workflowsDir };
        _symlinker = new WorkflowSymlinker(_settings, _linker, _scanner, logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <summary>创建一个 valid downloaded subfolder(workflow.json + meta.json)。</summary>
    private string CreateDownloaded(string slug, string id8, string source = "community_json")
    {
        var sub = Path.Combine(_workflowsDir, $"{slug}-{id8}");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "workflow.json"), "{}");
        File.WriteAllText(Path.Combine(sub, "meta.json"),
            $"{{\"title\":\"{slug}\",\"source\":\"{source}\",\"source_id\":\"{id8}\",\"downloaded_at\":\"2026-08-18T10:00:00Z\"}}");
        return sub;
    }

    [Fact]
    public async Task SyncToEnvAsync_NothingDownloaded_ReturnsEmpty()
    {
        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, result.Linked);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task SyncToEnvAsync_EmptyComfyuiSrc_ReturnsEmpty()
    {
        CreateDownloaded("portrait", "abc12345");

        var result = await _symlinker.SyncToEnvAsync("");

        Assert.Equal(0, result.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_NewSubfolder_CreatesLink()
    {
        CreateDownloaded("portrait", "abc12345");

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(1, result.Linked);
        var linkPath = Path.Combine(_envComfyuiSrc, "user", "default", "workflows", "portrait-abc12345");
        Assert.True(Directory.Exists(linkPath));
    }

    [Fact]
    public async Task SyncToEnvAsync_AlreadyCorrectLink_Skipped()
    {
        CreateDownloaded("portrait", "abc12345");
        var first = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);
        Assert.Equal(1, first.Linked);

        var second = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, second.Linked);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task SyncToEnvAsync_MultipleSubfolders_AllLinked()
    {
        CreateDownloaded("a", "11111111");
        CreateDownloaded("b", "22222222");
        CreateDownloaded("c", "33333333");

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(3, result.Linked);
        var linkDir = Path.Combine(_envComfyuiSrc, "user", "default", "workflows");
        Assert.Equal(3, Directory.GetDirectories(linkDir).Length);
    }

    [Fact]
    public async Task SyncToEnvAsync_WorkflowsDirMissing_ReturnsEmpty()
    {
        Directory.Delete(_workflowsDir, recursive: true);

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, result.Linked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task SyncToEnvAsync_WrongExistingJunction_RecreatesLink()
    {
        // 已下载一个 valid subfolder
        CreateDownloaded("portrait", "abc12345");

        // 预先在 target 路径放一个指向 *错误* target 的 junction
        var wrongTarget = Path.Combine(_tempRoot, "wrong-sub");
        Directory.CreateDirectory(wrongTarget);
        var linkPath = Path.Combine(_envComfyuiSrc, "user", "default", "workflows", "portrait-abc12345");
        await _linker.CreateAsync(linkPath, wrongTarget, default).ConfigureAwait(false);

        // 调用 sync — 应该检测到 mismatch → 删 + 重建
        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);

        // 确认 junction 现在指向正确的 downloaded subfolder
        Assert.True(Directory.Exists(linkPath));
        var actualTarget = await _linker.GetTargetAsync(linkPath, default).ConfigureAwait(false);
        var expectedTarget = Path.GetFullPath(Path.Combine(_workflowsDir, "portrait-abc12345"));
        Assert.Equal(expectedTarget, actualTarget, ignoreCase: true);
    }

    [Fact]
    public async Task SyncToEnvAsync_LinkCreationFails_RecordsErrorWithoutThrowing()
    {
        // 已下载一个 valid subfolder
        CreateDownloaded("broken", "deadbeef");

        // 在 link 路径放一个 regular file(非目录):symlinker 的 `Directory.Exists(link)` → false,
        // 走到 CreateAsync,CreateAsync 内部 `File.Exists(linkPath)` → true → 抛
        // JunctionCreationException("link 路径已存在");symlinker 应 catch 它并记录。
        var linkPath = Path.Combine(_envComfyuiSrc, "user", "default", "workflows", "broken-deadbeef");
        var linkParent = Path.GetDirectoryName(linkPath)!;
        Directory.CreateDirectory(linkParent);
        File.WriteAllText(linkPath, "occupying regular file");

        // sync 应该 catch 这个 link creation 失败 → Failed >= 1, 不抛
        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.True(result.Failed >= 1, $"expected Failed >= 1, got {result.Failed}");
        Assert.NotEmpty(result.Errors);
    }
}