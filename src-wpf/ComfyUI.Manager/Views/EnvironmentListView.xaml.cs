using System;
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class EnvironmentListView : UserControl
{
    public EnvironmentListView() { InitializeComponent(); }

    /// <summary>
    /// 装依赖状态面板 ✕ 按钮:用户手动收起面板(失败/取消后面板持续可见)。
    /// v1.0.0.x #590:ConsolePanel.ConsoleCloseRequested 是 EventHandler 类型,
    /// 参数类型是 <see cref="EventArgs"/> 而非 <see cref="RoutedEventArgs"/>。
    /// </summary>
    private void OnRequirementsStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.RequirementsStatus?.Hide();
        }
    }

    /// <summary>
    /// v0.6.11+ T4:ComfyUI Manager 状态面板 ✕ 按钮 — 镜像 RequirementsStatus 同模式。
    /// 失败/取消后面板持续可见,用户手动收起。
    /// v1.0.0.x #590:ConsoleCloseRequested 是 EventHandler,签名用 EventArgs。
    /// </summary>
    private void OnComfyUiManagerStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.ComfyUiManagerStatus?.Hide();
        }
    }

    /// <summary>
    /// v1.0.0.x #577:本地常用节点安装状态面板 ✕ 按钮 — 镜像 ComfyUI Manager 同模式,
    /// 但批量 + 多阶段所以从不 auto-hide,完成 / 失败都等用户手动关(要看总结)。
    /// v1.0.0.x #590:ConsoleCloseRequested 是 EventHandler,签名用 EventArgs。
    /// </summary>
    private void OnLocalNodeInstallStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.LocalNodeInstallStatus?.Hide();
        }
    }

    /// <summary>
    /// v0.6.15.8 T6:节点管理 inline 面板 ✕ 按钮 — 走 CloseNodeManagementCommand
    /// 清空 EnvListVM.NodeManagement(VM 留在 _nodeMgmtCache 里保留状态)。
    /// v0.6.15.9:OnUpgradeNodesCloseClicked 删除 — 升级迁入节点管理面板行内,
    /// 不再需要单独的升级面板 close handler。
    /// 注意:这个 handler 是给外层 Border 内手写的 Button 用的(走 Click routed event),
    /// 保留 RoutedEventArgs 签名。
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
    /// v1.0.0.x #590:ConsoleCloseRequested 是 EventHandler,签名用 EventArgs。
    /// </summary>
    private void OnStartStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.CloseStartStatusPanel();
        }
    }

    /// <summary>
    /// v1.0.0.x #590:卸载基础环境状态面板 ✕ 按钮 — ConsolePanel.ConsoleCloseRequested → 调 VM.Hide。
    /// 旧版用 inline Button + Click routed event,新版本 console 抽取后改走 ConsoleCloseRequested(EventArgs)。
    /// </summary>
    private void OnBaseEnvUninstallStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.BaseEnvUninstallStatus?.Hide();
        }
    }

    /// <summary>
    /// v1.0.0.x:Forge BED inline 面板 ✕ 按钮 handler — 镜像 OnRequirementsStatusCloseClicked
    /// 同 pattern。失败/取消场景下面板不会 auto-hide,用户用 ✕ 手动收起。
    /// </summary>
    private void OnBaseEnvStatusCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.BaseEnvStatus?.Hide();
        }
    }

    /// <summary>
    /// v0.6.22.x 删:OnTemplateUpdateStatusCloseClicked handler — 模板更新状态面板
    /// 跟 UpdateTemplateCommand 一起移走至 MainViewModel,env-list 不再展示模板更新。
    /// </summary>
    // private void OnTemplateUpdateStatusCloseClicked(object sender, RoutedEventArgs e) { ... }   // removed v0.6.22.x
}
