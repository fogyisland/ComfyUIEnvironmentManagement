# 节点管理 + 升级节点 Bottom-Popup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Revert v0.6.15.7 T7 master-detail side panel; add per-env "节点管理" (bottom-popup DataGrid + install/scan) and "升级节点" (bottom-popup outdated list + per-row upgrade) with per-env VM cache preserving state across switches.

**Architecture:** Two new inline status panels (`节点管理` + `升级节点`) in `EnvironmentListView.xaml` follow the existing 4-panel pattern (SurfaceBrush/OutlineBrush border + Visibility binding + ✕ close). Two new VMs (`NodeManagementViewModel`, `UpgradeNodesViewModel`) cached by envId in `EnvironmentListViewModel` via `Dictionary<string, _>`. `NodeOperations.RescanAsync` is the shared scan primitive (replaces v0.6.15.7 T3 MessageBox TODO).

**Tech Stack:** .NET 8, WPF, C# 12, xUnit, Moq (existing), Microsoft.Data.Sqlite (in-memory TestDb), RelayCommand pattern (existing).

**Spec:** `docs/superpowers/specs/2026-08-16-node-management-bottom-popup-design.md`

**Base branch:** main at `d9e1f8c` (spec commit). v0.6.15.7 final HEAD `6ac5853` already includes T1-T9.

## Global Constraints

- 测试套件基线 1206 PASS / 3 FAIL / 1 SKIP(3 FAIL 都是 pre-existing,本 SDD 不引入新 FAIL)
- 不动 v0.6.15.7 已 ship 的 `EnvironmentDetailViewModel` / `EnvironmentDetailView`(本轮 dead code,后续单独删)
- 不动 `CatalogEntryPickerDialog`(复用,本轮只调它)
- 不动 `EnvComponentReportBuilder`(本轮不重构共享扫描逻辑)
- 所有新 `bool`/enum binding 走 `BoolToVisibility`/`NullToVisibility` converter(Theme.xaml 已注册)
- 所有相对时间显示走 `RelativeTimeConverter`(v0.6.15.7 T8)
- Per-env 操作 button 用 `RelayCommand` + `CommandParameter` 传 env,CanExecute 检查 `!IsEnvBusy(env)`
- 中文 UI 文案:"节点管理" / "升级节点" / "扫描" / "安装节点" / "需要升级的节点" / "已装 tag" / "最新版本" / "升级"
- Commit message 格式:`feat(node-mgmt): <msg> (v0.6.15.8 Tn)` / `feat(upgrade-nodes): <msg>` / `feat(env-list): <msg>` / `feat(views): <msg>` / `test(node-mgmt): <msg>`
- 每 Task 末尾 commit 不含 pre-existing dirty WIP(LocalNodeCopyInstaller / RequirementsInstaller / Catalog/LocalNode VM+tests)— `git add <specific paths>` 严格白名单
- v-bump 跳过(用户单独决定),无 release zip

---

## Task 1: NodeOperations.RescanAsync + tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` (add `RescanAsync` + 3 private helpers)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsRescanAsyncTests.cs` (new)

**Interfaces:**
- Consumes: `NodeRepository _nodeRepo` (existing), `EnvironmentRepository _envRepo` (existing), `GitRunner _git` (existing), `_logger?.Info/Warn` (existing v0.6.5.13 pattern)
- Produces: `public virtual async Task<IReadOnlyList<ScannedNode>> RescanAsync(string envId, CancellationToken ct = default)` — scans env's CustomNodesPath, upserts ScannedNode rows, returns list. Pure FS + git describe; no UI.

- [ ] **Step 1: Write the failing test (happy path with subdirs)**

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

public class NodeOperationsRescanAsyncTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeOperations _ops;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
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
        _envRepo.Upsert(new ComfyUI.Manager.Models.Environment
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
        // FakeNodeOperations is a TestDb-aware subclass used elsewhere; use real with FakeGitRunner
        _ops = new NodeOperations(
            new FakeGitRunner(),
            _envRepo,
            new ComfyUI.Manager.Data.EnvironmentRepository(_db.Factory),
            _nodeRepo,
            logger: null);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

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
        // Each row has empty installed_tag (no git in fake) — assert key exists
        foreach (var row in dbRows)
        {
            Assert.True(row.ScanMeta.ContainsKey("installed_tag"));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeOperationsRescanAsyncTests.RescanAsync_HappyPath_CreatesRowsForEachSubdir" --no-restore`
Expected: FAIL with "RescanAsync does not exist" / CS1061

- [ ] **Step 3: Implement RescanAsync**

In `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`, add the following methods (after the existing `UpgradeAsync` or near `InstallAsync`):

```csharp
/// <summary>
/// v0.6.15.8:扫描 env 的 custom_nodes 目录,upsert ScannedNode,返 list。
/// 复用 EnvComponentReportBuilder 同样的扫描策略(每个子目录 = 一个节点),
/// 但只更新 ScannedNode 表,不渲染 HTML。空目录 / 不存在 → 返空 list + WARN log。
/// </summary>
public virtual async Task<IReadOnlyList<ScannedNode>> RescanAsync(
    string envId, CancellationToken ct = default)
{
    _logger?.Info("node-rescan", $"env='{envId}' 开始扫描 custom_nodes");
    var env = _envRepo.Get(envId);
    if (env is null)
    {
        _logger?.Warn("node-rescan", $"env='{envId}' 不存在,跳过");
        return Array.Empty<ScannedNode>();
    }

    var customNodesPath = env.CustomNodesPath;
    if (string.IsNullOrEmpty(customNodesPath) || !Directory.Exists(customNodesPath))
    {
        _logger?.Warn("node-rescan", $"env='{envId}' CustomNodesPath='{customNodesPath}' 不存在或为空");
        return Array.Empty<ScannedNode>();
    }

    var scanned = new List<ScannedNode>();
    foreach (var dir in Directory.EnumerateDirectories(customNodesPath))
    {
        ct.ThrowIfCancellationRequested();
        var nodeId = Path.GetFileName(dir);
        var package = TryReadPackageName(dir) ?? nodeId;
        var sha = await TryReadHeadShaAsync(dir, ct).ConfigureAwait(false);
        var tag = await TryReadInstalledTagAsync(dir, ct).ConfigureAwait(false);
        var node = new ScannedNode
        {
            Id = nodeId,
            EnvId = envId,
            Package = package,
            PackagePath = dir,
            Version = sha ?? "",
            Source = "env",
            ScanMeta = new Dictionary<string, string>
            {
                ["installed_tag"] = tag ?? "",
            },
        };
        _nodeRepo.Upsert(node);
        scanned.Add(node);
    }
    _logger?.Info("node-rescan", $"env='{envId}' 扫描完成,共 {scanned.Count} 个节点");
    return scanned;
}

private static string? TryReadPackageName(string dir)
{
    // 优先 __init__.py 顶部 'Name: x'(PEP 621 风格)
    var init = Path.Combine(dir, "__init__.py");
    if (File.Exists(init))
    {
        foreach (var line in File.ReadAllLines(init))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*Name\s*[:=]\s*([A-Za-z0-9_\-\.]+)");
            if (m.Success) return m.Groups[1].Value;
        }
    }
    // fallback: pyproject.toml [project] name
    var pyp = Path.Combine(dir, "pyproject.toml");
    if (File.Exists(pyp))
    {
        foreach (var line in File.ReadAllLines(pyp))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*name\s*=\s*""?([A-Za-z0-9_\-\.]+)");
            if (m.Success) return m.Groups[1].Value;
        }
    }
    return null;
}

private async Task<string?> TryReadHeadShaAsync(string workdir, CancellationToken ct)
{
    try
    {
        var r = await _git.RunAsync(workdir, new[] { "rev-parse", "HEAD" },
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var sha = r.Stdout.Trim();
        return string.IsNullOrEmpty(sha) ? null : sha.Substring(0, Math.Min(8, sha.Length));
    }
    catch { return null; }
}

private async Task<string?> TryReadInstalledTagAsync(string workdir, CancellationToken ct)
{
    try
    {
        var r = await _git.RunAsync(workdir, new[] { "describe", "--tags", "--abbrev=0" },
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var tag = r.Stdout.Trim();
        return string.IsNullOrEmpty(tag) ? null : tag;
    }
    catch { return null; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeOperationsRescanAsyncTests.RescanAsync_HappyPath_CreatesRowsForEachSubdir"`
Expected: PASS

- [ ] **Step 5: Add 4 more edge-case tests**

Append to the same test file:

```csharp
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
    var result = await _ops.RescanAsync(_envId);
    // Already empty from setup — assert empty
    // Note: this runs AFTER HappyPath, so DB has 3 rows from earlier
    // To make this isolated, re-create empty dir:
    foreach (var d in Directory.EnumerateDirectories(Path.Combine(_tempDir, "custom_nodes")))
    {
        Directory.Delete(d, recursive: true);
    }
    result = await _ops.RescanAsync(_envId);
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
```

- [ ] **Step 6: Run all 5 tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeOperationsRescanAsyncTests" --no-restore`
Expected: 5 PASS / 0 FAIL

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/NodeOperations.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsRescanAsyncTests.cs
git commit -m "feat(services): NodeOperations.RescanAsync + 5 tests (v0.6.15.8 T1)

Scans env CustomNodesPath, upserts ScannedNode rows with sha+tag ScanMeta.
Returns IReadOnlyList<ScannedNode>. Empty/missing dir → empty list + WARN log.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 2: NodeManagementViewModel + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/NodeManagementViewModel.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeManagementViewModelTests.cs` (new)

**Interfaces:**
- Consumes: `NodeRepository`, `NodeOperations.RescanAsync(envId)`, `ErrorBannerViewModel`, `CatalogEntryPickerDialog.Show(...)` (existing), `Views.ConfirmDialog.Show(...)` (existing)
- Produces:
  - `ObservableCollection<ScannedNode> Nodes { get; }`
  - `RelayCommand ScanCommand { get; }`
  - `RelayCommand InstallCommand { get; }`
  - `RelayCommand DeleteCommand { get; }` (parameter: ScannedNode)
  - `RelayCommand CloseCommand { get; }`
  - `bool Busy { get; set; }`
  - `string EnvName { get; }`
  - `Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }` (test seam)
  - `event Action? CloseRequested`
  - `public Task DeleteAsync(ScannedNode? node)`
  - `public Func<...>? OpenInstallPickerOverride { get; set; }` (test seam)

- [ ] **Step 1: Write the failing test (constructor auto-rescan)**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class NodeManagementViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _nodeRepo;
    private readonly FakeNodeOperations _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId = "env-1";

    public NodeManagementViewModelTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new FakeNodeOperations();
        _errorBanner = new ErrorBannerViewModel();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Constructor_TriggersScanAsync_PopulatesNodes()
    {
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
            new() { Id = "n2", EnvId = _envId, Package = "pkg-b", Source = "env" },
        };
        _nodeOps.NodeRepo = _nodeRepo;
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
        // Pump message loop briefly so fire-and-forget ScanAsync completes
        SpinWait.SpinUntil(() => vm.Nodes.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal("test-env", vm.EnvName);
        Assert.True(_nodeOps.RescanCalled);
    }
}
```

Add a `FakeNodeOperations` helper to `tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeNodeOperations.cs` (new file):

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Tests.Fakes;

public class FakeNodeOperations : NodeOperations
{
    public IReadOnlyList<ScannedNode>? ScanResult { get; set; }
    public NodeRepository? NodeRepo { get; set; }
    public bool RescanCalled { get; private set; }

    public FakeNodeOperations() : base(
        new FakeGitRunner(),
        new ComfyUI.Manager.Data.EnvironmentRepository(new TestDb().Factory),
        new ComfyUI.Manager.Data.EnvironmentRepository(new TestDb().Factory),
        new NodeRepository(new TestDb().Factory),
        logger: null)
    { }

    public override Task<IReadOnlyList<ScannedNode>> RescanAsync(
        string envId, CancellationToken ct = default)
    {
        RescanCalled = true;
        if (ScanResult is null) return Task.FromResult<IReadOnlyList<ScannedNode>>(new List<ScannedNode>());
        // Upsert into NodeRepo so ListByEnv works
        if (NodeRepo is not null)
        {
            foreach (var n in ScanResult) NodeRepo.Upsert(n);
        }
        return Task.FromResult(ScanResult);
    }
}
```

Note: this FakeNodeOperations creates throwaway DB instances in base ctor (unused for the override). Acceptable since `RescanAsync` override doesn't touch them.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeManagementViewModelTests.Constructor_TriggersScanAsync_PopulatesNodes" --no-restore`
Expected: FAIL with "NodeManagementViewModel does not exist"

- [ ] **Step 3: Implement NodeManagementViewModel**

Create `src-wpf/ComfyUI.Manager/ViewModels/NodeManagementViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15.8:per-env 节点管理 VM。构造时自动 rescan,Nodes 填充已装节点。
/// ScanCommand 重扫,InstallCommand 开 CatalogEntryPicker,DeleteCommand 删行。
/// CloseCommand fire CloseRequested event(EnvListVM 接 → NodeManagement = null 隐 panel)。
/// </summary>
public class NodeManagementViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand ScanCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public string EnvName { get; }
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    /// <summary>test seam:代替真实开 CatalogEntryPickerDialog。返 true 表示装成功。</summary>
    public Func<bool>? OpenInstallPickerOverride { get; set; }

    public event Action? CloseRequested;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                InstallCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public NodeManagementViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        ErrorBannerViewModel errorBanner, string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _errorBanner = errorBanner;
        _envId = envId;
        EnvName = envName;
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !Busy);
        InstallCommand = new RelayCommand(_ => OpenInstallPicker(), _ => !Busy);
        DeleteCommand = new RelayCommand(
            async p => await DeleteAsync(p as ScannedNode),
            p => p is ScannedNode && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        ToggleCommand = new RelayCommand(_ => { /* TODO 占位 */ }, _ => false);
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        Busy = true;
        try
        {
            await _nodeOps.RescanAsync(_envId).ConfigureAwait(false);
            // Marshal back to UI thread for ObservableCollection mutation
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Nodes.Clear();
                foreach (var n in _nodeRepo.ListByEnv(_envId)) Nodes.Add(n);
            });
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenInstallPicker()
    {
        bool? installed;
        if (OpenInstallPickerOverride is not null)
        {
            installed = OpenInstallPickerOverride();
        }
        else
        {
            // Production: fire-and-forget call to CatalogEntryPickerDialog.Show
            // (the dialog owns install + close, then VM rescans)
            installed = true;
            CatalogEntryPickerDialog.Show(
                envRepo: null!,  // picker no longer needs env repo — it builds from catalog+nodeRepo
                nodeOps: _nodeOps,
                catalogRepo: null!,
                nodeRepo: _nodeRepo,
                versionRepo: null!,
                logger: null,
                envId: _envId,
                onInstallSuccess: null,
                onClosed: () => _ = ScanAsync());
        }
        if (installed == true) _ = ScanAsync();
    }

    public async Task DeleteAsync(ScannedNode? node)
    {
        if (node is null) return;
        var ok = ConfirmDialogOverride is not null
            ? ConfirmDialogOverride($"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。", "确认删除", "取消")
            : ConfirmDialog.Show($"确认从 env 删除节点 {node.Package}?目录会从 custom_nodes 移除。");
        if (!ok) return;

        Busy = true;
        try
        {
            var r = await _nodeOps.UninstallAsync(_envId, node.Package, CancellationToken.None).ConfigureAwait(false);
            if (!r.Success)
            {
                _errorBanner.Add("env-detail-delete", $"删除失败:{r.Reason}", ErrorSeverity.Error);
                return;
            }
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Nodes.Remove(node));
        }
        finally
        {
            Busy = false;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeManagementViewModelTests.Constructor_TriggersScanAsync_PopulatesNodes"`
Expected: PASS

- [ ] **Step 5: Add 6 more tests**

Append to test file:

```csharp
[Fact]
public async Task ScanCommand_AfterBusyFalse_RerunsScan()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
    };
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "n2", EnvId = _envId, Package = "pkg-b", Source = "env" },
    };
    _nodeOps.RescanCalled = false;
    vm.ScanCommand.Execute(null);
    SpinWait.SpinUntil(() => vm.Nodes.Count == 1 && vm.Nodes[0].Id == "n2", TimeSpan.FromSeconds(2));
    Assert.True(_nodeOps.RescanCalled);
}

[Fact]
public void InstallCommand_OverrideTrue_TriggersRescan()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>();
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

    var called = false;
    vm.OpenInstallPickerOverride = () => { called = true; return true; };
    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "newpkg", EnvId = _envId, Package = "newpkg", Source = "env" },
    };
    _nodeOps.RescanCalled = false;
    vm.InstallCommand.Execute(null);
    Assert.True(called);
    SpinWait.SpinUntil(() => _nodeOps.RescanCalled, TimeSpan.FromSeconds(2));
}

[Fact]
public async Task DeleteCommand_ConfirmsAndDeletes_RemovesFromNodes()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
    };
    _nodeOps.UninstallResult = NodeOperationResult.Ok("v1.0");
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

    vm.ConfirmDialogOverride = (_, _, _) => true;
    await vm.DeleteAsync(vm.Nodes[0]);
    Assert.Empty(vm.Nodes);
    Assert.True(_nodeOps.UninstallCalled);
}

[Fact]
public async Task DeleteCommand_CancelledByUser_LeavesNodesIntact()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
    };
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

    vm.ConfirmDialogOverride = (_, _, _) => false;
    await vm.DeleteAsync(vm.Nodes[0]);
    Assert.Single(vm.Nodes);
    Assert.False(_nodeOps.UninstallCalled);
}

[Fact]
public async Task DeleteCommand_FailedResult_LeavesNodesIntact_AddsErrorBanner()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>
    {
        new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
    };
    _nodeOps.UninstallResult = NodeOperationResult.Fail("目录锁住");
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

    vm.ConfirmDialogOverride = (_, _, _) => true;
    await vm.DeleteAsync(vm.Nodes[0]);
    Assert.Single(vm.Nodes);
    Assert.True(_errorBanner.HasErrors);
}

[Fact]
public void CloseCommand_FiresCloseRequested_Event()
{
    _nodeOps.NodeRepo = _nodeRepo;
    _nodeOps.ScanResult = new List<ScannedNode>();
    var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

    var fired = false;
    vm.CloseRequested += () => fired = true;
    vm.CloseCommand.Execute(null);
    Assert.True(fired);
}
```

Also add 2 fields + override to `FakeNodeOperations`:

```csharp
public NodeOperationResult UninstallResult { get; set; } = NodeOperationResult.Ok("v0");
public bool UninstallCalled { get; private set; }

public override Task<NodeOperationResult> UninstallAsync(
    string envId, string packageName, CancellationToken ct = default)
{
    UninstallCalled = true;
    return Task.FromResult(UninstallResult);
}
```

- [ ] **Step 6: Run all 7 tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeManagementViewModelTests" --no-restore`
Expected: 7 PASS / 0 FAIL

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/NodeManagementViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeManagementViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeNodeOperations.cs
git commit -m "feat(node-mgmt): NodeManagementViewModel + 7 tests (v0.6.15.8 T2)

Per-env VM: Nodes ObservableCollection, Scan/Install/Delete/Close commands.
Auto-rescan on construct via NodeOperations.RescanAsync (T1).
ConfirmDialogOverride + OpenInstallPickerOverride test seams.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 3: UpgradeNodesViewModel + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/UpgradeNodesViewModelTests.cs` (new)

**Interfaces:**
- Consumes: `NodeRepository.ListByEnv(envId)`, `NodeOperations.RescanAsync(envId)`, `NodeOperations.UpgradeAsync(envId, package, ct)`, `CatalogRepository.Search("", int)` (returns `IReadOnlyList<CatalogEntry>`)
- Produces:
  - `ObservableCollection<ScannedNode> OutdatedNodes { get; }`
  - `RelayCommand UpgradeCommand { get; }` (parameter: ScannedNode)
  - `RelayCommand CloseCommand { get; }`
  - `bool Busy { get; set; }`
  - `string EnvName { get; }`
  - `string? LatestVersion(ScannedNode node)` — helper for XAML binding
  - `event Action? CloseRequested`

- [ ] **Step 1: Write the failing test (filter outdated only)**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class UpgradeNodesViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _nodeRepo;
    private readonly FakeNodeOperations _nodeOps;
    private readonly FakeCatalogRepository _catalogRepo;
    private readonly string _envId = "env-1";

    public UpgradeNodesViewModelTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new FakeNodeOperations { NodeRepo = _nodeRepo };
        _catalogRepo = new FakeCatalogRepository();
    }

    public void Dispose() => _db.Dispose();

    private void Seed(string id, string pkg, string? tag)
    {
        var n = new ScannedNode
        {
            Id = id, EnvId = _envId, Package = pkg, Source = "env",
            ScanMeta = new Dictionary<string, string>(),
        };
        if (tag is not null) n.ScanMeta["installed_tag"] = tag;
        _nodeRepo.Upsert(n);
    }

    [Fact]
    public void Constructor_LoadsOutdatedOnly()
    {
        Seed("o1", "outdated-pkg", "v1.0");
        Seed("c1", "current-pkg", "v1.2");
        Seed("u1", "untagged-pkg", null);
        _catalogRepo.Entries = new List<CatalogEntry>
        {
            new() { Package = "outdated-pkg", LatestVersion = "v1.2" },
            new() { Package = "current-pkg", LatestVersion = "v1.2" },
            new() { Package = "untagged-pkg", LatestVersion = "v1.0" },
        };
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId).ToList();

        var vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps, _catalogRepo, envId: _envId, envName: "test-env");
        SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count >= 1, TimeSpan.FromSeconds(2));

        Assert.Single(vm.OutdatedNodes);
        Assert.Equal("outdated-pkg", vm.OutdatedNodes[0].Package);
        Assert.Equal("v1.2", vm.LatestVersion(vm.OutdatedNodes[0]));
    }
}
```

Add a `FakeCatalogRepository` helper to `tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeCatalogRepository.cs` (new file):

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Tests.Fakes;

public class FakeCatalogRepository
{
    public List<CatalogEntry> Entries { get; set; } = new();

    public IEnumerable<CatalogEntry> Search(string q, int limit)
        => Entries.Where(e => string.IsNullOrEmpty(q) || e.Package.Contains(q, System.StringComparison.OrdinalIgnoreCase)).Take(limit);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~UpgradeNodesViewModelTests.Constructor_LoadsOutdatedOnly" --no-restore`
Expected: FAIL with "UpgradeNodesViewModel does not exist"

- [ ] **Step 3: Implement UpgradeNodesViewModel**

Create `src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15.8:per-env 升级节点 VM。拉 catalog + Rescan + 过滤 outdated。
/// 节点 outdated = ScanMeta["installed_tag"] 非空 + 与 catalog LatestVersion 不一致 + 都有值。
/// UpgradeCommand per-row 触发 NodeOperations.UpgradeAsync,完成后 LoadAsync 重过滤。
/// </summary>
public class UpgradeNodesViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly FakeCatalogSource _catalog;
    private readonly string _envId;
    private Dictionary<string, string> _latestByPackage = new();

    public ObservableCollection<ScannedNode> OutdatedNodes { get; } = new();
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand CloseCommand { get; }
    public string EnvName { get; }
    public event Action? CloseRequested;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                UpgradeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public UpgradeNodesViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        FakeCatalogSource catalog, string envId, string envName)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _catalog = catalog;
        _envId = envId;
        EnvName = envName;
        UpgradeCommand = new RelayCommand(
            async p => await UpgradeAsync(p as ScannedNode),
            p => p is ScannedNode && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = LoadAsync();
    }

    public string? LatestVersion(ScannedNode node)
        => _latestByPackage.TryGetValue(node.Package, out var v) ? v : null;

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            await _nodeOps.RescanAsync(_envId).ConfigureAwait(false);
            var scanned = _nodeRepo.ListByEnv(_envId).ToList();
            var catalogEntries = _catalog.Search("", 5000).ToList();

            _latestByPackage = catalogEntries
                .Where(e => !string.IsNullOrEmpty(e.LatestVersion))
                .GroupBy(e => e.Package)
                .ToDictionary(g => g.Key, g => g.First().LatestVersion);

            var outdated = scanned.Where(s =>
                s.ScanMeta.TryGetValue("installed_tag", out var tag)
                && !string.IsNullOrEmpty(tag)
                && _latestByPackage.TryGetValue(s.Package, out var latest)
                && !string.IsNullOrEmpty(latest)
                && tag != latest).ToList();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                OutdatedNodes.Clear();
                foreach (var n in outdated) OutdatedNodes.Add(n);
            });
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UpgradeAsync(ScannedNode? node)
    {
        if (node is null) return;
        Busy = true;
        try
        {
            await _nodeOps.UpgradeAsync(_envId, node.Package, CancellationToken.None).ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false); // reload filter, node may now be in-sync
        }
        finally
        {
            Busy = false;
        }
    }
}
```

Also add a `FakeCatalogSource` adapter in the same test file or in `Fakes/`:

```csharp
// In Fakes/FakeCatalogSource.cs (new)
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Tests.Fakes;

/// <summary>Lightweight catalog source — VM-friendly; wraps an entry list with Search().</summary>
public class FakeCatalogSource
{
    public List<CatalogEntry> Entries { get; set; } = new();

    public IEnumerable<CatalogEntry> Search(string query, int limit)
        => Entries
            .Where(e => string.IsNullOrEmpty(query)
                || e.Package.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            .Take(limit);
}
```

Update the VM ctor to take `FakeCatalogSource` (rename parameter from `FakeCatalogRepository`). Actually the VM was designed to take a catalog-like interface — to make it testable, define a minimal interface in the VM file:

```csharp
public interface ICatalogSource
{
    IEnumerable<CatalogEntry> Search(string query, int limit);
}
```

Then in `UpgradeNodesViewModel` ctor take `ICatalogSource catalog`. `FakeCatalogSource` implements it. Production wiring passes `new CatalogRepositoryAdapter(realRepo)` or similar — but for now, no production wiring yet (T5 wires real one).

Actually simpler: just take `Func<string, int, IEnumerable<CatalogEntry>>` as a delegate in ctor. Avoids needing to define interface + adapter. Let me revise the ctor:

```csharp
public UpgradeNodesViewModel(
    NodeRepository repo, NodeOperations nodeOps,
    Func<string, int, IEnumerable<CatalogEntry>> catalogSearch,
    string envId, string envName)
{
    _nodeRepo = repo;
    _nodeOps = nodeOps;
    _catalogSearch = catalogSearch;
    _envId = envId;
    EnvName = envName;
    // ... rest same
}
```

In `LoadAsync`, replace `_catalog.Search(...)` with `_catalogSearch("", 5000)`.

In tests, pass `((q, n) => _catalogRepo.Entries.Where(...))` as the delegate. Cleaner — no extra interface.

Update Task 3 Step 3 implementation to use `Func<string, int, IEnumerable<CatalogEntry>> catalogSearch` delegate.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~UpgradeNodesViewModelTests.Constructor_LoadsOutdatedOnly"`
Expected: PASS

- [ ] **Step 5: Add 4 more tests**

Append to test file:

```csharp
[Fact]
public async Task UpgradeCommand_Successful_ReloadsNodeLeavesList()
{
    Seed("o1", "outdated-pkg", "v1.0");
    _catalogRepo.Entries = new List<CatalogEntry>
    {
        new() { Package = "outdated-pkg", LatestVersion = "v1.2" },
    };
    _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId).ToList();
    _nodeOps.UpgradeResult = NodeOperationResult.Ok("v1.2");

    var vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps,
        catalogSearch: (q, n) => _catalogRepo.Entries,
        envId: _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 1, TimeSpan.FromSeconds(2));

    // After upgrade, simulate that tag now matches latest — re-seed
    Seed("o1", "outdated-pkg", "v1.2");
    _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId).ToList();
    await Task.Yield();
    vm.UpgradeCommand.Execute(vm.OutdatedNodes[0]);
    SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 0, TimeSpan.FromSeconds(3));
    Assert.Empty(vm.OutdatedNodes);
    Assert.True(_nodeOps.UpgradeCalled);
}

[Fact]
public async Task UpgradeCommand_Failed_KeepsNodeInList()
{
    Seed("o1", "outdated-pkg", "v1.0");
    _catalogRepo.Entries = new List<CatalogEntry>
    {
        new() { Package = "outdated-pkg", LatestVersion = "v1.2" },
    };
    _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId).ToList();
    _nodeOps.UpgradeResult = NodeOperationResult.Fail("git pull 失败");

    var vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps,
        catalogSearch: (q, n) => _catalogRepo.Entries,
        envId: _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 1, TimeSpan.FromSeconds(2));

    vm.UpgradeCommand.Execute(vm.OutdatedNodes[0]);
    SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));
    Assert.Single(vm.OutdatedNodes);
    Assert.True(_nodeOps.UpgradeCalled);
}

[Fact]
public void CloseCommand_FiresCloseRequested_Event()
{
    _catalogRepo.Entries = new List<CatalogEntry>();
    _nodeOps.ScanResult = new List<ScannedNode>();
    var vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps,
        catalogSearch: (q, n) => _catalogRepo.Entries,
        envId: _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

    var fired = false;
    vm.CloseRequested += () => fired = true;
    vm.CloseCommand.Execute(null);
    Assert.True(fired);
}

[Fact]
public void Constructor_CatalogMissingEntry_NodeExcludedFromOutdated()
{
    Seed("x1", "missing-from-catalog", "v1.0");
    _catalogRepo.Entries = new List<CatalogEntry>();
    _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId).ToList();

    var vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps,
        catalogSearch: (q, n) => _catalogRepo.Entries,
        envId: _envId, envName: "test-env");
    SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

    Assert.Empty(vm.OutdatedNodes);
}
```

Add 2 fields + override to `FakeNodeOperations`:

```csharp
public NodeOperationResult UpgradeResult { get; set; } = NodeOperationResult.Ok("v0");
public bool UpgradeCalled { get; private set; }

public override Task<NodeOperationResult> UpgradeAsync(
    string envId, string packageName, CancellationToken ct = default)
{
    UpgradeCalled = true;
    return Task.FromResult(UpgradeResult);
}
```

- [ ] **Step 6: Run all 5 tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~UpgradeNodesViewModelTests" --no-restore`
Expected: 5 PASS / 0 FAIL

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/UpgradeNodesViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeNodeOperations.cs \
        tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeCatalogSource.cs
git commit -m "feat(upgrade-nodes): UpgradeNodesViewModel + 5 tests (v0.6.15.8 T3)

Per-env VM: filters outdated (ScanMeta[installed_tag] != catalog.LatestVersion,
both non-empty). Per-row UpgradeCommand → NodeOperations.UpgradeAsync, reload.
Catalog via Func delegate (no interface needed for production wiring T5).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 4: NodeManagementView + UpgradeNodesView XAML/code-behind

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml.cs`
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (add `xmlns:v` already present; confirm)

**Interfaces:**
- Consumes: `NodeManagementViewModel` (T2), `UpgradeNodesViewModel` (T3), `RelativeTimeConverter` (Theme.xaml existing), `BoolToVisibilityConverter` (Theme.xaml existing), `NullToVisibilityConverter` (Theme.xaml existing)
- Produces: 2 new UserControls binding to the 2 VMs via DataTemplate (wired in T6)

- [ ] **Step 1: Create NodeManagementView.xaml**

`src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml`:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.NodeManagementView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DataGrid ItemsSource="{Binding Nodes}"
              SelectedItem="{Binding SelectedNode}"
              AutoGenerateColumns="False" IsReadOnly="True"
              MinHeight="300">
        <DataGrid.Columns>
            <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
            <DataGridTextColumn Header="版本" Binding="{Binding Version}" Width="100" />
            <DataGridTextColumn Header="作者" Binding="{Binding Author}" Width="*" />
            <DataGridTextColumn Header="状态" Binding="{Binding Status}" Width="80" />
            <DataGridCheckBoxColumn Header="锁" Binding="{Binding Locked}" Width="40" />
            <DataGridTextColumn Header="仓库 URL" Binding="{Binding RepositoryUrl}" Width="200" />
            <DataGridTextColumn Header="加载时间" Width="100"
                                Binding="{Binding LastScannedAt, Converter={StaticResource RelativeTime}}" />
            <DataGridTextColumn Header="版本 tag" Binding="{Binding ScanMeta[installed_tag]}" Width="100" />
            <DataGridTemplateColumn Header="加载错误" Width="100">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <Border Background="#D32F2F" CornerRadius="4" Padding="4,2"
                                HorizontalAlignment="Left"
                                ToolTip="{Binding ScanMeta[load_error]}"
                                Visibility="{Binding ScanMeta[load_error],
                                             Converter={StaticResource NullToVisibility},
                                             FallbackValue=Collapsed}">
                            <TextBlock Text="加载失败" Foreground="White" FontSize="11" />
                        </Border>
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
            <DataGridTextColumn Header="来源" Binding="{Binding Source}" Width="70" />
            <DataGridTemplateColumn Header="操作" Width="160">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <StackPanel Orientation="Horizontal">
                            <Button Content="删除"
                                    Style="{StaticResource DangerButton}"
                                    Command="{Binding DataContext.DeleteCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                        </StackPanel>
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
        </DataGrid.Columns>
    </DataGrid>
</UserControl>
```

- [ ] **Step 2: Create NodeManagementView.xaml.cs**

`src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class NodeManagementView : UserControl
{
    public NodeManagementView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Create UpgradeNodesView.xaml**

`src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml`:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.UpgradeNodesView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels">
    <DataGrid ItemsSource="{Binding OutdatedNodes}"
              AutoGenerateColumns="False" IsReadOnly="True"
              MinHeight="200">
        <DataGrid.Columns>
            <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
            <DataGridTextColumn Header="已装 tag" Binding="{Binding ScanMeta[installed_tag]}" Width="120" />
            <DataGridTemplateColumn Header="最新版本" Width="120">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding DataContext.LatestVersion,
                                          RelativeSource={RelativeSource AncestorType=UserControl},
                                          Converter={StaticResource RelativeTime},
                                          FallbackValue='-'}" />
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
            <DataGridTemplateColumn Header="操作" Width="100">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <Button Content="升级"
                                Command="{Binding DataContext.UpgradeCommand,
                                          RelativeSource={RelativeSource AncestorType=UserControl}}"
                                CommandParameter="{Binding}" />
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
        </DataGrid.Columns>
    </DataGrid>
</UserControl>
```

Note: `LatestVersion` is a method call binding in WPF (`{Binding LatestVersion}` does NOT call methods). To call method use a converter or expose a property. Simplest: bind to `{Binding LatestVersion}` and define `LatestVersion` as a `Func<ScannedNode, string?>` wrapper property. But WPF doesn't bind to methods either.

Workaround: change XAML to bind via DataContext (DataGridCell's parent VM) and use `RelativeSource`:

```xml
<TextBlock Text="{Binding DataContext.LatestVersion,
                  RelativeSource={RelativeSource AncestorType=UserControl},
                  Converter={x:Static views:LatestVersionConverter.Instance},
                  ConverterParameter={Binding}}" />
```

Or simpler: bind to `LatestVersions` Dictionary lookup via `MultiBinding`. Or use a property on `ScannedNode` — but that's invasive.

Cleanest: define a `LatestVersionConverter : IValueConverter` that takes `ScannedNode` as value, looks up `_latestByPackage` via static or VM-static lookup. Or add a method-to-property wrapper.

Actually simplest: bind to `{Binding LatestVersionForThis}` where `ScannedNode.LatestVersionForThis` is set by VM during filter step. But ScannedNode is shared model — modifying it is messy.

Pragmatic solution: pre-compute outdated + their `LatestVersion` as a wrapper class. Skip the method-call issue.

Update `UpgradeNodesViewModel` to expose:

```csharp
public ObservableCollection<UpgradeCandidate> OutdatedNodes { get; } = new();

public class UpgradeCandidate
{
    public ScannedNode Node { get; init; } = null!;
    public string LatestVersion { get; init; } = "";
}
```

Then XAML binds `OutdatedNodes[*].Node.Package`, etc. Cleaner. Update Task 3 to use this wrapper.

But Task 3 already shipped with `ObservableCollection<ScannedNode>`. To minimize change, add a parallel observable:

Actually simplest fix to Task 3: change `ObservableCollection<ScannedNode>` to `ObservableCollection<UpgradeCandidate>` and `UpgradeCommand` parameter to `UpgradeCandidate`. Then in `UpgradeAsync`, look up `Node.Package` from the candidate.

Updating Task 3 implementation:
- Add `public class UpgradeCandidate { ScannedNode Node; string LatestVersion; }` in same file
- Change `OutdatedNodes` type to `ObservableCollection<UpgradeCandidate>`
- In `LoadAsync`, build `UpgradeCandidate` instances with both fields
- In `UpgradeAsync`, use `candidate.Node.Package`
- In `LatestVersion(ScannedNode)` helper — delete it; replace with the wrapper's property

Update Task 3 test to use new type. The 4 additional tests need adjustment:
- `Assert.Equal("outdated-pkg", vm.OutdatedNodes[0].Node.Package)` 
- `Assert.Equal("v1.2", vm.OutdatedNodes[0].LatestVersion)`
- `vm.UpgradeCommand.Execute(vm.OutdatedNodes[0])` — still works (parameter type changed)

Update Task 4 XAML to bind to wrapper:

```xml
<DataGrid ItemsSource="{Binding OutdatedNodes}" ...>
    <DataGrid.Columns>
        <DataGridTextColumn Header="包名" Binding="{Binding Node.Package}" Width="*" />
        <DataGridTextColumn Header="已装 tag" Binding="{Binding Node.ScanMeta[installed_tag]}" Width="120" />
        <DataGridTextColumn Header="最新版本" Binding="{Binding LatestVersion}" Width="120" />
        <DataGridTemplateColumn Header="操作" Width="100">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <Button Content="升级"
                            Command="{Binding DataContext.UpgradeCommand,
                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                            CommandParameter="{Binding}" />
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
    </DataGrid.Columns>
</DataGrid>
```

Update Task 3 implementation in this Task 4 step BEFORE moving on:

```csharp
// In UpgradeNodesViewModel.cs:
public class UpgradeCandidate
{
    public ScannedNode Node { get; init; } = null!;
    public string LatestVersion { get; init; } = "";
}

public ObservableCollection<UpgradeCandidate> OutdatedNodes { get; } = new();

// Replace _latestByPackage with inline latest version capture per candidate
// In LoadAsync:
var outdated = scanned
    .Select(s => new { Node = s, Tag = s.ScanMeta.TryGetValue("installed_tag", out var t) ? t : null })
    .Where(x => !string.IsNullOrEmpty(x.Tag))
    .Select(x => new {
        x.Node,
        x.Tag,
        Latest = _latestByPackage.TryGetValue(x.Node.Package, out var l) ? l : null
    })
    .Where(x => !string.IsNullOrEmpty(x.Latest) && x.Tag != x.Latest)
    .Select(x => new UpgradeCandidate { Node = x.Node, LatestVersion = x.Latest! })
    .ToList();

await Dispatcher.InvokeAsync(() => {
    OutdatedNodes.Clear();
    foreach (var c in outdated) OutdatedNodes.Add(c);
});

// Delete LatestVersion(node) helper — no longer needed.

// In UpgradeAsync:
private async Task UpgradeAsync(UpgradeCandidate? candidate)
{
    if (candidate is null) return;
    Busy = true;
    try
    {
        await _nodeOps.UpgradeAsync(_envId, candidate.Node.Package, CancellationToken.None);
        await LoadAsync();
    }
    finally { Busy = false; }
}
```

Update Task 3 test assertions:

```csharp
Assert.Single(vm.OutdatedNodes);
Assert.Equal("outdated-pkg", vm.OutdatedNodes[0].Node.Package);
Assert.Equal("v1.2", vm.OutdatedNodes[0].LatestVersion);
```

Update UpgradeCommand signature in `RelayCommand ctor`:

```csharp
UpgradeCommand = new RelayCommand(
    async p => await UpgradeAsync(p as UpgradeCandidate),
    p => p is UpgradeCandidate && !Busy);
```

- [ ] **Step 4: Update Task 3 VM + tests with wrapper class**

Edit `src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs` per above wrapper-class refactor.

Edit `tests-wpf/ComfyUI.Manager.Tests/ViewModels/UpgradeNodesViewModelTests.cs` — change `vm.OutdatedNodes[0].Package` → `vm.OutdatedNodes[0].Node.Package` (2 places in `Constructor_LoadsOutdatedOnly` test).

- [ ] **Step 5: Re-run Task 3 tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~UpgradeNodesViewModelTests"`
Expected: 5 PASS / 0 FAIL (after wrapper refactor)

- [ ] **Step 6: Create UpgradeNodesView.xaml.cs**

`src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class UpgradeNodesView : UserControl
{
    public UpgradeNodesView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: Build to verify XAML compiles**

Run: `dotnet build src-wpf/ComfyUI.Manager`
Expected: 0 errors (warnings OK)

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml \
        src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml.cs \
        src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml \
        src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/UpgradeNodesViewModelTests.cs
git commit -m "feat(views): NodeManagementView + UpgradeNodesView XAML + wrapper class (v0.6.15.8 T4)

NodeManagementView: 9-column DataGrid (reuses v0.6.15.7 T4 pattern).
UpgradeNodesView: 4-column DataGrid (Package/已装tag/最新版本/操作).
UpgradeNodesViewModel uses UpgradeCandidate wrapper (Node + LatestVersion)
so XAML can bind LatestVersion as a property, not a method call.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 5: EnvironmentListViewModel refactor (revert T7 + add cache + commands + tests)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (delete T7 fields/methods + add cache/properties/commands)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelNodeManagementTests.cs` (new)

**Interfaces:**
- Consumes: `NodeRepository _nodeRepo` (existing), `NodeOperations _nodeOps` (existing), `CatalogRepository _catalogRepo` (existing v0.6.14 wiring), `NodeVersionRepository _versionRepo` (existing v0.6.14 wiring), `ErrorBannerViewModel _errorBanner` (existing)
- Produces:
  - `NodeManagementViewModel? NodeManagement { get; private set; }` + `bool IsNodeManagementVisible`
  - `UpgradeNodesViewModel? UpgradeNodes { get; private set; }` + `bool IsUpgradeNodesVisible`
  - `RelayCommand OpenNodeManagementCommand`
  - `RelayCommand OpenUpgradeNodesCommand`
  - `RelayCommand CloseNodeManagementCommand`
  - `RelayCommand CloseUpgradeNodesCommand`
  - private `Dictionary<string, NodeManagementViewModel> _nodeMgmtCache`
  - private `Dictionary<string, UpgradeNodesViewModel> _upgradeCache`

- [ ] **Step 1: Write failing tests (cache hit/miss/switch)**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelNodeManagementTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelNodeManagementTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _nodeRepo;
    private readonly FakeNodeOperations _nodeOps;
    private readonly FakeCatalogRepository _catalogRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly EnvironmentListViewModel _vm;

    public EnvironmentListViewModelNodeManagementTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeOps = new FakeNodeOperations { NodeRepo = _nodeRepo };
        _catalogRepo = new FakeCatalogRepository();
        SeedEnv("env-a", "env-A");
        SeedEnv("env-b", "env-B");
        _vm = new EnvironmentListViewModel(
            _envRepo,
            catalogRepo: null,
            nodeOps: _nodeOps,
            versionRepo: null,
            gitRunner: new FakeGitRunner(),
            logger: null,
            nodeRepo: _nodeRepo,
            pythonProvider: null,
            componentReportBuilderOverride: null,
            baseEnvInstallerOverride: null,
            requirementsInstallerOverride: null);
    }

    private void SeedEnv(string id, string name)
    {
        _envRepo.Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = id, Name = name, RootPath = $"/x/{id}", ComfyuiLayout = "standalone",
        });
    }

    public void Dispose() => _db.Dispose();

    private ComfyUI.Manager.Models.Environment GetEnv(string id)
        => _envRepo.Get(id) ?? throw new InvalidOperationException("missing env");

    [Fact]
    public void OpenNodeManagement_NewEnv_CreatesVM_ShowsPanel()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);
        Assert.NotNull(_vm.NodeManagement);
        Assert.True(_vm.IsNodeManagementVisible);
        Assert.Equal("env-A", _vm.NodeManagement!.EnvName);
    }

    [Fact]
    public void OpenNodeManagement_SameEnvTwice_ReusesCachedVM()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);
        var first = _vm.NodeManagement;
        _vm.OpenNodeManagementCommand.Execute(env);
        Assert.Same(first, _vm.NodeManagement);
    }

    [Fact]
    public void OpenNodeManagement_DifferentEnv_SwitchesPanelVM()
    {
        _vm.OpenNodeManagementCommand.Execute(GetEnv("env-a"));
        var first = _vm.NodeManagement;
        _vm.OpenNodeManagementCommand.Execute(GetEnv("env-b"));
        Assert.NotSame(first, _vm.NodeManagement);
        Assert.Equal("env-B", _vm.NodeManagement!.EnvName);
    }

    [Fact]
    public void CloseNodeManagementCommand_HidesPanel_PreservesCache()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);
        var cached = _vm.NodeManagement;
        _vm.CloseNodeManagementCommand.Execute(null);
        Assert.Null(_vm.NodeManagement);
        Assert.False(_vm.IsNodeManagementVisible);
        // Re-open same env → reuse cached
        _vm.OpenNodeManagementCommand.Execute(env);
        Assert.Same(cached, _vm.NodeManagement);
    }

    [Fact]
    public void OpenUpgradeNodes_NewEnv_CreatesVM_ShowsPanel()
    {
        var env = GetEnv("env-a");
        // For UpgradeNodesVM ctor, EnvListVM needs to pass catalog search delegate.
        // Test setup uses FakeCatalogRepository; production will wire real one.
        _vm.OpenUpgradeNodesCommand.Execute(env);
        Assert.NotNull(_vm.UpgradeNodes);
        Assert.True(_vm.IsUpgradeNodesVisible);
        Assert.Equal("env-A", _vm.UpgradeNodes!.EnvName);
    }

    [Fact]
    public void OpenNodeManagement_BusyEnv_GatedByCanExecute()
    {
        var env = GetEnv("env-a");
        _vm.MarkEnvBusy(env, "test");
        Assert.False(_vm.OpenNodeManagementCommand.CanExecute(env));
        _vm.UnmarkEnvBusy(env);
        Assert.True(_vm.OpenNodeManagementCommand.CanExecute(env));
    }
}
```

(Adjust ctor signature to match actual `EnvironmentListViewModel` — read the file first to confirm parameter list.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvironmentListViewModelNodeManagementTests" --no-restore`
Expected: 6 FAIL (compile errors for missing `NodeManagement` / `IsNodeManagementVisible` / `OpenNodeManagementCommand` etc)

