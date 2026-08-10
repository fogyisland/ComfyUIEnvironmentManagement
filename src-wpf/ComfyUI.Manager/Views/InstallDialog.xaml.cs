using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class InstallDialog : Window
{
    public InstallDialog(InstallDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }

    /// <summary>
    /// Show(envRepo, nodeOps, entry, preselectedEnvId, preselectedTag):弹 InstallDialog,
    /// preselectedEnvId 非空时默认选中该 env,空时选第一个 env。
    /// preselectedTag(v0.6.11 T3):caller 显式选中的 GitHub tag,装完 git checkout 钉到该版本。
    /// 调用方提供 envRepo + nodeOps(由 App.xaml.cs 统一构造,跟其他 view 共享同一份)。
    /// </summary>
    public static void Show(
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogEntry entry,
        string? preselectedEnvId = null,
        string? preselectedTag = null)
    {
        var vm = new InstallDialogViewModel(envRepo, nodeOps, entry, preselectedEnvId, preselectedTag);
        var dlg = new InstallDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }
}
