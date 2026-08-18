# Workflow Marketplace v0.6.19 — Design Spec

> **Status:** DRAFT — awaiting user review before plan/implementation.
> Supersedes the 2026-08-12 draft (which used 4 tab-separated sources + SQLite cache + per-source Settings overrides). This rewrite is user-driven v0.6.19: aggregated multi-source view, filesystem-derived download state, multi-select + batch download, env-startup symlink sync.

**Goal:** Add a "工作流市场" (workflow marketplace) section to ComfyUI Manager that aggregates workflow listings from multiple online sources into a single searchable card grid, lets users multi-select and batch-download ComfyUI workflow JSON files into a shared directory, and makes those downloaded workflows available to running envs via junction/symlink sync at env-start time.

**Architecture:** New `MainSection.Workflows` sidebar entry. Pluggable `IWorkflowSource` interface with 3 concrete fetchers (CommunityJson + CivitAi + OpenArt) — each normalized to a `WorkflowEntry` record. Aggregator service (`WorkflowMarketplaceService`) merges results with in-memory dedup by `(source, id)`. Download state is **derived from filesystem scan** of `Settings.WorkflowsDirectory` (no DB), so adding/removing a workflow file via Explorer is naturally reflected in UI. Multi-select + 1-click batch download (SemaphoreSlim=4 concurrency). env-startup hook invokes `WorkflowSymlinker.SyncToEnv` after a successful env-start to create per-env junctions pointing at each downloaded subfolder; failure does not block env-start.

**Tech stack:** WPF .NET 8 / C# 12 · xUnit · SQLite (no new DB — existing `state.db` only seeds env-comfyui-source path, download state is filesystem-derived) · `HttpClient` injected via singleton in `App.xaml.cs` · `JunctionLinker` (existing in `Infrastructure/`) for Windows junctions + `Directory.CreateSymbolicLink` for Linux/macOS · `AppLogger` subsystems: `workflow-marketplace`, `workflow-download`, `workflow-symlink`, `workflow-<source>`.

**base SHA:** `b6d8dc6` (post v0.6.18.4 bulk update console).

---

## 1. Background & user request

User original messages (verbatim, over 2 turns in v0.6.19 brainstorm):

> "增加一个菜单，工作流市场，可以集成工作流搜索，按照要求搜索工作流并且下载。设置中增加自定义的节点访问和地址下载连接"

> "在左边菜单中提供一个本地模型管理和下载功能，将本地模型和文件信息路径进入数据库保存。扫描本地的模型对接civital，并且拉取信息和图片保存在模型相同目录下。下载模型则放到模型定义的路径下，这里注意要分成各种不同的分类，例如是 embedding则在Embedding目录下，如果是unet则放入unet目录下。"

User-clarified decisions (during brainstorm):
- **Priority:** 工作流市场 (workflows) first → 本地模型管理 (models) second → 设置工作流源 (source URL config) third.
- **Package:** 独立 spec + 单独 commit per feature.
- **Sources:** All 3 selected — CommunityJson (generic), CivitAi (`/v1/images` API), OpenArt (generic JSON).
- **Target path:** 用户可配 (user-configurable). Default = `<projectRoot>/workflows/`.
- **Env availability:** 下载 + env 启动时软链/拷贝 (junction/symlink at env-start).
- **Console panel:** 加 Console 面板 (for download progress, mirroring env-start console pattern).
- **Filter scope:** 全部都加 search filters — text + source + sort + "需装节点" (required-nodes) + tags.
- **Multi-select:** 多选 + 批量下载按钮 (multi-select with batch download).
- **Preview image:** 下载时存同目录 (preview saved next to workflow.json in same subfolder).

The app currently manages **nodes** (custom_nodes git repos), **models** (env models dir + shared junction), **envs**, **Python interpreters**, **requirements**, **workflow bulk-updates** — but does not provide a **first-class marketplace UI for downloading workflow JSON files** from online sources. Workflows are a new asset class (alongside nodes and models) that the app must treat as first-class: aggregated source listings, shared download destination, filesystem-derived ownership, per-env availability via junction.

---

## 2. Scope

### In scope

