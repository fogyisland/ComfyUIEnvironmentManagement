using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v0.6.18 inline 批量更新 view。DataContext 由 MainViewModel.OpenBulkUpdate 设成
/// <see cref="BulkUpdateViewModel"/>,布局完全镜像 <see cref="EnvironmentListView"/>
/// (DockPanel + Top 工具栏 + Bottom 状态 + Middle 主区)。无 code-behind 行为 —
/// 状态机全在 VM,UI 只负责展示。
/// </summary>
public partial class BulkUpdateView : UserControl
{
    public BulkUpdateView()
    {
        InitializeComponent();
    }
}
