using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class NodeInstallDiffWarningDialogTests
{
    [Fact]
    public void Vm_Ctor_PopulatesWarnings()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
            new DiffEntry("foo", DiffCategory.New, null, ">=1.0"),
            new DiffEntry("bar", DiffCategory.Conflict, "3.0", "<1"),
        });

        var vm = new NodeInstallDiffWarningViewModel(report, "my-node", "my-env");

        Assert.Equal(2, vm.Warnings.Count); // 只 Downgrade + Conflict
        Assert.Equal("torch", vm.Warnings[0].Name);
        Assert.Equal("bar", vm.Warnings[1].Name);
        Assert.Equal("my-node", vm.NodePackage);
        Assert.Equal("my-env", vm.EnvName);
    }

    [Fact]
    public void Vm_CancelCommand_SetsProceedFalse_TriggersCloseRequested()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
        });
        var vm = new NodeInstallDiffWarningViewModel(report, "n", "e");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Proceed);
        Assert.True(fired);
    }

    [Fact]
    public void Vm_ProceedCommand_SetsProceedTrue_TriggersCloseRequested()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
        });
        var vm = new NodeInstallDiffWarningViewModel(report, "n", "e");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.ProceedCommand.Execute(null);

        Assert.True(vm.Proceed);
        Assert.True(fired);
    }
}