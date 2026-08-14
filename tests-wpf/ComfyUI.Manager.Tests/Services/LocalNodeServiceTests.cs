using System;
using System.IO;
using System.Linq;
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

public class LocalNodeInfoTests
{
    [Fact]
    public void Record_CanBeConstructed_WithAllFields()
    {
        var info = new LocalNodeInfo(
            NodeId: "comfyui-controlnet",
            HeadSha: "abc12345",
            InstallDate: new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc),
            HasPhysicalDir: true,
            IsInDb: true,
            InstalledEnvIds: new[] { "env-1" },
            InstalledEnvNames: new[] { "prod" });
        Assert.Equal("comfyui-controlnet", info.NodeId);
        Assert.Equal("abc12345", info.HeadSha);
        Assert.True(info.HasPhysicalDir);
        Assert.True(info.IsInDb);
        Assert.Single(info.InstalledEnvIds);
        Assert.Single(info.InstalledEnvNames);
    }
}

public class NodeRepositoryDeleteBySourceTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _repo;
    private const string SourceUrl = "https://example.com/catalog.json";

    public NodeRepositoryDeleteBySourceTests()
    {
        _db = new TestDb();
        _repo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void DeleteBySourceAndEnvId_RemovesOnlyMatchingRow()
    {
        // 三行:本地下载 + env-1 装 + env-2 装。
        // 注:id 是 PK;跨 env 装同包要用 env-specific id 后缀(PK 唯一)。
        // 这是项目既有 pattern(见 NodeRepositoryCountTests.cs)。
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "", Source = "download", Package = "pkg-a" });
        _repo.Upsert(new ScannedNode { Id = "pkg-a-env-1", EnvId = "env-1", Source = "env", Package = "pkg-a" });
        _repo.Upsert(new ScannedNode { Id = "pkg-a-env-2", EnvId = "env-2", Source = "env", Package = "pkg-a" });

        _repo.DeleteBySourceAndEnvId("pkg-a", "", "download");

        // download 行(id=pkg-a)删了
        Assert.Null(_repo.Get("pkg-a"));
        // env-1 + env-2 行还在
        var remaining = CountRows();
        Assert.Equal(2, remaining);  // 只剩 env-1 + env-2
    }

    [Fact]
    public void DeleteBySourceAndEnvId_NoMatch_NoOp()
    {
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "env-1", Source = "env", Package = "pkg-a" });
        _repo.DeleteBySourceAndEnvId("pkg-a", "", "download");  // 不匹配(env_id="env-1",不是 "")
        Assert.Equal(1, CountRows());
    }

    private int CountRows()
    {
        using var conn = new SqliteConnectionFactory(_db.Path).Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scanned_nodes";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

public class LocalNodeServiceTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly Settings _settings;
    private readonly string _localDir;
    private readonly GitRunner _git;
    private readonly NodeOperations _nodeOps;
    private readonly LocalNodeService _svc;

    public LocalNodeServiceTests()
    {
        _db = new TestDb();
        _localDir = Path.Combine(Path.GetTempPath(), "local-nodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        _nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
        _envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        _settings = new Settings { LocalNodeDirectory = _localDir };
        _git = new GitRunner("git");
        _nodeOps = new NodeOperations(
            _git, _envRepo, _nodeRepo, _settings,
            new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
        _svc = new LocalNodeService(_settings, _nodeRepo, _envRepo, _nodeOps, logger: null);
    }
    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
    }

    [Fact]
    public async Task ListAsync_EmptyDir_ReturnsEmpty()
    {
        var list = await _svc.ListAsync(CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task ListAsync_PhysicalDirOnly_HasPhysicalDirTrueIsInDbFalse()
    {
        // 物理目录有但 DB 无 row — 孤儿目录
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-a", "README.md"), "x");

        var list = await _svc.ListAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("pkg-a", list[0].NodeId);
        Assert.True(list[0].HasPhysicalDir);
        Assert.False(list[0].IsInDb);
        Assert.Empty(list[0].InstalledEnvIds);
    }

    [Fact]
    public async Task ListAsync_DbRowOnly_OrphanedDbRow()
    {
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-b", EnvId = "", Source = "download", Package = "pkg-b" });

        var list = await _svc.ListAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("pkg-b", list[0].NodeId);
        Assert.False(list[0].HasPhysicalDir);
        Assert.True(list[0].IsInDb);
    }

    [Fact]
    public async Task ListAsync_DbRowAndCrossEnvInstalls_BuildsBadge()
    {
        // Seed env-1 装了 pkg-c(env 装 + package=pkg-c)
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", RootPath = "/tmp/env1" });
        _envRepo.Upsert(new Environment { Id = "env-2", Name = "dev", RootPath = "/tmp/env2" });
        // 注:id 是 PK;同包跨 env 装用 env-specific id 后缀(项目既有 pattern)
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-c-env-1", EnvId = "env-1", Source = "env", Package = "pkg-c" });
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-c-env-2", EnvId = "env-2", Source = "env", Package = "pkg-c" });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-c"));

        var list = await _svc.ListAsync(CancellationToken.None);

        var item = list.Single();
        Assert.True(item.HasPhysicalDir);
        Assert.Equal(new[] { "env-1", "env-2" }, item.InstalledEnvIds);
        Assert.Equal(new[] { "prod", "dev" }, item.InstalledEnvNames);
    }

    [Fact]
    public async Task ListAsync_EnvInstalledPkgWithoutLocalDownload_NotShown()
    {
        // env 装了 pkg-d 但没本地下载 → 不该出现在本地节点列表
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", RootPath = "/tmp/env1" });
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-d", EnvId = "env-1", Source = "env", Package = "pkg-d" });

        var list = await _svc.ListAsync(CancellationToken.None);

        Assert.Empty(list);
    }

    [Fact]
    public async Task ListAsync_DownloadRowIgnoredAsInstalled()
    {
        // Source="download" 的行 EnvId="" 不算 installed(用 env_id != '' 过滤)
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-e", EnvId = "", Source = "download", Package = "pkg-e" });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-e"));

        var list = await _svc.ListAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Empty(list[0].InstalledEnvIds);  // download 行不算
    }

    [Fact]
    public async Task DeleteAsync_RemovesDirAndDbRow()
    {
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-f"));
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-f", EnvId = "", Source = "download", Package = "pkg-f" });

        var r = await _svc.DeleteAsync("pkg-f", CancellationToken.None);

        Assert.True(r.Success);
        Assert.False(Directory.Exists(Path.Combine(_localDir, "pkg-f")));
        Assert.Null(_nodeRepo.Get("pkg-f"));  // DB row 也清
    }

    [Fact]
    public async Task DeleteAsync_KeepsEnvInstallsIntact()
    {
        // pkg-g 在本地 + env-1 装过 — 删本地不动 env 行
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", RootPath = "/tmp/env1" });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-g"));
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-g", EnvId = "", Source = "download", Package = "pkg-g" });
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-g-env-1", EnvId = "env-1", Source = "env", Package = "pkg-g" });

        await _svc.DeleteAsync("pkg-g", CancellationToken.None);

        // 物理目录删
        Assert.False(Directory.Exists(Path.Combine(_localDir, "pkg-g")));
        // Source="env" 行还在(Get 按 id 拿任意一个,这里拿 env-1 那行)
        var remaining = _nodeRepo.Get("pkg-g-env-1");
        Assert.NotNull(remaining);
        Assert.Equal("env-1", remaining!.EnvId);
    }
}
