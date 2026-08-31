using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01): 锁 <see cref="TemplateConfigDefaults"/> 11 个 built-in 工厂的
/// <c>RequirementsFile</c> 默认值 ——
/// <list type="bullet">
///   <item>Fooocus = "requirements_versions.txt"(上游默认文件名)</item>
///   <item>HunyuanVideo / CogVideoX = "requirements.txt"</item>
///   <item>LTXVideo = ""(uv sync 装 pyproject.toml)</item>
///   <item>ComfyUI / Forge / OpenVoice / Whisper / CoquiTTS / Bark / HivisionIDPhotos = ""
///   (走自己 Actions Grid 或 env-create 自动 pip install)</item>
/// </list>
///
/// 镜像 <see cref="TemplateConfigDefaultsVerifiedTests"/> pattern(反射工厂方法
/// + Theory InlineData),新 built-in 加进来自动覆盖(只要加 InlineData)。
/// </summary>
public sealed class TemplateConfigDefaultsRequirementsFileTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void Fooocus_RequirementsFile_IsRequirementsVersionsTxt()
    {
        // Fooocus 上游 launch.py 用 requirements_versions.txt(23 non-torch deps)
        var cfg = TemplateConfigDefaults.Fooocus(ProjectRoot);
        Assert.Equal("requirements_versions.txt", cfg.RequirementsFile);
    }

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

    [Fact]
    public void LTXVideo_RequirementsFile_IsEmpty_UvSyncHandlesDeps()
    {
        // v1.0.0.x LTX-2 plan: uv sync step 7.5 装 pyproject.toml 全套依赖
        // (含 torch ≥2.1.0 + LTX-2 自定义 deps),不需要额外 Requirements 按钮。
        var cfg = TemplateConfigDefaults.LTXVideo(ProjectRoot);
        Assert.Equal("", cfg.RequirementsFile);
    }

    [Theory]
    [InlineData("ComfyUi")]
    [InlineData("Forge")]
    [InlineData("OpenVoice")]
    [InlineData("Whisper")]
    [InlineData("CoquiTts")]
    [InlineData("Bark")]
    [InlineData("HivisionIdPhotos")]
    public void OtherBuiltIn_RequirementsFile_IsEmpty(string factoryName)
    {
        // 其它 7 个 built-in 不需要 RequirementsFile:
        // - ComfyUI / Forge 走自己 Actions Grid 的 RequirementsInstaller
        // - OpenVoice / Whisper / CoquiTTS / Bark / HivisionIDPhotos env-create 时
        //   已经 `pip install -e .` 或类似自带依赖(不在本 wave 范围)
        // 镜像 Factory Method 的 CamelCase 命名规则(CoquiTts / HivisionIdPhotos
        // 不是 CoquiTTS / HivisionIDPhotos — Kind 字符串才是后者)
        var method = typeof(TemplateConfigDefaults)
            .GetMethod(factoryName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new System.InvalidOperationException($"Factory method '{factoryName}' not found on TemplateConfigDefaults");
        var cfg = (TemplateConfig)method.Invoke(null, new object[] { ProjectRoot })!;

        Assert.Equal("", cfg.RequirementsFile);
    }
}
