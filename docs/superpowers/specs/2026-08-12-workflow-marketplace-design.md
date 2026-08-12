# Workflow Marketplace SDD (D4) — Design Spec

> **Status:** DRAFT — awaiting user review before plan/implementation.

**Goal:** Add a "Workflow Marketplace" tab to ComfyUI Manager that integrates with 4 workflow marketplaces (国内 AIGODLIKE + Liblib; 国外 comfyworkflows.com + Civitai workflows) so users can browse and download ComfyUI workflow JSON files into a shared directory configured in Settings.

**Architecture:** New top-level sidebar section backed by a new SQLite cache + a pluggable `IWorkflowMarketplaceClient` interface with 4 concrete implementations. Download writes one subfolder per workflow (`workflow.json` + optional `cover.png` + `meta.json` sidecar) into `Settings.WorkflowDirectory`. Per-source API-key / base-URL / cache-TTL / request-delay overrides live in Settings under a new "工作流市场" section.

**Tech stack:** WPF .NET 8 / C# 12 · xUnit · SQLite via Microsoft.Data.Sqlite (new DB file `data/workflow-cache.db`, isolated from `catalog-cache.db`) · `HttpClient` injected via singleton · `AppLogger` (existing) for `workflow-marketplace` / `workflow-download` / `workflow-<source>` subsystems.

**base SHA:** `939dfea` (main, post SDD A Catalog completeness MERGE).

---

## 1. Background & user request

User original message (verbatim, while SDD A was running):
> "增加一个工作流市场，通过对接国内外工作流市场通过对接来将工作流直接下载下来"

Clarifications (user-clarified during brainstorming):
- "下载工作流相当于内置一个浏览器，下载的工作流将会保存在共享的目录中，这个目录在设置中进行设定"
- "针对不同的工作流平台定制不同的规则，这些均在设置中完成设置"

