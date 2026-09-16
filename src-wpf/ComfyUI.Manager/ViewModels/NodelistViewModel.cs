using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-redesign:Settings → 节点列表目录 UI VM
///
/// 用户原话:
/// 1. 节点目录按钮:完整入库 / 增量入库
/// 2. 提取内容建 SQLite 表 NodesList + Nodesdetail(一对多)
/// 3. 直接加载数据库展示所有节点
/// 4. 默认节点 json 放 nodelist 目录,每次从
///    https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json 拉所有文件
/// 5. 增量入库:解析文件夹下 json 写 author + repo_name 到 NodesList
/// 6. 调 author + repo_name 去云端查详细
/// 7. 写 NodesList + NodesDetailed(一对多)
/// 8. 一天一次增量入库,用户可手动点按钮
///
/// 简化:只保留"增量入库"按钮 + "下载 + 入库"组合。
/// v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43)+ user:「左右结构」+ 「空路径自动 seed」
/// </summary>
public sealed class NodelistViewModel : ViewModelBase
{
    private readonly NodelistDownloader _downloader;
    private readonly NodelistIngestor _ingestor;
    private readonly NodelistRepository _repo;
    private readonly string _defaultDirectory;  // <projectRoot>/nodelist,VM 永远有 fallback 路径
    private readonly Action<string>? _onConfiguredDirectoryChanged;  // 把有效路径写回 Settings

    // v1.0.0.x (2026-09-15) T43g+user:详情 Action Bar 三个按钮依赖的 service。
    // 全 nullable 保留向后兼容(测试 ctor / 早期 caller 不传),App.xaml.cs 总是传。
    private readonly IBrowserLauncher? _browserLauncher;
    private readonly IEnvironmentRepository? _envRepo;
    private readonly NodeOperations? _nodeOps;
    // v1.0.0.x (2026-09-16) T43i.5+user「下载到本地是下载到本地的节点 LocalNodes 下面」:
    // 「💾 下载到本地」按钮调 _nodeOps.DownloadAsync(localDir=..., ...),
    // localDir 来自 _settings.LocalNodeDirectory(用户配置 LocalNodes 目录)。
    private readonly Settings? _settings;

    /// <summary>左 list — 作者/仓库名列表(全量,Reload 时从 SQLite 灌入)。</summary>
    public ObservableCollection<NodelistEntryRow> Entries { get; } = new();

    // v1.0.0.x (2026-09-15) T43g+user 「中间实现分页效果,不然耗费大量内存资源」:
    // T43f 用 FilteredEntries(全量客户端过滤+虚拟滚动),T43g 改成传统分页器。
    // _allFiltered = search 后的全量,CurrentPageItems = 当前页 50 条切片,绑 XAML。
    private const int DefaultPageSize = 50;
    private List<NodelistEntryRow> _allFiltered = new();
    private int _currentPage = 1;

    /// <summary>左 list 当前页切片 — ListBox 实际绑这个。</summary>
    public ObservableCollection<NodelistEntryRow> CurrentPageItems { get; } = new();

    /// <summary>当前页码(1-based)。变更通过 GoToPage 触发 LoadCurrentPage + RaisePageProperties。</summary>
    public int CurrentPage => _currentPage;

