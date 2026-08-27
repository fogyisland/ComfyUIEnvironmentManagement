using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x #589:LocalNodeSyncService(env → LocalNodesDirectory)单元测试 —
/// 覆盖 happy path、单文件节点包装、排除规则、overwrite 已有子目录、配置/路径缺失、
/// ResolveSourceDirectory 相对/绝对/不存在路径解析、EnumerateNodeEntries 过滤排序等分支。
///
/// <para>
/// 全测试用 <see cref="Path.GetTempPath"/> 下的唯一子目录 + disposable 模式,跑完自动清理。
/// pip 跟 git 都不走(本 service 只做文件 copy),所以无需 fake。
/// </para>
/// </summary>
public class LocalNodeSyncServiceTests : IDisposable
{
    private readonly string _srcDir;   // localnodes 源(模拟 LocalNodesDirectory)
    private readonly string _envRoot;  // env 根(模拟 env.RootPath)

    public LocalNodeSyncServiceTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "lnss-src-" + Guid.NewGuid().ToString("N"));
        _envRoot = Path.Combine(Path.GetTempPath(), "lnss-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_srcDir)) TryDelete(_srcDir);
        if (Directory.Exists(_envRoot)) TryDelete(_envRoot);
    }

    private static void TryDelete(string p)
    {
        try { Directory.Delete(p, recursive: true); } catch { /* 容忍 lock */ }
    }

    /// <summary>
    /// 在 _envRoot/&lt;envId&gt;/custom_nodes/ 下创建一些节点条目(子目录 + 单文件 +
    /// 应排除的干扰项),返回 Environment。
    /// </summary>
    private Environment SeedEnv(
        string envId = "env-1",
        Action<string>? customizer = null)
    {
        var customNodes = Path.Combine(_envRoot, envId, "custom_nodes");
        Directory.CreateDirectory(customNodes);
        // 子目录节点
        Directory.CreateDirectory(Path.Combine(customNodes, "ComfyUI-VideoHelperSuite"));
        File.WriteAllText(Path.Combine(customNodes, "ComfyUI-VideoHelperSuite", "requirements.txt"),
            "opencv-python\nimageio-ffmpeg\n");
        File.WriteAllText(Path.Combine(customNodes, "ComfyUI-VideoHelperSuite", "__init__.py"), "");
        Directory.CreateDirectory(Path.Combine(customNodes, "rgthree-comfy"));
        File.WriteAllText(Path.Combine(customNodes, "rgthree-comfy", "__init__.py"), "");
        // 单文件节点
        File.WriteAllText(Path.Combine(customNodes, "websocket_image_save.py"), "# node\n");
        // 应排除项
        Directory.CreateDirectory(Path.Combine(customNodes, "__pycache__"));
        Directory.CreateDirectory(Path.Combine(customNodes, ".git"));
        File.WriteAllText(Path.Combine(customNodes, ".hidden_node"), "");
        Directory.CreateDirectory(Path.Combine(customNodes, "ComfyUI-Manager"));
        File.WriteAllText(Path.Combine(customNodes, "ComfyUI-Manager", "__init__.py"), "");
        customizer?.Invoke(customNodes);

        return new Environment
        {
            Id = envId,
            Name = envId,
            CustomNodesPath = customNodes,
        };
    }

    private LocalNodeSyncService MakeService(Settings? settings = null)
        => new LocalNodeSyncService(
            settings ?? new Settings { LocalNodesDirectory = _srcDir });

    // ============ EnumerateNodeEntries 静态过滤 + 排序 ============

    [Fact]
    public void EnumerateNodeEntries_FiltersExcludes_AndSortsByName()
    {
        SeedEnv();
        var envCustomNodes = Path.Combine(_envRoot, "env-1", "custom_nodes");
        var entries = LocalNodeSyncService.EnumerateNodeEntries(envCustomNodes, CancellationToken.None);

        // 应剩 3 条:ComfyUI-VideoHelperSuite (dir), rgthree-comfy (dir), websocket_image_save (file)
        // __pycache__ / .git / .hidden_node / ComfyUI-Manager 全排除
        Assert.Equal(3, entries.Count);
        Assert.Equal("ComfyUI-VideoHelperSuite", entries[0].Name);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("rgthree-comfy", entries[1].Name);
        Assert.True(entries[1].IsDirectory);
        Assert.Equal("websocket_image_save", entries[2].Name);
        Assert.False(entries[2].IsDirectory);
    }

    [Fact]
    public void EnumerateNodeEntries_DirectoryNotExists_ReturnsEmpty()
    {
        var entries = LocalNodeSyncService.EnumerateNodeEntries(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()),
            CancellationToken.None);
        Assert.Empty(entries);
    }

    [Fact]
    public void EnumerateNodeEntries_NullOrEmptyPath_ReturnsEmpty()
    {
        Assert.Empty(LocalNodeSyncService.EnumerateNodeEntries("", CancellationToken.None));
        Assert.Empty(LocalNodeSyncService.EnumerateNodeEntries("  ", CancellationToken.None));
    }

    // ============ ResolveSourceDirectory ============

    [Fact]
    public void ResolveSourceDirectory_AbsolutePathExists_ReturnsIt()
    {
        var svc = MakeService(new Settings { LocalNodesDirectory = _srcDir });
        Assert.Equal(_srcDir, svc.ResolveSourceDirectory());
    }

    [Fact]
    public void ResolveSourceDirectory_AbsolutePathMissing_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "lnss-missing-" + Guid.NewGuid());
        var svc = MakeService(new Settings { LocalNodesDirectory = missing });
        Assert.Null(svc.ResolveSourceDirectory());
    }

    [Fact]
    public void ResolveSourceDirectory_EmptyOrWhitespace_ReturnsNull()
    {
        var svc = MakeService(new Settings { LocalNodesDirectory = "" });
        Assert.Null(svc.ResolveSourceDirectory());
        svc = MakeService(new Settings { LocalNodesDirectory = "   " });
        Assert.Null(svc.ResolveSourceDirectory());
    }

    // ============ SyncAsync happy path ============

    [Fact]
    public async Task SyncAsync_HappyPath_CopiesAllEntriesToLocalNodes()
    {
        var env = SeedEnv();
        var svc = MakeService();

        var result = await svc.SyncAsync(env);

        Assert.True(result.Success);
        Assert.Equal(3, result.Added.Count + result.Updated.Count);
        // 2 个子目录 + 1 个单文件包装成的子目录
        Assert.True(Directory.Exists(Path.Combine(_srcDir, "ComfyUI-VideoHelperSuite")));
        Assert.True(Directory.Exists(Path.Combine(_srcDir, "rgthree-comfy")));
        Assert.True(Directory.Exists(Path.Combine(_srcDir, "websocket_image_save")));
        // 单文件节点包成同名子目录 + 同名文件
        Assert.True(File.Exists(
            Path.Combine(_srcDir, "websocket_image_save", "websocket_image_save.py")));
        // requirements.txt 也复制了 → 下次 LocalNodeBulkInstaller 会 pip install cv2
        Assert.True(File.Exists(
            Path.Combine(_srcDir, "ComfyUI-VideoHelperSuite", "requirements.txt")));
        Assert.Contains("opencv-python",
            File.ReadAllText(Path.Combine(_srcDir, "ComfyUI-VideoHelperSuite", "requirements.txt")));
        // 排除项没复制
        Assert.False(Directory.Exists(Path.Combine(_srcDir, "__pycache__")));
        Assert.False(Directory.Exists(Path.Combine(_srcDir, ".git")));
        Assert.False(File.Exists(Path.Combine(_srcDir, ".hidden_node")));
        Assert.False(Directory.Exists(Path.Combine(_srcDir, "ComfyUI-Manager")));
    }

    [Fact]
    public async Task SyncAsync_OverwriteExistingSubdir_MarksAsUpdated()
    {
        // pre-seed localnodes 里 ComfyUI-VideoHelperSuite 但版本旧
        var oldDir = Path.Combine(_srcDir, "ComfyUI-VideoHelperSuite");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "requirements.txt"), "OLD\n");

        var env = SeedEnv();
        var svc = MakeService();

        var result = await svc.SyncAsync(env);

        Assert.True(result.Success);
        Assert.Contains("ComfyUI-VideoHelperSuite", result.Updated);
        // 旧文件被覆盖(不是追加;Add + Remove + Overwrite)
        var newReq = File.ReadAllText(
            Path.Combine(_srcDir, "ComfyUI-VideoHelperSuite", "requirements.txt"));
        Assert.DoesNotContain("OLD", newReq);
        Assert.Contains("opencv-python", newReq);
    }

    // ============ SyncAsync 失败分支 ============

    [Fact]
    public async Task SyncAsync_LocalNodesDirectoryNotConfigured_ReturnsFail()
    {
        var env = SeedEnv();
        var svc = MakeService(new Settings { LocalNodesDirectory = "" });
        var result = await svc.SyncAsync(env);
        Assert.False(result.Success);
        Assert.Contains("LocalNodesDirectory", result.Reason);
    }

    [Fact]
    public async Task SyncAsync_LocalNodesDirectoryMissing_ReturnsFail()
    {
        var env = SeedEnv();
        var missing = Path.Combine(Path.GetTempPath(), "lnss-missing-" + Guid.NewGuid());
        var svc = MakeService(new Settings { LocalNodesDirectory = missing });
        var result = await svc.SyncAsync(env);
        Assert.False(result.Success);
        Assert.Contains("LocalNodesDirectory", result.Reason);
    }

    [Fact]
    public async Task SyncAsync_EnvCustomNodesPathNull_ReturnsFail()
    {
        var env = new Environment { Id = "broken", Name = "broken", CustomNodesPath = null };
        var svc = MakeService();
        var result = await svc.SyncAsync(env);
        Assert.False(result.Success);
        Assert.Contains("custom_nodes_path", result.Reason);
    }

    [Fact]
    public async Task SyncAsync_EnvCustomNodesPathMissing_ReturnsFail()
    {
        var env = new Environment
        {
            Id = "broken",
            Name = "broken",
            CustomNodesPath = Path.Combine(_envRoot, "nonexistent", "custom_nodes"),
        };
        var svc = MakeService();
        var result = await svc.SyncAsync(env);
        Assert.False(result.Success);
        Assert.Contains("custom_nodes 目录不存在", result.Reason);
    }

    // ============ 进度回调 ============

    [Fact]
    public async Task SyncAsync_ReportsStageAndInfoLines()
    {
        var env = SeedEnv();
        var svc = MakeService();
        var lines = new System.Collections.Generic.List<string>();
        var progress = new SyncProgressRecorder(lines);

        var result = await svc.SyncAsync(env, progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(lines, l => l.StartsWith("stage:开始同步"));
        Assert.Contains(lines, l => l.StartsWith("stage:同步完成"));
        Assert.Contains(lines, l => l.StartsWith("info:新增 ") || l.StartsWith("info:更新 "));
    }

    [Fact]
    public async Task SyncAsync_EnvAlreadyInSync_AllEntriesMarkedAsUpdated()
    {
        // 先 sync 一次建立所有节点
        var env = SeedEnv();
        var svc = MakeService();
        await svc.SyncAsync(env);

        // 第二次 sync → 全部都算 updated
        var result = await svc.SyncAsync(env);
        Assert.True(result.Success);
        Assert.Equal(3, result.Updated.Count);
        Assert.Empty(result.Added);
    }

    // ============ LocalNodeSyncResult ============

    [Fact]
    public void LocalNodeSyncResult_Ok_ExposesFields()
    {
        var r = LocalNodeSyncResult.Ok(
            "summary",
            new[] { "a" },
            new[] { "b" },
            Array.Empty<string>(),
            Array.Empty<string>());
        Assert.True(r.Success);
        Assert.Equal("summary", r.Reason);
        Assert.Equal(new[] { "a" }, r.Added);
        Assert.Equal(new[] { "b" }, r.Updated);
    }

    [Fact]
    public void LocalNodeSyncResult_Fail_ExposesReasonInFailReasons()
    {
        var r = LocalNodeSyncResult.Fail("boom");
        Assert.False(r.Success);
        Assert.Equal("boom", r.Reason);
        Assert.Single(r.FailReasons);
        Assert.Equal("boom", r.FailReasons[0]);
    }

    private sealed class SyncProgressRecorder : IProgress<string>
    {
        private readonly System.Collections.Generic.List<string> _lines;
        public SyncProgressRecorder(System.Collections.Generic.List<string> lines) => _lines = lines;
        public void Report(string value) => _lines.Add(value);
    }
}
