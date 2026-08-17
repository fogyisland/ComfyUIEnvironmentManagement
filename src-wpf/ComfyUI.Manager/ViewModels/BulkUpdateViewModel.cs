using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.18.1 批量更新 inline VM — env+node 双层。
///
/// 三类 target:
/// 1. <see cref="BulkUpdateTargetKind.ComfyUi"/> — env.ComfyuiSource(基础环境,默认全跑)
/// 2. <see cref="BulkUpdateTargetKind.ComfyUiManager"/> — env 下的 ComfyUI-Manager 子目录(默认全跑)
/// 3. <see cref="BulkUpdateTargetKind.Node"/> — custom_nodes/* 各节点(<see cref="AvailableNodes"/>
///    里 Selected=true 的那些,用户可取消勾选)
///
/// 顶层 UI 镜像:
/// - 左列 = env checkbox 列表(<see cref="EnvRows"/>,默认全选)
/// - 中列 = 选中 envs 合并后的节点 checkbox 列表(<see cref="AvailableNodes"/>,默认全选)
/// - 右列 = TabControl 状态(基础环境 / ComfyUI / 自定义节点 三 tab 共用 <see cref="Rows"/>)
///
/// 启动方式:
/// 1. <see cref="OpenBulkUpdateCommand"/> 触发(由 MainViewModel 缓存调用) →
///    <see cref="MainViewModel.OpenBulkUpdate"/> 调
///    <see cref="LoadEnvs(System.Collections.Generic.IEnumerable{EnvRow}, NodeRepository)"/>
///    传 env + nodeRepo;VM 拉每个 env 的 scanned_nodes,过滤掉 ComfyUI-Manager
///    行(ComfyUI-Manager 是 env-level target,不进 node list)。
/// 2. 用户点"开始" → <see cref="Start"/> 组装 jobs(env-level auto + 选中的 node-level)
///    → 调 <see cref="BulkUpdateOrchestrator.StartAsync"/>。
/// </summary>
public class BulkUpdateViewModel : ViewModelBase
{
    private readonly BulkUpdateOrchestrator _orchestrator;
    private readonly NodeRepository _nodeRepo;
    private CancellationTokenSource _runCts = new();

    private string? _errorMessage;
    private bool _isBusy;

    /// <summary>左列:env 选择列表(checkbox 驱动)。</summary>
    public ObservableCollection<EnvRow> EnvRows { get; } = new();

    /// <summary>中列:选中 envs 合并后的节点列表(checkbox 驱动,默认全选)。</summary>
    public ObservableCollection<NodeRow> AvailableNodes { get; } = new();

    /// <summary>右列:进度(共用一份,TabControl 按 TargetKind filter 渲染)。</summary>
    public ObservableCollection<BulkUpdateRow> Rows { get; } = new();

    /// <summary>v0.6.18.1:三个 filter view,对应 TabControl 的 3 个 tab。
    /// 用 <see cref="CollectionViewSource.GetDefaultView"/> 共享 <see cref="Rows"/> 集合,
    /// 加 <see cref="ICollectionView.Filter"/> 回调按 <see cref="BulkUpdateTargetKind"/>
    /// 分类。Rows 添加 / 替换时自动刷新。</summary>
    public ICollectionView BaseEnvRowsView { get; }
    public ICollectionView ComfyUiManagerRowsView { get; }
    public ICollectionView NodeRowsView { get; }

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllEnvCommand { get; }
    public RelayCommand ToggleSelectAllNodesCommand { get; }

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
        ToggleSelectAllNodesCommand = new RelayCommand(_ => ToggleSelectAllNodes());

        _orchestrator.Progress += OnProgress;
        _orchestrator.Completed += OnCompleted;
        _orchestrator.Cancelled += OnCancelled;

        EnvRows.CollectionChanged += OnEnvRowsChanged;

