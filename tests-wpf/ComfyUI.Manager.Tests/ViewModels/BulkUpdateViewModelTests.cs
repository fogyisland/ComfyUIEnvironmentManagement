using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.18:测试 <see cref="BulkUpdateViewModel"/>(原 <c>BulkUpdateDialogViewModel</c> 的 inline 替代)。
/// 行为跟 dialog VM 等价 —— 删了 <c>Mode</c>/<c>Summary</c> setter 跟 <c>BulkUpdateMode</c> enum
/// (UI 永远可见,summary 直接渲染在底部 inline Border,不需要 dialog 模式切换)。
/// </summary>
public class BulkUpdateViewModelTests
{
    private static BulkUpdateViewModel NewVmWithFixture()
    {
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var orch = new BulkUpdateOrchestrator(
            System.IO.Path.GetTempPath(), "git", envRepo, nodeRepo);

        var vm = new BulkUpdateViewModel(orch);
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
    public void CancelCommand_DisabledWhenNotBusy()
    {
        // v0.6.18:inline VM 的 CancelCommand.CanExecute 是 IsBusy(原 dialog
        // 还多一项 Mode==Running;inline 没 Mode 概念,纯 IsBusy 就够了)。
        var vm = NewVmWithFixture();
        Assert.False(vm.IsBusy);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Summary_InitiallyNull_NeverThrows()
    {
        var vm = NewVmWithFixture();
        Assert.Null(vm.Summary);   // inline 模式下 summary 没 run 过为 null,UI 用 NullToVisibility 自动隐藏
    }

    [Fact]
    public void LoadEnvs_ClearsPreviousList()
    {
        var vm = NewVmWithFixture();
        vm.LoadEnvs(new[]
        {
            new EnvRow("env-a", "Env A"),
            new EnvRow("env-b", "Env B"),
        });
        Assert.Equal(2, vm.EnvRows.Count);
        Assert.Equal("env-a", vm.EnvRows[0].EnvId);
    }
}
