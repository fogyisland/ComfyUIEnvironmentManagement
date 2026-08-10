using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
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
        env1.Selected = true;
        vm.LoadEnvs(new[] { env1 });
        return vm;
    }

    [Fact]
    public void LoadEnvs_PopulatesEnvRows()
    {
        var vm = NewVmWithFixture();
        Assert.Single(vm.EnvRows);
    }

    [Fact]
    public void SelectedIds_ReflectCheckboxes()
    {
        var vm = NewVmWithFixture();
        Assert.Equal(new[] { "env-1" }, vm.SelectedEnvIds());
        // v0.6.11 T8:默认两个 target 都勾上。
        Assert.Equal(
            new[] { BulkUpdateTargetKind.ComfyUi, BulkUpdateTargetKind.ComfyUiManager },
            vm.SelectedTargetKinds());
    }

    [Fact]
    public void StartCommand_EnabledWhenSelectionPresent()
    {
        var vm = NewVmWithFixture();
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_DisabledWhenAllTargetsUnchecked()
    {
        var vm = NewVmWithFixture();
        vm.UpdateComfyUi = false;
        vm.UpdateComfyUiManager = false;
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_EnabledWhenOneTargetChecked()
    {
        var vm = NewVmWithFixture();
        vm.UpdateComfyUi = true;
        vm.UpdateComfyUiManager = false;
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(
            new[] { BulkUpdateTargetKind.ComfyUi },
            vm.SelectedTargetKinds());
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
