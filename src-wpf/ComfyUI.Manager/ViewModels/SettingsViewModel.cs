using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Microsoft.Win32;

namespace ComfyUI.Manager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsRepository _repo;
    private readonly GitProxyConfig _proxy;
    private readonly CatalogRefreshService _refreshService;
    private Settings _settings;

    private bool _isAddQuerySourceOpen;
    private bool _isAddDownloadSourceOpen;
    private string _newQuerySourceName = "";
    private string _newQuerySourceUrl = "";
    private string _newDownloadSourceName = "";
    private string _newDownloadSourceUrl = "";

    public SettingsViewModel(SettingsRepository repo, GitProxyConfig proxy, CatalogRefreshService refreshService)
    {
        _repo = repo;
        _proxy = proxy;
        _refreshService = refreshService;
        _settings = _repo.Load();
        // 首次启动/无 settings.json 时把默认值(node source 列表 + active 名)填上。
        // 生产环境 App.xaml.cs 也会 Apply,这里再 Apply 一次保证测试直构造 VM
        // 时也能拿到默认值(幂等:已存在的非空列表/active 名不会被覆盖)。
        SettingsDefaults.Apply(_settings, AppContext.BaseDirectory);
        _repo.Save(_settings);
        ExtraPaths = new ObservableCollection<ExtraPath>(_settings.ExtraPaths);
        ExtraPaths.CollectionChanged += (_, _) =>
        {
            _settings.ExtraPaths = new List<ExtraPath>(ExtraPaths);
            _repo.Save(_settings);
        };
        QuerySources = new ObservableCollection<NodeSource>(_settings.QuerySources);
        QuerySources.CollectionChanged += (_, _) =>
        {
            _settings.QuerySources = new List<NodeSource>(QuerySources);
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActiveQuerySource));
        };
        DownloadSources = new ObservableCollection<NodeSource>(_settings.DownloadSources);
        DownloadSources.CollectionChanged += (_, _) =>
        {
            _settings.DownloadSources = new List<NodeSource>(DownloadSources);
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActiveDownloadSource));
        };
        AddExtraPathCommand = new RelayCommand(_ => ExtraPaths.Add(new ExtraPath()));
        RemoveExtraPathCommand = new RelayCommand(p =>
        {
            if (p is ExtraPath ep) ExtraPaths.Remove(ep);
        });
        AddQuerySourceCommand = new RelayCommand(_ =>
        {
            NewQuerySourceName = "";
            NewQuerySourceUrl = "";
            IsAddQuerySourceOpen = true;
        });
        RemoveQuerySourceCommand = new RelayCommand(p =>
        {
            if (p is NodeSource ns)
            {
                var wasActive = ns.Name == _settings.ActiveQuerySourceName;
                QuerySources.Remove(ns);
                // 删的是 active → 把 active 名改落到列表第一条(空表则清空),
                // 避免悬空指针 / 下次 service Refresh 时报"未配置"。
                if (wasActive)
                {
                    ActiveQuerySource = QuerySources.FirstOrDefault();
                }
            }
        });
        ConfirmAddQuerySourceCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewQuerySourceName) ||
                string.IsNullOrWhiteSpace(NewQuerySourceUrl))
            {
                IsAddQuerySourceOpen = false;
                return;
            }
            var ns = new NodeSource { Name = NewQuerySourceName, Url = NewQuerySourceUrl };
            QuerySources.Add(ns);
            ActiveQuerySource = ns;  // 自动 active
            // 表单关闭 → 清空 inputs,下次再开 Add 是空白
            NewQuerySourceName = "";
            NewQuerySourceUrl = "";
            IsAddQuerySourceOpen = false;
        });
        CancelAddQuerySourceCommand = new RelayCommand(_ =>
        {
            IsAddQuerySourceOpen = false;
        });
        AddDownloadSourceCommand = new RelayCommand(_ =>
        {
            NewDownloadSourceName = "";
            NewDownloadSourceUrl = "";
            IsAddDownloadSourceOpen = true;
        });
        RemoveDownloadSourceCommand = new RelayCommand(p =>
        {
            if (p is NodeSource ns)
            {
                var wasActive = ns.Name == _settings.ActiveDownloadSourceName;
                DownloadSources.Remove(ns);
                if (wasActive)
                {
                    ActiveDownloadSource = DownloadSources.FirstOrDefault();
                }
            }
        });
        ConfirmAddDownloadSourceCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewDownloadSourceName) ||
                string.IsNullOrWhiteSpace(NewDownloadSourceUrl))
            {
                IsAddDownloadSourceOpen = false;
                return;
            }
            var ns = new NodeSource { Name = NewDownloadSourceName, Url = NewDownloadSourceUrl };
            DownloadSources.Add(ns);
            ActiveDownloadSource = ns;
            NewDownloadSourceName = "";
            NewDownloadSourceUrl = "";
            IsAddDownloadSourceOpen = false;
        });
        CancelAddDownloadSourceCommand = new RelayCommand(_ =>
        {
            IsAddDownloadSourceOpen = false;
        });
        RefreshCatalogCommand = new RelayCommand(
            _ => _ = RefreshCatalogAsync(),
            _ => !IsBusy);
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

    // —— 节点源(query / download) ——
    public ObservableCollection<NodeSource> QuerySources { get; }
    public ObservableCollection<NodeSource> DownloadSources { get; }

    public NodeSource? ActiveQuerySource
    {
        get => QuerySources.FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        set
        {
            _settings.ActiveQuerySourceName = value?.Name ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    public NodeSource? ActiveDownloadSource
    {
        get => DownloadSources.FirstOrDefault(s => s.Name == _settings.ActiveDownloadSourceName);
        set
        {
            _settings.ActiveDownloadSourceName = value?.Name ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    public bool IsAddQuerySourceOpen
    {
        get => _isAddQuerySourceOpen;
        set => SetField(ref _isAddQuerySourceOpen, value);
    }
    public bool IsAddDownloadSourceOpen
    {
        get => _isAddDownloadSourceOpen;
        set => SetField(ref _isAddDownloadSourceOpen, value);
    }
    public string NewQuerySourceName
    {
        get => _newQuerySourceName;
        set => SetField(ref _newQuerySourceName, value);
    }
    public string NewQuerySourceUrl
    {
        get => _newQuerySourceUrl;
        set => SetField(ref _newQuerySourceUrl, value);
    }
    public string NewDownloadSourceName
    {
        get => _newDownloadSourceName;
        set => SetField(ref _newDownloadSourceName, value);
    }
    public string NewDownloadSourceUrl
    {
        get => _newDownloadSourceUrl;
        set => SetField(ref _newDownloadSourceUrl, value);
    }

    public RelayCommand AddQuerySourceCommand { get; }
    public RelayCommand RemoveQuerySourceCommand { get; }
    public RelayCommand ConfirmAddQuerySourceCommand { get; }
    public RelayCommand CancelAddQuerySourceCommand { get; }
    public RelayCommand AddDownloadSourceCommand { get; }
    public RelayCommand RemoveDownloadSourceCommand { get; }
    public RelayCommand ConfirmAddDownloadSourceCommand { get; }
    public RelayCommand CancelAddDownloadSourceCommand { get; }

    public RelayCommand CheckUpdateCommand { get; } = new RelayCommand(_ =>
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases",
            UseShellExecute = true,
        });
    });

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RefreshCatalogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public RelayCommand RefreshCatalogCommand { get; }

    private async Task RefreshCatalogAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        try
        {
            var result = await _refreshService.RefreshAsync();
            if (result.Success)
            {
                StatusMessage = $"刷新成功,共 {result.EntryCount} 个条目";
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

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
        RaisePropertyChanged(nameof(ActiveQuerySource));
        RaisePropertyChanged(nameof(ActiveDownloadSource));
    }
}
