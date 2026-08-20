using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.20 T8:模型市场 ViewModel。镜像 v0.6.19 WorkflowMarketplaceViewModel 模式:
/// toolbar / kind filter chips / card grid / console panel + 3-state console 可见性。
/// 卡片内容 = ModelEntry (title + Kind badge + NsfwKind badge + per-version CheckBox)。
/// 选中以版本为单位(不是以模型为单位)—— 1 个 CheckBox = 1 个下载任务。
/// </summary>
public class ModelMarketplaceViewModel : INotifyPropertyChanged
{
    private readonly ModelMarketplaceService _marketplace;
    private readonly ModelDownloader _downloader;
    private readonly ModelFilesystemScanner _scanner;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;
    // v0.6.22+:可选 settings 持久化钩子 — proxy toggle 写完设置后立即 Save(用户勾选
    // 期待下次重启生效)。null = 测试 ctor 不传,纯内存可写。
    private readonly SettingsRepository? _settingsRepo;

    private readonly List<ModelEntry> _allModels = new();
    private bool _userHiddenConsole;
    private string _query = "";
    private ModelKind? _activeKindFilter;
    // v0.6.22+:是否显示 NSFW/Mature 模型。默认 true(用户开启 marketplace 的预期就是看到所有内容,
    // CivitAI API 已 nsfw=true 全部拉回);UI 提供 CheckBox 切换,filter 在 ApplyFilter 内做。
    // 不影响 service 层(始终全量 fetch),只影响 _allModels → Models 投影。
    private bool _includeNsfw = true;
    private bool _isBusy;
    // v0.6.22 T6:source 单选 radio — 默认 CivitAI,切换自动重跑当前 query。
    private ModelSourceKind _activeSource = ModelSourceKind.CivitAi;
    // v0.6.22+:分页状态 — _nextCursor=null 表示已无更多(HasNextPage=false)。
    // RefreshAsync / 切 source 都会重置;LoadMoreAsync 拿下一页 + 更新 cursor。
    private string? _nextCursor;
    // v0.6.22+:CivitAI sort + period 过滤 — 用户 2026-08-20 反馈"搜索似乎只传关键词,
    // 不传递其他参数"。HF 不支持这两个参数,VM 仍保持状态但切到 HF 时不影响 API 请求。
    private CivitAiSort _activeSort = CivitAiSort.Newest;
    private CivitAiPeriod _activePeriod = CivitAiPeriod.AllTime;

    /// <summary>底层 fetch 后被 filter strip 处理的"全集"。</summary>
    public ObservableCollection<ModelEntry> Models { get; } = new();

    /// <summary>已勾选要下载的版本(per-version 多选)。</summary>
    public ObservableCollection<ModelVersionEntry> SelectedVersions { get; } = new();

    /// <summary>UI 行内 Console log,同 v0.6.18.4 BulkUpdate 模式。</summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();

    /// <summary>8 个 kind 过滤选项(Unknown 排除)。</summary>
    public ObservableCollection<ModelKind> KindFilters { get; } = new(
        Enum.GetValues<ModelKind>().Where(k => k != ModelKind.Unknown));

    // v0.6.22+:CivitAI sort + period filter chip 选项 — 用户 2026-08-20 反馈
    // "搜索似乎只传关键词"。枚举值驱动 API 参数,UI chip 直接绑。
    public ObservableCollection<CivitAiSort> SortOptions { get; } = new(
        Enum.GetValues<CivitAiSort>());
    public ObservableCollection<CivitAiPeriod> PeriodOptions { get; } = new(
        Enum.GetValues<CivitAiPeriod>());

    // —— Commands ——
    public ICommand RefreshCommand { get; }
    // v0.6.22 T6:SearchCommand — 输入框 Enter 键 + 工具栏 "搜索" 按钮绑的同一命令。
    public ICommand SearchCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand ClearConsoleLogCommand { get; }
    public ICommand HideConsoleCommand { get; }
    // v0.6.22+:toolbar "Console" toggle 按钮 — ✕ 后能再开。
    public ICommand ToggleConsoleVisibilityCommand { get; }
    public ICommand ToggleVersionSelectionCommand { get; }
    // v0.6.22+:分页"加载更多"按钮 — HasNextPage=true 时可点。
    public ICommand LoadMoreCommand { get; }
    // v0.6.22+:卡片每行 📋 按钮 — 把 ModelVersionEntry.PrimaryDownloadUrl 复制到系统剪贴板。
    // 用户 2026-08-20 反馈"没有下载的地址"。
    public ICommand CopyDownloadUrlCommand { get; }

