using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Data;

/// <summary>
/// Settings persisted by the Python CLI to
/// %APPDATA%\ComfyUI-Manager\settings.json.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("db_path")]
    public string DbPath { get; set; } = "";
    [JsonPropertyName("git_portable_path")]
    public string GitPortablePath { get; set; } = "";
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "material_purple";
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh_CN";
    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "info";
}

/// <summary>
/// SettingsRepository:reads/writes the JSON settings file emitted by the
/// Python CLI. The file lives next to the SQLite catalog under
/// %APPDATA%\ComfyUI-Manager.
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
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
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
