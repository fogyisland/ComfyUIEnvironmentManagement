// v0.6.21 T3:BindablePasswordBox 的 code-behind — 处理 template part (InnerPasswordBox /
// InnerTextBox) 跟 outer Password DP 的双向同步:
//   - OnApplyTemplate:挂事件 + 把当前 Password 同步到 inner。
//   - InnerPasswordBox.PasswordChanged → SetCurrentValue(PasswordProperty, inner.Password)。
//   - InnerTextBox.TextChanged → SetCurrentValue(PasswordProperty, inner.Text)。
// 用 SetCurrentValue 避免触发 OnPasswordChanged 形成循环(后者只写不读 inner)。
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Controls;

public partial class BindablePasswordBox
{
    private PasswordBox? _innerPasswordBox;
    private TextBox? _innerTextBox;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Unhook 老 template(控件可能被重用 / 重新 template)
        if (_innerPasswordBox is not null) _innerPasswordBox.PasswordChanged -= InnerPasswordBox_PasswordChanged;
        if (_innerTextBox is not null) _innerTextBox.TextChanged -= InnerTextBox_TextChanged;

        _innerPasswordBox = GetTemplateChild("InnerPasswordBox") as PasswordBox;
        _innerTextBox = GetTemplateChild("InnerTextBox") as TextBox;

        if (_innerPasswordBox is not null)
        {
            _innerPasswordBox.Password = Password;
            _innerPasswordBox.PasswordChanged += InnerPasswordBox_PasswordChanged;
        }
        if (_innerTextBox is not null)
        {
            _innerTextBox.Text = Password;
            _innerTextBox.TextChanged += InnerTextBox_TextChanged;
        }
    }

    private void InnerPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_innerPasswordBox is not null && Password != _innerPasswordBox.Password)
        {
            SetCurrentValue(PasswordProperty, _innerPasswordBox.Password);
        }
    }

    private void InnerTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_innerTextBox is not null && Password != _innerTextBox.Text)
        {
            SetCurrentValue(PasswordProperty, _innerTextBox.Text);
        }
    }
}