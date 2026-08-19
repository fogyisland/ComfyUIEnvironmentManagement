// v0.6.21 T3:WPF PasswordBox 不暴露 Password 为 DependencyProperty(安全原因 — 防止
// 密码出现在 DP 快照 / 数据绑定 dump 里)。本 custom control 包 PasswordBox 提供可绑定
// 的 Password DP,加 IsPasswordRevealed DP 供模板切换(PasswordBox ↔ TextBox)实现
// 👁 toggle。模板在 BindablePasswordBox.xaml,template part 同步逻辑在 .xaml.cs。
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Controls;

public partial class BindablePasswordBox : Control, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            nameof(Password),
            typeof(string),
            typeof(BindablePasswordBox),
            new FrameworkPropertyMetadata(
                "",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordChanged));

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    public static readonly DependencyProperty IsPasswordRevealedProperty =
        DependencyProperty.Register(
            nameof(IsPasswordRevealed),
            typeof(bool),
            typeof(BindablePasswordBox),
            new PropertyMetadata(false, OnIsPasswordRevealedChanged));

    public bool IsPasswordRevealed
    {
        get => (bool)GetValue(IsPasswordRevealedProperty);
        set => SetValue(IsPasswordRevealedProperty, value);
    }

    public void RevealPassword() => IsPasswordRevealed = true;
    public void HidePassword() => IsPasswordRevealed = false;

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Template part hook 在 .xaml.cs 里读这个 DP 并 forward 到 inner PasswordBox.Password;
        // 反向 inner PasswordBox.PasswordChanged → SetCurrentValue 写回 DP,避免循环触发。
        // 这里只负责通知 INotifyPropertyChanged 订阅者(测试用)。
        if (d is BindablePasswordBox box)
        {
            box.PropertyChanged?.Invoke(box, new PropertyChangedEventArgs(nameof(Password)));
        }
    }

    private static void OnIsPasswordRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BindablePasswordBox box)
        {
            box.PropertyChanged?.Invoke(box, new PropertyChangedEventArgs(nameof(IsPasswordRevealed)));
        }
    }
}