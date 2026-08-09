// v0.6.9.3 T4:可复用 32x32 太阳/月亮图标按钮。IsChecked=true 显示太阳(代表当前是 Light),
// IsChecked=false 显示月亮(代表当前是 Dark)。点击 fire Command(参数 = 切后的 next-mode)。
// ToolTip 走 UserControl 基类自带 DependencyProperty,不重声明。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ComfyUI.Manager.Controls;

public partial class SunMoonIconButton : UserControl
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(
            nameof(IsChecked),
            typeof(bool),
            typeof(SunMoonIconButton),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(SunMoonIconButton));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
            nameof(CommandParameter),
            typeof(object),
            typeof(SunMoonIconButton));

    public SunMoonIconButton()
    {
        InitializeComponent();
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}