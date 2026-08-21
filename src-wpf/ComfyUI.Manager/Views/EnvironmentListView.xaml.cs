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
    /// v0.6.15.9:OnUpgradeNodesCloseClicked 删除 — 升级迁入节点管理面板行内,
    /// 不再需要单独的升级面板 close handler。
    /// </summary>
    private void OnNodeManagementCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.CloseNodeManagementCommand.Execute(null);
        }
    }

    /// <summary>
    /// v0.6.17:启动状态面板 ✕ 按钮 — 调 <see cref="EnvironmentListViewModel.CloseStartStatusPanel"/>
    /// 隐面板但 dict 条目留着(用户随时可通过 env 行 port 旁的小图标按钮重新打开)。
    /// v0.6.17.1:入口从独立"启动控制台"按钮改成 port 9000 旁的小图标(更紧凑)。
    /// </summary>
    private void OnStartStatusCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.CloseStartStatusPanel();
        }
    }

    /// <summary>
    /// v0.6.22.x 删:OnTemplateUpdateStatusCloseClicked handler — 模板更新状态面板
    /// 跟 UpdateTemplateCommand 一起移走至 MainViewModel,env-list 不再展示模板更新。
    /// </summary>
    // private void OnTemplateUpdateStatusCloseClicked(object sender, RoutedEventArgs e) { ... }   // removed v0.6.22.x
}
