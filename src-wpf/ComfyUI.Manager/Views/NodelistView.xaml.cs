using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace ComfyUI.Manager.Views;

public partial class NodelistView : UserControl
{
    public NodelistView()
    {
        InitializeComponent();
    }

    /// <summary>v1.0.0.x T43f+user:右边详情卡的「🔗 仓库地址」超链接 ——
    /// 调 OS 默认浏览器打开 html_url。Hyperlink.NavigateUri 不可直接
    /// 跨进程跳转(在 WPF 里会触发 pack URI 解析失败),必须 RequestNavigate
    /// + Process.Start 走 UseShellExecute。</summary>
    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch
        {
            // URL 格式异常(空 / null / 非法)→ 静默。
            // 真实场景下 DetailRow.HtmlUrl 来自 raw_json.github/html_url,极少异常。
        }
    }
}