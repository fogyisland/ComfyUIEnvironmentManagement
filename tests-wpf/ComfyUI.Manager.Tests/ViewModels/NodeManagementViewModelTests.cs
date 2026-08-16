using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public class NodeManagementViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _nodeRepo;
    private readonly FakeNodeOperationsForManagement _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly EnvironmentRepository _envRepo;
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _envId = "env-1";

    public NodeManagementViewModelTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new FakeNodeOperationsForManagement();
        _errorBanner = new ErrorBannerViewModel();
        // R1 fix: VM ctor now takes real EnvironmentRepository / CatalogRepository /
        // NodeVersionRepository. Tests don't exercise the picker dialog directly
        // (uses OpenInstallPickerOverride), but VM ctor must accept non-null values
        // so production paths won't pass null! to CatalogEntryPickerDialog.Show.
        _envRepo = new EnvironmentRepository(_db.Factory);
        _catalogRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        _versionRepo = new NodeVersionRepository(new CatalogCacheStore(_db.Path));
    }

    // NOTE: brief's tests use plain `FakeNodeOperations` but there's already an
    // internal class of the same name in `ComfyUI.Manager.Tests.ViewModels`
    // (v0.6.15.5 T2's InstallDialogViewModelProgressTests). Renamed to
    // `FakeNodeOperationsForManagement` to avoid namespace collision.

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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        // Pump message loop briefly so fire-and-forget ScanAsync completes
        SpinWait.SpinUntil(() => vm.Nodes.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal("test-env", vm.EnvName);
        Assert.True(_nodeOps.RescanCalled);
    }

    [Fact]
    public async Task ScanCommand_AfterBusyFalse_RerunsScan()
    {
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "n1", EnvId = _envId, Package = "pkg-a", Source = "env" },
        };
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

        var fired = false;
        vm.CloseRequested += () => fired = true;
        vm.CloseCommand.Execute(null);
        Assert.True(fired);
    }

    /// <summary>v0.6.15.9:scan 后给每行填 IsOutdated — installed_tag != catalog.LatestVersion → true,
    /// 其他情况 → false(包含 installed_tag 缺失 / catalog 没这个 package)。</summary>
    [Fact]
    public void Scan_PopulatesIsOutdated_BasedOnCatalog()
    {
        _catalogRepo.Upsert(new CatalogEntry { Id = "cat-outdated", Package = "outdated-pkg", SourceUrl = "https://example.com/outdated", CachedAt = "2026-08-16T00:00:00", ExpiresAt = "2099-12-31T00:00:00" });
        _catalogRepo.Upsert(new CatalogEntry { Id = "cat-current", Package = "current-pkg", SourceUrl = "https://example.com/current", CachedAt = "2026-08-16T00:00:00", ExpiresAt = "2099-12-31T00:00:00" });
        // CatalogRepository.Upsert 不写 latest_version 列 — 走 GitHubVersionService 的
        // UpdateLatestVersions 单独写入。test seed 必须显式调一次模拟。
        _catalogRepo.UpdateLatestVersions(new[] {
            ("https://example.com/outdated", "outdated-pkg", "v1.2"),
            ("https://example.com/current", "current-pkg", "v1.0"),
        });
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "o1", EnvId = _envId, Package = "outdated-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.0" } },
            new() { Id = "c1", EnvId = _envId, Package = "current-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.0" } },
            new() { Id = "u1", EnvId = _envId, Package = "untagged-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string>() },
            new() { Id = "x1", EnvId = _envId, Package = "uncatalogued-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v0.1" } },
        };
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => vm.Nodes.Count == 4, TimeSpan.FromSeconds(2));

        Assert.True(vm.Nodes.Single(n => n.Id == "o1").IsOutdated);
        Assert.False(vm.Nodes.Single(n => n.Id == "c1").IsOutdated);
        Assert.False(vm.Nodes.Single(n => n.Id == "u1").IsOutdated);
        Assert.False(vm.Nodes.Single(n => n.Id == "x1").IsOutdated);
    }

    /// <summary>v0.6.15.9:行内 UpgradeCommand 对 IsOutdated=true 的 node CanExecute=true 且
    /// 调 <c>NodeOps.UpgradeAsync</c>;成功后 ScanAsync rebuild,IsOutdated 转 false(若新 tag 匹配)。</summary>
    [Fact]
    public async Task UpgradeCommand_OutdatedNode_CallsNodeOpsUpgrade_ThenRescans()
    {
        _catalogRepo.Upsert(new CatalogEntry { Id = "cat-up", Package = "outdated-pkg", SourceUrl = "https://example.com/outdated", CachedAt = "2026-08-16T00:00:00", ExpiresAt = "2099-12-31T00:00:00" });
        _catalogRepo.UpdateLatestVersions(new[] {
            ("https://example.com/outdated", "outdated-pkg", "v1.2"),
        });
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.UpgradeResult = NodeOperationResult.Ok("v1.2");
        // 第二次 ScanResult 模拟升级后节点的 installed_tag 跟最新一致。
        var afterUpgrade = new List<ScannedNode>
        {
            new() { Id = "o1", EnvId = _envId, Package = "outdated-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.2" } },
        };
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "o1", EnvId = _envId, Package = "outdated-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.0" } },
        };
        // After first scan returns, swap ScanResult to simulate upgrade
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));
        // Outdated should be true at this point
        Assert.True(vm.Nodes[0].IsOutdated);
        Assert.True(vm.UpgradeCommand.CanExecute(vm.Nodes[0]));

        // Replace ScanResult so subsequent rescans (triggered by UpgradeAsync) get the upgraded state
        _nodeOps.ScanResult = afterUpgrade;

        await vm.UpgradeAsync(vm.Nodes[0]);
        SpinWait.SpinUntil(() => vm.Nodes.Count == 1 && !vm.Nodes[0].IsOutdated, TimeSpan.FromSeconds(2));

        Assert.True(_nodeOps.UpgradeCalled);
        Assert.False(vm.Nodes[0].IsOutdated);
        Assert.False(vm.UpgradeCommand.CanExecute(vm.Nodes[0]));
    }

    /// <summary>v0.6.15.9:installed_tag == catalog.LatestVersion 的 node 行内 UpgradeCommand
    /// CanExecute=false(按钮 disabled,不显示)。</summary>
    [Fact]
    public void UpgradeCommand_CurrentNode_CanExecuteFalse()
    {
        _catalogRepo.Upsert(new CatalogEntry { Id = "cat-curr", Package = "current-pkg", SourceUrl = "https://example.com/current", CachedAt = "2026-08-16T00:00:00", ExpiresAt = "2099-12-31T00:00:00" });
        _catalogRepo.UpdateLatestVersions(new[] {
            ("https://example.com/current", "current-pkg", "v1.2"),
        });
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "c1", EnvId = _envId, Package = "current-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.2" } },
        };
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

        Assert.False(vm.Nodes[0].IsOutdated);
        Assert.False(vm.UpgradeCommand.CanExecute(vm.Nodes[0]));
    }

    /// <summary>v0.6.15.9:catalog 没这个 package → IsOutdated=false,行内按钮 disabled。
    /// 即便 installed_tag 看起来"老",我们不主动报过时(避免误判,跟 UpgradeNodesViewModel 既有逻辑一致)。</summary>
    [Fact]
    public void UpgradeCommand_UncataloguedNode_CanExecuteFalse()
    {
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "x1", EnvId = _envId, Package = "unknown-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v0.1" } },
        };
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

        Assert.False(vm.Nodes[0].IsOutdated);
        Assert.False(vm.UpgradeCommand.CanExecute(vm.Nodes[0]));
    }

    /// <summary>v0.6.15.9:NodeOps.UpgradeAsync 返 Fail → ErrorBanner 加错误,节点保留(不删 IsOutdated)。
    /// 下次重试还显示升级按钮。</summary>
    [Fact]
    public async Task UpgradeCommand_FailedResult_LeavesNodeInList_AddsErrorBanner()
    {
        _catalogRepo.Upsert(new CatalogEntry { Id = "cat-fail", Package = "outdated-pkg", SourceUrl = "https://example.com/outdated", CachedAt = "2026-08-16T00:00:00", ExpiresAt = "2099-12-31T00:00:00" });
        _catalogRepo.UpdateLatestVersions(new[] {
            ("https://example.com/outdated", "outdated-pkg", "v1.2"),
        });
        _nodeOps.NodeRepo = _nodeRepo;
        _nodeOps.ScanResult = new List<ScannedNode>
        {
            new() { Id = "o1", EnvId = _envId, Package = "outdated-pkg", Source = "env",
                    ScanMeta = new Dictionary<string, string> { ["installed_tag"] = "v1.0" } },
        };
        _nodeOps.UpgradeResult = NodeOperationResult.Fail("git pull 失败");
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envRepo, _catalogRepo, _versionRepo, _envId, envName: "test-env");
        SpinWait.SpinUntil(() => vm.Nodes.Count == 1, TimeSpan.FromSeconds(2));

        await vm.UpgradeAsync(vm.Nodes[0]);
        SpinWait.SpinUntil(() => _errorBanner.HasErrors, TimeSpan.FromSeconds(2));

        Assert.True(_errorBanner.HasErrors);
        Assert.Single(vm.Nodes);
        Assert.True(vm.Nodes[0].IsOutdated);
        Assert.True(vm.UpgradeCommand.CanExecute(vm.Nodes[0]));
    }
}
