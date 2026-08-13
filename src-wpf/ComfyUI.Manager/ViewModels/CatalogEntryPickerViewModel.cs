using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public enum PickerFilter { All, NotInstalled, Installed, Outdated }

/// <summary>
/// v0.6.14 picker redesign:env-aware catalog 条目选择 dialog。
///
/// 职责:
/// 1. 从 CatalogRepository 拉 catalog 全表(limit=200)
/// 2. 跟当前 env 的 scanned_nodes 按 Package join,标记 IsInstalled + InstalledTag
/// 3. 提供 query(自由文本搜索 Package/Description/Author)+ 4 个 filter chips
/// 4. 已装条目行内"卸载"按钮(走 NodeOperations.UninstallAsync)
/// 5. v0.6.14 T5:未装条目行内"安装"按钮直接走 NodeOperations.InstallAsync(不再弹
///    第二个 InstallDialog),成功后 BuildItems rebuild,picker 保持打开
/// </summary>
public class CatalogEntryPickerViewModel : ViewModelBase
{
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _envId;
    private readonly AppLogger? _logger;
    private readonly Func<string, Task>? _onInstallSuccess;

    private List<CatalogEntryPickerItem> _allItems = new();
    public ObservableCollection<CatalogEntryPickerItem> Items { get; } = new();

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) ApplyFilter(); }
    }

    private PickerFilter _activeFilter = PickerFilter.All;
    public PickerFilter ActiveFilter
    {
        get => _activeFilter;
        set { if (SetField(ref _activeFilter, value)) ApplyFilter(); }
    }

    public IReadOnlyList<PickerFilterOption> FilterOptions { get; } =
        new[]
        {
            new PickerFilterOption(PickerFilter.All, "All", "全部"),
            new PickerFilterOption(PickerFilter.NotInstalled, "NotInstalled", "未装"),
            new PickerFilterOption(PickerFilter.Installed, "Installed", "已装"),
            new PickerFilterOption(PickerFilter.Outdated, "Outdated", "已过时"),
        };

    private CatalogEntryPickerItem? _selected;
    public CatalogEntryPickerItem? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) RaiseCanExecuteChanged(); }
    }

    private bool _busy;
    /// <summary>卸载中或安装中 disable 操作(InstallCommand + UninstallCommand)。</summary>
    public bool Busy
    {
        get => _busy;
        private set { if (SetField(ref _busy, value)) RaiseCanExecuteChanged(); }
    }

    public RelayCommand InstallCommand { get; }       // v0.6.14 T5:行内安装,参数:CatalogEntryPickerItem
    public RelayCommand CancelCommand { get; }
    public RelayCommand UninstallCommand { get; }     // 参数:CatalogEntryPickerItem

    /// <summary>Picker 关 dialog 时 fire 一次(Cancel / X / Alt+F4 都触发),caller 用来刷新 env-list。</summary>
    public event Action? Closed;

    private bool _closedFired;

    /// <summary>
    /// v0.6.14 T3:让 dialog code-behind 在 X 按钮 / Alt+F4 路径上 fire Closed。
    /// event 外部只能 +=/-,用这个方法中转给 Closing handler 调用。
    /// 幂等:已经 fire 过一次就不重复 fire。
    /// </summary>
    public void RaiseClosed()
    {
        if (_closedFired) return;
        _closedFired = true;
        Closed?.Invoke();
    }

    public CatalogEntryPickerViewModel(
        CatalogRepository catalogRepo,
        NodeRepository nodeRepo,
        NodeOperations nodeOps,
        NodeVersionRepository versionRepo,
        string envId,
        AppLogger? logger = null,
        Func<string, Task>? onInstallSuccess = null)
    {
        _catalogRepo = catalogRepo;
        _nodeRepo = nodeRepo;
        _nodeOps = nodeOps;
        _versionRepo = versionRepo;
        _envId = envId;
        _logger = logger;
        _onInstallSuccess = onInstallSuccess;
        InstallCommand = new RelayCommand(
            async item => await InstallAsync(item as CatalogEntryPickerItem),
            item => item is CatalogEntryPickerItem { IsInstalled: false, IsInstalling: false }
                    && !Busy);
        CancelCommand = new RelayCommand(_ => RaiseClosed());
        UninstallCommand = new RelayCommand(
            async item => await UninstallAsync(item as CatalogEntryPickerItem),
            item => item is CatalogEntryPickerItem { IsInstalled: true } && !Busy);
        BuildItems();
    }

    private void BuildItems()
    {
        // 1. catalog: 空 query 拉全部
        var entries = _catalogRepo.Search("", limit: 200);
        // 2. installed: ListByEnv
        var installedByPackage = _nodeRepo.ListByEnv(_envId)
            .GroupBy(n => n.Package, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _allItems = entries.Select(e =>
        {
            installedByPackage.TryGetValue(e.Package, out var node);
            string? tag = null;
            if (node is not null
                && node.ScanMeta is { Count: > 0 }
                && node.ScanMeta.TryGetValue("installed_tag", out var t))
            {
                tag = t;
            }
            // InstalledSha = node.Version 前 8 字符(老节点没装 tag 时显示用),
            // null/空 Version → null。
            string? sha8 = null;
            if (node is not null && !string.IsNullOrEmpty(node.Version))
            {
                sha8 = node.Version[..Math.Min(8, node.Version.Length)];
            }
            // v0.6.14 T4: 拉版本列表(node_id = CatalogEntry.Id,schema 已经 cascade 删,
            // 老 id 在 node_versions 里查不到 = 空 list 不会抛)。
            var versions = _versionRepo.ListByNode(e.Id);
            // Default SelectedVersion:LatestVersion 优先 → 命中 versions 就用;否则 list 第一条;
            // 都没有 → null(XAML ComboBox collapsed,LastUpdate 仍显示 raw_metadata.LastUpdate)。
            string? selected = null;
            if (!string.IsNullOrEmpty(e.LatestVersion)
                && versions.Any(v => string.Equals(v.Tag, e.LatestVersion, StringComparison.Ordinal)))
            {
                selected = e.LatestVersion;
            }
            else if (versions.Count > 0)
            {
                selected = versions[0].Tag;
            }
            return new CatalogEntryPickerItem
            {
                Entry = e,
                IsInstalled = node is not null,
                InstalledTag = tag,
                InstalledSha = sha8,
                Versions = versions,
                SelectedVersion = selected,
            };
        }).ToList();

        Selected = null;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = (_query ?? "").Trim();
        var filtered = _allItems.Where(item =>
            MatchesQuery(item, q) && MatchesFilter(item)).ToList();
        Items.Clear();
        foreach (var i in filtered) Items.Add(i);
    }

    private static bool MatchesQuery(CatalogEntryPickerItem item, string q)
    {
        if (string.IsNullOrEmpty(q)) return true;
        var lower = q.ToLowerInvariant();
        if (item.Entry.Package?.Contains(lower, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (item.Entry.Description?.Contains(lower, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (item.Entry.Author?.Contains(lower, StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }

    private bool MatchesFilter(CatalogEntryPickerItem item) => ActiveFilter switch
    {
        PickerFilter.NotInstalled => !item.IsInstalled,
        PickerFilter.Installed => item.IsInstalled && !item.IsOutdated,
        PickerFilter.Outdated => item.IsOutdated,
        _ => true,
    };

    /// <summary>
    /// v0.6.14 T5:行内安装 — 直接走 NodeOperations.InstallAsync,不再弹第二个 InstallDialog。
    /// 成功 → BuildItems rebuild,该行变成 Installed(IsInstalling 自动清掉,因 row 被替换)。
    /// 失败 → InstallError 写原因,IsInstalling=false,行状态不变(可重试)。
    /// 装成功后再 fire-and-forget 调 _onInstallSuccess(等同 InstallDialog 的 onInstallSuccess 行为)。
    /// </summary>
    private async Task InstallAsync(CatalogEntryPickerItem? item)
    {
        if (item is null || item.IsInstalled || item.IsInstalling) return;

        var entry = item.Entry;
        var repoUrl = ExtractRepoUrl(entry);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            item.InstallError = "catalog 条目缺 repository url";
            return;
        }

        item.InstallError = null;
        item.IsInstalling = true;
        item.InstallProgress = "准备...";
        Busy = true;
        try
        {
            // 进度回调:marshal 到 UI 线程 + 更新当前 item 的 InstallProgress
            var progress = new Progress<string>(msg => item.InstallProgress = msg);

            var result = await _nodeOps.InstallAsync(
                _envId,
                entry.Package,
                repoUrl,
                targetTag: item.SelectedVersion,
                catalogPipReqs: entry.PipRequirements,
                ct: default);

            if (result.Success)
            {
                _logger?.Info("catalog-picker",
                    $"env='{_envId}' node='{entry.Package}' tag='{item.SelectedVersion}' 装成功");
                // rebuild:item 被换成 IsInstalled=true 的新 row,IsInstalling 自动清掉
                BuildItems();
                // 自动重启回调(等同原 InstallDialog 的 onInstallSuccess)
                if (_onInstallSuccess is not null)
                {
                    var cb = _onInstallSuccess;
                    _ = Task.Run(() => cb(_envId));
                }
            }
            else
            {
                item.InstallError = result.Reason ?? "安装失败";
                item.InstallProgress = null;
                item.IsInstalling = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-picker",
                $"env='{_envId}' node='{entry.Package}' 装异常:{ex.Message}");
            item.InstallError = ex.Message;
            item.InstallProgress = null;
            item.IsInstalling = false;
        }
        finally
        {
            Busy = false;
            RaiseCanExecuteChanged();
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is not null)
        {
            if (entry.RawMetadata.TryGetValue("repository", out var r))
            {
                var rs = ToStringValue(r);
                if (!string.IsNullOrWhiteSpace(rs)) return rs;
            }
            if (entry.RawMetadata.TryGetValue("url", out var u))
            {
                var us = ToStringValue(u);
                if (!string.IsNullOrWhiteSpace(us)) return us;
            }
        }
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }

    /// <summary>
    /// raw_metadata 反序列化后 value 可能是 string 也可能是 JsonElement(走 SQLite
    /// 往返后),统一 ToString 提取字符串值。
    /// </summary>
    private static string? ToStringValue(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        // JsonElement.GetString() 返 null 当 element 不是 string 时
        if (value is System.Text.Json.JsonElement je) return je.GetString();
        return value.ToString();
    }

    private async System.Threading.Tasks.Task UninstallAsync(CatalogEntryPickerItem? item)
    {
        if (item is null || !item.IsInstalled) return;
        Busy = true;
        try
        {
            var result = await _nodeOps.UninstallAsync(_envId, item.Entry.Package);
            if (result.Success)
            {
                _logger?.Info("catalog-picker",
                    $"env='{_envId}' node='{item.Entry.Package}' 卸载成功");
                BuildItems();   // rebuild,清除 Selected
            }
            else
            {
                _logger?.Warn("catalog-picker",
                    $"env='{_envId}' node='{item.Entry.Package}' 卸载失败:{result.Reason}");
            }
        }
        // v0.6.14 R1 fix:async void(走 RelayCommand 的 async lambda)无 catch 会
        // 把异常扔到 WPF dispatcher → ShutdownMode=OnMainWindowClose 直接杀进程
        // (v0.6.9.2 postmortem 同款)。这里 top-level catch 记日志并保持 UI 可用。
        catch (System.Exception ex)
        {
            _logger?.Error("catalog-picker",
                $"env='{_envId}' node='{item.Entry.Package}' 卸载异常:{ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    private void RaiseCanExecuteChanged()
    {
        InstallCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
    }
}
