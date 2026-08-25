using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v0.6.15.4: 迁 git_proxy_* → http_proxy_*;v0.6.22++ 再迁 http_proxy_* bool → http_proxy_mode enum。
///
/// v1.0.0.1 (settings-to-inf):迁路径走 dual-arg ctor —— primary = settings.inf,
/// legacy = settings.json。Load 检 legacy JSON 触发迁移 → 写 INF → 删 JSON。
/// </summary>
public class SettingsMigrationTests : System.IDisposable
{
    private readonly string _tmpDir;
    private readonly string _configDir;
    private readonly string _managerDir;
    private readonly string _settingsInfPath;
    private readonly string _legacyJsonPath;

    public SettingsMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"settings-mig-{System.Guid.NewGuid():N}");
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
    public void Load_OldGitProxyKeys_MigratesToHttpProxy()
    {
        // 写一份 v0.6.15.3 old-schema settings.json
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"192.168.1.1\",\n" +
            "  \"git_proxy_port\": 7890\n" +
            "}");

        var repo = CreateRepo();
        var s = repo.Load();

        // v0.6.22++:git_proxy_* → http_proxy_* → http_proxy_mode=Custom
        Assert.Equal(HttpProxyMode.Custom, s.HttpProxyMode);
        Assert.Equal("192.168.1.1", s.HttpProxyUrl);
        Assert.Equal(7890, s.HttpProxyPort);
    }

    [Fact]
    public void Load_MigrationHappens_SavesBackNewSchemaWithoutOldKeys()
    {
        File.WriteAllText(_legacyJsonPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"old.local\",\n" +
            "  \"git_proxy_port\": 8888\n" +
            "}");

        var repo = CreateRepo();
        repo.Load();

        // 迁移触发:写 .inf → 删老 .json
        Assert.True(File.Exists(_settingsInfPath));
        Assert.False(File.Exists(_legacyJsonPath));

        var reloadedInf = File.ReadAllText(_settingsInfPath);

        // 旧 key 应被写回删除
        Assert.DoesNotContain("git_proxy_enabled", reloadedInf);
        Assert.DoesNotContain("git_proxy_url", reloadedInf);
        Assert.DoesNotContain("git_proxy_port", reloadedInf);
        // v0.6.22++:老 schema → http_proxy_enabled,再 → http_proxy_mode=Custom
        // 一次 Load 触发两段迁移。
        // 第一段 git_proxy_* → http_proxy_enabled=true (写入)
        // 第二段 http_proxy_enabled=true + http_proxy_use_system=false → http_proxy_mode=Custom (写入)
        // 最终 schema 只剩新字段。
        Assert.DoesNotContain("http_proxy_enabled", reloadedInf);
        Assert.DoesNotContain("http_proxy_use_system", reloadedInf);
        Assert.Contains("http_proxy_mode", reloadedInf);
        Assert.Contains("http_proxy_url", reloadedInf);
        Assert.Contains("http_proxy_port", reloadedInf);
    }
}
