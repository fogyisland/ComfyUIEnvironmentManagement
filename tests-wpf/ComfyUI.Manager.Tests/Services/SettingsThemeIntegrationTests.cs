using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.9 T2:SettingsThemeIntegrationTests — Settings 持久化 + ThemeMode 解析路径。
/// 覆盖 4 个关键路径:
/// 1. SettingsRepository 写读 ThemeMode="dark" 保留值
/// 2. SettingsRepository.Load 缺文件回退默认(ThemeMode="dark" per G5)
/// 3. SettingsRepository.Load 非法 theme_mode 值 normalize → "dark"
/// 4. SettingsViewModel.ParseThemeMode 4 路径解析(light/dark/system/invalid)
/// </summary>
public class SettingsThemeIntegrationTests
{
    private static string TempSettingsFile()
    {
        return Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void Write_ThenRead_PreservesThemeMode_Dark()
    {
        var path = TempSettingsFile();
        try
        {
            var repo = new SettingsRepository(path);
            var s = new Settings { ThemeMode = "dark" };
            repo.Save(s);
            var read = repo.Load();
            Assert.Equal("dark", read.ThemeMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_FallsBackToDark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json");
        var repo = new SettingsRepository(path);
        var s = repo.Load();
        // G5: 缺省 Dark(老的 "system" 字段已被 T1 默认值改为 "dark")
        Assert.Equal("dark", s.ThemeMode);
    }

    [Fact]
    public void InvalidValue_FallsBackToDarkOnNextLoad()
    {
        var path = TempSettingsFile();
        try
        {
            // 写一个非法值进 settings.json
            File.WriteAllText(path, "{\"theme_mode\": \"nonsense\"}");
            var repo = new SettingsRepository(path);
            var s = repo.Load();
            // SettingsRepository.Load 应在 deserialize 后 normalize 非法值 → "dark"
            Assert.Equal("dark", s.ThemeMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ThemeMode_ParseToThemeServiceEnum()
    {
        // "light" → Light, "dark" → Dark, "system" → FollowSystem, invalid → Dark
        Assert.Equal(ThemeMode.Light, SettingsViewModel.ParseThemeMode("light"));
        Assert.Equal(ThemeMode.Dark, SettingsViewModel.ParseThemeMode("dark"));
        Assert.Equal(ThemeMode.FollowSystem, SettingsViewModel.ParseThemeMode("system"));
        Assert.Equal(ThemeMode.Dark, SettingsViewModel.ParseThemeMode("nonsense"));
    }
}