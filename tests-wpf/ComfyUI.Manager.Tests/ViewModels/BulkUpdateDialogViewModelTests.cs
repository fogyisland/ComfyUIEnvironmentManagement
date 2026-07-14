using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BulkUpdateDialogViewModelTests
{
    private static BulkUpdateDialogViewModel NewVmWithFixture()
    {
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var orch = new BulkUpdateOrchestrator(
            System.IO.Path.GetTempPath(), "git", envRepo, nodeRepo);

        var vm = new BulkUpdateDialogViewModel(orch);
        var env1 = new EnvRow("env-1", "Env 1");
        env1.Nodes.Add(new NodeSelectRow("node-a", "Node A"));
        env1.Nodes.Add(new NodeSelectRow("node-b", "Node B"));
        env1.Selected = true;
        env1.Nodes[0].Selected = true;
        env1.Nodes[1].Selected = true;
        vm.LoadEnvs(new[] { env1 });
        return vm;
    }

    [Fact]
    public void LoadEnvs_PopulatesEnvRows()
    {
        var vm = NewVmWithFixture();
        Assert.Single(vm.EnvRows);
        Assert.Equal(2, vm.EnvRows[0].Nodes.Count);
    }

    [Fact]
    public void SelectedIds_ReflectCheckboxes()
    {
        var vm = NewVmWithFixture();
        Assert.Equal(new[] { "env-1" }, vm.SelectedEnvIds());
        Assert.Equal(new[] { "node-a", "node-b" }, vm.SelectedNodeIds());
    }

    [Fact]
    public void StartCommand_EnabledWhenSelectionPresent()
    {
        var vm = NewVmWithFixture();
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleSelectAll_ClearsWhenAllSelected()
    {
        var vm = NewVmWithFixture();
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.False(vm.EnvRows[0].Selected);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartsInSelectEnvMode()
    {
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var orch = new BulkUpdateOrchestrator(
            System.IO.Path.GetTempPath(), "git", envRepo, nodeRepo);
        var vm = new BulkUpdateDialogViewModel(orch);
        Assert.Equal(BulkUpdateMode.SelectEnv, vm.Mode);
    }
}
