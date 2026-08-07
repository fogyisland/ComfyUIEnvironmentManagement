using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class BaseEnvProfilePickerViewTests
{
    private static BaseEnvProfile Profile(string cuda) =>
        new() { Id = $"torch==2.4.1+{cuda}", TorchVersion = "2.4.1", CudaVersion = cuda, CudaVariant = cuda };

    [Fact]
    public void SelectionMode_Set_UpdatesListBoxSelectionMode()
    {
        StaFact.RunOnSTA(() =>
        {
            var view = new BaseEnvProfilePickerView { SelectionMode = PickerSelectionMode.Single };
            Assert.Equal(SelectionMode.Single, view.ProfileListBox.SelectionMode);
            view.SelectionMode = PickerSelectionMode.Multi;
            Assert.Equal(SelectionMode.Extended, view.ProfileListBox.SelectionMode);
        });
    }

    [Fact]
    public void SelectedProfiles_Set_SelectsMatchingItemsInListBox()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = new[] { Profile("cu118"), Profile("cu121"), Profile("cu126") };
            var view = new BaseEnvProfilePickerView
            {
                ViewModel = new BaseEnvProfilePickerViewModel(profiles, null, PickerSelectionMode.Multi),
            };
            view.SelectedProfiles = new[] { profiles[0], profiles[2] };
            Assert.Equal(2, view.ProfileListBox.SelectedItems.Count);
            Assert.Contains(profiles[0], view.ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
            Assert.Contains(profiles[2], view.ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
        });
    }

    [Fact]
    public void SelectedProfiles_Get_ReturnsListBoxSelection()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = new[] { Profile("cu118"), Profile("cu121"), Profile("cu126") };
            var view = new BaseEnvProfilePickerView
            {
                ViewModel = new BaseEnvProfilePickerViewModel(profiles, null, PickerSelectionMode.Multi),
            };
            view.ProfileListBox.SelectedItems.Add(profiles[1]);
            view.ProfileListBox.SelectedItems.Add(profiles[2]);
            var selected = view.SelectedProfiles;
            Assert.Equal(2, selected.Count);
            Assert.Contains(profiles[1], selected);
            Assert.Contains(profiles[2], selected);
        });
    }
}
