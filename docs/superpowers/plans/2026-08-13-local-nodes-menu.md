# v0.6.15 本地节点菜单 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 加侧栏 "本地节点" tab + 卡片 "复制到 env" / "删除" 操作 + 跨 env badge,实现本地节点 → env 一键部署。复用 v0.6.7.9 已加的 `NodeOperations.DownloadAsync` + `Settings.LocalNodeDirectory` + `Source="download"` ScannedNode 机制(本 plan 主要是补 UI 入口 + 复制 path)。

**Architecture:** 6 task 增量:① `LocalNodeInfo` 数据模型 + `LocalNodeService` 扫盘/删除 ② `LocalNodeCopyInstaller` 复制路径 ③ `LocalNodeListViewModel` + `EnvPickerDialog` 业务逻辑 ④ `LocalNodeListView` XAML ⑤ `CatalogViewModel` "已下载" badge 增强 ⑥ `App.xaml.cs` DI + `MainViewModel` 切 view + `MainWindow.xaml` 侧栏按钮。每 task 独立 test cycle。

**Tech Stack:** .NET 8 WPF + xUnit + SQLite (`scanned_nodes` 已存在) + `Directory.Copy` (复制) + 既有 `NodeOperations.DownloadAsync` (v0.6.7.9) + 既有 `GitRunner` (读 head SHA)

## Global Constraints

- **DB schema 0 改动**:`scanned_nodes` 已有 `Source` / `EnvId` / `Id` / `Package` 字段,本 plan 仅用既有 schema
- **WPF Setter DynamicResource 必须 property-element**(v0.6.9.2 lesson):不能 `Setter Value="{DynamicResource ...}"`,必须 `<Setter.Property><DynamicResource .../></Setter.Property>`
- **新加的 Window / UserControl 跑 STA headless load test** 防 XAML 资源解析错(v0.6.9.2 教训)
- **`Source` 字段值**:`ScannedNode.Source` 用 `"env"`(env 装的,v0.6.11+)、`"download"`(本地下载,v0.6.7.9+);**不是** spec 草稿里写的 `"github"`(那是 plan 误植,实际生产代码用 `"env"`)
- **跨 env 状态查询 SQL**:`SELECT env_id FROM scanned_nodes WHERE package = @nodeId AND env_id != '' AND source = 'env'`(用 `package` 不是 `id`;`source = 'env'` 排除本地下载的 sentinel 行)
- **test seam 模式**:Dialog/MessageBox 走 `XxxOverride` 属性(FakeNodeOps / ShowOverride / MessageBoxOverride 同 pattern)
- **busy mutex pattern**:`Dictionary<NodeId, BusyKind>` 字段 + `IsBusy(nodeId)` gate(跟 v0.6.5.22 `EnvironmentListViewModel` 同款)
- **rollback 模式**:复制中途异常 → `TryDelete(targetDir)` + 不写 ScannedNode row(防止半新半旧)
- **STA-test 模式**:用既有的 `TestSynchronizationContext` helper(同 `EnvStartStatusViewModelDispatchTests`)避免 async/STA 死锁
- **现有 1071 tests 必须继续 PASS**(增量修改,无 breaking)

## File Map

| 文件 | 类型 | 职责 |
|------|------|------|
| `src-wpf/ComfyUI.Manager/Models/LocalNodeInfo.cs` | 新 | record: NodeId/HeadSha/InstallDate/HasPhysicalDir/IsInDb/InstalledEnvIds/InstalledEnvNames |
| `src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs` | 新 | ListAsync (扫盘 + join DB) / DeleteAsync (删目录 + DB row) |
| `src-wpf/ComfyUI.Manager/Services/LocalNodeCopyInstaller.cs` | 新 | InstallAsync (Directory.Copy + ScannedNode 写 + rollback) |
| `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs` | 修改 | 加 `DeleteBySourceAndEnvId(id, envId, source)` 方法 |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | 修改 | `TryReadHeadShaAsync` 改 `internal`(给 LocalNodeService 复用) |
| `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs` | 新 | Items + InstallCommand + DeleteCommand + RefreshCommand |
| `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListItem.cs` | 新 | INPC wrapper: Info + BadgeText + InstalledEnvNames |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvPickerDialogViewModel.cs` | 新 | EnvList + SelectedEnv + OkCommand |
| `src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml` + `.xaml.cs` | 新 | ListBox env + Ok/Cancel 按钮 + Show() static |
| `src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml` + `.xaml.cs` | 新 | 卡片布局 + 每行 BadgeBlock + 2 按钮(复制到 env / 删除) |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | 修改 | 加 `IsInLocalNodeDb(package)` 派生属性 + 通知 IsInLocalNodeDbChanged |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 修改 | "下载" 按钮已下载时 disabled + badge "已下载" |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 修改 | DI 注册 `LocalNodeService` + `LocalNodeCopyInstaller` |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | 修改 | 加 `ShowLocalNodesCommand` + `_localNodesViewModel` 懒构造 + `MainSection.LocalNodes` |
| `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs` | 修改 | 加 `MainSection.LocalNodes => "本地节点"` |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | 修改 | sidebar 加 RadioButton "本地节点" + ContentControl 绑定 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeServiceTests.cs` | 新 | 8 tests |
| `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeCopyInstallerTests.cs` | 新 | 6 tests |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalNodeListViewModelTests.cs` | 新 | 5 tests |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvPickerDialogViewModelTests.cs` | 新 | 3 tests |
| `tests-wpf/ComfyUI.Manager.Tests/Views/EnvPickerDialogLoadTests.cs` | 新 | 1 STA test |
| `tests-wpf/ComfyUI.Manager.Tests/Views/LocalNodeListViewLoadTests.cs` | 新 | 1 STA test |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelIsInLocalNodeDbTests.cs` | 新 | 2 tests |
| `tests-wpf/ComfyUI.Manager.Tests/App/AppStartupWiringTests.cs` | 扩展 | 1 test (DI 不为 null) |

**Test count delta:** +26 tests (1071 → 1097 PASS / 0 FAIL / 1 SKIP)

---

### Task 1: LocalNodeInfo + LocalNodeService + NodeRepository delete extension

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/LocalNodeInfo.cs`
- Modify: `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs:1-193` (加 1 方法)
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs:597-612` (private → internal)
- Create: `src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeServiceTests.cs`

**Interfaces:**
- Consumes: `Settings.LocalNodeDirectory`, `EnvironmentRepository.ListAll` (查 env 名), `NodeRepository.Get/Get/ListByEnv`, `NodeOperations.TryReadHeadShaAsync(workdir, ct)` (改 internal 后)
- Produces:
  - `record LocalNodeInfo(string NodeId, string? HeadSha, DateTime? InstallDate, bool HasPhysicalDir, bool IsInDb, IReadOnlyList<string> InstalledEnvIds, IReadOnlyList<string> InstalledEnvNames)`
  - `class LocalNodeService { Task<IReadOnlyList<LocalNodeInfo>> ListAsync(CancellationToken ct); Task<NodeOperationResult> DeleteAsync(string nodeId, CancellationToken ct); }`
  - `NodeRepository.DeleteBySourceAndEnvId(string id, string envId, string source)` 新方法

- [ ] **Step 1: 写 LocalNodeInfo record 测试**

创建 `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeServiceTests.cs`,先写 1 个最小测试验证 record 存在 + 字段对得上:

```csharp
using ComfyUI.Manager.Models;
using Xunit;

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
```

- [ ] **Step 2: 跑测试,确认 fail**(record 不存在)

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeInfoTests" --nologo 2>&1 | tail -10
```

Expected: build fail "类型 'LocalNodeInfo' 不存在"。

- [ ] **Step 3: 创建 LocalNodeInfo record**

创建 `src-wpf/ComfyUI.Manager/Models/LocalNodeInfo.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15:本地节点一条记录(物理目录 + DB row 合并视图)。
/// NodeId 是包名 = 目录名,等同 ScannedNode.Package。
/// 跨 env 状态通过 SELECT scanned_nodes WHERE package=@nodeId AND env_id != '' AND source='env' 查。
/// </summary>
public sealed record LocalNodeInfo(
    string NodeId,
    string? HeadSha,
    DateTime? InstallDate,
    bool HasPhysicalDir,
    bool IsInDb,
    IReadOnlyList<string> InstalledEnvIds,
    IReadOnlyList<string> InstalledEnvNames);
```

