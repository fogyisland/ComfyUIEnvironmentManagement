using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01): 锁 <see cref="TemplateConfigDefaults"/> 8 个 built-in 工厂的
/// <c>RequirementsFile</c> 默认值 ——
/// <list type="bullet">
///   <item>HunyuanVideo / CogVideoX = "requirements.txt"</item>
///   <item>删 LTXVideo(2026-09-02 T31: uv sync 已下线)</item>
///   <item>ComfyUI / Forge / OpenVoice / HivisionIDPhotos = ""
///   (走自己 Actions Grid 或 env-create 自动 pip install)</item>
/// </list>
/// T29 (2026-09-01): Fooocus 已下线,requirements_versions.txt 配置随之删除。
/// T30 (2026-09-01): CoquiTTS + Bark 已下线,RequirementsFile 配置随之删除。
/// T31 (2026-09-02): Whisper + LTXVideo 已下线,LTXVideo_RequirementsFile_IsEmpty_UvSyncHandlesDeps test 整段删 + Whisper InlineData 删。
///
/// 镜像 <see cref="TemplateConfigDefaultsVerifiedTests"/> pattern(反射工厂方法
/// + Theory InlineData),新 built-in 加进来自动覆盖(只要加 InlineData)。
/// </summary>
public sealed class TemplateConfigDefaultsRequirementsFileTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void HunyuanVideo_RequirementsFile_IsRequirementsTxt()
    {
        var cfg = TemplateConfigDefaults.HunyuanVideo(ProjectRoot);
        Assert.Equal("requirements.txt", cfg.RequirementsFile);
    }

    [Fact]
    public void CogVideoX_RequirementsFile_IsRequirementsTxt()
    {
        var cfg = TemplateConfigDefaults.CogVideoX(ProjectRoot);
        Assert.Equal("requirements.txt", cfg.RequirementsFile);
    }

    [Theory]
    [InlineData("ComfyUi")]
    [InlineData("Forge")]
    [InlineData("OpenVoice")]
    [InlineData("HivisionIdPhotos")]
    public void OtherBuiltIn_RequirementsFile_IsEmpty(string factoryName)
    {
        // 其它 5 个 built-in 不需要 RequirementsFile:
        // - ComfyUI / Forge 走自己 Actions Grid 的 RequirementsInstaller
        // - OpenVoice / HivisionIDPhotos env-create 时
        //   已经 `pip install -e .` 或类似自带依赖(不在本 wave 范围)
        // 镜像 Factory Method 的 CamelCase 命名规则(HivisionIdPhotos
        // 不是 HivisionIDPhotos — Kind 字符串才是后者)
        // v1.0.0.x (2026-09-01) T30:CoquiTTS + Bark 已下线,不在此列表。
        var method = typeof(TemplateConfigDefaults)
            .GetMethod(factoryName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new System.InvalidOperationException($"Factory method '{factoryName}' not found on TemplateConfigDefaults");
        var cfg = (TemplateConfig)method.Invoke(null, new object[] { ProjectRoot })!;

        Assert.Equal("", cfg.RequirementsFile);
    }
}
