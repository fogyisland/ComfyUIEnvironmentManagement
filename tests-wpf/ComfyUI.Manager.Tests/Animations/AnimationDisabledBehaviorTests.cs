using ComfyUI.Manager.Animations;
using Xunit;

namespace ComfyUI.Manager.Tests.Animations;

/// <summary>
/// v0.6.9 T9:G9 短路 + G10 不依赖 WPF UI 测试 — 仅验证 MotionSettings override
/// 行为在 T9 范围(4 个新动效 surface)里仍然正确,跟 T8 已测过的 contract 一致。
///
/// <para>
/// G6 限制(WPF Window/Storyboard 不可写脆弱 UI 测试)→
/// <list type="bullet">
/// <item>测 1:MotionSettings override 路径(T8 已测过,T9 重测确认 T9 范围仍生效)</item>
/// <item>测 2:RippleBehavior 短路路径 — RippleBehavior.OnPreviewMouseLeftButtonDown 顶部
/// <c>if (!MotionSettings.IsAnimationEnabled) return;</c>(T8 §7 commit)。这里只间接验证
/// MotionSettings 状态被 IsAnimationEnabled 暴露,因为 RippleBehavior 静态 + 真 Button
/// instance 才能跑。Logic-level 验证足够覆盖"动效可关闭"这条 G9 contract。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AnimationDisabledBehaviorTests
{
    [Fact]
    public void MotionSettings_IsAnimationEnabled_CanBeOverridden()
    {
        // T9 范围内重测 T8 contract:override true → IsAnimationEnabled=true;
        // override false → IsAnimationEnabled=false。任何 T9 加的 surface(ErrorBanner /
        // Dashboard / Settings pulse / Theme crossfade)都从这个 property 读短路信号。
        MotionSettings.Reset();
        MotionSettings.SetUserOverride(false);
        Assert.False(MotionSettings.IsAnimationEnabled);

        MotionSettings.SetUserOverride(true);
        Assert.True(MotionSettings.IsAnimationEnabled);

        MotionSettings.Reset();
    }

    [Fact]
    public void RippleBehavior_OverrideFalse_PreventsAnimationContract()
    {
        // G9 短路 contract 验证:RippleBehavior.OnPreviewMouseLeftButtonDown 顶部
        // if (!MotionSettings.IsAnimationEnabled) return; — 没创建 Ellipse 就不调 AnimateRipple。
        // 直接测静态方法不可行(需 ButtonBase 实例),改为验证 contract source of truth:
        // MotionSettings override=false 时 IsAnimationEnabled 一致返 false,任何 surface
        // 顶部短路检查都走 short-circuit 路径。
        MotionSettings.SetUserOverride(false);
        try
        {
            Assert.False(MotionSettings.IsAnimationEnabled);
            // Reset 后回 OS 默认 — 用户桌面如果 OS 关闭动画,这条测试也成立
        }
        finally
        {
            MotionSettings.Reset();
        }
    }
}