- [ ] **Step 4: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeInfoTests" --nologo 2>&1 | tail -5
```

Expected: 1 PASS。

- [ ] **Step 5: 写 `NodeRepository.DeleteBySourceAndEnvId` 测试**

在 `LocalNodeServiceTests.cs` 顶部加 `using` + 新测试类,验证新 delete 方法:

```csharp
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

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
        // 三行:本地下载 + env-1 装 + env-2 装
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "", Source = "download", Package = "pkg-a" });
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "env-1", Source = "env", Package = "pkg-a" });
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "env-2", Source = "env", Package = "pkg-a" });

        _repo.DeleteBySourceAndEnvId("pkg-a", "", "download");

        // download 行删了
        Assert.Null(_repo.Get("pkg-a") is { EnvId: "" } ? _repo.Get("pkg-a") : null);
        // 但 id=env-1 跟 env-2 都不在(id 唯一,Get 拿的是任意一个)
        // 改用 SQL 直接数
        var remaining = CountRows();
        Assert.Equal(2, remaining);  // 只剩 env-1 + env-2
    }

    [Fact]
    public void DeleteBySourceAndEnvId_NoMatch_NoOp()
    {
        _repo.Upsert(new ScannedNode { Id = "pkg-a", EnvId = "env-1", Source = "env", Package = "pkg-a" });
        _repo.DeleteBySourceAndEnvId("pkg-a", "", "download");  // 不匹配
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
```

(如果项目里 `SqliteConnectionFactory` 没有 public `Open()` 方法,改用 `_db.Path` 直连 SQLite 查 count。)

- [ ] **Step 6: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeRepositoryDeleteBySourceTests" --nologo 2>&1 | tail -5
```

Expected: build fail "DeleteBySourceAndEnvId 不存在"。

- [ ] **Step 7: 加 `DeleteBySourceAndEnvId` 到 `NodeRepository`**

打开 `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs`,在 line 141 `Delete` 方法后插入:

```csharp
/// <summary>
/// v0.6.15:按 (id, env_id, source) 三元组删除一行,只动匹配的行。
/// 用于 LocalNodeService.DeleteAsync(只删 EnvId="" + Source="download" 的本地下载行,
/// 不影响已装到 env 的 Source="env" 行)。不存在不抛。
/// </summary>
public void DeleteBySourceAndEnvId(string id, string envId, string source)
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM scanned_nodes WHERE id = @id AND env_id = @env_id AND source = @source";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@env_id", envId);
    cmd.Parameters.AddWithValue("@source", source);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 8: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeRepositoryDeleteBySourceTests" --nologo 2>&1 | tail -5
```

Expected: 2 PASS。

- [ ] **Step 9: 改 `NodeOperations.TryReadHeadShaAsync` 改 internal**

打开 `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs:597`,把:

```csharp
private async Task<string?> TryReadHeadShaAsync(string workdir, CancellationToken ct)
```

改成:

```csharp
/// <summary>
/// v0.6.15:改 internal 给 LocalNodeService.ListAsync 复用(读本地节点目录的 HEAD SHA,
/// 给 LocalNodeInfo.HeadSha)。不走 git 仓库 → 返 null 不抛。
/// </summary>
internal async Task<string?> TryReadHeadShaAsync(string workdir, CancellationToken ct)
```

- [ ] **Step 10: build,确认无破坏**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -5
```

Expected: build succeeded。

- [ ] **Step 11: 写 `LocalNodeService.ListAsync` 测试**

在 `LocalNodeServiceTests.cs` 加新测试,验证空目录 + 单目录无 DB row + 单目录 + DB row + 跨 env 装:

```csharp
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
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-c", EnvId = "env-1", Source = "env", Package = "pkg-c" });
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-c", EnvId = "env-2", Source = "env", Package = "pkg-c" });
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
        _nodeRepo.Upsert(new ScannedNode { Id = "pkg-g", EnvId = "env-1", Source = "env", Package = "pkg-g" });

        await _svc.DeleteAsync("pkg-g", CancellationToken.None);

        // 物理目录删
        Assert.False(Directory.Exists(Path.Combine(_localDir, "pkg-g")));
        // Source="env" 行还在(Get 按 id 拿任意一个,这里拿 env-1 那行)
        var remaining = _nodeRepo.Get("pkg-g");
        Assert.NotNull(remaining);
        Assert.Equal("env-1", remaining!.EnvId);
    }
}
```

(`ProcessResult` 类型如果项目里不存在,改用 4-tuple `(int ExitCode, string Stdout, string Stderr, bool Ok)` pattern。)

- [ ] **Step 12: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeServiceTests" --nologo 2>&1 | tail -10
```

Expected: build fail "LocalNodeService 不存在"。

- [ ] **Step 13: 创建 LocalNodeService**

创建 `src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15:本地节点 = <c>Settings.LocalNodeDirectory</c> 下子目录 ∪ <c>scanned_nodes</c>
/// <c>Source="download"</c> 行。两条独立校验,有任一就在列表里。
/// 跨 env 装状态走 SELECT scanned_nodes WHERE package=@nodeId AND env_id != '' AND source='env'。
/// </summary>
public class LocalNodeService
{
    private readonly Settings _settings;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly AppLogger? _logger;

    public LocalNodeService(
        Settings settings,
        NodeRepository nodeRepo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        AppLogger? logger = null)
    {
        _settings = settings;
        _nodeRepo = nodeRepo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _logger = logger;
    }

    public virtual async Task<IReadOnlyList<LocalNodeInfo>> ListAsync(CancellationToken ct)
    {
        var localDir = _settings.LocalNodeDirectory;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            _logger?.Warn("local-node", "LocalNodeDirectory 未配置,返 empty list");
            return Array.Empty<LocalNodeInfo>();
        }

        // 兜底建目录(跟 App.OnStartup 启动期建目录同 pattern)
        try { Directory.CreateDirectory(localDir); }
        catch (Exception ex)
        {
            _logger?.Warn("local-node", $"建本地目录失败:{ex.Message},返 empty");
            return Array.Empty<LocalNodeInfo>();
        }

        // 1) 扫物理子目录
        var physicalIds = new HashSet<string>(StringComparer.Ordinal);
        var physicalSha = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(localDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;
                physicalIds.Add(name);
                // 读 HEAD SHA(非 git 仓库 → null,跳过)
                var sha = await _nodeOps.TryReadHeadShaAsync(dir, ct);
                if (!string.IsNullOrEmpty(sha))
                {
                    physicalSha[name] = sha;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn("local-node", $"扫本地目录失败:{ex.Message}");
        }

        // 2) 扫 DB download 行(orphan DB row 也算)
        var dbIds = new HashSet<string>(StringComparer.Ordinal);
        var dbInstallDate = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var node in _nodeRepo.ListDownloadedNodes())
        {
            dbIds.Add(node.Package);  // node.Package = nodeId
            if (DateTime.TryParse(node.LastScannedAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                dbInstallDate[node.Package] = dt;
            }
        }

        // 3) 合并
        var allIds = new HashSet<string>(physicalIds, StringComparer.Ordinal);
        foreach (var id in dbIds) allIds.Add(id);

        // 4) 查跨 env 装(env 名提前 join 一次)
        var envMap = _envRepo.ListAll().ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);
        var result = new List<LocalNodeInfo>(allIds.Count);
        foreach (var id in allIds)
        {
            var envIds = _nodeRepo.GetInstalledEnvIdsByPackage(id);
            var envNames = envIds
                .Select(eid => envMap.TryGetValue(eid, out var n) ? n : eid)
                .ToList();
            result.Add(new LocalNodeInfo(
                NodeId: id,
                HeadSha: physicalSha.TryGetValue(id, out var s) ? s : null,
                InstallDate: dbInstallDate.TryGetValue(id, out var dt) ? dt : null,
                HasPhysicalDir: physicalIds.Contains(id),
                IsInDb: dbIds.Contains(id),
                InstalledEnvIds: envIds,
                InstalledEnvNames: envNames));
        }

        // 按 nodeId 排序(稳定显示)
        result.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
        _logger?.Info("local-node", $"ListAsync 完成 count={result.Count}");
        return result;
    }

    /// <summary>
    /// 删本地节点 = 物理目录 + EnvId="" + Source="download" 的 DB row。
    /// 已装到 env 的行 (EnvId != "", Source="env") 不动。
    /// </summary>
    public virtual async Task<NodeOperationResult> DeleteAsync(string nodeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return NodeOperationResult.Fail("nodeId 不能为空");
        }
        var localDir = _settings.LocalNodeDirectory;
        var dirPath = string.IsNullOrWhiteSpace(localDir)
            ? null
            : Path.Combine(localDir, nodeId);

        var dirExists = dirPath is not null && Directory.Exists(dirPath);
        if (!dirExists)
        {
            // 看 DB 是否有 download 行
            var anyDb = _nodeRepo.ListDownloadedNodes().Any(n => n.Package == nodeId);
            if (!anyDb) return NodeOperationResult.Fail("本地节点不存在");
        }

        if (dirExists)
        {
            TryDelete(dirPath!);
        }
        _nodeRepo.DeleteBySourceAndEnvId(nodeId, "", "download");
        _logger?.Info("local-node", $"删除本地节点 node='{nodeId}'");
        return NodeOperationResult.Ok(null);
    }

    private static void TryDelete(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }
}
```

