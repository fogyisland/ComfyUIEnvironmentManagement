using System.Collections.Generic;
using System.Diagnostics;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsRepository _repo;
    private Settings _settings;

    public SettingsViewModel(SettingsRepository repo)
    {
        _repo = repo;
        _settings = _repo.Load();
        RaisePropertyChanged(nameof(Language));
        RaisePropertyChanged(nameof(ThemeMode));
        RaisePropertyChanged(nameof(CacheTtlMinutes));
        RaisePropertyChanged(nameof(CompatApiBaseUrl));
    }

    public List<string> Languages { get; } = new() { "zh_CN", "en_US" };
    public List<string> ThemeModes { get; } = new() { "light", "dark", "system" };

    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; _repo.Save(_settings); }
    }
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set { _settings.ThemeMode = value; _repo.Save(_settings); }
    }
    public int CacheTtlMinutes
    {
        get => _settings.CatalogCacheTtlMinutes;
        set { _settings.CatalogCacheTtlMinutes = value; _repo.Save(_settings); }
    }
    public string CompatApiBaseUrl
    {
        get => _settings.CompatApiBaseUrl;
        set { _settings.CompatApiBaseUrl = value; _repo.Save(_settings); }
    }

    public RelayCommand CheckUpdateCommand { get; } = new RelayCommand(_ =>
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases",
            UseShellExecute = true,
        });
    });
}
