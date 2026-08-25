using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Inf;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.1 (settings-to-inf):SettingsRepository 改 INF 持久化的回归测试。
/// 覆盖:Load/Save 基本、legacy .json fallback、复杂字段(List/Dict) round-trip、
/// 简单字段 round-trip、主题非法值 normalize。
/// </summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "settings-repo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private SettingsRepository NewRepo(string legacyJsonPath)
        => new SettingsRepository(
            Path.Combine(_tempDir, "config", "settings.inf"),
            legacyJsonPath);

    private SettingsRepository NewRepoNoLegacy()
        => new SettingsRepository(Path.Combine(_tempDir, "config", "settings.inf"));

    [Fact]
    public void Load_NoFiles_ReturnsDefaults()
    {
        var repo = NewRepoNoLegacy();
        var s = repo.Load();
        Assert.NotNull(s);
        Assert.Equal("material_purple", s.Theme);
        Assert.Equal("dark", s.ThemeMode);
        Assert.Equal(HttpProxyMode.InheritSystem, s.HttpProxyMode);
    }

    [Fact]
    public void Save_WritesInfFile_ThenLoad_RoundTripsSimpleFields()
    {
        var repo = NewRepoNoLegacy();
        var s = new Settings
        {
            Theme = "dark",
            Language = "en_US",
            CatalogAutoRefresh = true,
            CatalogCacheTtlMinutes = 120,
            HttpProxyMode = HttpProxyMode.Custom,
            HttpProxyUrl = "http://proxy.local",
            HttpProxyPort = 8080,
        };
        repo.Save(s);

        var loaded = repo.Load();
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("en_US", loaded.Language);
        Assert.True(loaded.CatalogAutoRefresh);
        Assert.Equal(120, loaded.CatalogCacheTtlMinutes);
        Assert.Equal(HttpProxyMode.Custom, loaded.HttpProxyMode);
        Assert.Equal("http://proxy.local", loaded.HttpProxyUrl);
        Assert.Equal(8080, loaded.HttpProxyPort);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsComplexFields_QuerySources()
    {
        var repo = NewRepoNoLegacy();
        var s = new Settings();
        s.QuerySources.Clear();
        s.QuerySources.Add(new NodeSource
        {
            Name = "custom",
            Url = "https://example.com/list.json",
        });
        s.ActiveQuerySourceName = "custom";
        repo.Save(s);

        var loaded = repo.Load();
        Assert.Single(loaded.QuerySources);
        Assert.Equal("custom", loaded.QuerySources[0].Name);
        Assert.Equal("https://example.com/list.json", loaded.QuerySources[0].Url);
        Assert.Equal("custom", loaded.ActiveQuerySourceName);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsComplexFields_Templates()
    {
        var repo = NewRepoNoLegacy();
        var s = new Settings();
        s.Templates.Clear();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = "ComfyUITemplate",
            EntryScript = "main.py",
            EntryArgs = "--port 8188",
            ModelsSubdir = "models",
        };
        repo.Save(s);

        var loaded = repo.Load();
        Assert.True(loaded.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("ComfyUITemplate", loaded.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("main.py", loaded.Templates["ComfyUI"].EntryScript);
        Assert.Equal("--port 8188", loaded.Templates["ComfyUI"].EntryArgs);
        Assert.Equal("models", loaded.Templates["ComfyUI"].ModelsSubdir);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsComplexFields_CommonNodes()
    {
        var repo = NewRepoNoLegacy();
        var s = new Settings();
        s.CommonNodes.Clear();
        s.CommonNodes.Add(new CommonNodeEntry
        {
            Id = "ltdrdata/ComfyUI-Manager",
            DisplayName = "ComfyUI Manager",
            IsBuiltIn = true,
            Enabled = false,
        });
        repo.Save(s);

        var loaded = repo.Load();
        Assert.Single(loaded.CommonNodes);
        Assert.Equal("ltdrdata/ComfyUI-Manager", loaded.CommonNodes[0].Id);
        Assert.Equal("ComfyUI Manager", loaded.CommonNodes[0].DisplayName);
        Assert.True(loaded.CommonNodes[0].IsBuiltIn);
        Assert.False(loaded.CommonNodes[0].Enabled);
    }

    [Fact]
    public void Load_InfPrefersOverJson_WhenBothExist()
    {
        // .inf 存在 + 老 .json 也存在 → 优先 .inf(可能是用户手动重写后被覆盖的边缘情况)
        var infPath = Path.Combine(_tempDir, "config", "settings.inf");
        var jsonPath = Path.Combine(_tempDir, "old.json");

        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        InfWriter.Write(infPath, new System.Collections.Generic.Dictionary<string, string>
        {
            ["theme"] = "from_inf",
        });
        File.WriteAllText(jsonPath, "{\"theme\": \"from_json\"}");

        var repo = new SettingsRepository(infPath, jsonPath);
        var s = repo.Load();
        Assert.Equal("from_inf", s.Theme);
        // .json 仍在 — 优先模式不删(用户可能想保留)
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public void Load_FallsBackToLegacyJson_OnFirstRun_WritesInfDeletesJson()
    {
        var jsonPath = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(jsonPath, """
            {
              "theme": "dark",
              "language": "zh_CN",
              "catalog_auto_refresh": true,
              "http_proxy_mode": "Custom",
              "http_proxy_url": "http://p:8080"
            }
            """);
        var infPath = Path.Combine(_tempDir, "config", "settings.inf");
        var repo = new SettingsRepository(infPath, jsonPath);

        var s = repo.Load();
        Assert.Equal("dark", s.Theme);
        Assert.True(s.CatalogAutoRefresh);
        Assert.Equal(HttpProxyMode.Custom, s.HttpProxyMode);

        // 一次性迁移:.inf 写出来了 + 老 .json 删了
        Assert.True(File.Exists(infPath));
        Assert.False(File.Exists(jsonPath));
    }

    [Fact]
    public void LoadWithRawJson_FromInf_ReturnsNullRawJson()
    {
        var infPath = Path.Combine(_tempDir, "config", "settings.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(infPath)!);
        InfWriter.Write(infPath, new System.Collections.Generic.Dictionary<string, string>
        {
            ["theme"] = "dark",
        });

        var repo = new SettingsRepository(infPath);
        var (s, rawJson) = repo.LoadWithRawJson();
        Assert.Equal("dark", s.Theme);
        Assert.Null(rawJson); // .inf 没有老字段要迁移
    }

    [Fact]
    public void LoadWithRawJson_FromLegacyJson_ReturnsJsonText()
    {
        var jsonPath = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(jsonPath, """
            {
              "theme": "dark",
              "template_comfyui_dir": "MyOldComfyUI"
            }
            """);
        var infPath = Path.Combine(_tempDir, "config", "settings.inf");

        var repo = new SettingsRepository(infPath, jsonPath);
        var (s, rawJson) = repo.LoadWithRawJson();
        Assert.Equal("dark", s.Theme);
        Assert.NotNull(rawJson);
        Assert.Contains("template_comfyui_dir", rawJson!);
    }

    [Fact]
    public void Load_InvalidThemeMode_NormalizesToDark()
    {
        var repo = NewRepoNoLegacy();
        var s = new Settings { ThemeMode = "invalid_value" };
        repo.Save(s);

        var loaded = repo.Load();
        Assert.Equal("dark", loaded.ThemeMode);
    }

    [Fact]
    public void Load_JsonWithHttpProxyBoolMigration_ConvertsToEnum()
    {
        // 老 .json 含 http_proxy_enabled + http_proxy_use_system bool → 新 enum
        var jsonPath = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(jsonPath, """
            {
              "http_proxy_enabled": true,
              "http_proxy_use_system": true
            }
            """);
        var infPath = Path.Combine(_tempDir, "config", "settings.inf");
        var repo = new SettingsRepository(infPath, jsonPath);

        var s = repo.Load();
        Assert.Equal(HttpProxyMode.InheritSystem, s.HttpProxyMode);
    }
}