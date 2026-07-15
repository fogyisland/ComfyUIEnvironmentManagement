using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Microsoft.Win32;

namespace ComfyUI.Manager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsRepository _repo;
    private readonly GitProxyConfig _proxy;
    private Settings _settings;

    public SettingsViewModel(SettingsRepository repo, GitProxyConfig proxy)
    {
        _repo = repo;
        _proxy = proxy;
        _settings = _repo.Load();
        ExtraPaths = new ObservableCollection<ExtraPath>(_settings.ExtraPaths);
        ExtraPaths.CollectionChanged += (_, _) =>
        {
            _settings.ExtraPaths = new List<ExtraPath>(ExtraPaths);
            _repo.Save(_settings);
        };
        AddExtraPathCommand = new RelayCommand(_ => ExtraPaths.Add(new ExtraPath()));
        RemoveExtraPathCommand = new RelayCommand(p =>
        {
            if (p is ExtraPath ep) ExtraPaths.Remove(ep);
        });
        RaiseAllPropertiesChanged();
    }

    public List<string> Languages { get; } = new() { "zh_CN", "en_US" };
    public List<string> ThemeModes { get; } = new() { "light", "dark", "system" };

    // —— 基础 / 显示 ——
    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set { _settings.ThemeMode = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public int CacheTtlMinutes
    {
        get => _settings.CatalogCacheTtlMinutes;
        set { _settings.CatalogCacheTtlMinutes = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string CompatApiBaseUrl
    {
        get => _settings.CompatApiBaseUrl;
        set { _settings.CompatApiBaseUrl = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }

    // —— 路径 ——
    public string TemplatePythonDir
    {
        get => _settings.TemplatePythonDir;
        set { _settings.TemplatePythonDir = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string TemplateComfyuiDir
    {
        get => _settings.TemplateComfyuiDir;
        set { _settings.TemplateComfyuiDir = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string EnvsDir
    {
        get => _settings.EnvsDir;
        set { _settings.EnvsDir = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string GlobalNodesDir
    {
        get => _settings.GlobalNodesDir;
        set { _settings.GlobalNodesDir = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }

    // —— 环境 / 工具 ——
    public string PythonVenvBaseline
    {
        get => _settings.PythonVenvBaseline;
        set { _settings.PythonVenvBaseline = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string GitExe
    {
        get => _settings.GitExe;
        set { _settings.GitExe = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string GitProxyUrl
    {
        // getter/setter 都双写:_settings(持久化) + _proxy(运行期 live)。
        // 让 git 代理开关能即时生效,不用重启。
        get => _proxy.Url;
        set
        {
            _proxy.Url = value;
            _settings.GitProxyUrl = value;
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }
    public int GitProxyPort
    {
        get => _proxy.Port;
        set
        {
            _proxy.Port = value;
            _settings.GitProxyPort = value;
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }
    public bool GitProxyEnabled
    {
        get => _proxy.Enabled;
        set
        {
            _proxy.Enabled = value;
            _settings.GitProxyEnabled = value;
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    // —— 高级:用户自定义 path 表 ——
    public ObservableCollection<ExtraPath> ExtraPaths { get; }

    public RelayCommand AddExtraPathCommand { get; }
    public RelayCommand RemoveExtraPathCommand { get; }

    public RelayCommand CheckUpdateCommand { get; } = new RelayCommand(_ =>
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases",
            UseShellExecute = true,
        });
    });

    // —— File pickers:用 Microsoft.Win32 (违反严格 MVVM,但 win-x64 单平台 OK) ——
    public string? PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "选择目录" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void RaiseAllPropertiesChanged()
    {
        RaisePropertyChanged(nameof(Language));
        RaisePropertyChanged(nameof(ThemeMode));
        RaisePropertyChanged(nameof(CacheTtlMinutes));
        RaisePropertyChanged(nameof(CompatApiBaseUrl));
        RaisePropertyChanged(nameof(TemplatePythonDir));
        RaisePropertyChanged(nameof(TemplateComfyuiDir));
        RaisePropertyChanged(nameof(EnvsDir));
        RaisePropertyChanged(nameof(GlobalNodesDir));
        RaisePropertyChanged(nameof(PythonVenvBaseline));
        RaisePropertyChanged(nameof(GitExe));
        RaisePropertyChanged(nameof(GitProxyUrl));
        RaisePropertyChanged(nameof(GitProxyPort));
        RaisePropertyChanged(nameof(GitProxyEnabled));
    }
}