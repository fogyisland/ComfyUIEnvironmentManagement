using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    /// <summary>v1.0.0 Console panel:用户 ✕ 关闭意图必须保留,下次 Reload 复位。
    /// 三态可见性 = !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0)。</summary>
    private bool _userHiddenConsole;
    /// <summary>v1.0.0 Console panel:内部 Progress&lt;string&gt; sink — 接收 scanner [hash]/[match]/[preview] 行,
    /// 推 ConsoleLog ObservableCollection。ctor 构造时捕获 UI SynchronizationContext
    /// 自动 marshal 回 UI 线程(同 v0.6.18.4 BulkUpdate 模式)。</summary>
    private readonly IProgress<string> _consoleSink;
    /// <summary>v1.0.0 T-D5:scanner streaming emit 的累加器 — Phase 1 一波填满,Phase 2 增量覆盖同 SourceId 行。
    /// Task.Run 完后 final 列表覆盖这里(streaming 可能有 race 覆盖错),作为 authoritative result。</summary>
    private readonly List<DownloadedModel> _streamedRaw = new();

    public ObservableCollection<LocalModelCard> FilteredModels { get; } = new();
    public ObservableCollection<KindChip> KindChips { get; } = new();
    /// <summary>v1.0.0 Console panel:[hash]/[match]/[preview] 实时日志流。
    /// 镜像 v0.6.18.4 批量更新 Console 模式 — Progress&lt;string&gt; 构造时捕获 UI SynchronizationContext,
    /// 自动 marshal 回 UI 线程避免 STA 跨线程异常(同 v0.6.19.x hotfix)。</summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();
    /// <summary>v1.0.0 三态可见性:用户未关 && (busy 或 有内容)。Start() 复位 _userHiddenConsole,
    /// 用户点 ✕ 设 true(下次 Start 才重新显示,意图优先)。</summary>
    public bool IsConsoleVisible =>
        !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);
    public string? EmptyMessage { get; private set; }
    public bool IsBusy { get; private set; }
    /// <summary>v1.0.0 T2:View 绑 IsEmpty 切 empty state vs card grid(NullToVisibilityConverter 不支持 invert 参数)。</summary>
    public bool IsEmpty => EmptyMessage is not null;
    /// <summary>首次扫描时(scanner 还没产数据 + IsBusy=true)显示"加载中…"overlay。
    /// 已有卡片时(用户切走再回来触发 background refresh)不挡 card grid — 直接刷新现有数据,
    /// toolbar 显示细"刷新中…"指示。这是用户反馈 "本地模型一直出在加载中,其实应该首先加载完了,
    /// 再刷新这样比较好" 的修复:首屏可能短暂显示 loading,后续进入永远先看到现有卡。</summary>
    public bool ShowLoadingOverlay => IsBusy && _allCards.Count == 0;
    /// <summary>Toolbar 上的 "刷新中…" 指示 — IsBusy && _allCards.Count > 0(已有数据 + 正在刷新)。
    /// 跟 ShowLoadingOverlay 互补:loading overlay 走首次,刷新中走后续。</summary>
    public bool IsRefreshingInBackground => IsBusy && _allCards.Count > 0;

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
        // v1.0.0 Console panel:内部 sink 接收 scanner progress 行,推 ConsoleLog。
        // ctor 在 UI 线程跑 → Progress 捕获 UI SyncContext → Report 自动 marshal 回 UI 线程。
        _consoleSink = new Progress<string>(line =>
        {
            ConsoleLog.Add(line);
            PropertyChanged?.Invoke(this, new(nameof(IsConsoleVisible)));
        });
        // v1.0.0 Console panel:ConsoleLog.CollectionChanged 也触发 IsConsoleVisible 重算
        // (覆盖 ClearConsoleLog 等直接改 ObservableCollection 的路径)。
        ConsoleLog.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new(nameof(IsConsoleVisible)));
        // v1.0.0.x:用户反馈"本地模型默认情况刷新操作不自动启动,只有手动启动才去进行刷新操作"。
        // VM ctor 设 EmptyMessage placeholder 提醒用户点「🔄 刷新」按钮 — 不再 fire-and-forget 触发
        // ReloadAsync。IsBusy 默认 false → ShowLoadingOverlay / IsRefreshingInBackground 都 false,
        // 用户首屏看到 placeholder 提示而非 loading 圈。
        EmptyMessage = "点击「🔄 刷新」加载本地模型";
        PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
        PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
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

    /// <summary>v1.0.0 T13-7:reload + 可选 progress forward 到 scanner(hash + match + cover 下载进度)。
    /// 调用方(Initialize / 按钮 click)不传 progress → 走 null 路径,scanner 内部 ctx.Progress 也是 null,
    /// 行为跟 T11 一致。MainVM 传 progress 时,用户能在日志/Console 看到 `[hash] N/总数` 等行。
    /// 用户反馈 "本地模型一直出在加载中" — ShowLocalModels 每次进入都触发本方法,带 in-flight 守卫:
    /// 上一次 reload 还没跑完时跳过(避免 sidebar 反复切导致并发 scan 互踩 FilteredModels)。
    /// skip-path 返回 completed task 让 caller 的 `_ = ReloadAsync()` 不报 unobserved exception。
    /// v1.0.0 T-D5:scanner 通过 ctx.ModelUpdated stream emit DownloadedModel — Phase 1
    /// (ScanCore 完)emit 所有 raw entries → 立即建卡 + IsBusy=false → 用户秒级看到卡;
    /// Phase 2 (HashAndMatch 每个 match 完)emit 更新版 → 按 SourceId 就地更新卡(match badge)。
    /// 这样 hash+match 阶段网络慢不再挡 UI(用户观察的 "一直出在加载中" 是 scanner 把整段
    /// 串行化导致 — 之前 Phase 2 完成才出卡,现在 Phase 1 完就出卡,Phase 2 在背后渐进更新)。
    /// Progress&lt;DownloadedModel&gt; 捕获 UI SyncContext,callback 自动 marshal 回 UI 线程更新
    /// ObservableCollection — 跟 v0.6.18.4 / v0.6.19 / v0.6.22.++ 同款 progress pattern。</summary>
    public async Task ReloadAsync(IProgress<string>? progress = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(ShowLoadingOverlay)));
        PropertyChanged?.Invoke(this, new(nameof(IsRefreshingInBackground)));
        _reloadCommand.RaiseCanExecuteChanged();

        var dir = _settings.DefaultModelsDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            EmptyMessage = "未配置 Models 目录 — 请在设置中配置";
            _allCards = new();
            RebuildKindChips();
            ActiveChip = KindChips[0];
            ApplyFilter();
        }
        else
        {
            // v1.0.0 Console panel:重置 console 状态 — 清旧行 + 复位 _userHiddenConsole 让 ✕ 关闭意图在下一次 Reload 解除。
            ConsoleLog.Clear();
            _userHiddenConsole = false;
            PropertyChanged?.Invoke(this, new(nameof(IsConsoleVisible)));

            // v1.0.0 T-D5:用 _streamedRaw 累加 scanner stream 出来的 entries — Phase 1 一波填满,
            // Phase 2 增量覆盖同 SourceId 行;scan 完成后用 final Raw 校正(可能并发 race 的覆盖)。
            _streamedRaw.Clear();
            var modelUpdated = new Progress<DownloadedModel>(OnModelStreamed);

            // v1.0.0 Console panel:链式转发 scanner progress 行到 (a) 本 VM 内部 _consoleSink → ConsoleLog
            // (用户可见 UI 面板) + (b) 调用方传入的 progress(MainVM 转发到自己的 logger)。
            // progress 为 null 时直接用 _consoleSink — 没调用方要转发。两条路径都 Push 到 UI 线程,
            // 顺序由各自 Post 排队决定,可接受(行号按 scanner emit 顺序,只是 UI 显示可能交错)。
            IProgress<string> ctxProgress;
            if (progress is null)
            {
                ctxProgress = _consoleSink;
            }
            else
            {
                var outer = progress;
                ctxProgress = new Progress<string>(line =>
                {
                    _consoleSink.Report(line);
                    outer.Report(line);
                });
            }

            ScanContext? ctx = null;
            if (_hashCache is not null && _orchestrator is not null)
            {
                ctx = new ScanContext
                {
                    HashCache = _hashCache,
                    Matcher = _orchestrator,
                    Progress = ctxProgress,
                    ModelUpdated = modelUpdated,
                };
            }
            else
            {
                // v1.0.0 T-D5:即使没 hash+match,仍用 streaming 路径 — 一致性 + 让用户秒级看到卡。
                // 没 hash+match 时 scanner 不 emit [hash]/[match] 行,console 会空 — 这是正确行为。
                ctx = new ScanContext { ModelUpdated = modelUpdated };
            }

            IReadOnlyList<DownloadedModel> final;
            try
            {
                final = await Task.Run(() => _scanner.Scan(dir, ctx)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Warn("local-models", $"scan failed: {ex.Message}");
                final = Array.Empty<DownloadedModel>();
            }

            // scanner 返回的 final 列表是权威结果(streaming 可能有 race 覆盖错) — 用它覆盖 _streamedRaw。
            _streamedRaw.Clear();
            foreach (var m in final) _streamedRaw.Add(m);
            RebuildCardsAndChips();

            EmptyMessage = _allCards.Count == 0 ? "暂无已下载模型" : null;
        }

        IsBusy = false;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(ShowLoadingOverlay)));
        PropertyChanged?.Invoke(this, new(nameof(IsRefreshingInBackground)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
        PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
        _reloadCommand.RaiseCanExecuteChanged();
    }

    /// <summary>v1.0.0 T-D5:scanner stream callback — Phase 1 一次性收到所有 raw entries,
    /// 立即 RebuildCardsAndChips → IsBusy 由调用方在 Task.Run await 完成后置 false,
    /// 但因为 callback 已经填好卡,用户在等 final scan 期间(可能 0 ms — filesystem enumeration)
    /// 已经能看到卡。如果 Phase 2 来(同 SourceId 已存在卡),则 merge:Hash / MatchedDetail /
    /// MatchSource / PreviewImagePath 任一非空时覆盖 card 对应字段。</summary>
    private void OnModelStreamed(DownloadedModel m)
    {
        // Phase 1:第一次见到 SourceId 时 append
        var existingIdx = _streamedRaw.FindIndex(x => x.SourceId == m.SourceId);
        if (existingIdx < 0)
        {
            _streamedRaw.Add(m);
        }
        else
        {
            // Phase 2 update:覆盖同 SourceId 行(scanner HashAndMatch emit 更新版 entries),
            // 保留 Phase 1 已经有但 Phase 2 没改的字段(VersionCount 等)。
            _streamedRaw[existingIdx] = m;
        }

        // 第一次见到任何 entry 时(Phase 1 第一条)就重建卡 + 关掉 IsBusy — 用户秒级看到。
        // 后续 emit(Phase 2 更新)就地更新对应卡 — 不重建整个 FilteredModels(避免 ObservableCollection clear+re-add 闪烁)。
        if (IsBusy)
        {
            RebuildCardsAndChips();
            // 注意:此时不设 IsBusy=false — final 还没回,可能还有 stream emit。IsBusy 在 caller await 完时设 false。
        }
        else
        {
            UpdateCardForEntry(m);
        }
    }

    /// <summary>v1.0.0 T-D5:用 _streamedRaw 重算 _allCards + KindChips + FilteredModels。
    /// 比旧的 _allCards = GroupToCards(raw) 更通用 — 同样能 list 整 raw 列表。</summary>
    private void RebuildCardsAndChips()
    {
        _allCards = GroupToCards(_streamedRaw);
        EmptyMessage = _allCards.Count == 0 ? "暂无已下载模型" : null;
        RebuildKindChips();
        ActiveChip = KindChips[0];
        ApplyFilter();

        PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
        PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
        PropertyChanged?.Invoke(this, new(nameof(ShowLoadingOverlay)));
        PropertyChanged?.Invoke(this, new(nameof(IsRefreshingInBackground)));
    }

    /// <summary>v1.0.0 T-D5:按 SourceId 找到对应 LocalModelCard 就地更新 Hash / MatchedDetail /
    /// MatchSource 字段(其他字段 Title / Kind / VersionCount 等不变)。LocalModelCard 是
    /// positional record 属性 init-only,所以用 `with` 建新 card → 替换 _allCards[i] →
    /// 同步替换 FilteredModels 里同一实例引用(ObservableCollection<T> reference equality
    /// 通过 oldCard.IndexOf 定位)。如果找不到 SourceId(罕见 — Phase 1 emit 漏了某条),
    /// fallback RebuildCardsAndChips。</summary>
    private void UpdateCardForEntry(DownloadedModel m)
    {
        var idx = _allCards.FindIndex(c => c.SourceId == m.SourceId);
        if (idx < 0)
        {
            RebuildCardsAndChips();
            return;
        }
        if (m.MatchedDetail is null && m.MatchSource is null && m.Hash is null) return;

        var oldCard = _allCards[idx];
        var newCard = oldCard.WithMatchStatus(m.Hash, m.MatchedDetail, m.MatchSource);
        _allCards[idx] = newCard;

        var fIdx = FilteredModels.IndexOf(oldCard);
        if (fIdx >= 0) FilteredModels[fIdx] = newCard;
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
                    SourceId: g.Key,
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

    /// <summary>v1.0.0 Console panel:用户点 ✕ — 清空行 + 记录"用户主动隐藏"意图。
    /// 下次 Start()/Reload() 重置 _userHiddenConsole → IsConsoleVisible 自动重算。</summary>
    public void ClearConsoleLog()
    {
        _userHiddenConsole = true;
        ConsoleLog.Clear();
    }
}

public sealed record KindChip(ModelKind? Kind, string Display, int Count);
