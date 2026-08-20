# Catalog Entry Picker — Installed Marker + Filter + Uninstall

## Context

The env-list "Install Node" button opens `CatalogEntryPickerDialog` — a basic ListBox of catalog entries with only a search box. Users complain:
- Too simple, UX not friendly
- No filtering
- Already-installed nodes have no marker (user can't tell what they already have)
- No way to uninstall from here

The dialog is launched from `EnvironmentListViewModel.OpenInstallNodePicker(env)` and the env is already in scope. The plan: redesign the picker to be env-aware, show installed status per entry (using existing `ScannedNode` table — no new schema), add filter chips, and add inline uninstall.

`InstallDialog` (the second dialog in the flow) is out of scope.

## Architecture

### Detection source

Use the existing `scanned_nodes` table (`ScannedNode` model). `NodeRepository.ListByEnv(envId)` already returns all per-env installed nodes. The picker joins catalog entries with this list by `package` (stable key — `entry.Id` is `Guid.NewGuid()` per refresh, NOT stable).

### "已过时" (outdated) detection

`ScannedNode.Version` stores HEAD SHA (not a tag). To compare with catalog `LatestVersion` (semver-ish tag from GitHub releases), we need the installed node's resolved tag.

**Solution**: Add `ScanMeta["installed_tag"]` at install/upgrade time (cheap: `git describe --tags --always` after install). If missing (old installs), outdated = false (don't claim outdated without evidence).

### Uninstall

Add `NodeOperations.UninstallAsync(envId, nodeId, ct)`:
1. Verify `ScannedNode` row exists for `(env_id, package)`
2. `Directory.Delete(<CustomNodesPath>/<nodeId>, recursive: true)` — reuse `TryDelete` helper
3. `_nodeRepo.Delete(nodeId)` — new method
4. Return `NodeOperationResult`

## File Changes

| File | Change |
|------|--------|
| `src-wpf/ComfyUI.Manager/Models/ScannedNode.cs` | No change (use existing `ScanMeta` dict) |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | Add `UninstallAsync` + capture `installed_tag` in `InstallAsync` + `UpgradeAsync` + `ScanAsync` via new helper `TryReadInstalledTagAsync` |
| `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs` | Add `Delete(string nodeId)` method |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogEntryPickerViewModel.cs` | Full rewrite: env-aware, filter chips, item wrapper, uninstall command |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogEntryPickerItem.cs` (new) | Wrapper: `Entry` + `IsInstalled` + `InstalledTag` + `InstalledSha` + `IsOutdated` + `BadgeText` + `BadgeKind` |
| `src-wpf/ComfyUI.Manager/Views/CatalogEntryPickerDialog.xaml` | Card layout with filter chips, per-row action button, status badge |
| `src-wpf/ComfyUI.Manager/Views/CatalogEntryPickerDialog.xaml.cs` | `Show(string envId, …)` signature |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | `OpenInstallNodePicker`: pass `env.Id` to picker; refresh env-list after picker closes |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogEntryPickerViewModelTests.cs` (new) | Build items, filter each chip, search intersect, uninstall command |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsUninstallTests.cs` (new) | UninstallAsync happy path, row missing, dir missing, busy retry |
| `tests-wpf/ComfyUI.Manager.Tests/Data/NodeRepositoryDeleteTests.cs` (new) | Delete row, delete missing |

## Design Details

### 1. `CatalogEntryPickerItem` (new wrapper)

```csharp
public class CatalogEntryPickerItem
{
    public CatalogEntry Entry { get; init; } = null!;
    public bool IsInstalled { get; init; }
    public string? InstalledTag { get; init; }     // ScanMeta["installed_tag"]
    public string? InstalledSha { get; init; }     // ScannedNode.Version (first 8 chars)
    public bool IsOutdated { get; init; }         // IsInstalled && installed_tag != entry.LatestVersion && both present
    public string? LatestVersion => Entry.LatestVersion;

    public string BadgeText => !IsInstalled ? "未安装"
        : IsOutdated ? "已过时"
        : "已安装";
    public string BadgeKind => !IsInstalled ? "NotInstalled"
        : IsOutdated ? "Outdated"
        : "Installed";
}
```

DataTemplate converts `BadgeKind` to brush via value converter (or use existing pill style with new color mapping in Theme.xaml).

### 2. `CatalogEntryPickerViewModel` rewrite

```csharp
public class CatalogEntryPickerViewModel : ViewModelBase
{
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly string _envId;
    private readonly AppLogger? _logger;

    public ObservableCollection<CatalogEntryPickerItem> Items { get; } = new();
    public IReadOnlyList<CatalogEntryPickerItem> AllItems { get; private set; } = Array.Empty<CatalogEntryPickerItem>();

    public string Query { get; set; } = "";
    public FilterKind ActiveFilter { get; set; } = FilterKind.All;  // All / NotInstalled / Installed / Outdated

    public RelayCommand OkCommand { get; }       // install selected item (only enabled for NotInstalled)
    public RelayCommand CancelCommand { get; }
    public RelayCommand UninstallCommand { get; } // parameter: CatalogEntryPickerItem

    public event Action<CatalogEntry>? CloseWithEntry;  // picker done, install this entry
    public event Action? Cancelled;
}
```

Behavior:
- On construction: query catalog (`Search("", 200)`) + query ScannedNode (`ListByEnv(envId)`), build items joined by `Package`
- `Query` setter triggers re-filter
- `ActiveFilter` setter triggers re-filter
- `UninstallCommand` parameter = item: calls `_nodeOps.UninstallAsync(envId, item.Entry.Package)`, on success rebuild items
- `OkCommand`: only enabled when `Selected is { IsInstalled: false }` — fires `CloseWithEntry`

### 3. `CatalogEntryPickerDialog.xaml` layout

```
┌─────────────────────────────────────────────────┐
│ 搜索: [_______________]  全部 ◉ 未装 ○ 已装 ○ 已过时 │
├─────────────────────────────────────────────────┤
│ [card1] pkg-name              [已装 v1.2.3] [卸载]│
│         description…                              │
│         ★ 12  linux  img2img                     │
│         latest: v1.3.0                            │
├─────────────────────────────────────────────────┤
│ [card2] pkg-name-2            [未安装]    [安装]│
│         description…                              │
├─────────────────────────────────────────────────┤
│ [card3] pkg-name-3            [已过时]    [升级]│
│         ...                                       │
└─────────────────────────────────────────────────┘
                                       [取消]
```

- Filter chips: 4 `RadioButton` with `CatalogSegmentedRadioButton` style, GroupName="PickerFilter"
- ListBox: existing `CatalogCardItemContainerStyle` + extended `DataTemplate` (reuse most of `CatalogRowCardTemplate`, append badge + button column)
- Per-row action button: MaterialButton for install (NotInstalled) / DangerButton for uninstall (Installed)
- Footer: only Cancel button (action is per-row; selected item in NotInstalled state still has the per-row install button)

Wait — actually two paths:
- User double-clicks a NotInstalled row → close + open InstallDialog (existing flow)
- User double-clicks an Installed row → no-op or show "已安装"
- User clicks "卸载" button on Installed row → uninstall + refresh list
- User clicks "安装" button on NotInstalled row → close + open InstallDialog

So the row action button replaces the "double-click" behavior for that row. Cleaner UX: button replaces double-click.

### 4. `NodeOperations.UninstallAsync`

```csharp
public virtual async Task<NodeOperationResult> UninstallAsync(
    string envId, string nodeId, CancellationToken ct = default)
{
    _logger?.Info("node-uninstall", $"env='{envId}' node='{nodeId}' 开始卸载");
    var env = _envRepo.Get(envId);
    if (env is null) return NodeOperationResult.Fail("env 不存在");

    var node = _nodeRepo.Get(nodeId);
    if (node is null) return NodeOperationResult.Fail("节点未注册");

    var targetDir = !string.IsNullOrWhiteSpace(node.PackagePath)
        ? node.PackagePath
        : Path.Combine(env.CustomNodesPath ?? "", nodeId);

    if (Directory.Exists(targetDir))
    {
        try { TryDelete(targetDir); }
        catch (Exception ex) { return NodeOperationResult.Fail($"删目录失败:{ex.Message}"); }
    }

    _nodeRepo.Delete(nodeId);
    _logger?.Info("node-uninstall", $"env='{envId}' node='{nodeId}' 卸载成功");
    return NodeOperationResult.Ok(node.Version);
}
```

### 5. `NodeRepository.Delete`

```csharp
public void Delete(string nodeId)
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM scanned_nodes WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", nodeId);
    cmd.ExecuteNonQuery();
}
```

### 6. Capture `installed_tag` at install/upgrade time

New helper in `NodeOperations`:
```csharp
private async Task<string?> TryReadInstalledTagAsync(string workdir, CancellationToken ct)
{
    try
    {
        var r = await _git.RunAsync(workdir,
            new[] { "describe", "--tags", "--abbrev=0" },
            TimeSpan.FromSeconds(10), ct);
        if (!r.Ok) return null;
        var tag = r.Stdout.Trim();
        return string.IsNullOrEmpty(tag) ? null : tag;
    }
    catch { return null; }
}
```

In `InstallAsync` after `TryReadHeadShaAsync`: also call `TryReadInstalledTagAsync`, set `node.ScanMeta["installed_tag"] = ...`. Same in `UpgradeAsync` and `ScanAsync`.

## Tests

### `CatalogEntryPickerViewModelTests` (new)
- `Constructor_JoinsCatalogWithInstalledByPackage`
- `Query_EmptyReturnsAll`
- `Query_TextFiltersByPackageOrMetadata`
- `Filter_NotInstalled_HidesInstalled`
- `Filter_Installed_HidesNotInstalled`
- `Filter_Outdated_ShowsOnlyInstalledWithDifferentTag`
- `Filter_AndQuery_Intersect`
- `OkCommand_FiresCloseWithEntry_OnlyForNotInstalled`
- `UninstallCommand_CallsNodeOps_AndRefreshesItems`
- `UninstallCommand_FailedResult_LeavesItemsIntact`

### `NodeOperationsUninstallTests` (new)
- `UninstallAsync_HappyPath_RemovesDirAndRow`
- `UninstallAsync_RowMissing_ReturnsFail`
- `UninstallAsync_DirMissing_StillRemovesRow`
- `UninstallAsync_NonExistentEnv_ReturnsFail`
- `InstallAsync_CapturesInstalledTag_InScanMeta`

### `NodeRepositoryDeleteTests` (new)
- `Delete_ExistingRow_RemovesRow`
- `Delete_NonExistent_NoOp`

## Verification

1. Run `dotnet build` — verify no errors
2. Run `dotnet test tests-wpf/ComfyUI.Manager.Tests` — full suite green (expect ~1015 PASS / 0 FAIL / 1 SKIP)
3. Staging rebuild: `dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"`
4. GUI smoke on desktop:
   - Open app → env-list → click "安装节点" on env X
   - Verify dialog opens with 4 filter chips (全部 selected)
   - Search "control" → list filters
   - Click "已装" chip → only installed nodes shown (none initially if clean env)
   - Install one node via picker → install completes → close → reopen picker
   - Verify that node shows "已装" badge with version + "卸载" button
   - Click "已过时" → verify filter works after catalog refresh updates LatestVersion
   - Click "卸载" → confirm → node gone from list
   - Verify env-list "已安装节点" count decremented

## Out of Scope

- `InstallDialog` (second dialog) — unchanged
- Multi-env install from picker — single-env only (per user direction)
- Filter persistence across dialog opens — fresh state each open
- Search highlighting in results
- Sort by column — not requested