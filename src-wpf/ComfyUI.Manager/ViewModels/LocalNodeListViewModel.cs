using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:本地节点列表页 VM。Items + 3 commands(Refresh / Install / Delete)+ busy mutex。
/// v0.6.15.6:"复制到 env" 流程加 (1) 已装节点走 info banner (2) 复制成功自动装节点依赖。
/// </summary>
public class LocalNodeListViewModel : ViewModelBase
{
    private readonly LocalNodeService _svc;
    private readonly LocalNodeCopyInstaller _installer;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly ErrorBannerViewModel _errorBanner;

    public ObservableCollection<LocalNodeListItem> Items { get; } = new();

    /// <summary>test seam:替代真弹 EnvPickerDialog。返 null = 取消。</summary>
    public Func<string, List<EnvOption>, EnvOption?>? EnvPickerOverride { get; set; }

    /// <summary>test seam:替代真弹 ConfirmDialog。返 true = 确认删。</summary>
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }

    /// <summary>
    /// v0.6.15.6:装节点依赖的 inline 状态面板。null = 无任务在跑。
    /// XAML 绑这个属性:非 null 且 <c>IsVisible</c> 时显示 Border(StatusText + Logs + �)。
    /// </summary>
    public NodeRequirementsStatusViewModel? NodeRequirementsStatus { get; private set; }

    public LocalNodeListViewModel(
        LocalNodeService svc,
        LocalNodeCopyInstaller installer,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        RequirementsInstaller requirementsInstaller,
        ErrorBannerViewModel errorBanner)
    {
        _svc = svc;
        _installer = installer;
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _requirementsInstaller = requirementsInstaller;
        _errorBanner = errorBanner;
        RefreshCommand = new RelayCommand(_ => RefreshAsync());
        InstallCommand = new RelayCommand(
            async info => await InstallAsync((LocalNodeInfo)info!),
            info => info is LocalNodeInfo);
        DeleteCommand = new RelayCommand(
            async info => await DeleteAsync((LocalNodeInfo)info!),
            info => info is LocalNodeInfo);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var list = await _svc.ListAsync(CancellationToken.None);
            Items.Clear();
            foreach (var info in list)
            {
                Items.Add(new LocalNodeListItem(info));
            }
        }
        catch (Exception ex)
        {
            _errorBanner.Add("local-node-refresh", $"加载本地节点失败:{ex.Message}",
                ErrorSeverity.Warn);
        }
    }

    public async Task InstallAsync(LocalNodeInfo info)
    {
        var envs = _envRepo.ListAll()
            .Select(e => new EnvOption(e.Id, e.Name))
            .ToList();
        if (envs.Count == 0)
        {
            _errorBanner.Add("local-node-install", "没有可用的 env,请先创建一个", ErrorSeverity.Warn);
            return;
        }

        var title = $"将 {info.NodeId} 复制到哪个 env?";
        EnvOption? selected = EnvPickerOverride is not null
            ? EnvPickerOverride(title, envs)
            : Views.EnvPickerDialog.Show(title, envs);
        if (selected is null) return;  // 用户取消

        // brief 原稿包含一段反射拿 _settings.LocalNodeDirectory 的 dead code,已删除 — 简化为走 _svc.GetLocalNodePath helper
        var sourcePath = _svc.GetLocalNodePath(info.NodeId);
        if (string.IsNullOrEmpty(sourcePath) || !System.IO.Directory.Exists(sourcePath))
        {
            _errorBanner.Add("local-node-install", $"本地源目录不存在:{sourcePath}", ErrorSeverity.Warn);
            return;
        }

        var env = _envRepo.Get(selected.Id);
        if (env is null)
        {
            _errorBanner.Add("local-node-install", $"env '{selected.Id}' 已不存在,请刷新", ErrorSeverity.Warn);
            return;
        }
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            _errorBanner.Add("local-node-install", $"env '{env.Name}' 缺 custom_nodes_path", ErrorSeverity.Error);
            return;
        }

        // v0.6.15.6:已装节点不再走红色错误条 — ScannedNode 行 + Directory 双查,
        // 命中 → info banner "节点已存在",避免用户重复点复制弹"复制失败"。
        var targetDir = Path.Combine(env.CustomNodesPath, info.NodeId);
        var alreadyInstalled = _nodeRepo.Get(info.NodeId) is not null
            && _nodeRepo.Get(info.NodeId)!.EnvId == env.Id;
        if (alreadyInstalled || System.IO.Directory.Exists(targetDir))
        {
            _errorBanner.Add("local-node-install",
                $"节点 '{info.NodeId}' 已在 env '{env.Name}' 中,无需重复安装",
                ErrorSeverity.Info);
            return;
        }

        var r = await _installer.InstallAsync(selected.Id, sourcePath, info.NodeId, CancellationToken.None);
        if (!r.Success)
        {
            _errorBanner.Add("local-node-install", $"复制失败:{r.Reason}", ErrorSeverity.Error);
            return;
        }
        // 更新受影响 card 的 badge,不重 fetch 整列表
        var item = Items.FirstOrDefault(i => i.Info.NodeId == info.NodeId);
        if (item is not null)
        {
            var newEnvIds = item.Info.InstalledEnvIds.Append(selected.Id).Distinct().ToList();
            var newEnvNames = newEnvIds
                .Select(eid => _envRepo.Get(eid)?.Name ?? eid)
                .ToList();
            // 替换 Info(immutable record)
            var newInfo = info with { InstalledEnvIds = newEnvIds, InstalledEnvNames = newEnvNames };
            var idx = Items.IndexOf(item);
            Items[idx] = new LocalNodeListItem(newInfo);
        }

        // v0.6.15.6:复制成功 → 自动跑节点自己的 requirements.txt。失败按用户偏好
        // 复制算 OK,只 WARN 日志(installer 内部 _logger.Warn),UI 面板保留可见
        // 让用户看到具体 pip 错误(RequirementsInstaller 已日志,面板反映 StatusText)。
        await RunNodeRequirementsInstallAsync(env, info.NodeId, targetDir);
    }

    /// <summary>
    /// v0.6.15.6:节点复制成功后触发节点自身 requirements.txt 装依赖。
    /// 新建 inline 状态面板 VM,设 <see cref="NodeRequirementsStatus"/> + 通知。
    /// 成功 / 失败都让面板显示 — 成功路径 2s 后自动 Hide,失败路径等用户手动关。
    /// </summary>
    private async Task RunNodeRequirementsInstallAsync(Environment env, string nodeId, string nodeDir)
    {
        var status = new NodeRequirementsStatusViewModel(env, nodeId, nodeDir, _requirementsInstaller);
        NodeRequirementsStatus = status;
        RaisePropertyChanged(nameof(NodeRequirementsStatus));

        // 不 await RunAsync 后续的"成功 → 2s 后 Hide"逻辑,避免阻塞 caller;
        // 跑完后让面板 VM 自己管自己。失败的话面板继续显示,用户可手动关。
        _ = status.RunAsync().ContinueWith(t =>
        {
            // 异常路径(已经在 RunAsync 内 catch + Fail,这里只是兜底)
            if (t.IsFaulted)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NodeReq] unexpected fault: {t.Exception?.GetBaseException().Message}");
            }
        });
    }

    public async Task DeleteAsync(LocalNodeInfo info)
    {
        var ok = ConfirmDialogOverride is not null
            ? ConfirmDialogOverride(
                $"确认删除本地节点 {info.NodeId}?已装到 env 的副本不删。",
                "确认删除", "取消")
            : Views.ConfirmDialog.Show(
                $"确认删除本地节点 {info.NodeId}?已装到 env 的副本不删。");
        if (!ok) return;

        var r = await _svc.DeleteAsync(info.NodeId, CancellationToken.None);
        if (!r.Success)
        {
            _errorBanner.Add("local-node-delete", $"删除失败:{r.Reason}", ErrorSeverity.Error);
            return;
        }
        var item = Items.FirstOrDefault(i => i.Info.NodeId == info.NodeId);
        if (item is not null) Items.Remove(item);
    }
}