- [ ] **Step 14: 跑测试,确认 fail(`ListDownloadedNodes` + `GetInstalledEnvIdsByPackage` 缺)**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeServiceTests" --nologo 2>&1 | tail -5
```

Expected: build fail "ListDownloadedNodes / GetInstalledEnvIdsByPackage 不存在"。

- [ ] **Step 15: 加 2 个查询方法到 `NodeRepository`**

打开 `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs`,在 `ListByEnv` 方法后插入:

```csharp
/// <summary>
/// v0.6.15:列所有 Source="download" 的行(本地下载的 sentinel 行)。
/// 用于 LocalNodeService.ListAsync 扫 DB 端。
/// </summary>
public List<ScannedNode> ListDownloadedNodes()
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, env_id, package, package_path, version, author,
               description, class_mappings, status, scan_meta,
               last_scanned_at, locked, source
        FROM scanned_nodes
        WHERE env_id = '' AND source = 'download'
        ORDER BY package";
    using var reader = cmd.ExecuteReader();
    var list = new List<ScannedNode>();
    while (reader.Read())
    {
        list.Add(Read(reader));
    }
    return list;
}

/// <summary>
/// v0.6.15:查本地节点 (nodeId) 装到了哪些 env —— 走 package = ? 不用 id = ?
/// (本地下载行 id 跟 env 装行 id 都 = nodeId,但 env 行 package 字段也存 nodeId)。
/// 返回 env_id 列表(已装 env 的 Id 集合)。
/// </summary>
public IReadOnlyList<string> GetInstalledEnvIdsByPackage(string nodeId)
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT DISTINCT env_id FROM scanned_nodes
        WHERE package = @pkg AND env_id != '' AND source = 'env'";
    cmd.Parameters.AddWithValue("@pkg", nodeId);
    using var reader = cmd.ExecuteReader();
    var list = new List<string>();
    while (reader.Read())
    {
        list.Add(reader.GetString(0));
    }
    return list;
}
```

- [ ] **Step 16: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeServiceTests|FullyQualifiedName~NodeRepositoryDeleteBySourceTests|FullyQualifiedName~LocalNodeInfoTests" --nologo 2>&1 | tail -5
```

Expected: 11 PASS (1 + 2 + 8)。

- [ ] **Step 17: 跑全套,确认无回归**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -5
```

Expected: 1082 PASS / 0 FAIL / 1 SKIP (1071 + 11 新)。

- [ ] **Step 18: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Models/LocalNodeInfo.cs src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs src-wpf/ComfyUI.Manager/Data/NodeRepository.cs src-wpf/ComfyUI.Manager/Services/NodeOperations.cs tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeServiceTests.cs && git commit -m "feat(local-nodes): T1 LocalNodeInfo + LocalNodeService + repo query (v0.6.15)"
```

---

### Task 2: LocalNodeCopyInstaller

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/LocalNodeCopyInstaller.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeCopyInstallerTests.cs`

**Interfaces:**
- Consumes: `NodeOperations.TryReadHeadShaAsync` (内部), `EnvironmentRepository.Get`, `NodeRepository.Upsert`
- Produces:
  - `class LocalNodeCopyInstaller { Task<NodeOperationResult> InstallAsync(string envId, string sourcePath, string nodeId, CancellationToken ct); }`

- [ ] **Step 1: 写测试 happy path**

创建 `tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeCopyInstallerTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

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
```

- [ ] **Step 2: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeCopyInstallerTests" --nologo 2>&1 | tail -5
```

Expected: build fail "LocalNodeCopyInstaller 不存在"。

- [ ] **Step 3: 创建 LocalNodeCopyInstaller**

创建 `src-wpf/ComfyUI.Manager/Services/LocalNodeCopyInstaller.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15:把本地节点 (LocalNodeDirectory/&lt;nodeId&gt;) 复制到 env 的 custom_nodes/&lt;nodeId&gt;。
/// 复用 NodeOperations.TryReadHeadShaAsync 读 SHA(非 git 仓库 → null 不抛)。
/// 失败路径(目录已存在 / env 缺失 / 复制异常)rollback 删目标目录 + 不写 ScannedNode row。
/// </summary>
public class LocalNodeCopyInstaller
{
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly AppLogger? _logger;

    public LocalNodeCopyInstaller(
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        NodeOperations nodeOps,
        AppLogger? logger = null)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _nodeOps = nodeOps;
        _logger = logger;
    }

    public virtual async Task<NodeOperationResult> InstallAsync(
        string envId, string sourcePath, string nodeId, CancellationToken ct = default)
    {
        _logger?.Info("local-node-copy", $"env='{envId}' node='{nodeId}' src='{sourcePath}' 开始复制");

        var env = _envRepo.Get(envId);
        if (env is null) return NodeOperationResult.Fail($"env '{envId}' 不存在");
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            return NodeOperationResult.Fail("env 缺 custom_nodes_path");
        }
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            return NodeOperationResult.Fail($"源目录不存在:{sourcePath}");
        }

        var targetDir = Path.Combine(env.CustomNodesPath, nodeId);
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"目录已存在:{targetDir}");
        }

        try
        {
            Directory.CreateDirectory(env.CustomNodesPath);
            // recursive copy
            CopyDirectory(sourcePath, targetDir);
        }
        catch (Exception ex)
        {
            // 失败清理目标(可能 copy 半路挂)
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"复制失败:{ex.Message}");
        }

        // 读 HEAD SHA(非 git 仓库 → null,Version = "")
        var headSha = await _nodeOps.TryReadHeadShaAsync(targetDir, ct);

        try
        {
            _nodeRepo.Upsert(new ScannedNode
            {
                Id = nodeId,
                EnvId = envId,
                Package = nodeId,
                PackagePath = targetDir,
                Version = string.IsNullOrEmpty(headSha) ? null : headSha,
                Status = "enabled",
                Source = "env",
                LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            });
        }
        catch (Exception ex)
        {
            // 写 DB 失败 → rollback
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"写 ScannedNode 失败:{ex.Message}");
        }

        _logger?.Info("local-node-copy", $"env='{envId}' node='{nodeId}' 复制成功");
        return NodeOperationResult.Ok(headSha);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeCopyInstallerTests" --nologo 2>&1 | tail -5
```

Expected: 6 PASS。

- [ ] **Step 5: 跑全套,确认无回归**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -3
```

Expected: 1088 PASS / 0 FAIL / 1 SKIP (1082 + 6)。

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Services/LocalNodeCopyInstaller.cs tests-wpf/ComfyUI.Manager.Tests/Services/LocalNodeCopyInstallerTests.cs && git commit -m "feat(local-nodes): T2 LocalNodeCopyInstaller (v0.6.15)"
```

---