- [ ] **Step 3: Edit EnvironmentListViewModel.cs**

Read the file first (`git show HEAD:src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs | head -100` or just `Read` with limit) to find exact lines to edit.

**Deletions** (lines/blocks to remove):
- `private EnvironmentDetailViewModel? _environmentDetail;` (line ~390)
- `private string? _environmentDetailEnvId;` (line ~391)
- `public EnvironmentDetailViewModel? EnvironmentDetail { get; private set { ... } }` (lines 392-402)
- `public bool HasEnvironmentDetail => _environmentDetail is not null;` (line 403)
- `private void SelectedChangedHandler()` (lines 423-451) — entire method
- `SelectedChangedHandler()` call inside `Selected` setter (line 414)
- Any related `using ComfyUI.Manager.ViewModels;` import if no longer needed (verify)

**Additions**:

Add field block near top of class (after `_logger` field area):
```csharp
// v0.6.15.8:per-env VM cache — 切换 env 不重建,保留 selected row / scroll / 弹窗状态
private readonly Dictionary<string, NodeManagementViewModel> _nodeMgmtCache = new();
private readonly Dictionary<string, UpgradeNodesViewModel> _upgradeCache = new();
private NodeManagementViewModel? _nodeManagement;
private UpgradeNodesViewModel? _upgradeNodes;
```

