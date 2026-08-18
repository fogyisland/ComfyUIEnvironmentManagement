# Model Marketplace v0.6.20 — Design Spec

> **Status:** DRAFT — awaiting user review before plan/implementation.
> Complements v0.6.19 workflow marketplace (`docs/superpowers/specs/2026-08-18-workflow-marketplace-design.md`). Mirror architecture + new domain (GB-scale model files, CivitAI Models API, kind classification, NSFW badging, multi-version selection).

**Goal:** Add a "模型市场" (model marketplace) sidebar section to ComfyUI Manager that lists models from CivitAI's public Models API, lets users **multi-select one or more versions per model** and batch-download them into a shared directory organized by ComfyUI's standard model-kind subfolder (`checkpoints/`, `loras/`, `vae/`, `controlnet/`, `embeddings/`, `upscale_models/`, `hypernetworks/`, `other/`), and makes those downloaded models available to running envs via per-version junction/symlink sync at env-start time. Display-only NSFW badge (no content filter), kind filter chips, by-kind download directory, by-version folder collision suffix, and streaming + progress % for GB-scale files.

**Architecture:** New `MainSection.Models` sidebar entry (9th position, after `Workflows`). Pluggable `IModelSource` interface — **v0.6.20 ships with `CivitAiModelSource` only** (uses `https://civitai.com/api/v1/models`). HuggingFace and other sources are reserved (interface stub + DI register returns empty) so the architecture is extension-ready. Aggregator (`ModelMarketplaceService`) merges results with in-memory dedup by `(Source, SourceId)`. Download state is **derived from filesystem scan** of `Settings.ModelsDirectory` (no DB). Selection granularity is **per-version** (a card's checkbox selects all versions; each version has its own checkbox to select subset). Streaming HTTP download with progress %, atomic rename of `<file>.partial` → `<file>` on success, batch with `SemaphoreSlim(4)` concurrency. env-startup hook invokes `ModelSymlinker.SyncToEnv` after successful env-start; failure does not block env-start.

**Tech stack:** WPF .NET 8 / C# 12 · xUnit · SQLite (no new DB — same as v0.6.19, filesystem-derived download state) · `HttpClient` injected via singleton in `App.xaml.cs` (existing v0.6.19 wiring) · `JunctionLinker` (existing) for Windows junctions + `Directory.CreateSymbolicLink` for Linux/macOS · `AppLogger` subsystems: `model-marketplace`, `model-download`, `model-symlink`, `model-<source>`.

**base SHA:** `a8a47bf` (post v0.6.19.x workflow marketplace + hotfix wave).

---

## 1. Background & user request

User original message (verbatim):

> "下载 模型的按钮 没有出来，功能也没有？修完这个问题之后 立即完成后续的任务"

User-clarified decisions (during brainstorm — `2026-08-18` follow-up):
- **Form:** Standalone sidebar section, full UI (not a button on existing view).
- **Scope:** v0.6.20 = CivitAI Models API only. HuggingFace and other sources = v0.6.21+ (interfaces stubbed now, not implemented).
- **Version selection:** Per-version checkbox multi-select. All `modelVersions` of a model are flat-laid in the card; user can select 1, many, or all.
- **NSFW policy:** Display all content (no filter), but show NSFW / Mature / SFW badge pill per card so users know the rating.
- **Classification:** By model kind (`Checkpoint` / `LORA` / `VAE` / `Controlnet` / `TextualInversion` / `Upscaler` / `Hypernetwork` / `Other`). Storage directory split by kind → matches ComfyUI's standard subfolder structure.

The app currently provides **node management**, **workflow marketplace (v0.6.19)**, **env management**, **shared models editor button** (v0.6.18 for editing env junction dirs), **system status**, **bulk update** — but provides no **first-class UI for discovering and downloading models from online sources**. Users currently hand-download models from civitai.com or huggingface.co and place them in env directories manually. v0.6.20 makes model discovery + acquisition a click-through experience, mirroring v0.6.19's workflow marketplace.

---

## 2. Scope

### In scope

- **CivitAI Models API integration** — public, anonymous (rate-limited but workable for v0.6.20). Single source for v1.
- **Aggregated single view** — one card grid; source provenance shown as badge (CivitAI for v1; interface reserves room for HuggingFace).
- **Multi-version selection** — each card lists all `modelVersions` of the model; each version is independently checkboxable. Card-level "全选/反选" toggles all versions.
- **NSFW badge pill** — every card displays NSFW rating (SFW / Mature / NSFW) as colored pill. No filtering; all content visible.
- **Kind classification** — `Checkpoint` / `LORA` / `VAE` / `Controlnet` / `TextualInversion` / `Upscaler` / `Hypernetwork` / `Other`. Top filter strip has chips per kind. Storage directory split by kind → matches ComfyUI standard subfolders.
- **Streaming download with progress %** — HTTP `ResponseHeadersRead` → manual `CopyToAsync` with progress callback. Writes `<file>.partial`, atomic rename to `<file>` on success. Models are GB-scale; UI shows real-time percentage.
- **Batch download** — `SemaphoreSlim(4)` concurrency. Failures don't abort batch; per-version log line.
- **env-startup junction sync** — `ModelSymlinker.SyncToEnv(envId)` runs after successful env-start; creates `<env.ComfyuiSource>/models/<kind>/<version-slug>-<vid8>` junctions pointing to each downloaded version subfolder.
- **Filesystem-derived download state** — scan `Settings.ModelsDirectory` on view open; no DB tracking.
- **Settings section** — `ModelsDirectory` (path picker) + 1 source Enabled toggle + 1 button "打开模型目录".
- **AppLogger instrumentation** — `model-marketplace`, `model-download`, `model-symlink`, `model-civitai`.
- **3 new converters** — `ModelNsfwBadgeBrush`, `ModelNsfwBadgeText`, `ModelKindBadgeBrush` for kind+NSFW pill rendering.
- **Source hooks for v0.6.21+** — `IModelSource` interface + DI registration pattern; `HuggingFaceModelSource` placeholder class returns empty list (no impl).

### Out of scope (YAGNI for v0.6.20)

- **HuggingFace implementation** — interface reserved; class returns empty list. Real impl is v0.6.21+ task.
- **NSFW content filter toggle** — user explicitly chose "always display". No UI control.
- **Per-source API keys, TTL, request-delay knobs** — YAGNI. CivitAI is public.
- **SQLite cache for source listings** — YAGNI; refresh on demand.
- **Multi-file version bundles** (some CivitAI versions ship model + VAE as separate files in one version) — v0.6.20 downloads the primary file only. Future: support multi-file.
- **HTTP Range resume on download failure** — partial download is deleted and restarted. v0.6.20 doesn't resume from where it left off.
- **Version comparison / "latest" badge** — user picks versions manually. No auto-suggested.
- **My downloads view inside app** — Explorer + "打开模型目录" button is sufficient.
- **Auto-loading downloaded models into a running ComfyUI** — env-start symlink sync IS the loading mechanism.
- **Multi-env sync** — single shared directory + env-start junction sync = same models everywhere.
- **FTS5 search, infinite scroll, pagination** — in-memory filter on aggregated list (≤200 typical).
- **Preview image cropping / editing** — direct download + display.
- **Model metadata editing** — users edit in ComfyUI.

---

## 3. Global constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | Models land in a **shared directory** configured in Settings (`Settings.ModelsDirectory`). Default = `<projectRoot>/models/`. No per-env destination, no env-selection UX. | user clarification |
| **G2** | **v0.6.20 ships 1 source: CivitAI Models API**. HuggingFace and others are reserved via interface but not implemented. | user decision |
| **G3** | **Aggregated single view** — NOT tab-separated per source. Source provenance shown as badge per card. | design decision (mirrors v0.6.19) |
| **G4** | **Download state is filesystem-derived** — scan `Settings.ModelsDirectory` on view open; no DB tracking. | design decision (mirrors v0.6.19) |
| **G5** | **Multi-version selection** — per-version checkbox + per-card "全选" toggle + "批量下载" button (SemaphoreSlim=4 concurrency). | user decision |
| **G6** | **NSFW badge displayed for all content** — no filter, no UI toggle. Badge color = SFW(gray)/Mature(warning)/NSFW(error). | user decision |
| **G7** | **Kind classification drives both filter chips AND storage subfolder** — Checkpoint→`checkpoints/`, LORA→`loras/`, VAE→`vae/`, etc. ComfyUI standard subfolder mapping (see §5.5). | user decision |
| **G8** | **Streaming download with progress %** — `HttpCompletionOption.ResponseHeadersRead` + manual `CopyToAsync` with progress callback. Models are GB-scale. | design decision |
| **G9** | **env-startup junction sync** — after successful env-start, `ModelSymlinker.SyncToEnv` creates per-version junctions in `<env.ComfyuiSource>/models/<kind>/<version-slug>-<vid8>` for each downloaded version. Failure does not block env-start (just logs WARN). | design decision (mirrors v0.6.19) |
| **G10** | **Single injected `HttpClient`** (singleton in `App.xaml.cs`, same instance as v0.6.19). No `new HttpClient()` per call. | .NET best practice |
| **G11** | **Console panel for download progress** — mirrors env-start `EnvStartStatusViewModel` pattern + v0.6.19 download console (SurfaceBrush/OutlineBrush/CornerRadius 6 + Consolas 11pt NoWrap + ✕ close + auto-scroll). | design decision (mirrors v0.6.19) |
| **G12** | All HTTP I/O goes through **per-source `IModelSource` implementation**. Aggregator calls `IModelSource.SearchAsync(query, ct)` on each enabled source in parallel (`Task.WhenAll`). | design decision (mirrors v0.6.19) |
| **G13** | Junction creation uses existing `JunctionLinker` (Windows) or `Directory.CreateSymbolicLink` (Linux/macOS). | existing infrastructure (M5.2) |
| **G14** | Existing patterns preserved: `AppLogger` subsystems, `MarkDirty` Settings plumbing, `WpfTestResources.EnsureLoaded` STA-load helper, `Property-element + DynamicResource` Setter shape in XAML. | project conventions |
| **G15** | New `MainSection.Models` enum value (9th sidebar position, between `Workflows` and `Settings`). | this SDD |
| **G16** | Real-fetch integration tests use `[Fact(Skip=...)]`. CI does not hit the network. | project convention |
| **G17** | YAGNI: no SQLite cache, no API keys, no TTL knobs, no pagination, no custom user sources, no HF impl, no resume. | explicit YAGNI |

---

## 4. Architecture

### 4.1 Component diagram

```
              ┌────────────────────────────────────────────────────────────┐
              │  ModelMarketplaceView (XAML)                                │
              │  [search] [kind chips] [source chips] [sort] [counts]       │
              │  [grid of 240×280 cards with version checkboxes]            │
              │  [Console panel — streaming download progress]              │
              └────────┬───────────────────────────────────────────────────┘
                       │ DataContext
                       ▼
              ┌────────────────────────────────────────────────────────────┐
              │  ModelMarketplaceViewModel                                  │
              │   - SearchText, ActiveKindFilters, ActiveSourceFilters      │
              │   - SortBy (Newest / Downloads / Name)                      │
              │   - Models (ObservableCollection<ModelEntry>)               │
              │   - SelectedVersions (ObservableCollection<ModelVersionEntry>) │
              │   - SelectedCount, HasSelection                             │
              │   - Refresh / ToggleSelectAll / BatchDownload               │
              │   - ConsoleLog, IsConsoleVisible                            │
              │   - Per-card DownloadSingleCommand                          │
              └────┬──────────────────┬─────────────────┬──────────────────┘
                   │                  │                 │
                   ▼                  ▼                 ▼
       ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
       │ ModelMktService  │  │ ModelDownloader  │  │ ModelSymlinker   │
       │ (aggregate+      │  │ (streaming HTTP, │  │ (junction/symlink│
       │  filter+dedup)   │  │  SemaphoreSlim=4,│  │  at env-start,   │
       │                  │  │  progress %)     │  │  per-version)    │
       └────┬─────────┬───┘  └──────────────────┘  └──────────────────┘
            │         │
            ▼         ▼
       ┌────────┐ ┌───────────┐
       │CivitAi │ │HuggingFace│   ←  IModelSource interface
       │MdSrc   │ │ (stub:    │      (CivitAI = v0.6.20 full impl;
       │        │ │  empty)   │       HF stub returns [] for v0.6.20)
       └────────┘ └───────────┘
            │
            ▼
       ┌─────────────────────────────────────────────────────┐
       │ ModelFilesystemScanner                              │
       │  scan(Settings.ModelsDirectory) →                  │
       │   List<DownloadedModel>                             │
       │   (used by ViewModel + per-card "已下载" badge)     │
       └─────────────────────────────────────────────────────┘

       ┌─────────────────────────────────────────────────────┐
       │ ModelSymlinker.SyncToEnv(envId, envComfyuiSrc)      │
       │  for each downloaded version subfolder →           │
       │    ensure junction at <ComfyuiSrc>/models/         │
       │    <kind>/<model-slug>-<id8>__<version-slug>-<vid8> │
       │    → <ModelsDir>/<kind>/<model-slug>-<id8>/        │
       │      <version-slug>-<vid8>/                         │
       │  failure → log WARN, do not throw                  │
       └─────────────────────────────────────────────────────┘
```

### 4.2 Data flow

**Browse + filter:**
```
User opens "模型市场" sidebar entry
  → MainViewModel.ShowModels()
    → ShowModelMarketplaceView() lazy-creates ViewModel
      → VM ctor: scan Settings.ModelsDirectory → Downloaded list
      → VM ctor: Task.Run(LoadAllAsync) — parallel fetch from enabled sources
                  → IModelSource.SearchAsync("", ct) each
                  → dedup by (source, id) → List<ModelEntry>
                  → bind into ObservableCollection
                  → ApplyFilter() in-memory (text + kind + source + sort)
User toggles kind chip / search → VM.Filtered → ApplyFilter() → rebind Models
```

**Batch download:**
```
User expands card → sees 5 versions, checks 3 → SelectedVersions = 3
User clicks "批量下载 (3)" →
  ModelDownloader.DownloadBatchAsync(selectedVersions, ModelsDir, ConsoleLog)
    SemaphoreSlim(4) — process N in parallel with max 4 concurrent
    for each version:
      1. Validate download URL (sanity check primary file URL)
      2. Resolve target dir: <ModelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/
      3. Create dir if missing (collision → suffix -1, -2, ...)
      4. HTTP GET primary file with ResponseHeadersRead
      5. Stream to <file>.partial with progress % callback
      6. Atomic rename → <file>
      7. Write meta.json sidecar
      8. On success: log "✓ OK saved to <path> (X.X GB, Y.Ys)"
      9. On failure: log "✗ FAIL <reason>" (HTTP 404, disk full, etc.)
    Returns summary { Success: N, Failed: M }
  ViewModel: Summary → ConsoleLog final line + InfoBanner
  ViewModel: trigger refresh of Downloaded state (re-scan)
```

**env-startup junction sync:**
```
User clicks env Start in EnvironmentListView
  EnvironmentListViewModel.StartAsync(envId)
    → ProcessLauncher → env reaches "running" status
    → OnSuccess callback:
      → ModelSymlinker.SyncToEnv(envId, env.ComfyuiSource)
        for each DownloadedModel:
          target = <ModelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>
          link   = <ComfyuiSrc>/models/<kind>/<model-slug>-<id8>__<version-slug>-<vid8>
          if Directory.Exists(link) AND JunctionLinker.GetTargetAsync(link) == target:
            skip (already correct)
          elif Directory.Exists(link):
            Directory.Delete(link) — recreate
          else:
            JunctionLinker.CreateAsync(link, target)
        log INFO count of synced junctions; failures → log WARN
        NEVER throws (fire-and-forget)
```

---

## 5. Data model

### 5.1 `ModelEntry` (aggregate model — 1 card per CivitAI model)

```csharp
public class ModelEntry
{
    public ModelSourceKind Source { get; init; } = ModelSourceKind.CivitAi;
    public string SourceId { get; init; } = "";        // CivitAI model id (e.g. "12345")
    public string SourceUrl { get; init; } = "";        // https://civitai.com/models/{id}
    public string Title { get; init; } = "";           // CivitAI name field
    public string? Description { get; init; }
    public string? Author { get; init; }               // creator.username
    public string? AuthorUrl { get; init; }            // https://civitai.com/user/{username}
    public ModelKind Kind { get; init; }                // Checkpoint/LORA/VAE/... parsed from "type"
    public string? BaseModel { get; init; }             // first version's baseModel (display)
    public ModelNsfwKind NsfwKind { get; init; }        // SFW/Mature/NSFW from nsfwLevel
    public int? NsfwLevel { get; init; }                // 0=None,1=Soft,2=Mature,3=NSFW
    public int? DownloadCount { get; init; }            // stats.downloadCount
    public int? RatingCount { get; init; }              // stats.ratingCount
    public double? RatingStars { get; init; }           // stats.rating (0-5)
    public DateTimeOffset? PublishedAt { get; init; }   // createdAt
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? PreviewImageUrl { get; init; }       // first version's first image.url
    public IReadOnlyList<ModelVersionEntry> Versions { get; init; } = Array.Empty<ModelVersionEntry>();
}

public enum ModelSourceKind { CivitAi }
public enum ModelKind { Unknown, Checkpoint, LORA, VAE, Controlnet, TextualInversion, Upscaler, Hypernetwork, Other }
public enum ModelNsfwKind { SFW, Mature, NSFW }
```

`ModelKind` is parsed from CivitAI `type` string. Mapping (case-insensitive, normalized):
- `"Checkpoint"` → `Checkpoint`
- `"LORA"` / `"LyCORIS"` → `LORA`
- `"VAE"` → `VAE`
- `"Controlnet"` → `Controlnet`
- `"TextualInversion"` → `TextualInversion`
- `"Upscaler"` / `"ESRGAN"` / `"RealESRGAN"` → `Upscaler`
- `"Hypernetwork"` → `Hypernetwork`
- Anything else / missing → `Other`

`ModelNsfwKind` is derived from CivitAI `nsfwLevel` (preferred) or `nsfw` boolean fallback:
- `nsfwLevel == 0` → SFW
- `nsfwLevel == 1` → SFW (Soft, treated as SFW for badge)
- `nsfwLevel == 2` → Mature
- `nsfwLevel >= 3` → NSFW
- If `nsfwLevel` missing but `nsfw == true` → Mature
- If both missing → SFW (CivitAI default)

### 5.2 `ModelVersionEntry` (per-version selection unit)

```csharp
public class ModelVersionEntry
{
    /// Composite ID = "{SourceKind}:{ModelId}:{VersionId}" — globally unique, used in SelectedVersions.</summary>
    public string Id { get; init; } = "";
    public ModelEntry Parent { get; init; } = null!;
    public string SourceVersionId { get; init; } = "";   // CivitAI modelVersionId (e.g. "67890")
    public string Name { get; init; } = "";              // version.name (e.g. "v5.0 fp16")
    public string? BaseModel { get; init; }               // version.baseModel
    public long SizeBytes { get; init; }                  // primary file.sizeKB * 1024
    public string PrimaryDownloadUrl { get; init; } = ""; // primary file.downloadUrl
    public IReadOnlyList<ModelFile> Files { get; init; } = Array.Empty<ModelFile>();
    public DateTimeOffset? PublishedAt { get; init; }      // version.createdAt
    public bool IsEarlyAccess { get; init; }               // version.earlyAccessEnabled
}

public class ModelFile
{
    public string Name { get; init; } = "";               // e.g. "model.safetensors" / "VAE.safetensors"
    public string Format { get; init; } = "";             // "Safe Tensor"/"PickleTensor"/"ONNX"/"Other"
    public long SizeBytes { get; init; }                   // sizeKB * 1024
    public string DownloadUrl { get; init; } = "";
    public bool IsPrimary { get; init; }                  // marked primary in API; v0.6.20 downloads primary only
}
```

v0.6.20 downloads only the primary file per version (largest or marked primary in API). Multi-file bundle support deferred.

### 5.3 `DownloadedModel` (filesystem-derived state)

```csharp
public class DownloadedModel
{
    public string SubfolderName { get; init; } = "";     // "<version-slug>-<vid8>" (per-version folder name)
    public string FullPath { get; init; } = "";           // <ModelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/
    public ModelKind Kind { get; init; }
    public string? Title { get; init; }                   // from meta.json
    public string Source { get; init; } = "";             // from meta.json
    public string SourceId { get; init; } = "";           // from meta.json
    public string SourceVersionId { get; init; } = "";    // from meta.json
    public DateTime DownloadedAt { get; init; }           // from meta.json
}
```

`ModelFilesystemScanner.Scan(modelsDir)` recursively walks `<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/` for `meta.json`. Subfolders missing `meta.json` are skipped with WARN.

### 5.4 `meta.json` sidecar

```json
{
  "title": "Realistic Vision v5.0",
  "kind": "Checkpoint",
  "base_model": "SD 1.5",
  "author": "AuthorName",
  "source": "civitai",
  "source_id": "12345",
  "source_version_id": "67890",
  "source_url": "https://civitai.com/models/12345",
  "primary_filename": "realisticVision_v5.safetensors",
  "size_bytes": 6789012345,
  "nsfw_level": 0,
  "downloaded_at": "2026-08-18T10:00:00Z"
}
```

### 5.5 File layout for downloads

```
<Settings.ModelsDirectory>/                   ← default <projectRoot>/models/
  checkpoints/                                 ← kind subfolder
    realistic-vision-12345678/                 ← model-slug + 8-char model-id
        v50-fp16-87654321/                     ← version-slug + 8-char version-id
          realisticVision_v5.safetensors       ← primary file (or <filename>.ext)
          meta.json                            ← sidecar
        v51-fp32-11223344/                     ← another version (if downloaded)
          ...
  loras/
    detail-totaling-23456789/
      v1-99887766/
        detail_totaling.safetensors
        meta.json
  vae/
    ...
```

**Slug generation:** lowercase, replace non-`[a-z0-9-]` with `-`, collapse repeated `-`, trim. 8-char ID = first 8 chars of source ID (e.g. `"12345"` → `"12345"` if short, or first 8 chars if long).

**Collision handling:** if `<version-slug>-<vid8>/` already exists, append `-1`, `-2`, etc., scanning sequentially until a free name is found. **Version folders never overwrite** (a model with 3 versions yields 3 distinct subdirs even if names collide).

### 5.6 Settings shape

Add 2 fields to `Models/Settings.cs` + corresponding rows in `CopyInto`:

```csharp
// v0.6.20: 模型市场
[JsonPropertyName("models_directory")] public string ModelsDirectory { get; set; } = "";
[JsonPropertyName("model_source_civitai_enabled")] public bool ModelSourceCivitAiEnabled { get; set; } = true;
```

Default = `<projectRoot>/models/` resolved by `SettingsDefaults.Apply` (mirroring `LocalNodeDirectory` pattern from v0.6.5.9 and `WorkflowsDirectory` from v0.6.19).

### 5.7 Kind → ComfyUI subfolder mapping

```csharp
private static readonly Dictionary<ModelKind, string> KindToComfyUiSubfolder = new()
{
    [ModelKind.Checkpoint] = "checkpoints",
    [ModelKind.LORA] = "loras",
    [ModelKind.VAE] = "vae",
    [ModelKind.Controlnet] = "controlnet",
    [ModelKind.TextualInversion] = "embeddings",
    [ModelKind.Upscaler] = "upscale_models",
    [ModelKind.Hypernetwork] = "hypernetworks",
    [ModelKind.Other] = "other",
    [ModelKind.Unknown] = "other",
};
```

Used by:
1. Storage path: `<ModelsDir>/<kind-subfolder>/<model-slug>-<id8>/<version-slug>-<vid8>/`
2. Symlink path: `<env.ComfyuiSource>/models/<kind-subfolder>/<model-slug>-<id8>__<version-slug>-<vid8>` → above
3. Symlink name separator: `__` (double underscore) prevents collisions when model-slug and version-slug have similar prefixes.

---

## 6. Source interfaces + CivitAI fetcher

### 6.1 `IModelSource` contract

```csharp
public interface IModelSource
{
    ModelSourceKind SourceKind { get; }
    string DisplayName { get; }      // user-visible badge text, e.g. "CivitAI"
    bool IsEnabled { get; set; }     // bound from Settings

    /// <summary>Search + return up to N entries. v0.6.20: query string only, no kind filter (caller filters).</summary>
    Task<IReadOnlyList<ModelEntry>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct);
}
```

All sources take a single injected `HttpClient`. Each implementation owns its own URL templates + JSON parsing. Outputs always normalize to `ModelEntry` (with populated `Versions` list).

### 6.2 Per-source responsibilities

| Source | `SourceKind` | Status | Endpoint | Notes |
|---|---|---|---|---|
| `CivitAiModelSource` | `CivitAi` | v0.6.20 full | `https://civitai.com/api/v1/models?limit=100&page={n}&nsfw=true&sort=Newest` | Paginated; pulls 100/page. Each item = `{id, name, type, nsfw, nsfwLevel, tags[], stats{}, creator{}, modelVersions[]}` |
| `HuggingFaceModelSource` | `HuggingFace` | v0.6.20 stub | N/A | Returns empty `IReadOnlyList<ModelEntry>`. Constructor + interface methods all in place. v0.6.21+ task to implement. |

`CivitAiModelSource` ships with:
1. **Unit tests** using `DelegatingHandler` stub HTTP responses (~7 tests: list shape, pagination, nsfw passthrough, kind parsing, version parsing, file extraction, error mapping, empty result, malformed JSON).
2. **One real-fetch integration test** `[Fact(Skip=...)]` (CI does not hit network).

If `https://civitai.com/api/v1/models` returns 401 (auth required), the spec is amended — likely with API-key optional path, mirroring v0.6.5.10's HEAD SHA fallback.

### 6.3 `ModelMarketplaceService` aggregator

```csharp
public class ModelMarketplaceService
{
    private readonly IReadOnlyList<IModelSource> _sources;

    /// <summary>Run all enabled sources in parallel; merge results; dedup by (Source, SourceId).</summary>
    public async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query,
        int maxResultsPerSource,
        CancellationToken ct);
}
```

No persistent cache (G17 / YAGNI). Re-fetched each time user clicks "刷新". UI keeps last-loaded results in memory until next refresh.

### 6.4 `ModelFilesystemScanner`

```csharp
public class ModelFilesystemScanner
{
    public IReadOnlyList<DownloadedModel> Scan(string modelsDir);
}
```

Recursively walks `<modelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/` for `meta.json`. Returns one `DownloadedModel` per found `meta.json`. Subfolders missing `meta.json` skipped with WARN log. Returns empty list if `modelsDir` doesn't exist.

---

## 7. Model Downloader (streaming + progress)

### 7.1 `ModelDownloader` API

```csharp
public class ModelDownloader
{
    /// <summary>Download one version's primary file to target directory.</summary>
    public async Task<ModelDownloadResult> DownloadAsync(
        ModelVersionEntry version,
        string targetDir,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Batch download with SemaphoreSlim(4) concurrency.</summary>
    public async Task<ModelDownloadSummary> DownloadBatchAsync(
        IReadOnlyList<ModelVersionEntry> versions,
        string modelsDir,
        IProgress<string>? log = null,
        CancellationToken ct = default);
}

public class ModelDownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent => TotalBytes.HasValue ? (double)BytesDownloaded / TotalBytes.Value * 100 : 0;
    public string FormatPercent() => $"{Percent:F1}%";
}

public class ModelDownloadResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string? FilePath { get; init; }       // on success
    public long SizeBytes { get; init; }
}

public class ModelDownloadSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public TimeSpan TotalDuration { get; init; }
}
```

### 7.2 Download algorithm

```
DownloadAsync(version, targetDir, progress, ct):
  1. Resolve kind subfolder (KindToComfyUiSubfolder[version.Parent.Kind])
  2. Build path: <modelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/
  3. Resolve collision: if exists, append -1, -2, ... to leaf folder name
  4. Create target dir
  5. Build file path: <leaf>/<primary_filename>
  6. HttpClient.GetAsync(primary_download_url, ResponseHeadersRead, ct)
     - on non-2xx: throw HttpRequestException with status code
     - on Content-Length present: store as TotalBytes
  7. Stream to <file>.partial with CopyToAsync + progress callback
  8. Atomic rename: File.Move(<file>.partial, <file>, overwrite: true)
  9. Write meta.json sidecar
  10. Return ModelDownloadResult { Success: true, FilePath: <file>, SizeBytes }
  On exception: delete <file>.partial if exists, return Fail with reason
```

### 7.3 Progress reporting

For ViewModel `ConsoleLog` (string progress lines per file), `DownloadAsync` also accepts `IProgress<string>? log = null` for batch context (logs "下载中: realisticVision_v5.safetensors (6.3 GB) 45.2%" each second or on percentage milestones). For single-card download (UI per-card progress), a different callback signature could be used — but v0.6.20 uses just the console-line pattern for simplicity, mirroring v0.6.19. Per-card real-time % progress is **out of scope** for v0.6.20.

### 7.4 Batch concurrency

```csharp
public async Task<ModelDownloadSummary> DownloadBatchAsync(versions, modelsDir, log, ct)
{
    var semaphore = new SemaphoreSlim(4);
    var tasks = versions.Select(async v =>
    {
        await semaphore.WaitAsync(ct);
        try
        {
            log?.Report($"[开始] {v.Parent.Title} / {v.Name}");
            var result = await DownloadAsync(v, modelsDir, null, ct);
            if (result.Success)
                log?.Report($"[✓ OK] {v.Name} → {result.FilePath} ({FormatSize(result.SizeBytes)})");
            else
                log?.Report($"[✗ FAIL] {v.Name}: {result.FailureReason}");
            return result;
        }
        finally { semaphore.Release(); }
    });
    var results = await Task.WhenAll(tasks);
    return new ModelDownloadSummary
    {
        Succeeded = results.Count(r => r.Success),
        Failed = results.Count(r => !r.Success),
        TotalBytesDownloaded = results.Where(r => r.Success).Sum(r => r.SizeBytes),
        TotalDuration = stopwatch.Elapsed,
    };
}
```

**Failure tolerance:** one failed version does not abort others. Failures logged with reason; summary returned.

---

## 8. Model Symlinker (env-start sync)

### 8.1 `ModelSymlinker` API

```csharp
public class ModelSymlinker
{
    public async Task<ModelSyncResult> SyncToEnvAsync(
        string envId,
        string envComfyuiSource,
        CancellationToken ct = default);
}

public class ModelSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
```

### 8.2 Sync logic

```
SyncToEnvAsync(envId, envComfyuiSource, ct):
  1. Resolve env.ComfyuiSource. Empty/null → log WARN + return empty result.
  2. Scan Settings.ModelsDirectory → List<DownloadedModel>
  3. Ensure <ComfyuiSource>/models/ exists (create if missing).
  4. For each DownloadedModel dm:
     kind = KindToComfyUiSubfolder[dm.Kind]    // "checkpoints"/"loras"/...
     target = <ModelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/
     link   = <ComfyuiSource>/models/<kind>/<model-slug>-<id8>__<version-slug>-<vid8>
     if Directory.Exists(link) AND JunctionLinker.GetTargetAsync(link, ct) == target:
       Skipped++
     elif Directory.Exists(link):
       Directory.Delete(link) — recreate
       JunctionLinker.CreateAsync(link, target, ct)
       Linked++
     else:
       JunctionLinker.CreateAsync(link, target, ct)
       Linked++
     On failure → Failed++ + log WARN (do NOT throw)
  5. Return result.
```

**Caller (EnvironmentListViewModel.StartAsync):** fire-and-forget with `_ = SyncToEnvAsync(...)` wrapped in try/catch. Adds to existing v0.6.19 workflow symlink hook (runs in parallel via separate `Task.Run`). Both sync operations are independent — neither blocks the other.

---

## 9. UI

### 9.1 Sidebar + `MainViewModel` integration

```csharp
public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    LocalNodes,
    Workflows,   // v0.6.19 (8th position)
    Models,      // v0.6.20 NEW (9th position)
    Settings,
    BulkUpdate,
    SystemStatus
}
```

- 9th sidebar RadioButton "模型市场" → `ShowModelsCommand` → `MainViewModel.ShowModels()`.
- Cached `ModelMarketplaceViewModel` + `ModelMarketplaceView` (same lazy pattern as `ShowCatalog` and `ShowWorkflows`).
- `MainSectionNameProvider.cs` adds mapping: `MainSection.Models => "模型市场"`.
- App.xaml.cs DI: reuses existing HttpClient singleton from v0.6.19; adds `ModelMarketplaceService`, `ModelDownloader`, `ModelFilesystemScanner`, `ModelSymlinker`, `CivitAiModelSource` (full impl), `HuggingFaceModelSource` (stub).

### 9.2 `ModelMarketplaceView` layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ 模型市场   [search: ___________]  [↻ 刷新]  [⛶ 打开目录]  [批量下载 (N)] [全选]│
├──────────────────────────────────────────────────────────────────────┤
│ Kind: [☑ Checkpoint] [☑ LORA] [☑ VAE] [☑ Controlnet] [☑ TI] [☑ Upscaler] [☑ Hyper] [☑ Other] │
│ 源: [☑ CivitAi]  排序: [最新▼]                            共 M 条 / 已下载 D 个       │
├──────────────────────────────────────────────────────────────────────┤
│ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐         │
│ │ ☑ 📷 thumb │ │ ☐ 📷 thumb │ │ ☑ 📷 thumb │ │ ☐ 📷 thumb │   ← 240×280│
│ │  title     │ │  title     │ │  title     │ │  title     │            │
│ │  author    │ │  author    │ │  author    │ │  author    │            │
│ │ [Checkpt]  │ │ [LORA]     │ │ [Checkpt]  │ │ [VAE]      │  kind pill │
│ │ [SFW]      │ │ [Mature]   │ │ [SFW]      │ │ [NSFW]     │  nsfw pill │
│ │ SD 1.5     │ │ SDXL       │ │ Flux       │ │ SD 1.5     │  baseModel │
│ │ ▼ versions │ │ ▼ versions │ │ ▼ versions │ │ ▼ versions │            │
│ │ ☑ v5 fp16  │ │ ☐ v1       │ │ ☑ v1.0     │ │ ☐ 8k-clipped│            │
│ │   6.3GB    │ │   140MB    │ │   23GB     │ │   240MB    │            │
│ │ ☐ v5 fp32  │ │ ☐ v1-pruned│ │            │ │            │            │
│ │   13GB     │ │   70MB     │ │            │ │            │            │
│ │ [⬇ 批量下] │ │ [⬇ 批量下] │ │ [⬇ 批量下] │ │ [⬇ 批量下] │            │
│ └────────────┘ └────────────┘ └────────────┘ └────────────┘         │
│ ...                                                                  │
├──────────────────────────────────────────────────────────────────────┤
│ Console [N 行]                                             [✕ close]  │
│ ┌────────────────────────────────────────────────────────────────┐   │
│ │ [开始] Realistic Vision v5 / v5.0 fp16                          │  │
│ │ [✓ OK] v5.0 fp16 → models/checkpoints/realistic-vision-.../    │  │
│ │       v50-fp16-12345678/realisticVision_v5.safetensors (6.3GB) │   │
│ │ [开始] Realistic Vision v5 / v5.0 fp32                          │  │
│ │ [✗ FAIL] v5.0 fp32: HTTP 404 Not Found                          │  │
│ │ [批量下载完成] 成功 1 / 失败 1                                    │  │
│ └────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
```

Components:
- **Top toolbar** — title + search box + 全选 button + 批量下载 button (shows count badge) + 刷新 button + 打开目录 button.
- **Filter strip** — kind chip row (8 `CheckBox` chips: Checkpoint/LORA/VAE/Controlnet/TI/Upscaler/Hypernetwork/Other), source chip (1 for v0.6.20: CivitAi), sort dropdown (Newest/Downloads/Name), counts pushed right (共 M 条 / 已下载 D 个).
- **Card grid** — `ItemsControl` with `WrapPanel` (240×280 cards, 16px gutter). Each card = `Border` with `Background=SurfaceBrush`:
  - **Top-left checkbox** — toggles all versions of this model
  - **Thumbnail** (Uniform stretch, SurfaceVariantBrush letterbox fill, 240×140)
  - **Title** (13pt semibold, max 2 lines ellipsis)
  - **Author** (11pt gray)
  - **Kind pill + NSFW pill** (10pt, kind on left in kind-specific color, NSFW on right per G6 colors)
  - **BaseModel** (11pt gray, e.g. "SD 1.5", "SDXL", "Flux")
  - **Versions section** (collapsible, expanded by default; each version row = checkbox + name + size in MB/GB)
  - **Bottom action button** — "批量下载" / per-version checkbox auto-adds to SelectedVersions
- **Console panel** — same as v0.6.19: SurfaceBrush + OutlineBrush + CornerRadius 6 + DockPanel title + ✕ + ScrollViewer Height 160 + Consolas 11pt NoWrap ItemsControl. Auto-scroll via `CollectionChanged`. Three-state visibility: `!userHidden && (IsBusy || ConsoleLog.Count > 0)`.
- **Info / Error strip** — same pattern as Catalog.

### 9.3 `ModelMarketplaceViewModel` shape

```csharp
public class ModelMarketplaceViewModel : ViewModelBase
{
    // Inputs
    public string SearchText { get; set; } = "";
    public ObservableCollection<ModelKind> ActiveKindFilters { get; } = new();   // default: all
    public ObservableCollection<ModelSourceKind> ActiveSourceFilters { get; } = new();  // default: CivitAi
    public ModelSortKind SortBy { get; set; } = ModelSortKind.Newest;

    // Output (filtered view)
    public ObservableCollection<ModelEntry> Models { get; } = new();
    public int TotalCount { get; private set; }
    public int DownloadedCount { get; private set; }

    // Selection (per-version granularity)
    public ObservableCollection<ModelVersionEntry> SelectedVersions { get; } = new();
    public bool HasSelection => SelectedVersions.Count > 0;
    public int SelectedCount => SelectedVersions.Count;

    // Console
    public ObservableCollection<string> ConsoleLog { get; } = new();
    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);
    public bool IsBusy { get; private set; }
    public bool IsEmpty => !IsBusy && Models.Count == 0 && ErrorMessage is null;
    public bool NotIsBusy => !IsBusy;
    public string? ErrorMessage { get; private set; }
    public string? InfoMessage { get; private set; }

    // Commands
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }       // toggles all versions across all cards
    public RelayCommand BatchDownloadCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand DownloadSingleCommand { get; }       // parameter: ModelVersionEntry
    public RelayCommand ToggleModelVersionsCommand { get; }  // parameter: ModelEntry (toggle all versions of one model)
}
```

Filter/sort logic runs in-memory on `Models` collection; debounced 250ms on `SearchText` changes.

**ToggleSelectAllCommand** behavior:
- If `SelectedVersions.Count == Sum of all versions across Models`: clear all selections
- Else: add all versions of all currently filtered models to SelectedVersions

### 9.4 Settings UI section

Append a new "模型市场" section to `SettingsView.xaml`, after "工作流市场" section:

```
─── 模型市场 ───
  Models Directory:  [<path>] [Browse]

  数据源:
    [☑] CivitAi
