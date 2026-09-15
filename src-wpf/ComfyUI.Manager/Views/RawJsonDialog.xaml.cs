using System.Windows;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v1.0.0.x (2026-09-15) T43h+user:debug panel 用的只读 JSON viewer。
/// NodelistViewModel 「📄 查看完整 GitHub API 响应」按钮调用 ——
/// new RawJsonDialog(rawJson).ShowDialog(),ShowDialog 完释放,无 ViewModel 复杂度。
///
/// 设计要点:
/// - 无 ViewModel:JSON 通过 ctor 一次性传入,TextBox 直接显示(只读 + 等宽字体)
/// - 关闭按钮 + IsCancel=True(ESC 也关闭)— 标准 dialog 模式
/// - Consolas 字体 + 双向滚动 + 不自动换行(JSON 保留原始结构)
/// - TextBox IsReadOnly 但支持文本选择 + Ctrl+C 复制(用户可复制到 clipboard)
/// </summary>
public partial class RawJsonDialog : Window
{
    public RawJsonDialog(string rawJson)
    {
        InitializeComponent();
        JsonTextBox.Text = string.IsNullOrWhiteSpace(rawJson) ? "(空)" : rawJson;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
