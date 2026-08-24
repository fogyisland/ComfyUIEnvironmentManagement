using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTemplateSeedTests
{
    private static readonly string ProjectRoot =
        Path.Combine(Path.GetTempPath(), "cmgr-templates-test");

    [Fact]
    public void Apply_EmptySettings_SeedsComfyUIAndA1111Templates()
    {
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.True(s.Templates.ContainsKey("A1111"));
    }

    [Fact]
    public void Apply_EmptySettings_ComfyUITemplateHasCorrectDefaults()
    {
        // G5: ComfyUI defaults
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var c = s.Templates["ComfyUI"];
        Assert.Equal("ComfyUI", c.Name);
        Assert.Equal("ComfyUI", c.Kind);
        Assert.Equal("main.py", c.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir);
    }

    [Fact]
    public void Apply_EmptySettings_A1111TemplateHasCorrectDefaults()
    {
        // G5: A1111 defaults
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var a = s.Templates["A1111"];
        Assert.Equal("A1111", a.Name);
        Assert.Equal("A1111", a.Kind);
        Assert.Equal("webui.py", a.EntryScript);
        Assert.Equal("--port {port}", a.EntryArgs);
        Assert.Equal("models/Stable-diffusion", a.ModelsSubdir);
    }

    [Fact]
    public void Apply_UserCustomizedTemplate_NotOverwritten()
    {
        // G4: never overwrite user customization
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "MyCustomName",
            Kind = "ComfyUI",
            LocalSourceDir = "D:/my-fork",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 127.0.0.1",
            ModelsSubdir = "models",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("MyCustomName", s.Templates["ComfyUI"].Name);
        Assert.Equal("D:/my-fork", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("--port {port} --listen 127.0.0.1", s.Templates["ComfyUI"].EntryArgs);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDirField_MigratedToTemplatesDict()
    {
        // G6: migrate from old Settings.TemplateComfyuiDir (JSON property
        // "template_comfyui_dir") -> Settings.Templates["ComfyUI"].LocalSourceDir。
        // T12 移除 Settings.TemplateComfyuiDir 字段 — 改走 rawJson + JsonDocument
        // 读老 JSON property。模拟老 settings.json 整文件 deserialize 后,
        // Apply 用 rawJson 触发迁移。
        var s = new Settings();
        var oldJson = "{\"template_comfyui_dir\":\"D:/old/comfyui-source\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("D:/old/comfyui-source", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("main.py", s.Templates["ComfyUI"].EntryScript);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_NotMigrated_WhenRawJsonNull()
    {
        // T12 后:无 rawJson(纯 in-memory settings,没经过 SettingsRepository.Load)
        // → 不迁移,只 seed 默认 ComfyUI entry。保护 caller 不被"凭空迁移"覆盖。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot, rawJson: null);

        // Templates["ComfyUI"] 由 SeedBuiltInTemplatesIfMissing 填默认,
        // LocalSourceDir 指向相对路径 envTemplates/ComfyUI(v1.0.0.x)。
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal(Path.Combine("envTemplates", "ComfyUI"),
            s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_NotMigrated_WhenUserAlreadyHasComfyUI()
    {
        // G4:用户已设过 ComfyUI template → 不让老 JSON 字段覆盖当前 entry。
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = "D:/user-picked",
            EntryScript = "main.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models",
        };
        var oldJson = "{\"template_comfyui_dir\":\"D:/old/comfyui-source\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        Assert.Equal("D:/user-picked", s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_EmptyValue_NotMigrated()
    {
        // 老 JSON 字段为空字符串 → 不迁移,跟原 Settings 字段空白的语义一致。
        var s = new Settings();
        var oldJson = "{\"template_comfyui_dir\":\"\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        // seed 默认 ComfyUI entry(LocalSourceDir 指向 <projectRoot>/envTemplates/ComfyUI,v1.0.0.x 改动)
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal(Path.Combine("envTemplates", "ComfyUI"),
            s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_MalformedJson_DoesNotThrow()
    {
        // 防御:JSON 解析失败 → 静默不抛,后续 seed 走默认 ComfyUI entry。
        var s = new Settings();
        var badJson = "{this is not valid json";

        var ex = Record.Exception(() => SettingsDefaults.Apply(s, ProjectRoot, badJson));

        Assert.Null(ex);
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
    }
}
