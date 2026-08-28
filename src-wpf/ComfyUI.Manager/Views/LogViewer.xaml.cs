using System;
using System.Windows;
using System.Windows.Controls;
namespace ComfyUI.Manager.Views;
public partial class LogViewer : UserControl
{
    public LogViewer() { InitializeComponent(); }

    /// <summary>
    /// v1.0.0.x #590:ConsolePanel 自带 ✕ 按钮 raise 此事件 — 关 dialog(window)而不是仅清内容。
    /// 沿 Dialog/Window tree 找最近的 Window 并 Close()。
    /// ConsoleCloseRequested 是 EventHandler(非 RoutedEvent),参数类型用 EventArgs。
    /// </summary>
    private void OnConsoleCloseClicked(object sender, EventArgs e)
    {
        var win = Window.GetWindow(this);
        win?.Close();
    }
}
