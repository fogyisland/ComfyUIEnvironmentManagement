using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v0.6.18 inline 批量更新 view。DataContext 由 MainViewModel.OpenBulkUpdate 设成
/// <see cref="BulkUpdateViewModel"/>,布局完全镜像 <see cref="EnvironmentListView"/>
/// (DockPanel + Top 工具栏 + Bottom 状态 + Middle 主区)。
///
/// v1.0.0.x #590:Console 面板抽取到 <see cref="ComfyUI.Manager.Controls.ConsolePanel"/>,
/// auto-scroll 跟 hook/unhook 都搬进 UserControl 内部。View 只剩
/// <see cref="OnConsoleCloseRequested"/> 处理 ✕ → 调 VM.ClearConsoleLog。
/// </summary>
public partial class BulkUpdateView : UserControl
{
    private BulkUpdateViewModel? _vm;

    public BulkUpdateView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => _vm = e.NewValue as BulkUpdateViewModel;
    }

    /// <summary>v1.0.0.x #590:Console ✕ 触发 — 清空日志 + 触发 IsConsoleVisible 重算。</summary>
    private void OnConsoleCloseRequested(object? sender, System.EventArgs e)
    {
        _vm?.ClearConsoleLog();
    }
}