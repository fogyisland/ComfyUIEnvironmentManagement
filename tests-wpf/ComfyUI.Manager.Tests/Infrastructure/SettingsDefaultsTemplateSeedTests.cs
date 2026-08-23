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
        // G6: migrate from old Settings.TemplateComfyuiDir -> Settings.Templates["ComfyUI"]
        var s = new Settings { TemplateComfyuiDir = "D:/old/comfyui-source" };
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("D:/old/comfyui-source", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("main.py", s.Templates["ComfyUI"].EntryScript);
    }
}
