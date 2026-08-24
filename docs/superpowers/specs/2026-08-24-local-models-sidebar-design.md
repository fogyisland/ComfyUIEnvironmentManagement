# v1.0.0 本地模型 (Local Models) Sidebar View — Design Spec

> **Status:** approved (chat 2026-08-24)
> **Path:** architectural (new subsystem)
> **Author:** Claude + user collaboration

## 1. Goal

Add a new sidebar entry "本地模型" (Local Models) that lists all models already downloaded to `Settings.DefaultModelsDirectory`, grouped by ModelKind (Checkpoint / Lora / VAE / ControlNet / ...). View-only — no per-card actions.

## 2. Motivation

Today, the user has no way to see what's installed locally without manually browsing `Models/`. With the model marketplace (sidebar Models entry) coming online in future, users need a counterpart view of what they already own.

Existing service: `ModelFilesystemScanner.Scan(modelsDir)` already walks `<ModelsDir>/<kind>/<model-slug-id>/<version-slug-id>/meta.json` and returns per-version `DownloadedModel` records. The VM just needs to group versions into models and present them by kind.

## 3. Non-goals (YAGNI for v1.0.0)

- Per-card actions (open folder, delete, show installed envs, download more)
- File-size aggregation (show version count + latest date instead)
- Per-version detail expand
- Bulk operations
- Sort controls (default: most-recently-downloaded first)
- Persisting the active kind filter across restarts

## 4. UX

### 4.1 Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ [全部] [Checkpoint 12] [Lora 5] [VAE 3] [ControlNet 1] [Embed 1]  │  ← kind filter chips
├──────────────────────────────────────────────────────────────────┤
│ ┌────────────┐ ┌────────────┐                                    │
│ │ Checkpoint │ │ Lora       │  ← kind badge (color per kind)     │
│ │ ModelName1 │ │ ModelName2 │  ← title (max 2 lines)            │
│ │            │ │            │                                     │
│ │ 📦 3 ver   │ │ 📦 1 ver   │  ← version count                   │
│ │ CivitAI    │ │ HuggingFace│  ← source                          │
│ │ 📅 08-20   │ │ 📅 08-18   │  ← latest download                 │
│ └────────────┘ └────────────┘                                    │
└──────────────────────────────────────────────────────────────────┘
```

- **Filter strip** at top: 1 "全部" chip + 1 chip per kind found in scan. Single-select radio behavior. Default = "全部".
- **Grid**: 2-column landscape cards (same width as v0.6.22++ model marketplace landscape layout — 540×220 each).
- **Card**: kind badge top-left (color per ModelKind), title, version count + source + latest date.
- **Empty state** (when DefaultModelsDirectory unset/missing/empty): single grey card "未配置 Models目录 — 请在设置中配置" with link/button to Settings.

### 4.2 Sidebar position

Insert "本地模型" between "工作流库" (Workflows) and "模板管理" (Templates). Rationale: groups "local files" features together (LocalNodes, 本地模型) before "templates" (kind of meta — describes how to build envs).

### 4.3 Sidebar enable

Add `LocalModels=1` to `config/sidebar.inf` (default = enabled). Symmetric with `LocalNodes=0` (intentionally disabled — local node feature not yet polished; we keep 本地模型 enabled because the data path is read-only and well-tested).

## 5. Architecture

### 5.1 New components

| Component | File | Responsibility |
|-----------|------|----------------|
| `LocalModelsViewModel` | `ViewModels/LocalModelsViewModel.cs` | Load from scanner, group by SourceId, expose `Models` + `KindChips` + `ActiveKind` + `ReloadAsync` |
| `LocalModelsView` | `Views/LocalModelsView.xaml` + `.xaml.cs` | Top filter strip + 2-col card grid + empty state |
| `LocalModelCard` (inner DataTemplate) | inline in `LocalModelsView.xaml` | Single model card (kind badge + title + meta rows) |

### 5.2 Reused components

| Component | Reused as |
|-----------|-----------|
| `ModelFilesystemScanner.Scan(settings.DefaultModelsDirectory)` | Raw data source |
| `DownloadedModel` (record) | Per-version record, grouped in VM |
| `ModelKind` enum | Kind taxonomy + per-kind colors |
| `MainViewModel.ShowLocalModels()` (new, mirrors `ShowLocalNodes` lazy pattern) | Sidebar wiring |
| `App.xaml.cs ApplySidebarInf` | Apply `LocalModels=1` from sidebar.inf |

### 5.3 Data flow

1. User clicks sidebar "本地模型" → `MainWindow` `RadioButton.Checked` → `MainViewModel.ShowLocalModels()` (lazy-construct pattern)
2. First construct: instantiate VM with `settings` + `scanner` + `logger`
3. VM ctor calls `LoadAsync()` → `scanner.Scan(settings.DefaultModelsDirectory)` → `IReadOnlyList<DownloadedModel>`
4. VM groups by `SourceId` (same model across multiple versions = 1 card), builds `KindChips` from union of kinds in scan
5. ActiveKind = "全部" by default → filter = full Models collection
6. User clicks kind chip → setter on `ActiveKind` → triggers filter recompute + UI rebind
7. User clicks "🔄 刷新" → `ReloadAsync()` → re-scan + reset filter to "全部"

### 5.4 Grouping key

`DownloadedModel.SourceId` is the canonical model id (CivitAI numeric id or HF repo id). Multiple versions of the same model share SourceId. **Caveat**: if a model was downloaded from different sources (e.g., one version from CivitAI, another from HF), they have different SourceIds and would appear as separate cards. Acceptable for v1 — different sources typically mean different models. Future fix: also group by `Title` if needed.

### 5.5 Card model (VM-level)

```csharp
public sealed record LocalModelCard(
    string Title,            // group.Key.First().Title (first version)
    ModelKind Kind,          // group.Key.First().Kind (assume all versions of one model share kind)
    ModelSourceKind Source,  // first version's source
    int VersionCount,
    DateTimeOffset? LatestDownloadedAt,
    string? SourceUrl        // for tooltip / future detail)
);

