using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.18.2 批量更新 inline VM — 扁平 checklist,所有 updateable items(env-level
/// + node-level)合并到一个 <see cref="UpdateItems"/> 列表,用户像勾选普通节点
/// (e.g. One Button Prompt)一样勾选。
///
/// 之前 v0.6.18.1 的 3 类 target 概念保留(env-level 自动 + node-level 按勾),
/// 但 UI 上不再做 env/node 列拆分 —— 左边列 env 列表(选中 env 才贡献 items),
/// 中间列扁平 <see cref="UpdateItems"/>(含 env-level items + node-level items),
/// 右边列单一 DataGrid 显示全部进度(无 tab 拆分)。
///
/// 启动方式:
/// 1. <see cref="OpenBulkUpdateCommand"/> 触发(由 MainViewModel 缓存调用) →
///    <see cref="MainViewModel.OpenBulkUpdate"/> 调
///    <see cref="LoadEnvs(System.Collections.Generic.IEnumerable{EnvRow}, NodeRepository)"/>
///    传 env + nodeRepo。
/// 2. VM 拉每个选中 env 的 scanned_nodes,合并去重,过滤 ComfyUI-Manager,
///    跟 env-level items(每个 env 2 条:基础环境 + ComfyUI-Manager)合并成扁平
///    <see cref="UpdateItems"/>。
/// 3. 用户点"开始" → <see cref="Start"/> 调 <see cref="BuildJobs"/>
///    收集选中 env 的 env-level jobs + 勾选 node-level jobs → orchestrator。
/// </summary>
public class BulkUpdateViewModel : ViewModelBase
{
    private readonly BulkUpdateOrchestrator _orchestrator;
    private readonly NodeRepository _nodeRepo;
    private CancellationTokenSource _runCts = new();

    private string? _errorMessage;
    private bool _isBusy;
    // v0.6.18.4:用户主动点 ✕ 关闭 Console 时置 true,Start() 时复位 false。
    // IsConsoleVisible = !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0)
    private bool _userHiddenConsole;

    /// <summary>左列:env 选择列表(checkbox 驱动)。</summary>
    public ObservableCollection<EnvRow> EnvRows { get; } = new();

    /// <summary>
    /// v0.6.18.2 中列:扁平 checklist — env-level items(每个 env 2 条:
    /// 基础环境 + ComfyUI-Manager) + node-level items(每个 installed node 1 条),
    /// 全部 checkbox 驱动,默认 Selected=true。
    /// </summary>
    public ObservableCollection<UpdateItem> UpdateItems { get; } = new();

    /// <summary>右列:全部进度(扁平,无 TabControl filter)。</summary>
    public ObservableCollection<BulkUpdateRow> Rows { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllEnvCommand { get; }
    public RelayCommand ToggleSelectAllItemsCommand { get; }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            RaisePropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            // v0.6.18.4:IsConsoleVisible 依赖 IsBusy(IsBusy=true → 必可见)。
            RaisePropertyChanged(nameof(IsConsoleVisible));
            StartCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public BulkUpdateViewModel(BulkUpdateOrchestrator orchestrator, NodeRepository nodeRepo)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart());
        CancelCommand = new RelayCommand(
            _ => Cancel(),
            _ => IsBusy);
        ToggleSelectAllEnvCommand = new RelayCommand(_ => ToggleSelectAllEnvs());
        ToggleSelectAllItemsCommand = new RelayCommand(_ => ToggleSelectAllItems());

        _orchestrator.Progress += OnProgress;
        _orchestrator.Completed += OnCompleted;
        _orchestrator.Cancelled += OnCancelled;

        EnvRows.CollectionChanged += OnEnvRowsChanged;
        // v0.6.18.4:Console 行追加 / 清空时通知 IsConsoleVisible。
        ConsoleLog.CollectionChanged += OnConsoleLogChanged;
    }

    /// <summary>
    /// 加载 env 列表。nodeRepo 用于查每个 env 的 scanned_nodes;VM 不持有 param 引用,
    /// 既有的 _nodeRepo 永远是查询源。
    /// </summary>
    public void LoadEnvs(IEnumerable<EnvRow> envs, NodeRepository nodeRepo)
    {
        foreach (var e in EnvRows) e.PropertyChanged -= OnEnvRowChanged;

        EnvRows.Clear();
        foreach (var e in envs)
        {
            e.PropertyChanged += OnEnvRowChanged;
            EnvRows.Add(e);
        }

        RebuildUpdateItems();
    }

    private void OnEnvRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (EnvRow row in e.NewItems)
            {
                row.PropertyChanged += OnEnvRowChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (EnvRow row in e.OldItems)
            {
                row.PropertyChanged -= OnEnvRowChanged;
            }
        }
        RebuildUpdateItems();
        StartCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(HasRunningSelectedEnv));
    }

    private void OnEnvRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvRow.Selected))
        {
            RebuildUpdateItems();
            StartCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(HasRunningSelectedEnv));
        }
    }

    /// <summary>
    /// v0.6.18.2 重算 <see cref="UpdateItems"/>:每个选中 env 贡献 2 条 env-level
    /// (基础环境 + ComfyUI-Manager),加上该 env 的所有 installed node(去重 +
    /// 过滤 ComfyUI-Manager)。未选中 env 不贡献任何 item。
    /// </summary>
    private void RebuildUpdateItems()
    {
        foreach (var item in UpdateItems) item.PropertyChanged -= OnUpdateItemChanged;

        UpdateItems.Clear();
        var seenNodes = new Dictionary<string, UpdateItem>(StringComparer.Ordinal);

        foreach (var env in EnvRows.Where(e => e.Selected))
        {
            // env-level items:每个 env 两条,默认勾上
            UpdateItems.Add(new UpdateItem(
                envId: env.EnvId,
                displayName: $"{env.DisplayName} · 基础环境",
                target: BulkUpdateTargetKind.ComfyUi,
                nodeId: null,
                installedEnvId: env.EnvId));

            UpdateItems.Add(new UpdateItem(
                envId: env.EnvId,
                displayName: $"{env.DisplayName} · ComfyUI-Manager",
                target: BulkUpdateTargetKind.ComfyUiManager,
                nodeId: null,
                installedEnvId: env.EnvId));

            // node-level items:每个 installed node 一条,跨 env 合并去重
            foreach (var n in _nodeRepo.ListByEnv(env.EnvId))
            {
                // ComfyUI-Manager 是 env-level target,node 列表里跳过
                if (string.Equals(n.Package, "comfyui-manager", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Id, "ComfyUI-Manager", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (seenNodes.TryGetValue(n.Id, out var existing))
                {
                    if (!existing.DisplayName.Contains("…"))
                    {
                        existing.DisplayName = existing.DisplayName + "…";
                    }
                    continue;
                }
                seenNodes[n.Id] = new UpdateItem(
                    envId: env.EnvId,
                    displayName: n.Package ?? n.Id,
                    target: BulkUpdateTargetKind.Node,
                    nodeId: n.Id,
                    installedEnvId: env.EnvId);
            }
        }

        foreach (var nodeItem in seenNodes.Values) UpdateItems.Add(nodeItem);
        foreach (var item in UpdateItems) item.PropertyChanged += OnUpdateItemChanged;
        StartCommand.RaiseCanExecuteChanged();
    }

    private void OnUpdateItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateItem.Selected))
        {
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    private void ToggleSelectAllEnvs()
    {
        var allSelected = EnvRows.Count > 0 && EnvRows.All(e => e.Selected);
        foreach (var e in EnvRows) e.Selected = !allSelected;
    }

    private void ToggleSelectAllItems()
    {
        var allSelected = UpdateItems.Count > 0 && UpdateItems.All(n => n.Selected);
        foreach (var n in UpdateItems) n.Selected = !allSelected;
    }

    private bool CanStart() =>
        !IsBusy
        && EnvRows.Any(e => e.Selected)
        && UpdateItems.Any(i => i.Selected);

    /// <summary>
    /// 从勾选的 <see cref="UpdateItems"/> 生成 jobs。env-level items 跟
    /// node-level items 现在统一在 <see cref="UpdateItems"/> 里,直接映射即可。
    /// </summary>
    private IReadOnlyList<(string EnvId, BulkUpdateTargetKind TargetKind, string? NodeId)>
        BuildJobs()
    {
        var jobs = new List<(string, BulkUpdateTargetKind, string?)>();
        foreach (var item in UpdateItems.Where(i => i.Selected))
        {
            jobs.Add((item.EnvId, item.Target, item.NodeId));
        }
        return jobs;
    }

    /// <summary>summary 计数 — 绑底部 inline Border,实时跟 orchestrator Completed 更新。</summary>
    public BulkUpdateSummary? Summary { get; private set; }

    /// <summary>
    /// v0.6.18.2 G11+:True = 至少有一个 *被选中* 且 *正在运行* 的 env。绑顶部警告 banner
    /// "更新前请先关闭环境,否则 git 操作可能失败或留下脏状态"。env 选中 / 取消勾时
    /// PropertyChanged 触发重算。
    /// </summary>
    [JsonIgnore]
    public bool HasRunningSelectedEnv =>
        EnvRows.Any(e => e.Selected && string.Equals(e.Status, "running", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// v0.6.18.4:Console 行流(每个 job 的 git pull stdout/stderr,带 [envId · itemName]
    /// 前缀)。绑 View 底部 Console 面板 ScrollViewer,镜像 EnvStartStatusViewModel
    /// LogLines 模式。Start() 时 Clear;orchestrator 进度通过 <see cref="IProgress{T}"/>
    /// 异步追加,Progress<string> 在构造时捕获 UI SynchronizationContext(同
    /// <see cref="EnvStartStatusViewModel"/> 修过的 STA-死锁 fix 模式)。
    /// </summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();

    /// <summary>
    /// v0.6.18.4:Console 面板可见性 — busy 时自动可见,run 结束后保留日志直到用户点 ✕。
    /// 绑 View Border Visibility。用户点 ✕ 后(<see cref="_userHiddenConsole"/>=true)
    /// 即使 IsBusy=true 也不显示(用户明确意图优先);<see cref="Start"/> 时复位。
    /// </summary>
    [JsonIgnore]
    public bool IsConsoleVisible =>
        !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Console 面板可见性跟 log 数联动(IsBusy=false 时也保留,直到用户点 ✕)。
        // 加行 / 清空都触发 PropertyChanged 让 View 重新绑定 Visibility。
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems is { Count: > 0 })
        {
            RaisePropertyChanged(nameof(IsConsoleVisible));
        }
    }

    /// <summary>
    /// v0.6.18.4:点 Console 面板 ✕ 关闭按钮时调用,清空日志并隐藏面板。
    /// 即使 IsBusy=true 也会隐藏(用户明确意图);下次 Start() 时复位
    /// <see cref="_userHiddenConsole"/> 重新显示。
    /// </summary>
    public void ClearConsoleLog()
    {
        ConsoleLog.Clear();
        _userHiddenConsole = true;
        RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    private void Start()
    {
        var jobs = BuildJobs();
        if (jobs.Count == 0) return;

        // 预填 Rows —— 每个 job 一条 "pending"。Orchestrator 的 Progress 事件
        // 从背景任务发,我们用 EnvId + TargetKind + NodeId 三元组直接查找更新。
        Rows.Clear();
        foreach (var (envId, target, nodeId) in jobs)
        {
            Rows.Add(new BulkUpdateRow(envId, target, "pending", null, 0, 0, nodeId));
        }

        // v0.6.18.4:清空 Console —— 每次新 run 重新累计;复位用户上次的 ✕ 隐藏。
        ConsoleLog.Clear();
        _userHiddenConsole = false;

        try { _runCts.Dispose(); } catch { }
        _runCts = new CancellationTokenSource();

        IsBusy = true;
        ErrorMessage = null;
        Summary = null;
        RaisePropertyChanged(nameof(Summary));

        // v0.6.18.4:IProgress<string> 走 Progress<T> 构造(捕获当前 SynchronizationContext,
        // 自动 marshal 回 UI 线程)。同 EnvStartStatusViewModel 修过的 STA 死锁 fix
        // 模式 — 不要在 ViewModel 显式 Dispatcher.Invoke。
        var consoleProgress = new Progress<string>(line =>
        {
            ConsoleLog.Add(line);
            // IsConsoleVisible 只在 log 数 0→>0 时变 true(IsBusy=true 时已 true);
            // ObservableCollection.Add 不自动 raise PropertyChanged,手动通知。
            if (ConsoleLog.Count == 1)
            {
                RaisePropertyChanged(nameof(IsConsoleVisible));
            }
        });

        _ = _orchestrator.StartAsync(jobs, _runCts.Token, consoleProgress)
            .ContinueWith(t => DispatcherHelper.RunOnUiAsync(() => OnRunFinished(t)));
    }

    private void Cancel()
    {
        if (!IsBusy) return;
        CancelRun();
    }

    /// <summary>
    /// 由 View (dialog Closing / tab 切换 / app 退出) 调用,确保 user 主动离开时 run 也会被取消,
    /// 而不是默默在后台跑完。MainViewModel 切走 section 时若 <c>IsBusy</c>,通过这里取消。
    /// </summary>
    public void CancelRun()
    {
        if (!IsBusy) return;
        _orchestrator.CancelAsync();
        try { _runCts.Cancel(); } catch { }
    }

    // -------- Orchestrator event handlers (called from background task) --------

    private void OnProgress(BulkUpdateRow row)
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var existing = Rows[i];
                if (existing.EnvId == row.EnvId
                    && existing.TargetKind == row.TargetKind
                    && existing.NodeId == row.NodeId
                    && existing.Status is "pending" or "running")
                {
                    Rows[i] = row;
                    return;
                }
            }
            Rows.Add(row);
        });
    }

    private void OnCompleted(BulkUpdateSummary summary)
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            Summary = summary;
            RaisePropertyChanged(nameof(Summary));
            IsBusy = false;
        });
    }

    private void OnCancelled()
    {
        DispatcherHelper.RunOnUiAsync(() =>
        {
            ErrorMessage = "已取消";
        });
    }

    private void OnRunFinished(Task<BulkUpdateSummary> task)
    {
        if (task.IsFaulted)
        {
            var msg = task.Exception?.GetBaseException().Message
                ?? "未知错误";
            ErrorMessage = $"运行失败:{msg}";
            IsBusy = false;
        }
    }
}

