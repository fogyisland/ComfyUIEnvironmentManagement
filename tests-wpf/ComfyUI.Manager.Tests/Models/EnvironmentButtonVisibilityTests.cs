using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v1.0.0.x (2026-08-31): 锁 <see cref="Environment.GenericActionsVisible"/> 行为 ——
/// Row 2 第 3 个 Grid `GenericActions` 显示条件:
/// <list type="bullet">
///   <item>9 个 non-ComfyUI/Forge built-in kind = true(Fooocus / OpenVoice / Whisper / CoquiTTS / Bark / HunyuanVideo / LTXVideo / CogVideoX / HivisionIDPhotos)</item>
///   <item>ComfyUI + Forge = false(各自走自己的 5×2 / 3×2 Grid)</item>
///   <item>空 / 未知 kind = false(default)</item>
/// </list>
///
/// 镜像 <see cref="Environment.RequirementsButtonVisible"/> 模式(inverse computed bool),
/// XAML 用单 DataTrigger Value="True" 触发 visible。
/// </summary>
public sealed class EnvironmentButtonVisibilityTests
{
    [Theory]
    [InlineData("Fooocus")]
    [InlineData("OpenVoice")]
    [InlineData("Whisper")]
    [InlineData("CoquiTTS")]
    [InlineData("Bark")]
    [InlineData("HunyuanVideo")]
    [InlineData("LTXVideo")]
    [InlineData("CogVideoX")]
    [InlineData("HivisionIDPhotos")]
    public void GenericActionsVisible_TrueForNineNonComfyUiForgeBuiltInKinds(string kind)
    {
        // v1.0.0.x:9 个 built-in 用通用 5-button Grid(start/log/browser/report/delete)
        var env = new Environment { TemplateKind = kind };
        Assert.True(env.GenericActionsVisible);
    }

    [Theory]
    [InlineData("ComfyUI")]   // 回归:ComfyUI 走自己 5×2 Grid
    [InlineData("Forge")]     // 回归:Forge 走自己 3×2 Grid
    public void GenericActionsVisible_FalseForComfyUiAndForge(string kind)
    {
        var env = new Environment { TemplateKind = kind };
        Assert.False(env.GenericActionsVisible);
    }

    [Theory]
    [InlineData("MyCustomKind")]
    [InlineData("A1111")]      // 已下线 — SettingsDefaults.Apply prune 后不应出现,但代码防御
    [InlineData("SwarmUI")]    // 同上
    public void GenericActionsVisible_TrueForAnyNonComfyUiForgeKind(string kind)
    {
        // 任何既不是 ComfyUI 也不是 Forge 的 kind(包括下线 + 用户自定义)
        // 都走通用 Grid。
        var env = new Environment { TemplateKind = kind };
        Assert.True(env.GenericActionsVisible);
    }

    [Fact]
    public void GenericActionsVisible_EmptyTemplateKind_FallsIntoGenericGrid()
    {
        // 空 TemplateKind(防御测试 — 默认 Environment.TemplateKind = "ComfyUI"
        // 不会出空值,但 computed bool 不能 crash)。空 != ComfyUI 且 != Forge → true
        // → 通用 Grid 显示。安全 fallback(不会因空 kind 渲染崩溃)。
        var env = new Environment { TemplateKind = "" };
        Assert.True(env.GenericActionsVisible);
    }

    [Fact]
    public void GenericActionsVisible_DefaultEnvTemplateKindIsComfyUi_GenericActionsFalse()
    {
        // Environment.cs line 62 默认 TemplateKind = "ComfyUI" → 通用 Grid false
        // (默认新 env 应该走 ComfyUI 5×2 Grid,跟 v1.0.0 行为一致)
        var env = new Environment();
        Assert.Equal("ComfyUI", env.TemplateKind);
        Assert.False(env.GenericActionsVisible);
    }

    [Fact]
    public void FooocusEnv_GenericActionsVisible_True_ComfyUiManagerVisible_False()
    {
        // v1.0.0.x (2026-08-31) 正交验证:Fooocus env 走通用 Grid(5 buttons:
        // 启动 / 查看日志 / 打开浏览器 / 组件报告 / 删除),不显示 ComfyUI Manager
        // 按钮(ComfyUiManagerButtonVisible = false,因为 Kind != "ComfyUI")。
        // 两个属性各管各的 button visibility,不互相影响。
        var fooocus = new Environment { TemplateKind = "Fooocus" };
        Assert.False(fooocus.ComfyUiManagerButtonVisible);   // Fooocus 无 ComfyUI Manager
        Assert.True(fooocus.GenericActionsVisible);           // Fooocus 用通用 5-button Grid
    }
}
