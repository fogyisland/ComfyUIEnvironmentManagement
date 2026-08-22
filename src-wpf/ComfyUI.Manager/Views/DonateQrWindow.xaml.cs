using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class DonateQrWindow : Window
{
    public DonateQrWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹非模态赞助二维码独立窗口 — v0.6.5.21 hotfix。
    /// Owner 通常 AboutDialog(从 AboutDialog 内"查看赞助二维码"按钮触发)或主窗口(从菜单触发)。
    /// projectRoot 用于定位 <c>assets/receiveMark.jpg</c>。
    /// </summary>
    public static void Show(Window owner, string projectRoot)
    {
        var vm = new DonateQrViewModel(projectRoot);
        var dlg = new DonateQrWindow
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        dlg.Loaded += (_, _) =>
        {
            if (vm.HasDonateImage)
            {
                dlg.DonateImage.Source = vm.CreateDonateImage();
            }
        };
        dlg.Show();
    }
}