- **3 marketplace source integrations**: CommunityJson (generic schema), CivitAi (uses `/v1/images` API filtered by `tags=workflow`), OpenArt (generic JSON schema).
- **Aggregated single view** — one merged card grid, NOT tab-separated per source. Each card carries its source badge so users see provenance.
- **Per-source Settings controls** — `Enabled` toggle only (one bool per source in `Settings.WorkflowSourcesEnabled`). No API keys / TTL / request-delay knobs in v1 (YAGNI; can add later if a source needs auth).
- **Filter strip** — text search + source chips (multi-select) + tag multi-select + sort dropdown (newest / downloads / name) + "需装节点" (required nodes) checkbox (filters to workflows whose node references are all already installed in at least one env).
- **Card grid** — 200×260 cards: preview image + title + author + tag chips + source badge + checkbox + per-row download button.
- **Multi-select + batch download** — checkbox on each card + 全选/反选 in toolbar + "批量下载" button shows count + runs with `SemaphoreSlim(4)` concurrency.
- **Preview image saved to same subfolder** — when downloading, fetch preview URL and write `<subfolder>/<workflow-slug>-<id8>.preview.<ext>` alongside `workflow.json`.
- **Download console panel** — bottom panel mirroring env-start StartStatus: scrollable monospace log + ✕ close. Streams per-file fetch progress.
- **env-startup junction sync** — `WorkflowSymlinker.SyncToEnv(envId)` runs after successful env-start; creates `<env.ComfyuiSource>/user/default/workflows/` junctions pointing to each downloaded workflow subfolder. Missing env path → log + skip. Junction already correct → skip. Broken junction → recreate.
- **Filesystem-derived download state** — on view open, scan `Settings.WorkflowsDirectory` and read each subfolder's `workflow.json` to populate "已下载" badge + disable per-card download button if already present.
- **Settings section** — `WorkflowsDirectory` (path picker) + 3 source Enabled toggles + 1 button "打开工作流目录".
- **AppLogger instrumentation** — `workflow-marketplace` (aggregator), `workflow-download` (per-file), `workflow-symlink` (env-start sync), `workflow-<source>` (per-source HTTP).

### Out of scope (YAGNI)

- **Tab-separated per-source view** — single aggregated view with source chips filter is sufficient for v1.
- **Per-source API keys, TTL, request-delay knobs** — YAGNI; sources are public. Add later if a source needs auth.
- **SQLite cache for source listings** — YAGNI; refresh on demand from UI. Source APIs are public + cheap.
- **"我的下载" view inside the app** — Windows Explorer + the "打开工作流目录" header button is sufficient.
- **Auto-loading downloaded workflows into a running ComfyUI** — env-start symlink sync IS the loading mechanism.
- **User-added custom marketplace sources** — UI supports the 3 seeded sources only.
- **Workflow JSON validation beyond `JsonDocument.Parse`** — we treat it as opaque JSON.
- **Workflow editing / version tracking / model-association checks** — users edit in ComfyUI.
- **Multi-env sync (different workflows visible per env)** — single shared directory + env-start junction sync = same workflows everywhere.
- **FTS5 search, infinite scroll, pagination** — in-memory filter on aggregated list (≤500 entries typical).
- **Preview image editing / cropping** — direct download + display.

---

## 3. Global constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | Workflows land in a **shared directory** configured in Settings (`Settings.WorkflowsDirectory`). Default = `<projectRoot>/workflows/`. No per-env destination, no env-selection UX. | user clarification ("用户可配路径") |
| **G2** | **3 marketplace sources integrated in v1**: CommunityJson + CivitAi + OpenArt. All aggregated into one view. | user decision ("1 2 3 都要") |
| **G3** | **Aggregated single view** — NOT tab-separated. Source provenance shown as badge per card. Filter via source chips. | design decision (single aggregated UX is the value-add) |
| **G4** | **Download state is filesystem-derived** — scan `Settings.WorkflowsDirectory` on view open; no DB tracking. | design decision (DB-less = simpler, no migration risk) |
| **G5** | **Multi-select + batch download** — checkbox per card + 全选/反选 + "批量下载" button (SemaphoreSlim=4 concurrency). | user decision |
| **G6** | **All filter categories enabled**: text + source + sort + "需装节点" + tags. | user decision ("全部都加 search filters") |
| **G7** | **Preview image saved to same subfolder** — `<subfolder>/<workflow-slug>-<id8>.preview.<ext>`. Fetched in same HTTP pass as `workflow.json`. | user decision ("下载时存同目录") |
| **G8** | **env-startup junction sync** — after successful env-start, `WorkflowSymlinker.SyncToEnv` creates junctions in `<env.ComfyuiSource>/user/default/workflows/` for each downloaded workflow subfolder. Failure does not block env-start (just logs WARN). | user decision ("下载 + env 启动时软链/拷贝") |
| **G9** | **Single injected `HttpClient`** (singleton in `App.xaml.cs`). No `new HttpClient()` per call. | .NET best practice + project convention |
| **G10** | **Console panel for download progress** — mirrors env-start `EnvStartStatusViewModel` pattern (SurfaceBrush/OutlineBrush/CornerRadius 6 + Consolas 11pt NoWrap + ✕ close + auto-scroll). | user decision + v0.6.18.4 pattern |
| **G11** | All HTTP I/O goes through **per-source `IWorkflowSource` implementation**. Aggregator calls `IWorkflowSource.SearchAsync(query, ct)` on each enabled source in parallel (`Task.WhenAll`). | design decision (plug-in interface) |
| **G12** | Junction creation uses existing `JunctionLinker` (Windows) or `Directory.CreateSymbolicLink` (Linux/macOS). | existing infrastructure (M5.2) |
| **G13** | Existing patterns preserved: `AppLogger` subsystems, `MarkDirty` Settings plumbing, `WpfTestResources.EnsureLoaded` STA-load helper, `Property-element + DynamicResource` Setter shape in XAML. | project conventions |
| **G14** | New `MainSection.Workflows` enum value (8th sidebar position, between `LocalNodes` and `Settings`). | this SDD |
| **G15** | Real-fetch integration tests use `[Fact(Skip=...)]`. CI does not hit the network. | project convention |
| **G16** | YAGNI: no SQLite cache, no API keys, no TTL knobs, no pagination, no custom user sources. | explicit YAGNI |

