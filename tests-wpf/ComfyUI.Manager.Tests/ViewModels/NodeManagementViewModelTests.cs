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
    private readonly FakeNodeOperationsForManagement _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId = "env-1";

    public NodeManagementViewModelTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new FakeNodeOperationsForManagement();
        _errorBanner = new ErrorBannerViewModel();
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
        var vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, _envId, envName: "test-env");
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
}
