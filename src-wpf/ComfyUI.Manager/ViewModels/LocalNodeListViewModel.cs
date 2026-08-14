using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:本地节点列表页 VM。Items + 3 commands(Refresh / Install / Delete)+ busy mutex。
/// </summary>
public class LocalNodeListViewModel : ViewModelBase
{
    private readonly LocalNodeService _svc;
    private readonly LocalNodeCopyInstaller _installer;
    private readonly EnvironmentRepository _envRepo;
    private readonly ErrorBannerViewModel _errorBanner;

    public ObservableCollection<LocalNodeListItem> Items { get; } = new();

    /// <summary>test seam:替代真弹 EnvPickerDialog。返 null = 取消。</summary>
    public Func<string, List<EnvOption>, EnvOption?>? EnvPickerOverride { get; set; }

    /// <summary>test seam:替代真弹 ConfirmDialog。返 true = 确认删。</summary>
    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public LocalNodeListViewModel(
        LocalNodeService svc,
        LocalNodeCopyInstaller installer,
        EnvironmentRepository envRepo,
        ErrorBannerViewModel errorBanner)
    {
        _svc = svc;
        _installer = installer;
        _envRepo = envRepo;
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