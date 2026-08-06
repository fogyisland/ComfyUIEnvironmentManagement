using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BaseEnvProfilePickerViewModelTests
{
    private static BaseEnvProfile Profile(string torch, string cuda = "cu118") =>
        new() { Id = $"torch=={torch}+{cuda}", TorchVersion = torch, CudaVersion = cuda, CudaVariant = cuda };

    private static PyTorchVersionEntry Entry(string version, bool nightly = false) =>
        new() { Version = version, IsNightly = nightly, DisplayName = nightly ? "PyTorch Nightly" : $"PyTorch {version}" };

    [Fact]
    public void Constructor_Multi_InitializesProfilesFromInput()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        Assert.Equal(PickerSelectionMode.Multi, vm.SelectionMode);
        Assert.NotEmpty(vm.Versions);
        Assert.NotEmpty(vm.Profiles);
    }

    [Fact]
    public void Constructor_Single_PreselectsDefault()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.Single(vm.SelectedProfiles);
        Assert.Equal(profiles[0], vm.SelectedProfiles[0]);
    }

    [Fact]
    public void SelectedVersion_Changes_FiltersProfiles()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.5.0", "cu118") };
        var versions = new[] { Entry("2.4.1"), Entry("2.5.0") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        vm.SelectedVersion = versions[1];
        Assert.Single(vm.Profiles);
        Assert.Equal("2.5.0", vm.Profiles[0].TorchVersion);
    }

    [Fact]
    public void SelectedVersion_Changes_ClearsSelectedProfiles()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.5.0", "cu118") };
        var versions = new[] { Entry("2.4.1"), Entry("2.5.0") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Multi);
        Assert.Single(vm.SelectedProfiles);
        vm.SelectedVersion = versions[1];
        Assert.Empty(vm.SelectedProfiles);
    }

    [Fact]
    public void Constructor_EmptyProfiles_DoesNotThrow()
    {
        var vm = new BaseEnvProfilePickerViewModel(Array.Empty<BaseEnvProfile>(), preselected: null, PickerSelectionMode.Single);
        Assert.Empty(vm.Profiles);
        Assert.Empty(vm.Versions);
        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedProfiles_Set_NotifiesBinding()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedProfiles)) raised = true; };
        vm.SelectedProfiles = new[] { profiles[1] };
        Assert.True(raised);
    }

    [Fact]
    public void PickerMode_Multi_OkReturnsAllSelected()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        vm.SelectedProfiles = profiles;
        Assert.True(vm.OkCommand.CanExecute(null));
        vm.OkCommand.Execute(null);
        Assert.Equal(profiles, vm.Result);
    }

    [Fact]
    public void PickerMode_Single_OkReturnsFirstOrNull()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.True(vm.OkCommand.CanExecute(null));
        vm.OkCommand.Execute(null);
        Assert.NotNull(vm.Result);
        Assert.Single(vm.Result!);

        vm.SelectedProfiles = Array.Empty<BaseEnvProfile>();
        Assert.False(vm.OkCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void OkCommand_CanExecute_Multi_RequiresAtLeastOne()
    {
        var profiles = new[] { Profile("2.4.1", "cu118") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        Assert.False(vm.OkCommand.CanExecute(null));
        vm.SelectedProfiles = profiles;
        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void SelectionMode_Single_SetMoreThanOne_Throws()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.Throws<ArgumentException>(() => vm.SelectedProfiles = profiles);
    }
}