        // 三个 filter view 共享 Rows 集合 —— TabControl 渲染时分门别类。
        BaseEnvRowsView = CollectionViewSource.GetDefaultView(Rows);
        BaseEnvRowsView.Filter = r => ((BulkUpdateRow)r).TargetKind == BulkUpdateTargetKind.ComfyUi;

        ComfyUiManagerRowsView = CollectionViewSource.GetDefaultView(Rows);
        ComfyUiManagerRowsView.Filter = r => ((BulkUpdateRow)r).TargetKind == BulkUpdateTargetKind.ComfyUiManager;

        NodeRowsView = CollectionViewSource.GetDefaultView(Rows);
        NodeRowsView.Filter = r => ((BulkUpdateRow)r).TargetKind == BulkUpdateTargetKind.Node;
    }

    /// <summary>
    /// 加载 env 列表,同时记下 nodeRepo 以便 <see cref="RebuildAvailableNodes"/> 查节点。
    /// 每个 env 默认勾上;现有 <see cref="AvailableNodes"/> 会被重算。
    /// </summary>
    public void LoadEnvs(IEnumerable<EnvRow> envs, NodeRepository nodeRepo)
    {
        // 取消旧 env 上的 PropertyChanged hook(避免泄漏)
        foreach (var e in EnvRows) e.PropertyChanged -= OnEnvRowChanged;

        EnvRows.Clear();
        foreach (var e in envs)
        {
            e.PropertyChanged += OnEnvRowChanged;
            EnvRows.Add(e);
        }

        // VM 在 ctor 已注入 _nodeRepo,这里不再校验 —— 调用方
        // (MainViewModel.OpenBulkUpdate)两次进入会 new 两次 NodeRepository
        // (无状态 wrapper,等价);传入的 param 形参保留为 API 约定:
        // 测试 seam 可以传 mock repo,不需要通过 VM ctor 注入。
        // 既有的 _nodeRepo 永远是 RebuildAvailableNodes 的源,不会动。

        RebuildAvailableNodes();
    }

    private void OnEnvRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 给新加的 env 接 PropertyChanged,旧的已经在 LoadEnvs 里摘了
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
        RebuildAvailableNodes();
        StartCommand.RaiseCanExecuteChanged();
    }

    private void OnEnvRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvRow.Selected))
        {
            RebuildAvailableNodes();
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 重算 <see cref="AvailableNodes"/> —— 选中 envs 的所有 scanned_nodes
    /// 合并去重(同一 node 装在多个 env 里出现多条时只保留一条,Checked 状态取 OR),
    /// 并排除 ComfyUI-Manager(那是 env-level target,不该出现在 node 列表里)。
    /// 默认 Selected=true。
    /// </summary>
    private void RebuildAvailableNodes()
    {
        AvailableNodes.Clear();
        var seen = new Dictionary<string, NodeRow>(StringComparer.Ordinal);

        foreach (var env in EnvRows.Where(e => e.Selected))
        {
            foreach (var n in _nodeRepo.ListByEnv(env.EnvId))
            {
                // ComfyUI-Manager 是 env-level target,node 列表里跳过
                if (string.Equals(n.Package, "comfyui-manager", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Id, "ComfyUI-Manager", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (seen.TryGetValue(n.Id, out var existing))
                {
                    // 同一 node 跨多 env 装了多份,DisplayName 标记 (多 env)
                    if (!existing.DisplayName.Contains("…"))
                    {
                        existing.DisplayName = existing.DisplayName + "…";
                    }
                    continue;
                }
                seen[n.Id] = new NodeRow(n.Id, n.Package ?? n.Id, env.EnvId)
                {
                    Selected = true,
                };
            }
        }

        foreach (var nr in seen.Values)
        {
            nr.PropertyChanged += OnNodeRowChanged;
            AvailableNodes.Add(nr);
        }
        StartCommand.RaiseCanExecuteChanged();
    }

    private void OnNodeRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeRow.Selected))
        {
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    private void ToggleSelectAllEnvs()
    {
        var allSelected = EnvRows.Count > 0 && EnvRows.All(e => e.Selected);
        foreach (var e in EnvRows) e.Selected = !allSelected;
    }

    private void ToggleSelectAllNodes()
    {
        var allSelected = AvailableNodes.Count > 0 && AvailableNodes.All(n => n.Selected);
        foreach (var n in AvailableNodes) n.Selected = !allSelected;
    }

    private bool CanStart() =>
        !IsBusy
        && EnvRows.Any(e => e.Selected);

    // env-level target 永远跑(node-level 可由用户取消勾选)
    private IReadOnlyList<(string EnvId, BulkUpdateTargetKind TargetKind, string? NodeId)>
        BuildJobs()
    {
        var jobs = new List<(string, BulkUpdateTargetKind, string?)>();
        foreach (var env in EnvRows.Where(e => e.Selected))
        {
            jobs.Add((env.EnvId, BulkUpdateTargetKind.ComfyUi, null));
            jobs.Add((env.EnvId, BulkUpdateTargetKind.ComfyUiManager, null));
        }
        foreach (var node in AvailableNodes.Where(n => n.Selected))
        {
            jobs.Add((node.InstalledEnvId, BulkUpdateTargetKind.Node, node.Id));
        }
        return jobs;
    }

    /// <summary>summary 计数 — 绑底部 inline Border,实时跟 orchestrator Completed 更新。</summary>
    public BulkUpdateSummary? Summary { get; private set; }

    private void Start()
    {
        var jobs = BuildJobs();
        if (jobs.Count == 0) return;

        // 预填 Rows —— 每个 job 一条 "pending"。Orchestrator 的 Progress
        // 事件从背景任务发,我们用 EnvId + TargetKind + NodeId 三元组直接
        // 查找更新对应 row,无需每次都遍历查找(虽然 OnProgress 内部遍历,
        // 但 dataset 通常很小)。
        Rows.Clear();
        foreach (var (envId, target, nodeId) in jobs)
        {
            Rows.Add(new BulkUpdateRow(envId, target, "pending", null, 0, 0, nodeId));
        }

        // 旧 CTS 释放 —— 上一轮如果意外没释放,以这里为权威源。
        try { _runCts.Dispose(); } catch { }
        _runCts = new CancellationTokenSource();

        IsBusy = true;
        ErrorMessage = null;
        Summary = null;
        RaisePropertyChanged(nameof(Summary));

        _ = _orchestrator.StartAsync(jobs, _runCts.Token)
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
            // 找到现有的 pending / running 行,直接替换 —— EnvId + TargetKind + NodeId 三元组定位。
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
            // 兜底:没找到就 append(不应该发生 —— 我们 Start 时已预填)
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
        // Orchestrator 的 Completed 事件已经把 Summary 设好。
        // 这里只处理异常 + 最终收尾(IsBusy 在 OnCompleted 里已设 false,这里冗余也无害)。
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
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public EnvRow(string envId, string displayName)
    {
        EnvId = envId;
        DisplayName = displayName;
    }
}

/// <summary>
/// v0.6.18.1:批量更新节点列表一行。
/// - <see cref="Id"/> = ScannedNode.Id(可作 git pull 目录定位)
/// - <see cref="InstalledEnvId"/> = 该节点装在哪个 env 里(同一 node 跨 env
///   时 <see cref="ComfyUI.Manager.ViewModels.BulkUpdateViewModel.RebuildAvailableNodes"/>
///   会合并成一行,InstalledEnvId 留第一个 env)
/// - <see cref="DisplayName"/> = 包名;跨多 env 时 DisplayName 后缀 "…"
/// </summary>
public class NodeRow : ViewModelBase
{
    private bool _selected = true;
    public string Id { get; }
    public string DisplayName { get; set; }
    public string InstalledEnvId { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public NodeRow(string id, string displayName, string installedEnvId)
    {
        Id = id;
        DisplayName = displayName;
        InstalledEnvId = installedEnvId;
    }
}