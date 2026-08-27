using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views.TemplateManagement;

public partial class TemplateManagementView : UserControl
{
    public TemplateManagementView()
    {
        InitializeComponent();
        DataContextChanged += TemplateManagementView_DataContextChanged;
    }

    private TemplateManagementViewModel? _vm;

    // v1.0.0 hotfix: subscribe to TemplateManagementViewModel.ShowEditDialogRequested
    // so AddCommand / EditCommand actually open the EditTemplateDialog window.
    // Without this the VM's event fires with no listeners and the click no-ops.
    private void TemplateManagementView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.ShowEditDialogRequested -= ShowEditDialog;
        _vm = e.NewValue as TemplateManagementViewModel;
        if (_vm != null) _vm.ShowEditDialogRequested += ShowEditDialog;
    }

    private void ShowEditDialog(EditTemplateDialogViewModel editVm)
    {
        var dlg = new EditTemplateDialog { DataContext = editVm };
        dlg.ShowDialog();
    }

    // v1.0.0.x #590:Console ✕ → 调 VM.ClearConsoleLog() 清空内容 + 设 _userHiddenConsole=true,
    // 下次新 run 复位 false 时面板自动重新出现。Auto-scroll + hook/unhook 都搬进 ConsolePanel 内部。
    private void OnConsoleCloseRequested(object? sender, System.EventArgs e)
    {
        if (DataContext is TemplateManagementViewModel vm)
        {
            vm.ClearConsoleLog();
        }
    }
}