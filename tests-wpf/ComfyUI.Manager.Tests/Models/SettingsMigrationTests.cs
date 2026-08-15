using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsMigrationTests : System.IDisposable
{
    private readonly string _tmpDir;
    private readonly string _settingsPath;

    public SettingsMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"settings-mig-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _settingsPath = Path.Combine(_tmpDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_OldGitProxyKeys_MigratesToHttpProxy()
    {
        // 写一份 v0.6.15.3 old-schema settings.json
        File.WriteAllText(_settingsPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"192.168.1.1\",\n" +
            "  \"git_proxy_port\": 7890\n" +
            "}");

        var repo = new SettingsRepository(_settingsPath);
        var s = repo.Load();

        Assert.True(s.HttpProxyEnabled);
        Assert.Equal("192.168.1.1", s.HttpProxyUrl);
        Assert.Equal(7890, s.HttpProxyPort);
    }

    [Fact]
    public void Load_MigrationHappens_SavesBackNewSchemaWithoutOldKeys()
    {
        File.WriteAllText(_settingsPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"old.local\",\n" +
            "  \"git_proxy_port\": 8888\n" +
            "}");

        var repo = new SettingsRepository(_settingsPath);
        repo.Load();
        var reloadedJson = File.ReadAllText(_settingsPath);

        // 旧 key 应被写回删除
        Assert.DoesNotContain("git_proxy_enabled", reloadedJson);
        Assert.DoesNotContain("git_proxy_url", reloadedJson);
        Assert.DoesNotContain("git_proxy_port", reloadedJson);
        // 新 key 写出
        Assert.Contains("http_proxy_enabled", reloadedJson);
        Assert.Contains("http_proxy_url", reloadedJson);
        Assert.Contains("http_proxy_port", reloadedJson);
    }
}
