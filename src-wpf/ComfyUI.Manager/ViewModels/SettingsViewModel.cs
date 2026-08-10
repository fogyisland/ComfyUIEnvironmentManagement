using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Microsoft.Win32;

namespace ComfyUI.Manager.ViewModels;

public class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsRepository _repo;
    private readonly GitProxyConfig _proxy;
    private readonly IPythonInterpreterValidator _validator;
    // v0.6.9 T2:接 IThemeService,ThemeMode setter 改即调 Apply 立即预览。
    // 可空:既有 12+ 测试 callers 不传 themeService 也能跑(Apply no-op)。
    // 生产路径 App.xaml.cs:217 通过 MainViewModel 间接构造,总是传非 null 实例。
    private readonly IThemeService? _themeService;
    private readonly CancellationTokenSource _addPythonInterpreterCts = new();
    private Settings _settings;

    private bool _isAddQuerySourceOpen;
    private bool _isAddDownloadSourceOpen;
    private string _newQuerySourceName = "";
    private string _newQuerySourceUrl = "";
    private string _newDownloadSourceName = "";
    private string _newDownloadSourceUrl = "";
    private string _newPythonInterpreterName = "";
    private string _newPythonInterpreterPath = "";
    private string _addPythonInterpreterError = "";
    private bool _isAddPythonInterpreterOpen;

    public SettingsViewModel(
        SettingsRepository repo,
        GitProxyConfig proxy,
        IPythonInterpreterValidator validator,
        Settings? sharedSettings = null,
        IThemeService? themeService = null)
    {
        _repo = repo;
        _proxy = proxy;
        _validator = validator;
        _themeService = themeService;
        // 优先用 MainViewModel 注入的共享实例(同 App 内 Settings 状态统一)。
        // 没有注入时(单元测试)才从 disk 加载。
        _settings = sharedSettings ?? _repo.Load();
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
        PythonInterpreters = new ObservableCollection<PythonInterpreter>(_settings.PythonInterpreters);
        PythonInterpreters.CollectionChanged += (_, _) =>
        {
            _settings.PythonInterpreters = new List<PythonInterpreter>(PythonInterpreters);
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActivePythonInterpreter));
        };
        AddPythonInterpreterCommand = new RelayCommand(_ =>
        {
            NewPythonInterpreterName = "";
            NewPythonInterpreterPath = "";
            AddPythonInterpreterError = "";
            IsAddPythonInterpreterOpen = true;
        });
        CancelAddPythonInterpreterCommand = new RelayCommand(_ =>
        {
            IsAddPythonInterpreterOpen = false;
            AddPythonInterpreterError = "";
        });
        ConfirmAddPythonInterpreterCommand = new RelayCommand(async _ =>
        {
            await ConfirmAddPythonInterpreterAsync().ConfigureAwait(false);
        });
        RemovePythonInterpreterCommand = new RelayCommand(p =>
        {
            if (p is PythonInterpreter pi)
            {
                var wasActive = pi.Name == _settings.ActivePythonInterpreterName;
                PythonInterpreters.Remove(pi);
                if (wasActive)
                {
                    _settings.ActivePythonInterpreterName = PythonInterpreters.FirstOrDefault()?.Name ?? "";
                    _repo.Save(_settings);
                    RaisePropertyChanged(nameof(ActivePythonInterpreter));
                }
            }
        });
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
        RaiseAllPropertiesChanged();
    }

    public List<string> Languages { get; } = new() { "zh_CN", "en_US" };
    public List<string> ThemeModes { get; } = new() { "light", "dark", "system" };
    public List<string> DefaultPythonVersions { get; } = new() { "3.10", "3.11", "3.12", "3.13" };

    // —— 基础 / 显示 ——
    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set
        {
            if (_settings.ThemeMode == value) return;
            _settings.ThemeMode = value;
            _repo.Save(_settings);
            // v0.6.9 T2:改即 Apply(立即预览,Settings UI 风格:ComboBox 切换不需 Save)。
            // _themeService 可空 — 既有测试 callers 不传也能跑(Apply no-op)。
            _themeService?.Apply(ParseThemeMode(value));
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// 把 Settings.ThemeMode lowercase 字符串解析成 <see cref="Services.ThemeMode"/> enum。
    /// "light" → <see cref="Services.ThemeMode.Light"/>;"dark" → <see cref="Services.ThemeMode.Dark"/>;
    /// "system" → <see cref="Services.ThemeMode.FollowSystem"/>;缺省 / 任何其它值 →
    /// <see cref="Services.ThemeMode.Dark"/>(G5 缺省 Dark)。
    /// <see cref="IThemeService.Apply"/> 自带 ResolveMode(把 FollowSystem 落定到
    /// Light/Dark),所以这里只做 string → enum 翻译,不做 heuristic 决策。
    /// </summary>
    public static Services.ThemeMode ParseThemeMode(string value) => value switch
    {
        "light" => Services.ThemeMode.Light,
        "dark" => Services.ThemeMode.Dark,
        "system" => Services.ThemeMode.FollowSystem,
        _ => Services.ThemeMode.Dark,
    };
    public int CacheTtlMinutes
    {
        get => _settings.CatalogCacheTtlMinutes;
        set { _settings.CatalogCacheTtlMinutes = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    // v0.6.7.1: ComfyUI 启动就绪超时(秒),默认 600。
    public int ComfyUiStartupTimeoutSeconds
    {
        get => _settings.ComfyUiStartupTimeoutSeconds;
        set { _settings.ComfyUiStartupTimeoutSeconds = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    // v0.6.7.2: ComfyUI UI locale code(空 = 不动 ComfyUI 配置,其他值写进
    // <comfyui-root>/user/default/comfy.settings.json 的 Comfy.Locale)。
    public string ComfyUiLocale
    {
        get => _settings.ComfyUiLocale;
        set { _settings.ComfyUiLocale = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    /// <summary>
    /// ComfyUI 已知的 locale code 列表 + 空字符串 ("不修改")。ComboBox 显示给用户。
    /// </summary>
    public List<string> ComfyUiLocales { get; } = new()
    {
        "", "zh", "en", "ja", "ko", "ru", "fr", "es",
    };
    public string CompatApiBaseUrl
    {
        get => _settings.CompatApiBaseUrl;
        set { _settings.CompatApiBaseUrl = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }

    /// <summary>
    /// GitHub PAT 用于 catalog 刷新时拉各节点最新 release 版本号。空 = 不拉。
    /// View 端用 PasswordBox,不在 XAML 里直接 TwoWay bind(string 类型会明文显示)。
    /// 由 View 的 PasswordChanged 事件反向写入此属性并 persist。
    /// </summary>
    public string GitHubToken
    {
        get => _settings.GitHubToken;
        set { _settings.GitHubToken = value ?? ""; _repo.Save(_settings); }
    }

    /// <summary>
    /// v0.6.11 T3: 开关 gate — 开启时 refresh catalog 同步拉各节点版本号,无需配 token
    /// 也可走(走未鉴权 60/h 限流)。默认 OFF 保持向后兼容。
    /// </summary>
    public bool FetchNodeVersionsOnRefresh
    {
        get => _settings.FetchNodeVersionsOnRefresh;
        set { _settings.FetchNodeVersionsOnRefresh = value; _repo.Save(_settings); RaisePropertyChanged(); }
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
    public string DefaultPythonVersion
    {
        get => _settings.DefaultPythonVersion;
        set { _settings.DefaultPythonVersion = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    public string GlobalNodesDir
    {
        get => _settings.GlobalNodesDir;
        set { _settings.GlobalNodesDir = value; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    // v0.6.5.9: Catalog 主页「下载」按钮的目标目录
    public string LocalNodeDirectory
    {
        get => _settings.LocalNodeDirectory;
        set
        {
            _settings.LocalNodeDirectory = value ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }
    // v0.6.10: 全局默认 Models 目录(env-create junction 目标)。空 = 不动 env 的 models 目录。
    public string DefaultModelsDirectory
    {
        get => _settings.DefaultModelsDirectory;
        set { _settings.DefaultModelsDirectory = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
    }
    // v0.6.7.3: 全局共享 Models 目录。空 = 不共享。
    public string SharedModelsDirectory
    {
        get => _settings.SharedModelsDirectory;
        set { _settings.SharedModelsDirectory = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
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

    // v0.6.9 T9:Check Update 按钮 pulse 动效 source of truth — true 时启动 DataTrigger
    // 进入 pulse,完成(finally)设回 false。当前 CheckUpdateCommand 同步开浏览器瞬间就结束,
    // pulse 几乎看不到;但保留 property 给未来真正的 async 检查接线(future feature)。
    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        set => SetField(ref _isChecking, value);
    }

    // —— 多 Python 解释器(v0.6.5.6) ——
    public ObservableCollection<PythonInterpreter> PythonInterpreters { get; }

    public PythonInterpreter? ActivePythonInterpreter
    {
        get
        {
            var name = _settings.ActivePythonInterpreterName;
            if (string.IsNullOrEmpty(name)) return null;
            return _settings.PythonInterpreters.FirstOrDefault(p => p.Name == name);
        }
        set
        {
            _settings.ActivePythonInterpreterName = value?.Name ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    public string ActivePythonInterpreterName
    {
        get => _settings.ActivePythonInterpreterName;
        set
        {
            _settings.ActivePythonInterpreterName = value ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActivePythonInterpreter));
        }
    }

    public RelayCommand AddPythonInterpreterCommand { get; }
    public RelayCommand ConfirmAddPythonInterpreterCommand { get; }
    public RelayCommand CancelAddPythonInterpreterCommand { get; }
    public RelayCommand RemovePythonInterpreterCommand { get; }

    public string NewPythonInterpreterName
    {
        get => _newPythonInterpreterName;
        set => SetField(ref _newPythonInterpreterName, value);
    }
    public string NewPythonInterpreterPath
    {
        get => _newPythonInterpreterPath;
        set => SetField(ref _newPythonInterpreterPath, value);
    }
    public string AddPythonInterpreterError
    {
        get => _addPythonInterpreterError;
        private set
        {
            if (SetField(ref _addPythonInterpreterError, value))
                RaisePropertyChanged(nameof(HasAddPythonInterpreterError));
        }
    }
    public bool HasAddPythonInterpreterError => !string.IsNullOrEmpty(_addPythonInterpreterError);
    public bool IsAddPythonInterpreterOpen
    {
        get => _isAddPythonInterpreterOpen;
        private set => SetField(ref _isAddPythonInterpreterOpen, value);
    }

    public async Task ConfirmAddPythonInterpreterAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPythonInterpreterName) ||
            string.IsNullOrWhiteSpace(NewPythonInterpreterPath))
        {
            IsAddPythonInterpreterOpen = false;
            return;
        }

        // 测试 / 单入口调用场景下,表单可能没先开。Confirm 时强制打开,
        // 让验证失败时错误信息有地方显示。
        IsAddPythonInterpreterOpen = true;
        AddPythonInterpreterError = "";
        try
        {
            var result = await _validator
                .ValidateAsync(NewPythonInterpreterPath, _addPythonInterpreterCts.Token)
                .ConfigureAwait(true);
            if (!result.IsValid)
            {
                AddPythonInterpreterError = result.Error ?? "验证失败";
                return;  // 表单保持打开
            }

            var pi = new PythonInterpreter
            {
                Name = NewPythonInterpreterName,
                Path = NewPythonInterpreterPath,
            };
            // 先在 _settings 上写 active 名(新增即激活),再 Add,
            // 让 CollectionChanged 触发的那一次 Save 把"列表+active 名"一并持久化。
            _settings.ActivePythonInterpreterName = pi.Name;
            PythonInterpreters.Add(pi);

            IsAddPythonInterpreterOpen = false;
            NewPythonInterpreterName = "";
            NewPythonInterpreterPath = "";
        }
        catch (OperationCanceledException)
        {
            // vm dispose 时取消,静默
        }
    }

    public void Dispose()
    {
        try { _addPythonInterpreterCts.Cancel(); } catch { }
        _addPythonInterpreterCts.Dispose();
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
        RaisePropertyChanged(nameof(ComfyUiStartupTimeoutSeconds));
        RaisePropertyChanged(nameof(CompatApiBaseUrl));
        RaisePropertyChanged(nameof(TemplatePythonDir));
        RaisePropertyChanged(nameof(TemplateComfyuiDir));
        RaisePropertyChanged(nameof(EnvsDir));
        RaisePropertyChanged(nameof(DefaultPythonVersion));
        RaisePropertyChanged(nameof(GlobalNodesDir));
        RaisePropertyChanged(nameof(LocalNodeDirectory));
        RaisePropertyChanged(nameof(DefaultModelsDirectory));
        RaisePropertyChanged(nameof(SharedModelsDirectory));
        RaisePropertyChanged(nameof(PythonVenvBaseline));
        RaisePropertyChanged(nameof(GitExe));
        RaisePropertyChanged(nameof(GitProxyUrl));
        RaisePropertyChanged(nameof(GitProxyPort));
        RaisePropertyChanged(nameof(GitProxyEnabled));
        RaisePropertyChanged(nameof(ActiveQuerySource));
        RaisePropertyChanged(nameof(ActiveDownloadSource));
        RaisePropertyChanged(nameof(FetchNodeVersionsOnRefresh));
    }

    // ============ v0.6.9 T7 Spotlight 集成 ============

    /// <summary>
    /// v0.6.9 T7:Spotlight 选中 SettingsSection 后,MainViewModel 调这里触发
    /// SettingsView 滚动到对应锚点。<c>sectionKey</c> 跟 <c>SettingsView.xaml</c>
    /// 里 7 个 section header TextBlock 的 <c>x:Name="Section{Key}"</c> 对齐。
    /// <para>
    /// WPF VM 不能直接控制 ScrollViewer,所以 emit event 让 SettingsView.xaml.cs 订阅。
    /// 没订阅者(sandbox 测试 / 没人监听) → event 没人接,no-op fallback。
    /// </para>
    /// </summary>
    public event EventHandler<string>? SectionScrollRequested;

    public void ScrollToSection(string sectionKey)
    {
        if (string.IsNullOrEmpty(sectionKey)) return;
        SectionScrollRequested?.Invoke(this, sectionKey);
    }
}