```

3 fields only — YAGNI.

### 9.5 New converters (XAML)

| Converter | Input | Output | Used for |
|---|---|---|---|
| `ModelNsfwBadgeBrush` | `ModelNsfwKind` enum | `SolidColorBrush` (OutlineBrush for SFW, WarningBrush for Mature, ErrorBrush for NSFW) | NSFW pill background |
| `ModelNsfwBadgeText` | `ModelNsfwKind` enum | string ("SFW"/"Mature"/"NSFW") | NSFW pill text |
| `ModelKindBadgeBrush` | `ModelKind` enum | `SolidColorBrush` (kind-specific color: Checkpoint=purple, LORA=blue, VAE=cyan, Controlnet=teal, TI=green, Upscaler=orange, Hypernetwork=red, Other/Unknown=gray) | Kind pill background |

---

## 10. env-startup integration

`ModelSymlinker.SyncToEnv(envId)` is called from `EnvironmentListViewModel.StartAsync` after env reaches "running" status, alongside the existing v0.6.19 `WorkflowSymlinker.SyncToEnvAsync` call. Both run as fire-and-forget `Task.Run` after env-start; neither blocks the other.

```csharp
// In EnvironmentListViewModel.StartAsync after env reaches "running":
_ = Task.Run(async () =>
{
    try
    {
        await _modelSymlinker?.SyncToEnvAsync(env.Id, env.ComfyuiSource ?? "", default);
    }
    catch (Exception ex)
    {
        _logger?.Error("env-start", $"model symlink sync failed for env '{env.Id}': {ex.Message}");
    }
});
```

Implementation: same fire-and-forget pattern as v0.6.19 WorkflowSymlinker. Symlinker returns `ModelSyncResult`; failure logged but does not propagate.

---

## 11. Testing strategy

| Layer | Test type | Count | Coverage |
|---|---|---|---|
| `ModelFilesystemScanner` | Unit | 4–5 | scan empty dir, scan with N model/version subfolders, skip subfolder without `meta.json`, parse `meta.json` malformed → skip, dedup |
| `IModelSource` mock (used by all VMs) | Unit | 2 | returns canned entries, simulates timeout |
| `CivitAiModelSource` | Unit (HTTP mocked) | 6–8 | list shape, nsfw passthrough, kind parsing (each enum value), version parsing, file extraction, error mapping, empty result, malformed JSON |
| `CivitAiModelSource` | Real-fetch | 1 (SKIP) | confirms endpoint still public at impl time |
| `HuggingFaceModelSource` (stub) | Unit | 2 | returns empty list, respects IsEnabled flag |
| `ModelMarketplaceService` | Unit | 4–5 | aggregate 1 source (CivitAi), dedup by (source,id), parallel via `Task.WhenAll`, disabled source skipped, partial failure tolerated |
| `ModelDownloader` | Unit | 6–8 | single download OK, streaming + progress callback (verify % increments), batch with SemaphoreSlim=4 concurrency, atomic rename (`.partial` → final), subfolder collision → suffix, primary file extraction (skip non-primary), HTTP 404 → fail-soft |
| `ModelDownloader` progress | Unit | 2 | progress callback fires with monotonic BytesDownloaded, TotalBytes matches Content-Length |
| `ModelSymlinker` | Unit | 4–5 | sync OK with N versions, skip already-correct junction, recreate broken junction, env.ComfyuiSource empty → return null result, fail-soft on 1 junction failure |
| `ModelMarketplaceViewModel` | Unit | 8–10 | filter intersect (text+kind+source), sort by Newest/Downloads/Name, 全选/反选 all versions, per-version checkbox toggle, refresh triggers fetch, scan derived "已下载" badges, IsEmpty computes correctly, console 3-state visibility |
| `ModelMarketplaceView` | STA load | 3 | dark theme render, light theme render, console panel render |
| `MainViewModel` integration | Unit | 3 | sidebar nav, lazy VM cache, env-start hooks fire |

**Total target:** ~45–55 new tests. Project baseline (post v0.6.19.x) = 1421 / 0 FAIL / 4 SKIP (assuming v0.6.19 ships at end); target post-SDD = **~1470 / 0 FAIL / 5 SKIP** (baseline 4 + 1 new CivitAI real-fetch).

---

## 12. Error handling

| Failure | Behavior |
|---|---|
| Source HTTP fail / timeout | Other sources continue; failed source omitted from result set + log `model-<source>` ERROR. UI shows ErrorBanner summary. |
| Source 401/403 (auth) | Log WARN with hint ("CivitAI may rate-limit anonymous access; try later"). Other sources still displayed. |
| Source 429 (rate limit) | Log WARN. Omitted from result. Other sources still displayed. |
| Aggregator: source fails | Show entries from successful sources. ErrorBanner lists failed source name. |
| Aggregator: ALL sources fail | Show empty state + ErrorBanner "all sources unavailable, retry". |
| Download: HTTP 404 for primary file | Skip version, log ERROR, continue batch. Other versions still download. |
| Download: disk full / perms | Log ERROR, delete `.partial`, skip version, continue batch. |
| Download: HTTP 416 (Range not satisfiable — unlikely) | Treat as failure, retry from scratch. |
| Download: subfolder name collision | Append `-1`, `-2`, etc., to version folder name. |
| Download: streaming connection drops mid-file | Delete `.partial`, return failure. No resume in v0.6.20 (YAGNI). |
| ModelSymlinker: 1 junction fail | Log WARN, Failed++, continue. Other junctions still synced. |
| ModelSymlinker: env.ComfyuiSource empty | Log WARN, return empty result. Caller no-ops. |
| `ModelsDirectory` empty / unset | Download disabled with banner hint "configure Models Directory in Settings". View still browsable. |
| `ModelsDirectory` set but does not exist | Auto-create on first scan/download attempt. Failure surfaces to ErrorBanner. |
| `meta.json` malformed in a subfolder | Scanner skips + logs WARN. UI doesn't crash. |
| NSFW level > 3 (CivitAI legacy) | Cap at NSFW (no error). |
| CivitAI returns no `modelVersions` for a model | Skip entry in version enumeration; entry still appears in card but with 0 versions → user cannot select. |

**Logger subsystems:**
- `model-marketplace` — aggregator (per-source load + count)
- `model-download` — per-file download (URL, bytes, result, % milestones)
- `model-symlink` — env-start sync (count + errors)
- `model-civitai` — CivitAI HTTP calls, errors
- `model-huggingface` — HF stub (always logs "v0.6.20 stub returns empty")

---

## 13. Implementation task breakdown

~10 tasks, single SDD:

| Task | Files | Notes |
|---|---|---|
| **T1** | Settings shape + UI section | Add `ModelsDirectory` + `ModelSourceCivitAiEnabled` to `Settings.cs` + `CopyInto`; SettingsViewModel + SettingsView.xaml "模型市场" section (2 fields + Browse + OpenFolder button); `SettingsDefaults.Apply` resolves default path |
| **T2** | `ModelEntry` + `ModelVersionEntry` + `ModelFile` + `DownloadedModel` + `ModelFilesystemScanner` | `Models/ModelEntry.cs`, `Models/ModelKind.cs`, `Models/ModelNsfwKind.cs`, `Models/ModelSourceKind.cs`, `Services/ModelFilesystemScanner.cs`; 5 unit tests |
| **T3** | `IModelSource` interface + `CivitAiModelSource` + `HuggingFaceModelSource` stub | `Services/ModelSource/IModelSource.cs`, `CivitAiModelSource.cs`, `HuggingFaceModelSource.cs` (stub); 7 tests + 1 SKIP real-fetch + 2 stub tests |
| **T4** | `ModelMarketplaceService` aggregator | `Services/ModelMarketplaceService.cs`; parallel via `Task.WhenAll`, dedup, partial-failure tolerance; 5 tests |
| **T5** | `ModelDownloader` (streaming + progress + batch) | `Services/ModelDownloader.cs`; streaming with `ResponseHeadersRead`, atomic rename, batch with `SemaphoreSlim(4)`; 8 tests (incl. 2 progress tests) |
| **T6** | `ModelSymlinker` (junction via existing `JunctionLinker` + symlink via `Directory.CreateSymbolicLink`) | `Services/ModelSymlinker.cs`; 5 tests |
| **T7** | 3 converters (`ModelNsfwBadgeBrush`, `ModelNsfwBadgeText`, `ModelKindBadgeBrush`) + Theme.xaml registration | `Views/Converters.cs` additions; mirror v0.6.19 `WorkflowSourceBadge*` |
| **T8** | `ModelMarketplaceViewModel` + `ModelMarketplaceView` XAML | VM: filter/sort/multi-version-select/console/refresh/batch-download. XAML: filter strip + 240×280 cards with version list + console panel. ~10 tests + 3 STA load tests |
| **T9** | MainViewModel + MainWindow integration + env-start hook + App.xaml.cs DI | `MainSection.Models` enum value + 9th sidebar button + `ShowModelsCommand` + `MainSectionNameProvider` mapping; `EnvironmentListViewModel.StartAsync` hook fires `_modelSymlinker.SyncToEnvAsync` fire-and-forget; App.xaml.cs DI for ModelMarketplaceService/ModelDownloader/ModelSymlinker/CivitAiModelSource/HuggingFaceModelSource (stub); reuses HttpClient singleton from v0.6.19 |
| **T10** | Final review + MEMORY + staging rebuild | opus final review, MEMORY entry, MEMORY.md index update, GUI smoke list, fix wave if needed |

**Total:** ~16–20 files (3–4 model + 1 settings + 1 scanner + 1 interface + 2 sources + 1 service + 1 downloader + 1 symlinker + 3 converters + 2 vm/view + 1 test seam + 2 settings ui + 1 mainviewmodel wiring + 1 DI update), ~45–55 tests, ~900–1300 LoC.

---

## 14. Risks

| Risk | Mitigation |
|---|---|
| CivitAI rate-limits anonymous access (60 req/hour) | Acceptable for v0.6.20 — users browse once, cache in memory. Add API key support in v0.6.21 if needed. |
| CivitAI changes API schema (breaking change) | One source failing doesn't block others (interface reserved). Spec amended mid-impl. |
| Models are GB-scale — slow downloads on slow connections | Streaming + progress % gives user feedback. Auto-resume is v0.6.21+ (YAGNI for v0.6.20). |
| Disk space exhaustion mid-batch | Per-version try/catch; remaining versions continue. UI shows error. |
| CivitAI modelVersion `files[]` contains multiple files (e.g. model + VAE bundled) | v0.6.20 downloads only primary file. Multi-file bundle support deferred to v0.6.21+. |
| NSFW content visible without opt-in | User explicitly chose "always display" + badge. No UI toggle (YAGNI). |
| HuggingFace stub confuses users ("HF doesn't work?") | Disabled by default in Settings (`ModelSourceHuggingFaceEnabled = false`, not exposed yet). When implemented, list shows empty. UI doesn't show "HF" badge source until enabled. |
| Subfolder name collision (multiple models with same slug + id8 prefix) | Collision suffix `-1`, `-2` resolves deterministically. |
| Env-side symlink path collisions (model-slug + version-slug similar) | Double underscore `__` separator between model and version in env link name prevents collision. |
| Symlink target path changes between sessions (user reorganizes `<ModelsDir>`) | Sync detects mismatch and recreates junction. |
| env.ComfyuiSource empty (no env-start yet) | Symlinker returns empty result, logs WARN, caller no-ops. |
| Sync runs at every env-start = redundant junction creates | Sync is idempotent — already-correct junctions skipped. |
| `meta.json` parse fails for malformed entries | Skip with WARN, don't include in results (graceful degradation). |
| Card grid rendering 200+ model cards = UI jank | WrapPanel handles horizontal flow; Window scroll. Acceptable for v1. |
| CivitAI returns model with no modelVersions | Card shows with empty version list, no checkbox interactivity. Logged as warn by source parser. |
| Wrong `Kind` for legacy CivitAI types (e.g. "MotionModule") | Falls into `Other`. Doesn't break. UI shows "Other" pill. |

---

## 15. Open questions (none blocking)

None. All architectural decisions are locked in this spec.

---

## 16. References

- Project memory: `docs/superpowers/specs/2026-08-18-workflow-marketplace-design.md` (v0.6.19 — direct architectural mirror; reuse IWorkflowSource pattern as IModelSource)
- Project memory: `project_v0_6_18_4_bulk_update_console.md` (v0.6.18.4 Console panel pattern reused for download progress)
- Project memory: `project_env_start_status.md` (env-start status panel pattern, mirrored for Console)
- Project memory: `project_catalog_local_download.md` (v0.6.5.9 Catalog local-download → similar `<LocalNodeDirectory>` filesystem-derived state pattern reused for `<ModelsDirectory>`)
- Project memory: `project_v0_6_18_3_dispatcher_env_warning.md` (Dispatcher threading — `Application.Current?.Dispatcher` pattern reused for VM collection mutations)
- `Models/Settings.cs` (existing pattern: `[JsonPropertyName("...")]` + `CopyInto` row)
- `Infrastructure/JunctionLinker.cs` (existing Windows junction helper)
- `Infrastructure/LocalDataPaths.cs` (existing projectRoot/.manager/ path helper)
- `Services/CatalogFetcher.cs` (existing JSON fetcher pattern — generic JSON parsing + native type conversion)
- `Services/WorkflowMarketplace/WorkflowMarketplaceService.cs` (v0.6.19 — direct template for aggregator)
- `Services/WorkflowMarketplace/WorkflowDownloader.cs` (v0.6.19 — direct template for downloader; v0.6.20 replaces simple GET with streaming + atomic rename)
- `Services/WorkflowMarketplace/WorkflowSymlinker.cs` (v0.6.19 — direct template for symlinker)
- `WpfTestResources.EnsureLoaded` (v0.6.9.3 STA-load helper) — pattern for `ModelMarketplaceViewLoadTests`
- `LiveGitHubVersionFetchTests` — pattern for real-fetch `[SKIP]`-able integration tests
- `IBrowserLauncher` (v0.6.10 T2) — used for "打开模型目录" button
- CivitAI Models API: `https://civitai.com/api/v1/models?limit=100&page={n}&nsfw=true&sort=Newest`