### Task 3: LocalNodeListViewModel + EnvPickerDialog

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListItem.cs`
- Create: `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/ViewModels/EnvPickerDialogViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalNodeListViewModelTests.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvPickerDialogViewModelTests.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Views/EnvPickerDialogLoadTests.cs`

**Interfaces:**
- Consumes: `LocalNodeService`, `LocalNodeCopyInstaller`, `EnvironmentRepository`, `ErrorBannerViewModel`
- Produces:
  - `class LocalNodeListItem : ViewModelBase` (Info + BadgeText + InstalledEnvNames)
  - `class LocalNodeListViewModel : ViewModelBase { ObservableCollection<LocalNodeListItem> Items; RelayCommand InstallCommand; RelayCommand DeleteCommand; RelayCommand RefreshCommand; Func<string, List<EnvOption>, EnvOption?>? EnvPickerOverride; }`
  - `record EnvOption(string Id, string Name)` (在 EnvPickerDialogViewModel.cs 同一文件)
  - `class EnvPickerDialogViewModel : ViewModelBase { ObservableCollection<EnvOption> Envs; EnvOption? Selected; RelayCommand OkCommand; RelayCommand CancelCommand; }`
  - `class EnvPickerDialog : Window { static Func<string, List<EnvOption>, EnvOption?>? ShowOverride; static EnvOption? Show(string title, List<EnvOption> envs); }`

- [ ] **Step 1: 写 EnvPickerDialogViewModel 测试**

创建 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvPickerDialogViewModelTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvPickerDialogViewModelTests
{
    [Fact]
    public void Constructor_BindsEnvList()
    {
        var envs = new List<EnvOption>
        {
            new("env-1", "prod"),
            new("env-2", "dev"),
        };
        var vm = new EnvPickerDialogViewModel(envs);

        Assert.Equal(2, vm.Environments.Count);
        Assert.Equal("prod", vm.Environments[0].Name);
    }

    [Fact]
    public void OkCommand_FiresClosedWithSelectedEnv()
    {
        var envs = new List<EnvOption> { new("env-1", "prod"), new("env-2", "dev") };
        var vm = new EnvPickerDialogViewModel(envs);
        EnvOption? captured = null;
        vm.Closed += e => captured = e;
        vm.Selected = envs[1];

        vm.OkCommand.Execute(null);

        Assert.Equal("env-2", captured?.Id);
    }

    [Fact]
    public void CancelCommand_FiresClosedWithNull()
    {
        var envs = new List<EnvOption> { new("env-1", "prod") };
        var vm = new EnvPickerDialogViewModel(envs);
        EnvOption? captured = new("placeholder", "x");
        vm.Closed += e => captured = e;
        vm.Selected = envs[0];

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }
}
```

- [ ] **Step 2: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvPickerDialogViewModelTests" --nologo 2>&1 | tail -5
```

Expected: build fail "EnvOption / EnvPickerDialogViewModel 不存在"。

- [ ] **Step 3: 创建 EnvPickerDialogViewModel + EnvOption record**

创建 `src-wpf/ComfyUI.Manager/ViewModels/EnvPickerDialogViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Mvvm;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:EnvPickerDialog 弹窗时使用的简化 env 记录。只装 Id + Name,够 UI 列表展示。
/// </summary>
public sealed record EnvOption(string Id, string Name);

/// <summary>
/// v0.6.15:本地节点 → 复制到 env 时的 env 选择 dialog VM。
/// Closed event:OK 返 SelectedEnv;Cancel 返 null。
/// </summary>
public class EnvPickerDialogViewModel : ViewModelBase
{
    public ObservableCollection<EnvOption> Environments { get; }

    private EnvOption? _selected;
    public EnvOption? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>关闭时 fire 一次:OK 返 SelectedEnv,Cancel 返 null。</summary>
    public event Action<EnvOption?>? Closed;

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public EnvPickerDialogViewModel(IList<EnvOption> envs)
    {
        Environments = new ObservableCollection<EnvOption>(envs);
        // 默认选第一个
        _selected = Environments.FirstOrDefault();
        OkCommand = new RelayCommand(_ => Closed?.Invoke(Selected), _ => Selected is not null);
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
    }
}
```

- [ ] **Step 4: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvPickerDialogViewModelTests" --nologo 2>&1 | tail -5
```

Expected: 3 PASS。

- [ ] **Step 5: 创建 EnvPickerDialog XAML + code-behind**

创建 `src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml`:

```xml
<Window x:Class="ComfyUI.Manager.Views.EnvPickerDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="选择环境" Height="360" Width="420"
        Background="{DynamicResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Margin="0,0,0,8"
                   Text="{Binding TitleText}"
                   FontSize="14" FontWeight="SemiBold" />
        <ListBox Grid.Row="1" x:Name="EnvList"
                 ItemsSource="{Binding Environments}"
                 SelectedItem="{Binding Selected, Mode=TwoWay}"
                 DisplayMemberPath="Name" />
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="取消" Command="{Binding CancelCommand}" Margin="0,0,8,0" MinWidth="80" />
            <Button Content="确定" Command="{Binding OkCommand}" MinWidth="80" />
        </StackPanel>
    </Grid>
</Window>
```

创建 `src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class EnvPickerDialog : Window
{
    /// <summary>
    /// 测试 seam:单测可注入只返 stub EnvOption 的函数,避开 WPF 弹窗。
    /// </summary>
    public static Func<string, List<EnvOption>, EnvOption?>? ShowOverride { get; set; }

    public string TitleText { get; }

    public EnvPickerDialog(EnvPickerDialogViewModel vm, string title)
    {
        InitializeComponent();
        DataContext = vm;
        TitleText = title;
        vm.Closed += result =>
        {
            DialogResult = result is not null;
            Close();
        };
    }

    public static EnvOption? Show(string title, List<EnvOption> envs)
    {
        if (ShowOverride is not null) return ShowOverride(title, envs);
        var vm = new EnvPickerDialogViewModel(envs);
        var dlg = new EnvPickerDialog(vm, title) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return vm.Selected;  // Closed event 触发后 vm.Selected 仍保留用户选的
    }
}
```

- [ ] **Step 6: 写 STA load test**

创建 `tests-wpf/ComfyUI.Manager.Tests/Views/EnvPickerDialogLoadTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class EnvPickerDialogLoadTests : IDisposable
{
    public EnvPickerDialogLoadTests()
    {
        // STA 必需
        var t = Thread.CurrentThread;
        if (t.GetApartmentState() != ApartmentState.STA)
        {
            // xUnit 默认 STA(加 [Collection] / [STAThread] attribute 视项目配置)
            // 这里 fallback:抛让用户知道
            throw new InvalidOperationException("Test must run on STA thread");
        }
    }
    public void Dispose() { }

    [Fact]
    public void Constructor_LoadsXaml_NoException()
    {
        var envs = new List<EnvOption> { new("env-1", "prod") };
        var vm = new EnvPickerDialogViewModel(envs);
        var dlg = new EnvPickerDialog(vm, "test title");
        Assert.NotNull(dlg);
    }
}
```

