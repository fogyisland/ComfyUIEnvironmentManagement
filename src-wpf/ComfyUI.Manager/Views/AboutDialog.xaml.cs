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
    /// 弹模态 About 对话框。Owner 通常 <c>Application.Current.MainWindow</c>。
    /// v0.6.5.21 spec G9。projectRoot 用于定位 <c>assets/wechat-donate.png</c>。
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
        dlg.Loaded += (_, _) =>
        {
            // UI 线程同步创建 BitmapImage,缺位 null → Image 隐藏(Visibility 已经按 HasDonateImage 走)
            if (vm.HasDonateImage)
            {
                dlg.DonateImage.Source = vm.CreateDonateImage();
            }
        };
        dlg.ShowDialog();
    }

    /// <summary>Hyperlink 点击 → 用默认浏览器开 URL。</summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