---

## 4. Architecture

### 4.1 Component diagram

```
              ┌────────────────────────────────────────────────────────┐
              │  WorkflowMarketplaceView (XAML)                         │
              │  [search] [source chips] [tags] [sort] [need-nodes]     │
              │  [grid of 200×260 cards]                                 │
              │  [Console panel — download progress]                     │
              └────────┬─────────────────────────────────────────────────┘
                       │ DataContext
                       ▼
              ┌────────────────────────────────────────────────────────┐
              │  WorkflowMarketplaceViewModel                           │
              │   - SearchText, ActiveSourceFilters, ActiveTagFilters   │
              │   - SortBy (Newest / Downloads / Name)                   │
              │   - FilterInstalledNodesOnly                            │
              │   - Workflows (ObservableCollection<WorkflowEntry>)      │
              │   - SelectedCount, HasSelection                          │
              │   - Refresh / ToggleSelectAll / BatchDownload           │
              │   - ConsoleLog, IsConsoleVisible                         │
              └────┬──────────────────┬─────────────────┬───────────────┘
                   │                  │                 │
                   ▼                  ▼                 ▼
       ┌──────────────────┐  ┌──────────────────┐  ┌────────────────────┐
       │ WorkflowMktSvc   │  │ WorkflowDownload │  │ WorkflowSymlinker  │
       │ (aggregate+      │  │ er (HTTP fan-out,│  │ (junction/symlink  │
       │  filter+cache)   │  │  SemaphoreSlim=4)│  │  at env-start)     │
       └────┬─────────┬───┘  └──────────────────┘  └────────────────────┘
            │         │
            ▼         ▼
       ┌────────┐ ┌────────┐ ┌────────┐
       │Community│ │CivitAi │ │OpenArt │   ←  IWorkflowSource interface
       │JsonSrc │ │ Source │ │ Source │      (3 plug-in implementations)
       └────────┘ └────────┘ └────────┘
            │           │           │
            └───────────┴───────────┘
                        │
                        ▼
                ┌──────────────────────────┐
                │ IWorkflowSource (interf) │
                └──────────────────────────┘

       ┌─────────────────────────────────────────────────────┐
       │ WorkflowFilesystemScanner                          │
       │  scan(Settings.WorkflowsDirectory) →              │
       │   List<DownloadedWorkflow>                         │
       │   (used by ViewModel + per-card "已下载" badge)    │
       └─────────────────────────────────────────────────────┘

       ┌─────────────────────────────────────────────────────┐
       │ WorkflowSymlinker.SyncToEnv(envId, envComfyuiSrc)  │
       │  for each downloaded subfolder →                   │
       │    ensure junction at <ComfyuiSrc>/user/default/  │
       │    workflows/<subfolder> → <WorkflowsDirectory>/   │
       │    <subfolder>                                      │
       │  failure → log WARN, do not throw                  │
       └─────────────────────────────────────────────────────┘
```

### 4.2 Data flow

**Browse + filter:**
```
User opens "工作流市场" sidebar entry
  → MainViewModel.ShowWorkflows()
    → ShowWorkflowMarketplaceView() lazy-creates ViewModel
      → VM ctor: scan Settings.WorkflowsDirectory → Downloaded list (G4)
      → VM ctor: Task.Run(LoadAllAsync) — parallel fetch from 3 sources
                  → IWorkflowSource.SearchAsync("", ct) each
                  → dedup by (source, id) → List<WorkflowEntry>
                  → bind into ObservableCollection
                  → ApplyFilter() in-memory (text + source + tags + sort + need-nodes)
User toggles filter → VM.Filtered → ApplyFilter() → rebind Workflows
```

**Batch download:**
```
User selects N cards (checkbox) → SelectedCount = N
User clicks "批量下载 (N)" →
  WorkflowDownloader.DownloadBatchAsync(selectedEntries, WorkflowsDir, ConsoleLog)
    SemaphoreSlim(4) — process N in parallel with max 4 concurrent
    for each entry:
      1. Validate source URL (sanity check workflow.json URL)
      2. Create subfolder <sanitized-title>-<id8>/
      3. HTTP GET workflow.json → pretty-print to disk
      4. HTTP GET preview URL → write <slug>-<id8>.preview.<ext>
      5. On success: log INFO line + scan downloaded subfolder to refresh "已下载" badge
      6. On failure: log ERROR line + continue (don't abort batch)
    Returns summary { Success: N, Failed: M }
  ViewModel: Summary → ConsoleLog final line + InfoBanner
  ViewModel: trigger refresh of Downloaded state (re-scan)
```