Add property block (near other properties):
```csharp
public NodeManagementViewModel? NodeManagement
{
    get => _nodeManagement;
    private set
    {
        if (SetField(ref _nodeManagement, value))
            RaisePropertyChanged(nameof(IsNodeManagementVisible));
    }
}
public bool IsNodeManagementVisible => _nodeManagement is not null;

public UpgradeNodesViewModel? UpgradeNodes
{
    get => _upgradeNodes;
    private set
    {
        if (SetField(ref _upgradeNodes, value))
            RaisePropertyChanged(nameof(IsUpgradeNodesVisible));
    }
}
public bool IsUpgradeNodesVisible => _upgradeNodes is not null;
```

Add commands (in `RelayCommand` initialization block, where `InstallNodeCommand` etc are defined — line ~312):
```csharp
OpenNodeManagementCommand = new RelayCommand(
    p => OpenNodeManagement(p as Environment ?? Selected),
    p => (p as Environment ?? Selected) is { } e && !IsEnvBusy(e));
OpenUpgradeNodesCommand = new RelayCommand(
    p => OpenUpgradeNodes(p as Environment ?? Selected),
    p => (p as Environment ?? Selected) is { } e && !IsEnvBusy(e));
CloseNodeManagementCommand = new RelayCommand(_ => NodeManagement = null);
CloseUpgradeNodesCommand = new RelayCommand(_ => UpgradeNodes = null);
```

