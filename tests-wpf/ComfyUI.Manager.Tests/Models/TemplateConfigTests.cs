using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class TemplateConfigTests
{
    [Fact]
    public void RoundTrip_AllFields_PreservesValues()
    {
        var original = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = "Templates/ComfyUI",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
            ExtraJunctionTargets = new System.Collections.Generic.List<string> { "extra1", "extra2" },
            UserExtraArgs = "--preview-method auto",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<TemplateConfig>(json);

        Assert.NotNull(restored);
        Assert.Equal("ComfyUI", restored!.Name);
        Assert.Equal("ComfyUI", restored.Kind);
        Assert.Equal("Templates/ComfyUI", restored.LocalSourceDir);
        Assert.Equal("main.py", restored.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", restored.EntryArgs);
        Assert.Equal("models", restored.ModelsSubdir);
        Assert.Equal(2, restored.ExtraJunctionTargets.Count);
        Assert.Equal("--preview-method auto", restored.UserExtraArgs);
    }

    [Fact]
    public void DefaultValues_AreEmptyStrings_AndEmptyList()
    {
        var c = new TemplateConfig();
        Assert.Equal("", c.Name);
        Assert.Equal("", c.Kind);
        Assert.Equal("", c.LocalSourceDir);
        Assert.Equal("", c.EntryScript);
        Assert.Equal("", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir); // G5 default
        Assert.Empty(c.ExtraJunctionTargets);
        Assert.Equal("", c.UserExtraArgs);
    }

    [Fact]
    public void JsonPropertyNames_MatchSpec()
    {
        // spec §3 verbatim property names
        var c = new TemplateConfig { Name = "X", Kind = "X" };
        var json = JsonSerializer.Serialize(c);
        Assert.Contains("\"name\":\"X\"", json);
        Assert.Contains("\"kind\":\"X\"", json);
        Assert.Contains("\"local_source_dir\":\"\"", json);
        Assert.Contains("\"entry_script\":\"\"", json);
        Assert.Contains("\"entry_args\":\"\"", json);
        Assert.Contains("\"models_subdir\":\"models\"", json);
        Assert.Contains("\"extra_junction_targets\":[]", json);
        Assert.Contains("\"user_extra_args\":\"\"", json);
    }

    [Fact]
    public void JsonOptions_UsesSnakeCase_NamesFromComfySettingsWriter()
    {
        // ComfySettingsWriter / JsonOptions.cs uses PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
        // (or equivalent). Verify TemplateConfig serializes with snake_case without custom attribute.
        var c = new TemplateConfig { LocalSourceDir = "x", ExtraJunctionTargets = new() { "a" } };
        var json = JsonSerializer.Serialize(c);
        Assert.Contains("local_source_dir", json);
        Assert.Contains("extra_junction_targets", json);
    }
}
