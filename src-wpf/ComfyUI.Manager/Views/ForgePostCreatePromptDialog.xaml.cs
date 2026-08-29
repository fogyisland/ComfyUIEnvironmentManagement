using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v1.0.0.x (2026-08-29):Forge env 创建成功后的「去设置 LoRA/VAE 路径」提示框。
/// 镜像 <see cref="NodeInstallDiffWarningDialog"/> canonical pattern:
/// <c>DataContext = vm; vm.CloseRequested += Close</c>。
/// </summary>
public partial class ForgePostCreatePromptDialog : Window
{
    public ForgePostCreatePromptDialog(ForgePostCreatePromptViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}
