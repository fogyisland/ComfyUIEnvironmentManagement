using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ComfyUI.Manager.Animations;

namespace ComfyUI.Manager.Behaviors;

/// <summary>
/// v0.6.9 T8:Button 点击 ripple 动效。给 ButtonBase 派生控件加
/// <c>behaviors:RippleBehavior.IsEnabled="True"</c> 即可启用。
///
/// <para>
/// 工作机制:
/// <list type="bullet">
/// <item>OnIsEnabledChanged hook <c>PreviewMouseLeftButtonDown</c>,在鼠标点击位置插入 Ellipse</item>
/// <item>Ellipse Fill 用 <see cref="Brush"/> 复制(不冻结),改 Opacity 不会污染 shared brush</item>
/// <item>Ellipse 用 <c>RenderTransform = ScaleTransform</c> 缩放 0→1,加 Opacity 0.5→0 淡出</item>
/// <item>Storyboard.Completed remove Ellipse,不留视觉残留</item>
/// <item><see cref="MotionSettings.IsAnimationEnabled"/> = false 直接 return,不动 DOM</item>
/// </list>
/// </para>
///
/// <para>
/// MaterialButton style 必须提供 <c>RippleOverlay</c> 名 Canvas(详见 Theme.xaml),
/// 没有它 ripple 无挂载点。
/// </para>
/// </summary>
public static class RippleBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(RippleBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    // 每个 ButtonBase 实例的 overlay 缓存(避免反复 FindName)
    private static readonly DependencyProperty RippleOverlayProperty =
        DependencyProperty.RegisterAttached(
            "RippleOverlay", typeof(Panel), typeof(RippleBehavior),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase btn) return;
        if ((bool)e.NewValue) Attach(btn);
        else Detach(btn);
    }

    private static void Attach(ButtonBase btn)
    {
        // 等 template apply 后再 hook(Loaded 触发 ApplyTemplate 完成)
        btn.Loaded -= OnLoaded;
        btn.Loaded += OnLoaded;
        if (btn.IsLoaded) EnsureOverlay(btn);

        btn.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        btn.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void Detach(ButtonBase btn)
    {
        btn.Loaded -= OnLoaded;
        btn.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase btn) EnsureOverlay(btn);
    }

    private static void EnsureOverlay(ButtonBase btn)
    {
        var root = btn.Template?.FindName("RippleOverlay", btn) as Panel;
        if (root is null)
        {
            // MaterialButton style 还没 apply,force 一次
            btn.ApplyTemplate();
            root = btn.Template?.FindName("RippleOverlay", btn) as Panel;
        }
        btn.SetValue(RippleOverlayProperty, root);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ButtonBase btn) return;
        if (!MotionSettings.IsAnimationEnabled) return;
        var root = btn.GetValue(RippleOverlayProperty) as Panel;
        if (root is null) return;

        var pt = e.GetPosition(root);
        var ellipse = MakeEllipse(pt, btn);
        ellipse.RenderTransform = new ScaleTransform(0, 0);
        root.Children.Add(ellipse);
        AnimateRipple(ellipse, () => root.Children.Remove(ellipse));
    }

    private static Ellipse MakeEllipse(Point pt, ButtonBase btn)
    {
        var size = Math.Max(btn.ActualWidth, btn.ActualHeight) * 2;

        // 实例化新 brush,不冻结。共享的 PrimaryBrush 是 frozen(freeze 后改 Opacity 会抛或污染所有 button)。
        Brush fill = TryClonePrimaryBrush(btn) ?? Brushes.White;

        var el = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(pt.X - size / 2, pt.Y - size / 2, 0, 0),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0.5,
        };
        return el;
    }

    private static Brush? TryClonePrimaryBrush(DependencyObject scope)
    {
        if (System.Windows.Application.Current?.Resources["PrimaryBrush"] is SolidColorBrush frozen)
        {
            // Frozen brush 不能 Clone,但可以直接 new 一个同色 SolidColorBrush(未 freeze)替换
            return new SolidColorBrush(frozen.Color);
        }
        return null;
    }

    private static void AnimateRipple(Ellipse el, Action onCompleted)
    {
        var sb = new Storyboard { Duration = MotionSettings.DurationRipple };

        var growX = new DoubleAnimationUsingKeyFrames();
        growX.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        growX.KeyFrames.Add(new EasingDoubleKeyFrame(
            1, KeyTime.FromTimeSpan(MotionSettings.DurationRipple), new CircleEase { EasingMode = EasingMode.EaseOut }));
        Storyboard.SetTarget(growX, el);
        Storyboard.SetTargetProperty(growX, new PropertyPath("RenderTransform.ScaleX"));
        sb.Children.Add(growX);

        var growY = new DoubleAnimationUsingKeyFrames();
        growY.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        growY.KeyFrames.Add(new EasingDoubleKeyFrame(
            1, KeyTime.FromTimeSpan(MotionSettings.DurationRipple), new CircleEase { EasingMode = EasingMode.EaseOut }));
        Storyboard.SetTarget(growY, el);
        Storyboard.SetTargetProperty(growY, new PropertyPath("RenderTransform.ScaleY"));
        sb.Children.Add(growY);

        var fade = new DoubleAnimation(0.5, 0, MotionSettings.DurationRipple);
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
        sb.Children.Add(fade);

        sb.Completed += (_, _) =>
        {
            onCompleted();
            sb.Stop(el);  // 解绑 timeline,允许 GC 回收
        };
        sb.Begin();
    }
}