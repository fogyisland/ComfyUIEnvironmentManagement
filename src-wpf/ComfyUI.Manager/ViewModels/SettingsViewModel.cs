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
using ComfyUI.Manager.Services.ModelSources;
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
    // v1.0.0.x: 用户改 Settings.EnvsDir 保存后,App.xaml.cs 注入的 callback 触发
    // EnvDirectoryScanner.ScanAsync(envsDir)。可空:测试 callers 不传也能跑。
    private readonly Func<string, Task>? _onEnvsDirSaved;
    // v1.0.0.x #589:env → localnodes 反向 sync — 把 ComfyUI-Manager 装的节点补到本地源。
    // 可空:测试 callers 不传也能跑。
    private readonly IEnvironmentRepository? _envRepo;
    private readonly LocalNodeSyncService? _syncService;
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
        IThemeService? themeService = null,
        // v1.0.0.x: 用户改 Settings.EnvsDir 保存后,触发 EnvDirectoryScanner 扫新目录。
        // callback 接收 EnvsDir 相对路径,resolve 到绝对路径由 App.xaml.cs 处理
        // (SettingsViewModel 不需要知道 projectRoot)。
        Func<string, Task>? onEnvsDirSaved = null,
        // v1.0.0.x #589:env → localnodes 反向 sync 入口依赖 — App.xaml.cs 注入共享实例;
        // 测试 callers 不传也能跑(命令 CanExecute 返 false,按钮 disabled)。
        IEnvironmentRepository? envRepo = null,
        LocalNodeSyncService? syncService = null)
    {
        _repo = repo;
        _proxy = proxy;
        _validator = validator;
        _themeService = themeService;
        _onEnvsDirSaved = onEnvsDirSaved;
        _envRepo = envRepo;
        _syncService = syncService;
        // 优先用 MainViewModel 注入的共享实例(同 App 内 Settings 状态统一)。
        // 没有注入时(单元测试)才从 disk 加载。T12:走 LoadWithRawJson 拿到磁盘 raw JSON,
        // 以便 Apply 触发老 template_comfyui_dir 字段迁移。生产 App.xaml.cs 也用同一路径。
        string? rawJsonForApply = null;
        if (sharedSettings is not null)
        {
            _settings = sharedSettings;
        }
        else
        {
            var (loaded, rawJson) = _repo.LoadWithRawJson();
            _settings = loaded;
            rawJsonForApply = rawJson;
        }
        // 首次启动/无 settings.json 时把默认值(node source 列表 + active 名)填上。
        // 生产环境 App.xaml.cs 也会 Apply,这里再 Apply 一次保证测试直构造 VM
        // 时也能拿到默认值(幂等:已存在的非空列表/active 名不会被覆盖)。
        // 共享实例路径下 rawJson=null(已经 App.xaml.cs Apply 过),不需要再 Apply。
        if (sharedSettings is null)
        {
            SettingsDefaults.Apply(_settings, AppContext.BaseDirectory, rawJsonForApply);
        }
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
            async _ =>
            {
                var envsDirJustChanged = Dirty[nameof(EnvsDir)];
                var envsDirValue = _settings.EnvsDir;
                _repo.Save(_settings);
                ClearDirty();
                // v1.0.0.x: EnvsDir 改了 → 触发 EnvDirectoryScanner 扫新目录 auto-import
                // env。可空 callback,没注入就直接跳过(测试路径)。
                if (envsDirJustChanged && _onEnvsDirSaved is not null)
                {
                    try { await _onEnvsDirSaved(envsDirValue ?? ""); }
                    catch { /* scan 失败不阻塞 save 反馈 */ }
                }
            },
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
        // v1.0.0.x #589:env → localnodes 反向 sync 命令。把所有 env 的 custom_nodes/
        // 一次性 copy 到 LocalNodesDirectory,让 LocalNodeBulkInstaller 下次重装能恢复
        // (含 requirements.txt)。CanExecute 依赖 _envRepo + _syncService 都注入 +
        // LocalNodesDirectory 路径有效;否则 disabled。
        SyncNodesFromEnvCommand = new RelayCommand(
            async _ => await SyncNodesFromEnvAsync().ConfigureAwait(false),
            _ => _envRepo is not null && _syncService is not null && !IsSyncInProgress);
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
    // v1.0.0.x: TemplatePythonDir / DefaultPythonVersion 两个 legacy 字段的 VM 属性已删除 —
    // UI 不再暴露(模板 Python 目录被「系统模板库目录」取代,默认 Python 版本被「Python
    // 解释器」多解释器区段取代)。Settings.cs 字段保留用于老 settings.json 加载时的
    // Python 解释器 一次性迁移。
    // v1.0.0.x: 系统模板库目录 — 用户配置的共享模板根目录
    public string SystemTemplateLibraryDir
    {
        get => _settings.SystemTemplateLibraryDir;
        set { _settings.SystemTemplateLibraryDir = value ?? ""; MarkDirty(nameof(SystemTemplateLibraryDir)); RaisePropertyChanged(); }
    }
    public string EnvsDir
    {
        get => _settings.EnvsDir;
        set { _settings.EnvsDir = value; MarkDirty(nameof(EnvsDir)); RaisePropertyChanged(); }
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
    // v1.0.0.x #577:本地常用节点根目录(env 行「安装本地常用」按钮的源)。
    // 空 = 不启用(运行时报错提示)。BrowseLocalNodesDir 帮助选目录。
    public string LocalNodesDirectory
    {
        get => _settings.LocalNodesDirectory;
        set
        {
            _settings.LocalNodesDirectory = value ?? "";
            MarkDirty(nameof(LocalNodesDirectory));
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
    // v0.6.22+:ModelsDirectory 字段已硬删 — 共享 models 目录 = DefaultModelsDirectory(全局默认 Models 目录)。
    // 此处不再暴露 ModelsDirectory UI;DefaultModelsDirectory 字段承担 env-create junction 目标 +
    // 模型市场下载目录两职。

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
    // v0.6.22+:CivitAI API key — 受限 / NSFW / 标记敏感模型 401/403 解决。
    // 镜像 HuggingFaceApiToken 模式:BindablePasswordBox + MarkDirty + 不立即持久化
    // (用户点 应用 才 Save 写 settings.json)。改 token 后需重启应用让 CivitAiModelSource
    // 重建(同 mirror toggle 行为 — source 在 OnStartup 一次性构造)。
    public string CivitAiApiToken
    {
        get => _settings.CivitAiApiToken;
        set
        {
            var v = value ?? "";
            if (_settings.CivitAiApiToken == v) return;
            _settings.CivitAiApiToken = v;
            MarkDirty(nameof(CivitAiApiToken));
            RaisePropertyChanged();
        }
    }
    // v0.6.21: 模型市场 per-source mirror + HuggingFace source + API token
    public bool ModelSourceCivitAiUseMirror
    {
        get => _settings.ModelSourceCivitAiUseMirror;
        set
        {
            if (_settings.ModelSourceCivitAiUseMirror == value) return;
            _settings.ModelSourceCivitAiUseMirror = value;
            MarkDirty(nameof(ModelSourceCivitAiUseMirror));
            RaisePropertyChanged();
        }
    }

    public string ModelSourceCivitAiMirrorUrl
    {
        get => _settings.ModelSourceCivitAiMirrorUrl;
        set
        {
            var v = value ?? "";
            if (_settings.ModelSourceCivitAiMirrorUrl == v) return;
            _settings.ModelSourceCivitAiMirrorUrl = v;
            MarkDirty(nameof(ModelSourceCivitAiMirrorUrl));
            RaisePropertyChanged();
        }
    }

    // v0.6.22++:per-source 代理三态 — Off / InheritGlobal / AlwaysOn。
    // 决策见 ModelSourceProxyDecision.Resolve(globalMode, sourceMode, settings)。
    // 默认 = InheritGlobal(全局开关一键代理,per-source 跟全局走;Opt-out 显式设 Off;
    // AlwaysOn 用于强制走代理场景)。
    // 改完需重启应用才能让该 source 重新创建带 WebProxy 的 HttpClient。
    public ModelSourceProxyMode ModelSourceCivitAiProxyMode
    {
        get => _settings.ModelSourceCivitAiProxyMode;
        set
        {
            if (_settings.ModelSourceCivitAiProxyMode == value) return;
            _settings.ModelSourceCivitAiProxyMode = value;
            MarkDirty(nameof(ModelSourceCivitAiProxyMode));
            RaisePropertyChanged();
        }
    }

    public bool ModelSourceHuggingFaceEnabled
    {
        get => _settings.ModelSourceHuggingFaceEnabled;
        set
        {
            if (_settings.ModelSourceHuggingFaceEnabled == value) return;
            _settings.ModelSourceHuggingFaceEnabled = value;
            MarkDirty(nameof(ModelSourceHuggingFaceEnabled));
            RaisePropertyChanged();
        }
    }

    public string HuggingFaceApiToken
    {
        get => _settings.HuggingFaceApiToken;
        set
        {
            var v = value ?? "";
            if (_settings.HuggingFaceApiToken == v) return;
            _settings.HuggingFaceApiToken = v;
            MarkDirty(nameof(HuggingFaceApiToken));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsHuggingFaceMirrorInsecure));
        }
    }

    public bool ModelSourceHuggingFaceUseMirror
    {
        get => _settings.ModelSourceHuggingFaceUseMirror;
        set
        {
            if (_settings.ModelSourceHuggingFaceUseMirror == value) return;
            _settings.ModelSourceHuggingFaceUseMirror = value;
            MarkDirty(nameof(ModelSourceHuggingFaceUseMirror));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsHuggingFaceMirrorInsecure));
        }
    }

    public string ModelSourceHuggingFaceMirrorUrl
    {
        get => _settings.ModelSourceHuggingFaceMirrorUrl;
        set
        {
            var v = value ?? "";
            if (_settings.ModelSourceHuggingFaceMirrorUrl == v) return;
            _settings.ModelSourceHuggingFaceMirrorUrl = v;
            MarkDirty(nameof(ModelSourceHuggingFaceMirrorUrl));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsHuggingFaceMirrorInsecure));
        }
    }

    public ModelSourceProxyMode ModelSourceHuggingFaceProxyMode
    {
        get => _settings.ModelSourceHuggingFaceProxyMode;
        set
        {
            if (_settings.ModelSourceHuggingFaceProxyMode == value) return;
            _settings.ModelSourceHuggingFaceProxyMode = value;
            MarkDirty(nameof(ModelSourceHuggingFaceProxyMode));
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// v0.6.21: Returns true if user has a token configured AND the mirror is http:// (insecure).
    /// Used in XAML to show a security warning when token would be sent over plaintext HTTP.
    /// </summary>
    public bool IsHuggingFaceMirrorInsecure
    {
        get
        {
            if (!ModelSourceHuggingFaceUseMirror) return false;
            var url = ModelSourceHuggingFaceMirrorUrl ?? "";
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(HuggingFaceApiToken);
        }
    }

    // v0.6.22.x:ModelScope 国内模型源(默认 disabled)
    public bool ModelSourceModelScopeEnabled
    {
        get => _settings.ModelSourceModelScopeEnabled;
        set
        {
            if (_settings.ModelSourceModelScopeEnabled == value) return;
            _settings.ModelSourceModelScopeEnabled = value;
            MarkDirty(nameof(ModelSourceModelScopeEnabled));
            RaisePropertyChanged();
        }
    }

    public string ModelSourceModelScopeApiToken
    {
        get => _settings.ModelSourceModelScopeApiToken;
        set
        {
            var v = value ?? "";
            if (_settings.ModelSourceModelScopeApiToken == v) return;
            _settings.ModelSourceModelScopeApiToken = v;
            MarkDirty(nameof(ModelSourceModelScopeApiToken));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsModelScopeMirrorInsecure));
        }
    }

    public bool ModelSourceModelScopeUseMirror
    {
        get => _settings.ModelSourceModelScopeUseMirror;
        set
        {
            if (_settings.ModelSourceModelScopeUseMirror == value) return;
            _settings.ModelSourceModelScopeUseMirror = value;
            MarkDirty(nameof(ModelSourceModelScopeUseMirror));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsModelScopeMirrorInsecure));
        }
    }

    public string ModelSourceModelScopeMirrorUrl
    {
        get => _settings.ModelSourceModelScopeMirrorUrl;
        set
        {
            var v = value ?? "";
            if (_settings.ModelSourceModelScopeMirrorUrl == v) return;
            _settings.ModelSourceModelScopeMirrorUrl = v;
            MarkDirty(nameof(ModelSourceModelScopeMirrorUrl));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsModelScopeMirrorInsecure));
        }
    }

    public ModelSourceProxyMode ModelSourceModelScopeProxyMode
    {
        get => _settings.ModelSourceModelScopeProxyMode;
        set
        {
            if (_settings.ModelSourceModelScopeProxyMode == value) return;
            _settings.ModelSourceModelScopeProxyMode = value;
            MarkDirty(nameof(ModelSourceModelScopeProxyMode));
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// v0.6.22.x: Returns true if user has a token configured AND the mirror is http:// (insecure).
    /// Mirrors HF security policy (no token over plaintext HTTP).
    /// </summary>
    public bool IsModelScopeMirrorInsecure
    {
        get
        {
            if (!ModelSourceModelScopeUseMirror) return false;
            var url = ModelSourceModelScopeMirrorUrl ?? "";
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ModelSourceModelScopeApiToken);
        }
    }

    /// <summary>
    /// v0.6.22.x:Reset ModelScope mirror URL to the official https://www.modelscope.cn.
    /// </summary>
    public RelayCommand ResetModelScopeMirrorUrlCommand => new RelayCommand(_ =>
    {
        ModelSourceModelScopeMirrorUrl = ModelSourceFactory.ModelScopeOfficial;
    });

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
            RaisePropertyChanged(nameof(ShowsIgnoredProxyUrlWarning));
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
            RaisePropertyChanged(nameof(ShowsIgnoredProxyUrlWarning));
        }
    }
    public HttpProxyMode HttpProxyMode
    {
        get => _settings.HttpProxyMode;
        set
        {
            var v = value;
            if (_settings.HttpProxyMode == v) return;
            _settings.HttpProxyMode = v;
            // 同步 live HttpProxyConfig(运行时立即生效)
            _proxy.Enabled = v != HttpProxyMode.Off;
            _proxy.UseSystemProxy = v == HttpProxyMode.InheritSystem;
            MarkDirty(nameof(HttpProxyMode));
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ShowsIgnoredProxyUrlWarning));
        }
    }

    /// <summary>
    /// v0.6.22.1+:派生告警 — 当用户启用了「继承系统代理」但 URL/Port 仍填了值时为 true。
    /// URL/Port 在 UseSystemProxy=true 时被 HttpProxyConfig.ApplyTo 默默忽略,无告警用户
    /// 会以为配置生效 → 实际直连超时。XAML 绑这个属性显示一条 ⚠️ Banner 提醒用户去清字段。
    /// </summary>
    public bool ShowsIgnoredProxyUrlWarning =>
        _proxy.Enabled
        && _proxy.UseSystemProxy
        && (!string.IsNullOrWhiteSpace(_proxy.Url) || _proxy.Port > 0);

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
        RaisePropertyChanged(nameof(SystemTemplateLibraryDir));
        RaisePropertyChanged(nameof(EnvsDir));
        RaisePropertyChanged(nameof(GlobalNodesDir));
        RaisePropertyChanged(nameof(LocalNodeDirectory));
        RaisePropertyChanged(nameof(LocalNodesDirectory));
        RaisePropertyChanged(nameof(DefaultModelsDirectory));
        RaisePropertyChanged(nameof(WorkflowsDirectory));
        RaisePropertyChanged(nameof(WorkflowSourceCommunityJsonEnabled));
        RaisePropertyChanged(nameof(WorkflowSourceCivitAiEnabled));
        RaisePropertyChanged(nameof(WorkflowSourceOpenArtEnabled));
        RaisePropertyChanged(nameof(ModelSourceCivitAiEnabled));
        RaisePropertyChanged(nameof(CivitAiApiToken));
        RaisePropertyChanged(nameof(ModelSourceCivitAiUseMirror));
        RaisePropertyChanged(nameof(ModelSourceCivitAiMirrorUrl));
        RaisePropertyChanged(nameof(ModelSourceHuggingFaceEnabled));
        RaisePropertyChanged(nameof(HuggingFaceApiToken));
        RaisePropertyChanged(nameof(ModelSourceHuggingFaceUseMirror));
        RaisePropertyChanged(nameof(ModelSourceHuggingFaceMirrorUrl));
        RaisePropertyChanged(nameof(IsHuggingFaceMirrorInsecure));
        RaisePropertyChanged(nameof(ModelSourceModelScopeEnabled));
        RaisePropertyChanged(nameof(ModelSourceModelScopeApiToken));
        RaisePropertyChanged(nameof(ModelSourceModelScopeUseMirror));
        RaisePropertyChanged(nameof(ModelSourceModelScopeMirrorUrl));
        RaisePropertyChanged(nameof(IsModelScopeMirrorInsecure));
        RaisePropertyChanged(nameof(LogDirectory));
        RaisePropertyChanged(nameof(PythonVenvBaseline));
        RaisePropertyChanged(nameof(GitExe));
        RaisePropertyChanged(nameof(HttpProxyUrl));
        RaisePropertyChanged(nameof(HttpProxyPort));
        RaisePropertyChanged(nameof(HttpProxyMode));
        RaisePropertyChanged(nameof(ShowsIgnoredProxyUrlWarning));
        RaisePropertyChanged(nameof(ActiveQuerySource));
        RaisePropertyChanged(nameof(ActiveDownloadSource));
        RaisePropertyChanged(nameof(FetchNodeVersionsOnRefresh));
        RaisePropertyChanged(nameof(FetchCatalogMetadata));
        RaisePropertyChanged(nameof(PipMirror));
        RaisePropertyChanged(nameof(PipMirrorCustomUrl));
        RaisePropertyChanged(nameof(IsCustomPipMirrorSelected));
        RaisePropertyChanged(nameof(CommonNodes));
        RaisePropertyChanged(nameof(SyncStatusText));
        RaisePropertyChanged(nameof(IsSyncInProgress));
    }

    // ============ v1.0.0.x #589 节点 sync(env → localnodes) ============

    /// <summary>
    /// v1.0.0.x #589:同步按钮命令 — 把所有 env 的 <c>custom_nodes/</c> 反向 copy 到
    /// <see cref="LocalNodesDirectory"/>,让 <c>LocalNodeBulkInstaller</c> 下次重装能恢复
    /// 这些节点(连同 requirements.txt,弥补 cv2 / triton 等 Manager 装但 BED 没装的依赖)。
    /// </summary>
    public RelayCommand SyncNodesFromEnvCommand { get; }

    private string _syncStatusText = "";
    /// <summary>
    /// 同步状态文本 — 给 UI TextBlock 显示进度。空字符串 = 未启动 / 已清空。
    /// </summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set
        {
            if (_syncStatusText == value) return;
            _syncStatusText = value;
            RaisePropertyChanged(nameof(SyncStatusText));
        }
    }

    private bool _isSyncInProgress;
    /// <summary>
    /// sync 进行中 — 期间禁用按钮(防双击)+ status 显示「同步中...」。
    /// </summary>
    public bool IsSyncInProgress
    {
        get => _isSyncInProgress;
        private set
        {
            if (_isSyncInProgress == value) return;
            _isSyncInProgress = value;
            RaisePropertyChanged(nameof(IsSyncInProgress));
            SyncNodesFromEnvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 跑所有 env 的 sync。可空依赖没注入 → no-op 提示用户。聚合每个 env 的结果写到
    /// <see cref="SyncStatusText"/>。
    /// </summary>
    private async Task SyncNodesFromEnvAsync()
    {
        if (_envRepo is null || _syncService is null)
        {
            SyncStatusText = "依赖未注入(只在生产路径可用)";
            return;
        }

        IsSyncInProgress = true;
        // G6:WPF IProgress<T> 在原 SynchronizationContext 上 Report — 用 Progress<string>
        // 包 lambda,所有回调走 UI thread,改 SyncStatusText 不会触发 "itemscontrol 与
        // 项源不一致"。(参见 feedback_wpf_observablecollection_progress)
        // 声明类型用 IProgress<string> 而不是 Progress<string> 是因为 .NET Progress<T>.Report
        // 是 IProgress<T>.Report 的显式接口实现,只有 interface 类型上能直接调。
        IProgress<string> progress = new Progress<string>(line => SyncStatusText = line);
        try
        {
            SyncStatusText = "读取 env 列表...";
            // v0.6.5.8 ListAll 是 sync I/O,SQLite 走内存 + 进程内 file lock,几 ms 内返。
            // 不用 await,保留 UI sync context 给下面 SyncAsync 的 Progress<T> Report 用。
            var envs = _envRepo.ListAll();
            if (envs.Count == 0)
            {
                SyncStatusText = "没有 env,无需同步";
                return;
            }

            var totalAdded = 0;
            var totalUpdated = 0;
            var totalFailed = 0;
            var envFailures = new List<string>();

            foreach (var env in envs)
            {
                progress.Report($"[{envs.IndexOf(env) + 1}/{envs.Count}] 同步 {env.Name} ...");
                var result = await _syncService.SyncAsync(env, progress, CancellationToken.None)
                    .ConfigureAwait(false);
                totalAdded += result.Added.Count;
                totalUpdated += result.Updated.Count;
                totalFailed += result.FailReasons.Count;
                if (!result.Success && !string.IsNullOrEmpty(result.Reason))
                {
                    envFailures.Add($"{env.Name}:{result.Reason}");
                }
            }

            var summary = $"完成 ✓ 新增 {totalAdded} / 更新 {totalUpdated}"
                          + (totalFailed > 0 ? $" / 失败 {totalFailed}" : "");
            SyncStatusText = envFailures.Count > 0
                ? $"{summary};失败 env:{string.Join("; ", envFailures)}"
                : summary;
        }
        catch (Exception ex)
        {
            SyncStatusText = $"同步异常:{ex.Message}";
        }
        finally
        {
            IsSyncInProgress = false;
        }
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
