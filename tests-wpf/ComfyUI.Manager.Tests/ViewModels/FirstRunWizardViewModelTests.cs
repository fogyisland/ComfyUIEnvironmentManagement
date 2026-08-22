using System;
using System.IO;
using ComfyUI.Manager.ViewModels.FirstRunWizard;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class FirstRunWizardViewModelTests
{
    [Fact]
    public void InitialStep_IsWelcome()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        Assert.Equal(FirstRunWizardStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void Next_FromWelcome_GoesToPython()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test" };
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Python, vm.CurrentStep);
    }

    [Fact]
    public void Next_FromWelcome_Disabled_WhenInstallPathEmpty()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Back_FromPython_GoesToWelcome()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test" };
        vm.NextCommand.Execute(null);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void Finish_FiresCompleted()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test", PythonPath = "" };
        var fired = false;
        vm.Completed += () => fired = true;
        vm.NextCommand.Execute(null);  // to Python
        vm.NextCommand.Execute(null);  // to Confirm
        vm.FinishCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void Cancel_FiresCancelled()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        var fired = false;
        vm.Cancelled += () => fired = true;
        vm.CancelCommand.Execute(null);
        Assert.True(fired);
    }
}