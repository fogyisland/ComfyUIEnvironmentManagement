using System.IO;
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

    // --- T16: CanUpdateSource + SourceKindBadge computed properties ---

    [Fact]
    public void CanUpdateSource_LocalBuiltIn_True()
    {
        var cfg = new TemplateConfig { Kind = "ComfyUI", SourceKind = TemplateSourceKind.Local };
        Assert.True(cfg.CanUpdateSource);
    }

    [Fact]
    public void CanUpdateSource_LocalCustom_False()
    {
        var cfg = new TemplateConfig { Kind = "MySwarm", SourceKind = TemplateSourceKind.Local };
        Assert.False(cfg.CanUpdateSource);
    }

    [Fact]
    public void CanUpdateSource_GitHub_True()
    {
        var cfg = new TemplateConfig { Kind = "Anything", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.True(cfg.CanUpdateSource);
    }

    [Fact]
    public void SourceKindBadge_LocalKind_ReturnsLocalText()
    {
        var cfg = new TemplateConfig { Kind = "MySwarm", SourceKind = TemplateSourceKind.Local };
        Assert.Equal("[本地]", cfg.SourceKindBadge);
    }

    [Fact]
    public void SourceKindBadge_GitHubKind_ReturnsGitHubText()
    {
        var cfg = new TemplateConfig { Kind = "GhTpl", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.Equal("[GitHub]", cfg.SourceKindBadge);
    }

    // --- v1.0.0.x: CanDelete (hides grayed-out Delete button on built-in templates) ---

    [Fact]
    public void CanDelete_BuiltInComfyUI_False()
    {
        var cfg = new TemplateConfig { Kind = "ComfyUI" };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInA1111_False()
    {
        var cfg = new TemplateConfig { Kind = "A1111" };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_CustomLocal_True()
    {
        var cfg = new TemplateConfig { Kind = "MySwarm", SourceKind = TemplateSourceKind.Local };
        Assert.True(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_CustomGitHub_True()
    {
        // v1.0.0.x: Forge 现在是 built-in(本地 shipped),不能用作 custom 例子;
        // 改用 "MyVoiceTts" 之类非 built-in kind 测 custom GitHub delete 路径。
        var cfg = new TemplateConfig { Kind = "MyVoiceTts", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.True(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_EmptyKind_True()
    {
        var cfg = new TemplateConfig { Kind = "" };
        Assert.True(cfg.CanDelete);
    }

    // --- v1.0.0.x: 6 new built-in kinds (G13 delete 保护) ---

    [Fact]
    public void CanDelete_BuiltInForge_False()
    {
        var cfg = new TemplateConfig { Kind = "Forge", SourceKind = TemplateSourceKind.Local };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInSwarmUI_False()
    {
        var cfg = new TemplateConfig { Kind = "SwarmUI", SourceKind = TemplateSourceKind.Local };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInOpenVoice_False()
    {
        var cfg = new TemplateConfig { Kind = "OpenVoice", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInWhisper_False()
    {
        var cfg = new TemplateConfig { Kind = "Whisper", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInCoquiTTS_False()
    {
        var cfg = new TemplateConfig { Kind = "CoquiTTS", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.False(cfg.CanDelete);
    }

    [Fact]
    public void CanDelete_BuiltInBark_False()
    {
        var cfg = new TemplateConfig { Kind = "Bark", SourceKind = TemplateSourceKind.GitHub, GitHubRepoUrl = "https://x" };
        Assert.False(cfg.CanDelete);
    }

    // --- v1.0.0.x: Forge/SwarmUI 加 built-in repo URL,可走 UpdateAsync ---

    [Fact]
    public void CanUpdateSource_BuiltInForge_True()
    {
        var cfg = new TemplateConfig { Kind = "Forge", SourceKind = TemplateSourceKind.Local };
        Assert.True(cfg.CanUpdateSource);
    }

    [Fact]
    public void CanUpdateSource_BuiltInSwarmUI_True()
    {
        var cfg = new TemplateConfig { Kind = "SwarmUI", SourceKind = TemplateSourceKind.Local };
        Assert.True(cfg.CanUpdateSource);
    }

    // --- v1.0.0.x: 本地目录状态 badge (TemplateManagementView "本地目录为空") ---

    [Fact]
    public void LocalDirExists_AbsolutePathExists_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tmplcfg-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new TemplateConfig { LocalSourceDir = dir };
            Assert.True(cfg.LocalDirExists(null));
            Assert.True(cfg.LocalDirExists(""));   // 空 anchor → AppContext.BaseDirectory fallback
            Assert.True(cfg.LocalDirExists(@"D:\some\nonexistent\anchor"));  // absolute path 不看 anchor
        }
        finally { try { Directory.Delete(dir); } catch { } }
    }

    [Fact]
    public void LocalDirExists_AbsolutePathMissing_False()
    {
        var cfg = new TemplateConfig { LocalSourceDir = @"D:\definitely\not\a\real\dir-" + Path.GetRandomFileName() };
        Assert.False(cfg.LocalDirExists(null));
        Assert.False(cfg.LocalDirExists(@"D:\some\nonexistent\anchor"));
    }

    [Fact]
    public void LocalDirExists_RelativePath_WithAnchorThatExists_True()
    {
        var dirName = "tmplcfg-rel-" + Path.GetRandomFileName();
        var anchor = Path.Combine(Path.GetTempPath(), "tmplcfg-anchor-" + Path.GetRandomFileName());
        Directory.CreateDirectory(anchor);
        // 在 anchor 下创建子目录,LocalSourceDir 用子目录名 → resolve = anchor/dirName
        var subDir = Path.Combine(anchor, dirName);
        Directory.CreateDirectory(subDir);
        try
        {
            var cfg = new TemplateConfig { LocalSourceDir = dirName };
            Assert.True(cfg.LocalDirExists(anchor));
        }
        finally { try { Directory.Delete(anchor, recursive: true); } catch { } }
    }

    [Fact]
    public void LocalDirExists_RelativePath_WithMissingAnchor_False()
    {
        var cfg = new TemplateConfig { LocalSourceDir = "OpenVoice" };
        Assert.False(cfg.LocalDirExists(@"D:\this\anchor\does\not\exist-" + Path.GetRandomFileName()));
    }

    [Fact]
    public void LocalDirBadge_ExistsDirectory_ReturnsEmpty_HintStringMatches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tmplcfg-badge-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new TemplateConfig { LocalSourceDir = dir };
            Assert.Equal("", cfg.LocalDirBadge(null));
            Assert.Equal("本地目录为空", TemplateConfig.LocalDirBadgeHint);
        }
        finally { try { Directory.Delete(dir); } catch { } }
    }

    [Fact]
    public void LocalDirBadge_MissingDirectory_ReturnsHint()
    {
        var cfg = new TemplateConfig { LocalSourceDir = @"D:\no\such\dir-" + Path.GetRandomFileName() };
        Assert.Equal(TemplateConfig.LocalDirBadgeHint, cfg.LocalDirBadge(null));
    }

    [Fact]
    public void LocalDirMissing_NotSerialized()
    {
        // 运行时状态 — JsonIgnore
        var cfg = new TemplateConfig
        {
            LocalSourceDir = "ComfyUI",
            LocalDirMissing = true,
        };
        var json = JsonSerializer.Serialize(cfg, JsonOptions.Default);
        Assert.DoesNotContain("local_dir_missing", json);
        var restored = JsonSerializer.Deserialize<TemplateConfig>(json, JsonOptions.Default);
        Assert.NotNull(restored);
        Assert.False(restored!.LocalDirMissing);   // 反序列化回默认 false
    }
}
