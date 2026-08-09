using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ComfyUI.Manager.Animations;

namespace ComfyUI.Manager.Behaviors;

/// <summary>
/// v0.6.9 T8:ContentControl Content 切换时 200ms opacity fade。
///
/// <para>
/// 给主窗口承载 view 的 ContentControl 加
/// <c>behaviors:ViewFadeTransitionBehavior.IsEnabled="True"</c> 即可启用。
/// </para>
///
/// <para>
/// 实现要点:
/// <list type="bullet">
/// <item>用 <see cref="DependencyPropertyDescriptor.FromProperty"/> 监听
/// <see cref="ContentControl.ContentProperty"/> 变化</item>
/// <item>简化为只 fade-in(Opacity 0→1),不 fade-out — 避免新旧 Storyboard 并行冲突</item>
/// <item>Storyboard.Completed 显式 <c>Opacity = 1</c>,防止停在中间态(G9 invariant)</item>
/// <item><see cref="MotionSettings.IsAnimationEnabled"/> = false 直接 return,Content 立即可见</item>
/// </list>
/// </para>
///
/// <para>
/// T8 实测 .NET 8 + WPF:<see cref="DependencyPropertyDescriptor.FromProperty"/>
/// 返回非 null,<see cref="Attach(ContentControl)"/> 工作正常,未触发 brief §6.2 列出的
/// "FromProperty returns null in .NET 8" 风险路径(详见 task-8-report §7)。
/// </para>
/// </summary>
public static class ViewFadeTransitionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ViewFadeTransitionBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl cc) return;
        if ((bool)e.NewValue) Attach(cc);
        else Detach(cc);
    }

    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached(
            "ContentChangedHandler", typeof(EventHandler), typeof(ViewFadeTransitionBehavior),
            new PropertyMetadata(null));

    private static void Attach(ContentControl cc)
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        if (dpd is null) return;

        EventHandler handler = (_, _) => OnContentChanged(cc);
        cc.SetValue(HandlerProperty, handler);
        dpd.AddValueChanged(cc, handler);
    }

    private static void Detach(ContentControl cc)
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        if (dpd is null) return;

        if (cc.GetValue(HandlerProperty) is EventHandler handler)
        {
            dpd.RemoveValueChanged(cc, handler);
            cc.SetValue(HandlerProperty, null);
        }
    }

    private static void OnContentChanged(ContentControl cc)
    {
        if (!MotionSettings.IsAnimationEnabled) return;
        FadeIn(cc);
    }

    // 单 _active field(简化):MainWindow 只 1 个 ContentControl,够用。
    // 多个 ContentControl 共用会冲突,future case 改 instance-keyed dictionary。
    private static Storyboard? _active;

    private static void FadeIn(ContentControl cc)
    {
        _active?.Stop(cc);
        cc.Opacity = 0;

        var sb = new Storyboard { Duration = MotionSettings.DurationFadeView };
        var fade = new DoubleAnimation(0, 1, MotionSettings.DurationFadeView)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, cc);
        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
        sb.Children.Add(fade);

        sb.Completed += (_, _) =>
        {
            cc.Opacity = 1;  // explicit final state — G9 invariant
            sb.Stop(cc);      // 解绑 timeline
        };

        _active = sb;
        sb.Begin(cc);
    }
}