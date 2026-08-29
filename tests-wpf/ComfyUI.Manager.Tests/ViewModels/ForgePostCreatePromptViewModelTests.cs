using System;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-08-29):ForgePostCreatePromptViewModel 测试 ——
/// 镜像 NodeInstallDiffWarningDialogTests 模式。验证 Choice 写值 + CloseRequested 触发。
/// </summary>
public class ForgePostCreatePromptViewModelTests
{
    [Fact]
    public void Vm_Ctor_PopulatesEnvNameAndMessage()
    {
        var vm = new ForgePostCreatePromptViewModel("forge-foo");

        Assert.Equal("forge-foo", vm.EnvName);
        Assert.Contains("forge-foo", vm.Message);
        Assert.Contains("Forge 模型目录", vm.Message);
        Assert.Contains("LoRA", vm.Message);
        Assert.Contains("VAE", vm.Message);
    }

    [Fact]
    public void Vm_Ctor_NullEnvName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ForgePostCreatePromptViewModel(null!));
    }

    [Fact]
    public void Vm_InitialChoice_IsNull()
    {
        var vm = new ForgePostCreatePromptViewModel("forge-foo");

        Assert.Null(vm.Choice);
    }

    [Fact]
    public void Vm_GoToSettingsCommand_SetsChoiceToSettings_TriggersCloseRequested()
    {
        var vm = new ForgePostCreatePromptViewModel("forge-foo");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.GoToSettingsCommand.Execute(null);

        Assert.Equal("settings", vm.Choice);
        Assert.True(fired);
    }

    [Fact]
    public void Vm_SkipCommand_SetsChoiceToSkip_TriggersCloseRequested()
    {
        var vm = new ForgePostCreatePromptViewModel("forge-foo");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.SkipCommand.Execute(null);

        Assert.Equal("skip", vm.Choice);
        Assert.True(fired);
    }
}
