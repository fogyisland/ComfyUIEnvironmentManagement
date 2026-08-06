using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹非模态 About 对话框(独立窗口,不阻塞主窗口 — v0.6.5.21 hotfix)。
    /// Owner 通常 <c>Application.Current.MainWindow</c>(用于位置关联 + WindowStartupLocation=CenterOwner)。
    /// projectRoot 用于定位 <c>asset/receiveMark.jpg</c>(QR 单独从 DonateQrWindow 打开)。
    /// </summary>
    public static void Show(Window owner, string projectRoot)
    {
        var vm = new AboutDialogViewModel(projectRoot);
        var dlg = new AboutDialog
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        vm.OpenDonateQrRequested += (_, _) =>
        {
            // 在 AboutDialog 上点"查看赞助二维码"→ 弹独立 DonateQrWindow(也非模态)
            DonateQrWindow.Show(dlg, projectRoot);
        };
        dlg.Show();  // 非模态:不阻塞主窗口,用户可一边看 About 一边操作主界面
    }

    /// <summary>Hyperlink 点击 → 用默认浏览器开 URL。</summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
