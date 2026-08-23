using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views.TemplateManagement;

public partial class EditTemplateDialog : Window
{
    public EditTemplateDialog()
    {
        InitializeComponent();
        Loaded += EditTemplateDialog_Loaded;
    }

    // v1.0.0 T14: sync initial ComboBox selection from VM on load.
    private void EditTemplateDialog_Loaded(object sender, RoutedEventArgs e)
    {
        SyncSourceKindComboFromVm();
    }

    // v1.0.0 T14: map selected ComboBoxItem.Tag (TemplateSourceKind) -> vm.SourceKind.
    private void SourceKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not EditTemplateDialogViewModel vm) return;
        if (SourceKindCombo.SelectedItem is ComboBoxItem item && item.Tag is TemplateSourceKind kind)
        {
            vm.SourceKind = kind;
        }
    }

    private void SyncSourceKindComboFromVm()
    {
        if (DataContext is not EditTemplateDialogViewModel vm) return;
        var target = vm.WorkingConfig.SourceKind;
        foreach (var obj in SourceKindCombo.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is TemplateSourceKind kind && kind == target)
            {
                SourceKindCombo.SelectedItem = item;
                return;
            }
        }
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