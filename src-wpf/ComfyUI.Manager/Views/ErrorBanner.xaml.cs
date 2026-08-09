using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ComfyUI.Manager.Animations;

namespace ComfyUI.Manager.Views;

public partial class ErrorBanner : UserControl
{
    private Storyboard? _slideIn;

    public ErrorBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // v0.6.9 T9:slide-in 动画。G9 — IsAnimationEnabled=false 直接 return,RootBorder
        // 默认已是终态 (Opacity=1, Y=0),用户直接看到 banner 无视觉跳变。
        if (!MotionSettings.IsAnimationEnabled) return;
        _slideIn = BuildSlideStoryboard(-40d, 0d, 0d, 1d);
        _slideIn.Begin();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // 防止 Storyboard 时钟继续走(Window 关闭后离屏渲染)。
        _slideIn?.Stop();
    }

    /// <summary>
    /// 构造单一 slide-in Storyboard(从 fromY/opacityFrom 滑入到 0/1)。
    /// TranslateTransform 而非 Margin — Margin 动画触发 layout invalidate,
    /// RenderTransform 是 GPU 合成。Target 用 RootBorder 直接 element reference,
    /// 不依赖 NameScope.FindName(更可靠)。
    /// </summary>
    private Storyboard BuildSlideStoryboard(double fromY, double toY, double opacityFrom, double opacityTo)
    {
        var duration = MotionSettings.DurationSlideBanner;
        var sb = new Storyboard { Duration = duration };

        var translate = new DoubleAnimation(fromY, toY, duration);
        Storyboard.SetTarget(translate, RootBorder);
        Storyboard.SetTargetProperty(translate, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(translate);

        var fade = new DoubleAnimation(opacityFrom, opacityTo, duration);
        Storyboard.SetTarget(fade, RootBorder);
        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
        sb.Children.Add(fade);

        return sb;
    }
}