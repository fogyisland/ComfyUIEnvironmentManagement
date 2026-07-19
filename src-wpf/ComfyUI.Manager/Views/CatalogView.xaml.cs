using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace ComfyUI.Manager.Views;

public partial class CatalogView : UserControl
{
    public CatalogView() { InitializeComponent(); }

    private void OnRepoLinkClick(object sender, RequestNavigateEventArgs e)
    {
        var url = e.Uri?.ToString();
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* swallow — 用户点不动链接不影响主流程 */ }
        e.Handled = true;
    }
}
