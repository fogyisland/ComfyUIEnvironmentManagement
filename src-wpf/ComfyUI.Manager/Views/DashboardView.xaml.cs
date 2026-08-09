using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ComfyUI.Manager.Animations;

namespace ComfyUI.Manager.Views;

public partial class DashboardView : UserControl
{
    // v0.6.9 T9:4 卡片 stagger fade — 首次 Loaded 触发,主线程同步动画。
    // Refresh 后是否重播留给 T10(spec §6.2 brief 注明 T9 简化:stagger 只在 OnLoaded 触发)。
    private Border[]? _staggerCards;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _staggerCards = new[] { EnvStatsCard, NodeCountCard, RecentOpsCard, VersionCard };

        // G9:无动效模式 → 直接显示终态(Opacity=1, Y=0),不创建 Storyboard。
        if (!MotionSettings.IsAnimationEnabled)
        {
            ResetCardsToVisible();
            return;
        }
        StaggerCards();
    }

    /// <summary>
    /// 4 卡片 stagger fade-in。BeginTime 控制每张卡延迟 0/100/200/300ms,
    /// 单卡动画时长 DurationStaggerDashboardMs(100ms)。TranslateTransform
    /// 而非 Margin 避免 layout invalidate。
    /// </summary>
    private void StaggerCards()
    {
        if (_staggerCards is null) return;

        for (int i = 0; i < _staggerCards.Length; i++)
        {
            var card = _staggerCards[i];
            // 初始 Opacity=0(代码内 set 而非 XAML,避免 binding-time 跳变)
            card.Opacity = 0;

            var sb = new Storyboard
            {
                BeginTime = TimeSpan.FromMilliseconds(i * MotionSettings.DurationStaggerDashboardMs),
                Duration = MotionSettings.DurationStaggerDashboard,
            };

            var fade = new DoubleAnimation(0, 1, MotionSettings.DurationStaggerDashboard);
            Storyboard.SetTarget(fade, card);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            var translate = new DoubleAnimation(20, 0, MotionSettings.DurationStaggerDashboard);
            Storyboard.SetTarget(translate, card);
            Storyboard.SetTargetProperty(translate,
                new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            sb.Children.Add(translate);

            sb.Begin();
        }
    }

    /// <summary>无动效模式直接显示终态,避免 Opacity=0 永久不可见。</summary>
    private void ResetCardsToVisible()
    {
        if (_staggerCards is null) return;
        foreach (var card in _staggerCards)
        {
            card.Opacity = 1;
            if (card.RenderTransform is TranslateTransform t) t.Y = 0;
        }
    }
}