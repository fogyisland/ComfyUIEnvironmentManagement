using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class HistoryDialog : Window
{
    public HistoryDialog(HistoryDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}