The app currently manages **nodes** (custom_nodes git repos, v0.6.5.9 local-download pattern), **models** (env models dir + shared junction), **envs**, **Python interpreters**, and **requirements** — but does not manage **workflows** (ComfyUI's JSON graph files). Workflows are a new asset class that the app must treat as first-class: a shared download destination, marketplace-driven acquisition, no per-env state.

---

## 2. Scope

### In scope

- 4 marketplace integrations: **AIGODLIKE**, **Liblib**, **comfyworkflows.com**, **Civitai workflows**.
- Tab-bar navigation between marketplaces (top-level tabs, like a multi-tab browser).
- Search box + paginated grid (page size reuses `Settings.CatalogPageSize`; YAGNI per-marketplace page size for v1).
- Detail panel: cover image + title + description + author + tags + source badge + 2 action buttons (Download / Open in browser).
- Download writes `<WorkflowDirectory>/<slug>-<id8>/{workflow.json,cover.png?,meta.json}`.
- Per-source Settings overrides: `Enabled` / `ApiKey` / `BaseUrl` / `CacheTtlMinutes` / `RequestDelayMs`.
- SQLite cache with TTL for both list pages and detail payloads (separate DB file).
- AppLogger instrumentation across `workflow-marketplace` / `workflow-download` / `workflow-<source>` subsystems.
- STA-thread load tests (dark + light themes) for the new view.

### Out of scope (YAGNI)

- "My Downloads" view inside the app — user manages downloaded workflows via Windows Explorer + the new "Open workflow folder" header button.
- Auto-loading downloaded workflows into a running ComfyUI env.
- User-added custom marketplaces (UI supports the 4 seeded sources only).
- Workflow editing / version tracking / model-association checks / JSON validation beyond `JsonDocument.Parse`.
- FTS5 search, infinite scroll, multi-source merged view.
- Per-source request signing / OAuth flows beyond a static `ApiKey` header.
- Non-listed marketplaces.
- Civitai token-aware features beyond higher rate limit (model associations, NSFW flags, etc.).

---

## 3. Global constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | Workflows land in a **shared directory** configured in Settings (`Settings.WorkflowDirectory`). Default = `<projectRoot>/workflows/`. No per-env destination, no env-selection UX, no integration with `Environment.ComfyuiSource`. | user clarification |
| **G2** | **4 marketplaces integrated in this SDD**: AIGODLIKE, Liblib, comfyworkflows.com, Civitai. All 4 share the same UI shell, different `IWorkflowMarketplaceClient` implementations. | user decision (all selected) |
| **G3** | **Tab-bar navigation** between marketplaces (top-level tabs at the top of `WorkflowMarketplaceView`). | user decision (tabs) |
| **G4** | **SQLite cache with TTL** for both list pages and detail payloads. New DB file `<AppBaseDir>/data/workflow-cache.db`. TTL per-source (`Settings.WorkflowMarketplaceSources[i].CacheTtlMinutes`, default 60). | user decision (cache) |
| **G5** | **Per-source Settings overrides**: each source has `Enabled` / `ApiKey` / `BaseUrl` / `CacheTtlMinutes` / `RequestDelayMs`. All editable in Settings under a new "工作流市场" section. | user clarification ("针对不同的工作流平台定制不同的规则，这些均在设置中完成设置") |
| **G6** | **Public APIs only by default** — `ApiKey` is optional per source. If a source turns out to require auth during implementation, the spec is amended at that point rather than guessed now. | project pattern (v0.6.11+ T3 version-fetch gate) |
| **G7** | All HTTP I/O goes through a **single injected `HttpClient`**. No `new HttpClient()` per call (socket exhaustion). | .NET best practice + project convention |
| **G8** | Existing patterns preserved: `AppLogger` subsystems, `MarkDirty` Settings plumbing (SDD B), `WpfTestResources.EnsureLoaded` STA-load helper (v0.6.9.3), `Property-element + DynamicResource` Setter shape in XAML (v0.6.9.2). | project conventions |
| **G9** | SQLite `EnsureColumn` / `CREATE TABLE IF NOT EXISTS` patterns; new DB file isolated from `catalog-cache.db`. | project convention |
| **G10** | Real-fetch integration tests are `[Fact(Skip=...)]` like `LiveGitHubVersionFetchTests.LiveFetch_RealGitHub_StoresTags`. CI does not hit the network. | project convention |
| **G11** | Downloaded workflow subfolder = `<sanitized-title>-<id8>/`. Filenames inside: `workflow.json` (lowercase, mandatory), `cover.png` (optional), `meta.json` (mandatory sidecar). | user decision (subfolder + sidecar) |
| **G12** | Workflow cache DB file lives at `<AppBaseDir>/data/workflow-cache.db`, created on first use. AppContext.BaseDirectory-relative so it ships inside the self-contained exe. | project convention |
| **G13** | New `MainSection.WorkflowMarketplace` enum value + 7th sidebar button. | this SDD |
| **G14** | YAGNI: no auto-load into ComfyUI, no "My Downloads" view, no custom marketplace UI, no FTS5 search. | explicit YAGNI |

---

## 4. Architecture

### 4.1 Component diagram

```
                  ┌──────────────────────────────────────┐
                  │  WorkflowMarketplaceView (XAML)      │
                  │  [tabs] [search] [grid] [detail]     │
                  └────────┬─────────────────────────────┘
                           │ DataContext
                           ▼
                  ┌──────────────────────────────────────┐
                  │ WorkflowMarketplaceViewModel         │
                  │  - ActiveSource / SourceTabs         │
                  │  - Workflows (paged)                 │
                  │  - Selected Workflow                 │
                  │  - Refresh / Search / Page / Download│
                  └────┬──────────┬───────────┬──────────┘
                       │          │           │
                       ▼          ▼           ▼
              ┌─────────────────────────┐  ┌──────────────────┐
              │ WorkflowMarketplaceSvc  │  │ WorkflowDownload │
              │  (orchestrator + cache) │  │  er (filesystem) │
              └────┬─────────┬───────────┘  └──────────────────┘
                   │         │
                   ▼         ▼
        ┌──────────┐ ┌──────────┐ ┌─────────────┐ ┌──────────┐
        │ Aigodlike│ │ Liblib   │ │ Comfywork.. │ │ Civitai  │
        │ Client   │ │ Client   │ │ Client      │ │ Client   │
        └─────┬────┘ └─────┬────┘ └──────┬──────┘ └─────┬────┘
              │            │             │              │
              └────────────┴─────────────┴──────────────┘
                                │
                                ▼
                    ┌───────────────────────────┐
                    │ IWorkflowMarketplaceClient│
                    │  (interface contract)     │
                    └───────────────────────────┘
                                │
                                ▼
                    ┌───────────────────────────┐
                    │ WorkflowRepository        │◄── reads from
                    │ (search / detail / cache) │    WorkflowCacheStore
                    └───────────────────────────┘
                                │
                                ▼
                    ┌───────────────────────────┐
                    │ WorkflowCacheStore        │
                    │ (SQLite workflow-cache.db)│
                    └───────────────────────────┘
```

### 4.2 Data flow

**Browse:**
```
User clicks tab → ViewModel sets ActiveSource
User types search → ViewModel calls Service.ListAsync(source, query, page)
  Service checks WorkflowCacheStore for (source, query_key, page)
    cache hit + not expired → return cached WorkflowPage
    cache miss / expired → call IWorkflowMarketplaceClient.ListAsync
      on success → write cache + return WorkflowPage
      on HTTP failure → return empty WorkflowPage + log ERROR
User selects tile → ViewModel calls Service.GetDetailAsync(source, workflowId)
  (same cache+miss flow at detail level)
```

**Download:**
```
User clicks "Download" → ViewModel calls WorkflowDownloader.DownloadAsync(detail, targetDir)
  Downloader sanitizes title → <slug>-<id8>
  Creates subfolder
  GET workflow.json → parse JsonDocument → write back pretty-printed
  Optional: GET cover.png → write (skip if already exists)
  Writes meta.json sidecar (title/desc/author/source/url/tags/downloaded_at)
  Returns WorkflowDownloadResult { Success, SubfolderPath, Reason }
ViewModel surfaces result via InfoMessage / ErrorMessage
```

---

## 5. Data model

### 5.1 SQLite cache schema

```sql
-- List-level cache: one row per (source, query_key, page)
CREATE TABLE IF NOT EXISTS workflow_cache_page (
    source TEXT NOT NULL,            -- 'aigodlike' | 'liblib' | 'comfyworkflows' | 'civitai'
    query_key TEXT NOT NULL,         -- normalized 'q=<query>|p=<page>'
    page INTEGER NOT NULL,
    total INTEGER,                   -- total count (-1 if unknown)
    payload_json TEXT NOT NULL,      -- serialized List<Workflow>
    cached_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    PRIMARY KEY(source, query_key, page)
);

-- Detail-level cache: one row per (source, workflow_id)
CREATE TABLE IF NOT EXISTS workflow_cache_detail (
    source TEXT NOT NULL,
    workflow_id TEXT NOT NULL,
    payload_json TEXT NOT NULL,      -- serialized WorkflowDetail
    fetched_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    PRIMARY KEY(source, workflow_id)
);
```

In-memory search filter (`Title LIKE %query%`, `Tag.Contains`) runs after deserialize. With hundreds of items per page, in-memory filter is fast and avoids denormalized-schema traps.

### 5.2 Workflow models

```csharp
public class Workflow
{
    public WorkflowSourceKind Source { get; init; }
    public string SourceId { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? CoverImageUrl { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string RawJsonPreview { get; init; } = ""; // first ~2KB for detail panel
    public DateTimeOffset CachedAt { get; init; }
}

public class WorkflowDetail : Workflow
{
    public string? FullDescription { get; init; }
    public IReadOnlyList<string>? UsedNodes { get; init; }
    public IReadOnlyDictionary<string, object> Extra { get; init; }
        = new Dictionary<string, object>();  // source-specific, persisted to raw_metadata
}

public enum WorkflowSourceKind { Aigodlike, Liblib, Comfyworkflows, Civitai }

public class WorkflowPage
{
    public IReadOnlyList<Workflow> Items { get; init; } = Array.Empty<Workflow>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; } = -1;          // -1 = unknown
    public bool HasNextPage { get; init; }
}

public class WorkflowMarketplaceSettings
{
    public WorkflowSourceKind SourceKind { get; init; }
    public string SourceName { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int CacheTtlMinutes { get; set; } = 60;
    public int RequestDelayMs { get; set; }
}
```

`WorkflowMarketplaceSettings` is the **per-source Settings row** materialized into a class passed to clients. Clients do not touch `Models.Settings` directly — they read overrides via this POCO.

### 5.3 File layout for downloads

```
<Settings.WorkflowDirectory>/
  <sanitized-title-slug>-<8-char-id>/
    workflow.json     (the ComfyUI workflow definition, pretty-printed UTF-8)
    cover.png         (optional; only if API provided CoverImageUrl + succeeded)
    meta.json         (sidecar: title/desc/author/source/url/tags/downloaded_at)
```

Slug generation: lowercase, replace non-`[a-z0-9-]` with `-`, collapse repeated `-`, trim. 8-char ID = first 8 chars of `SourceId` (or hex hash if not available). Subfolder exists → use next available suffix `-1`, `-2`, ...

`meta.json` shape:

```json
{
  "title": "...",
  "description": "...",
  "author": "...",
  "source": "aigodlike",
  "source_url": "https://...",
  "workflow_id": "...",
  "tags": ["portrait", "anime"],
  "downloaded_at": "2026-08-12T10:00:00Z"
}
```

---

## 6. Marketplace clients

### 6.1 `IWorkflowMarketplaceClient` contract

```csharp
public interface IWorkflowMarketplaceClient
{
    WorkflowSourceKind SourceKind { get; }
    string SourceName { get; }       // stable id, e.g. "aigodlike"
    string DisplayName { get; }      // user-visible, e.g. "AIGODLIKE"
    bool DefaultEnabled { get; }

    Task<WorkflowPage> ListAsync(
        string query, int page, int pageSize,
        WorkflowMarketplaceSettings settings,
        CancellationToken ct);

    Task<WorkflowDetail> GetDetailAsync(
        string workflowId,
        WorkflowMarketplaceSettings settings,
        CancellationToken ct);
}
```

All clients take a single injected `HttpClient` (no per-call instantiation). Each client owns its own URL templates + JSON parsing. Client outputs always normalize to `Workflow` / `WorkflowDetail` / `WorkflowPage`.

### 6.2 Per-source responsibilities

| Client | `SourceKind` | Public API endpoint (verified at impl time) | Notes |
|---|---|---|---|
| `AigodlikeMarketplaceClient` | `Aigodlike` | TBD (workflow listing endpoint, JSON expected) | Chinese-language; User-Agent spoofing may be required |
| `LiblibMarketplaceClient` | `Liblib` | TBD (workflow section endpoint) | Chinese; optional `ApiKey` for higher rate limit |
| `ComfyWorkflowsMarketplaceClient` | `Comfyworkflows` | TBD (workflows endpoint) | English; public; no auth |
| `CivitaiMarketplaceClient` | `Civitai` | TBD (workflows endpoint on `civitai.com/api/v1/`) | Optional `ApiKey` for higher rate limit |

Each client file ships with:
1. **Unit tests** using `DelegatingHandler` stub HTTP responses (~5–8 tests each: list shape, detail shape, auth header injection, error mapping, empty result).
2. **One real-fetch integration test** `[Fact(Skip=...)]` (CI does not hit network).

If an endpoint cannot be located or requires auth that isn't optional, the spec is amended mid-implementation.

---

## 7. UI

### 7.1 `WorkflowMarketplaceView` layout

```
+------------------------------------------------------------------+
| [AIGODLIKE] [Liblib] [comfyworkflows] [Civitai]   📁 Open folder |
+------------------------------------------------------------------+
| Search: [____________]   Page 1/10 [<] [>]  ↻ Refresh           |
+------------------------------------------------------------------+
| ┌─────────────┐  ┌─────────────────────────────────────────────┐ |
| │ ▢ tile      │  │ Detail panel                                │ |
| │ ▢ tile      │  │  ┌────────┐  Title (large)                  │ |
| │ ▢ tile      │  │  │ cover  │  author • source badge           │ |
| │ ▢ tile      │  │  │  img   │  [tag1] [tag2] [tag3]            │ |
| │ ▢ tile      │  │  └────────┘  description...                  │ |
| │ (grid)      │  │              [⬇ Download] [🌐 Open in browser]│ |
| └─────────────┘  └─────────────────────────────────────────────┘ |
+------------------------------------------------------------------+
| Info: "Saved to workflows/<slug>-abc12345/"  /  Error: ...     |
+------------------------------------------------------------------+
```

Components:
- **Tab strip** — `TabControl` with 4 `TabItem`s, one per source. Active tab triggers `RefreshActiveAsync()`. `DynamicResource` for brushes.
- **Search box + page controls + refresh button** — copies the `CatalogView` pattern. Page size reuses `Settings.CatalogPageSize` for v1.
- **Tile grid** — `ItemsControl` with `WrapPanel`. Each tile = cover thumbnail (lazy-loaded from `CoverImageUrl` via `BitmapImage` + `ImageOpened` callback) + title + author + source badge. No virtualization required (paged, hundreds per page).
- **Detail panel** — `Grid` with two columns (`Auto`, `*`); cover (`Image` w/ `Stretch=UniformToFill`) + text + action buttons.
- **Action buttons**:
  - "⬇ Download" → calls `WorkflowDownloader.DownloadAsync`, surfaces result via InfoMessage / ErrorMessage.
  - "🌐 Open in browser" → opens `SourceUrl` in default browser (uses existing `IBrowserLauncher` from v0.6.10 T2).
- **"📁 Open folder" header button** → `MainViewModel.OpenFolder(Settings.WorkflowDirectory)`.
- **Info / Error strip** — same pattern as `CatalogView` (InfoMessage / ErrorMessage).

No "My Downloads" view; downloads live in a regular folder browsed via Explorer.

### 7.2 Settings integration

Append a new "工作流市场" section to `SettingsView.xaml`, after "Common Nodes":

```
─── 工作流市场 ───
  Workflow Directory:  [<path>] [Browse]

  Source: AIGODLIKE
    [☑] Enabled
    API key (optional):  [************]
    Base URL override:   [____________________]
    Cache TTL (minutes): [60]
    Request delay (ms):  [0]

  Source: Liblib        (same 5 fields)
  Source: comfyworkflows (same 5 fields)
  Source: Civitai       (same 5 fields)
```

SettingsViewModel binds via existing `MarkDirty` plumbing (SDD B). All 4 sources default to `Enabled=true`, `ApiKey=""`, `BaseUrl=""`, `CacheTtlMinutes=60`, `RequestDelayMs=0`. First-run seeds the 4 entries.

---

## 8. MainViewModel integration

```csharp
public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    WorkflowMarketplace,   // NEW — 4th sidebar position (between Catalog and Settings)
    Settings,
    BulkUpdate,
    SystemStatus
}
```

- `RelayCommand ShowWorkflowMarketplaceCommand` — triggers `ShowWorkflowMarketplace()` (same pattern as `ShowCatalog()`).
- Cached `WorkflowMarketplaceViewModel` + `WorkflowMarketplaceView` (same caching pattern as `ShowCatalog` for `Spotlight`-driven navigation).
- 7th sidebar button (XAML sidebar stack + matching `SectionEqualityToBoolConverter` enum-name mapping).

`MainViewModel` constructor signature: pass `HttpClient` (singleton instance), `IBrowserLauncher` (existing), `Settings.WorkflowDirectory` resolved path.

---

## 9. Testing strategy

| Layer | Test type | Count | Coverage |
|---|---|---|---|
| `AigodlikeMarketplaceClient` | Unit (HTTP mocked) | 5–8 | list shape, detail shape, auth header, error mapping, empty result, malformed JSON |
| `LiblibMarketplaceClient` | Unit (HTTP mocked) | 5–8 | same |
| `ComfyWorkflowsMarketplaceClient` | Unit (HTTP mocked) | 5–8 | same |
| `CivitaiMarketplaceClient` | Unit (HTTP mocked) | 5–8 | same |
| Each client | Real-fetch | 1 (SKIP) | confirms endpoint still public at impl time |
| `WorkflowCacheStore` | Unit | 4–6 | schema init, page insert/replace, TTL semantics, detail insert/replace |
| `WorkflowRepository` | Unit | 4–5 | search filter on cached page, cache hit vs miss, TTL expiry triggers refetch |
| `WorkflowMarketplaceService` | Unit | 4–6 | orchestrates client + cache correctly for list + detail |
| `WorkflowDownloader` | Unit | 5–7 | subfolder creation, slug generation, slug collision → suffix, cover skip-on-exists, JSON parse failure, meta.json sidecar shape |
| `WorkflowMarketplaceViewModel` | Unit | 5–6 | tab switch invalidates cache view, search command, page command, refresh command, download command (via fake seam), error/info message updates |
| `WorkflowMarketplaceView` | STA load | 3 | dark theme render, light theme render, detail panel render |

**Total target:** ~50–60 new tests. Project baseline (post SDD A) = 900; target post-SDD = **~950+ / 0 FAIL / 1 SKIP**.

---

## 10. Error handling

| Failure | Behavior |
|---|---|
| Client HTTP fail / timeout | Return empty `WorkflowPage` + log `workflow-<source>` ERROR. UI shows ErrorBanner. |
| Client 401/403 (auth) | Empty page + log WARN with hint ("set API key in Settings"). UI shows ErrorBanner. |
| Client 429 (rate limit) | Honor `Retry-After` if present, else skip. Log WARN. Empty page. |
| Cache write fail | Log ERROR, still return fresh data (cache is best-effort). |
| Download HTTP fail | Return `WorkflowDownloadResult.Fail(reason)`. UI shows ErrorBanner. |
| Download: cover URL 404 | Skip cover; still write `workflow.json` + `meta.json`. Log WARN. |
| Download: subfolder name collision | Append suffix `-1`, `-2`, etc. |
| Download: workflow JSON parse fail | Treat as non-workflow, return Fail. |
| `WorkflowDirectory` empty / unset | "Open folder" button still works (creates + opens), but Download button disabled with banner hint "configure Workflow Directory in Settings". |
| Workflow JSON is empty body or not JSON | Download fails with "invalid workflow JSON". |

**Logger subsystems:**
- `workflow-marketplace` — orchestrator (cache hits, refetch triggers)
- `workflow-download` — subfolder creation, JSON write, cover write, errors
- `workflow-<source>` — per-client HTTP calls, auth, rate limits

---

## 11. Implementation task breakdown

10 tasks, single SDD:

| Task | Files | Notes |
|---|---|---|
| T1 | Settings shape + UI section | `Settings.WorkflowDirectory`, `WorkflowMarketplaceSource` POCO, 4 seed entries, SettingsView "工作流市场" section, SettingsViewModel 5 fields × 4 sources bindings |
| T2 | Workflow model + WorkflowCacheStore | `Workflow`, `WorkflowDetail`, `WorkflowPage`, `WorkflowSourceKind`; new `WorkflowCacheStore` SQLite + `WorkflowRepository` CRUD |
| T3 | `IWorkflowMarketplaceClient` + base types + `WorkflowMarketplaceService` | interface, `WorkflowMarketplaceSettings`, service orchestration + cache + tests |
| T4 | Aigodlike + Liblib clients | 2 concrete clients + HTTP-mocked unit tests + 1 SKIP real-fetch each |
| T5 | Comfyworkflows + Civitai clients | 2 concrete clients + HTTP-mocked unit tests + 1 SKIP real-fetch each |
| T6 | `WorkflowDownloader` | subfolder + JSON + cover + meta sidecar + 5–7 tests |
| T7 | `WorkflowMarketplaceViewModel` + `WorkflowDetailViewModel` | tabs, search, page, refresh, select, download command; ~6 tests |
| T8 | `WorkflowMarketplaceView` XAML | tab + grid + detail panel + STA load test (dark + light) |
| T9 | MainViewModel integration | `MainSection.WorkflowMarketplace`, sidebar button, `ShowWorkflowMarketplaceCommand`, View/VM caching, `HttpClient` DI in `App.xaml.cs` |
| T10 | Final review + MEMORY + staging rebuild | opus final review, MEMORY entry, MEMORY.md index update, GUI smoke list |

**Total:** ~25–30 files, ~50–60 tests, ~1000–1500 LoC.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Marketplace endpoints return non-JSON or require login we can't bypass | Spec amended mid-implementation; one client failing doesn't block the other 3 |
| Civitai API rate limit (60/h without token) | Default ApiKey = "" (no extra cost); documentation note for users |
| Cover image CDNs block HttpClient (CORS / referrer) | Downloader treats cover as best-effort; workflow.json + meta.json still succeed |
| Subfolder name collision when 2 workflows have identical titles | Slug + 8-char ID suffix; if still collide, append `-1`, `-2` |
| Cache DB file path conflict with existing `catalog-cache.db` | New file `data/workflow-cache.db`; no shared tables, no migration risk |
| HttpClient socket exhaustion | Single injected `HttpClient` per app lifetime (DI singleton in App.xaml.cs) |
| Settings UI section gets long with 4 sources × 5 fields | `Expander` controls (one per source), default-collapsed |
| Real-fetch tests hit prod on CI run | `[Fact(Skip=...)]` with documented run-instruction |
| `WorkflowDirectory` is on a slow / read-only drive | Folder creation wrapped in try/catch; error surfaces to ErrorBanner; download disabled |

---

## 13. Open questions (none blocking)

None. All architectural decisions are locked in this spec.

---

## 14. References

- Project memory: `docs/superpowers/specs/2026-08-11-catalog-completeness-design.md` (Catalog completeness SDD A — most recent comparable feature)
- Project memory: SDD B Settings plumbing (`feedback_wpf_style_setter_dynamic_resource.md` — XAML pattern)
- `WpfTestResources.EnsureLoaded` (v0.6.9.3 STA-load helper) — pattern for `WorkflowMarketplaceViewLoadTests`
- `LiveGitHubVersionFetchTests` — pattern for real-fetch `[SKIP]`-able integration tests
- `IBrowserLauncher` (v0.6.10 T2) — used for "Open in browser" + "Open workflow folder" buttons