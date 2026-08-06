using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class UiPreferencesServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _configDir;
    private readonly UiPreferencesService _svc;

    public UiPreferencesServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "ui-prefs-tests-" + Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_projectRoot, "config");
        _svc = new UiPreferencesService(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultPath_IsUnderConfigUnderProjectRoot()
    {
        Assert.Equal(Path.Combine(_projectRoot, "config", "ui-preferences.json"), _svc.DefaultPath);
    }

    [Fact]
    public void LoadFromFile_NoFile_ReturnsDefaults_AndFiresLoaded()
    {
        var loaded = (UiPreferences?)null;
        _svc.Loaded += (_, p) => loaded = p;

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);

        Assert.Null(prefs.WindowWidth);
        Assert.True(prefs.SidebarVisible);
        Assert.NotNull(loaded);
        Assert.Equal(prefs.WindowWidth, loaded!.WindowWidth);
    }

    [Fact]
    public void SaveToFile_ThenLoadFromFile_RoundTripsAllFields()
    {
        var orig = new UiPreferences
        {
            WindowWidth = 1200,
            WindowHeight = 800,
            WindowLeft = 50,
            WindowTop = 50,
            WindowMaximized = true,
            SidebarVisible = false,
            LastSelectedEnvId = "env-x",
            LastViewName = "Environments",
        };
        _svc.SaveToFile(_svc.DefaultPath, orig);

        Assert.True(File.Exists(_svc.DefaultPath));

        var back = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1200, back.WindowWidth);
        Assert.Equal(800, back.WindowHeight);
        Assert.True(back.WindowMaximized);
        Assert.False(back.SidebarVisible);
        Assert.Equal("env-x", back.LastSelectedEnvId);
        Assert.Equal("Environments", back.LastViewName);
    }

    [Fact]
    public void LoadFromFile_MissingFields_ReturnsDefaults()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_svc.DefaultPath, "{\"window_width\": 1100}");  // 只写一个字段

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1100, prefs.WindowWidth);
        Assert.Null(prefs.WindowHeight);
        Assert.False(prefs.WindowMaximized);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void LoadFromFile_CorruptJson_ReturnsDefaults_DoesNotThrow()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_svc.DefaultPath, "{ this is not valid JSON :::");

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Null(prefs.WindowWidth);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void SaveToFile_CreatesParentDirIfMissing()
    {
        // _configDir 不存在,SaveToFile 应该自动建
        Assert.False(Directory.Exists(_configDir));
        _svc.SaveToFile(_svc.DefaultPath, new UiPreferences { WindowWidth = 999 });
        Assert.True(Directory.Exists(_configDir));
        Assert.True(File.Exists(_svc.DefaultPath));
    }
}