    public ModelMarketplaceViewModel(
        ModelMarketplaceService marketplace,
        ModelDownloader downloader,
        ModelFilesystemScanner scanner,
        Settings settings,
        AppLogger? logger,
        // v0.6.22+:可选 SettingsRepository — proxy toggle 写入后立即 Save 到 .manager/settings.json。
        // 留可空让既有 5 参测试 ctor 不破。可空 → 内存 mutation 但不落盘。
        SettingsRepository? settingsRepo = null)
    {
        _marketplace = marketplace;
        _downloader = downloader;
        _scanner = scanner;
        _settings = settings;
        _logger = logger;
        _settingsRepo = settingsRepo;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        // v0.6.22 T6:SearchCommand — 复用 RefreshAsync 实现,语义 = "用当前输入 + 当前 source 重查"。
        SearchCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        DownloadSelectedCommand = new RelayCommand(
            async _ => await DownloadSelectedAsync(),
            _ => SelectedVersions.Count > 0 && !IsBusy);
        ClearConsoleLogCommand = new RelayCommand(_ => ConsoleLog.Clear());
        HideConsoleCommand = new RelayCommand(_ =>
        {
            _userHiddenConsole = true;
            OnPropertyChanged(nameof(IsConsoleVisible));
        });
        // v0.6.22+:toolbar Console toggle — 可见 → 隐藏;隐藏 → 显示(有内容/busy 时)。
        // 注意:!IsConsoleVisible 的写法是错的 — 它只会在已隐藏时再设隐藏(无变化)。
        // 正确:把当前 visibility 状态映射到 _userHiddenConsole(可见→true 隐藏,隐藏→false 再显)。
        ToggleConsoleVisibilityCommand = new RelayCommand(_ =>
        {
            _userHiddenConsole = IsConsoleVisible;
            OnPropertyChanged(nameof(IsConsoleVisible));
        });
        ToggleVersionSelectionCommand = new RelayCommand(p =>
        {
            if (p is ModelVersionEntry v)
            {
                if (SelectedVersions.Contains(v)) SelectedVersions.Remove(v);
                else SelectedVersions.Add(v);
            }
        });
        // v0.6.22+:LoadMoreCommand — Cursor!=null 时可点,LoadMoreAsync 取下一页 append 到 _allModels。
        LoadMoreCommand = new RelayCommand(
            async _ => await LoadMoreAsync(),
            _ => !IsBusy && HasNextPage);
        // v0.6.22+:CopyDownloadUrlCommand — 参数是 ModelVersionEntry,clipboard 写 PrimaryDownloadUrl。
        // 失败(测试环境无 clipboard / STA 异常)catch 不抛 — UX 不强制成功。
        CopyDownloadUrlCommand = new RelayCommand(p =>
        {
            if (p is ModelVersionEntry v && !string.IsNullOrEmpty(v.PrimaryDownloadUrl))
            {
                try
                {
                    Clipboard.SetText(v.PrimaryDownloadUrl);
                    ConsoleLog.Add($"[复制] {v.PrimaryDownloadUrl}");
                }
                catch (Exception ex)
                {
                    _logger?.Warn("model-marketplace", $"clipboard 写入失败: {ex.Message}");
                }
            }
        });

        // 3-state console visibility:任何 ConsoleLog 变化触发 IsConsoleVisible 重算
        ConsoleLog.CollectionChanged += OnConsoleLogChanged;
        // v0.6.22+:Models 集合变化触发 IsEmpty 重算 — empty overlay Visibility 用。
        Models.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    // —— Bindable properties ——
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value;
            OnPropertyChanged();
            // v0.6.22 T6:UI 改为 Enter 键 / 搜索按钮显式触发 — 不再 auto-filter on type。
        }
    }

    public ModelKind? ActiveKindFilter
    {
        get => _activeKindFilter;
        set
        {
            if (_activeKindFilter == value) return;
            _activeKindFilter = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>
    /// v0.6.22+:NSFW/Mature 内容开关。默认 true(全显示),设 false 时 ApplyFilter 排除
    /// NsfwKind != SFW 的条目(Mature/NSFW)。切换立即 ApplyFilter — 不重 fetch(_allModels 已是全量)。
    /// </summary>
    public bool IncludeNsfw
    {
        get => _includeNsfw;
        set
        {
            if (_includeNsfw == value) return;
            _includeNsfw = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>
    /// v0.6.22 T6:toolbar source 单选 radio — 默认 CivitAI,切换 radio 自动用当前 query
    /// 重跑拉取(走 service-layer sourceFilter,避免 view-time 过滤造成的"看不到被禁 source")。
    /// PropertyChanged → fire-and-forget RefreshAsync;IsBusy 守卫防止并发。
    /// </summary>
    public ModelSourceKind ActiveSource
    {
        get => _activeSource;
        set
        {
            if (_activeSource == value) return;
            _activeSource = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    // v0.6.22+: per-source "使用代理" toggle 移出 model marketplace view(用户 2026-08-20
    // 反馈 "勾选代理直接在设置中勾选就好了,就不要在界面中选择是否使用代理")。
    // Per-source proxy 设置仍通过 SettingsView → 模型市场 段(对应 SettingsViewModel 属性
    // ModelSourceCivitAiUseProxy / ModelSourceHuggingFaceUseProxy)配置。此 VM 内的
    // CivitAiUseProxy / HuggingFaceUseProxy / IsGlobalProxyEnabled 已删除。

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConsoleVisible));
            // v0.6.22+:loading overlay 用 NotIsBusy / empty overlay IsEmpty 用 — IsBusy 翻转时同步通知。
            OnPropertyChanged(nameof(NotIsBusy));
            OnPropertyChanged(nameof(IsEmpty));
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadMoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// v0.6.22+:loading overlay IsEnabled 状态 — true = 不在加载,卡片 grid 可交互。
    /// IsBusy 翻转时 setter 内同步 fire(同 WorkflowMarketplaceViewModel 模式)。
    /// </summary>
    public bool NotIsBusy => !IsBusy;

    /// <summary>
    /// v0.6.22+:empty overlay 状态 — Models 0 条且不在加载时显示 "未找到匹配模型"。
    /// Models.CollectionChanged hook 同步 fire;IsBusy 翻转时也 fire(加载中永远不显示 empty)。
    /// </summary>
    public bool IsEmpty => Models.Count == 0;

    /// <summary>
    /// 3-state console visibility:!userHidden &amp;&amp; (IsBusy || hasContent)。
    /// 用户主动 ✕ 关闭后必须保留意图,直到下次 RefreshAsync/DownloadSelectedAsync 复位。
    /// </summary>
    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    /// <summary>
    /// v0.6.22+:还有更多可加载 = 当前 source 返回过 nextCursor,且不是空。
    /// 切换 query/source 后下次 RefreshAsync 重置。
    /// </summary>
    public bool HasNextPage => !string.IsNullOrEmpty(_nextCursor);

    /// <summary>
    /// v0.6.22+:当前已加载总条数 — UI 显示 "已加载 N 条"。
    /// </summary>
    public int LoadedCount => _allModels.Count;

    /// <summary>
    /// v0.6.22+:CivitAI 排序方式 — chip 点击触发 RefreshAsync 重新拉取(切 sort 必重 fetch)。
    /// 默认 Newest(API 默认值);切换到 HuggingFace 时 chip 隐藏,但属性保留 — 不影响 HF 请求。
    /// </summary>
    public CivitAiSort ActiveSort
    {
        get => _activeSort;
        set
        {
            if (_activeSort == value) return;
            _activeSort = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// v0.6.22+:CivitAI 时间窗口 — chip 点击触发 RefreshAsync 重新拉取。
    /// 默认 AllTime;同 ActiveSort,切到 HF 时 chip 隐藏但属性保留。
    /// </summary>
    public CivitAiPeriod ActivePeriod
    {
        get => _activePeriod;
        set
        {
            if (_activePeriod == value) return;
            _activePeriod = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    // —— Operations ——
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _userHiddenConsole = false;  // reset user-hidden on new refresh
        // v0.6.22+:分页 — 切 query/source/filter 都从第一页开始。RefreshAsync 用 LoadPageAsync
        // 而不是 LoadAllAsync 是关键:LoadAllAsync 内部循环 maxResults=50 后就停,根本不知道
        // "还有更多" — UI 上 Load more 按钮永远 disabled。LoadPageAsync 返回 nextCursor,
        // source 还有更多时 _nextCursor 非空 → HasNextPage=true → 按钮可点。
        _nextCursor = null;
        try
        {
            // v0.6.22 T6+:Progress<string> 在 VM 端构造 — ctor 捕获 UI SynchronizationContext,
            // service 内 Report() 自动 marshal 回 UI 线程 → ConsoleLog.Add 安全。
            var progress = new Progress<string>(line => ConsoleLog.Add(line));
            // VM-side await MUST NOT use .ConfigureAwait(false) — continuation runs on UI sync ctx,
            // touching Models.Clear() / Add() requires WPF-friendly context.
            var (entries, nextCursor) = await _marketplace.LoadPageAsync(
                _query, cursor: null, pageSize: 50, sourceFilter: _activeSource,
                sort: _activeSort, period: _activePeriod,
                progress: progress, ct: CancellationToken.None);
            _allModels.Clear();
            _allModels.AddRange(entries);
            _nextCursor = nextCursor;
            ApplyFilter();
            OnPropertyChanged(nameof(HasNextPage));
            OnPropertyChanged(nameof(LoadedCount));
            (LoadMoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-marketplace", $"刷新失败: {ex.Message}");
            ConsoleLog.Add($"[错误] 刷新失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// v0.6.22+:分页"加载更多" — 拿下一页 append 到 _allModels,更新 _nextCursor。
    /// 不重置现有数据(append 而非 replace),用户可看到逐渐累积的结果。
    /// HasNextPage=false 时按钮禁用。
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (IsBusy || !HasNextPage) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(line => ConsoleLog.Add(line));
            var (entries, nextCursor) = await _marketplace.LoadPageAsync(
                _query, _nextCursor, pageSize: 100, sourceFilter: _activeSource,
                sort: _activeSort, period: _activePeriod,
                progress: progress, ct: CancellationToken.None);
            _allModels.AddRange(entries);
            _nextCursor = nextCursor;
            ApplyFilter();
            OnPropertyChanged(nameof(HasNextPage));
            OnPropertyChanged(nameof(LoadedCount));
            (LoadMoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-marketplace", $"加载更多失败: {ex.Message}");
            ConsoleLog.Add($"[错误] 加载更多失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedVersions.Count == 0 || IsBusy) return;
        IsBusy = true;
        _userHiddenConsole = false;
        try
        {
            // Progress<string> captures the current SynchronizationContext (UI thread) at
            // construction — Report() automatically marshals back to UI thread.
            var progress = new Progress<string>(line => ConsoleLog.Add(line));
            var versions = SelectedVersions.ToList();
            // v0.6.22+:ModelsDirectory 字段已硬删,改用 DefaultModelsDirectory (同时担任 env-create
            // junction 目标 + 模型市场下载目录)。空字符串时 ModelDownloader 内部 fallback。
            var summary = await _downloader.DownloadBatchAsync(
                versions, _settings.DefaultModelsDirectory, progress);
            ConsoleLog.Add(
                $"[完成] 成功 {summary.Succeeded}, 失败 {summary.Failed}, 耗时 {summary.TotalDuration.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            _logger?.Error("model-download", $"批量下载异常: {ex.Message}");
            ConsoleLog.Add($"[错误] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allModels.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_query))
        {
            filtered = filtered.Where(m =>
                m.Title.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }
        if (_activeKindFilter is { } k)
        {
            filtered = filtered.Where(m => m.Kind == k);
        }
        // v0.6.22+:NSFW filter — IncludeNsfw=false 时只保留 SFW(Mature/NSFW 隐藏)。
        if (!_includeNsfw)
        {
            filtered = filtered.Where(m => m.NsfwKind == ModelNsfwKind.SFW);
        }
        Models.Clear();
        foreach (var m in filtered) Models.Add(m);
    }

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsConsoleVisible));
    }

    // —— INotifyPropertyChanged ——
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
