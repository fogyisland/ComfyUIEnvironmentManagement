using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmDialog(ConfirmDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += result =>
        {
            Result = result;
            DialogResult = result;
            Close();
        };
    }

    public static bool Show(string message, string confirm = "确认",
        string cancel = "取消")
    {
        var vm = new ConfirmDialogViewModel(message, confirm, cancel);
        var dlg = new ConfirmDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
}