**env-startup junction sync:**
```
User clicks env Start in EnvironmentListView
  EnvironmentListViewModel.StartAsync(envId)
    → EnvStartupStopper / ProcessLauncher → env reaches "running" status
    → OnSuccess callback:
      → WorkflowSymlinker.SyncToEnv(envId, env.ComfyuiSource)
        for each DownloadedWorkflow:
          target = <WorkflowsDir>/<subfolder>
          link   = <ComfyuiSrc>/user/default/workflows/<subfolder>
          if Directory.Exists(link) AND JunctionLinker.GetTargetAsync(link) == target:
            skip (already correct)
          elif Directory.Exists(link):
            Directory.Delete(link) — recreate
          else:
            JunctionLinker.CreateAsync(link, target)
        log INFO count of synced junctions; failures → log WARN
        NEVER throws (caller does not await result for env-start gate)
```

---

## 5. Data model

### 5.1 `WorkflowEntry` (aggregate model)

```csharp
public class WorkflowEntry
{
    public WorkflowSourceKind Source { get; init; }
    public string SourceId { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string WorkflowJsonUrl { get; init; } = "";
    public string? PreviewImageUrl { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public int? DownloadCount { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>节点 ID 列表(e.g. "ComfyUI-Impact-Pack" / "ltdrdata/ComfyUI-Manager"),
    /// 供 "需装节点" 过滤用 — 解析自 workflow.json 的 node class references。</summary>
    public IReadOnlyList<string> RequiredNodes { get; init; } = Array.Empty<string>();
}

public enum WorkflowSourceKind { CommunityJson, CivitAi, OpenArt }
```

`RequiredNodes` is parsed from `workflow.json` on demand (not on first browse — keeps browse fast). Parse is trivial: walk JSON, collect every node's `class_type` + reverse-lookup against known node-id mappings (best-effort; missing mappings are ignored, not blocking).

### 5.2 `DownloadedWorkflow` (filesystem-derived state)

```csharp
public class DownloadedWorkflow
{
    public string SubfolderName { get; init; } = "";   // "<slug>-<id8>"
    public string FullPath { get; init; } = "";        // <WorkflowsDir>/<SubfolderName>
    public string Title { get; init; } = "";           // from meta.json
    public string Source { get; init; } = "";          // from meta.json
    public string SourceId { get; init; } = "";       // from meta.json
    public DateTime DownloadedAt { get; init; }        // from meta.json
}
```

`WorkflowFilesystemScanner.Scan(workflowsDir)` returns `List<DownloadedWorkflow>` — one entry per subfolder containing a valid `meta.json`. Subfolders missing `meta.json` (corrupted/partial download) are skipped with WARN log.

### 5.3 `meta.json` sidecar

```json
{
  "title": "...",
  "description": "...",
  "author": "...",
  "source": "community_json",
  "source_id": "...",
  "source_url": "https://...",
  "workflow_json_url": "https://...",
  "preview_image_url": "https://...",
  "tags": ["portrait", "anime"],
  "downloaded_at": "2026-08-18T10:00:00Z"
}
```

### 5.4 File layout for downloads

```
<Settings.WorkflowsDirectory>/                ← default <projectRoot>/workflows/
  <sanitized-title-slug>-<8-char-id>/
    workflow.json                                 (the ComfyUI workflow, pretty-printed UTF-8)
    <slug>-<id8>.preview.<ext>                    (cover image; ext from URL)
    meta.json                                     (sidecar)
  <another-workflow-slug>-<8-char-id>/
    ...
```

Slug generation: lowercase, replace non-`[a-z0-9-]` with `-`, collapse repeated `-`, trim. 8-char ID = first 8 chars of `SourceId` (or hex hash if not available). Subfolder exists → use next available suffix `-1`, `-2`, ...

### 5.5 Settings shape

Add 3 fields to `Models/Settings.cs` + corresponding rows in `CopyInto`:

```csharp
// v0.6.19:工作流市场
[JsonPropertyName("workflows_directory")] public string WorkflowsDirectory { get; set; } = "";
[JsonPropertyName("workflow_source_community_json_enabled")] public bool WorkflowSourceCommunityJsonEnabled { get; set; } = true;
[JsonPropertyName("workflow_source_civitai_enabled")] public bool WorkflowSourceCivitAiEnabled { get; set; } = true;
[JsonPropertyName("workflow_source_openart_enabled")] public bool WorkflowSourceOpenArtEnabled { get; set; } = true;
```

Default = `<projectRoot>/workflows/` resolved by `SettingsDefaults.Apply` (mirroring `LocalNodeDirectory` pattern).

---

## 6. Source interfaces + 3 fetchers

### 6.1 `IWorkflowSource` contract

```csharp
public interface IWorkflowSource
{
    WorkflowSourceKind SourceKind { get; }
    string DisplayName { get; }      // user-visible badge text, e.g. "CivitAI"
    bool IsEnabled { get; set; }     // bound from Settings

    /// <summary>Search + return up to N entries. No pagination in v1.</summary>
    Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct);
}
```

All sources take a single injected `HttpClient`. Each implementation owns its own URL templates + JSON parsing. Outputs always normalize to `WorkflowEntry`.

### 6.2 Per-source responsibilities

