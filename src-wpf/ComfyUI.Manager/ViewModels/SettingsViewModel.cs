using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    private readonly HttpProxyConfig _proxy;
    private readonly IPythonInterpreterValidator _validator;
    // v0.6.9 T2:接 IThemeService,ThemeMode setter 改即调 Apply 立即预览。
    // 可空:既有 12+ 测试 callers 不传 themeService 也能跑(Apply no-op)。
    // 生产路径 App.xaml.cs:217 通过 MainViewModel 间接构造,总是传非 null 实例。
    private readonly IThemeService? _themeService;
    private readonly CancellationTokenSource _addPythonInterpreterCts = new();
    private Settings _settings;

    // v0.6.11+ SDD B T1:dirty tracking。XAML 行内 ⚠️ 通过 {Binding Dirty[Xxx]} 查,
    // SaveCommand 一次性写盘 + 清 dirty,DiscardCommand 用 CopyInto 回滚。
    public DirtyLookup Dirty { get; } = new();

    public bool HasUnsavedChanges => Dirty.Any;
    public int UnsavedCount => Dirty.Count;

    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }

    private void MarkDirty(string propertyName)
    {
        Dirty.Mark(propertyName);
        RaisePropertyChanged(nameof(HasUnsavedChanges));
        RaisePropertyChanged(nameof(UnsavedCount));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }

    private void ClearDirty()
    {
        Dirty.Clear();
        RaisePropertyChanged(nameof(HasUnsavedChanges));
        RaisePropertyChanged(nameof(UnsavedCount));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }

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
    private string _newCommonNodeId = "";
    private string _newCommonNodeDisplayName = "";
    private string _addCommonNodeError = "";
    private bool _isAddCommonNodeOpen;

    public SettingsViewModel(
        SettingsRepository repo,
        HttpProxyConfig proxy,
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
        CommonNodes = new ObservableCollection<CommonNodeEntry>(_settings.CommonNodes);
        CommonNodes.CollectionChanged += (_, _) =>
        {
            _settings.CommonNodes = new List<CommonNodeEntry>(CommonNodes);
            _repo.Save(_settings);
        };
        AddCommonNodeCommand = new RelayCommand(_ =>
        {
            NewCommonNodeId = "";
            NewCommonNodeDisplayName = "";
            AddCommonNodeError = "";
            IsAddCommonNodeOpen = true;
        });
        CancelAddCommonNodeCommand = new RelayCommand(_ =>
        {
            IsAddCommonNodeOpen = false;
            AddCommonNodeError = "";
        });
        ConfirmAddCommonNodeCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewCommonNodeId) || !NewCommonNodeId.Contains('/'))
            {
                AddCommonNodeError = "Id 必须是 owner/repo 形式(必须含 \"/\")";
                return;
            }
            if (CommonNodes.Any(n => string.Equals(n.Id, NewCommonNodeId, StringComparison.OrdinalIgnoreCase)))
            {
                AddCommonNodeError = $"已存在相同 Id 的节点:{NewCommonNodeId}";
                return;
            }
            CommonNodes.Add(new CommonNodeEntry
            {
                Id = NewCommonNodeId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(NewCommonNodeDisplayName)
                    ? NewCommonNodeId.Trim()
                    : NewCommonNodeDisplayName.Trim(),
                IsBuiltIn = false,
                Enabled = true,
            });
            NewCommonNodeId = "";
            NewCommonNodeDisplayName = "";
            AddCommonNodeError = "";
            IsAddCommonNodeOpen = false;
        });
        RemoveCommonNodeCommand = new RelayCommand(p =>
        {
            if (p is CommonNodeEntry entry && !entry.IsBuiltIn)
            {
                CommonNodes.Remove(entry);
            }
        });
        ToggleCommonNodeEnabledCommand = new RelayCommand(p =>
        {
            if (p is CommonNodeEntry entry)
            {
                entry.Enabled = !entry.Enabled;
                // CollectionChanged 只在 list 改动时触发,改 item property 不触发
                // → 手动 Save 持久化
                _settings.CommonNodes = new List<CommonNodeEntry>(CommonNodes);
                _repo.Save(_settings);
            }
        });
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
        SaveCommand = new RelayCommand(
            _ => { _repo.Save(_settings); ClearDirty(); },
            _ => HasUnsavedChanges);
        // v0.6.12 hotfix:BrowseLogDirectory 按钮绑的命令。initialPath = 当前 LogDirectory,
        // 让用户从已有位置浏览,而不是每次从系统默认(可能是桌面)。
        BrowseLogDirectoryCommand = new RelayCommand(_ =>
        {
            var picked = PickFolder("选择日志目录", LogDirectory);
            if (!string.IsNullOrEmpty(picked))
            {
                LogDirectory = picked;
            }
        });
        DiscardCommand = new RelayCommand(
            _ =>
            {
                var onDisk = _repo.Load();
                Settings.CopyInto(_settings, onDisk);
                // G3:ThemeMode 即使即时预览,Discard 也得反向 Apply 才能回滚
                _themeService?.Apply(ParseThemeMode(_settings.ThemeMode));
                RaiseAllPropertiesChanged();
                ClearDirty();
            },
            _ => HasUnsavedChanges);
        RaiseAllPropertiesChanged();
    }

    public List<string> Languages { get; } = new() { "zh_CN", "en_US" };
    public List<string> ThemeModes { get; } = new() { "light", "dark", "system" };
    public List<string> DefaultPythonVersions { get; } = new() { "3.10", "3.11", "3.12", "3.13" };

    // —— 基础 / 显示 ——
    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; MarkDirty(nameof(Language)); RaisePropertyChanged(); }
    }
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set
        {
            if (_settings.ThemeMode == value) return;
            _settings.ThemeMode = value;
            MarkDirty(nameof(ThemeMode));
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
        set { _settings.CatalogCacheTtlMinutes = value; MarkDirty(nameof(CacheTtlMinutes)); RaisePropertyChanged(); }
    }
    // v0.6.7.1: ComfyUI 启动就绪超时(秒),默认 600。
    public int ComfyUiStartupTimeoutSeconds
    {
        get => _settings.ComfyUiStartupTimeoutSeconds;
        set { _settings.ComfyUiStartupTimeoutSeconds = value; MarkDirty(nameof(ComfyUiStartupTimeoutSeconds)); RaisePropertyChanged(); }
    }
    // v0.6.7.2: ComfyUI UI locale code(空 = 不动 ComfyUI 配置,其他值写进
    // <comfyui-root>/user/default/comfy.settings.json 的 Comfy.Locale)。
    public string ComfyUiLocale
    {
        get => _settings.ComfyUiLocale;
        set { _settings.ComfyUiLocale = value ?? ""; MarkDirty(nameof(ComfyUiLocale)); RaisePropertyChanged(); }
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
        set { _settings.CompatApiBaseUrl = value; MarkDirty(nameof(CompatApiBaseUrl)); RaisePropertyChanged(); }
    }

    /// <summary>
    /// GitHub PAT 用于 catalog 刷新时拉各节点最新 release 版本号。空 = 不拉。
    /// View 端用 PasswordBox,不在 XAML 里直接 TwoWay bind(string 类型会明文显示)。
    /// 由 View 的 PasswordChanged 事件反向写入此属性并 persist。
    /// </summary>
    public string GitHubToken
    {
        get => _settings.GitHubToken;
        set { _settings.GitHubToken = value ?? ""; MarkDirty(nameof(GitHubToken)); }
    }

    /// <summary>
    /// v0.6.11 T3: 开关 gate — 开启时 refresh catalog 同步拉各节点版本号,无需配 token
    /// 也可走(走未鉴权 60/h 限流)。默认 OFF 保持向后兼容。
    /// </summary>
    public bool FetchNodeVersionsOnRefresh
    {
        get => _settings.FetchNodeVersionsOnRefresh;
        set { _settings.FetchNodeVersionsOnRefresh = value; MarkDirty(nameof(FetchNodeVersionsOnRefresh)); RaisePropertyChanged(); }
    }

    /// <summary>
    /// v0.6.13-B: 开关 gate — 开启时 refresh catalog 同步拉各节点 GitHub metadata
    /// (License/Tags/Stars/Downloads/LastCommit/Readme/Changelog/Deprecated/PythonCompat/OsCompat)。
    /// 默认 OFF 保持向后兼容;开启后无 token 会被 GitHub 限流 60/h。24h 本地缓存
    /// 写 <c>%APPDATA%/ComfyUI-Manager/catalog_metadata_cache.json</c>。
    /// </summary>
    public bool FetchCatalogMetadata
    {
        get => _settings.FetchCatalogMetadata;
        set { _settings.FetchCatalogMetadata = value; MarkDirty(nameof(FetchCatalogMetadata)); RaisePropertyChanged(); }
    }

    // v0.6.11++ pip mirror
    public List<string> PipMirrorKinds { get; } = new()
    {
        "official", "tsinghua_tuna", "aliyun", "ustc", "custom",
    };
    public string PipMirror
    {
        get => _settings.PipMirror;
        set
        {
            _settings.PipMirror = value ?? "official";
            MarkDirty(nameof(PipMirror));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsCustomPipMirrorSelected));
        }
    }
    public string PipMirrorCustomUrl
    {
        get => _settings.PipMirrorCustomUrl;
        set
        {
            _settings.PipMirrorCustomUrl = value ?? "";
            MarkDirty(nameof(PipMirrorCustomUrl));
            RaisePropertyChanged();
        }
    }
    public bool IsCustomPipMirrorSelected
        => string.Equals(_settings.PipMirror, "custom", System.StringComparison.OrdinalIgnoreCase);

    // —— 路径 ——
    public string TemplatePythonDir
    {
        get => _settings.TemplatePythonDir;
        set { _settings.TemplatePythonDir = value; MarkDirty(nameof(TemplatePythonDir)); RaisePropertyChanged(); }
    }
    public string TemplateComfyuiDir
    {
        get => _settings.TemplateComfyuiDir;
        set { _settings.TemplateComfyuiDir = value; MarkDirty(nameof(TemplateComfyuiDir)); RaisePropertyChanged(); }
    }
    public string EnvsDir
    {
        get => _settings.EnvsDir;
        set { _settings.EnvsDir = value; MarkDirty(nameof(EnvsDir)); RaisePropertyChanged(); }
    }
    public string DefaultPythonVersion
    {
        get => _settings.DefaultPythonVersion;
        set { _settings.DefaultPythonVersion = value ?? ""; MarkDirty(nameof(DefaultPythonVersion)); RaisePropertyChanged(); }
    }
    public string GlobalNodesDir
    {
        get => _settings.GlobalNodesDir;
        set { _settings.GlobalNodesDir = value; MarkDirty(nameof(GlobalNodesDir)); RaisePropertyChanged(); }
    }
    // v0.6.5.9: Catalog 主页「下载」按钮的目标目录
    public string LocalNodeDirectory
    {
        get => _settings.LocalNodeDirectory;
        set
        {
            _settings.LocalNodeDirectory = value ?? "";
            MarkDirty(nameof(LocalNodeDirectory));
            RaisePropertyChanged();
        }
    }
    // v0.6.19:工作流市场
    public string WorkflowsDirectory
    {
        get => _settings.WorkflowsDirectory;
        set
        {
            var v = value ?? "";
            if (_settings.WorkflowsDirectory == v) return;
            _settings.WorkflowsDirectory = v;
            MarkDirty(nameof(WorkflowsDirectory));
            RaisePropertyChanged();
        }
    }

    public bool WorkflowSourceCommunityJsonEnabled
    {
        get => _settings.WorkflowSourceCommunityJsonEnabled;
        set
        {
            if (_settings.WorkflowSourceCommunityJsonEnabled == value) return;
            _settings.WorkflowSourceCommunityJsonEnabled = value;
            MarkDirty(nameof(WorkflowSourceCommunityJsonEnabled));
            RaisePropertyChanged();
        }
    }

    public bool WorkflowSourceCivitAiEnabled
    {
        get => _settings.WorkflowSourceCivitAiEnabled;
        set
        {
            if (_settings.WorkflowSourceCivitAiEnabled == value) return;
            _settings.WorkflowSourceCivitAiEnabled = value;
            MarkDirty(nameof(WorkflowSourceCivitAiEnabled));
            RaisePropertyChanged();
        }
    }

    public bool WorkflowSourceOpenArtEnabled
    {
        get => _settings.WorkflowSourceOpenArtEnabled;
        set
        {
            if (_settings.WorkflowSourceOpenArtEnabled == value) return;
            _settings.WorkflowSourceOpenArtEnabled = value;
            MarkDirty(nameof(WorkflowSourceOpenArtEnabled));
            RaisePropertyChanged();
        }
    }
    // v0.6.20:模型市场
    public string ModelsDirectory
    {
        get => _settings.ModelsDirectory;
        set
        {
            var v = value ?? "";
            if (_settings.ModelsDirectory == v) return;
            _settings.ModelsDirectory = v;
            MarkDirty(nameof(ModelsDirectory));
            RaisePropertyChanged();
        }
    }

    public bool ModelSourceCivitAiEnabled
    {
        get => _settings.ModelSourceCivitAiEnabled;
        set
        {
            if (_settings.ModelSourceCivitAiEnabled == value) return;
            _settings.ModelSourceCivitAiEnabled = value;
            MarkDirty(nameof(ModelSourceCivitAiEnabled));
            RaisePropertyChanged();
        }
    }
    // v0.6.10: 全局默认 Models 目录(env-create junction 目标)。空 = 不动 env 的 models 目录。
    public string DefaultModelsDirectory
    {
        get => _settings.DefaultModelsDirectory;
        set { _settings.DefaultModelsDirectory = value ?? ""; MarkDirty(nameof(DefaultModelsDirectory)); RaisePropertyChanged(); }
    }
    /// <summary>
    /// v0.6.12:日志根目录(Logs/ 子目录的父目录)。空 = 默认 &lt;projectRoot&gt;。
    /// 改后 Settings.Save() 持久化,App 启动时 AppLogger / ProcessLauncher 读这个字段。
    /// 例如:设置为 "D:/my-logs" → 日志写到 D:/my-logs/Logs/。
    /// </summary>
    public string LogDirectory
    {
        get => _settings.LogDirectory;
        set
        {
            if (_settings.LogDirectory == value) return;
            _settings.LogDirectory = value ?? "";
            MarkDirty(nameof(LogDirectory));
            RaisePropertyChanged();
        }
    }

    // —— 环境 / 工具 ——
    public string PythonVenvBaseline
    {
        get => _settings.PythonVenvBaseline;
        set { _settings.PythonVenvBaseline = value; MarkDirty(nameof(PythonVenvBaseline)); RaisePropertyChanged(); }
    }
    public string GitExe
    {
        get => _settings.GitExe;
        set { _settings.GitExe = value; MarkDirty(nameof(GitExe)); RaisePropertyChanged(); }
    }
    public string HttpProxyUrl
    {
        // getter/setter 都双写: _settings (持久化) + _proxy (运行期 live)。
        // 让 HttpProxy 开关能即时生效, 不用重启。
        get => _proxy.Url;
        set
        {
            _proxy.Url = value;
            _settings.HttpProxyUrl = value;
            MarkDirty(nameof(HttpProxyUrl));
            RaisePropertyChanged();
        }
    }
    public int HttpProxyPort
    {
        get => _proxy.Port;
        set
        {
            _proxy.Port = value;
            _settings.HttpProxyPort = value;
            MarkDirty(nameof(HttpProxyPort));
            RaisePropertyChanged();
        }
    }
    public bool HttpProxyEnabled
    {
        get => _proxy.Enabled;
        set
        {
            _proxy.Enabled = value;
            _settings.HttpProxyEnabled = value;
            MarkDirty(nameof(HttpProxyEnabled));
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
            MarkDirty(nameof(ActiveQuerySource));
            RaisePropertyChanged();
        }
    }

    public NodeSource? ActiveDownloadSource
    {
        get => DownloadSources.FirstOrDefault(s => s.Name == _settings.ActiveDownloadSourceName);
        set
        {
            _settings.ActiveDownloadSourceName = value?.Name ?? "";
            MarkDirty(nameof(ActiveDownloadSource));
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

    // —— v0.6.11++ 常用节点 ——
    public ObservableCollection<CommonNodeEntry> CommonNodes { get; }

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
            MarkDirty(nameof(ActivePythonInterpreter));
            RaisePropertyChanged();
        }
    }

    public string ActivePythonInterpreterName
    {
        get => _settings.ActivePythonInterpreterName;
        set
        {
            _settings.ActivePythonInterpreterName = value ?? "";
            MarkDirty(nameof(ActivePythonInterpreterName));
            RaisePropertyChanged(nameof(ActivePythonInterpreter));
        }
    }

    public RelayCommand AddPythonInterpreterCommand { get; }
    public RelayCommand ConfirmAddPythonInterpreterCommand { get; }
    public RelayCommand CancelAddPythonInterpreterCommand { get; }
    public RelayCommand RemovePythonInterpreterCommand { get; }

    public string NewCommonNodeId
    {
        get => _newCommonNodeId;
        set => SetField(ref _newCommonNodeId, value);
    }
    public string NewCommonNodeDisplayName
    {
        get => _newCommonNodeDisplayName;
        set => SetField(ref _newCommonNodeDisplayName, value);
    }
    public string AddCommonNodeError
    {
        get => _addCommonNodeError;
        private set
        {
            if (SetField(ref _addCommonNodeError, value))
                RaisePropertyChanged(nameof(HasAddCommonNodeError));
        }
    }
    public bool HasAddCommonNodeError => !string.IsNullOrEmpty(_addCommonNodeError);
    public bool IsAddCommonNodeOpen
    {
        get => _isAddCommonNodeOpen;
        private set => SetField(ref _isAddCommonNodeOpen, value);
    }
    public RelayCommand AddCommonNodeCommand { get; }
    public RelayCommand CancelAddCommonNodeCommand { get; }
    public RelayCommand ConfirmAddCommonNodeCommand { get; }
    public RelayCommand RemoveCommonNodeCommand { get; }
    public RelayCommand ToggleCommonNodeEnabledCommand { get; }

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
    /// <summary>
    /// v0.6.12 hotfix:Browse 按钮的 folder dialog 测试 seam。
    /// 测试里赋值为返回固定路径(<c>@&quot;D:\custom-logs&quot;</c>);null 时走真
    /// <see cref="OpenFolderDialog"/>(STA 模态阻塞测试线程,所以测试必须 set)。
    /// 入参是 <c>initialPath</c>(当前 LogDirectory 值),返回选中的 folder name 或 null(取消)。
    /// </summary>
    public Func<string?, string?>? FolderDialogOverride { get; set; }

    public string? PickFolder()
    {
        if (FolderDialogOverride is not null) return FolderDialogOverride(null);
        var dlg = new OpenFolderDialog { Title = "选择目录" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>
    /// v0.6.12 hotfix:带 initial directory 的 folder picker(给 LogDirectory Browse 用)。
    /// initialPath = 当前 LogDirectory 值;null/不存在 = 不设 InitialDirectory(系统默认)。
    /// </summary>
    public string? PickFolder(string title, string? initialPath)
    {
        if (FolderDialogOverride is not null) return FolderDialogOverride(initialPath);
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
        {
            dlg.InitialDirectory = initialPath;
        }
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// v0.6.12 hotfix:打开 OpenFolderDialog 让用户选日志根目录。选了 → 写回
    /// <see cref="LogDirectory"/>(触发 dirty marker + MarkDirty);取消 → no-op。
    /// </summary>
    public RelayCommand BrowseLogDirectoryCommand { get; }

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
        RaisePropertyChanged(nameof(WorkflowsDirectory));
        RaisePropertyChanged(nameof(WorkflowSourceCommunityJsonEnabled));
        RaisePropertyChanged(nameof(WorkflowSourceCivitAiEnabled));
        RaisePropertyChanged(nameof(WorkflowSourceOpenArtEnabled));
        RaisePropertyChanged(nameof(ModelsDirectory));
        RaisePropertyChanged(nameof(ModelSourceCivitAiEnabled));
        RaisePropertyChanged(nameof(LogDirectory));
        RaisePropertyChanged(nameof(PythonVenvBaseline));
        RaisePropertyChanged(nameof(GitExe));
        RaisePropertyChanged(nameof(HttpProxyUrl));
        RaisePropertyChanged(nameof(HttpProxyPort));
        RaisePropertyChanged(nameof(HttpProxyEnabled));
        RaisePropertyChanged(nameof(ActiveQuerySource));
        RaisePropertyChanged(nameof(ActiveDownloadSource));
        RaisePropertyChanged(nameof(FetchNodeVersionsOnRefresh));
        RaisePropertyChanged(nameof(FetchCatalogMetadata));
        RaisePropertyChanged(nameof(PipMirror));
        RaisePropertyChanged(nameof(PipMirrorCustomUrl));
        RaisePropertyChanged(nameof(IsCustomPipMirrorSelected));
        RaisePropertyChanged(nameof(CommonNodes));
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