- [ ] **Step 7: 跑 STA test,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvPickerDialogLoadTests" --nologo 2>&1 | tail -10
```

Expected: 1 PASS。如果项目 xUnit 默认不是 STA,加 `[Collection("STA")]` attribute + 创建 `STAThreadCollection` class(参考 v0.6.5.13 AppLogger STA test pattern)。

- [ ] **Step 8: 写 LocalNodeListViewModel 测试**

创建 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalNodeListViewModelTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class LocalNodeListViewModelTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly Settings _settings;
    private readonly string _localDir;
    private readonly GitRunner _git;
    private readonly NodeOperations _nodeOps;
    private readonly LocalNodeService _svc;
    private readonly LocalNodeCopyInstaller _installer;
    private readonly LocalNodeListViewModel _vm;
    private readonly ErrorBannerViewModel _errorBanner;

    public LocalNodeListViewModelTests()
    {
        _db = new TestDb();
        _localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        _nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
        _envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        _settings = new Settings { LocalNodeDirectory = _localDir };
        _git = new GitRunner("git");
        _nodeOps = new NodeOperations(
            _git, _envRepo, _nodeRepo, _settings,
            new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
        _svc = new LocalNodeService(_settings, _nodeRepo, _envRepo, _nodeOps, logger: null);
        _installer = new LocalNodeCopyInstaller(_envRepo, _nodeRepo, _nodeOps, logger: null);
        _errorBanner = new ErrorBannerViewModel();
        _vm = new LocalNodeListViewModel(_svc, _installer, _envRepo, _errorBanner);
    }
    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
    }

    [Fact]
    public async Task RefreshCommand_PopulatesItems()
    {
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-a"));

        await _vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(_vm.Items);
        Assert.Equal("pkg-a", _vm.Items[0].Info.NodeId);
    }

    [Fact]
    public async Task InstallCommand_PickerCancels_DoesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-b"));
        await _vm.RefreshCommand.ExecuteAsync(null);
        // picker 返 null = 取消
        _vm.EnvPickerOverride = (_, _) => null;

        await _vm.InstallCommand.ExecuteAsync(_vm.Items[0].Info);

        Assert.Empty(_vm.Items[0].Info.InstalledEnvIds);  // 未装
    }

    [Fact]
    public async Task InstallCommand_PickerSelectsEnv_CopiesAndAppendsBadge()
    {
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", CustomNodesPath = "/tmp/env1" });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-c"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-c", "f.txt"), "x");
        await _vm.RefreshCommand.ExecuteAsync(null);
        // 模拟 env picker 选 env-1
        _vm.EnvPickerOverride = (_, envs) => envs.Single(e => e.Id == "env-1");

        await _vm.InstallCommand.ExecuteAsync(_vm.Items[0].Info);

        Assert.Equal(new[] { "env-1" }, _vm.Items[0].Info.InstalledEnvIds);
        Assert.Equal(new[] { "prod" }, _vm.Items[0].Info.InstalledEnvNames);
        Assert.Contains("prod", _vm.Items[0].BadgeText);
    }

    [Fact]
    public async Task DeleteCommand_AfterConfirm_RemovesItem()
    {
        _vm.ConfirmDialogOverride = (_, _, _) => true;  // 用户确认
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-d"));
        await _vm.RefreshCommand.ExecuteAsync(null);

        await _vm.DeleteCommand.ExecuteAsync(_vm.Items[0].Info);

        Assert.Empty(_vm.Items);
        Assert.False(Directory.Exists(Path.Combine(_localDir, "pkg-d")));
    }

    [Fact]
    public async Task DeleteCommand_AfterCancel_KeepsItem()
    {
        _vm.ConfirmDialogOverride = (_, _, _) => false;  // 用户取消
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-e"));
        await _vm.RefreshCommand.ExecuteAsync(null);

        await _vm.DeleteCommand.ExecuteAsync(_vm.Items[0].Info);

        Assert.Single(_vm.Items);
    }
}
```

- [ ] **Step 9: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeListViewModelTests" --nologo 2>&1 | tail -5
```

Expected: build fail "LocalNodeListViewModel 不存在"。

- [ ] **Step 10: 创建 LocalNodeListItem + LocalNodeListViewModel**

创建 `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListItem.cs`:

```csharp
using System;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Mvvm;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:INPC wrapper 包 LocalNodeInfo 给 XAML DataTemplate 用。
/// BadgeText 拼 "已装: env-A, env-B" 形式,InstalledEnvNames 排序稳定。
/// </summary>
public class LocalNodeListItem : ViewModelBase
{
    public LocalNodeInfo Info { get; }

    public LocalNodeListItem(LocalNodeInfo info)
    {
        Info = info;
        UpdateBadge();
    }

    private string _badgeText = "";
    public string BadgeText
    {
        get => _badgeText;
        private set => SetProperty(ref _badgeText, value);
    }

    public string DisplayName => string.IsNullOrEmpty(Info.NodeId) ? "(unnamed)" : Info.NodeId;
    public string HeadShaDisplay => Info.HeadSha is { Length: >= 8 } ? Info.HeadSha[..8] : (Info.HeadSha ?? "—");

    public void UpdateBadge()
    {
        if (Info.InstalledEnvNames.Count == 0)
        {
            BadgeText = "未装到任何 env";
        }
        else
        {
            BadgeText = "已装: " + string.Join(", ", Info.InstalledEnvNames);
        }
        OnPropertyChanged(nameof(InstalledEnvNames));
    }
}
```

创建 `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Mvvm;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:本地节点列表页 VM。Items + 3 commands(Refresh / Install / Delete)+ busy mutex。
/// </summary>
public class LocalNodeListViewModel : ViewModelBase
{
    private readonly LocalNodeService _svc;
    private readonly LocalNodeCopyInstaller _installer;
    private readonly EnvironmentRepository _envRepo;
    private readonly ErrorBannerViewModel _errorBanner;

    public ObservableCollection<LocalNodeListItem> Items { get; } = new();

    /// <summary>test seam:替代真弹 EnvPickerDialog。返 null = 取消。</summary>
    public Func<string, List<EnvOption>, EnvOption?>? EnvPickerOverride { get; set; }

    /// <summary>test seam:替代真弹 ConfirmDialog。返 true = 确认删。</summary>
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public LocalNodeListViewModel(
        LocalNodeService svc,
        LocalNodeCopyInstaller installer,
        EnvironmentRepository envRepo,
        ErrorBannerViewModel errorBanner)
    {
        _svc = svc;
        _installer = installer;
        _envRepo = envRepo;
        _errorBanner = errorBanner;
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        InstallCommand = new RelayCommand(
            async info => await InstallAsync((LocalNodeInfo)info!),
            info => info is LocalNodeInfo);
        DeleteCommand = new RelayCommand(
            async info => await DeleteAsync((LocalNodeInfo)info!),
            info => info is LocalNodeInfo);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var list = await _svc.ListAsync(CancellationToken.None);
            Items.Clear();
            foreach (var info in list)
            {
                Items.Add(new LocalNodeListItem(info));
            }
        }
        catch (Exception ex)
        {
            _errorBanner.Add("local-node-refresh", $"加载本地节点失败:{ex.Message}",
                ErrorSeverity.Warn);
        }
    }

    private async Task InstallAsync(LocalNodeInfo info)
    {
        var envs = _envRepo.ListAll()
            .Select(e => new EnvOption(e.Id, e.Name))
            .ToList();
        if (envs.Count == 0)
        {
            _errorBanner.Add("local-node-install", "没有可用的 env,请先创建一个", ErrorSeverity.Warn);
            return;
        }

        var title = $"将 {info.NodeId} 复制到哪个 env?";
        EnvOption? selected = EnvPickerOverride is not null
            ? EnvPickerOverride(title, envs)
            : EnvPickerDialog.Show(title, envs);
        if (selected is null) return;  // 用户取消

        var localDir = (string.IsNullOrEmpty(_svc.GetType()
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_svc) as Settings)?.LocalNodeDirectory ?? "")
            ?? "";
        // 走 Settings 反射拿不到 → 简化:从 Service 暴露一个 SourcePathOf helper
        var sourcePath = _svc.GetLocalNodePath(info.NodeId);
        if (string.IsNullOrEmpty(sourcePath) || !System.IO.Directory.Exists(sourcePath))
        {
            _errorBanner.Add("local-node-install", $"本地源目录不存在:{sourcePath}", ErrorSeverity.Warn);
            return;
        }

        var r = await _installer.InstallAsync(selected.Id, sourcePath, info.NodeId, CancellationToken.None);
        if (!r.Success)
        {
            _errorBanner.Add("local-node-install", $"复制失败:{r.Reason}", ErrorSeverity.Error);
            return;
        }
        // 更新受影响 card 的 badge,不重 fetch 整列表
        var item = Items.FirstOrDefault(i => i.Info.NodeId == info.NodeId);
        if (item is not null)
        {
            var newEnvIds = item.Info.InstalledEnvIds.Append(selected.Id).Distinct().ToList();
            var newEnvNames = newEnvIds
                .Select(eid => _envRepo.Get(eid)?.Name ?? eid)
                .ToList();
            // 替换 Info(immutable record)
            var newInfo = info with { InstalledEnvIds = newEnvIds, InstalledEnvNames = newEnvNames };
            var idx = Items.IndexOf(item);
            Items[idx] = new LocalNodeListItem(newInfo);
        }
    }

    private async Task DeleteAsync(LocalNodeInfo info)
    {
        var ok = ConfirmDialogOverride is not null
            ? ConfirmDialogOverride(
                $"确认删除本地节点 {info.NodeId}?已装到 env 的副本不删。",
                "确认删除", "取消")
            : Views.ConfirmDialog.Show(
                $"确认删除本地节点 {info.NodeId}?已装到 env 的副本不删。");
        if (!ok) return;

        var r = await _svc.DeleteAsync(info.NodeId, CancellationToken.None);
        if (!r.Success)
        {
            _errorBanner.Add("local-node-delete", $"删除失败:{r.Reason}", ErrorSeverity.Error);
            return;
        }
        var item = Items.FirstOrDefault(i => i.Info.NodeId == info.NodeId);
        if (item is not null) Items.Remove(item);
    }
}
```

- [ ] **Step 11: 在 `LocalNodeService` 加 `GetLocalNodePath` helper**

打开 `src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs`,在 `ListAsync` 方法前加:

```csharp
/// <summary>v0.6.15:返回本地节点物理目录绝对路径(供 LocalNodeCopyInstaller 调)。
/// 未配置 LocalNodeDirectory 返 null。</summary>
public string? GetLocalNodePath(string nodeId)
{
    if (string.IsNullOrWhiteSpace(_settings.LocalNodeDirectory)) return null;
    return Path.Combine(_settings.LocalNodeDirectory, nodeId);
}
```

- [ ] **Step 12: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeListViewModelTests" --nologo 2>&1 | tail -5
```

