using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.1 (settings-to-inf):UiPreferencesService 改 INF 持久化的回归测试。
/// UiPreferences 字段全简单(8 个),不需要 JSON-encode 复杂值。
/// </summary>
public class UiPreferencesServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly LocalDataPaths _paths;
    private readonly UiPreferencesService _svc;

    public UiPreferencesServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "ui-prefs-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new LocalDataPaths(_projectRoot);
        _svc = new UiPreferencesService(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultPath_IsUnderConfigUnderProjectRoot_Inf()
    {
        Assert.Equal(Path.Combine(_projectRoot, "config", "ui-preferences.inf"), _svc.DefaultPath);
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
    public void LoadFromFile_PartialInf_ReturnsDefaultsForMissing()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_svc.DefaultPath, "window_width = 1100\n");

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1100, prefs.WindowWidth);
        Assert.Null(prefs.WindowHeight);
        Assert.False(prefs.WindowMaximized);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void LoadFromFile_CorruptInf_ReturnsDefaults_DoesNotThrow()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_svc.DefaultPath, "this is not a valid inf line ====");

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Null(prefs.WindowWidth);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void SaveToFile_CreatesParentDirIfMissing()
    {
        // 验 InfWriter 透传 CreateDirectory 到深层路径 — LocalDataPaths ctor
        // 已创建 config/,这里用 deep/nested 路径触发 mkdir。
        var deep = Path.Combine(_paths.ConfigDirectory, "deep", "nested", "ui.inf");
        Assert.False(Directory.Exists(Path.GetDirectoryName(deep)));
        _svc.SaveToFile(deep, new UiPreferences { WindowWidth = 999 });
        Assert.True(Directory.Exists(Path.GetDirectoryName(deep)!));
        Assert.True(File.Exists(deep));
    }

    [Fact]
    public void LoadFromFile_LegacyJsonExists_MigratesToInf()
    {
        // 老 ui-preferences.json 存在 → 读 JSON → 写 INF → 删 JSON 一次性迁移
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var legacyJsonPath = Path.Combine(_paths.ConfigDirectory, "ui-preferences.json");
        File.WriteAllText(legacyJsonPath, """
            {
              "window_width": 1024,
              "window_height": 768,
              "window_left": 10,
              "window_top": 20,
              "window_maximized": true,
              "sidebar_visible": false,
              "last_selected_env_id": "env-legacy",
              "last_view_name": "Catalog"
            }
            """);

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1024, prefs.WindowWidth);
        Assert.Equal(768, prefs.WindowHeight);
        Assert.Equal("env-legacy", prefs.LastSelectedEnvId);
        Assert.Equal("Catalog", prefs.LastViewName);

        Assert.True(File.Exists(_svc.DefaultPath));
        Assert.False(File.Exists(legacyJsonPath));
    }
}