In `RaiseCanExecuteChanged` block (line ~1465-1471), add the 4 new commands:
```csharp
OpenNodeManagementCommand.RaiseCanExecuteChanged();
OpenUpgradeNodesCommand.RaiseCanExecuteChanged();
CloseNodeManagementCommand.RaiseCanExecuteChanged();
CloseUpgradeNodesCommand.RaiseCanExecuteChanged();
```

Add private methods (near `OpenInstallNodePicker` at line ~1305):
```csharp
private void OpenNodeManagement(Environment? env)
{
    if (env is null || _nodeRepo is null) return;
    if (!_nodeMgmtCache.TryGetValue(env.Id, out var vm))
    {
        vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, env.Id, env.Name);
        vm.CloseRequested += () => NodeManagement = null;
        _nodeMgmtCache[env.Id] = vm;
    }
    NodeManagement = vm;
}

private void OpenUpgradeNodes(Environment? env)
{
    if (env is null || _nodeRepo is null) return;
    if (!_upgradeCache.TryGetValue(env.Id, out var vm))
    {
        // production wiring (T5): pass catalog search delegate
        // For now, fallback to no-op search if catalog unavailable
        Func<string, int, IEnumerable<CatalogEntry>> catalogSearch =
            (_catalogRepo, _versionRepo) switch
            {
                (not null, _) => (q, n) => _catalogRepo!.Search(q, n),
                _ => (_, _) => Enumerable.Empty<CatalogEntry>()
            };
        vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps, catalogSearch, env.Id, env.Name);
        vm.CloseRequested += () => UpgradeNodes = null;
        _upgradeCache[env.Id] = vm;
    }
    UpgradeNodes = vm;
}
```

