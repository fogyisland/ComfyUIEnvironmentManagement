using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class EnvStartStatusViewModelTests
{
    [Fact]
    public void Initial_State_IsHiddenAndNoError()
    {
        var vm = new EnvStartStatusViewModel();
        Assert.False(vm.IsVisible);
        Assert.Equal(-1, vm.CurrentStageIndex);
        Assert.Equal("", vm.CurrentStageText);
        Assert.Null(vm.Error);
        Assert.Empty(vm.LogLines);
        Assert.False(vm.IsComplete);
        Assert.Equal(new[] { "激活本地环境", "在环境中启用", "完成" }, vm.Stages.ToArray());
    }

    [Fact]
    public void Begin_SetsIsVisibleAndStageIndexZero()
    {
        var vm = new EnvStartStatusViewModel();
        vm.Begin();
        Assert.True(vm.IsVisible);
        Assert.Equal(0, vm.CurrentStageIndex);
        Assert.Equal("激活本地环境", vm.CurrentStageText);
    }

    [Fact]
    public void AdvanceTo_UpdatesCurrentStageIndex()
    {
        var vm = new EnvStartStatusViewModel();
        vm.Begin();
        vm.AdvanceTo("在环境中启用");
        Assert.Equal(1, vm.CurrentStageIndex);
        Assert.Equal("在环境中启用", vm.CurrentStageText);
    }

    [Fact]
    public void Complete_AdvancesToCompletionStage()
    {
        var vm = new EnvStartStatusViewModel();
        vm.Begin();
        vm.Complete();
        Assert.Equal(2, vm.CurrentStageIndex);
        Assert.Equal("完成", vm.CurrentStageText);
        Assert.True(vm.IsComplete);
    }

    [Fact]
    public void Fail_SetsErrorAndKeepsPanelVisible()
    {
        var vm = new EnvStartStatusViewModel();
        vm.Begin();
        vm.Fail("启动失败:端口 8188 已占用");
        Assert.Equal("启动失败:端口 8188 已占用", vm.Error);
        Assert.True(vm.IsVisible);  // 不收起,等用户关
    }

    [Fact]
    public void Report_StagePrefixAdvances_PlainTextAppendsLog()
    {
        var vm = new EnvStartStatusViewModel();
        vm.Begin();
        vm.Report("stage:在环境中启用");
        Assert.Equal(1, vm.CurrentStageIndex);
        vm.Report("ComfyUI Server Starting...");
        vm.Report("Loading custom nodes");
        Assert.Equal(2, vm.LogLines.Count);
        Assert.Equal("ComfyUI Server Starting...", vm.LogLines[0]);
        Assert.Equal("Loading custom nodes", vm.LogLines[1]);
    }
}
