using System;
using System.Collections.Generic;
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
    private readonly FakeNodeOperationsForManagement _nodeOps;
    private readonly List<CatalogEntry> _catalogEntries = new();
    private readonly string _envId = "env-1";

    public UpgradeNodesViewModelTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new FakeNodeOperationsForManagement { NodeRepo = _nodeRepo };
    }

    public void Dispose() => _db.Dispose();

    private void Seed(string id, string pkg, string? tag)
    {
        var n = new ScannedNode
        {
            Id = id,
            EnvId = _envId,
            Package = pkg,
            Source = "env",
            ScanMeta = new Dictionary<string, string>(),
        };
        if (tag is not null) n.ScanMeta["installed_tag"] = tag;
        _nodeRepo.Upsert(n);
    }

    private UpgradeNodesViewModel CreateVm() =>
        new(_nodeRepo, _nodeOps, catalogSearch: (_, _) => _catalogEntries, _envId, envName: "test-env");

    [Fact]
    public void Constructor_LoadsOutdatedOnly()
    {
        Seed("o1", "outdated-pkg", "v1.0");
        Seed("c1", "current-pkg", "v1.2");
        Seed("u1", "untagged-pkg", null);
        _catalogEntries.AddRange(new[]
        {
            new CatalogEntry { Package = "outdated-pkg", LatestVersion = "v1.2" },
            new CatalogEntry { Package = "current-pkg", LatestVersion = "v1.2" },
            new CatalogEntry { Package = "untagged-pkg", LatestVersion = "v1.0" },
        });
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId);

        var vm = CreateVm();
        SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 1, TimeSpan.FromSeconds(2));

        Assert.Single(vm.OutdatedNodes);
        Assert.Equal("outdated-pkg", vm.OutdatedNodes[0].Node.Package);
        Assert.Equal("v1.2", vm.OutdatedNodes[0].LatestVersion);
    }

    [Fact]
    public async Task UpgradeCommand_Successful_ReloadsNodeLeavesList()
    {
        Seed("o1", "outdated-pkg", "v1.0");
        _catalogEntries.Add(new CatalogEntry { Package = "outdated-pkg", LatestVersion = "v1.2" });
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId);
        _nodeOps.UpgradeResult = NodeOperationResult.Ok("v1.2");

        var vm = CreateVm();
        SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 1, TimeSpan.FromSeconds(2));
        Assert.True(_nodeOps.RescanCalled);

        // Simulate that the upgrade bumped the tag: re-seed with current version
        // and a fresh scan so LoadAsync sees a current install.
        Seed("o1", "outdated-pkg", "v1.2");
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId);
        _nodeOps.RescanCalled = false;

        vm.UpgradeCommand.Execute(vm.OutdatedNodes[0].Node);
        SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 0, TimeSpan.FromSeconds(3));

        Assert.Empty(vm.OutdatedNodes);
        Assert.True(_nodeOps.UpgradeCalled);
        Assert.True(_nodeOps.RescanCalled);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UpgradeCommand_Failed_KeepsNodeInList()
    {
        Seed("o1", "outdated-pkg", "v1.0");
        _catalogEntries.Add(new CatalogEntry { Package = "outdated-pkg", LatestVersion = "v1.2" });
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId);
        _nodeOps.UpgradeResult = NodeOperationResult.Fail("git pull 失败");

        var vm = CreateVm();
        SpinWait.SpinUntil(() => !vm.Busy && vm.OutdatedNodes.Count == 1, TimeSpan.FromSeconds(2));

        vm.UpgradeCommand.Execute(vm.OutdatedNodes[0].Node);
        SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

        Assert.Single(vm.OutdatedNodes);
        Assert.Equal("outdated-pkg", vm.OutdatedNodes[0].Node.Package);
        Assert.True(_nodeOps.UpgradeCalled);
        await Task.CompletedTask;
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested_Event()
    {
        _nodeOps.ScanResult = new List<ScannedNode>();
        var vm = CreateVm();
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
        // _catalogEntries is empty: no catalog entry for "missing-from-catalog"
        _nodeOps.ScanResult = _nodeRepo.ListByEnv(_envId);

        var vm = CreateVm();
        SpinWait.SpinUntil(() => !vm.Busy, TimeSpan.FromSeconds(2));

        Assert.Empty(vm.OutdatedNodes);
    }
}
