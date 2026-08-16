using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15.10:NodeOperations.RescanAsync / InstallAsync / UpgradeAsync 充实 ScanMeta
/// 字典(branch / last_commit_* / is_dirty / behind_count / disk_size / file_count /
/// python_files / has_requirements / has_pyproject / has_init)。原有 FakeGitRunner
/// 不需要预设 stdout:对每个 node,git 命令在非 .git 目录里返回 ok 但空 stdout,
/// ScanMeta 字段会留空 — 我们重点验「空目录不会崩 + ScanMeta 字段齐 + 文件系统维度正确」。
/// </summary>
public class NodeOperationsMetaEnrichmentTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeOperations _ops;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeNodeOperationsForManagement _fakeOps;
    private readonly string _envId = "env-1";
    private readonly string _tempDir;

    public NodeOperationsMetaEnrichmentTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _envRepo = new EnvironmentRepository(_db.Factory);
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrMetaTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "custom_nodes"));
        _envRepo.Upsert(new Environment
        {
            Id = _envId,
            Name = "test-env",
            RootPath = _tempDir,
            ComfyuiLayout = "standalone",
            CustomNodesPath = Path.Combine(_tempDir, "custom_nodes"),
        });
        _fakeOps = new FakeNodeOperationsForManagement();
        _fakeOps.NodeRepo = _nodeRepo;
        _ops = new NodeOperations(
            new GitRunner("git"),
            _envRepo, _nodeRepo,
            new Settings { LocalNodeDirectory = _tempDir },
            NoopDiffService(),
            logger: null);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static NodeInstallDiffService NoopDiffService() =>
        new((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));

    [Fact]
    public async Task RescanAsync_PopulatesAllScanMetaKeys_WithFilesystemFields()
    {
        // Seed node with requirements.txt + __init__.py + pyproject.toml + .py files
        var nodeDir = Path.Combine(_tempDir, "custom_nodes", "ComfyUI-Test");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "__init__.py"), "Name: ComfyUI-Test\n");
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), "torch>=1.0\n");
        File.WriteAllText(Path.Combine(nodeDir, "pyproject.toml"), "[project]\nname = \"test\"\n");
        File.WriteAllText(Path.Combine(nodeDir, "main.py"), "# node");
        File.WriteAllText(Path.Combine(nodeDir, "utils.py"), "# utils");
        File.WriteAllText(Path.Combine(nodeDir, "README.md"), "# readme");

        // git 维度在非 .git 目录会返回空 stdout → ScanMeta 字段空字符串(无 throw)
        // 文件系统维度直接读 FS
        await _ops.RescanAsync(_envId);

        var node = _nodeRepo.GetByPackageName(_envId, "ComfyUI-Test");
        Assert.NotNull(node);
        var meta = node!.ScanMeta;
        Assert.NotNull(meta);
        // 13 个 key 全部存在(值可能空但 key 必须有)
        Assert.Contains("installed_tag", meta.Keys);
        Assert.Contains("branch", meta.Keys);
        Assert.Contains("last_commit_date", meta.Keys);
        Assert.Contains("last_commit_author", meta.Keys);
        Assert.Contains("last_commit_short", meta.Keys);
        Assert.Contains("is_dirty", meta.Keys);
        Assert.Contains("behind_count", meta.Keys);
        Assert.Contains("disk_size", meta.Keys);
        Assert.Contains("file_count", meta.Keys);
        Assert.Contains("python_files", meta.Keys);
        Assert.Contains("has_requirements", meta.Keys);
        Assert.Contains("has_pyproject", meta.Keys);
        Assert.Contains("has_init", meta.Keys);
        // FS 维度:有 6 个文件(3 py: __init__+main+utils + 1 md + 1 toml + 1 req),requirements + pyproject + init 都在
        Assert.Equal("6", meta["file_count"]);
        Assert.Equal("3", meta["python_files"]);
        Assert.True(int.Parse(meta["disk_size"]) > 0, "disk_size 应该 > 0");
        Assert.Equal("1", meta["has_requirements"]);
        Assert.Equal("1", meta["has_pyproject"]);
        Assert.Equal("1", meta["has_init"]);
        // is_dirty:在 fake git 下空 stdout → 默认 "false"
        Assert.Equal("false", meta["is_dirty"]);
    }

    [Fact]
    public async Task RescanAsync_NonGitDir_GitFieldsEmptyString_NotThrows()
    {
        var nodeDir = Path.Combine(_tempDir, "custom_nodes", "bare-node");
        Directory.CreateDirectory(nodeDir);

        // 不创 .git/ → 所有 git 命令应该返非零退出码或空 stdout,不能崩
        await _ops.RescanAsync(_envId);

        var node = _nodeRepo.GetByPackageName(_envId, "bare-node");
        Assert.NotNull(node);
        var meta = node!.ScanMeta;
        // git 维度应该都是空字符串(不会 throw 也不会写 "null")
        Assert.Equal("", meta["branch"]);
        Assert.Equal("", meta["last_commit_date"]);
        Assert.Equal("", meta["last_commit_author"]);
        Assert.Equal("", meta["last_commit_short"]);
        Assert.Equal("", meta["behind_count"]);
        // FS 维度正常:无子文件 → 全 0
        Assert.Equal("0", meta["file_count"]);
        Assert.Equal("0", meta["python_files"]);
        Assert.Equal("0", meta["disk_size"]);
        Assert.Equal("0", meta["has_requirements"]);
        Assert.Equal("0", meta["has_pyproject"]);
        Assert.Equal("0", meta["has_init"]);
    }

    [Fact]
    public async Task RescanAsync_PackageNameFromInitPy_TakesPriority()
    {
        // RescanAsync 仍按 TryReadPackageName 从 __init__.py 读 PEP 621 Name
        var nodeDir = Path.Combine(_tempDir, "custom_nodes", "directory-name");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "__init__.py"), "Name: real-package-name\n");

        await _ops.RescanAsync(_envId);

        var node = _nodeRepo.GetByPackageName(_envId, "real-package-name");
        Assert.NotNull(node);
        Assert.Equal("real-package-name", node!.Package);
    }

    [Fact]
    public void ComputeDirectorySize_Recursive_SumsAllFileSizes()
    {
        var d = Path.Combine(_tempDir, "size-test");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(d, "a.txt"), new string('x', 100));
        File.WriteAllText(Path.Combine(d, "sub", "b.txt"), new string('y', 50));

        var size = NodeOperations.ComputeDirectorySize(d);
        Assert.Equal(150, size);
    }

    [Fact]
    public void ComputeDirectorySize_EmptyDir_ReturnsZero()
    {
        var d = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(d);
        Assert.Equal(0, NodeOperations.ComputeDirectorySize(d));
    }

    [Fact]
    public void ComputeFileCount_Recursive_CountsAllFiles()
    {
        var d = Path.Combine(_tempDir, "count-test");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(d, "sub1"));
        Directory.CreateDirectory(Path.Combine(d, "sub2"));
        File.WriteAllText(Path.Combine(d, "a.txt"), "");
        File.WriteAllText(Path.Combine(d, "sub1", "b.txt"), "");
        File.WriteAllText(Path.Combine(d, "sub2", "c.py"), "");

        Assert.Equal(3, NodeOperations.ComputeFileCount(d));
    }

    [Fact]
    public void ComputePythonFileCount_OnlyCountsPyExtension()
    {
        var d = Path.Combine(_tempDir, "py-test");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "a.py"), "");
        File.WriteAllText(Path.Combine(d, "b.py"), "");
        File.WriteAllText(Path.Combine(d, "c.txt"), "");
        File.WriteAllText(Path.Combine(d, "README.md"), "");

        Assert.Equal(2, NodeOperations.ComputePythonFileCount(d));
    }

    [Fact]
    public void ComputeDirectorySize_NonExistentDir_ReturnsZero_NoThrow()
    {
        // 不存在的目录:返回 0 不抛
        Assert.Equal(0, NodeOperations.ComputeDirectorySize(Path.Combine(_tempDir, "nope")));
    }

    /// <summary>
    /// v0.6.15.10 ScanMeta dict 持久化走现有 scan_meta TEXT/JSON 列,Upsert 必须
    /// 把 dict 写回 DB。回归测试:rescan 后 ListByEnv 拿出来的 row 跟 ScanMeta 字段
    /// 全 round-trip 完整(防止 SQLite migration / serialization 路径吃掉新 keys)。
    /// </summary>
    [Fact]
    public async Task RescanAsync_ScanMetaKeys_PersistThroughSqliteRoundtrip()
    {
        var nodeDir = Path.Combine(_tempDir, "custom_nodes", "roundtrip-node");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "main.py"), "x");

        await _ops.RescanAsync(_envId);

        // 模拟重启:从 DB 重新 ListByEnv,ScanMeta 应该跟 rescan 时一致
        var rows = _nodeRepo.ListByEnv(_envId);
        Assert.Single(rows);
        var meta = rows[0].ScanMeta;
        Assert.Equal("0", meta["has_init"]);  // 没 __init__.py → "0"
        Assert.Equal("0", meta["has_requirements"]);  // 没 requirements.txt → "0"
        Assert.Equal("1", meta["python_files"]);  // main.py 是 .py
        Assert.Equal("1", meta["file_count"]);
        // 关键:13 个 key 都活着(不是 partial dict)
        Assert.Equal(13, meta.Count(k => true));
    }
}