using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

/// <summary>v1.0.0:本地模型视图模型。扫 <c>Settings.DefaultModelsDirectory</c>,
/// 按 <c>SourceId</c> 合并多版本,顶部 kind chip filter + 2 列卡片。View-only。</summary>
public sealed class LocalModelsViewModel : INotifyPropertyChanged
{
    private readonly Settings _settings;
    private readonly ModelFilesystemScanner _scanner;
    private readonly AppLogger? _logger;
    private readonly CivitAiLookupService? _lookup;
    private readonly CivitaiHashCache? _hashCache;
    private readonly CivitaiMatcherOrchestrator? _orchestrator;
    private readonly RelayCommand _reloadCommand;
    private readonly RelayCommand _lookupCivitAiCommand;
    private readonly HashSet<string> _lookupsInFlight = new();
    private List<LocalModelCard> _allCards = new();

    public ObservableCollection<LocalModelCard> FilteredModels { get; } = new();
    public ObservableCollection<KindChip> KindChips { get; } = new();
    public string? EmptyMessage { get; private set; }
    public bool IsBusy { get; private set; }
    /// <summary>v1.0.0 T2:View 绑 IsEmpty 切 empty state vs card grid(NullToVisibilityConverter 不支持 invert 参数)。</summary>
    public bool IsEmpty => EmptyMessage is not null;

    public ICommand ReloadCommand => _reloadCommand;
    /// <summary>v1.0.0 T11:CivitAI lookup 命令 — parameter 是被点的 LocalModelCard。
    /// 只对 Source="Local" 的卡可用(meta.json 卡已有 SourceUrl 直接 web 跳转,
    /// 按钮藏起来 + 命令 canExecute 返回 false)。</summary>
    public ICommand LookupCivitAiCommand => _lookupCivitAiCommand;

    public event PropertyChangedEventHandler? PropertyChanged;

    private KindChip? _activeChip;
    public KindChip? ActiveChip
    {
        get => _activeChip;
        set
        {
            _activeChip = value;
            PropertyChanged?.Invoke(this, new(nameof(ActiveChip)));
            ApplyFilter();
        }
    }

    public LocalModelsViewModel(
        Settings settings,
        ModelFilesystemScanner scanner,
        AppLogger? logger = null,
        CivitAiLookupService? lookup = null,
        CivitaiHashCache? hashCache = null,
        CivitaiMatcherOrchestrator? orchestrator = null)
    {
        _settings = settings;
        _scanner = scanner;
        _logger = logger;
        _lookup = lookup;
        _hashCache = hashCache;
        _orchestrator = orchestrator;
        _reloadCommand = new RelayCommand(_ => ReloadAsync(), _ => !IsBusy);
        // v1.0.0 T11:lookup 命令 — canExecute 守卫 (Source="Local" + lookup service 可用 + 无 in-flight)。
        // Button Visibility 用 IsLookupEnabled(card) 计算属性绑(避免新 converter,逻辑留 VM 可测)。
        _lookupCivitAiCommand = new RelayCommand(
            execute: card =>
            {
                if (card is LocalModelCard lc && lc.Source == "Local")
                {
                    _ = ExecuteLookupAsync(lc);
                }
            },
            canExecute: card =>
            {
                if (card is not LocalModelCard lc) return false;
                if (lc.Source != "Local") return false;
                if (_lookup is null) return false;
                return !_lookupsInFlight.Contains(lc.Title);
            });
    }

    public void Initialize() => ReloadAsync();

    /// <summary>v1.0.0 T13-7:reload + 可选 progress forward 到 scanner(hash + match + cover 下载进度)。
    /// 调用方(Initialize / 按钮 click)不传 progress → 走 null 路径,scanner 内部 ctx.Progress 也是 null,
    /// 行为跟 T11 一致。MainVM 传 progress 时,用户能在日志/Console 看到 `[hash] N/总数` 等行。</summary>
    public async Task ReloadAsync(IProgress<string>? progress = null)
    {
        IsBusy = true;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        _reloadCommand.RaiseCanExecuteChanged();

        var dir = _settings.DefaultModelsDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            EmptyMessage = "未配置 Models 目录 — 请在设置中配置";
            _allCards = new();
        }
        else
        {
            IReadOnlyList<DownloadedModel> raw;
            try
            {
                // v1.0.0 T13-7:Build ScanContext 当 hash cache + orchestrator 都注入时(scanner 内部
                // 检测 ctx != null 才启用 hash+match+cover 路径,T11 旧 caller 走 ctx=null 保持纯 enumeration)。
                ScanContext? ctx = null;
                if (_hashCache is not null && _orchestrator is not null)
                {
                    ctx = new ScanContext { HashCache = _hashCache, Matcher = _orchestrator, Progress = progress };
                }
                raw = await Task.Run(() => _scanner.Scan(dir, ctx)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Warn("local-models", $"scan failed: {ex.Message}");
                raw = Array.Empty<DownloadedModel>();
            }
            _allCards = GroupToCards(raw);
            EmptyMessage = _allCards.Count == 0 ? "暂无已下载模型" : null;
        }

        RebuildKindChips();
        ActiveChip = KindChips[0];   // "全部" chip
        ApplyFilter();

        IsBusy = false;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
        PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
        _reloadCommand.RaiseCanExecuteChanged();
    }

