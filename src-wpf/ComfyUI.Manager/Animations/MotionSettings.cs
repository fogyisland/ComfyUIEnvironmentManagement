using System;
using System.Windows;

namespace ComfyUI.Manager.Animations;

/// <summary>
/// v0.6.9 T8:集中动效时长 + 开关。所有动画点都从这里读,
/// 便于 T9 页面级动效统一节奏,G9 风险(尊重系统"减少动画")集中处理。
///
/// <para>
/// 静态类(无 DI、无 stateful service)— 简单优先,只有 5 个时长 const
/// 跟一个 IsAnimationEnabled 检测。所有 call site 直接读,无注册/初始化开销。
/// </para>
///
/// <para>
/// 三个开关优先级(从高到低):
/// <list type="number">
/// <item>测试 <see cref="SetUserOverride(bool?)"/> 强制值(测试 seam + 预留 future Settings UI 接线)</item>
/// <item><see cref="SystemParameters.ClientAreaAnimation"/> —— WPF 系统参数,Windows 设置 → 辅助功能 → 视觉效果 → "动画效果" 关闭时返回 false</item>
/// </list>
/// </para>
/// </summary>
public static class MotionSettings
{
    // 时长常数(单位:ms)—— Storyboard 用 TimeSpan.FromMilliseconds 转换
    public const int DurationRippleMs = 50;
    public const int DurationFadeViewMs = 200;
    public const int DurationSlideBannerMs = 250;
    public const int DurationStaggerDashboardMs = 100;
    public const int DurationThemeCrossfadeMs = 300;

    // 公共 TimeSpan 属性(Storyboard.Duration / DoubleAnimation.Duration 直接用)
    public static TimeSpan DurationRipple => TimeSpan.FromMilliseconds(DurationRippleMs);
    public static TimeSpan DurationFadeView => TimeSpan.FromMilliseconds(DurationFadeViewMs);
    public static TimeSpan DurationSlideBanner => TimeSpan.FromMilliseconds(DurationSlideBannerMs);
    public static TimeSpan DurationStaggerDashboard => TimeSpan.FromMilliseconds(DurationStaggerDashboardMs);
    public static TimeSpan DurationThemeCrossfade => TimeSpan.FromMilliseconds(DurationThemeCrossfadeMs);

    // 动效开关(系统 + 用户 override)
    private static bool? _userOverride;

    /// <summary>
    /// 当前动效是否启用。优先 <see cref="_userOverride"/>(测试 seam / future Settings 接线),
    /// fallback 到 <see cref="SystemParameters.ClientAreaAnimation"/>。
    /// </summary>
    public static bool IsAnimationEnabled
    {
        get
        {
            if (_userOverride.HasValue) return _userOverride.Value;
            return SystemParameters.ClientAreaAnimation;
        }
    }

    /// <summary>
    /// 测试 seam:强制覆盖。生产代码不调,仅测试可设(留给 future Settings UI 接线,
    /// 让用户在"减少动画"之外再多一个 app-level override)。
    /// </summary>
    public static void SetUserOverride(bool? value) => _userOverride = value;

    /// <summary>
    /// 测试 seam:清空 override,fallback 到系统设置。每测试跑前调避免 cross-test pollution。
    /// </summary>
    public static void Reset() => _userOverride = null;
}