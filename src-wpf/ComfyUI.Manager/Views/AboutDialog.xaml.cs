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
    /// 弹非模态 "关于系统" 对话框 — v1.0.0 拆分:只剩系统级信息。
    /// Owner 通常 <c>Application.Current.MainWindow</c>(用于位置关联 + WindowStartupLocation=CenterOwner)。
    /// 二维码 / 课程改独立窗口,见 <see cref="DonateQrWindow"/> / <see cref="ComfyUIGroupQrWindow"/> /
    /// <see cref="ComfyUICoursesWindow"/> — 这几个由主菜单顶级 dropdown 各自触发,不再从此对话框跳。
    ///
    /// projectRoot 参数保留以兼容旧 Show 调用位 — 当前 ctor 已不再读它。
    /// </summary>
    public static void Show(Window owner, string projectRoot)
    {
        _ = projectRoot;   // v1.0.0:VM 不再需要 projectRoot,显式丢弃保持 API 兼容。
        var vm = new AboutDialogViewModel(projectRoot);
        var dlg = new AboutDialog
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        dlg.Show();  // 非模态:不阻塞主窗口
    }

    /// <summary>Hyperlink 点击 → 用默认浏览器开 URL。</summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
