using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v0.6.22++:迁移 2-bool(http_proxy_enabled + http_proxy_use_system)→ HttpProxyMode enum;
/// per-source bool → ModelSourceProxyMode enum。
///
/// v1.0.0.1 (settings-to-inf):测迁路径走 dual-arg ctor —— primary = settings.inf,
/// legacy = settings.json。Load 检 legacy JSON 触发迁移 → 写 INF → 删 JSON。
/// </summary>
public class SettingsRepositoryMigrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _configDir;
    private readonly string _managerDir;
    private readonly string _settingsInfPath;
    private readonly string _legacyJsonPath;

    public SettingsRepositoryMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"settings-proxy-mig-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tmpDir, "config");
        _managerDir = Path.Combine(_tmpDir, ".manager");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_managerDir);
        _settingsInfPath = Path.Combine(_configDir, "settings.inf");
        _legacyJsonPath = Path.Combine(_managerDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private SettingsRepository CreateRepo() => new SettingsRepository(_settingsInfPath, _legacyJsonPath);

    [Fact]
    public void Load_OldGlobalProxyTrueUseSystemTrue_MigratesToInheritSystem()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"http_proxy_enabled\": true,\n" +
            "  \"http_proxy_use_system\": true\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal<HttpProxyMode>(HttpProxyMode.InheritSystem, s.HttpProxyMode);
        Assert.Equal<ModelSourceProxyMode>(ModelSourceProxyMode.InheritGlobal, s.ModelSourceCivitAiProxyMode);
        Assert.Equal<ModelSourceProxyMode>(ModelSourceProxyMode.InheritGlobal, s.ModelSourceHuggingFaceProxyMode);
    }

    [Fact]
    public void Load_OldGlobalProxyTrueUseSystemFalse_MigratesToCustom()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"http_proxy_enabled\": true,\n" +
            "  \"http_proxy_use_system\": false,\n" +
            "  \"http_proxy_url\": \"192.168.1.1\",\n" +
            "  \"http_proxy_port\": 7890\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(HttpProxyMode.Custom, s.HttpProxyMode);
        Assert.Equal("192.168.1.1", s.HttpProxyUrl);
        Assert.Equal(7890, s.HttpProxyPort);
    }

    [Fact]
    public void Load_OldGlobalProxyFalse_MigratesToOff()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"http_proxy_enabled\": false\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(HttpProxyMode.Off, s.HttpProxyMode);
    }

    [Fact]
    public void Load_OldCivitAiUseProxyTrue_MigratesToInheritGlobal()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"model_source_civitai_use_proxy\": true\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(ModelSourceProxyMode.InheritGlobal, s.ModelSourceCivitAiProxyMode);
    }

    [Fact]
    public void Load_OldCivitAiUseProxyFalse_MigratesToOff()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"model_source_civitai_use_proxy\": false\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(ModelSourceProxyMode.Off, s.ModelSourceCivitAiProxyMode);
    }

    [Fact]
    public void Load_OldHuggingFaceUseProxy_Migrates()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"model_source_huggingface_use_proxy\": false\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(ModelSourceProxyMode.Off, s.ModelSourceHuggingFaceProxyMode);
    }

    [Fact]
    public void Load_OldProxyFields_GetRewrittenToNewSchema_NoOldKeysInFile()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"http_proxy_enabled\": true,\n" +
            "  \"http_proxy_use_system\": false,\n" +
            "  \"http_proxy_url\": \"127.0.0.1\",\n" +
            "  \"http_proxy_port\": 7890,\n" +
            "  \"model_source_civitai_use_proxy\": true,\n" +
            "  \"model_source_huggingface_use_proxy\": false\n" +
            "}");

        var repo = CreateRepo();
        repo.Load();

        // 迁移触发:写 .inf → 删老 .json
        Assert.True(File.Exists(_settingsInfPath));
        Assert.False(File.Exists(_legacyJsonPath));

        var reloadedInf = File.ReadAllText(_settingsInfPath);
        // 老 key 应全部消失(不会出现在 INF 文件里)
        Assert.DoesNotContain("http_proxy_enabled", reloadedInf);
        Assert.DoesNotContain("http_proxy_use_system", reloadedInf);
        Assert.DoesNotContain("model_source_civitai_use_proxy", reloadedInf);
        Assert.DoesNotContain("model_source_huggingface_use_proxy", reloadedInf);
        // 新 key 应出现
        Assert.Contains("http_proxy_mode", reloadedInf);
        Assert.Contains("model_source_civitai_proxy_mode", reloadedInf);
        Assert.Contains("model_source_huggingface_proxy_mode", reloadedInf);
    }

    [Fact]
    public void Load_NewSchemaFile_NoMigration()
    {
        // 新 schema 文件:enum 字符串形式 — 应该原样 load,无迁移
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"http_proxy_mode\": \"Custom\",\n" +
            "  \"http_proxy_url\": \"127.0.0.1\",\n" +
            "  \"http_proxy_port\": 7890,\n" +
            "  \"model_source_civitai_proxy_mode\": \"AlwaysOn\",\n" +
            "  \"model_source_huggingface_proxy_mode\": \"Off\"\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        Assert.Equal(HttpProxyMode.Custom, s.HttpProxyMode);
        Assert.Equal(ModelSourceProxyMode.AlwaysOn, s.ModelSourceCivitAiProxyMode);
        Assert.Equal(ModelSourceProxyMode.Off, s.ModelSourceHuggingFaceProxyMode);
    }

    [Fact]
    public void Defaults_FreshSettings_HttpProxyMode_IsInheritSystem()
    {
        // 没文件 → new Settings() → 默认 HttpProxyMode = InheritSystem
        var repo = new SettingsRepository(_settingsInfPath);
        var s = repo.Load();

        Assert.Equal(HttpProxyMode.InheritSystem, s.HttpProxyMode);
        Assert.Equal(ModelSourceProxyMode.InheritGlobal, s.ModelSourceCivitAiProxyMode);
        Assert.Equal(ModelSourceProxyMode.InheritGlobal, s.ModelSourceHuggingFaceProxyMode);
    }
}