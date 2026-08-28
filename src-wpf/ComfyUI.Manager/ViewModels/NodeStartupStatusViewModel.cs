using System;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x: env 行 ! 按钮 → 弹 dialog 列出当前 env 的所有节点,区分启动期
/// 加载成功与加载失败(后者整行红 + 后缀 [import failed] + 错误原文)。
///
/// 数据源:<see cref="INodeRepository.ListByEnv"/> (sqlite scanned_nodes)。
/// 加载失败判定:<see cref="ScannedNode.HasLoadError"/>(<c>ScanMeta["load_error"]</c>
/// 非空)— <see cref="ComfyUI.Manager.Infrastructure.ProcessLauncher"/> 启动 5s grace 后
/// <see cref="ComfyUI.Manager.Services.NodeStartupErrorDetector"/> 写入。
///
/// env 从未启动过 → ScannedNode 列表可能为空;若 env 启动过但 <c>custom_nodes</c>
/// 为空 → 列表也空。两种情况下 FailedCount=0,UI 弹 dialog 标题副文本提示。
/// </summary>
public class NodeStartupStatusViewModel : ViewModelBase
{
    private readonly INodeRepository _nodeRepo;
    private readonly string _envId;
    private readonly string _envName;

    /// <summary>
    /// 全节点列表 — 单 ListBox 混排,行内根据 <see cref="ScannedNode.HasLoadError"/>
    /// 走 ✓ 绿 / ✗ 红。env 从未启动过 = 空集合。
    /// </summary>
    public ObservableCollection<ScannedNode> Nodes { get; } = new();

    /// <summary>Dialog 标题: "{envName} 的节点启动状态"。</summary>
    public string Title => $"{_envName} 的节点启动状态";

    /// <summary>标题下方副文本:"共 N 个节点,其中 M 个加载失败"。</summary>
    public string Summary
    {
        get
        {
            int total = Nodes.Count;
            int failed = Nodes.Count(n => n.HasLoadError);
            if (total == 0)
            {
                return "未扫描到任何节点(env 未启动或 custom_nodes 为空)";
            }
            return failed == 0
                ? $"共 {total} 个节点,全部加载成功"
                : $"共 {total} 个节点,其中 {failed} 个加载失败";
        }
    }

    /// <summary>总节点数(env 行 ! 按钮角标用)。</summary>
    public int TotalCount => Nodes.Count;

    /// <summary>失败节点数(env 行 ! 按钮角标用)。</summary>
    public int FailedCount => Nodes.Count(n => n.HasLoadError);

    public event Action? CloseRequested;
    public RelayCommand CloseCommand { get; }

    public NodeStartupStatusViewModel(INodeRepository nodeRepo, string envId, string envName)
    {
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));
        _envId = envId ?? throw new ArgumentNullException(nameof(envId));
        _envName = envName ?? "";

        // ctor 一次性 Load — dialog 是只读快照,不做"实时刷新"语义(env 重启后用户重开 dialog 即可)。
        // 单测可以建环境后重 New VM 验证 Load 行为。
        var list = _nodeRepo.ListByEnv(_envId);
        // 失败节点排前面(更醒目),其余按 package 名升序
        var sorted = list
            .OrderByDescending(n => n.HasLoadError)
            .ThenBy(n => n.Package, StringComparer.OrdinalIgnoreCase);
        foreach (var n in sorted) Nodes.Add(n);

        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
    }
}