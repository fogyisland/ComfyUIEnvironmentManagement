using System;
using System.Windows;
using ComfyUI.Manager.Animations;
using Xunit;

namespace ComfyUI.Manager.Tests.Animations;

/// <summary>
/// v0.6.9 T8:MotionSettings 静态类单测。3 测试覆盖时长常量、默认系统 setting 透传、override 切换。
///
/// <para>
/// 注意:
/// <list type="bullet">
/// <item>MotionSettings 是静态,test 顺序敏感 — xUnit 默认 per-class 顺序执行(同 class 测试 serial 跑),
/// 异 class 并行隔离。每测试显式 <see cref="MotionSettings.Reset"/> 防 cross-test pollution。</item>
/// <item>不依赖 <see cref="SystemParameters.ClientAreaAnimation"/> 的具体值(OS 设置可能关),
/// 只测"默认状态读 system setting"跟"override 改变结果"。</item>
/// </list>
/// </para>
/// </summary>
public sealed class MotionSettingsTests
{
    [Fact]
    public void Durations_AreCorrect()
    {
        Assert.Equal(50, MotionSettings.DurationRippleMs);
        Assert.Equal(200, MotionSettings.DurationFadeViewMs);
        Assert.Equal(250, MotionSettings.DurationSlideBannerMs);
        Assert.Equal(100, MotionSettings.DurationStaggerDashboardMs);
        Assert.Equal(300, MotionSettings.DurationThemeCrossfadeMs);

        Assert.Equal(TimeSpan.FromMilliseconds(50), MotionSettings.DurationRipple);
        Assert.Equal(TimeSpan.FromMilliseconds(200), MotionSettings.DurationFadeView);
        Assert.Equal(TimeSpan.FromMilliseconds(250), MotionSettings.DurationSlideBanner);
        Assert.Equal(TimeSpan.FromMilliseconds(100), MotionSettings.DurationStaggerDashboard);
        Assert.Equal(TimeSpan.FromMilliseconds(300), MotionSettings.DurationThemeCrossfade);
    }

    [Fact]
    public void IsAnimationEnabled_DefaultsToSystemSetting()
    {
        MotionSettings.Reset();
        var expected = SystemParameters.ClientAreaAnimation;
        Assert.Equal(expected, MotionSettings.IsAnimationEnabled);
    }

    [Fact]
    public void SetUserOverride_OverridesSystemSetting()
    {
        MotionSettings.SetUserOverride(false);
        Assert.False(MotionSettings.IsAnimationEnabled);

        MotionSettings.SetUserOverride(true);
        Assert.True(MotionSettings.IsAnimationEnabled);

        MotionSettings.Reset();
        // Reset 后回到系统 setting(不应为 false,除非用户 OS 真的关掉了动画)
        Assert.Equal(SystemParameters.ClientAreaAnimation, MotionSettings.IsAnimationEnabled);
    }
}