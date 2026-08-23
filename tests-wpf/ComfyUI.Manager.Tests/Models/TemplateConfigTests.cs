using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
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
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/foo/bar.git",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
            ExtraJunctionTargets = new System.Collections.Generic.List<string> { "extra1", "extra2" },
            UserExtraArgs = "--preview-method auto",
        };

        // Use JsonOptions.Default to mirror production serialization (includes JsonStringEnumConverter).
        var json = JsonSerializer.Serialize(original, JsonOptions.Default);
        var restored = JsonSerializer.Deserialize<TemplateConfig>(json, JsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Equal("ComfyUI", restored!.Name);
        Assert.Equal("ComfyUI", restored.Kind);
        Assert.Equal("Templates/ComfyUI", restored.LocalSourceDir);
        Assert.Equal(TemplateSourceKind.GitHub, restored.SourceKind);
        Assert.Equal("https://github.com/foo/bar.git", restored.GitHubRepoUrl);
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
        Assert.Equal(TemplateSourceKind.Local, c.SourceKind);
        Assert.Equal("", c.GitHubRepoUrl);
        Assert.Equal("", c.EntryScript);
        Assert.Equal("", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir); // G5 default
        Assert.Empty(c.ExtraJunctionTargets);
        Assert.Equal("", c.UserExtraArgs);
    }

    [Fact]
    public void JsonPropertyNames_MatchSpec()
    {
        // spec §3 verbatim property names. Uses JsonOptions.Default so the JsonStringEnumConverter
        // serializes SourceKind as "Local" (string) instead of 0 (int) — matches production settings.json.
        var c = new TemplateConfig { Name = "X", Kind = "X" };
        var json = JsonSerializer.Serialize(c, JsonOptions.Default);
        Assert.Contains("\"name\":\"X\"", json);
        Assert.Contains("\"kind\":\"X\"", json);
        Assert.Contains("\"local_source_dir\":\"\"", json);
        Assert.Contains("\"source_kind\":\"Local\"", json); // default
        Assert.Contains("\"github_repo_url\":\"\"", json);
        Assert.Contains("\"entry_script\":\"\"", json);
        Assert.Contains("\"entry_args\":\"\"", json);
        Assert.Contains("\"models_subdir\":\"models\"", json);
        Assert.Contains("\"extra_junction_targets\":[]", json);
        Assert.Contains("\"user_extra_args\":\"\"", json);
    }

    [Fact]
    public void BackwardCompat_OldJson_NoSourceKind_DefaultsToLocal()
    {
        // T2 seeded settings.json has TemplateConfig entries without source_kind/github_repo_url.
        // After T13 adds those fields, deserializing old JSON must still work and default to Local.
        const string oldJson = """{"name":"X","kind":"X","local_source_dir":"x","entry_script":"","entry_args":"","models_subdir":"models","extra_junction_targets":[],"user_extra_args":""}""";
        var c = JsonSerializer.Deserialize<TemplateConfig>(oldJson, JsonOptions.Default)!;
        Assert.Equal(TemplateSourceKind.Local, c.SourceKind);
        Assert.Equal("", c.GitHubRepoUrl);
    }

    [Fact]
    public void JsonPropertyNames_SnakeCase_WithoutExplicitOptions()
    {
        // JsonOptions uses CamelCase globally, but call JsonSerializer.Serialize without options here so
        // the explicit [JsonPropertyName] attributes are the only mapping. Guards against accidental
        // attribute removal for fields whose CamelCase rendering happens to match snake_case (a no-op bug).
        var c = new TemplateConfig { LocalSourceDir = "x", ExtraJunctionTargets = new() { "a" } };
        var json = JsonSerializer.Serialize(c);
        Assert.Contains("local_source_dir", json);
        Assert.Contains("extra_junction_targets", json);
    }
}
