using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x #594:PathMigrationConfirmViewModel 测试。镜像 NodeInstallDiffWarningDialogTests 模式。
/// 重点:command 写 Decisions + 触发 CloseRequested;Cancel 设 Decisions=null。
/// </summary>
public class PathMigrationConfirmViewModelTests
{
    private static IReadOnlyList<PathMigrationItem> ThreeSampleItems() => new[]
    {
        new PathMigrationItem("EnvsDir", @"D:\old\Envs", @"D:\new\Envs"),
        new PathMigrationItem("DefaultModelsDirectory", @"D:\old\Models", @"D:\new\Models"),
        new PathMigrationItem("WorkflowsDirectory", @"D:\old\Workflow", @"D:\new\Workflow"),
    };

    [Fact]
    public void Vm_Ctor_PopulatesItems()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal("EnvsDir", vm.Items[0].Label);
        Assert.Equal(@"D:\old\Envs", vm.Items[0].CurrentValue);
        Assert.Equal(@"D:\new\Envs", vm.Items[0].RecommendedValue);
        Assert.True(vm.Items[0].Selected);  // 默认勾选
        Assert.Null(vm.Decisions);
    }

    [Fact]
    public void Vm_HeaderText_CountsItems()
    {
        var vm0 = new PathMigrationConfirmViewModel(new PathMigrationItem[0]);
        Assert.Equal("未发现可疑路径", vm0.HeaderText);

        var vm1 = new PathMigrationConfirmViewModel(new[]
        {
            new PathMigrationItem("EnvsDir", @"D:\old\Envs", @"D:\new\Envs"),
        });
        Assert.Equal("检测到 1 个路径错位,请确认:", vm1.HeaderText);

        var vm3 = new PathMigrationConfirmViewModel(ThreeSampleItems());
        Assert.Equal("检测到 3 个路径错位,请逐项确认:", vm3.HeaderText);
    }

    [Fact]
    public void Vm_ApplyAllCommand_SelectsAllItems()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        // 先全部取消勾选
        foreach (var i in vm.Items) i.Selected = false;
        Assert.All(vm.Items, i => Assert.False(i.Selected));

        vm.ApplyAllCommand.Execute(null);

        Assert.All(vm.Items, i => Assert.True(i.Selected));
    }

    [Fact]
    public void Vm_KeepAllCommand_DeselectsAllItems()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        // 默认全勾选
        Assert.All(vm.Items, i => Assert.True(i.Selected));

        vm.KeepAllCommand.Execute(null);

        Assert.All(vm.Items, i => Assert.False(i.Selected));
    }

    [Fact]
    public void Vm_ToggleAllCommand_InvertsSelection()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        // 默认全勾选 → 反选后全不勾
        vm.ToggleAllCommand.Execute(null);
        Assert.All(vm.Items, i => Assert.False(i.Selected));
        // 再反选 → 恢复全勾
        vm.ToggleAllCommand.Execute(null);
        Assert.All(vm.Items, i => Assert.True(i.Selected));
    }

    [Fact]
    public void Vm_ConfirmCommand_SetsDecisions_TriggersCloseRequested()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        var closed = false;
        vm.CloseRequested += () => closed = true;

        // 取消第 2 项勾选
        vm.Items[1].Selected = false;
        vm.ConfirmCommand.Execute(null);

        Assert.True(closed);
        Assert.NotNull(vm.Decisions);
        Assert.Equal(3, vm.Decisions!.Count);

        // 第 1 项 Apply=true
        Assert.True(vm.Decisions[0].Apply);
        Assert.Equal("EnvsDir", vm.Decisions[0].Label);
        Assert.Equal(@"D:\old\Envs", vm.Decisions[0].CurrentValue);
        Assert.Equal(@"D:\new\Envs", vm.Decisions[0].RecommendedValue);

        // 第 2 项 Apply=false(被取消勾选)
        Assert.False(vm.Decisions[1].Apply);
        Assert.Equal("DefaultModelsDirectory", vm.Decisions[1].Label);

        // 第 3 项 Apply=true
        Assert.True(vm.Decisions[2].Apply);
    }

    [Fact]
    public void Vm_CancelCommand_SetsDecisionsNull_TriggersCloseRequested()
    {
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Null(vm.Decisions);
    }

    [Fact]
    public void Vm_Ctor_NullItems_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new PathMigrationConfirmViewModel(null!));
    }

    [Fact]
    public void Vm_Confirm_AllDeselected_WritesDecisionsWithAllApplyFalse()
    {
        // 极端场景:用户全不勾,直接 Confirm → Decisions 写出 3 条 Apply=false
        // (caller 看到 Apply=false 自然就跳过所有更新)
        var vm = new PathMigrationConfirmViewModel(ThreeSampleItems());
        vm.KeepAllCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(vm.Decisions);
        Assert.All(vm.Decisions!, d => Assert.False(d.Apply));
    }
}