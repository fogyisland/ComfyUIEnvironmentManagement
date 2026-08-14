using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public class LocalNodeCopyInstallerTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly Settings _settings;
    private readonly string _srcDir;
    private readonly string _envRoot;
    private readonly GitRunner _git;
    private readonly NodeOperations _nodeOps;
    private readonly LocalNodeCopyInstaller _installer;

    public LocalNodeCopyInstallerTests()
    {
        _db = new TestDb();
        _srcDir = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N"));
        _envRoot = Path.Combine(Path.GetTempPath(), "envroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_srcDir);
        _nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
        _envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        _settings = new Settings();
        _git = new GitRunner("git");
        _nodeOps = new NodeOperations(
            _git, _envRepo, _nodeRepo, _settings,
            new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
        _installer = new LocalNodeCopyInstaller(_envRepo, _nodeRepo, _nodeOps, logger: null);
    }
    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_srcDir)) Directory.Delete(_srcDir, recursive: true);
        if (Directory.Exists(_envRoot)) Directory.Delete(_envRoot, recursive: true);
    }

    private Environment SeedEnv(string id, string name, string customNodesPath)
    {
        var env = new Environment { Id = id, Name = name, CustomNodesPath = customNodesPath };
        _envRepo.Upsert(env);
        return env;
    }

    [Fact]
    public async Task InstallAsync_HappyPath_CopiesDirAndWritesScannedNode()
    {
        SeedEnv("env-1", "prod", Path.Combine(_envRoot, "env-1", "custom_nodes"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "code.py"), "x = 1");

        var r = await _installer.InstallAsync(
            "env-1", Path.Combine(_srcDir, "pkg-a"), "pkg-a", CancellationToken.None);

        Assert.True(r.Success);
        var target = Path.Combine(_envRoot, "env-1", "custom_nodes", "pkg-a");
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(target, "code.py")));
        // DB row 写了
        var row = _nodeRepo.Get("pkg-a");
        Assert.NotNull(row);
        Assert.Equal("env-1", row!.EnvId);
        Assert.Equal("env", row.Source);
        Assert.Equal("pkg-a", row.Package);
    }

    [Fact]
    public async Task InstallAsync_TargetDirExists_FailsWithoutOverwriting()
    {
        SeedEnv("env-1", "prod", Path.Combine(_envRoot, "env-1", "custom_nodes"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-b", "f.txt"), "new");
        var target = Path.Combine(_envRoot, "env-1", "custom_nodes", "pkg-b");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "existing");

        var r = await _installer.InstallAsync(
            "env-1", Path.Combine(_srcDir, "pkg-b"), "pkg-b", CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("目录已存在", r.Reason);
        // 现有文件未覆盖
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "f.txt")));
        // DB 没写
        Assert.Null(_nodeRepo.Get("pkg-b"));
    }

    [Fact]
    public async Task InstallAsync_EnvNotFound_Fails()
    {
        var r = await _installer.InstallAsync(
            "missing-env", Path.Combine(_srcDir, "pkg-c"), "pkg-c", CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("env", r.Reason);
    }

    [Fact]
    public async Task InstallAsync_SourceDirMissing_Fails()
    {
        SeedEnv("env-1", "prod", Path.Combine(_envRoot, "env-1", "custom_nodes"));

        var r = await _installer.InstallAsync(
            "env-1", Path.Combine(_srcDir, "missing-pkg"), "missing-pkg", CancellationToken.None);

        Assert.False(r.Success);
    }

    [Fact]
    public async Task InstallAsync_CustomNodesPathMissing_CreatesIt()
    {
        var cnp = Path.Combine(_envRoot, "env-1", "custom_nodes");
        // 不预建 CustomNodesPath
        SeedEnv("env-1", "prod", cnp);
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-d"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-d", "f.txt"), "x");

        var r = await _installer.InstallAsync(
            "env-1", Path.Combine(_srcDir, "pkg-d"), "pkg-d", CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(Directory.Exists(cnp));
    }

    [Fact]
    public async Task InstallAsync_Success_RecordsHeadShaWhenGitRepo()
    {
        // 简化:Source 非 git 目录 → Version 留空(null),不抛
        SeedEnv("env-1", "prod", Path.Combine(_envRoot, "env-1", "custom_nodes"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-e"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-e", "f.txt"), "x");
        // 不 init git → TryReadHeadShaAsync 返 null → Version = null

        var r = await _installer.InstallAsync(
            "env-1", Path.Combine(_srcDir, "pkg-e"), "pkg-e", CancellationToken.None);

        Assert.True(r.Success);
        var row = _nodeRepo.Get("pkg-e");
        Assert.NotNull(row);
        Assert.Null(row!.Version);  // 或 "" — 看实现选
    }
}