public sealed record KindChip(ModelKind Kind, int Count);
```

`ActiveKind` is `ModelKind?` — null = "全部".

## 6. Sidebar wiring (mirrors existing pattern)

### 6.1 `enum MainSection` (in `MainViewModel.cs:22-37`)

Add `LocalModels` between `Workflows` and `Templates` (semantic placement: local files before templates).

### 6.2 `MainWindow.xaml` (sidebar RadioButton)

Insert `<RadioButton x:Name="LocalModelsButton" ... />` between `WorkflowsButton` and `TemplatesButton`. Style: same `SidebarRadioButtonStyle` as others.

### 6.3 `MainViewModel.ShowLocalModels()`

Lazy constructor following `ShowLocalNodes()` pattern:
- If `_localModelsViewModel == null`: instantiate `LocalModelsViewModel(settings, scanner, logger)`, create `LocalModelsView` with that VM
- Set `CurrentSection = MainSection.LocalModels`, `CurrentView = _localModelsView`

### 6.4 `App.xaml.cs ApplySidebarInf`

Add one `ApplyButton(main, "LocalModelsButton", MainSection.LocalModels)` call.

### 6.5 `config/sidebar.inf`

Add line `LocalModels=1` (after `LocalNodes=0`).

## 7. Error handling

- `scanner.Scan("")` returns empty list (per existing implementation `if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir)) return results;`)
- VM shows "未配置 Models目录" empty state when `Models` is empty AND `DefaultModelsDirectory` is empty/whitespace
- VM shows "暂无已下载模型" when Models is empty AND directory exists but no models found
- `scanner.Scan()` per-version parse failures already logged via `_logger?.Warn` — VM does not surface them (avoid alert fatigue)

## 8. Testing

### 8.1 Unit tests (`tests-wpf/.../ViewModels/LocalModelsViewModelTests.cs`)

- `Constructor_EmptySettings_NoModels`
- `LoadAsync_ThreeModels_BuildsThreeCards`
- `LoadAsync_TwoVersionsSameSourceId_GroupsIntoOneCard`
- `LoadAsync_MixedKinds_BuildsKindChipsWithCounts`
- `LoadAsync_FilterByKind_ReturnsOnlyMatchingCards`
- `LoadAsync_FilterByKind_RebindsOnActiveKindChange`
- `LoadAsync_DefaultKindFilter_IsAll`
- `LoadAsync_ReloadAsync_RerunsScanAndResetsFilter`

### 8.2 Pattern for test isolation

Use a fake `ModelFilesystemScanner`-style dependency. Currently `ModelFilesystemScanner` is a concrete class (no interface). Two options:
- (A) Subclass + override `Scan` method (it has no `virtual` currently — would need adding `virtual`)
- (B) Add `IModelFilesystemScanner` interface

**Decision**: add `virtual` to `Scan()` (minimal, single keyword change). Test stubs subclass + override.

### 8.3 XAML load test

Add 1 smoke test that constructs `LocalModelsView` with a stub VM and asserts no XAML exceptions (same pattern as existing view smoke tests).

## 9. Files

**Create:**
- `src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs`
- `src-wpf/ComfyUI.Manager/Views/LocalModelsView.xaml`
- `src-wpf/ComfyUI.Manager/Views/LocalModelsView.xaml.cs`
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelsViewModelTests.cs`

**Modify:**
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (enum + ShowLocalModels + fields)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` (new RadioButton)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` (ApplySidebarInf call)
- `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (add `virtual` to Scan)
- `config/sidebar.inf` (LocalModels=1)

## 10. Out of scope (explicit)

- Delete / open folder / show installed envs (user picked "纯查看" in brainstorming)
- File size aggregation
- Per-version detail expand
- Bulk operations
- Sort controls
- Persisting active kind filter across restarts
- Models marketplace entry changes (separate feature)

## 11. Open questions (none — all resolved in brainstorming)

- Data scope: ✅ scan `Settings.DefaultModelsDirectory`
- Layout: ✅ top kind chip filter + 2-col landscape grid
- Actions: ✅ none (view only)
- Sidebar position: ✅ between Workflows and Templates
- Default enable state: ✅ LocalModels=1 (symmetric with `LocalNodes=0` only because that feature is incomplete; local model read path is stable)