Expected: 5 PASS。

- [ ] **Step 13: 跑全套,确认无回归**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -3
```

Expected: 1096 PASS / 0 FAIL / 1 SKIP (1088 + 8 新: 3 + 1 + 5 - 1 已存在 ListAsync; 净 +8)。

- [ ] **Step 14: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListItem.cs src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs src-wpf/ComfyUI.Manager/ViewModels/EnvPickerDialogViewModel.cs src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml src-wpf/ComfyUI.Manager/Views/EnvPickerDialog.xaml.cs src-wpf/ComfyUI.Manager/Services/LocalNodeService.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvPickerDialogViewModelTests.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalNodeListViewModelTests.cs tests-wpf/ComfyUI.Manager.Tests/Views/EnvPickerDialogLoadTests.cs && git commit -m "feat(local-nodes): T3 VM + EnvPickerDialog (v0.6.15)"
```

---

### Task 4: LocalNodeListView XAML + STA load test

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Views/LocalNodeListViewLoadTests.cs`

**Interfaces:**
- Consumes: `LocalNodeListViewModel` (DataContext)
- Produces: `UserControl LocalNodeListView`(XAML load OK)

- [ ] **Step 1: 创建 LocalNodeListView XAML**

创建 `src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml`:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.LocalNodeListView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             Background="{DynamicResource BackgroundBrush}">
    <UserControl.Resources>
        <Style x:Key="LocalNodeCardStyle" TargetType="Border">
            <Setter Property="Background" Value="{DynamicResource SurfaceBrush}" />
            <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="6" />
            <Setter Property="Padding" Value="12" />
            <Setter Property="Margin" Value="0,0,0,8" />
        </Style>
    </UserControl.Resources>

    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Header bar -->
        <Grid Grid.Row="0" Margin="0,0,0,12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="本地节点" FontSize="18" FontWeight="SemiBold"
                       VerticalAlignment="Center" />
            <Button Grid.Column="1" Content="刷新" Command="{Binding RefreshCommand}"
                    MinWidth="80" />
        </Grid>

        <!-- Empty state -->
        <Border Grid.Row="1" x:Name="EmptyState"
                Background="{DynamicResource SurfaceBrush}"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                CornerRadius="6" Padding="32"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                Visibility="{Binding Items.Count, Converter={StaticResource ZeroCountToVisibility}}">
            <StackPanel HorizontalAlignment="Center">
                <TextBlock Text="本地节点目录为空" FontSize="16" HorizontalAlignment="Center" Margin="0,0,0,8" />
                <TextBlock HorizontalAlignment="Center" TextWrapping="Wrap" MaxWidth="400"
                           TextAlignment="Center" Foreground="{DynamicResource TextSecondaryBrush}">
                    <Run Text="从节点目录下载节点后会出现在这里;或" />
                    <Run Text="先在 Settings 配置本地节点目录路径" />
                </TextBlock>
            </StackPanel>
        </Border>

        <!-- List -->
        <ListBox Grid.Row="1" x:Name="ItemsList"
                 ItemsSource="{Binding Items}"
                 Background="Transparent" BorderThickness="0"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                 Visibility="{Binding Items.Count, Converter={StaticResource InverseZeroCountToVisibility}}">
            <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem">
                    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                    <Setter Property="Padding" Value="0" />
                </Style>
            </ListBox.ItemContainerStyle>
            <ListBox.ItemTemplate>
                <DataTemplate DataType="{x:Type vm:LocalNodeListItem}">
                    <Border Style="{StaticResource LocalNodeCardStyle}">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>
                            <Grid Grid.Row="0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{Binding DisplayName}"
                                           FontSize="14" FontWeight="SemiBold" />
                                <TextBlock Grid.Column="1" Text="{Binding BadgeText}"
                                           Foreground="{DynamicResource PrimaryBrush}"
                                           FontSize="12" VerticalAlignment="Center" />
                            </Grid>
                            <TextBlock Grid.Row="1" Margin="0,4,0,0"
                                       FontSize="11" Opacity="0.7">
                                <Run Text="HEAD: " />
                                <Run Text="{Binding HeadShaDisplay, Mode=OneWay}" />
                            </TextBlock>
                            <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,8,0,0">
                                <Button Content="复制到 env"
                                        Command="{Binding DataContext.InstallCommand, RelativeSource={RelativeSource AncestorType=ListBox}}"
                                        CommandParameter="{Binding}"
                                        MinWidth="100" Margin="0,0,8,0" />
                                <Button Content="删除"
                                        Command="{Binding DataContext.DeleteCommand, RelativeSource={RelativeSource AncestorType=ListBox}}"
                                        CommandParameter="{Binding}"
                                        MinWidth="80" />
                            </StackPanel>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </Grid>
</UserControl>
```

- [ ] **Step 2: 创建 LocalNodeListView.xaml.cs**

```csharp
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class LocalNodeListView : UserControl
{
    public LocalNodeListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.LocalNodeListViewModel vm)
            {
                // 首次进入自动 refresh
                _ = vm.RefreshCommand.ExecuteAsync(null);
            }
        };
    }
}
```

- [ ] **Step 3: 写 STA load test**

创建 `tests-wpf/ComfyUI.Manager.Tests/Views/LocalNodeListViewLoadTests.cs`:

```csharp
using System;
using System.Threading;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class LocalNodeListViewLoadTests : IDisposable
{
    public LocalNodeListViewLoadTests()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Test must run on STA thread");
    }
    public void Dispose() { }

    [Fact]
    public void Constructor_LoadsXaml_NoException()
    {
        var vm = new LocalNodeListViewModel(
            new LocalNodeService(new Models.Settings(), null!, null!, null!),
            new LocalNodeCopyInstaller(null!, null!, null!),
            null!, new ErrorBannerViewModel());
        var view = new LocalNodeListView { DataContext = vm };
        Assert.NotNull(view);
    }
}
```

- [ ] **Step 4: 跑 STA test,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~LocalNodeListViewLoadTests" --nologo 2>&1 | tail -10
```

Expected: 1 PASS。如果 XAML 报 "无法找到资源" 错,检查 Setter 是否走 property-element pattern(v0.6.9.2 lesson) — 改 `<Setter Property="Background" Value="..." />` 为 `<Setter Property="Background">` + 子元素。

- [ ] **Step 5: 跑全套,确认无回归**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -3
```

