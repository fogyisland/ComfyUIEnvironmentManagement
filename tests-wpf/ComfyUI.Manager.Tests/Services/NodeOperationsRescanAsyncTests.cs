using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15.8 T1: NodeOperations.RescanAsync 扫描 env 的 CustomNodesPath,
/// upsert ScannedNode,返 list。空/不存在 → 空 list + WARN log。
///
/// 适配 codebase(跟 brief 有偏离):
/// - 没有 FakeNodeOperations:FakeGitRunner 直接喂 RunAsync 返回 fake result
/// - NodeOperations ctor 需要 Settings + NodeInstallDiffService(brief 漏列)
/// - AppLogger ctor = (projectRoot, baseDir?) — 不是 (logPath)
/// </summary>
public class NodeOperationsRescanAsyncTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeOperations _ops;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeGitRunner _git;
    private readonly string _envId = "env-1";
    private readonly string _tempDir;

    public NodeOperationsRescanAsyncTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _envRepo = new EnvironmentRepository(_db.Factory);
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrRescanTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Seed env + custom_nodes path
        Directory.CreateDirectory(Path.Combine(_tempDir, "custom_nodes"));
        _envRepo.Upsert(new Environment
        {
            Id = _envId,
            Name = "test-env",
            RootPath = _tempDir,
            ComfyuiLayout = "standalone",
            CustomNodesPath = Path.Combine(_tempDir, "custom_nodes"),
        });
        // Create 3 fake custom node directories
        foreach (var name in new[] { "ComfyUI-Impact-Pack", "ComfyUI-Manager", "ComfyUI-Inspire-Pack" })
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "custom_nodes", name));
        }
        _git = new FakeGitRunner();
        var settings = new Settings { LocalNodeDirectory = _tempDir };
        _ops = new NodeOperations(
            _git, _envRepo, _nodeRepo, settings,
            NoopDiffService(), logger: null);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>RescanAsync 测试不关心 diff — noop service。</summary>
    private static NodeInstallDiffService NoopDiffService() =>
        new((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));

    [Fact]
    public async Task RescanAsync_HappyPath_CreatesRowsForEachSubdir()
    {
        var result = await _ops.RescanAsync(_envId);
        Assert.Equal(3, result.Count);
        var packages = result.Select(n => n.Package).OrderBy(x => x).ToList();
        Assert.Contains("ComfyUI-Impact-Pack", packages);
        Assert.Contains("ComfyUI-Manager", packages);
        Assert.Contains("ComfyUI-Inspire-Pack", packages);
        // DB has 3 rows
        var dbRows = _nodeRepo.ListByEnv(_envId).ToList();
        Assert.Equal(3, dbRows.Count);
        // Each row has empty installed_tag (no git in fake — TryReadHeadShaAsync/TryReadInstalledTagAsync both return null)
        // assert key exists per brief
        foreach (var row in dbRows)
        {
            Assert.True(row.ScanMeta.ContainsKey("installed_tag"));
        }
    }

    [Fact]
    public async Task RescanAsync_CustomNodesPathMissing_ReturnsEmpty()
    {
        // Delete custom_nodes dir
        Directory.Delete(Path.Combine(_tempDir, "custom_nodes"), recursive: true);
        var result = await _ops.RescanAsync(_envId);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RescanAsync_NoSubdirs_ReturnsEmpty()
    {
        // custom_nodes exists but empty
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(_tempDir, "custom_nodes")))
        {
            Directory.Delete(d, recursive: true);
        }
        var result = await _ops.RescanAsync(_envId);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RescanAsync_NonExistentEnv_ReturnsEmpty()
    {
        var result = await _ops.RescanAsync("does-not-exist");
        Assert.Empty(result);
    }

    [Fact]
    public async Task RescanAsync_UpsertsExistingNode()
    {
        await _ops.RescanAsync(_envId); // first scan: 3 rows
        // Add a new subdir
        Directory.CreateDirectory(Path.Combine(_tempDir, "custom_nodes", "NewNode"));
        await _ops.RescanAsync(_envId); // second scan: 4 rows, original 3 upserted (id stable)
        var rows = _nodeRepo.ListByEnv(_envId).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.Id == "ComfyUI-Impact-Pack");
        Assert.Contains(rows, r => r.Id == "NewNode");
    }
}