`Selected` setter — remove the `SelectedChangedHandler()` call. The setter should only raise `StartTooltip` property change.

- [ ] **Step 4: Build + run tests**

Run: `dotnet build src-wpf/ComfyUI.Manager` then `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvironmentListViewModelNodeManagementTests" --no-restore`
Expected: 0 build errors, 6 tests PASS

- [ ] **Step 5: Run full suite to check no regression**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-restore`
Expected: 1206 + 6 new = 1212 PASS / 3 pre-existing FAIL / 1 SKIP (or similar — exact count may vary by prior test drift)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelNodeManagementTests.cs
git commit -m "feat(env-list): revert T7 + NodeManagement/UpgradeNodes cache + 6 tests (v0.6.15.8 T5)

Removes SelectedChangedHandler + EnvironmentDetail + HasEnvironmentDetail +
_environmentDetail/_environmentDetailEnvId (v0.6.15.7 T7 dead code).
Adds per-env Dictionary<string, _> caches for both VMs (state preserved
across env switches). 4 new RelayCommands + property raise notifications.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 6: EnvironmentListView.xaml revert + 2x6 grid + 2 new panels + code-behind

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (revert master-detail + button grid + 2 new panels)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs` (add 2 close click handlers)

- [ ] **Step 1: Revert master-detail Grid**