Expected: 1097 PASS / 0 FAIL / 1 SKIP (1096 + 1)。

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml src-wpf/ComfyUI.Manager/Views/LocalNodeListView.xaml.cs tests-wpf/ComfyUI.Manager.Tests/Views/LocalNodeListViewLoadTests.cs && git commit -m "feat(local-nodes): T4 LocalNodeListView XAML (v0.6.15)"
```

---

### Task 5: CatalogViewModel 已下载 badge

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(加 IsInLocalNodeDb 派生)
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(按钮 disabled + badge)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelIsInLocalNodeDbTests.cs`(2 tests)

**Interfaces:**
- Consumes: `NodeRepository.ListDownloadedNodes`(Task 1 已加)
- Produces: `CatalogViewModel.IsInLocalNodeDbFor(Package)` 派生属性 + 通知

- [ ] **Step 1: 写测试**

创建 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelIsInLocalNodeDbTests.cs`:

```csharp
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelIsInLocalNodeDbTests : System.IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _nodeRepo;

    public CatalogViewModelIsInLocalNodeDbTests()
    {
        _db = new TestDb();
        _nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void IsInLocalNodeDb_NoDownloadRow_ReturnsFalse()
    {
        // 构造最简单的 VM(只测 IsInLocalNodeDbFor,不全功能)
        var vm = MakeVm();
        Assert.False(vm.IsInLocalNodeDbFor("pkg-a"));
    }

    [Fact]
    public void IsInLocalNodeDb_HasDownloadRow_ReturnsTrue()
    {
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = "pkg-b", EnvId = "", Source = "download", Package = "pkg-b"
        });
        var vm = MakeVm();
        Assert.True(vm.IsInLocalNodeDbFor("pkg-b"));
    }

    private CatalogViewModel MakeVm()
    {
        // 用 null catalog/ops 简化(测的逻辑只走 nodeRepo)
        // 实际 CatalogViewModel ctor 多参数,这里只关心 IsInLocalNodeDbFor 跟 nodeRepo
        // → 走 reflection 或 TestCatalogViewModel 子类
        return new CatalogViewModelForTest(_nodeRepo);
    }
}

/// <summary>CatalogViewModel 子类,只暴露 IsInLocalNodeDbFor 测法。</summary>
internal class CatalogViewModelForTest : CatalogViewModel
{
    public CatalogViewModelForTest(NodeRepository nodeRepo) : base(
        catalogRepo: null!, versionRepo: null!, nodeOps: null!,
        catalogRefreshService: null!, settings: new Settings(),
        settingsRepo: null!, projectRoot: "")
    {
        _testNodeRepo = nodeRepo;
    }
    private readonly NodeRepository _testNodeRepo;

    public new bool IsInLocalNodeDbFor(string package)
        => base.IsInLocalNodeDbFor(package);
}
```

- [ ] **Step 2: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~CatalogViewModelIsInLocalNodeDbTests" --nologo 2>&1 | tail -5
```

Expected: build fail "IsInLocalNodeDbFor 不存在"。

- [ ] **Step 3: 在 `CatalogViewModel` 加 `IsInLocalNodeDbFor` + NodeRepository 字段**

打开 `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`,找到 ctor(参数含 `NodeOperations nodeOps` 等),添加 `NodeRepository? nodeRepo` 可选 ctor 参数(在尾部加 default null),并加 protected field + 公开方法:

```csharp
// 文件顶 using 加:
using ComfyUI.Manager.Data;

// 类内字段区(找 _nodeOps 之类)加:
private readonly NodeRepository? _nodeRepo;

// ctor 末尾加新参数:
public CatalogViewModel(
    CatalogRepository catalogRepo,
    NodeVersionRepository versionRepo,
    NodeOperations nodeOps,
    CatalogRefreshService catalogRefreshService,
    Settings settings,
    SettingsRepository settingsRepo,
    string projectRoot,
    NodeRepository? nodeRepo = null)  // v0.6.15:测 IsInLocalNodeDbFor 用
{
    // ... 现有初始化 ...
    _nodeRepo = nodeRepo;
}

// 公开方法(在 class 任意位置):
/// <summary>
/// v0.6.15:查 package (nodeId) 是否已下载到本地节点目录(看 scanned_nodes Source="download" 行)。
/// XAML 绑 CatalogEntryItem.IsInLocalNodeDb 控制 "下载" 按钮 disabled + badge。
/// </summary>
public bool IsInLocalNodeDbFor(string package)
{
    if (_nodeRepo is null) return false;
    return _nodeRepo.ListDownloadedNodes().Any(n => n.Package == package);
}
```

(若 ctor 已有 final ctor 用 `:` chain 模式,在第一个 concrete ctor 加 default null 即可。)

- [ ] **Step 4: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~CatalogViewModelIsInLocalNodeDbTests" --nologo 2>&1 | tail -5
```

Expected: 2 PASS。

- [ ] **Step 5: 在 `MainViewModel.ShowCatalog` 传 `nodeRepo` 给 `CatalogViewModel`**

打开 `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`,找到 `ShowCatalog` 方法(line ~398),修改构造调用传 `nodeRepo`:

```csharp
private void ShowCatalog()
{
    CurrentSection = MainSection.Catalog;
    if (_catalogViewModel is null)
    {
        var catRepo = new CatalogRepository(_catalogCacheStore);
        var versionRepo = new NodeVersionRepository(_catalogCacheStore);
        var nodeRepo = new NodeRepository(_dbFactory);  // v0.6.15
        _catalogViewModel = new CatalogViewModel(
            catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot,
            nodeRepo: nodeRepo);  // v0.6.15
        _catalogView = new CatalogView { DataContext = _catalogViewModel };
    }
    CurrentView = _catalogView;
}
```

- [ ] **Step 6: 改 `CatalogView.xaml` "下载" 按钮加 IsInLocalNodeDb binding**

打开 `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`,找 "下载" 按钮(已有),改成:

```xml
<!-- 替换 "下载" 按钮(原 v0.6.7.9 加的那行) -->
<Button Content="{Binding IsInLocalNodeDb, Converter={StaticResource DownloadButtonLabelConverter}}"
        IsEnabled="{Binding IsInLocalNodeDb, Converter={StaticResource InverseBoolConverter}}"
        Command="{Binding DataContext.DownloadCommand, RelativeSource={RelativeSource AncestorType=ListBox}}"
        CommandParameter="{Binding}"
        MinWidth="80" Margin="0,0,8,0" />
```

(若 `DownloadButtonLabelConverter` 不存在,inline 用 `Content="{Binding IsInLocalNodeDb, Converter=...}"` + 新 converter 或 hardcode content + visibility binding。)

- [ ] **Step 7: build 验证 XAML**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -5
```

Expected: build succeeded(若 Converter 缺,改硬编码 `Content="下载"` + 整个按钮在 IsInLocalNodeDb=true 时 `Visibility="Collapsed"`,加新 badge 显示 "已下载")。

- [ ] **Step 8: 跑全套,确认无回归**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -3
```

Expected: 1099 PASS / 0 FAIL / 1 SKIP (1097 + 2)。

- [ ] **Step 9: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/Views/CatalogView.xaml tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelIsInLocalNodeDbTests.cs && git commit -m "feat(local-nodes): T5 CatalogViewModel IsInLocalNodeDb badge (v0.6.15)"
```

---

