using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class EnvironmentListView : UserControl
{
    public EnvironmentListView() { InitializeComponent(); }

    /// <summary>
    /// 装依赖状态面板 ✕ 按钮:用户手动收起面板(失败/取消后面板持续可见)。
    /// </summary>
    private void OnRequirementsStatusCloseClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.EnvironmentListViewModel vm)
        {
            vm.RequirementsStatus?.Hide();
        }
    }
}
