using System.Windows;
using Microsoft.Win32;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views.TemplateManagement;

public partial class EditTemplateDialog : Window
{
    public EditTemplateDialog()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EditTemplateDialogViewModel vm && vm.SaveCommand.CanExecute(null))
        {
            vm.SaveCommand.Execute(null);
            DialogResult = vm.AppliedToSettings;
            if (DialogResult == true) Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EditTemplateDialogViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
        DialogResult = false;
        Close();
    }

    private void BrowseLocalSourceDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EditTemplateDialogViewModel vm) return;
        var dlg = new OpenFolderDialog
        {
            Title = "选择模板源目录",
            InitialDirectory = vm.WorkingConfig.LocalSourceDir,
        };
        if (dlg.ShowDialog(this) == true)
        {
            // T10 R1: write through the proxy setter so CanSave + SaveCommand.RaiseCanExecuteChanged
            // both fire. Writing WorkingConfig.LocalSourceDir directly bypasses INPC and leaves
            // the Save button visually disabled.
            vm.LocalSourceDir = dlg.FolderName;
        }
    }
}
