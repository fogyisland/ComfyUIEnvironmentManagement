using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31): 锁 <see cref="TemplateConfigDefaults"/> 8 个 built-in 工厂的
/// <c>Verified</c> 默认值 ——
/// <list type="bullet">
///   <item>ComfyUI + Forge = <c>true</c>(项目方已 dev build 验证 env-create + 启动 + 接口可达)</item>
///   <item>其它 4 个 = <c>false</c>(OpenVoice / HunyuanVideo / CogVideoX / HivisionIDPhotos — 等后续 wave 验证后逐个 ship)</item>
/// </list>
/// T29 (2026-09-01): Fooocus 已下线,从 9 → 8。
/// T30 (2026-09-01): CoquiTTS + Bark 已下线,从 8 → 6。
/// T31 (2026-09-02): Whisper + LTXVideo 已下线,从 6 → 4。
///
/// 用户决策(AskUserQuestion 2026-08-31):EditTemplateDialog 不暴露 Checkbox,
/// Verified 只能由工厂在 ship 时设置。
/// </summary>
public sealed class TemplateConfigDefaultsVerifiedTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void ComfyUi_Verified_IsTrue()
    {
        var cfg = TemplateConfigDefaults.ComfyUi(ProjectRoot);
        Assert.True(cfg.Verified);
    }

    [Fact]
    public void Forge_Verified_IsTrue()
    {
        var cfg = TemplateConfigDefaults.Forge(ProjectRoot);
        Assert.True(cfg.Verified);
    }

    [Theory]
    [InlineData("OpenVoice")]
    [InlineData("HunyuanVideo")]
    [InlineData("CogVideoX")]
    [InlineData("HivisionIdPhotos")]
    public void NonImageBuiltIn_Verified_DefaultsToFalse(string factoryName)
    {
        // 反射工厂方法 — 避开硬编码 6 个 inline call,新 built-in 加进来自动覆盖
        // 注意 C# method 命名:CamelCase "HivisionIdPhotos"(不是 "HivisionIDPhotos",
        // 虽然 Kind 字段是后者)
        // v1.0.0.x (2026-09-01) T30:CoquiTTS + Bark 已下线,不在此列表。
        var method = typeof(TemplateConfigDefaults)
            .GetMethod(factoryName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new System.InvalidOperationException($"Factory method '{factoryName}' not found on TemplateConfigDefaults");
        var cfg = (ComfyUI.Manager.Models.TemplateConfig)method.Invoke(null, new object[] { ProjectRoot })!;

        Assert.False(cfg.Verified);
    }
}