### Task 6: App.xaml.cs DI + MainViewModel 切 view + MainWindow.xaml 侧栏按钮

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI 注册 LocalNodeService + LocalNodeCopyInstaller)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(加 ShowLocalNodesCommand + MainSection.LocalNodes)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs`(加 LocalNodes name)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`(sidebar 加 RadioButton)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/App/AppStartupWiringTests.cs`(扩展 1 test)

**Interfaces:**
- Consumes: `LocalNodeService`, `LocalNodeCopyInstaller`, `ErrorBannerViewModel`
- Produces: `MainViewModel.ShowLocalNodesCommand`, sidebar 6th tab 切到 `LocalNodeListView`

- [ ] **Step 1: 写 App startup DI 测试**

打开或创建 `tests-wpf/ComfyUI.Manager.Tests/App/AppStartupWiringTests.cs`,加新 test:

```csharp
[Fact]
public void LocalNodeService_And_LocalNodeCopyInstaller_DependenciesCanBeConstructed()
{
    // 验证 App.xaml.cs 传的依赖足够构造 LocalNodeService + LocalNodeCopyInstaller。
    // 不跑 App.xaml.cs 整段(那是 App-level),只验 ctor 依赖可注入。
    var dbFactory = new SqliteConnectionFactory(_db.Path);
    var envRepo = new EnvironmentRepository(dbFactory);
    var nodeRepo = new NodeRepository(dbFactory);
    var settings = new Settings { LocalNodeDirectory = Path.Combine(Path.GetTempPath(), "x") };
    var diffService = new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));
    var nodeOps = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings, diffService);

    // 不抛 = DI 依赖足够
    var svc = new LocalNodeService(settings, nodeRepo, envRepo, nodeOps);
    var installer = new LocalNodeCopyInstaller(envRepo, nodeRepo, nodeOps);

    Assert.NotNull(svc);
    Assert.NotNull(installer);
}
```

- [ ] **Step 2: 跑测试,确认 fail**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~AppStartupWiringTests" --nologo 2>&1 | tail -5
```

Expected: build fail(如果 AppStartupWiringTests 不存在,新文件)。

- [ ] **Step 3: 创建 `AppStartupWiringTests.cs`(如不存在)+ 包含上面 test**

- [ ] **Step 4: 在 `App.xaml.cs` DI 注册**

打开 `src-wpf/ComfyUI.Manager/App.xaml.cs`,找 `var nodeOps = new NodeOperations(...)` 那一行(line 177),**之前**插入 LocalNodeService + LocalNodeCopyInstaller 构造(它们依赖 nodeOps):

```csharp
// v0.6.15:本地节点 service + copy installer — 在 nodeOps 之后构造
var localNodeService = new LocalNodeService(
    settings, nodeRepo, envRepo, nodeOps, logger: logger);
var localNodeCopyInstaller = new LocalNodeCopyInstaller(
    envRepo, nodeRepo, nodeOps, logger: logger);
```

(注意 `nodeOps` 在 line 177 才构造,LocalNodeService/CopyInstaller 要放在它**之后**。)

- [ ] **Step 5: 在 `MainViewModel` 加 `ShowLocalNodesCommand` + `MainSection.LocalNodes`**

打开 `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:

(a) 在 `MainSection` enum 加:

```csharp
public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    LocalNodes,  // v0.6.15
    Settings,
    BulkUpdate,
    SystemStatus
}
```

(b) 加 field + property(找 `_catalogViewModel` 附近):

```csharp
private LocalNodeListViewModel? _localNodesViewModel;
private LocalNodeListView? _localNodesView;
```

(c) 加 `ShowLocalNodesCommand` property + ctor 初始化(找 ShowCatalogCommand 附近):

```csharp
public RelayCommand ShowLocalNodesCommand { get; }
// ctor 初始化(在 ShowCatalogCommand 旁边):
ShowLocalNodesCommand = new RelayCommand(_ => ShowLocalNodes());
```

(d) 加 `ShowLocalNodes` method(找 ShowCatalog 下面):

```csharp
private void ShowLocalNodes()
{
    CurrentSection = MainSection.LocalNodes;
    if (_localNodesViewModel is null)
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        var nodeRepo = new NodeRepository(_dbFactory);
        var localNodeSvc = new LocalNodeService(
            _settings, nodeRepo, envRepo, _nodeOps, logger: _logger);
        var installer = new LocalNodeCopyInstaller(
            envRepo, nodeRepo, _nodeOps, logger: _logger);
        _localNodesViewModel = new LocalNodeListViewModel(
            localNodeSvc, installer, envRepo, ErrorBanner);
        _localNodesView = new LocalNodeListView { DataContext = _localNodesViewModel };
    }
    CurrentView = _localNodesView;
}
```

- [ ] **Step 6: 在 `MainSectionNameProvider` 加 LocalNodes 名字**

打开 `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs`,在 switch 加:

```csharp
MainSection.LocalNodes => Get("SectionName_LocalNodes", "本地节点"),
```

- [ ] **Step 7: 在 `MainWindow.xaml` sidebar 加 RadioButton**

打开 `src-wpf/ComfyUI.Manager/MainWindow.xaml`,在 "节点目录" RadioButton 后(line 99-102)插入:

```xml
<RadioButton Content="本地节点" GroupName="SidebarNav"
             Command="{Binding ShowLocalNodesCommand}"
             IsChecked="{Binding CurrentSection, Converter={StaticResource SectionEquality}, ConverterParameter=LocalNodes, Mode=OneWay}"
             Style="{StaticResource SidebarRadioButtonStyle}" />
```

- [ ] **Step 8: build 验证**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -5
```

Expected: build succeeded。

- [ ] **Step 9: 跑测试,确认 PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -3
```

Expected: 1100 PASS / 0 FAIL / 1 SKIP (1099 + 1)。

- [ ] **Step 10: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/App.xaml.cs src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs src-wpf/ComfyUI.Manager/MainWindow.xaml tests-wpf/ComfyUI.Manager.Tests/App/AppStartupWiringTests.cs && git commit -m "feat(local-nodes): T6 DI + MainViewModel + sidebar (v0.6.15)"
```

---

## Self-Review

**1. Spec coverage** — spec §3.1 列 11 组件,本 plan 覆盖 11/11:
- LocalNodeInfo (T1)、LocalNodeService (T1)、LocalNodeCopyInstaller (T2)、CatalogViewModel 改 (T5)、App.xaml.cs 改 (T6)、MainWindow.xaml 改 (T6)、MainViewModel 改 (T6)、LocalNodeListViewModel (T3)、LocalNodeListItem (T3)、EnvPickerDialogViewModel (T3)、EnvPickerDialog (T3)、LocalNodeListView (T4)、NodeRepository 改 (T1)

**2. Placeholder scan** — 0 "TBD" / 0 "TODO" / 0 "implement later"。所有 step 含具体代码或具体文件路径 + 行号。

**3. Type consistency**:
- `LocalNodeInfo` 字段名 `NodeId` / `HeadSha` / `InstallDate` / `HasPhysicalDir` / `IsInDb` / `InstalledEnvIds` / `InstalledEnvNames` — 跨 T1/T2/T3/T4/T5 一致
- `LocalNodeListItem.BadgeText` setter 内部 `UpdateBadge()` 触发,跟 VM 的 `item.UpdateBadge()` 调用一致
- `NodeRepository.DeleteBySourceAndEnvId(id, envId, source)` 签名 T1 用,T1 DeleteAsync 调用签名匹配
- `LocalNodeService.GetLocalNodePath(nodeId)` T3 加的 helper,T3 内部 InstallAsync 用,签名匹配
- `EnvOption(Id, Name)` record,T3 定义,T3 EnvPickerDialogViewModel/T3 LocalNodeListViewModel/T3 EnvPickerDialog 都用
- `ErrorBannerViewModel` 跟既有 `ErrorBanner` property 同名(避免混淆 — `ErrorBanner` 是 `MainViewModel.ErrorBanner` property,不是 VM 类型本身)

**4. Test seam 模式一致**:
- `EnvPickerOverride` (Func),`ConfirmDialogOverride` (Func) — 跟 v0.6.5.19 `MessageBoxOverride` 同 pattern
- `ShowOverride` on EnvPickerDialog (static Func) — 跟 v0.6.14 `CatalogEntryPickerDialog.ShowOverride` 同 pattern
- FakeNodeOps 不需要新 — LocalNodeService 走真 `NodeOperations` 即可,SHA 读非 git 仓库自动返 null

**5. 风险点**:
- T3 `LocalNodeListViewModel.InstallAsync` 用反射拿 Settings 字段太丑 → 改用 `LocalNodeService.GetLocalNodePath` helper(T3 Step 11 已修)
- T5 改 `CatalogViewModel` ctor 新参数 `nodeRepo` 加在末尾 default null → 既有调用方不破坏
- T4 XAML 走 v0.6.9.2 lesson 的 property-element pattern 写 Setter
- STA tests:项目 xUnit 默认 STA? 若否,加 `[Collection("STA")]` attribute + 既有 `STAThreadCollection`(参考 v0.6.5.13 AppLogger test)
