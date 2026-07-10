using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private Settings _settings = new();

    public SettingsViewModel(ApiClient api)
    {
        _api = api;
        _ = LoadAsync();
    }

    public List<string> Languages { get; } = new() { "zh_CN", "en_US" };
    public List<string> ThemeModes { get; } = new() { "light", "dark", "system" };

    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; _ = SaveAsync("language", value); }
    }
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set { _settings.ThemeMode = value; _ = SaveAsync("theme_mode", value); }
    }
    public int CacheTtlMinutes
    {
        get => _settings.CatalogCacheTtlMinutes;
        set { _settings.CatalogCacheTtlMinutes = value; _ = SaveAsync("catalog_cache_ttl_minutes", value); }
    }
    public string CompatApiBaseUrl
    {
        get => _settings.CompatApiBaseUrl;
        set { _settings.CompatApiBaseUrl = value; _ = SaveAsync("compat_api_base_url", value); }
    }

    public RelayCommand CheckUpdateCommand { get; } = new RelayCommand(_ =>
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases",
            UseShellExecute = true,
        });
    });

    private async Task LoadAsync()
    {
        var r = await _api.PostAsync<Settings>("settings/get-all", new { });
        if (r.Ok && r.Value is not null)
        {
            _settings = r.Value;
            RaisePropertyChanged(nameof(Language));
            RaisePropertyChanged(nameof(ThemeMode));
            RaisePropertyChanged(nameof(CacheTtlMinutes));
            RaisePropertyChanged(nameof(CompatApiBaseUrl));
        }
    }

    private async Task SaveAsync(string key, object value)
    {
        await _api.PostAsync<object>(
            "settings/set-value", new { key, value });
    }
}
