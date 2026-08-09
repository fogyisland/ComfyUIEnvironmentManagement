using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class NodeInstallDiffWarningDialog : Window
{
    public NodeInstallDiffWarningDialog(NodeInstallDiffWarningViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}