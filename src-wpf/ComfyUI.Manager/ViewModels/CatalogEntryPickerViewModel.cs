using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
/// 5. 选未装条目 → fire CloseWithEntry → caller 接着弹 InstallDialog
/// </summary>
public class CatalogEntryPickerViewModel : ViewModelBase
{
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly string _envId;
    private readonly AppLogger? _logger;

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

    public IReadOnlyList<PickerFilter> FilterOptions { get; } =
        new[] { PickerFilter.All, PickerFilter.NotInstalled, PickerFilter.Installed, PickerFilter.Outdated };

    private CatalogEntryPickerItem? _selected;
    public CatalogEntryPickerItem? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) OkCommand.RaiseCanExecuteChanged(); }
    }

    private bool _busy;
    /// <summary>卸载中 disable 操作(OkCommand + UninstallCommand)。</summary>
    public bool Busy
    {
        get => _busy;
        private set { if (SetField(ref _busy, value)) RaiseCanExecuteChanged(); }
    }

    public RelayCommand OkCommand { get; }             // 安装选中(仅未装)
    public RelayCommand CancelCommand { get; }
    public RelayCommand UninstallCommand { get; }      // 参数:CatalogEntryPickerItem

    /// <summary>用户选了未装条目,关 dialog 触发安装流程。caller 拿 entry + envId。</summary>
    public event Action<CatalogEntry>? CloseWithEntry;
    public event Action? Cancelled;

    public CatalogEntryPickerViewModel(
        CatalogRepository catalogRepo,
        NodeRepository nodeRepo,
        NodeOperations nodeOps,
        string envId,
        AppLogger? logger = null)
    {
        _catalogRepo = catalogRepo;
        _nodeRepo = nodeRepo;
        _nodeOps = nodeOps;
        _envId = envId;
        _logger = logger;
        OkCommand = new RelayCommand(
            _ => { if (Selected is { IsInstalled: false }) CloseWithEntry?.Invoke(Selected.Entry); },
            _ => Selected is { IsInstalled: false } && !Busy);
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
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
            return new CatalogEntryPickerItem
            {
                Entry = e,
                IsInstalled = node is not null,
                InstalledTag = tag,
                InstalledSha = sha8,
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
        finally
        {
            Busy = false;
        }
    }

    private void RaiseCanExecuteChanged()
    {
        OkCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
    }
}