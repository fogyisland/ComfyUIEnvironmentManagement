using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v1.0.0.x: env 行 ! 按钮 → 弹 dialog 列出当前 env 节点的启动状态。
/// Canonical pattern 跟 <see cref="InstallDialog"/> 一致:
/// ctor 收 VM → DataContext = vm → vm.CloseRequested += () => Close()。
/// caller 用 <c>ShowDialog()</c>。
/// </summary>
public partial class NodeStartupStatusDialog : Window
{
    public NodeStartupStatusDialog(NodeStartupStatusViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}