| Source | `SourceKind` | Endpoint (verified at impl time) | Notes |
|---|---|---|---|
| `CommunityJsonSource` | `CommunityJson` | URL resolved at implementation time — public JSON list endpoint | Generic `{items: [...]}`, each item = `{id, title, author, tags, json_url, preview_url, ...}` |
| `CivitAiSource` | `CivitAi` | `https://civitai.com/api/v1/images?tags=workflow&...` | Optional `ApiKey` header in v2; v1 = public (lower rate). Each image has metadata.workflow field with JSON URL. |
| `OpenArtSource` | `OpenArt` | URL resolved at implementation time — public workflow browse endpoint | Generic `{items: [...]}` shape similar to CommunityJson |

Each source file ships with:
1. **Unit tests** using `DelegatingHandler` stub HTTP responses (~5–8 tests each: list shape, auth header, error mapping, empty result, malformed JSON).
2. **One real-fetch integration test** `[Fact(Skip=...)]` (CI does not hit network).

If an endpoint cannot be located or requires auth, the spec is amended mid-implementation.

### 6.3 `WorkflowMarketplaceService` aggregator

```csharp
public class WorkflowMarketplaceService
{
    private readonly IReadOnlyList<IWorkflowSource> _sources;

    /// <summary>Run all enabled sources in parallel; merge results; dedup by (Source, SourceId).</summary>
    public async Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
        string query,
        int maxResultsPerSource,
        CancellationToken ct);
}
```

No persistent cache (G4 / YAGNI). Re-fetched each time user clicks "刷新". UI keeps last-loaded results in memory until next refresh.

---

## 7. UI

### 7.1 Sidebar + `MainViewModel` integration

```csharp
public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    LocalNodes,
    Workflows,   // v0.6.19 NEW — 5th sidebar position (between LocalNodes and Settings)
    Settings,
    BulkUpdate,
    SystemStatus
}
```

- 8th sidebar RadioButton "工作流市场" → `ShowWorkflowsCommand` → `MainViewModel.ShowWorkflows()`.
- Cached `WorkflowMarketplaceViewModel` + `WorkflowMarketplaceView` (same lazy pattern as `ShowCatalog`).
- `MainSectionNameProvider.cs` adds mapping: `MainSection.Workflows => "工作流市场"`.
- App.xaml.cs DI: inject `HttpClient` (singleton), `JunctionLinker` (singleton), `IBrowserLauncher` (existing), `Settings.WorkflowsDirectory` resolved path, `EnvironmentRepository` (for env-startup sync to lookup env.ComfyuiSource).