    private static List<LocalModelCard> GroupToCards(IReadOnlyList<DownloadedModel> raw)
    {
        return raw
            .GroupBy(d => d.SourceId)
            .Select(g =>
            {
                // v1.0.0 T10:GroupBy first 是 GroupBy 内部顺序,语义模糊(用户重命名 preview 时可能不一致)。
                // 改为 OrderBy(DownloadedAt).Last() — latest-mtime record wins preview path
                // (跟 LatestDownloadedAt 一致,卡片显示也是 latest mtime)。
                var latestRecord = g.OrderBy(d => d.DownloadedAt).Last();
                var latest = g.Max(d => d.DownloadedAt);
                return new LocalModelCard(
                    Title: latestRecord.Title ?? "",
                    Kind: latestRecord.Kind,
                    Source: latestRecord.Source,
                    VersionCount: g.Count(),
                    LatestDownloadedAt: latest,
                    SourceUrl: null,
                    PreviewImagePath: latestRecord.PreviewImagePath,
                    // v1.0.0 T13-7:3 个 hash-matching 字段从 DownloadedModel 透传到 card。
                    // latestRecord 已是 sorted-by-mtime Last(),其 Hash/MatchedDetail/MatchSource
                    // 由 scanner 的 HashAndMatch 阶段填入(scanner 内部所有同 SourceId record 共享
                    // 同一文件 hash,MatchSource/MatchedDetail 也一致 — 这里取 latestRecord 即可)。
                    Hash: latestRecord.Hash,
                    MatchedDetail: latestRecord.MatchedDetail,
                    MatchSource: latestRecord.MatchSource);
            })
            .OrderByDescending(c => c.LatestDownloadedAt ?? DateTime.MinValue)
            .ToList();
    }

    private void RebuildKindChips()
    {
        KindChips.Clear();
        KindChips.Add(new KindChip(null, "全部", _allCards.Count));
        var byKind = _allCards.GroupBy(c => c.Kind).OrderBy(g => g.Key.ToString());
        foreach (var g in byKind)
        {
            KindChips.Add(new KindChip(g.Key, g.Key.ToString(), g.Count()));
        }
    }

    private void ApplyFilter()
    {
        FilteredModels.Clear();
        var src = _activeChip?.Kind is null
            ? _allCards
            : _allCards.Where(c => c.Kind == _activeChip!.Kind).ToList();
        foreach (var c in src) FilteredModels.Add(c);
    }

    // ===== v1.0.0 T11:CivitAI lookup integration =====

    /// <summary>v1.0.0 T11:卡片 lookup 按钮可见性 — Source="Local" 且 lookup service 注入。
    /// meta.json / civitai / huggingface 卡片(Source != "Local")不显示按钮 — 这些卡已有
    /// SourceUrl,用户直接 web 跳转,不需要再查一遍。</summary>
    public bool IsLookupEnabled(LocalModelCard card)
        => card.Source == "Local" && _lookup is not null;

    /// <summary>v1.0.0 T11:卡片 lookup in-flight 状态 — XAML 绑 button IsEnabled + 忙提示。
    /// 用 Title 作 key(SourceId 也可,Title 同 kind 下唯一)。同一卡同时只 1 个 lookup 在跑。</summary>
    public bool IsLookupInProgress(LocalModelCard card)
        => _lookupsInFlight.Contains(card.Title);

    /// <summary>v1.0.0 T11:执行 lookup — fire-and-forget 在 command execute 启动。
    /// Modal dialog 在 UI 线程 ShowDialog,LoadAsync 等 await 回 UI SynchronizationContext。
    /// ConfigureAwait(true) 在 VM 内部仍需(ObservableCollection 跨线程写会抛 — v0.6.19.x lesson)。
    /// v1.0.0 T13-7:把 card 传给 dialog VM — 如果 card 已被 scanner 在 Reload 阶段 hash-matched
    /// (MatchedDetail 非 null),dialog 直接开 Detail state,跳过 searching。</summary>
    private async Task ExecuteLookupAsync(LocalModelCard card)
    {
        if (_lookup is null) return;

        _lookupsInFlight.Add(card.Title);
        RaiseCommandsCanExecuteChanged();
        try
        {
            var dlg = new LocalModelCivitAiDialog
            {
                DataContext = new LocalModelCivitAiDialogViewModel(_lookup, card.Title, _logger, card: card),
            };
            // modal 阻塞到用户关窗
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger?.Error("local-models",
                $"Lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _lookupsInFlight.Remove(card.Title);
            RaiseCommandsCanExecuteChanged();
        }
    }

    /// <summary>v1.0.0 T11:in-flight 集合变更后让 button CanExecute 重新计算。
    /// LocalModelCard 是 record value type — IsLookupEnabled / IsLookupInProgress 接收 card 实例,
    /// WPF CommandManager 不能直接 re-eval,只能 RaiseCanExecuteChanged on RelayCommand。</summary>
    private void RaiseCommandsCanExecuteChanged()
    {
        _reloadCommand.RaiseCanExecuteChanged();
        _lookupCivitAiCommand.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
    }
}

public sealed record KindChip(ModelKind? Kind, string Display, int Count);
