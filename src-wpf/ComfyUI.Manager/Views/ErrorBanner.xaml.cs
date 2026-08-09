using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ComfyUI.Manager.Animations;

namespace ComfyUI.Manager.Views;

public partial class ErrorBanner : UserControl
{
    private Storyboard? _slideIn;
    private Storyboard? _slideOut;

    public ErrorBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _slideIn = (Storyboard)Resources["SlideInStoryboard"];
        _slideOut = (Storyboard)Resources["SlideOutStoryboard"];

        // v0.6.9 T9:slide-in 动画。IsAnimationEnabled=false 直接 return — RootBorder 默认
        // 已是终态(Opacity=1, Y=0),用户直接看到 banner,无视觉跳变。
        if (MotionSettings.IsAnimationEnabled) _slideIn?.Begin();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // 防止 Storyboard 时钟继续走(Window 关闭后离屏渲染)。
        _slideIn?.Stop();
        _slideOut?.Stop();
    }
}