using System.Linq;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvView : UserControl
{
    private BaseEnvViewModel? _vm;

    public BaseEnvView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _vm = DataContext as BaseEnvViewModel;
            if (_vm is not null) await _vm.LoadAsync();
        };
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SetSelectedProfiles(ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
    }

    private void OnEnvSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SetSelectedEnvIds(EnvListBox.SelectedItems.Cast<EnvModel>());
    }
}