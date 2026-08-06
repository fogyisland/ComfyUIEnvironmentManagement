using System.Linq;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// BaseEnvUninstallStatusViewModel 测试(v0.6.5.22 T3)。
/// 跟 RequirementsStatusViewModel 同模式单阶段 inline 状态面板,
/// 用于 env-list 操作列"卸载基础环境"按钮的内嵌 UI。
/// </summary>
public sealed class BaseEnvUninstallStatusViewModelTests
{
    [Fact]
    public void InitialState_IsVisibleFalseErrorNullLogEmpty()
    {
        var vm = new BaseEnvUninstallStatusViewModel();

        Assert.False(vm.IsVisible);
        Assert.Null(vm.Error);
        Assert.Empty(vm.LogLines);
    }

    [Fact]
    public void Begin_SetsIsVisibleTrueAndAddsStartLog()
    {
        var vm = new BaseEnvUninstallStatusViewModel();

        vm.Begin();

        Assert.True(vm.IsVisible);
        Assert.Null(vm.Error);
        Assert.Single(vm.LogLines);
        Assert.Equal("开始卸载基础环境...", vm.LogLines[0]);
    }

    [Fact]
    public void Complete_AppendsCompletionLog()
    {
        var vm = new BaseEnvUninstallStatusViewModel();
        vm.Begin();

        vm.Complete();

        Assert.Equal(2, vm.LogLines.Count);
        Assert.Equal("开始卸载基础环境...", vm.LogLines[0]);
        Assert.Equal("卸载完成 — env 可重新部署基础环境", vm.LogLines[1]);
    }

    [Fact]
    public void Fail_SetsErrorButStaysVisible()
    {
        var vm = new BaseEnvUninstallStatusViewModel();
        vm.Begin();

        vm.Fail("卸载异常:torch not found");

        Assert.Equal("卸载异常:torch not found", vm.Error);
        Assert.True(vm.IsVisible);
    }
}