In `EnvironmentListView.xaml`, replace the `<Grid Margin="12,0,12,12">` block (lines 182-436) with a simpler `<Grid Margin="12,0,12,12">` containing only the empty-state Border + ListBox (no master-detail, no GridSplitter, no right panel):

```xml
<!-- 中间:env card 列表 -->
<Grid Margin="12,0,12,12">
    <!-- 空状态 -->
    <Border Visibility="{Binding Environments.Count, Converter={StaticResource ZeroCountToVisibility}}"
            Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
            CornerRadius="8" Padding="32"
            HorizontalAlignment="Center" VerticalAlignment="Center">
        <StackPanel HorizontalAlignment="Center">
            <TextBlock Text="📦" FontSize="48" HorizontalAlignment="Center"
                       Foreground="{DynamicResource OutlineBrush}" />
            <TextBlock Text="还没有任何环境" FontSize="16" Margin="0,8,0,4"
                       HorizontalAlignment="Center"
                       Foreground="{DynamicResource OnSurfaceBrush}" />
            <TextBlock Text="点右上角「+ 新建环境」开始"
                       FontSize="12" HorizontalAlignment="Center"
                       Foreground="{DynamicResource OutlineBrush}" />
        </StackPanel>
    </Border>

    <!-- env 卡片列表 -->
    <ListBox ItemsSource="{Binding Environments}"
             SelectedItem="{Binding Selected}"
             Background="Transparent" BorderThickness="0"
             ScrollViewer.HorizontalScrollBarVisibility="Disabled"
             HorizontalContentAlignment="Stretch"
             Visibility="{Binding Environments.Count, Converter={StaticResource InverseZeroCountToVisibility}}">
        <ListBox.ItemContainerStyle>
            <Style TargetType="ListBoxItem">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Padding" Value="0" />
                <Setter Property="Margin" Value="0,0,0,8" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ListBoxItem">
                            <ContentPresenter />
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
        </ListBox.ItemContainerStyle>
        <ListBox.ItemTemplate>
            <DataTemplate>
                <!-- env card: header / meta / 2-row 6-col button grid -->
                <Border Padding="12" CornerRadius="6"
                        Background="{DynamicResource SurfaceBrush}"
                        BorderThickness="1">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Setter Property="BorderBrush" Value="{DynamicResource OutlineBrush}" />
                            <Style.Triggers>
                                <DataTrigger Value="True">
                                    <DataTrigger.Binding>
                                        <Binding Path="IsSelected"
                                                 RelativeSource="{RelativeSource AncestorType=ListBoxItem}" />
                                    </DataTrigger.Binding>
                                    <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}" />
                                    <Setter Property="BorderThickness" Value="2" />
                                    <Setter Property="Padding" Value="11" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>

                        <!-- Row 0: Header — 状态点 + Name + Port + BED 徽章 -->
                        <Grid Grid.Row="0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <Ellipse Grid.Column="0" Width="10" Height="10" VerticalAlignment="Center"
                                     Margin="0,0,8,0">
                                <Ellipse.Style>
                                    <Style TargetType="Ellipse">
                                        <Setter Property="Fill" Value="{DynamicResource OutlineBrush}" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Status}" Value="running">
                                                <Setter Property="Fill" Value="{DynamicResource SuccessBrush}" />
                                            </DataTrigger>
                                            <DataTrigger Binding="{Binding Status}" Value="failed">
                                                <Setter Property="Fill" Value="{DynamicResource ErrorBrush}" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Ellipse.Style>
                            </Ellipse>
                            <TextBlock Grid.Column="1" Text="{Binding Name}"
                                       FontSize="16" FontWeight="Bold" VerticalAlignment="Center"
                                       Foreground="{DynamicResource OnSurfaceBrush}" />
                            <Border Grid.Column="2" Padding="8,2" CornerRadius="4"
                                    Background="{DynamicResource BackgroundBrush}"
                                    VerticalAlignment="Center" Margin="0,0,8,0">
                                <TextBlock FontSize="11"
                                           Foreground="{DynamicResource OnSurfaceBrush}">
                                    <Run Text="Port " Foreground="{DynamicResource OutlineBrush}" />
                                    <Run Text="{Binding Port, Mode=OneWay}" FontWeight="Bold" />
                                </TextBlock>
                            </Border>
                            <Border Grid.Column="3" Padding="8,2" CornerRadius="4"
                                    VerticalAlignment="Center">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Background" Value="{DynamicResource OutlineBrush}" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding BedStatus}" Value="done">
                                                <Setter Property="Background" Value="{DynamicResource SuccessBrush}" />
                                            </DataTrigger>
                                            <DataTrigger Binding="{Binding BedStatus}" Value="installing">
                                                <Setter Property="Background" Value="{DynamicResource SecondaryBrush}" />
                                            </DataTrigger>
                                            <DataTrigger Binding="{Binding BedStatus}" Value="failed">
                                                <Setter Property="Background" Value="{DynamicResource ErrorBrush}" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                                <TextBlock Text="{Binding BedDisplay}" FontSize="11"
                                           Foreground="{DynamicResource OnPrimaryBrush}" />
                            </Border>
                        </Grid>

                        <!-- Row 1: Meta — PID + Python/BED profile -->
                        <TextBlock Grid.Row="1" Margin="18,4,0,8" FontSize="11"
                                   Foreground="{DynamicResource OutlineBrush}">
                            <Run Text="PID " />
                            <Run Text="{Binding Pid, Mode=OneWay, TargetNullValue='-'}" />
                            <Run Text="  ·  " />
                            <Run Text="{Binding PythonVersion, Mode=OneWay, TargetNullValue='-'}" />
                            <Run Text="  ·  " />
                            <Run Text="{Binding BedProfileId, Mode=OneWay, TargetNullValue='未部署'}" />
                        </TextBlock>

                        <!-- Row 2: Actions — 2 行 × 6 列 Grid -->
                        <Grid Grid.Row="2">
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <!-- Row 0: 装卸链路 -->
                            <Button Grid.Row="0" Grid.Column="0" Content="启动" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.StartCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}"
                                    ToolTip="{Binding DataContext.StartTooltip,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}" />
                            <Button Grid.Row="0" Grid.Column="1" Content="停止" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.StopCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="0" Grid.Column="2" Content="{Binding RequirementsButtonText}" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.ToggleRequirementsCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="0" Grid.Column="3" Content="{Binding BaseEnvButtonText}" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.ToggleBaseEnvCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="0" Grid.Column="4" Content="{Binding ComfyUiManagerButtonText}" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.ToggleComfyUiManagerCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <!-- v0.6.15.8 T6:第 6 列预留空 cell(后续填),保留 grid 形状 -->
                            <Border Grid.Row="0" Grid.Column="5" Margin="2" />
                            <!-- Row 1: 调试/删除链路 -->
                            <Button Grid.Row="1" Grid.Column="0" Content="查看日志" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.ShowLogCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="1" Grid.Column="1" Content="打开浏览器" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.OpenBrowserCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <!-- v0.6.15.8 T6:节点管理(原"安装节点"改名)+ 升级节点(新) -->
                            <Button Grid.Row="1" Grid.Column="2" Content="节点管理" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.OpenNodeManagementCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="1" Grid.Column="3" Content="升级节点" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.OpenUpgradeNodesCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="1" Grid.Column="4" Content="组件报告" Margin="2" MinWidth="0"
                                    Style="{StaticResource MaterialButton}"
                                    Command="{Binding DataContext.ReportComponentsCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                            <Button Grid.Row="1" Grid.Column="5" Content="删除" Margin="2" MinWidth="0"
                                    Style="{StaticResource DangerButton}"
                                    Command="{Binding DataContext.DeleteCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                        </Grid>
                    </Grid>
                </Border>
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>
</Grid>
```

