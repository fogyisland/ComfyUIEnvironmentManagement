using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvPickerDialogViewModelTests
{
    [Fact]
    public void Constructor_BindsEnvList()
    {
        var envs = new List<EnvOption>
        {
            new("env-1", "prod"),
            new("env-2", "dev"),
        };
        var vm = new EnvPickerDialogViewModel(envs);

        Assert.Equal(2, vm.Environments.Count);
        Assert.Equal("prod", vm.Environments[0].Name);
    }

    [Fact]
    public void OkCommand_FiresClosedWithSelectedEnv()
    {
        var envs = new List<EnvOption> { new("env-1", "prod"), new("env-2", "dev") };
        var vm = new EnvPickerDialogViewModel(envs);
        EnvOption? captured = null;
        vm.Closed += e => captured = e;
        vm.Selected = envs[1];

        vm.OkCommand.Execute(null);

        Assert.Equal("env-2", captured?.Id);
    }

    [Fact]
    public void CancelCommand_FiresClosedWithNull()
    {
        var envs = new List<EnvOption> { new("env-1", "prod") };
        var vm = new EnvPickerDialogViewModel(envs);
        EnvOption? captured = new("placeholder", "x");
        vm.Closed += e => captured = e;
        vm.Selected = envs[0];

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }
}