public class EnvRow : ViewModelBase
{
    private bool _selected = true;
    public string EnvId { get; }
    public string DisplayName { get; }
    /// <summary>v0.6.18.2 G11+:running / stopped / failed,镜像 Environment.Status,用于左列卡片显示状态点 + 顶部"先关闭环境再更新"警告 banner。</summary>
    public string Status { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public EnvRow(string envId, string displayName, string status = "stopped")
    {
        EnvId = envId;
        DisplayName = displayName;
        Status = status;
    }
}

/// <summary>
/// v0.6.18.2:批量更新扁平 checklist 一行 —— env-level(基础环境 / ComfyUI-Manager)
/// 跟 node-level(节点)统一表达。
///
/// - <see cref="EnvId"/> = 该 item 所属 env
/// - <see cref="DisplayName"/> = 显示文本(env-level 加 " · 基础环境" 后缀,
///   跨 env 同 node 时加 "…" 后缀)
/// - <see cref="Target"/> = <see cref="BulkUpdateTargetKind"/>,跟 orchestrator jobs 对应
/// - <see cref="NodeId"/> = 仅 Node target 时填,其它 target 为 null
/// - <see cref="InstalledEnvId"/> = 节点装在哪个 env(env-level 时 = EnvId)
/// </summary>
public class UpdateItem : ViewModelBase
{
    private bool _selected = true;
    public string EnvId { get; }
    public string DisplayName { get; set; }
    public BulkUpdateTargetKind Target { get; }
    public string? NodeId { get; }
    public string InstalledEnvId { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public UpdateItem(
        string envId,
        string displayName,
        BulkUpdateTargetKind target,
        string? nodeId,
        string installedEnvId)
    {
        EnvId = envId;
        DisplayName = displayName;
        Target = target;
        NodeId = nodeId;
        InstalledEnvId = installedEnvId;
    }
}