    /// <summary>总页数 — _allFiltered 为空时为 1(避免 CanGoPrev 在空状态下莫名可点)。</summary>
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_allFiltered.Count / DefaultPageSize));

    /// <summary>分页栏文本(「1 / 104 页 (共 5175 条,每页 50 条)」)。</summary>
    public string PageInfoText => $"{_currentPage} / {TotalPages} 页 (共 {_allFiltered.Count} 条,每页 {DefaultPageSize} 条)";

    /// <summary>「« 首页」「‹ 上一页」CanExecute 条件。</summary>
    public bool CanGoPrev => _currentPage > 1;

    /// <summary>「下一页 ›」「末页 »」CanExecute 条件。</summary>
    public bool CanGoNext => _currentPage < TotalPages;

    /// <summary>右 detail — 选中 entry 的版本列表(随 SelectedEntry 联动)。</summary>
    public ObservableCollection<DetailRow> SelectedDetails { get; } = new();

    private NodelistEntryRow? _selectedEntry;
    /// <summary>左 list 当前选中 — 联动 SelectedDetails + SelectedEntryHeader。</summary>
    public NodelistEntryRow? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (_selectedEntry == value) return;
            _selectedEntry = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(SelectedEntryHeader));
            RefreshSelectedDetails();
            // v1.0.0.x (2026-09-15) T43g+user:Action Bar 按钮依赖选中项 ——
            // 选中变化时刷新 4 个 command 的 CanExecute(无选中时禁用)。
            // v1.0.0.x (2026-09-16) T43i.5 新增 DownloadToLocalCommand(💾 下载到本地)。
            OpenInBrowserCommand.RaiseCanExecuteChanged();
            CopyUrlCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            DownloadToLocalCommand.RaiseCanExecuteChanged();
            // v1.0.0.x T43h:ShowRawJsonCommand 依赖 SelectedDetails[0].RawJson,
            // 选中变化后 RefreshSelectedDetails 已重填 SelectedDetails,这里刷 CanExecute。
            ShowRawJsonCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>右 detail 顶部 header 文本("foo / bar" 大字)。</summary>
    public string SelectedEntryHeader =>
        _selectedEntry is null
            ? "(请从左侧选一个 entry)"
            : $"{_selectedEntry.Author} / {_selectedEntry.RepoName}";

    /// <summary>右 detail 顶部 metadata 文本。
    /// v1.0.0.x T43h+user:在原有 FirstSeenAt/LastIngestedAt/Source 上追加 Host/FetchedAt
    /// 元信息 — 详情来源(GitHub/GitLab)+ 最近一次拉取时间,提供数据来源透明度。</summary>
    public string SelectedEntryMeta
    {
        get
        {
            if (_selectedEntry is null) return "";
            var firstDetail = SelectedDetails.FirstOrDefault();
            if (firstDetail is null)
                return $"首次入库:{_selectedEntry.FirstSeenAt}    最近入库:{_selectedEntry.LastIngestedAt}    来源:{_selectedEntry.Source}";
            var hostStr = string.IsNullOrWhiteSpace(firstDetail.Host) ? "github" : firstDetail.Host;
            var fetchedStr = string.IsNullOrWhiteSpace(firstDetail.FetchedAt) ? "?" : firstDetail.FetchedAt;
            return $"Host:{hostStr}    详情拉取:{fetchedStr}    首次入库:{_selectedEntry.FirstSeenAt}    最近入库:{_selectedEntry.LastIngestedAt}    来源:{_selectedEntry.Source}";
        }
    }

    // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43):「NodelistDirectory」语义 ——
    // Settings.NodelistDirectory 若空,fallback 到 _defaultDirectory(<projectRoot>/nodelist);
    // setter 把 fallback 路径写回 Settings(用户首次 seed 后下次不再触发 seed)。
    // getter 永远返回非空(VM CanExecute 始终可点)。
    private string _configuredDirectory = "";
    public string NodelistDirectory
    {
        get => string.IsNullOrWhiteSpace(_configuredDirectory) ? _defaultDirectory : _configuredDirectory;
        set
        {
            var v = value ?? "";
            if (_configuredDirectory == v) return;
            _configuredDirectory = v;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsNodelistDirectoryCustomized));
            _onConfiguredDirectoryChanged?.Invoke(v);
        }
    }

    /// <summary>true = 用户用了非默认路径(Settings 里写了非空值)。false = 走默认 fallback。</summary>
    public bool IsNodelistDirectoryCustomized => !string.IsNullOrWhiteSpace(_configuredDirectory);

    private string _host = "https://api.github.com";
    public string Host
    {
        get => _host;
        set { _host = value ?? ""; RaisePropertyChanged(); }
    }

    private string _token = "";
    public string Token
    {
        get => _token;
        set { _token = value ?? ""; RaisePropertyChanged(); }
    }

    private string _statusText = "准备就绪";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; RaisePropertyChanged(); }
    }

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i+user 「入库过程中加日志记录,我希望看到进度和日志」:
    /// 入库滚动日志。ConsolePanel Lines 绑定。cap 500 行(跟 LogViewerViewModel 一致),
    /// AppendLog 自动 RemoveAt(0) 滚动裁掉最旧行。
    /// </summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public const int MaxLogLines = 500;

    /// <summary>
    /// 控制入库日志 Border 显隐。入库启动时 = true(自动展开),完成时 = false(自动折叠,
    /// 用户原话"完成时折叠")。用户点 ConsolePanel 关闭按钮会置 false(日志保留,下次启动再展开)。
    /// </summary>
    public bool IsLogPanelVisible
    {
        get => _isLogPanelVisible;
        private set
        {
            if (_isLogPanelVisible == value) return;
            _isLogPanelVisible = value;
            RaisePropertyChanged();
        }
    }
    private bool _isLogPanelVisible;

    private void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    private void ClearLogs() => LogLines.Clear();

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i+user:View Code-behind 点 ConsolePanel ✕ 关闭按钮时调用。
    /// 只设 IsLogPanelVisible=false(日志保留在 LogLines 集合),下次入库启动
    /// 会再次自动展开。Set 保持 private,view 走 method 走单点入口。
    /// </summary>
    public void CloseLogPanel() => IsLogPanelVisible = false;

    // v1.0.0.x (2026-09-15) T43f+user:左 list 顶部 search TextBox 绑定 ——
    // 模糊匹配 author / repo_name(用户原话"在这里只需要搜索就好了")。
    // 输入立即生效(LostFocus 性能差;N=1500 in-memory filter 毫秒级)。
    // 空字符串 = 显示全部。
    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            var v = value ?? "";
            if (_searchText == v) return;
            _searchText = v;
            RaisePropertyChanged();
            ApplyFilter();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsNotBusy));
            DownloadAndIngestCommand.RaiseCanExecuteChanged();
            IngestCommand.RaiseCanExecuteChanged();
            // v1.0.0.x (2026-09-16) T43i.5+user:DownloadToLocalCommand CanExecute 联动 IsNotBusy
            // (防 git clone 并发),这里跟 InstallCommand 同样需要刷新 CanExecute。
            // 注:InstallCommand 同款联动但此处未显式刷新(T43g 既有行为,跟本任务无关不擅自改)。
            DownloadToLocalCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotBusy => !_isBusy;

    // v1.0.0.x (2026-09-16) T43h+user:后台入库标志位 —— 跟 IsBusy 解耦,只用来 disable
    // 「⤴ 增量入库」按钮自己(防重入),不再 disable Install/分页/搜索等其他按钮。
    // 用户原话"采用后台任务 不要影响到前台操作,避免前台程序不能动"——
    // 任何后台入库期间,前台继续可滚动/搜索/选条目/点安装。
    private bool _isIngesting;
    public bool IsIngesting
    {
        get => _isIngesting;
        private set
        {
            _isIngesting = value;
            IngestCommand.RaiseCanExecuteChanged();
        }
    }

    // v1.0.0.x feat/nodelist-directory:增量入库 command
    public RelayCommand IngestCommand { get; }
    public RelayCommand DownloadAndIngestCommand { get; }

    // v1.0.0.x (2026-09-15) T43g+user:分页器 4 个 command + Action Bar 3 个 command。
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand OpenInBrowserCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand InstallCommand { get; }
    // v1.0.0.x (2026-09-16) T43i.5+user「下载到本地是下载到本地的节点 LocalNodes 下面」:
    // 「💾 下载到本地」按钮,跟「📥 一键安装」并存 —— 后者装到 env 的 custom_nodes/,
    // 前者装到 Settings.LocalNodeDirectory 下的 LocalNodes 目录(本地节点,无 env)。
    public RelayCommand DownloadToLocalCommand { get; }
    // v1.0.0.x (2026-09-15) T43h+user:debug panel 「📄 查看完整 GitHub API 响应」按钮
    // — 弹 RawJsonDialog 显示 SelectedDetails[0].RawJson(整段 JSON 给调试 / 验证用)。
    public RelayCommand ShowRawJsonCommand { get; }

    /// <summary>
    /// ctor ——
    /// v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43)+user 「空路径自动 seed」:
    /// <paramref name="defaultDirectory"/> 兜底 <projectRoot>/nodelist;<paramref name="onConfiguredDirectoryChanged"/>
    /// 在用户改路径时把有效值写回 Settings(下次启动记住)。
    /// v1.0.0.x (2026-09-15) T43g+user:加 3 个 nullable service 注入(浏览器打开 / 环境 / 安装),
    /// 全部 nullable 保留向后兼容 — 旧 caller 不传也不报错,只是按钮不可用。
    /// </summary>
    public NodelistViewModel(
        NodelistDownloader downloader,
        NodelistIngestor ingestor,
        NodelistRepository repo,
        string defaultDirectory,
        Action<string>? onConfiguredDirectoryChanged = null,
        // v1.0.0.x (2026-09-15) feat/nodelist-source-config:host/token 从 SQLite 读,
        // Settings 的 NodelistServerUrl/NodelistApiToken 不再直接走(只作 .inf 镜像)。
        // sourceConfig 由 MainViewModel 注入,caller 先设 Host/Token 也可(向后兼容)。
        NodelistSourceConfigRepository? sourceConfig = null,
        // v1.0.0.x (2026-09-15) T43g+user 「顶部 header + action bar」:
        // BrowserLauncher —— 详情「🌐 浏览器打开」按钮走 OpenWithChromeFallback 统一入口。
        IBrowserLauncher? browserLauncher = null,
        // EnvironmentRepository —— 详情「📥 一键安装」按钮弹 dialog 选环境用。
        IEnvironmentRepository? envRepo = null,
        // NodeOperations —— 详情「📥 一键安装」按钮调 InstallAsync。
        NodeOperations? nodeOps = null,
        // v1.0.0.x (2026-09-16) T43i.5+user「下载到本地是下载到本地的节点 LocalNodes 下面」:
        // 「💾 下载到本地」按钮调 NodeOperations.DownloadAsync,目标目录 = _settings.LocalNodeDirectory。
        // Settings 仅在 DownloadToLocalAsync 里读 LocalNodeDirectory,可空(老 wire 不传也能编)。
        Settings? settings = null)
    {
        _downloader = downloader;
        _ingestor = ingestor;
        _repo = repo;
        _defaultDirectory = defaultDirectory;
        _onConfiguredDirectoryChanged = onConfiguredDirectoryChanged;
        _sourceConfig = sourceConfig;
        _browserLauncher = browserLauncher;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _settings = settings;

        // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43) CanExecute 条件:
        // NodelistDirectory getter 永远非空(fallback 到 defaultDirectory),
        // 所以按钮总是可点 — 只在 IsBusy 时禁用。修之前「Settings.NodelistDirectory 默认空 → 按钮 silent disabled」bug。
        DownloadAndIngestCommand = new RelayCommand(
            async _ => await DownloadAndIngestAsync(),
            _ => IsNotBusy);

        // v1.0.0.x (2026-09-16) T43h+user:增量入库 CanExecute 只看 IsIngesting(后台跑期间禁用自己防重入),
        // 不再联动 IsBusy —— IsBusy 现在只控制「⬇ 重新下载」+「📥 一键安装」(下载/安装是前台流程,
        // 需要联动避免并发);前台其他操作(分页/搜索/选条目/复制/浏览器打开)都不受影响。
        IngestCommand = new RelayCommand(
            _ => StartBackgroundIngest(),
            _ => !IsIngesting);

        // v1.0.0.x (2026-09-15) T43g+user:分页器 4 个 button + Action Bar 3 个 button。
        FirstPageCommand = new RelayCommand(_ => GoToPage(1), _ => CanGoPrev);
        PrevPageCommand = new RelayCommand(_ => GoToPage(_currentPage - 1), _ => CanGoPrev);
        NextPageCommand = new RelayCommand(_ => GoToPage(_currentPage + 1), _ => CanGoNext);
        LastPageCommand = new RelayCommand(_ => GoToPage(TotalPages), _ => CanGoNext);

        // Action Bar —— 无选中项时不可用(IsBusy 也禁用 Install 防止并发)。
        OpenInBrowserCommand = new RelayCommand(
            _ => OpenInBrowser(),
            _ => _selectedEntry is not null);
        CopyUrlCommand = new RelayCommand(
            _ => CopyUrl(),
            _ => _selectedEntry is not null);
        InstallCommand = new RelayCommand(
            async _ => await InstallNodeAsync(),
            // v1.0.0.x (2026-09-16) T43h+user:InstallCommand 不再联动 IsBusy —— 后台入库期间
            // 用户可以正常点「📥 一键安装」(NodeOperations 走自己的环境目录,不写 nodelist_entries,
            // 跟后台 ingest 无冲突)。IsBusy 留给 IsNotBusy → false 时(下载中)防止跟「📥 一键安装」并发。
            _ => _selectedEntry is not null && _envRepo is not null && _nodeOps is not null && IsNotBusy);
        // v1.0.0.x (2026-09-16) T43i.5+user「下载到本地是下载到本地的节点 LocalNodes 下面」:
        // 「💾 下载到本地」按钮调 _nodeOps.DownloadAsync,把选中 entry git clone 到
        // _settings.LocalNodeDirectory/<repoName>,完成后 NodeOperations 内部 upsert 一条
        // scanned_nodes(Source="download", EnvId="") 让 LocalNodes 面板立即可见。
        // CanExecute 同 Install:需选中 + 注入存在 + IsNotBusy(防 git clone 并发)。
        DownloadToLocalCommand = new RelayCommand(
            async _ => await DownloadToLocalAsync(),
            _ => _selectedEntry is not null && _nodeOps is not null && _settings is not null && IsNotBusy);
        // v1.0.0.x T43h+user:RawJson 弹窗 command — CanExecute 要求 SelectedDetails[0] 有 raw_json。
        ShowRawJsonCommand = new RelayCommand(
            _ => ShowRawJsonDialog(),
            _ => SelectedDetails.FirstOrDefault()?.RawJson is { Length: > 0 });
    }

    private readonly NodelistSourceConfigRepository? _sourceConfig;

    public sealed class NodelistEntryRow  // 改名 NodelistEntryRow,避免跟 IngestAsync 返回值混淆
    {
        public string Author { get; set; } = "";
        public string RepoName { get; set; } = "";
        public string FirstSeenAt { get; set; } = "";
        public string LastIngestedAt { get; set; } = "";
        public string Source { get; set; } = "";

        // v1.0.0.x (2026-09-15) T43h+user:raw JSON 全量入库字段(18 列)。
        // v1.0.0.x (2026-09-16) T43i.1+user:加 Description(19 列),raw JSON 顶层节点作者自填描述。
        // v1.0.0.x (2026-09-16) T43i.2.2-fix:删 5 个 ≤0.017% 覆盖率 raw JSON 字段
        // (Nickname/LastUpdate/BadgesJson/AptDependency/DependenciesJson) ——
        // 它们在 nodelist_entries 表被删,对应 POCO 属性也清掉。
        // 由 NodelistRepository.GetAllEntries 读 SQLite 填充,UI 在「数据源」Expander 展示。
        public string? Id { get; set; }
        public string? Description { get; set; }   // T43i.1+user
        public string? InstallType { get; set; }
        public string? PipJson { get; set; }
        public string? TagsJson { get; set; }
        public string? PreemptionsJson { get; set; }
        public string? Category { get; set; }
        public string? Reference { get; set; }
        public string? Reference2 { get; set; }
        public string? FilesJson { get; set; }
        public string? JsPath { get; set; }
        public string? NodenamePattern { get; set; }

        // Computed: 反序列化 JSON 字符串数组给 XAML 用(避免 XAML 写 string.Split)。
        // 解析失败/空字符串都返空数组,UI ItemsControl 走 0-count 路径不报错。
        // v1.0.0.x (2026-09-16) T43i.2.2-fix:BadgesList 依赖已删的 BadgesJson,一并移除。
        public IReadOnlyList<string> PipList => DeserializeStringArray(PipJson);
        public IReadOnlyList<string> TagsList => DeserializeStringArray(TagsJson);
        public IReadOnlyList<string> PreemptionsList => DeserializeStringArray(PreemptionsJson);

        private static IReadOnlyList<string> DeserializeStringArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>v1.0.0.x T43f+user:右边详情一行 — 显示单个 version 的全部仓库字段。
    /// 字段对应 <see cref="NodelistRepository.Detail"/>,从 SQLite 读后做显示友好格式化。
    /// XAML 走 ItemsControl(分页=最新 10 个 version,见 GetDetailsByAuthor LIMIT 10)。</summary>
    public sealed class DetailRow
    {
        public string Version { get; set; } = "";
        public string? Description { get; set; }
        public string Stars { get; set; } = "";
        public string Watchers { get; set; } = "";
        public string Forks { get; set; } = "";
        public string? License { get; set; }
        public string? DefaultBranch { get; set; }
        public string? UpdatedAt { get; set; }
        public string? PushedAt { get; set; }
        public string? HtmlUrl { get; set; }
        public string? Language { get; set; }
        public string OpenIssues { get; set; } = "";
        // 逗号分隔 string(来自 RepoMetadata.Topics),XAML 用 ItemsControl + tag pill 渲染。
        public string? Topics { get; set; }
        public string? Host { get; set; }
        // 拆 Topics 给 XAML 用(避免 XAML 写 string.Split)。
        public IReadOnlyList<string> TopicTags { get; set; } = Array.Empty<string>();
        // v1.0.0.x (2026-09-15) T43h+user:fetched_at + raw_json 已有 nodelist_details 列,
        // 现在 DetailRow 暴露给 UI —「数据源」Expander 的「查看完整 JSON」按钮用 RawJson 弹窗。
        public string? FetchedAt { get; set; }
        public string? RawJson { get; set; }

        // v1.0.0.x (2026-09-16) user「云端其实是有这个功能,只是没有写入」+ 选 B 方案:
        // API 返回的 branches/recentReleases/releaseCount/latestRelease 都在 raw_json 里,
        // 不开 schema 新列,直接 RawJson 懒解析给 UI 用。
        // - LatestReleaseTag:recentReleases[0].tag_name(也试 latestRelease.tag_name)
        // - ReleaseCount:repository.releaseCount(API 直给,无 release 时 = 0)
        // - BranchNames:repository.branches[].name
        // - RecentReleases:recentReleases[] 简化为 "{tag_name} ({published_at 前 10 字符})"
        // RawJson 为 null/非 JSON/无 repository → 全部空值(UI 走 0-count 路径)。
        public string? LatestReleaseTag { get; set; }
        public int? ReleaseCount { get; set; }
        public IReadOnlyList<string> BranchNames { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> RecentReleases { get; set; } = Array.Empty<string>();

        /// <summary>v1.0.0.x user「云端其实是有这个功能,只是没有写入」+ 选 B 方案:
        /// 从 raw_json 懒解析 branches/recentReleases/releaseCount/latestRelease 4 字段。
        /// 不写 SQLite,启动时一次 parse,详情面板直接显示。
        /// Null/空 raw_json / 非 JSON / 无 repository 嵌套 → 全部空值(UI 走 0-count 路径)。</summary>
        public void RefreshFromRawJson()
        {
            var (branches, releases, count, latest) = ParseRawJson(RawJson);
            BranchNames = branches;
            RecentReleases = releases;
            ReleaseCount = count;
            LatestReleaseTag = latest;
        }

        private static (IReadOnlyList<string> Branches, IReadOnlyList<string> Releases,
                        int? ReleaseCount, string? LatestTag) ParseRawJson(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return (Array.Empty<string>(), Array.Empty<string>(), null, null);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                // API 返回 { "repository": { branches/recentReleases/releaseCount/latestRelease, ... } }
                // GitHub 原生顶层没有 branches/releases → 顶层格式时只算 releaseCount fallback。
                JsonElement repoEl = root.ValueKind == JsonValueKind.Object &&
                                     root.TryGetProperty("repository", out var r) &&
                                     r.ValueKind == JsonValueKind.Object
                    ? r : root;

                // branches[] → 只取 .name
                var branches = new List<string>();
                if (repoEl.ValueKind == JsonValueKind.Object &&
                    repoEl.TryGetProperty("branches", out var brEl) &&
                    brEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in brEl.EnumerateArray())
                    {
                        if (b.ValueKind == JsonValueKind.Object &&
                            b.TryGetProperty("name", out var nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String)
                        {
                            var n = nameEl.GetString();
                            if (!string.IsNullOrWhiteSpace(n)) branches.Add(n);
                        }
                    }
                }

                // recentReleases[] → "{tag_name} (YYYY-MM-DD)" 简短展示
                var releases = new List<string>();
                if (repoEl.ValueKind == JsonValueKind.Object &&
                    repoEl.TryGetProperty("recentReleases", out var rrEl) &&
                    rrEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rel in rrEl.EnumerateArray())
                    {
                        if (rel.ValueKind != JsonValueKind.Object) continue;
                        string? tag = rel.TryGetProperty("tag_name", out var tn) &&
                                      tn.ValueKind == JsonValueKind.String
                            ? tn.GetString() : null;
                        string? published = rel.TryGetProperty("published_at", out var pa) &&
                                            pa.ValueKind == JsonValueKind.String
                            ? pa.GetString() : null;
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        var dateShort = published is { Length: >= 10 } ? published[..10] : "";
                        releases.Add(string.IsNullOrEmpty(dateShort) ? tag : $"{tag} ({dateShort})");
                    }
                }

                // releaseCount:repository.releaseCount 直接 int(API 已 aggregate 过)
                int? count = null;
                if (repoEl.ValueKind == JsonValueKind.Object &&
                    repoEl.TryGetProperty("releaseCount", out var rcEl) &&
                    rcEl.ValueKind == JsonValueKind.Number &&
                    rcEl.TryGetInt32(out var rcInt))
                {
                    count = rcInt;
                }
                // 当 releaseCount 缺失但 recentReleases 已知长度 → 兜底
                if (count is null && releases.Count > 0) count = releases.Count;

                // latestRelease.tag_name 优先,空则取 recentReleases[0].tag_name
                string? latestTag = null;
                if (repoEl.ValueKind == JsonValueKind.Object &&
                    repoEl.TryGetProperty("latestRelease", out var lrEl) &&
                    lrEl.ValueKind == JsonValueKind.Object &&
                    lrEl.TryGetProperty("tag_name", out var ltn) &&
                    ltn.ValueKind == JsonValueKind.String)
                {
                    latestTag = ltn.GetString();
                }
                if (string.IsNullOrWhiteSpace(latestTag) && releases.Count > 0)
                {
                    var first = releases[0];
                    var spaceIdx = first.IndexOf(' ');
                    latestTag = spaceIdx > 0 ? first[..spaceIdx] : first;
                }

                return (branches, releases, count, latestTag);
            }
            catch
            {
                return (Array.Empty<string>(), Array.Empty<string>(), null, null);
            }
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43)+user 「空路径自动 seed」:
    /// 首次进节点市场时调用 ——
    /// 1. 若 NodelistDirectory 已配:直接 ReloadAsync 显示 entries;
    /// 2. 若空:自动 seed 到 defaultDirectory(下载 + 入库),seed 完成后 NodelistDirectory
    ///    自动 fallback 到 defaultDirectory,setter 走 onConfiguredDirectoryChanged 写回 Settings。
    /// ShowNodelistView 在 _nodelistViewModel 首次构造后调一次。
    /// </summary>
    public async Task EnsureSeededAsync()
    {
        if (IsNodelistDirectoryCustomized)
        {
            // 已配路径 — 直接 reload
            await ReloadAsync();
            return;
        }

        // 空路径 — 自动 seed 到 defaultDirectory
        var seedDir = _defaultDirectory;
        StatusText = $"首次启动:从网络下载默认节点列表 → {seedDir}\\";
        try
        {
            var dl = await _downloader.DownloadDefaultAsync(seedDir);
            StatusText = $"下载完成 ({dl.SizeBytes / 1024} KB) — 立即入库...";

            // 写回 Settings(下次不再 seed)
            NodelistDirectory = seedDir;

            await RunIngestAsync(dl.FilePath, $"seed 完成 — 扫 {0}");
        }
        catch (Exception ex)
        {
            StatusText = $"Seed 失败:{ex.Message} — 请检查网络,或在设置里手填 NodelistDirectory";
        }
    }

    /// <summary>刷新左 list — SQLite → Entries,然后走 ApplyFilter 灌 _allFiltered + CurrentPageItems。</summary>
    public async Task ReloadAsync()
    {
        var entries = _repo.GetAllEntries();
        Entries.Clear();
        // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43):左 list 不再嵌套 details —
        // details 由 SelectedEntry 联动 SelectedDetails 显示。
        foreach (var e in entries)
        {
            Entries.Add(new NodelistEntryRow
            {
                Author = e.Author,
                RepoName = e.RepoName,
                FirstSeenAt = e.FirstSeenAt,
                LastIngestedAt = e.LastIngestedAt ?? "",
                Source = e.Source,
                // v1.0.0.x (2026-09-16) T43i.1+user:把 raw JSON 顶层 description 字段
                // 透传给 row(其它 18 列 T43h 字段透传留待后续 task 集中处理)。
                Description = e.Description,
            });
        }
        ApplyFilter();
        // 默认选中第一个(当前页切片后)
        if (CurrentPageItems.Count > 0 && SelectedEntry is null)
        {
            SelectedEntry = CurrentPageItems[0];
        }
        await Task.CompletedTask;
    }

    /// <summary>v1.0.0.x T43g+user:把 Entries 按 SearchText 过滤后灌入 _allFiltered + 重置到第 1 页。
    /// T43f 用 FilteredEntries(全量+虚拟滚动),T43g 改成 _allFiltered + 切片(传统分页器)。
    /// 客户端过滤(1500 量级毫秒级),用户敲字 → 立即更新 XAML + ResetPage。
    /// 大小写不敏感,空 = 不过滤。</summary>
    private void ApplyFilter()
    {
        var prevSelected = SelectedEntry;
        _allFiltered = new List<NodelistEntryRow>();
        var q = (_searchText ?? "").Trim();
        if (q.Length == 0)
        {
            foreach (var e in Entries) _allFiltered.Add(e);
        }
        else
        {
            foreach (var e in Entries)
            {
                if (e.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    e.RepoName.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    _allFiltered.Add(e);
                }
            }
        }
        // search 改变总数,留住当前页可能越界 → 重置第 1 页 + 重新切 CurrentPageItems。
        _currentPage = 1;
        LoadCurrentPage();
        // 过滤后若原选中项被过滤掉,清空选中(让 XAML 右侧 detail 也清空)。
        if (prevSelected is not null && !_allFiltered.Contains(prevSelected))
        {
            SelectedEntry = null;
        }
        // 通知左 list 标题数字 + 分页栏全部变更。
        RaisePageProperties();
    }

    /// <summary>当前页切片 → CurrentPageItems。清空再灌(50 条以内,无性能问题)。</summary>
    private void LoadCurrentPage()
    {
        CurrentPageItems.Clear();
        var start = (_currentPage - 1) * DefaultPageSize;
        var end = Math.Min(start + DefaultPageSize, _allFiltered.Count);
        for (int i = start; i < end; i++)
        {
            CurrentPageItems.Add(_allFiltered[i]);
        }
    }

    /// <summary>跳转到指定页。page 越界或不变 → no-op。Reload + Raise 由调用方负责。</summary>
    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages || page == _currentPage) return;
        _currentPage = page;
        LoadCurrentPage();
        RaisePageProperties();
    }

    /// <summary>分页器相关 property + command CanExecute 集中刷新(单方法避免遗漏)。
    /// ApplyFilter / GoToPage / ReloadAsync 调一次就够。</summary>
    private void RaisePageProperties()
    {
        RaisePropertyChanged(nameof(EntriesSummaryText));
        RaisePropertyChanged(nameof(PageInfoText));
        RaisePropertyChanged(nameof(TotalPages));
        RaisePropertyChanged(nameof(CanGoPrev));
        RaisePropertyChanged(nameof(CanGoNext));
        FirstPageCommand.RaiseCanExecuteChanged();
        PrevPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>左 list 顶部标题:"仓库/作者 (15 / 1504)"。15=过滤后,1504=总数。</summary>
    public string EntriesSummaryText => $"仓库 / 作者({_allFiltered.Count} / {Entries.Count})";

    /// <summary>右 detail 顶部版本列表标题:"版本(N/10)"。N=当前 detail 行数(0-10)。</summary>
    public string SelectedDetailsSummaryText => $"版本({SelectedDetails.Count} / 10)";

    /// <summary>刷新右 detail — 联动 SelectedEntry。</summary>
    private void RefreshSelectedDetails()
    {
        SelectedDetails.Clear();
        if (_selectedEntry is null) return;
        foreach (var d in _repo.GetDetailsByAuthor(_selectedEntry.Author, _selectedEntry.RepoName))
        {
            SelectedDetails.Add(new DetailRow
            {
                Version = d.Version,
                Description = d.Description,
                Stars = d.Stars?.ToString() ?? "",
                Watchers = d.Watchers?.ToString() ?? "",
                Forks = d.Forks?.ToString() ?? "",
                License = d.License,
                DefaultBranch = d.DefaultBranch,
                UpdatedAt = d.UpdatedAt,
                PushedAt = d.PushedAt,
                HtmlUrl = d.HtmlUrl,
                Language = d.Language,
                OpenIssues = d.OpenIssues?.ToString() ?? "",
                Topics = d.Topics,
                TopicTags = string.IsNullOrWhiteSpace(d.Topics)
                    ? Array.Empty<string>()
                    : d.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries),
                Host = d.Host,
                // v1.0.0.x T43h+user:FetchedAt 给 SelectedEntryMeta 显示;RawJson 给 debug panel
                // 「查看完整 GitHub API 响应」按钮弹 RawJsonDialog。
                FetchedAt = d.FetchedAt,
                RawJson = d.RawJson,
            });
            // v1.0.0.x user「云端其实是有这个功能」+ B 方案:raw_json 懒解析
            // branches/recentReleases/releaseCount/latestRelease 给 UI 用。
            // 不写 SQLite,启动 O(1) parse,DetailRow 直接显示。
            SelectedDetails[^1].RefreshFromRawJson();
        }
        RaisePropertyChanged(nameof(SelectedDetailsSummaryText));
    }

    // v1.0.0.x (2026-09-16) T43h+user:「⤴ 增量入库」后台化 ——
    // 用户原话"采用后台任务 不要影响到前台操作,避免前台程序不能动"。
    // 实现:Task.Run 把 IngestAsync 切到 threadpool,前台继续响应分页/搜索/选条目/安装等。
    // IsIngesting 只控制按钮自己禁用(防重入),不再 disable 其他按钮(交给 IsBusy 管)。
    // 进度通过 Progress<T> 自动 marshal 回 UI 线程更新 StatusText(不阻塞后台 worker)。
    // 整个流程不依赖 await —— RelayCommand.Execute 返回 void 即可,fire-and-forget。
    private void StartBackgroundIngest()
    {
        if (IsIngesting) return;
        var jsonFile = System.IO.Path.Combine(NodelistDirectory, NodelistDownloader.SeedFileName);
        if (!System.IO.File.Exists(jsonFile))
        {
            StatusText = $"文件不存在: {jsonFile} — 请先点『⬇ 重新下载』";
            return;
        }
        IsIngesting = true;
        StatusText = "⏳ 后台入库启动中...";
        // Task.Run 切后台线程 —— 不阻塞 UI 线程。
        // 不等待 — IngestCommand.Execute 是 void,fire-and-forget;
        // 异常由 RunIngestBackgroundAsync 内部 catch + 落 nodelist_debug.log(不外抛)。
        _ = Task.Run(async () => await RunIngestBackgroundAsync(jsonFile, "增量入库"));
    }

    private async Task DownloadAndIngestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Services.NodelistDownloader.DownloadResult? dl = null;
        try
        {
            StatusText = "下载 default custom-node-list.json...";
            dl = await _downloader.DownloadDefaultAsync(NodelistDirectory);
            StatusText = $"下载完成 ({dl.SizeBytes / 1024} KB) → {dl.FilePath}";
            await RunIngestAsync(dl.FilePath, "重新下载 + 入库");
        }
        catch (Exception ex)
        {
            // v1.0.0.x (2026-09-15) T43e 修复完成后保留日志 fallback ——
            // 任何后续 NRE / 异常都先落 nodelist_debug.log,Release stack 不可靠。
            TryWriteNreDebugLog("DownloadAndIngestAsync", ex);
            StatusText = $"失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>共用的入库流程 + progress + ReloadAsync + IsBusy 设置。</summary>
    private async Task RunIngestAsync(string jsonFile, string progressLabel)
    {
        IsBusy = true;
        try
        {
            await RunIngestCoreAsync(jsonFile, progressLabel, isBackground: false);
            await ReloadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43h+user:后台入库 —— 跟 RunIngestAsync 一样的入库流程,
    /// 但只翻 IsIngesting 标志位(给「⤴ 增量入库」按钮用),不动 IsBusy(留给前台流程用)。
    /// 进度通过 Progress<T> 让 Ingestor 自动 marshal 回 UI 线程,后台 worker 不阻塞。
    /// 完成后再 ReloadAsync 一次让用户看到新增 entry。
    /// </summary>
    private async Task RunIngestBackgroundAsync(string jsonFile, string progressLabel)
    {
        IsIngesting = true;
        try
        {
            await RunIngestCoreAsync(jsonFile, progressLabel, isBackground: true);
            await ReloadAsync();
        }
        finally
        {
            IsIngesting = false;
        }
    }

    /// <summary>
    /// 共用的入库核心(下载 + 入库 / 后台入库 都走这个):
    /// 1. Progress&lt;NodelistIngestor.IngestProgress&gt;(VM 在 UI 线程 new,自动 marshal 回 UI)
    /// 2. 拉 host/token(从 SQLite source config 或 fallback 到 Host/Token)
    /// 3. await IngestAsync(后台 worker 不会跨线程 dispatch UI)
    /// </summary>
    private async Task RunIngestCoreAsync(string jsonFile, string progressLabel, bool isBackground)
    {
        try
        {
            StatusText = isBackground
                ? $"⏳ 后台入库({progressLabel}):解析 + 入库中..."
                : $"{progressLabel}:解析 + 入库中...";
            var progress = new Progress<NodelistIngestor.IngestEvent>(evt =>
            {
                switch (evt.Kind)
                {
                    case NodelistIngestor.IngestEventKind.Started:
                        ClearLogs();
                        IsLogPanelVisible = true;
                        AppendLog($"── 入库启动 — 共 {evt.Total} 条 entry ──");
                        StatusText = isBackground
                            ? $"⏳ 后台入库(0/{evt.Total}):开始..."
                            : $"{progressLabel}(0/{evt.Total}):开始...";
                        break;

                    case NodelistIngestor.IngestEventKind.EntryUpserted:
                        AppendLog($"[新] {evt.Author}/{evt.RepoName}  ({evt.Current}/{evt.Total})");
                        StatusText = isBackground
                            ? $"⏳ 后台入库({evt.Current}/{evt.Total}):{evt.Author}/{evt.RepoName}"
                            : $"{progressLabel}({evt.Current}/{evt.Total}):{evt.Author}/{evt.RepoName}";
                        break;

                    case NodelistIngestor.IngestEventKind.EntrySkipped:
                        AppendLog($"[已存在] {evt.Author}/{evt.RepoName}  ({evt.Current}/{evt.Total})");
                        StatusText = isBackground
                            ? $"⏳ 后台入库({evt.Current}/{evt.Total} 跳过):{evt.Author}/{evt.RepoName}"
                            : $"{progressLabel}({evt.Current}/{evt.Total} 跳过):{evt.Author}/{evt.RepoName}";
                        break;

                    case NodelistIngestor.IngestEventKind.EntryFailed:
                        AppendLog($"✗ entry 失败:{evt.Author}/{evt.RepoName} — {evt.Message}");
                        break;

                    case NodelistIngestor.IngestEventKind.DetailFetched:
                        AppendLog($"  详情 ✓ {evt.Author}/{evt.RepoName}");
                        break;

                    case NodelistIngestor.IngestEventKind.DetailFailed:
                        AppendLog($"  详情 ✗ {evt.Author}/{evt.RepoName} — {evt.Message}");
                        break;

                    case NodelistIngestor.IngestEventKind.Completed:
                        var r = evt.Result!;
                        AppendLog($"── 完成 — 扫 {r.EntriesScanned},新增 {r.EntriesNew},跳过 {r.EntriesSkipped}," +
                                  $"详情写入 {r.DetailsWritten}(失败 {r.DetailsFailed}),耗时 {r.Elapsed:hh\\:mm\\:ss} ──");
                        IsLogPanelVisible = false;  // 完成时折叠(用户原话)
                        StatusText = isBackground
                            ? $"✓ 后台入库完成 — 扫 {r.EntriesScanned},新增 {r.EntriesNew},跳过 {r.EntriesSkipped},详情 {r.DetailsWritten}(失败 {r.DetailsFailed})"
                            : $"{progressLabel}完成 — 扫 {r.EntriesScanned},新增 {r.EntriesNew},跳过 {r.EntriesSkipped},详情 {r.DetailsWritten}(失败 {r.DetailsFailed})";
                        break;
                }
            });
            var (host, token) = _sourceConfig is not null
                ? ((Func<(string, string)>)(() =>
                {
                    var cfg = _sourceConfig.Get();
                    return (cfg.ServerUrl, cfg.ApiToken);
                }))()
                : (Host, Token);
            // v1.0.0.x (2026-09-16) T43i+user:Completed event 已在 IngestAsync 内部 Report(result),
            // VM lambda 已写「── 完成 — ...」行 + 设 IsLogPanelVisible=false + 更新 StatusText。
            // 这里不再二次处理 result(避免重复);保留 var 让后续调试可用。
            var result = await _ingestor.IngestAsync(
                jsonFile, host, token, forceFull: false, progress: progress);
            _ = result;
        }
        catch (Exception ex)
        {
            // v1.0.0.x (2026-09-15) T43e 修复完成后保留日志 fallback —
            // 任何后续 NRE / 异常都先落 nodelist_debug.log,Release stack 不可靠。
            // T43i+user:catch 路径也写一条日志到 LogLines,让用户看到「整体失败」。
            TryWriteNreDebugLog("RunIngestAsync", ex);
            AppendLog($"✗ 整体失败:{ex.Message}");
            StatusText = $"失败:{ex.Message}";
            IsLogPanelVisible = false;
        }
    }

    // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43e) debug helper:
    // 把 NRE / 其他异常 stack 落到 <exe>/nodelist_debug.log,append 模式不覆盖历史。
    // 用户复现后可直接 cat / 截屏发给我做诊断。写文件失败静默(只读盘 / 权限不足
    // 等不影响主流程)。
    private static void TryWriteNreDebugLog(string source, Exception ex)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var path = System.IO.Path.Combine(dir, "nodelist_debug.log");
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            // v1.0.0.x T43e: 写更完整 context — Exception type + message + stack + InnerException
            // (Release build 行号不可靠,但 stack 里的方法名能定位是哪个 service / method)。
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{stamp}] [{source}] EXCEPTION TYPE: {ex.GetType().FullName}");
            sb.AppendLine($"[{stamp}] [{source}] MESSAGE: {ex.Message}");
            sb.AppendLine($"[{stamp}] [{source}] STACK:");
            sb.AppendLine(ex.StackTrace ?? "(null)");
            if (ex.InnerException is not null)
            {
                sb.AppendLine($"[{stamp}] [{source}] INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                sb.AppendLine(ex.InnerException.StackTrace ?? "(null)");
            }
            sb.AppendLine("--- end ---");
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // 静默 — 不影响主流程
        }
    }

    // v1.0.0.x (2026-09-15) T43g+user 「详情 Action Bar」(3 个 button) ——
    // 浏览器打开 + 复制链接 + 一键安装。选 Entry 后 detail 头按钮启用,统一拿 SelectedDetails[0].HtmlUrl。

    /// <summary>从 SelectedDetails 取最新 version 的 html_url(无 detail 时返回 null)。</summary>
    private string? GetSelectedRepoUrl()
    {
        var first = SelectedDetails.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.HtmlUrl));
        return first?.HtmlUrl;
    }

    // v1.0.0.x (2026-09-15) T43h+user:debug panel 「📄 查看完整 GitHub API 响应」按钮 —
    // 弹 RawJsonDialog 显示 SelectedDetails[0].RawJson(整段 JSON 给调试 / 验证用)。
    // RawJsonDialog 走标准 ShowDialog() 模式(无 CloseRequested 复杂度,只读 + 关闭按钮)。
    private void ShowRawJsonDialog()
    {
        var firstDetail = SelectedDetails.FirstOrDefault();
        var rawJson = firstDetail?.RawJson;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            StatusText = "无 raw_json(该 entry 未拉过 GitHub API 详情)";
            return;
        }
        var dlg = new Views.RawJsonDialog(rawJson)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dlg.ShowDialog();
    }

    /// <summary>详情「🌐 浏览器打开」—— 走 BrowserLauncher 统一入口(Chrome → Edge → 默认浏览器)。
    /// 项目反馈 [[feedback-browser-chrome-fallback]] 强制统一,严禁直接 Process.Start。</summary>
    private void OpenInBrowser()
    {
        var url = GetSelectedRepoUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "无仓库链接(未拉过详情)";
            return;
        }
        if (_browserLauncher is null)
        {
            // v1.0.0.x T43g:测试 ctor / 早期 caller 没传 BrowserLauncher —— 不静默,告知用户。
            StatusText = "浏览器打开不可用:未配置 BrowserLauncher";
            return;
        }
        _browserLauncher.OpenWithChromeFallback(url,
            (code, msg, sev) => StatusText = $"打开浏览器失败:{msg}");
        StatusText = $"已在浏览器打开:{url}";
    }

    /// <summary>详情「📋 复制链接」—— 走 System.Windows.Clipboard 静态调用(项目 4 处先例)。
    /// 异常(如剪贴板被其他程序锁住)→ StatusText 提示,不抛给 XAML。</summary>
    private void CopyUrl()
    {
        var url = GetSelectedRepoUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "无仓库链接";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(url);
            StatusText = $"已复制:{url}";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败:{ex.Message}";
        }
    }

    /// <summary>详情「📥 一键安装」—— 弹 EnvPickerDialog(复用现有 EnvOption)选环境 → 调
    /// <see cref="NodeOperations.InstallAsync"/>。StatusText 实时报告进度,IsBusy 期间禁用按钮。
    /// 无 SelectedEntry / 缺 _envRepo / 缺 _nodeOps 时静默提示,不抛。</summary>
    private async Task InstallNodeAsync()
    {
        if (_selectedEntry is null)
        {
            StatusText = "未选中 entry";
            return;
        }
        if (_envRepo is null || _nodeOps is null)
        {
            StatusText = "安装未配置:缺少 envRepo / nodeOps 注入";
            return;
        }
        var entry = _selectedEntry;
        var envs = _envRepo.ListAll();
        if (envs.Count == 0)
        {
            StatusText = "没有可用环境,请先创建 ComfyUI 环境";
            return;
        }

        // 弹环境 picker dialog(复用现成 EnvPickerDialog — 单一可测试 seam ShowOverride)。
        var opts = envs.Select(e => new EnvOption(e.Id, e.Name)).ToList();
        var picked = EnvPickerDialog.Show($"安装 {entry.Author}/{entry.RepoName} → 选择环境", opts);
        if (picked is null)
        {
            StatusText = "已取消";
            return;
        }
        var env = envs.First(e => e.Id == picked.Id);
        var repoUrl = $"https://github.com/{entry.Author}/{entry.RepoName}.git";
        // 默认装最新版本;若用户选了某个 version,把它当 targetTag 传给 git checkout。
        // DetailRow.Version 可能是 repoName(未拉详情)或 git tag/sha — 仅当形态像 tag 才传,避免假 commit。
        var rawVersion = SelectedDetails.FirstOrDefault()?.Version;
        string? targetTag = !string.IsNullOrWhiteSpace(rawVersion) && rawVersion != entry.RepoName
            ? rawVersion
            : null;

        IsBusy = true;
        try
        {
            StatusText = $"正在安装 {entry.Author}/{entry.RepoName} → {env.Name}...";
            var progress = new Progress<string>(line => StatusText = line);
            var result = await _nodeOps.InstallAsync(
                envId: env.Id,
                nodeId: entry.RepoName,
                repoUrl: repoUrl,
                targetTag: targetTag,
                progress: progress);
            StatusText = result.Success
                ? $"安装成功:{entry.Author}/{entry.RepoName} → {env.Name} (sha={result.Version ?? "?"})"
                : $"安装失败:{result.Reason}";
        }
        catch (Exception ex)
        {
            StatusText = $"安装异常:{ex.Message}";
            TryWriteNreDebugLog("InstallNodeAsync", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>详情「💾 下载到本地」—— 把选中 entry git clone 到
    /// <c>Settings.LocalNodeDirectory/&lt;repoName&gt;</c>(本地节点,跟 env 的 custom_nodes/ 解耦)。
    /// 调 <see cref="NodeOperations.DownloadAsync"/>,完成后 NodeOperations 内部会 upsert 一条
    /// <c>scanned_nodes</c>(Source="download", EnvId=""),LocalNodes 面板立即可见。
    /// 失败时返回 <c>NodeOperationResult.Fail(reason)</c> 转译到 StatusText。
    /// 无 SelectedEntry / 缺 _nodeOps / 缺 _settings 时静默提示,不抛。
    /// IsBusy 联动同 InstallNodeAsync:防止跟「📥 一键安装」/「⬇ 重新下载」并发(都写磁盘)。</summary>
    private async Task DownloadToLocalAsync()
    {
        if (_selectedEntry is null)
        {
            StatusText = "未选中 entry";
            return;
        }
        if (_nodeOps is null || _settings is null)
        {
            StatusText = "下载未配置:缺少 nodeOps / settings 注入";
            return;
        }
        var entry = _selectedEntry;
        var localDir = _settings.LocalNodeDirectory;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            StatusText = "未配置本地节点目录(Settings.LocalNodeDirectory),请先在 Settings 填写";
            return;
        }
        var repoUrl = $"https://github.com/{entry.Author}/{entry.RepoName}.git";

        IsBusy = true;
        try
        {
            StatusText = $"正在下载 {entry.Author}/{entry.RepoName} → LocalNodes ...";
            var progress = new Progress<string>(line => StatusText = line);
            // 复用 NodeOperations.DownloadAsync(line 283):它会 mkdir + git clone,
            // 然后 upsert ScannedNode(Source="download", EnvId="") — LocalNodeService 立刻可见。
            // targetTag 选 SelectedDetails[0].Version 跟 InstallNodeAsync 同样策略(只传真 tag)。
            var rawVersion = SelectedDetails.FirstOrDefault()?.Version;
            string? targetTag = !string.IsNullOrWhiteSpace(rawVersion) && rawVersion != entry.RepoName
                ? rawVersion
                : null;
            var result = await _nodeOps.DownloadAsync(
                localDir: localDir,
                nodeId: entry.RepoName,
                repoUrl: repoUrl,
                targetTag: targetTag,
                progress: progress);
            StatusText = result.Success
                ? $"下载成功:{entry.Author}/{entry.RepoName} → {localDir}\\{entry.RepoName} (sha={result.Version ?? "?"})"
                : $"下载失败:{result.Reason}";
        }
        catch (Exception ex)
        {
            StatusText = $"下载异常:{ex.Message}";
            TryWriteNreDebugLog("DownloadToLocalAsync", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