- [ ] **Step 2: Add 2 new inline status panels**

After the existing 4 status panels (启动/装依赖/卸载基础环境/ComfyUI Manager — find the closing `</StackPanel>` of the bottom status container, around line 180), append:

```xml
            <!-- v0.6.15.8:节点管理 inline panel(同 SurfaceBrush/OutlineBrush/Border pattern) -->
            <Border Margin="0,6,0,0" Padding="12"
                    Background="{DynamicResource SurfaceBrush}"
                    BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
                    CornerRadius="6"
                    Visibility="{Binding IsNodeManagementVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
                <StackPanel DataContext="{Binding NodeManagement}">
                    <DockPanel>
                        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                            <Button Content="扫描" Command="{Binding ScanCommand}"
                                    Style="{StaticResource MaterialButton}" />
                            <Button Content="安装节点" Command="{Binding InstallCommand}"
                                    Style="{StaticResource MaterialButton}" Margin="6,0,0,0" />
                            <Button Content="✕" Margin="6,0,0,0"
                                    Click="OnNodeManagementCloseClicked"
                                    Style="{StaticResource GearIconButtonStyle}"
                                    Foreground="{DynamicResource OnSurfaceBrush}" />
                        </StackPanel>
                        <TextBlock VerticalAlignment="Center">
                            <Run Text="{Binding EnvName, Mode=OneWay}" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
                            <Run Text=" 的节点管理" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
                        </TextBlock>
                    </DockPanel>
                    <ContentControl Content="{Binding}" Margin="0,8,0,0" MinHeight="300">
                        <ContentControl.Resources>
                            <DataTemplate DataType="{x:Type vm:NodeManagementViewModel}">
                                <v:NodeManagementView />
                            </DataTemplate>
                        </ContentControl.Resources>
                    </ContentControl>
                </StackPanel>
            </Border>

            <!-- v0.6.15.8:升级节点 inline panel(同 pattern,无顶右按钮) -->
            <Border Margin="0,6,0,0" Padding="12"
                    Background="{DynamicResource SurfaceBrush}"
                    BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
                    CornerRadius="6"
                    Visibility="{Binding IsUpgradeNodesVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
                <StackPanel DataContext="{Binding UpgradeNodes}">
                    <DockPanel>
                        <Button DockPanel.Dock="Right" Content="✕"
                                Click="OnUpgradeNodesCloseClicked"
                                Style="{StaticResource GearIconButtonStyle}"
                                Foreground="{DynamicResource OnSurfaceBrush}" />
                        <TextBlock VerticalAlignment="Center">
                            <Run Text="{Binding EnvName, Mode=OneWay}" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
                            <Run Text=" 需要升级的节点" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
                        </TextBlock>
                    </DockPanel>
                    <ContentControl Content="{Binding}" Margin="0,8,0,0" MinHeight="200">
                        <ContentControl.Resources>
                            <DataTemplate DataType="{x:Type vm:UpgradeNodesViewModel}">
                                <v:UpgradeNodesView />
                            </DataTemplate>
                        </ContentControl.Resources>
                    </ContentControl>
                </StackPanel>
            </Border>
```

- [ ] **Step 3: Add close click handlers in code-behind**

In `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs`, add 2 methods:

```csharp
private void OnNodeManagementCloseClicked(object sender, System.Windows.RoutedEventArgs e)
{
    if (DataContext is EnvironmentListViewModel vm)
    {
        vm.CloseNodeManagementCommand.Execute(null);
    }
}

private void OnUpgradeNodesCloseClicked(object sender, System.Windows.RoutedEventArgs e)
{
    if (DataContext is EnvironmentListViewModel vm)
    {
        vm.CloseUpgradeNodesCommand.Execute(null);
    }
}
```

(Pattern matches existing `OnRequirementsStatusCloseClicked` / `OnComfyUiManagerStatusCloseClicked` handlers.)

- [ ] **Step 4: Build to verify XAML compiles**

Run: `dotnet build src-wpf/ComfyUI.Manager`
Expected: 0 errors

- [ ] **Step 5: Run full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-restore`
Expected: ~1224 PASS (1206 + 18 new across T1-T5 + 0 from T6 since T6 has no tests) / 3 pre-existing FAIL / 1 SKIP

- [ ] **Step 6: Staging rebuild**

```bash
dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"
```

Expected: build succeeds, no errors

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs
git commit -m "feat(env-list): revert T7 master-detail + 2x6 grid + 2 bottom-popup panels (v0.6.15.8 T6)

Replaces master-detail side panel with 2 bottom-popup inline panels
(nodes-mgmt + upgrade-nodes) following existing 4 status-panel pattern.
Renames 安装节点 → 节点管理 (per-env VM cache preserves state). Adds new
升级节点 button. 2x6 button grid (row 0 col 5 = empty for now).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 7: Final review + verification

**Files:** none (verification only)

- [ ] **Step 1: Run full test suite — confirm baseline + new tests all pass (pre-existing FAILs allowed)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-restore`
Expected: 1212+ PASS / 3 pre-existing FAIL / 1 SKIP (no new FAILs from v0.6.15.8 work)

- [ ] **Step 2: Staging rebuild (final, post-T6) — already done in T6 step 6. Verify no later dirty state.**

Run: `git status --short`
Expected: clean (no dirty files beyond pre-existing WIP the user owns)

- [ ] **Step 3: Write ship summary to memory**

Update `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_15_8_node_panel_redesign.md` (new) with:
- Plan + spec paths
- HEAD commit
- Test count
- Files touched
- v-bump decision (default: skip — user's call)
- Verification checklist for GUI smoke

Add line to `MEMORY.md` index: `[v0.6.15.8 node-panel redesign](project_v0_6_15_8_node_panel_redesign.md) — ✓ SHIP-READY <date>, HEAD \`<sha>\`, <test_count> PASS / 3 FAIL / 1 SKIP; 7 tasks replacing T7 master-detail with bottom-popup 节点管理 + 升级节点; staging rebuilt; GUI smoke TBD`

- [ ] **Step 4: Present ship checklist to user**

Show:
1. Staging binary ready at `release/staging/ComfyUI Manager/`
2. GUI smoke steps per spec §Verification (节点管理 + 升级节点 + 缓存保留)
3. Optional v-bump / release zip decisions
4. Pre-existing WIP user-owned (10+ files)

Wait for user confirmation before any v-bump / release zip / push.

---

## Self-Review (post-write)

**Spec coverage:**
- T7 revert → T5 ✓ (delete fields/method)
- Bottom-popup inline panels → T6 ✓ (XAML)
- 节点管理 VM (auto-rescan + scan/install/delete/close) → T2 ✓
- 升级节点 VM (outdated filter + upgrade per-row) → T3 ✓
- Per-env VM cache → T5 ✓ (Dictionary + commands)
- NodeOperations.RescanAsync → T1 ✓
- 2x6 button grid → T6 ✓
- XAML Views → T4 ✓
- Tests (~22 new) → T1-T5 each have inline tests
- Verification (build + tests + staging + GUI smoke) → T6 + T7 ✓

**Placeholder scan:** No TBD/TODO/"implement later"/"fill in details" found. (One TODO in T2 ToggleCommand + one in spec — both intentional placeholders for non-ship-blocking deferred features.)

**Type consistency:**
- `NodeManagementViewModel` ctor `(NodeRepository, NodeOperations, ErrorBannerViewModel, string envId, string envName)` — used consistently in T2 tests + T5 EnvListVM OpenNodeManagement method
- `UpgradeNodesViewModel` ctor `(NodeRepository, NodeOperations, Func<string,int,IEnumerable<CatalogEntry>>, string envId, string envName)` — used consistently in T3 tests + T5 EnvListVM OpenUpgradeNodes method
- `RescanAsync(string envId, CancellationToken ct = default)` → `Task<IReadOnlyList<ScannedNode>>` — used consistently in T1 tests + T2 + T3 VMs
- `UpgradeCandidate` wrapper class — used in T3 + T4 (XAML binding `Node.Package` / `LatestVersion`)

**Plan deviations from spec noted:**
- T3 added `UpgradeCandidate` wrapper class (not in spec) — required for XAML binding since WPF can't bind to method calls. Implementation-step note in T3.
- T6 Row 0 col 5 empty Border placeholder (not in spec) — to maintain 2x6 grid shape; user can fill later.
- T3 catalog via `Func<>` delegate instead of injected `ICatalogSource` interface (not in spec) — simpler test wiring, no production code change needed (EnvListVM wraps real CatalogRepository.Search into the delegate).

All deviations are pragmatic refinements documented inline.