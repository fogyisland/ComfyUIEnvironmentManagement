using System.Collections.Specialized;
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v0.6.18 inline 批量更新 view。DataContext 由 MainViewModel.OpenBulkUpdate 设成
/// <see cref="BulkUpdateViewModel"/>,布局完全镜像 <see cref="EnvironmentListView"/>
/// (DockPanel + Top 工具栏 + Bottom 状态 + Middle 主区)。
///
/// v0.6.18.4:加 Console 面板 + code-behind 处理 ✕ 关闭 + ScrollViewer auto-scroll
/// (新行追加时 ScrollToEnd 让用户总看最新输出,免手动滚)。
/// </summary>
public partial class BulkUpdateView : UserControl
{
    public BulkUpdateView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookConsoleLog();
        Unloaded += OnUnloaded;
    }

    private BulkUpdateViewModel? _vm;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _consoleHandler;

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // 切换 VM 时解绑旧 VM 的 ConsoleLog.CollectionChanged,避免内存泄漏 +
        // 旧 VM 残留事件触发旧 ScrollViewer 滚。
        UnhookConsoleLog();
        _vm = e.NewValue as BulkUpdateViewModel;
        HookConsoleLog();
    }

    private void HookConsoleLog()
    {
        if (_vm is null || _consoleHandler is not null) return;
        _consoleHandler = (_, _) =>
        {
            // ConsoleScrollViewer 在 Loaded 后才存在,守卫一下。
            if (ConsoleScrollViewer is null) return;
            ConsoleScrollViewer.ScrollToEnd();
        };
        _vm.ConsoleLog.CollectionChanged += _consoleHandler;
    }

    private void UnhookConsoleLog()
    {
        if (_vm is null || _consoleHandler is null) return;
        _vm.ConsoleLog.CollectionChanged -= _consoleHandler;
        _consoleHandler = null;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnhookConsoleLog();
    }

    /// <summary>v0.6.18.4:Console 面板 ✕ 关闭按钮 — 清空日志 + 触发 IsConsoleVisible 重算。</summary>
    private void OnConsoleCloseClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ClearConsoleLog();
    }
}
