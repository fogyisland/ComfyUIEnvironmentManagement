// v0.6.9.3 T3:可复用的透明齿轮设置按钮。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ComfyUI.Manager.Controls;

public partial class GearIconButton : UserControl
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(GearIconButton));

    public GearIconButton()
    {
        InitializeComponent();
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
}
