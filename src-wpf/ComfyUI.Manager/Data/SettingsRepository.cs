using System;
using System.IO;
using System.Text.Json;
using SysEnv = System.Environment;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// SettingsRepository:reads/writes the JSON settings file at
/// %APPDATA%\ComfyUI-Manager\settings.json. Binds to the same
/// <see cref="Settings"/> model the WPF UI uses, so the load/save path and
/// the view-model bindings share one shape.
/// </summary>
public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public SettingsRepository() : this(DefaultSettingsPath())
    {
    }

    public SettingsRepository(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    private static string DefaultSettingsPath()
    {
        var appData = SysEnv.GetFolderPath(
            SysEnv.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ComfyUI-Manager", "settings.json");
    }

    public Settings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Settings();
        }

        var json = File.ReadAllText(_settingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Settings();
        }

        return JsonSerializer.Deserialize<Settings>(json, JsonOptions)
            ?? new Settings();
    }

    public void Save(Settings s)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(s, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