### 7.2 `WorkflowMarketplaceView` layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ 工作流市场   [search: ___________]  [↻ 刷新]   [⛶ 打开目录]   [批量下载 (N)] [全选]│
├──────────────────────────────────────────────────────────────────────┤
│ 源: [☑CommunityJson] [☑CivitAi] [☑OpenArt]   标签: [☑portrait] [☑anime] ... │
│ 排序: [最新▼]  [☑ 只显示可运行(需装节点已装)]    共 M 条 / 已下载 D 个         │
├──────────────────────────────────────────────────────────────────────┤
│ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐                         │
│ │ ☑ 📷 │ │ ☐ 📷 │ │ ☑ 📷 │ │ ☐ 📷 │ │ ☐ 📷 │   ← 200×260 cards         │
│ │title │ │title │ │title │ │title │ │title │                         │
│ │author│ │author│ │author│ │author│ │author│                         │
│ │[#tag]│ │[#tag]│ │[#tag]│ │[#tag]│ │[#tag]│                         │
│ │[⬇ 已下]│ │[⬇ 下载]│ │[⬇ 已下]│ │[⬇ 下载]│ │[⬇ 下载]│                         │
│ └──────┘ └──────┘ └──────┘ └──────┘ └──────┘                         │
│ ...                                                                   │
├──────────────────────────────────────────────────────────────────────┤
│ Console [N 行]                                            [✕ close]  │
│ ┌──────────────────────────────────────────────────────────────────┐ │
│ │ [env-1] 开始下载:portrait-gen-v2-abc12345.json                    │ │
│ │ [env-1] ✓ OK saved to <...>/portrait-gen-v2-abc12345/             │ │
│ │ [env-2] 开始下载:anime-style-def67890.json                        │ │
│ │ [env-2] ✗ FAIL HTTP 404                                            │ │
│ │ [批量下载完成] 成功 1 / 失败 1                                     │ │
│ └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

Components:
- **Top toolbar** — title + search box + 全选/反选 button + 批量下载 button (shows count badge) + 刷新 button + 打开目录 button (calls `IBrowserLauncher.OpenFolder`).
- **Filter strip** — source chips (`CheckBox` × 3 with `ChipToggleButton` style), tag multi-select (`ItemsControl` of toggles derived from current result set's tag union), sort dropdown (`ComboBox` with `Newest` / `Downloads` / `Name`), "需装节点" checkbox (toggles filter).
- **Card grid** — `ItemsControl` with `WrapPanel` (200×260 cards, 16px gutter). Each card = `Border` with `Background=SurfaceBrush`, top-left `CheckBox`, preview image (lazy-loaded via `BitmapImage` + `ImageOpened` callback), title (max 2 lines ellipsis), author (11pt gray), tag chip strip (max 3 visible), source badge (small pill bottom-right), bottom action button "⬇ 下载" / "✓ 已下载" (disabled green when downloaded).
- **Console panel** — mirrors `EnvStartStatusViewModel.LogLines` pattern (SurfaceBrush + OutlineBrush + CornerRadius 6 + DockPanel title + ✕ + ScrollViewer Height 160 + Consolas 11pt NoWrap ItemsControl). Auto-scroll on new lines via `CollectionChanged` handler. Three-state visibility: `!userHidden && (IsBusy || ConsoleLog.Count > 0)` (v0.6.18.4 pattern).
- **Info / Error strip** — same pattern as Catalog (InfoMessage / ErrorMessage bindings).

### 7.3 `WorkflowMarketplaceViewModel` shape

```csharp
public class WorkflowMarketplaceViewModel : ViewModelBase
{
    // Inputs
    public string SearchText { get; set; } = "";
    public ObservableCollection<WorkflowSourceKind> ActiveSourceFilters { get; } = new();
    public ObservableCollection<string> ActiveTagFilters { get; } = new();
    public WorkflowSortKind SortBy { get; set; } = WorkflowSortKind.Newest;
    public bool FilterInstalledNodesOnly { get; set; }

    // Output (filtered view)
    public ObservableCollection<WorkflowEntry> Workflows { get; } = new();
    public ObservableCollection<string> AllTags { get; } = new();          // union of current result set
    public int TotalCount { get; private set; }
    public int DownloadedCount { get; private set; }

    // Selection
    public ObservableCollection<WorkflowEntry> Selected { get; } = new();
    public bool HasSelection => Selected.Count > 0;

    // Console
    public ObservableCollection<string> ConsoleLog { get; } = new();
    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);
    public bool IsBusy { get; private set; }

    // Commands
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }
    public RelayCommand BatchDownloadCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }   // opens WorkflowsDirectory
    public RelayCommand DownloadSingleCommand { get; }   // parameter: WorkflowEntry

    // Lifecycle
    public Task LoadAsync(CancellationToken ct);    // initial fetch + scan
}
```

Filter/sort logic runs in-memory on `Workflows` collection; debounced 250ms on `SearchText` changes to avoid jitter.

### 7.4 Settings UI section

Append a new "工作流市场" section to `SettingsView.xaml`, after "本地节点" section:

```
─── 工作流市场 ───
  Workflows Directory:  [<path>] [Browse]

  数据源:
    [☑] CommunityJson
    [☑] CivitAi
    [☑] OpenArt
```

3 fields only — YAGNI (no API keys / TTL / request-delay knobs in v1).

---

## 8. env-startup integration

`WorkflowSymlinker.SyncToEnv(envId)` is called from `EnvironmentListViewModel.StartAsync` after env reaches "running" status. Implementation:

```csharp
public class WorkflowSymlinker
{
    private readonly Settings _settings;
    private readonly JunctionLinker _linker;
    private readonly WorkflowFilesystemScanner _scanner;
    private readonly AppLogger? _logger;

    public async Task<WorkflowSyncResult> SyncToEnvAsync(
        string envId,
        string envComfyuiSource,
        CancellationToken ct = default);
}

public class WorkflowSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
```

Hook point: `EnvironmentListViewModel.StartAsync` already calls a sequence after env-start. Add `_workflowSymlinker?.SyncToEnvAsync(envId, env.ComfyuiSource ?? "", ct)` as fire-and-forget `ContinueWith` (failure logged but does NOT affect env-start result). The user already sees env as "running"; junction sync proceeds in background.

Sync logic:
1. Resolve `env.ComfyuiSource` from `EnvironmentRepository.Get(envId).ComfyuiSource`. Empty/null → log WARN + return.
2. Scan `Settings.WorkflowsDirectory` → `List<DownloadedWorkflow>`.
3. Ensure `<ComfyuiSource>/user/default/workflows/` exists (create if missing).
4. For each subfolder:
   - target = `<WorkflowsDir>/<subfolder>`
   - link = `<ComfyuiSource>/user/default/workflows/<subfolder>`
   - if `Directory.Exists(link)` AND `JunctionLinker.GetTargetAsync(link, ct) == target` → Skipped++ (already correct).
   - elif `Directory.Exists(link)` AND target mismatch → delete + recreate. On Linux/macOS: `Directory.Delete(link)` then `Directory.CreateSymbolicLink(link, target)`.
   - else → `JunctionLinker.CreateAsync(link, target, ct)` or symlink equivalent.
   - On success → Linked++. On failure → Failed++ + log WARN (do NOT throw).
5. Return result.

**Caller (EnvironmentListViewModel.StartAsync)**: fire-and-forget with `_ = SyncToEnvAsync(...)` wrapped in try/catch — any unexpected exception is caught and logged but does not propagate to env-start status.

---

## 9. Testing strategy

| Layer | Test type | Count | Coverage |
|---|---|---|---|
| `WorkflowFilesystemScanner` | Unit | 4–6 | scan empty dir, scan with N subfolders, skip subfolder without `meta.json`, parse `meta.json` malformed → skip, dedup by source+id |
| `IWorkflowSource` mock (used by all VMs) | Unit | 2 | returns canned entries, simulates timeout |
| `CommunityJsonSource` | Unit (HTTP mocked) | 5–8 | list shape, search error, empty result, malformed JSON, network error, custom URL override |
| `CommunityJsonSource` | Real-fetch | 1 (SKIP) | confirms endpoint still public at impl time |
| `CivitAiSource` | Unit (HTTP mocked) | 5–8 | list shape, image→workflow extraction, error mapping, empty result |
| `CivitAiSource` | Real-fetch | 1 (SKIP) | confirms `/v1/images?tags=workflow` returns data |
| `OpenArtSource` | Unit (HTTP mocked) | 5–8 | list shape, error mapping, empty result |
| `OpenArtSource` | Real-fetch | 1 (SKIP) | confirms endpoint still public at impl time |
| `WorkflowMarketplaceService` | Unit | 4–6 | aggregate 3 sources, dedup by (source,id), parallel via `Task.WhenAll`, disabled source skipped, partial failure tolerated |
| `WorkflowDownloader` | Unit | 5–8 | single download OK, batch with SemaphoreSlim=4 concurrency, subfolder collision → suffix, preview skip-on-404, JSON parse fail, meta.json shape |
| `WorkflowSymlinker` | Unit | 4–6 | sync OK with N subfolders, skip already-correct, recreate broken junction, env.ComfyuiSource empty → return null result, fail-soft on 1 junction failure |
| `WorkflowMarketplaceViewModel` | Unit | 6–8 | filter intersect (text+source+tags), sort by Newest/Downloads/Name, 全选/反选 toggle, multi-select, refresh triggers fetch, scan derived "已下载" badges |
| `WorkflowMarketplaceView` | STA load | 3 | dark theme render, light theme render, console panel render |

**Total target:** ~50–60 new tests (5 + 7 + 7 + 7 + 5 + 7 + 6 + 8 + 3 across T2–T9). Project baseline (post v0.6.18.4) = 1361 / 0 FAIL / 1 SKIP; target post-SDD = **~1415+ / 0 FAIL / 4 SKIP** (baseline 1 + 3 new source real-fetches).

---

## 10. Error handling

| Failure | Behavior |
|---|---|
| Source HTTP fail / timeout | Other sources continue; failed source omitted from result set + log `workflow-<source>` ERROR. UI shows ErrorBanner summary. |
| Source 401/403 (auth) | Same — log WARN with hint ("set API key in Settings" — for v2 only). |
| Source 429 (rate limit) | Log WARN. Omitted from result. Other sources still displayed. |
| Aggregator: 1 of 3 sources fails | Show entries from successful sources. ErrorBanner lists failed source name. |
| Aggregator: ALL sources fail | Show empty state + ErrorBanner "all 3 sources unavailable, retry". |
| Download: HTTP 404 for `workflow.json` | Skip entry, log ERROR, continue batch. Other entries still download. |
| Download: HTTP 404 for preview | Log WARN, write `workflow.json` + `meta.json` only (preview is best-effort). |
| Download: subfolder name collision | Append `-1`, `-2`, etc. |
| Download: workflow JSON parse fail | Treat as non-workflow, log ERROR, skip. |
| Download: write fail (disk full / perms) | Log ERROR, skip. Batch continues. |
| WorkflowSymlinker: 1 junction fail | Log WARN, Failed++, continue. Other junctions still synced. |
| WorkflowSymlinker: env.ComfyuiSource empty | Log WARN, return empty result. Caller no-ops. |
| WorkflowSymlinker: envStart already running, junction sync still runs | Fire-and-forget; does NOT block env-start status. |
| `WorkflowsDirectory` empty / unset | Download disabled with banner hint "configure Workflows Directory in Settings". View still browsable. |
| `WorkflowsDirectory` set but does not exist | Auto-create on first scan/download attempt (mirroring `LocalNodeDirectory` pattern in `CatalogViewModel`). Failure surfaces to ErrorBanner. |
| `meta.json` malformed in a subfolder | Scanner skips + logs WARN. UI doesn't crash. |

**Logger subsystems:**
- `workflow-marketplace` — aggregator (per-source load + count)
- `workflow-download` — per-file download (URL, bytes, result)
- `workflow-symlink` — env-start sync (count + errors)
- `workflow-<source>` — per-source HTTP calls, errors

---

## 11. Implementation task breakdown

~10 tasks, single SDD:

| Task | Files | Notes |
|---|---|---|
| **T1** | Settings shape + UI section | Add `WorkflowsDirectory` + 3 source enabled bools to `Settings.cs` + `CopyInto`; SettingsViewModel + SettingsView.xaml "工作流市场" section (3 fields + Browse + OpenFolder button); `SettingsDefaults.Apply` resolves default path |
| **T2** | `WorkflowEntry` model + `WorkflowFilesystemScanner` + `meta.json` writer | `Models/WorkflowEntry.cs`, `Models/WorkflowSourceKind.cs`, `Services/WorkflowFilesystemScanner.cs`; 5 unit tests |
| **T3** | `IWorkflowSource` interface + `CommunityJsonSource` + 1 mock impl | `Services/WorkflowSource/IWorkflowSource.cs`, `CommunityJsonSource.cs`; 6 tests + 1 SKIP real-fetch |
| **T4** | `CivitAiSource` | `Services/WorkflowSource/CivitAiSource.cs`; 6 tests + 1 SKIP real-fetch |
| **T5** | `OpenArtSource` | `Services/WorkflowSource/OpenArtSource.cs`; 6 tests + 1 SKIP real-fetch |
| **T6** | `WorkflowMarketplaceService` aggregator | `Services/WorkflowMarketplaceService.cs`; parallel via `Task.WhenAll`, dedup, partial-failure tolerance; 5 tests |
| **T7** | `WorkflowDownloader` (single + batch with SemaphoreSlim=4) | `Services/WorkflowDownloader.cs`; subfolder + JSON + preview + meta.json sidecar; 7 tests |
| **T8** | `WorkflowSymlinker` (junction via existing `JunctionLinker` + symlink via `Directory.CreateSymbolicLink`) | `Services/WorkflowSymlinker.cs`; 6 tests |
| **T9** | `WorkflowMarketplaceViewModel` + `WorkflowMarketplaceView` XAML | VM: filter/sort/multi-select/console/refresh/batch-download. XAML: filter strip + card grid + console panel. ~8 tests + 3 STA load tests |
| **T10** | MainViewModel + MainWindow integration + env-start hook | `MainSection.Workflows` enum value + sidebar button + `ShowWorkflowsCommand` + `MainSectionNameProvider` mapping; `EnvironmentListViewModel.StartAsync` hook fires `_workflowSymlinker.SyncToEnvAsync` fire-and-forget; App.xaml.cs DI for HttpClient / JunctionLinker / Settings |
| **T11** | Final review + MEMORY + staging rebuild | opus final review, MEMORY entry, MEMORY.md index update, GUI smoke list |

**Total:** ~18 files (3 model + 1 settings + 6 services + 3 sources + 2 vm/view + 1 test seam + 2 settings ui + 1 mainviewmodel wiring), ~50–60 tests, ~1000–1400 LoC.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Source endpoints return non-JSON or require auth | Spec amended mid-implementation; one source failing doesn't block the other 2 |
| Civitai rate limit (60/h without token) | Default = public; users can disable CivitAi in Settings if hit |
| Preview image CDNs block HttpClient | Downloader treats preview as best-effort; workflow.json + meta.json still succeed |
| Subfolder name collision when 2 workflows have identical titles | Slug + 8-char ID suffix; if still collide, append `-1`, `-2` |
| Junction target path changes between sessions | Sync detects mismatch and recreates |
| env.ComfyuiSource is empty (no env-start yet) | Symlinker returns empty result, logs WARN, caller no-ops |
| Sync runs at every env-start = redundant junction creates | Sync is idempotent — already-correct junctions skipped |
| "需装节点" filter requires parsing every workflow.json at filter change | Parse on demand + cache per-entry; first click triggers parse, subsequent clicks hit cache |
| 3-source parallel fetch = 3 simultaneous HTTPs at startup | Acceptable (each ~1–3s, parallel); show skeleton + console progress |
| New sidebar position breaks muscle memory | Acceptable — 1 new button between LocalNodes and Settings; logical grouping (Local Nodes + Workflows both under "local assets") |
| Large workflow.json files (10+ MB embedded models) | Out of scope; sources typically don't embed full models. If hit, cap preview size + warn |
| WorkflowScan reads every subfolder on each view-open | Acceptable (≤100 subfolders typical); incremental refresh via FileSystemWatcher is YAGNI |
| `workflow.json` parse fails for malformed entries | Skip with WARN, don't include in results (graceful degradation) |

---

## 13. Open questions (none blocking)

None. All architectural decisions are locked in this spec.

---

## 14. References

- Project memory: `docs/superpowers/specs/2026-08-12-workflow-marketplace-design.md` (superseded — older 4-source tab design)
- Project memory: `docs/superpowers/specs/2026-08-16-node-management-bottom-popup-design.md` (most recent comparable UI feature with bottom popup pattern)
- Project memory: `project_v0_6_18_4_bulk_update_console.md` (v0.6.18.4 Console panel pattern reused for download progress)
- Project memory: `project_env_start_status.md` (env-start status panel pattern, mirrored for Console)
- Project memory: `project_catalog_local_download.md` (v0.6.5.9 Catalog local-download → similar `<LocalNodeDirectory>` filesystem-derived state pattern reused for `<WorkflowsDirectory>`)
- `Models/Settings.cs` (existing pattern: `[JsonPropertyName("...")]` + `CopyInto` row)
- `Infrastructure/JunctionLinker.cs` (existing Windows junction helper)
- `Infrastructure/LocalDataPaths.cs` (existing projectRoot/.manager/ path helper)
- `Services/CatalogFetcher.cs` (existing JSON fetcher pattern — generic JSON parsing + native type conversion)
- `WpfTestResources.EnsureLoaded` (v0.6.9.3 STA-load helper) — pattern for `WorkflowMarketplaceViewLoadTests`
- `LiveGitHubVersionFetchTests` — pattern for real-fetch `[SKIP]`-able integration tests
- `IBrowserLauncher` (v0.6.10 T2) — used for "打开工作流目录" button