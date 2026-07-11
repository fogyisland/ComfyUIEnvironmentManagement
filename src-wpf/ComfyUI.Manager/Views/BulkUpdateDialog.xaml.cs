using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BulkUpdateDialog : Window
{
    public BulkUpdateDialog(BulkUpdateDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static void Show(BulkUpdateDialogViewModel vm, Window? owner = null)
    {
        var dlg = new BulkUpdateDialog(vm)
        {
            Owner = owner ?? Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }
}
