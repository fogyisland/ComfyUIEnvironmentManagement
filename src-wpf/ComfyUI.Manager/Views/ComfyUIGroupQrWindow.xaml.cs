using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class ComfyUIGroupQrWindow : Window
{
    public ComfyUIGroupQrWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹非模态 ComfyUI 技术组二维码独立窗口 — v1.0.0。
    /// Owner 通常 AboutDialog(从 AboutDialog 内"查看ComfyUI技术组"按钮触发)或主窗口。
    /// projectRoot 用于定位 <c>assets/wechatgroup.png</c>。
    /// </summary>
    public static void Show(Window owner, string projectRoot)
    {
        var vm = new ComfyUIGroupQrViewModel(projectRoot);
        var dlg = new ComfyUIGroupQrWindow
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        dlg.Loaded += (_, _) =>
        {
            if (vm.HasGroupImage)
            {
                dlg.GroupImage.Source = vm.CreateGroupImage();
            }
        };
        dlg.Show();
    }
}
