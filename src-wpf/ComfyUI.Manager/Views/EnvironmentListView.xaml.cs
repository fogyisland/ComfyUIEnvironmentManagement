using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class EnvironmentListView : UserControl
{
    public EnvironmentListView() { InitializeComponent(); }

    /// <summary>
    /// 装依赖状态面板 ✕ 按钮:用户手动收起面板(失败/取消后面板持续可见)。
    /// </summary>
    private void OnRequirementsStatusCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.RequirementsStatus?.Hide();
        }
    }

    /// <summary>
    /// v0.6.11+ T4:ComfyUI Manager 状态面板 ✕ 按钮 — 镜像 RequirementsStatus 同模式。
    /// 失败/取消后面板持续可见,用户手动收起。
    /// </summary>
    private void OnComfyUiManagerStatusCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.ComfyUiManagerStatus?.Hide();
        }
    }

    /// <summary>
    /// v0.6.15.8 T6:节点管理 inline 面板 ✕ 按钮 — 走 CloseNodeManagementCommand
    /// 清空 EnvListVM.NodeManagement(VM 留在 _nodeMgmtCache 里保留状态)。
    /// </summary>
    private void OnNodeManagementCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.CloseNodeManagementCommand.Execute(null);
        }
    }

    /// <summary>
    /// v0.6.15.8 T6:升级节点 inline 面板 ✕ 按钮 — 走 CloseUpgradeNodesCommand
    /// 清空 EnvListVM.UpgradeNodes(VM 留在 _upgradeCache 里保留状态)。
    /// </summary>
    private void OnUpgradeNodesCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.CloseUpgradeNodesCommand.Execute(null);
        }
    }
}
