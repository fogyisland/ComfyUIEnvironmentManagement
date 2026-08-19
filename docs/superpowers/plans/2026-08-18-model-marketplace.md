# Model Marketplace v0.6.20 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "模型市场" sidebar section that lists CivitAI models (multi-version per card, kind filter chips, NSFW badge pills), streams GB-scale downloads with progress %, and auto-syncs them to running envs via per-version junction/symlink at env-start.

**Architecture:** New `MainSection.Models` sidebar entry (9th position). Pluggable `IModelSource` interface with 1 concrete fetcher `CivitAiModelSource` (uses `https://civitai.com/api/v1/models` endpoint, paginated, nsfw=true) + `HuggingFaceModelSource` stub (returns empty list, v0.6.21+ impl). Aggregator `ModelMarketplaceService` parallel via `Task.WhenAll` with dedup by `(Source, SourceId)`. Selection granularity is **per-version** via `SelectedVersions: ObservableCollection<ModelVersionEntry>` (a card's master checkbox toggles all versions; each version has its own checkbox). Download state is filesystem-derived (scan `Settings.ModelsDirectory`). `ModelDownloader` streams with `HttpCompletionOption.ResponseHeadersRead` + manual `CopyToAsync` progress callback, writes `<file>.partial` then atomic `File.Move(overwrite: true)`. Batch with `SemaphoreSlim(4)`. `ModelSymlinker.SyncToEnv` runs fire-and-forget after env-start via existing `JunctionLinker` (Windows) + `Directory.CreateSymbolicLink` (Linux/macOS), creates per-version junctions at `<env>/models/<kind>/<model-slug>-<id8>__<version-slug>-<vid8>` → `<projectRoot>/models/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/`. Console panel mirrors v0.6.18.4 + v0.6.19 pattern. NSFW badge pill (`ModelNsfwBadgeBrush/Text`) and kind badge pill (`ModelKindBadgeBrush`) converters added to `Views/Converters.cs`.

**Tech Stack:** .NET 8 / WPF / C# 12 / SQLite / xUnit / Moq / `HttpClient` (singleton in `App.xaml.cs`, reused from v0.6.19) / `JunctionLinker` (existing) / `Progress<T>` (long-running → UI thread marshal)

**Spec:** `docs/superpowers/specs/2026-08-18-model-marketplace-design.md`

**Base branch:** main at `4e4bf7b` (v0.6.20 spec committed, on top of v0.6.19.x hotfix `a8a47bf`).

## Global Constraints

- Test baseline `1421 PASS / 0 FAIL / 4 SKIP` (post v0.6.19.x hotfix); target post-SDD `~1470 PASS / 0 FAIL / 5 SKIP` (1 new CivitAI real-fetch `[SKIP]` test added)
- All path fields follow `SettingsDefaults.Resolve(...)` pattern (template-style: empty → default subdir name; relative paths preserved; absolute paths under `projectRoot` migrated to relative)
- All new `bool` / enum bindings use existing converters registered in `Resources/Theme.xaml` (`BoolToVisibility` / `NullToVisibility` / `SectionEquality` / `EnumEqualsConverter` / `WorkflowSourceBadge*` for v0.6.19 reference). New converters `ModelNsfwBadgeBrush` / `ModelNsfwBadgeText` / `ModelKindBadgeBrush` are added in T7 and registered in `Theme.xaml` static resources
- Sidebar RadioButton follows MainWindow.xaml pattern (`Style="{StaticResource SidebarRadioButtonStyle}"`)
- AppLogger subsystem strings: `model-marketplace`, `model-download`, `model-symlink`, `model-civitai`, `model-huggingface`
- Settings plumbing: `[JsonPropertyName("...")] public T X { get; set; } = default;` + matching row in `CopyInto(target, source)`
- 9th sidebar position (`MainSection.Models`) between `Workflows` (v0.6.19 8th) and `Settings`
- Env-start hook: `EnvironmentListViewModel.StartAsync` adds `_modelSymlinker?.SyncToEnvAsync(envId, env.ComfyuiSource, ct)` as second fire-and-forget after the existing v0.6.19 workflow symlink hook; failure logged but does NOT propagate. Both hooks run as independent `Task.Run` after env-start completes
- Storage path: `<Settings.ModelsDirectory>/<kind-subfolder>/<model-slug>-<id8>/<version-slug>-<vid8>/<primary-filename>.<ext>` + `meta.json` sidecar
- Env-side junction: `<env.ComfyuiSource>/models/<kind-subfolder>/<model-slug>-<id8>__<version-slug>-<vid8>` → `<Settings.ModelsDirectory>/<kind-subfolder>/<model-slug>-<id8>/<version-slug>-<vid8>/` (double underscore `__` separates model from version to prevent collisions)
- GB-scale streaming: `HttpCompletionOption.ResponseHeadersRead` + manual `CopyToAsync(stream, buffer, callback)` + atomic `File.Move(<file>.partial, <file>, overwrite: true)` on success
- Per-version selection: `SelectedVersions: ObservableCollection<ModelVersionEntry>` (NOT `Selected: ObservableCollection<ModelEntry>`) — granularity is the version, not the model
- NSFW policy: always show all content (no filter, no UI toggle); badge pill (SFW=gray OutlineBrush / Mature=WarningBrush / NSFW=ErrorBrush)
- Kind classification: 8 values (`Checkpoint` / `LORA` / `VAE` / `Controlnet` / `TextualInversion` / `Upscaler` / `Hypernetwork` / `Other`), drives both filter chips AND storage subfolder via `KindToComfyUiSubfolder` Dictionary (publicly exposed via `ModelEntry` or `ModelKindExtensions` static class)
- Slug generation: lowercase, replace non-`[a-z0-9-]` with `-`, collapse repeated `-`, trim. 8-char ID = first 8 chars of source ID (pad if short)
- Collision handling: version folder already exists → append `-1`, `-2`, ... to leaf folder name. Version folders NEVER overwrite (3 versions of one model = 3 distinct subdirs)
- HuggingFace is a stub `IModelSource` implementation that returns `Array.Empty<ModelEntry>()` for v0.6.20 — interface + DI registration reserved for v0.6.21+ impl
- Real-fetch tests use `[Fact(Skip = "...")]` with descriptive reason (CI does not hit network)
- Commits: scoped per task (`git add <specific paths>` whitelist); no bundled WIP
- 中文 UI copy: "模型市场" / "搜索" / "Kind" / "源" / "排序" / "批量下载" / "全选" / "打开目录" / "刷新" / "Console" / "已下载" / "加载中…" / "未找到匹配模型"
- YAGNI: no SQLite cache, no API keys, no resume (HTTP Range), no pagination UI, no 我的下载 view, no multi-file bundle, no FTS5, no HF impl
- v-bump skipped (user decides); no release zip; staging rebuild at end (blocked by user's PID 14212 until they close staging exe)
- Tests live under `tests-wpf/ComfyUI.Manager.Tests/Services/`, `/ViewModels/`, or `/Views/` mirroring production folder structure
- All temp files in tests: `Path.Combine(Path.GetTempPath(), "ComfyUIMgr<Name>_" + Guid.NewGuid().ToString("N"))` + cleanup in `Dispose`
- DelegatingHandler pattern for HTTP mocking (existing project pattern, see `WorkflowSourceCommunityJsonTests` for template)
- VM UI-bound awaits must NOT use `.ConfigureAwait(false)` (per `feedback_configureawait_false_placement.md`) — service layer internal awaits may use it for thread pool efficiency
- IProgress<T> implementations must be wrapped in `new Progress<T>(...)` constructed on UI thread (per `feedback_wpf_observablecollection_progress.md`) so Report marshals back to UI thread

## Files to Touch

### New files

| Path | Purpose |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/ModelEntry.cs` | Aggregate model + ModelSourceKind/ModelKind/ModelNsfwKind enums + ModelVersionEntry + ModelFile + DownloadedModel + meta.json shape |
| `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` | Scan `Settings.ModelsDirectory` recursively → `List<DownloadedModel>` |
| `src-wpf/ComfyUI.Manager/Services/ModelSources/IModelSource.cs` | Interface (SourceKind / DisplayName / IsEnabled / SearchAsync) |
| `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs` | `/api/v1/models` fetcher with nsfw=true + kind parsing + version parsing |
| `src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs` | Stub returning empty list (v0.6.21+ impl) |
| `src-wpf/ComfyUI.Manager/Services/ModelMarketplaceService.cs` | Parallel aggregator with dedup |
| `src-wpf/ComfyUI.Manager/Services/ModelDownloader.cs` | Streaming single + batch (SemaphoreSlim=4) with progress + atomic rename |
| `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs` | Env-start per-version junction/symlink sync |
| `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs` | Filter / sort / per-version multi-select / console / refresh / batch |
| `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml` + `.xaml.cs` | Sidebar-section view (240×280 cards with version list) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerTests.cs` | Scanner unit tests (~5) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs` | CivitAI unit (~7) + 1 SKIP real-fetch |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs` | HF stub tests (~2) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelMarketplaceServiceTests.cs` | Aggregator unit (~5) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelDownloaderTests.cs` | Single + batch + progress + atomic rename (~8) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTests.cs` | Sync logic (~5) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs` | VM filter / sort / per-version multi-select / console (~10) |
| `tests-wpf/ComfyUI.Manager.Tests/Views/ModelMarketplaceViewLoadTests.cs` | STA load dark + light + console panel (~3) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelModelsSectionTests.cs` | Sidebar nav + lazy VM cache + env-start hook (~3) |

### Modified files

| Path | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | Add `ModelsDirectory` + `ModelSourceCivitAiEnabled` + CopyInto rows |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | Add `ModelsSubdir = "models"` const + Apply `s.ModelsDirectory = Resolve(...)` |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | Add 2 new properties (ModelsDirectory + ModelSourceCivitAiEnabled) with MarkDirty |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | Add "模型市场" section after "工作流市场" section |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` | Add `BrowseModelsDir` + `OpenModelsDir` handlers |
| `src-wpf/ComfyUI.Manager/Views/Converters.cs` | Add `ModelNsfwBadgeBrush`, `ModelNsfwBadgeText`, `ModelKindBadgeBrush` converters |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | Register 3 new converters as static resources |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | Add `MainSection.Models` enum value, `ShowModelsCommand`, lazy `ModelMarketplaceViewModel` cache |
| `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs` | Map `Models → "模型市场"` |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Add 9th sidebar RadioButton "模型市场" |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | DI for `ModelMarketplaceService` / `ModelDownloader` / `ModelFilesystemScanner` / `ModelSymlinker` / `CivitAiModelSource` / `HuggingFaceModelSource` |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Add `ModelSymlinker?` ctor param + fire-and-forget `SyncToEnvAsync` after successful env-start (alongside existing workflow symlinker hook) |

---

## Task 1: Settings shape + SettingsViewModel bindings + SettingsView XAML + default path resolution

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:55-65` (add 2 fields after v0.6.19 workflow fields)
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:154-157` (add 2 CopyInto rows)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:39` (add `ModelsSubdir` const)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:69-71` (Apply adds `s.ModelsDirectory = Resolve(...)` line)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (add 2 properties + Dirty map entries — mirror v0.6.19 `WorkflowsDirectory` pattern at ~line 487)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (add "模型市场" section after "工作流市场" section — mirror the v0.6.19 section structure)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs:152-161` (add `BrowseModelsDir` + `OpenModelsDir` handlers after `OpenWorkflowsDir`)

**Interfaces:**
- Consumes: existing `Settings` + `SettingsDefaults.Resolve(...)` + `SettingsViewModel` MarkDirty pattern (`WorkflowsDirectory` at ~line 487) + `PickFolder()` helper
- Produces:
  - `Settings.ModelsDirectory : string` (default `"models"` resolved via `SettingsDefaults`)
  - `Settings.ModelSourceCivitAiEnabled : bool = true`
  - 2 `SettingsViewModel` properties mirroring `WorkflowsDirectory` setter pattern
  - 1 new XAML section "模型市场" with 1 path picker + 1 checkbox

- [ ] **Step 1: Add Settings fields + CopyInto rows**

In `Models/Settings.cs`, after the v0.6.19 workflow fields block (around line 60-65), add:

```csharp
// v0.6.20:模型市场 — 共享 models 目录 + CivitAI source enabled bool
[JsonPropertyName("models_directory")]
public string ModelsDirectory { get; set; } = "";
[JsonPropertyName("model_source_civitai_enabled")]
public bool ModelSourceCivitAiEnabled { get; set; } = true;
```

In `CopyInto(target, source)` (around line 154, after the v0.6.19 workflow CopyInto rows), add 2 rows:

```csharp
target.ModelsDirectory = source.ModelsDirectory;
target.ModelSourceCivitAiEnabled = source.ModelSourceCivitAiEnabled;
```

- [ ] **Step 2: Add `ModelsSubdir` const + Apply line**

In `Infrastructure/SettingsDefaults.cs`, in the const block (around line 39, after `WorkflowsSubdir`), add:

```csharp
public const string ModelsSubdir = "models";
```

In `Apply(s, projectRoot)` (around line 69-71, after the `s.WorkflowsDirectory = Resolve(...)` line), add:

```csharp
// v0.6.20:ModelsDirectory — template-style,空字段自动填 "models" 子目录名
s.ModelsDirectory = Resolve(s.ModelsDirectory, ModelsSubdir, projectRoot);
```

- [ ] **Step 3: Add `SettingsViewModel` properties + Dirty entries**

In `ViewModels/SettingsViewModel.cs`, after the v0.6.19 `WorkflowsDirectory` property block (around line 487), add:

```csharp
// v0.6.20:模型市场
public string ModelsDirectory
{
    get => _settings.ModelsDirectory;
    set
    {
        var v = value ?? "";
        if (_settings.ModelsDirectory == v) return;
        _settings.ModelsDirectory = v;
        MarkDirty(nameof(ModelsDirectory));
        RaisePropertyChanged();
    }
}

public bool ModelSourceCivitAiEnabled
{
    get => _settings.ModelSourceCivitAiEnabled;
    set
    {
        if (_settings.ModelSourceCivitAiEnabled == value) return;
        _settings.ModelSourceCivitAiEnabled = value;
        MarkDirty(nameof(ModelSourceCivitAiEnabled));
        RaisePropertyChanged();
    }
}
```

In the `RefreshFromSettings` / `MarkCleanAll`-style method (search for `WorkflowsDirectory` `RaisePropertyChanged` pattern), add 2 corresponding calls.

- [ ] **Step 4: Add SettingsView XAML section**

In `Views/SettingsView.xaml`, after the v0.6.19 "工作流市场" section closes (search for the close `</Border>` of that section, around line 491), add a new `<Border>` with title "模型市场" and:

- 1 TextBox for `ModelsDirectory` (same style as `WorkflowsDirectory`)
- 1 "Browse" button (`Click="BrowseModelsDir"`)
- 1 "打开目录" button (`Click="OpenModelsDir"`)
- 1 CheckBox for `ModelSourceCivitAiEnabled` with label "CivitAi"
- 2 Dirty indicators (`Visibility="{Binding Dirty[ModelsDirectory], Converter={StaticResource BoolToVisibility}}"`, etc.)

Match the v0.6.19 workflow section XAML exactly (Grid columns, control styles, spacing) for visual consistency. Use the `Dirty` dictionary binding pattern shown for `WorkflowsDirectory`.

- [ ] **Step 5: Add code-behind handlers in SettingsView.xaml.cs**

In `Views/SettingsView.xaml.cs`, after `OpenWorkflowsDir` (around line 151), add 2 methods:

```csharp
private void BrowseModelsDir(object sender, RoutedEventArgs e)
{
    if (DataContext is SettingsViewModel vm)
    {
        var picked = vm.PickFolder();
        if (picked is not null) vm.ModelsDirectory = picked;
    }
}

private void OpenModelsDir(object sender, RoutedEventArgs e)
{
    if (DataContext is not SettingsViewModel vm) return;
    var raw = vm.ModelsDirectory;
    if (string.IsNullOrWhiteSpace(raw)) return;
    // ModelsDirectory 可为相对子目录名(如 "models"),以 AppContext.BaseDirectory 解绝对
    var path = Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    try
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }
    catch
    {
        // 失败静默 — 用户用 "浏览..." 按钮 + 自己打开 explorer 也行
    }
}
```

- [ ] **Step 6: Build + verify**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -20`
Expected: 0 errors. Warnings about missing resource keys are acceptable; build SUCCESS overall.

- [ ] **Step 7: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Models/Settings.cs \
  src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
  src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
  src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
  src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs
git commit -m "feat(models): v0.6.20 T1 Settings shape + UI section"
```

---

## Task 2: ModelEntry + ModelVersionEntry + ModelFile + DownloadedModel + ModelFilesystemScanner

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/ModelEntry.cs` (aggregate model + 3 enums + ModelVersionEntry + ModelFile + DownloadedModel + meta sidecar)
- Create: `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (recursive scan + read meta.json + filter by `meta.json` validity)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerTests.cs` (5 tests)

**Interfaces:**
- Consumes: `JsonSerializer` (System.Text.Json, built-in), `JsonNamingPolicy.CamelCase`, `System.IO` recursive enumeration
- Produces:
  - `ModelEntry` (Source, SourceId, SourceUrl, Title, Description?, Author?, AuthorUrl?, Kind, BaseModel?, NsfwKind, NsfwLevel?, DownloadCount?, RatingCount?, RatingStars?, PublishedAt?, Tags IReadOnlyList<string>, PreviewImageUrl?, Versions IReadOnlyList<ModelVersionEntry>)
  - `ModelSourceKind` enum: `CivitAi = 0`
  - `ModelKind` enum: `Unknown = 0, Checkpoint, LORA, VAE, Controlnet, TextualInversion, Upscaler, Hypernetwork, Other` (8 values, `Other = 8`)
  - `ModelNsfwKind` enum: `SFW = 0, Mature, NSFW` (3 values)
  - `ModelVersionEntry` (Id=`"{SourceKind}:{SourceId}:{SourceVersionId}"`, Parent, SourceVersionId, Name, BaseModel?, SizeBytes, PrimaryDownloadUrl, Files IReadOnlyList<ModelFile>, PublishedAt?, IsEarlyAccess)
  - `ModelFile` (Name, Format, SizeBytes, DownloadUrl, IsPrimary)
  - `DownloadedModel` (SubfolderName, FullPath, Kind, Title?, Source, SourceId, SourceVersionId, DownloadedAt)
  - `ModelFilesystemScanner.Scan(modelsDir) → IReadOnlyList<DownloadedModel>`

- [ ] **Step 1: Write `ModelEntry.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>v0.6.20:模型市场聚合模型 — 来自任意 source 的单条 model 记录。
 1 个 ModelEntry = 1 张卡片,内含所有 ModelVersions(per-version checkbox 多选)。</summary>
public class ModelEntry
{
    [JsonPropertyName("source")] public ModelSourceKind Source { get; init; }
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = "";        // CivitAI model id
    [JsonPropertyName("source_url")] public string SourceUrl { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("author_url")] public string? AuthorUrl { get; init; }
    [JsonPropertyName("kind")] public ModelKind Kind { get; init; }                     // parsed from "type"
    [JsonPropertyName("base_model")] public string? BaseModel { get; init; }
    [JsonPropertyName("nsfw_kind")] public ModelNsfwKind NsfwKind { get; init; }         // parsed from nsfwLevel
    [JsonPropertyName("nsfw_level")] public int? NsfwLevel { get; init; }
    [JsonPropertyName("download_count")] public int? DownloadCount { get; init; }
    [JsonPropertyName("rating_count")] public int? RatingCount { get; init; }
    [JsonPropertyName("rating_stars")] public double? RatingStars { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    [JsonPropertyName("preview_image_url")] public string? PreviewImageUrl { get; init; }
    [JsonPropertyName("versions")] public IReadOnlyList<ModelVersionEntry> Versions { get; init; } = Array.Empty<ModelVersionEntry>();
}

public enum ModelSourceKind { CivitAi = 0 }

public enum ModelKind
{
    Unknown = 0,
    Checkpoint,
    LORA,
    VAE,
    Controlnet,
    TextualInversion,
    Upscaler,
    Hypernetwork,
    Other,
}

public enum ModelNsfwKind { SFW = 0, Mature, NSFW }

/// <summary>v0.6.20:per-version 选中单位。Id 全局唯一 = "{SourceKind}:{ModelId}:{VersionId}"。
 1 个 ModelVersionEntry 对应 1 个可下载的具体文件 + meta.json sidecar。</summary>
public class ModelVersionEntry
{
    public string Id { get; init; } = "";                                               // "{CivitAi}:{modelId}:{versionId}"
    public ModelEntry Parent { get; init; } = null!;
    public string SourceVersionId { get; init; } = "";                                  // CivitAI modelVersionId
    public string Name { get; init; } = "";                                              // e.g. "v5.0 fp16"
    public string? BaseModel { get; init; }
    public long SizeBytes { get; init; }                                                 // primary file size
    public string PrimaryDownloadUrl { get; init; } = "";                                // primary file downloadUrl
    public IReadOnlyList<ModelFile> Files { get; init; } = Array.Empty<ModelFile>();
    public DateTimeOffset? PublishedAt { get; init; }
    public bool IsEarlyAccess { get; init; }
}

public class ModelFile
{
    public string Name { get; init; } = "";                                              // e.g. "model.safetensors"
    public string Format { get; init; } = "";                                            // "Safe Tensor" / "PickleTensor" / "ONNX" / "Other"
    public long SizeBytes { get; init; }
    public string DownloadUrl { get; init; } = "";
    public bool IsPrimary { get; init; }                                                 // marked primary in API
}

/// <summary>v0.6.20:filesystem 扫描出来的"已下载"状态(无 DB)。
 SubfolderName = "<version-slug>-<vid8>"(per-version subfolder,collision suffix -1/-2 已 strip)。</summary>
public class DownloadedModel
{
    public string SubfolderName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public ModelKind Kind { get; init; }
    public string? Title { get; init; }
    public string Source { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string SourceVersionId { get; init; } = "";
    public DateTime DownloadedAt { get; init; }
}

/// <summary>v0.6.20:meta.json sidecar 反序列化形状。
 DownloadAsync 写,FilesystemScanner 读。其他字段 forward-compatible。</summary>
public class ModelMetaSidecar
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("kind")] public ModelKind Kind { get; set; }
    [JsonPropertyName("base_model")] public string? BaseModel { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("source_version_id")] public string SourceVersionId { get; set; } = "";
    [JsonPropertyName("source_url")] public string SourceUrl { get; set; } = "";
    [JsonPropertyName("primary_filename")] public string PrimaryFilename { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("nsfw_level")] public int NsfwLevel { get; set; }
    [JsonPropertyName("downloaded_at")] public DateTime DownloadedAt { get; set; }
}

/// <summary>v0.6.20:Kind → ComfyUI standard subfolder 映射。
 Public,供 Downloader / Symlinker / FilesystemScanner 共享。</summary>
public static class ModelKindExtensions
{
    private static readonly Dictionary<ModelKind, string> KindToSubfolder = new()
    {
        [ModelKind.Checkpoint] = "checkpoints",
        [ModelKind.LORA] = "loras",
        [ModelKind.VAE] = "vae",
        [ModelKind.Controlnet] = "controlnet",
        [ModelKind.TextualInversion] = "embeddings",
        [ModelKind.Upscaler] = "upscale_models",
        [ModelKind.Hypernetwork] = "hypernetworks",
        [ModelKind.Unknown] = "other",
        [ModelKind.Other] = "other",
    };

    public static string ToComfyUiSubfolder(this ModelKind kind) =>
        KindToSubfolder.TryGetValue(kind, out var s) ? s : "other";

    /// <summary>v0.6.20:从 CivitAI "type" 字符串解析 Kind(case-insensitive, normalized)。</summary>
    public static ModelKind ParseKind(string? typeString)
    {
        if (string.IsNullOrWhiteSpace(typeString)) return ModelKind.Other;
        return typeString.Trim().ToLowerInvariant() switch
        {
            "checkpoint" => ModelKind.Checkpoint,
            "lora" or "lyocris" => ModelKind.LORA,
            "vae" => ModelKind.VAE,
            "controlnet" => ModelKind.Controlnet,
            "textualinversion" => ModelKind.TextualInversion,
            "upscaler" or "esrgan" or "realesrgan" => ModelKind.Upscaler,
            "hypernetwork" => ModelKind.Hypernetwork,
            _ => ModelKind.Other,
        };
    }

    /// <summary>v0.6.20:从 CivitAI nsfwLevel / nsfw bool 解析 NsfwKind。
 nsfwLevel 0/1 → SFW;2 → Mature;3+ → NSFW。nsfwLevel 缺失但 nsfw=true → Mature;nsfw false → SFW。</summary>
    public static ModelNsfwKind ParseNsfwKind(int? nsfwLevel, bool? nsfwBool)
    {
        if (nsfwLevel.HasValue)
        {
            if (nsfwLevel.Value <= 1) return ModelNsfwKind.SFW;
            if (nsfwLevel.Value == 2) return ModelNsfwKind.Mature;
            return ModelNsfwKind.NSFW;
        }
        if (nsfwBool == true) return ModelNsfwKind.Mature;
        return ModelNsfwKind.SFW;
    }

    /// <summary>v0.6.20:Slug 生成 + 8-char id 拼成 "<slug>-<id8>"。
 Slug = lowercase, non-[a-z0-9-] → '-', collapse repeated '-', trim。
 Id8 = first 8 chars of source id (pad if shorter than 8)。.</summary>
    public static string ToSlugId(string title, string sourceId)
    {
        var slug = (title ?? "").ToLowerInvariant();
        var sb = new System.Text.StringBuilder(slug.Length);
        char last = '\0';
        foreach (var c in slug)
        {
            var ch = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : (c == '-' || c == ' ' || c == '_') ? '-' : '-';
            if (ch == '-' && last == '-') continue;
            sb.Append(ch);
            last = ch;
        }
        var trimmed = sb.ToString().Trim('-');
        if (string.IsNullOrEmpty(trimmed)) trimmed = "model";
        var id8 = (sourceId ?? "").Length >= 8 ? (sourceId ?? "").Substring(0, 8) : (sourceId ?? "").PadRight(8, '0');
        return $"{trimmed}-{id8}";
    }
}
```

- [ ] **Step 2: Write failing test for `ModelFilesystemScanner`**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerTests.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelFilesystemScannerTests : IDisposable
{
    private readonly string _tmp;

    public ModelFilesystemScannerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ComfyUIMgrModels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void Scan_EmptyDir_ReturnsEmpty()
    {
        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_DirDoesNotExist_ReturnsEmpty()
    {
        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(Path.Combine(_tmp, "missing"));
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_OneVersionFolder_ReturnsOneEntry()
    {
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var verDir = Path.Combine(kindDir, "realistic-vision-12345678");
        var versionDir = Path.Combine(verDir, "v50-fp16-87654321");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision v5.0",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "87654321",
                SourceUrl = "https://civitai.com/models/12345",
                PrimaryFilename = "model.safetensors",
                SizeBytes = 6789012345,
                NsfwLevel = 0,
                DownloadedAt = DateTime.UtcNow,
            }));

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("v50-fp16-87654321", result[0].SubfolderName);
        Assert.Equal(ModelKind.Checkpoint, result[0].Kind);
        Assert.Equal("12345", result[0].SourceId);
        Assert.Equal("87654321", result[0].SourceVersionId);
    }

    [Fact]
    public void Scan_MultipleKindsAndVersions_ReturnsAll()
    {
        // checkpoints / realistic-vision-12345678 / v50-fp16-87654321 / meta.json
        CreateVersion("checkpoints", "realistic-vision-12345678", "v50-fp16-87654321", "Realistic Vision");
        CreateVersion("checkpoints", "realistic-vision-12345678", "v51-fp32-11223344", "Realistic Vision");
        CreateVersion("loras", "detail-totaling-23456789", "v1-99887766", "Detail Totaling");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.SubfolderName == "v50-fp16-87654321");
        Assert.Contains(result, r => r.SubfolderName == "v51-fp32-11223344");
        Assert.Contains(result, r => r.SubfolderName == "v1-99887766");
    }

    [Fact]
    public void Scan_VersionFolderMissingMetaJson_SkippedWithWarn()
    {
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var verDir = Path.Combine(kindDir, "realistic-vision-12345678");
        var versionDir = Path.Combine(verDir, "v50-fp16-87654321");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        // No meta.json → skip

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Empty(result);
    }

    private void CreateVersion(string kind, string modelSlugId, string versionSlugId, string title)
    {
        var versionDir = Path.Combine(_tmp, kind, modelSlugId, versionSlugId);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = title,
                Kind = kind switch
                {
                    "checkpoints" => ModelKind.Checkpoint,
                    "loras" => ModelKind.LORA,
                    "vae" => ModelKind.VAE,
                    _ => ModelKind.Other,
                },
                Source = "civitai",
                SourceId = modelSlugId.Split('-').Last(),
                SourceVersionId = versionSlugId.Split('-').Last(),
                DownloadedAt = DateTime.UtcNow,
            }));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelFilesystemScannerTests" --no-restore 2>&1 | tail -10`
Expected: 5 FAIL with "ModelFilesystemScanner not defined" / "type or namespace not found"

- [ ] **Step 4: Implement `ModelFilesystemScanner`**

Create `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20:扫描 ModelsDirectory 找到已下载的 model versions。
 递归 walks <ModelsDir>/<kind>/<model-slug-id>/<version-slug-id>/meta.json。
 缺失 meta.json 或 malformed → 跳过(不 throw)。</summary>
public class ModelFilesystemScanner
{
    private readonly AppLogger? _logger;

    public ModelFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<DownloadedModel> Scan(string modelsDir)
    {
        var results = new List<DownloadedModel>();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return results;

        // Walk: modelsDir / <kind> / <model-slug-id> / <version-slug-id> / meta.json
        foreach (var kindDir in Directory.EnumerateDirectories(modelsDir))
        {
            var kindName = Path.GetFileName(kindDir);
            foreach (var modelDir in Directory.EnumerateDirectories(kindDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(modelDir))
                {
                    var metaPath = Path.Combine(versionDir, "meta.json");
                    if (!File.Exists(metaPath))
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: missing meta.json");
                        continue;
                    }

                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        var sidecar = JsonSerializer.Deserialize<ModelMetaSidecar>(json);
                        if (sidecar is null)
                        {
                            _logger?.Warn("model-scanner", $"skip {versionDir}: meta.json null");
                            continue;
                        }

                        results.Add(new DownloadedModel
                        {
                            SubfolderName = Path.GetFileName(versionDir),
                            FullPath = versionDir,
                            Kind = sidecar.Kind,
                            Title = sidecar.Title,
                            Source = sidecar.Source,
                            SourceId = sidecar.SourceId,
                            SourceVersionId = sidecar.SourceVersionId,
                            DownloadedAt = sidecar.DownloadedAt,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: parse fail {ex.Message}");
                    }
                }
            }
        }

        return results;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelFilesystemScannerTests" --no-restore 2>&1 | tail -10`
Expected: 5 PASS

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Models/ModelEntry.cs \
  src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerTests.cs
git commit -m "feat(models): v0.6.20 T2 ModelEntry DTOs + ModelFilesystemScanner"
```

---

## Task 3: IModelSource interface + CivitAiModelSource (full) + HuggingFaceModelSource (stub)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/IModelSource.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs` (full impl with nsfw=true + kind parsing)
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs` (stub returning empty)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs` (7 tests + 1 SKIP real-fetch)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs` (2 tests)

**Interfaces:**
- Consumes: `HttpClient` (injected, never `new HttpClient()`); `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`; `ModelEntry` + `ModelVersionEntry` + `ModelFile` from T2
- Produces:
  - `IModelSource` interface (`ModelSourceKind SourceKind`, `string DisplayName`, `bool IsEnabled`, `Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)`)
  - `CivitAiModelSource` (full): hits `https://civitai.com/api/v1/models?limit=100&page=N&nsfw=true&sort=Newest`, paginated, parses items → `ModelEntry` list with all fields populated + `Versions`
  - `HuggingFaceModelSource` (stub): `DisplayName = "HuggingFace"`, `IsEnabled = false` default, `SearchAsync` returns `Array.Empty<ModelEntry>()`

- [ ] **Step 1: Write `IModelSource` interface**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

public interface IModelSource
{
    ModelSourceKind SourceKind { get; }
    string DisplayName { get; }
    bool IsEnabled { get; set; }
    Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
```

- [ ] **Step 2: Write `HuggingFaceModelSource` stub**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:HF stub — 接口占位,SearchAsync 永远返回 empty。
 v0.6.21+ 实现真正搜索 (HF Hub API + token)。</summary>
public class HuggingFaceModelSource : IModelSource
{
    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;  // placeholder
    public string DisplayName => "HuggingFace";
    public bool IsEnabled { get; set; } = false;  // disabled by default in v0.6.20

    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ModelEntry>>(Array.Empty<ModelEntry>());
    }
}
```

- [ ] **Step 3: Write failing test for `CivitAiModelSource`**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceCivitAiTests
{
    private static HttpClient CreateClient(DelegatingHandlerStub handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("https://civitai.com/") };
    }

    [Fact]
    public async Task SearchAsync_ValidJson_ReturnsParsedEntries()
    {
        var json = """
        {
          "items": [
            {
              "id": 12345,
              "name": "Realistic Vision",
              "type": "Checkpoint",
              "nsfw": false,
              "nsfwLevel": 0,
              "tags": ["portrait"],
              "stats": {"downloadCount": 1000, "ratingCount": 50, "rating": 4.5},
              "creator": {"username": "AuthorA"},
              "modelVersions": [
                {
                  "id": 67890,
                  "name": "v5.0 fp16",
                  "baseModel": "SD 1.5",
                  "files": [
                    {"name": "model.safetensors", "format": "Safe Tensor", "sizeKB": 6789012, "downloadUrl": "https://cdn.example.com/a.safetensors", "primary": true}
                  ],
                  "images": [{"url": "https://cdn.example.com/preview.jpg"}],
                  "publishedAt": "2026-08-01T00:00:00.000Z",
                  "earlyAccessEnabled": false
                }
              ]
            }
          ],
          "metadata": {"nextPage": null}
        }
        """;
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler)) { IsEnabled = true };

        var entries = await source.SearchAsync("", maxResults: 50, ct: default);

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("12345", e.SourceId);
        Assert.Equal("Realistic Vision", e.Title);
        Assert.Equal(ModelKind.Checkpoint, e.Kind);
        Assert.Equal(ModelNsfwKind.SFW, e.NsfwKind);
        Assert.Equal("AuthorA", e.Author);
        Assert.Equal(1000, e.DownloadCount);
        Assert.Equal(4.5, e.RatingStars);
        Assert.Single(e.Versions);
        var v = e.Versions[0];
        Assert.Equal("CivitAi:12345:67890", v.Id);
        Assert.Equal("v5.0 fp16", v.Name);
        Assert.Equal("SD 1.5", v.BaseModel);
        Assert.Equal(6789012L * 1024, v.SizeBytes);
        Assert.Equal("https://cdn.example.com/a.safetensors", v.PrimaryDownloadUrl);
        Assert.False(v.IsEarlyAccess);
        Assert.Single(v.Files);
        Assert.True(v.Files[0].IsPrimary);
        Assert.Equal("Safe Tensor", v.Files[0].Format);
        Assert.Equal("https://cdn.example.com/preview.jpg", e.PreviewImageUrl);
    }

    [Fact]
    public async Task SearchAsync_NsfwLevel2_ParsedAsMature()
    {
        var json = """{"items": [{"id": 1, "name": "Mature Model", "type": "LORA", "nsfwLevel": 2, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler));

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelNsfwKind.Mature, entries[0].NsfwKind);
    }

    [Fact]
    public async Task SearchAsync_NsfwLevel3_ParsedAsNSFW()
    {
        var json = """{"items": [{"id": 2, "name": "NSFW Model", "type": "Checkpoint", "nsfwLevel": 3, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler));

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelNsfwKind.NSFW, entries[0].NsfwKind);
    }

    [Fact]
    public async Task SearchAsync_TypeLORA_ParsedAsLORA()
    {
        var json = """{"items": [{"id": 3, "name": "Lora", "type": "LORA", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler));

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelKind.LORA, entries[0].Kind);
    }

    [Fact]
    public async Task SearchAsync_TypeUnknown_FallsToOther()
    {
        var json = """{"items": [{"id": 4, "name": "Unknown", "type": "MotionModule", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler));

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelKind.Other, entries[0].Kind);
    }

    [Fact]
    public async Task SearchAsync_NextPage_PaginatesUntilNull()
    {
        var page1 = """{"items": [{"id": 1, "name": "A", "type": "Checkpoint", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": "abc"}}""";
        var page2 = """{"items": [{"id": 2, "name": "B", "type": "Checkpoint", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(page1, page2);
        var source = new CivitAiModelSource(CreateClient(handler));

        var entries = await source.SearchAsync("", maxResults: 100, default);

        Assert.Equal(2, entries.Count);
        Assert.Equal("1", entries[0].SourceId);
        Assert.Equal("2", entries[1].SourceId);
    }

    [Fact]
    public async Task SearchAsync_HttpError_Throws()
    {
        var handler = new DelegatingHandlerStub(HttpStatusCode.InternalServerError, "");
        var source = new CivitAiModelSource(CreateClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.SearchAsync("", 50, default));
    }

    [Fact(Skip = "Real network endpoint; CI does not hit network. Run manually to verify CivitAI still public.")]
    public async Task LiveFetch_RealEndpoint_ReturnsEntries()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://civitai.com/") };
        var source = new CivitAiModelSource(client);
        var entries = await source.SearchAsync("", 5, default);
        Assert.NotEmpty(entries);
    }
}

internal class DelegatingHandlerStub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode, string)> _responses = new();

    public DelegatingHandlerStub(string body)
    {
        _responses.Enqueue((HttpStatusCode.OK, body));
    }

    public DelegatingHandlerStub(params string[] bodies)
    {
        foreach (var b in bodies) _responses.Enqueue((HttpStatusCode.OK, b));
    }

    public DelegatingHandlerStub(HttpStatusCode code, string body)
    {
        _responses.Enqueue((code, body));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (code, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelSourceCivitAiTests" --no-restore 2>&1 | tail -10`
Expected: 7 FAIL (1 SKIP) with "CivitAiModelSource not defined"

- [ ] **Step 5: Implement `CivitAiModelSource`**

Create `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:CivitAI Models API fetcher。
 Endpoint: https://civitai.com/api/v1/models?limit=100&page=N&nsfw=true&sort=Newest
 Pagination: 走 "metadata.nextPage" cursor 直到 null。
 nsfw=true 全部拉回来,UI badge 区分 NSFW/Mature/SFW。</summary>
public class CivitAiModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private const int PageSize = 100;
    private const string BaseUrl = "https://civitai.com/api/v1/models";

    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;
    public string DisplayName => "CivitAI";
    public bool IsEnabled { get; set; } = true;

    public CivitAiModelSource(HttpClient http, AppLogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var results = new List<ModelEntry>();
        string? cursor = null;
        var pageCount = 0;
        const int maxPages = 10;  // hard cap to prevent runaway

        while (results.Count < maxResults && pageCount < maxPages)
        {
            pageCount++;
            var url = BuildUrl(query, cursor, pageCount == 1);
            _logger?.Info("model-civitai", $"fetch page {pageCount}: {url}");

            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            var page = JsonSerializer.Deserialize<CivitAiPage>(body, JsonOpts);
            if (page?.Items is null || page.Items.Count == 0) break;

            foreach (var item in page.Items)
            {
                var entry = MapItemToEntry(item);
                if (entry is not null) results.Add(entry);
            }

            cursor = page.Metadata?.NextPage;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return results.Take(maxResults).ToList();
    }

    private string BuildUrl(string query, string? cursor, bool firstPage)
    {
        var qs = new List<string>
        {
            $"limit={PageSize}",
            "sort=Newest",
            "nsfw=true",  // 全部拉回来,UI 分类
        };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"query={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrEmpty(cursor)) qs.Add($"page={Uri.EscapeDataString(cursor)}");
        return $"{BaseUrl}?{string.Join("&", qs)}";
    }

    private static ModelEntry? MapItemToEntry(CivitAiItem item)
    {
        if (item.Id is null || string.IsNullOrEmpty(item.Name)) return null;

        var versions = new List<ModelVersionEntry>();
        if (item.ModelVersions is not null)
        {
            foreach (var v in item.ModelVersions)
            {
                if (v.Id is null || v.Files is null || v.Files.Count == 0) continue;

                var files = v.Files.Select(f => new ModelFile
                {
                    Name = f.Name ?? "",
                    Format = f.Format ?? "Other",
                    SizeBytes = (f.SizeKB ?? 0) * 1024L,
                    DownloadUrl = f.DownloadUrl ?? "",
                    IsPrimary = f.Primary == true,
                }).ToList();

                var primary = files.FirstOrDefault(f => f.IsPrimary) ?? files.First();
                versions.Add(new ModelVersionEntry
                {
                    Id = $"{ModelSourceKind.CivitAi}:{item.Id}:{v.Id}",
                    Parent = null!,  // set below
                    SourceVersionId = v.Id.ToString() ?? "",
                    Name = v.Name ?? $"v{v.Id}",
                    BaseModel = v.BaseModel,
                    SizeBytes = primary.SizeBytes,
                    PrimaryDownloadUrl = primary.DownloadUrl,
                    Files = files,
                    PublishedAt = v.PublishedAt,
                    IsEarlyAccess = v.EarlyAccessEnabled == true,
                });
            }
        }

        // First version's first image as preview
        var preview = item.ModelVersions?.FirstOrDefault()?.Images?.FirstOrDefault()?.Url;

        var entry = new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = item.Id.ToString() ?? "",
            SourceUrl = $"https://civitai.com/models/{item.Id}",
            Title = item.Name,
            Description = item.Description,
            Author = item.Creator?.Username,
            AuthorUrl = item.Creator?.Link,
            Kind = ModelKindExtensions.ParseKind(item.Type),
            BaseModel = item.ModelVersions?.FirstOrDefault()?.BaseModel,
            NsfwKind = ModelKindExtensions.ParseNsfwKind(item.NsfwLevel, item.Nsfw),
            NsfwLevel = item.NsfwLevel,
            DownloadCount = item.Stats?.DownloadCount,
            RatingCount = item.Stats?.RatingCount,
            RatingStars = item.Stats?.Rating,
            PublishedAt = item.PublishedAt,
            Tags = item.Tags ?? new List<string>(),
            PreviewImageUrl = preview,
            Versions = versions,
        };

        // Backfill Parent ref
        for (var i = 0; i < versions.Count; i++)
        {
            versions[i] = new ModelVersionEntry
            {
                Id = versions[i].Id,
                Parent = entry,
                SourceVersionId = versions[i].SourceVersionId,
                Name = versions[i].Name,
                BaseModel = versions[i].BaseModel,
                SizeBytes = versions[i].SizeBytes,
                PrimaryDownloadUrl = versions[i].PrimaryDownloadUrl,
                Files = versions[i].Files,
                PublishedAt = versions[i].PublishedAt,
                IsEarlyAccess = versions[i].IsEarlyAccess,
            };
        }

        return entry;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // DTO classes for CivitAI JSON (private)
    private class CivitAiPage
    {
        [JsonPropertyName("items")] public List<CivitAiItem>? Items { get; set; }
        [JsonPropertyName("metadata")] public CivitAiMetadata? Metadata { get; set; }
    }
    private class CivitAiMetadata
    {
        [JsonPropertyName("nextPage")] public string? NextPage { get; set; }
    }
    private class CivitAiItem
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
        [JsonPropertyName("nsfwLevel")] public int? NsfwLevel { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("stats")] public CivitAiStats? Stats { get; set; }
        [JsonPropertyName("creator")] public CivitAiCreator? Creator { get; set; }
        [JsonPropertyName("modelVersions")] public List<CivitAiVersion>? ModelVersions { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
    }
    private class CivitAiStats
    {
        [JsonPropertyName("downloadCount")] public int? DownloadCount { get; set; }
        [JsonPropertyName("ratingCount")] public int? RatingCount { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
    }
    private class CivitAiCreator
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
    }
    private class CivitAiVersion
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
        [JsonPropertyName("files")] public List<CivitAiFile>? Files { get; set; }
        [JsonPropertyName("images")] public List<CivitAiImage>? Images { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("earlyAccessEnabled")] public bool? EarlyAccessEnabled { get; set; }
    }
    private class CivitAiFile
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("sizeKB")] public double? SizeKB { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("primary")] public bool? Primary { get; set; }
    }
    private class CivitAiImage
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
```

- [ ] **Step 6: Write HF stub tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs`:

```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceHuggingFaceTests
{
    [Fact]
    public async Task SearchAsync_AnyQuery_ReturnsEmpty()
    {
        var source = new HuggingFaceModelSource();
        var entries = await source.SearchAsync("anything", 50, CancellationToken.None);
        Assert.Empty(entries);
    }

    [Fact]
    public void IsEnabled_DefaultsFalse()
    {
        var source = new HuggingFaceModelSource();
        Assert.False(source.IsEnabled);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelSource(FullName: CivitAi|HuggingFace)Tests" --no-restore 2>&1 | tail -10`
Expected: 9 PASS / 1 SKIP (CivitAI 7 + HF 2, live-fetch SKIP)

- [ ] **Step 8: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Services/ModelSources/IModelSource.cs \
  src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs \
  src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs
git commit -m "feat(models): v0.6.20 T3 IModelSource + CivitAI full + HF stub"
```

---

## Task 4: ModelMarketplaceService aggregator

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelMarketplaceService.cs` (parallel + dedup + per-source try/catch)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelMarketplaceServiceTests.cs` (5 tests)

**Interfaces:**
- Consumes: `IEnumerable<IModelSource>` injected; `Task.WhenAll` parallelism; `HashSet<(ModelSourceKind, string)>` dedup
- Produces: `ModelMarketplaceService.LoadAllAsync(query, maxResultsPerSource, ct) → Task<IReadOnlyList<ModelEntry>>`

- [ ] **Step 1: Write failing test for `ModelMarketplaceService`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelMarketplaceServiceTests
{
    [Fact]
    public async Task LoadAllAsync_TwoSources_NoOverlap_AggregatesAll()
    {
        var s1 = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "A" });
        var s2 = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "2", Title = "B" });

        var svc = new ModelMarketplaceService(new IModelSource[] { s1, s2 });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task LoadAllAsync_TwoSources_SameId_DedupsToOne()
    {
        var entry = new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "A" };
        var s1 = new FakeSource(ModelSourceKind.CivitAi, entry);
        var s2 = new FakeSource(ModelSourceKind.CivitAi, entry);

        var svc = new ModelMarketplaceService(new IModelSource[] { s1, s2 });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_DisabledSource_Skipped()
    {
        var enabled = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "1", Title = "A" }) { IsEnabled = true };
        var disabled = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "2", Title = "B" }) { IsEnabled = false };

        var svc = new ModelMarketplaceService(new IModelSource[] { enabled, disabled });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
        Assert.Equal("1", result[0].SourceId);
    }

    [Fact]
    public async Task LoadAllAsync_OneSourceThrows_OthersStillReturn()
    {
        var good = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "1", Title = "A" }) { IsEnabled = true };
        var bad = new ThrowingSource { IsEnabled = true };

        var svc = new ModelMarketplaceService(new IModelSource[] { good, bad });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
        Assert.Equal("1", result[0].SourceId);
    }

    [Fact]
    public async Task LoadAllAsync_AllSourcesFail_ReturnsEmpty()
    {
        var svc = new ModelMarketplaceService(new IModelSource[] { new ThrowingSource(), new ThrowingSource() });
        var result = await svc.LoadAllAsync("", 50, default);
        Assert.Empty(result);
    }
}

internal class FakeSource : IModelSource
{
    private readonly ModelEntry[] _entries;
    public ModelSourceKind SourceKind { get; }
    public string DisplayName => "Fake";
    public bool IsEnabled { get; set; } = true;

    public FakeSource(ModelSourceKind kind, params ModelEntry[] entries)
    {
        SourceKind = kind;
        _entries = entries;
    }

    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelEntry>>(_entries);
}

internal class ThrowingSource : IModelSource
{
    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;
    public string DisplayName => "Throwing";
    public bool IsEnabled { get; set; } = true;
    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceServiceTests" --no-restore 2>&1 | tail -10`
Expected: 5 FAIL

- [ ] **Step 3: Implement `ModelMarketplaceService`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;

namespace ComfyUI.Manager.Services;

public class ModelMarketplaceService
{
    private readonly IReadOnlyList<IModelSource> _sources;
    private readonly AppLogger? _logger;

    public ModelMarketplaceService(IEnumerable<IModelSource> sources, AppLogger? logger = null)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(string query, int maxResultsPerSource, CancellationToken ct)
    {
        var enabled = _sources.Where(s => s.IsEnabled).ToList();
        var tasks = enabled.Select(async src =>
        {
            try
            {
                var entries = await src.SearchAsync(query, maxResultsPerSource, ct);
                _logger?.Info("model-marketplace", $"[{src.DisplayName}] fetched {entries.Count} entries");
                return (src.SourceKind, entries);
            }
            catch (Exception ex)
            {
                _logger?.Error("model-marketplace", $"[{src.DisplayName}] failed: {ex.Message}");
                return (src.SourceKind, (IReadOnlyList<ModelEntry>)Array.Empty<ModelEntry>());
            }
        });
        var results = await Task.WhenAll(tasks);

        var seen = new HashSet<(ModelSourceKind, string)>();
        var merged = new List<ModelEntry>();
        foreach (var (_, entries) in results)
        {
            foreach (var e in entries)
            {
                if (seen.Add((e.Source, e.SourceId)))
                    merged.Add(e);
            }
        }
        return merged;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceServiceTests" --no-restore 2>&1 | tail -10`
Expected: 5 PASS

- [ ] **Step 5: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Services/ModelMarketplaceService.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelMarketplaceServiceTests.cs
git commit -m "feat(models): v0.6.20 T4 ModelMarketplaceService aggregator"
```

---

## Task 5: ModelDownloader (streaming + progress + atomic rename + batch)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelDownloader.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelDownloaderTests.cs` (8 tests)

**Interfaces:**
- Consumes: `HttpClient` (injected, with `ResponseHeadersRead`); `Stream.CopyToAsync` for GB streaming; `IProgress<ModelDownloadProgress>` for UI; `SemaphoreSlim(4)` for batch concurrency
- Produces:
  - `ModelDownloadProgress { long BytesDownloaded, long? TotalBytes, double Percent }`
  - `ModelDownloadResult { bool Success, string? FailureReason, string? FilePath, long SizeBytes }`
  - `ModelDownloadSummary { int Succeeded, int Failed, long TotalBytesDownloaded, TimeSpan TotalDuration }`
  - `ModelDownloader.DownloadAsync(version, modelsDir, log=null, progress=null, ct) → Task<ModelDownloadResult>`
  - `ModelDownloader.DownloadBatchAsync(versions, modelsDir, log=null, ct) → Task<ModelDownloadSummary>`

- [ ] **Step 1: Write `ModelDownloader` with full streaming logic**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

public class ModelDownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent => TotalBytes.HasValue && TotalBytes.Value > 0
        ? (double)BytesDownloaded / TotalBytes.Value * 100.0
        : 0.0;
}

public class ModelDownloadResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string? FilePath { get; init; }
    public long SizeBytes { get; init; }
}

public class ModelDownloadSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;

    public ModelDownloader(HttpClient http, AppLogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ModelDownloadSummary> DownloadBatchAsync(
        IReadOnlyList<ModelVersionEntry> versions,
        string modelsDir,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sem = new SemaphoreSlim(4);
        var errors = new List<string>();
        var succeeded = 0;
        var failed = 0;
        long totalBytes = 0;

        var tasks = versions.Select(async v =>
        {
            await sem.WaitAsync(ct);
            try
            {
                log?.Report($"[开始] {v.Parent.Title} / {v.Name}");
                var result = await DownloadAsync(v, modelsDir, log, null, ct);
                if (result.Success)
                {
                    Interlocked.Increment(ref succeeded);
                    Interlocked.Add(ref totalBytes, result.SizeBytes);
                    log?.Report($"[✓ OK] {v.Name} → {result.FilePath} ({FormatSize(result.SizeBytes)})");
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    lock (errors) errors.Add($"{v.Name}: {result.FailureReason}");
                    log?.Report($"[✗ FAIL] {v.Name}: {result.FailureReason}");
                }
                return result;
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        return new ModelDownloadSummary
        {
            Succeeded = succeeded,
            Failed = failed,
            TotalBytesDownloaded = totalBytes,
            TotalDuration = sw.Elapsed,
            Errors = errors,
        };
    }

    public async Task<ModelDownloadResult> DownloadAsync(
        ModelVersionEntry version,
        string modelsDir,
        IProgress<string>? log = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var kind = version.Parent.Kind;
        var kindSubfolder = kind.ToComfyUiSubfolder();
        var modelSlugId = ModelKindExtensions.ToSlugId(version.Parent.Title, version.Parent.SourceId);
        var versionSlugId = ModelKindExtensions.ToSlugId(version.Name, version.SourceVersionId);

        var baseDir = Path.Combine(modelsDir, kindSubfolder, modelSlugId);
        var targetDir = ResolveCollisionFree(baseDir, versionSlugId);
        Directory.CreateDirectory(targetDir);

        var primary = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (primary is null || string.IsNullOrEmpty(primary.DownloadUrl))
            return new ModelDownloadResult { Success = false, FailureReason = "no primary file" };

        var fileName = string.IsNullOrEmpty(primary.Name) ? "model.safetensors" : primary.Name;
        var finalPath = Path.Combine(targetDir, fileName);
        var partialPath = finalPath + ".partial";

        try
        {
            using var resp = await _http.GetAsync(primary.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920]; // 80KB buffer for GB efficiency
            long downloaded = 0;
            int read;
            var lastReportBytes = 0L;
            const int reportIntervalBytes = 1_000_000; // report every ~1MB

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                progress?.Report(new ModelDownloadProgress
                {
                    BytesDownloaded = downloaded,
                    TotalBytes = totalBytes,
                });
                if (log is not null && downloaded - lastReportBytes >= reportIntervalBytes)
                {
                    lastReportBytes = downloaded;
                    var pct = totalBytes.HasValue && totalBytes.Value > 0
                        ? (double)downloaded / totalBytes.Value * 100.0
                        : 0.0;
                    log.Report($"  [{pct:F1}%] {FormatSize(downloaded)}/{FormatSize(totalBytes ?? 0)}");
                }
            }

            fileStream.Flush();
            fileStream.Close();

            // Atomic rename
            File.Move(partialPath, finalPath, overwrite: true);

            // Write meta.json sidecar
            var sidecar = new ModelMetaSidecar
            {
                Title = version.Parent.Title,
                Kind = kind,
                BaseModel = version.BaseModel ?? version.Parent.BaseModel,
                Author = version.Parent.Author,
                Source = version.Parent.Source.ToString().ToLowerInvariant(),
                SourceId = version.Parent.SourceId,
                SourceVersionId = version.SourceVersionId,
                SourceUrl = version.Parent.SourceUrl,
                PrimaryFilename = fileName,
                SizeBytes = downloaded,
                NsfwLevel = version.Parent.NsfwLevel ?? 0,
                DownloadedAt = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(
                Path.Combine(targetDir, "meta.json"),
                JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            _logger?.Info("model-download", $"OK {finalPath} ({FormatSize(downloaded)})");

            return new ModelDownloadResult
            {
                Success = true,
                FilePath = finalPath,
                SizeBytes = downloaded,
            };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { /* swallow */ }
            _logger?.Error("model-download", $"FAIL {version.Name}: {ex.Message}");
            return new ModelDownloadResult
            {
                Success = false,
                FailureReason = ex.Message,
            };
        }
    }

    /// <summary>v0.6.20:collision-free dir name = <baseDir>/<versionSlugId>[/+1/-2...]。
    </summary>
    private static string ResolveCollisionFree(string baseDir, string versionSlugId)
    {
        var candidate = Path.Combine(baseDir, versionSlugId);
        if (!Directory.Exists(candidate)) return candidate;
        for (var i = 1; i < 1000; i++)
        {
            var withSuffix = Path.Combine(baseDir, $"{versionSlugId}-{i}");
            if (!Directory.Exists(withSuffix)) return withSuffix;
        }
        throw new IOException($"collision runaway for {versionSlugId} under {baseDir}");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
```

- [ ] **Step 2: Write failing test for `ModelDownloader`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelDownloaderTests : IDisposable
{
    private readonly string _tmp;
    private readonly DelegatingHandlerStub _handler;

    public ModelDownloaderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ComfyUIMgrDl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _handler = new DelegatingHandlerStub();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private ModelDownloader NewDownloader() => new ModelDownloader(new HttpClient(_handler), null);

    private ModelVersionEntry MakeVersion(string title = "Realistic Vision", string modelId = "12345", string versionId = "67890")
    {
        var entry = new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = modelId,
            Title = title,
            Kind = ModelKind.Checkpoint,
            BaseModel = "SD 1.5",
        };
        return new ModelVersionEntry
        {
            Id = $"CivitAi:{modelId}:{versionId}",
            Parent = entry,
            SourceVersionId = versionId,
            Name = "v5.0 fp16",
            BaseModel = "SD 1.5",
            SizeBytes = 1024,
            PrimaryDownloadUrl = "https://cdn.example.com/model.safetensors",
            Files = new List<ModelFile> {
                new ModelFile {
                    Name = "model.safetensors",
                    Format = "Safe Tensor",
                    SizeBytes = 1024,
                    DownloadUrl = "https://cdn.example.com/model.safetensors",
                    IsPrimary = true
                }
            },
        };
    }

    [Fact]
    public async Task DownloadAsync_WritesFileAndMeta_ReturnsSuccess()
    {
        var fakeBytes = new byte[1024];
        for (var i = 0; i < fakeBytes.Length; i++) fakeBytes[i] = (byte)(i % 256);
        _handler.Enqueue(HttpStatusCode.OK, fakeBytes, contentLength: 1024);

        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, log: null, progress: null, default);

        Assert.True(result.Success);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath!));
        Assert.Equal(1024, result.SizeBytes);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.FilePath)!, "meta.json")));
    }

    [Fact]
    public async Task DownloadAsync_ProgressCallback_FiresMonotonic()
    {
        var fakeBytes = new byte[4096];
        _handler.Enqueue(HttpStatusCode.OK, fakeBytes, contentLength: 4096);

        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => reports.Add(p));

        var v = MakeVersion();
        await NewDownloader().DownloadAsync(v, _tmp, log: null, progress: progress, default);

        // Allow async Progress<T> callbacks to flush
        await Task.Delay(100);

        Assert.NotEmpty(reports);
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].BytesDownloaded >= reports[i - 1].BytesDownloaded);
        Assert.Equal(4096, reports[^1].BytesDownloaded);
        Assert.Equal(4096, reports[^1].TotalBytes);
    }

    [Fact]
    public async Task DownloadAsync_HttpError_ReturnsFail()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "Not Found");

        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Null(result.FilePath);
    }

    [Fact]
    public async Task DownloadAsync_VersionFolderExists_CollisionSuffixAdded()
    {
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var modelDir = Path.Combine(kindDir, "realistic-vision-12345");
        var existingDir = Path.Combine(modelDir, "v50-fp16-67890");
        Directory.CreateDirectory(existingDir);

        _handler.Enqueue(HttpStatusCode.OK, new byte[10], contentLength: 10);
        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        Assert.True(result.Success);
        Assert.True(result.FilePath!.Contains("v50-fp16-67890-1"));
    }

    [Fact]
    public async Task DownloadAsync_PartialFileCleanedOnFailure()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, "");

        var v = MakeVersion();
        await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        // No .partial should remain
        var partials = Directory.GetFiles(_tmp, "*.partial", SearchOption.AllDirectories);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task DownloadBatchAsync_Parallel4_AllSucceed()
    {
        for (var i = 0; i < 5; i++)
            _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);

        var versions = new List<ModelVersionEntry>();
        for (var i = 0; i < 5; i++)
            versions.Add(MakeVersion(versionId: $"v{i}"));

        var summary = await NewDownloader().DownloadBatchAsync(versions, _tmp, log: null, default);

        Assert.Equal(5, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(500, summary.TotalBytesDownloaded);
    }

    [Fact]
    public async Task DownloadBatchAsync_OneFails_OthersSucceed()
    {
        _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);
        _handler.Enqueue(HttpStatusCode.NotFound, "fail");
        _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);

        var versions = new List<ModelVersionEntry> {
            MakeVersion(versionId: "v0"),
            MakeVersion(versionId: "v1"),
            MakeVersion(versionId: "v2"),
        };

        var summary = await NewDownloader().DownloadBatchAsync(versions, _tmp, null, default);

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Single(summary.Errors);
    }

    [Fact]
    public async Task DownloadAsync_MetaJsonContainsRequiredFields()
    {
        _handler.Enqueue(HttpStatusCode.OK, new byte[10], contentLength: 10);
        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        var metaPath = Path.Combine(Path.GetDirectoryName(result.FilePath)!, "meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        Assert.Contains("\"title\"", json);
        Assert.Contains("Realistic Vision", json);
        Assert.Contains("\"kind\"", json);
        Assert.Contains("Checkpoint", json);
        Assert.Contains("\"source_id\"", json);
        Assert.Contains("12345", json);
        Assert.Contains("\"source_version_id\"", json);
        Assert.Contains("67890", json);
        Assert.Contains("\"downloaded_at\"", json);
    }
}

internal class DelegatingHandlerStub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode, byte[], long?)> _responses = new();

    public void Enqueue(HttpStatusCode code, byte[] body, long? contentLength = null)
    {
        _responses.Enqueue((code, body, contentLength));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (code, body, len) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, Array.Empty<byte>(), (long?)null);
        var msg = new HttpResponseMessage(code)
        {
            Content = new ByteArrayContent(body),
        };
        if (len.HasValue)
        {
            msg.Content.Headers.ContentLength = len.Value;
        }
        return Task.FromResult(msg);
    }
}
```

- [ ] **Step 3: Run tests**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelDownloaderTests" --no-restore 2>&1 | tail -10`
Expected: 8 PASS

- [ ] **Step 4: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Services/ModelDownloader.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelDownloaderTests.cs
git commit -m "feat(models): v0.6.20 T5 ModelDownloader streaming + batch + atomic rename"
```

---

## Task 6: ModelSymlinker (env-start per-version junction sync)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTests.cs` (5 tests)

**Interfaces:**
- Consumes: `JunctionLinker` (existing Windows helper); `Directory.CreateSymbolicLink` (Linux/macOS); `ModelFilesystemScanner` from T2; `KindToComfyUiSubfolder` from T2
- Produces:
  - `ModelSyncResult { int Linked, int Skipped, int Failed, IReadOnlyList<string> Errors }`
  - `ModelSymlinker.SyncToEnvAsync(envId, envComfyuiSource, ct) → Task<ModelSyncResult>`

- [ ] **Step 1: Write `ModelSymlinker` (mirror v0.6.19 `WorkflowSymlinker` pattern)**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

public class ModelSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class ModelSymlinker
{
    private readonly Settings _settings;
    private readonly ModelFilesystemScanner _scanner;
    private readonly JunctionLinker _linker;
    private readonly AppLogger? _logger;

    public ModelSymlinker(
        Settings settings,
        ModelFilesystemScanner scanner,
        JunctionLinker linker,
        AppLogger? logger = null)
    {
        _settings = settings;
        _scanner = scanner;
        _linker = linker;
        _logger = logger;
    }

    public async Task<ModelSyncResult> SyncToEnvAsync(string envId, string envComfyuiSource, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envComfyuiSource))
        {
            _logger?.Warn("model-symlink", $"env '{envId}' has empty ComfyuiSource; skip");
            return new ModelSyncResult();
        }

        var modelsDir = _settings.ModelsDirectory;
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            _logger?.Warn("model-symlink", $"ModelsDirectory '{modelsDir}' not exist; skip");
            return new ModelSyncResult();
        }

        var downloaded = _scanner.Scan(modelsDir);
        var linked = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        var envModelsDir = Path.Combine(envComfyuiSource, "models");
        Directory.CreateDirectory(envModelsDir);

        foreach (var dm in downloaded)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kindSubfolder = dm.Kind.ToComfyUiSubfolder();
                var envKindDir = Path.Combine(envModelsDir, kindSubfolder);
                Directory.CreateDirectory(envKindDir);

                // link name: <model-slug>-<id8>__<version-slug>-<vid8>
                var modelSlugId = ModelKindExtensions.ToSlugId(dm.Title ?? dm.SourceId, dm.SourceId);
                var versionSlugId = ModelKindExtensions.ToSlugId(dm.SubfolderName, dm.SourceVersionId);
                var linkName = $"{modelSlugId}__{versionSlugId}";
                var linkPath = Path.Combine(envKindDir, linkName);
                var targetPath = dm.FullPath;

                if (Directory.Exists(linkPath))
                {
                    var existingTarget = await _linker.GetTargetAsync(linkPath, ct);
                    if (existingTarget == targetPath)
                    {
                        skipped++;
                        continue;
                    }
                    // Mismatch — delete + recreate
                    Directory.Delete(linkPath);
                }

                await _linker.CreateAsync(linkPath, targetPath, ct);
                linked++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{dm.SubfolderName}: {ex.Message}");
                _logger?.Warn("model-symlink", $"FAIL {dm.SubfolderName}: {ex.Message}");
            }
        }

        _logger?.Info("model-symlink", $"env '{envId}' linked={linked} skipped={skipped} failed={failed}");
        return new ModelSyncResult { Linked = linked, Skipped = skipped, Failed = failed, Errors = errors };
    }
}
```

- [ ] **Step 2: Write failing test for `ModelSymlinker`**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSymlinkerTests : IDisposable
{
    private readonly string _envRoot;
    private readonly string _modelsDir;
    private readonly Settings _settings;

    public ModelSymlinkerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(), "ComfyUIMgrSym_" + Guid.NewGuid().ToString("N"));
        _modelsDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrModels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_envRoot);
        Directory.CreateDirectory(_modelsDir);
        _settings = new Settings { ModelsDirectory = _modelsDir };
    }

    public void Dispose()
    {
        if (Directory.Exists(_envRoot)) Directory.Delete(_envRoot, recursive: true);
        if (Directory.Exists(_modelsDir)) Directory.Delete(_modelsDir, recursive: true);
    }

    [Fact]
    public async Task SyncToEnvAsync_OneDownloadedModel_CreatesJunction()
    {
        // Setup: models/checkpoints/realistic-vision-12345/v50-fp16-67890/meta.json
        var versionDir = Path.Combine(_modelsDir, "checkpoints", "realistic-vision-12345", "v50-fp16-67890");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "67890",
                PrimaryFilename = "model.safetensors",
                DownloadedAt = DateTime.UtcNow,
            }));

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync("env1", _envRoot, default);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Failed);
        var linkPath = Path.Combine(_envRoot, "models", "checkpoints", "realistic-vision-12345__v50-fp16-67890");
        Assert.True(Directory.Exists(linkPath));
    }

    [Fact]
    public async Task SyncToEnvAsync_EmptyEnvComfyuiSource_ReturnsEmpty()
    {
        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync("env1", "", default);

        Assert.Equal(0, result.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_AlreadyCorrectJunction_Skipped()
    {
        // Setup: download + first sync
        var versionDir = Path.Combine(_modelsDir, "checkpoints", "realistic-vision-12345", "v50-fp16-67890");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "67890",
                PrimaryFilename = "model.safetensors",
                DownloadedAt = DateTime.UtcNow,
            }));

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());

        await symlinker.SyncToEnvAsync("env1", _envRoot, default);  // 1st sync
        var result2 = await symlinker.SyncToEnvAsync("env1", _envRoot, default);  // 2nd sync

        Assert.Equal(1, result2.Skipped);
        Assert.Equal(0, result2.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_WrongExistingJunction_RecreatesLink()
    {
        // Setup: pre-create wrong junction
        var envKindDir = Path.Combine(_envRoot, "models", "checkpoints");
        Directory.CreateDirectory(envKindDir);
        var linkPath = Path.Combine(envKindDir, "realistic-vision-12345__v50-fp16-67890");
        var wrongTarget = Path.Combine(_modelsDir, "wrong", "wrong");
        Directory.CreateDirectory(wrongTarget);
        try { Directory.CreateSymbolicLink(linkPath, wrongTarget); }
        catch { /* some FS may not support symlinks in tests; skip */ return; }

        // Setup: real download
        var versionDir = Path.Combine(_modelsDir, "checkpoints", "realistic-vision-12345", "v50-fp16-67890");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "67890",
                PrimaryFilename = "model.safetensors",
                DownloadedAt = DateTime.UtcNow,
            }));

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync("env1", _envRoot, default);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task SyncToEnvAsync_LinkCreationFails_RecordsErrorWithoutThrowing()
    {
        // Setup: download + env.ComfyuiSource is a FILE not DIR → CreateDirectory succeeds but junction will fail
        var versionDir = Path.Combine(_modelsDir, "checkpoints", "realistic-vision-12345", "v50-fp16-67890");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "67890",
                PrimaryFilename = "model.safetensors",
                DownloadedAt = DateTime.UtcNow,
            }));

        // Force a path collision by pre-creating a file where the link should go
        var envModelsDir = Path.Combine(_envRoot, "models");
        Directory.CreateDirectory(envModelsDir);
        var kindDir = Path.Combine(envModelsDir, "checkpoints");
        Directory.CreateDirectory(kindDir);
        File.WriteAllText(Path.Combine(kindDir, "realistic-vision-12345__v50-fp16-67890"), "blocker");

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync("env1", _envRoot, default);

        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.Failed);
        Assert.NotEmpty(result.Errors);
    }
}
```

- [ ] **Step 3: Run tests**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelSymlinkerTests" --no-restore 2>&1 | tail -10`
Expected: 5 PASS (one may skip if symlinks unsupported)

- [ ] **Step 4: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTests.cs
git commit -m "feat(models): v0.6.20 T6 ModelSymlinker per-version env junction sync"
```

---

### Task 7: Model Badge Converters (NSFW + Kind)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/Converters.cs:1-30` (add 3 new converter classes after WorkflowSourceBadgeTextConverter)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml:29-31` (register 3 new converters)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Views/ModelBadgeConverterTests.cs` (new)

**Interfaces:**
- Consumes: `ModelNsfwKind` enum (T2), `ModelKind` enum (T2), palette resources `OutlineBrush`/`WarningBrush`/`ErrorBrush`/`PrimaryBrush`/`SecondaryBrush`/`TertiaryBrush` (existing)
- Produces:
  - `ModelNsfwBadgeBrushConverter` — `ModelNsfwKind` → `Brush` via palette lookup (SFW=OutlineBrush, Mature=WarningBrush, NSFW=ErrorBrush)
  - `ModelNsfwBadgeTextConverter` — `ModelNsfwKind` → "SFW"/"Mature"/"NSFW" Chinese
  - `ModelKindBadgeBrushConverter` — `ModelKind` → `Brush` via palette lookup (8 kind-specific colors)

**Reuses:** `WorkflowSourceBadgeBrushConverter` pattern (line 53-77) — `Application.Current.TryFindResource(key)` lookup with hardcoded fallback SolidColorBrush.

- [ ] **Step 1: Write failing tests**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Views/ModelBadgeConverterTests.cs
using System.Globalization;
using System.Windows.Media;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class ModelBadgeConverterTests
{
    [Theory]
    [InlineData(ModelNsfwKind.SFW, "SFW")]
    [InlineData(ModelNsfwKind.Mature, "Mature")]
    [InlineData(ModelNsfwKind.NSFW, "NSFW")]
    public void NsfwBadgeText_ReturnsCorrectString(ModelNsfwKind kind, string expected)
    {
        var converter = ModelNsfwBadgeTextConverter.Instance;
        var result = converter.Convert(kind, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NsfwBadgeText_UnknownEnum_ReturnsQuestionMark()
    {
        var converter = ModelNsfwBadgeTextConverter.Instance;
        var result = converter.Convert((ModelNsfwKind)999, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("?", result);
    }

    [Fact]
    public void NsfwBadgeBrush_Nsfw_ReturnsErrorVariantBrush()
    {
        // In test process there's no Application.Current with palette resources,
        // so converter falls back to hardcoded SolidColorBrush — assert it returns a Brush
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.NSFW, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
        // NSFW = ErrorBrush, fallback RGB (0xBA, 0x1A, 0x1A)
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0xBA, 0x1A, 0x1A), brush.Color);
    }

    [Fact]
    public void NsfwBadgeBrush_Mature_ReturnsWarningBrush()
    {
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.Mature, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void NsfwBadgeBrush_Sfw_ReturnsOutlineBrush()
    {
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.SFW, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Theory]
    [InlineData(ModelKind.Checkpoint)]
    [InlineData(ModelKind.LORA)]
    [InlineData(ModelKind.VAE)]
    [InlineData(ModelKind.Controlnet)]
    [InlineData(ModelKind.TextualInversion)]
    [InlineData(ModelKind.Upscaler)]
    [InlineData(ModelKind.Hypernetwork)]
    [InlineData(ModelKind.Other)]
    public void KindBadgeBrush_AllKinds_ReturnNonNullBrush(ModelKind kind)
    {
        var converter = ModelKindBadgeBrushConverter.Instance;
        var result = converter.Convert(kind, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void KindBadgeBrush_UnknownKind_ReturnsOutlineFallback()
    {
        var converter = ModelKindBadgeBrushConverter.Instance;
        var result = converter.Convert((ModelKind)999, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void AllConverters_ConvertBack_ThrowNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            ModelNsfwBadgeBrushConverter.Instance.ConvertBack(null, typeof(Brush), null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() =>
            ModelNsfwBadgeTextConverter.Instance.ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() =>
            ModelKindBadgeBrushConverter.Instance.ConvertBack(null, typeof(Brush), null, CultureInfo.InvariantCulture));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelBadgeConverterTests" --no-restore 2>&1 | tail -10`
Expected: 3 type-not-found compile errors (conversions don't exist yet)

- [ ] **Step 3: Add 3 converters to Views/Converters.cs**

After the `WorkflowSourceBadgeTextConverter` class (line 102, ending `}`), insert:

```csharp
/// <summary>
/// v0.6.20 T7:ModelNsfwKind → Brush。SFW=OutlineBrush(中灰),Mature=WarningBrush(橙),NSFW=ErrorBrush(红)。
/// palette fallback:ErrorBrush → (0xBA,0x1A,0x1A) 红,WarningBrush → (0xE6,0x7E,0x22) 橙,OutlineBrush → (0xCC,0xCC,0xCC) 灰。
/// </summary>
public sealed class ModelNsfwBadgeBrushConverter : IValueConverter
{
    public static readonly ModelNsfwBadgeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ModelNsfwKind.SFW => "OutlineBrush",
            ModelNsfwKind.Mature => "WarningBrush",
            ModelNsfwKind.NSFW => "ErrorBrush",
            _ => "OutlineBrush",
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return key switch
        {
            "ErrorBrush" => new SolidColorBrush(Color.FromRgb(0xBA, 0x1A, 0x1A)),
            "WarningBrush" => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
            _ => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.20 T7:ModelNsfwKind → string,NSFW badge pill 文案。SFW="SFW",Mature="Mature",NSFW="NSFW"。
/// </summary>
public sealed class ModelNsfwBadgeTextConverter : IValueConverter
{
    public static readonly ModelNsfwBadgeTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            ModelNsfwKind.SFW => "SFW",
            ModelNsfwKind.Mature => "Mature",
            ModelNsfwKind.NSFW => "NSFW",
            _ => "?",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.20 T7:ModelKind → Brush(8 kind 各自的 palette 颜色)。
/// Checkpoint=PrimaryBrush,LORA=SecondaryBrush,VAE=TertiaryBrush,Controlnet=SuccessBrush,
/// TextualInversion=WarningBrush,Upscaler=InfoBrush,Hypernetwork=ErrorBrush,Other/Unknown=OutlineBrush。
/// palette fallback 8 种颜色全部硬编码,确保无 Application.Current 时 XAML 不会 UnsetValue。
/// </summary>
public sealed class ModelKindBadgeBrushConverter : IValueConverter
{
    public static readonly ModelKindBadgeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (key, fallback) = value switch
        {
            ModelKind.Checkpoint       => ("PrimaryBrush",   Color.FromRgb(0x67, 0x50, 0xA4)),
            ModelKind.LORA             => ("SecondaryBrush", Color.FromRgb(0x4F, 0x6D, 0x8C)),
            ModelKind.VAE              => ("TertiaryBrush",  Color.FromRgb(0x6B, 0x8E, 0x23)),
            ModelKind.Controlnet       => ("SuccessBrush",   Color.FromRgb(0x38, 0x8E, 0x3C)),
            ModelKind.TextualInversion => ("WarningBrush",   Color.FromRgb(0xE6, 0x7E, 0x22)),
            ModelKind.Upscaler         => ("InfoBrush",      Color.FromRgb(0x19, 0x76, 0xD2)),
            ModelKind.Hypernetwork     => ("ErrorBrush",     Color.FromRgb(0xBA, 0x1A, 0x1A)),
            ModelKind.Other            => ("OutlineBrush",   Color.FromRgb(0x75, 0x75, 0x75)),
            _                          => ("OutlineBrush",   Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
```

- [ ] **Step 4: Register 3 converters in Theme.xaml**

After line 31 (`<views:WorkflowSourceBadgeTextConverter x:Key="WorkflowSourceBadgeText" />`), add:

```xml
<!-- v0.6.20 T7:模型卡片 NSFW + Kind badge pill — ModelNsfwKind → Brush/Text,ModelKind → Brush。 -->
<views:ModelNsfwBadgeBrushConverter x:Key="ModelNsfwBadgeBrush" />
<views:ModelNsfwBadgeTextConverter x:Key="ModelNsfwBadgeText" />
<views:ModelKindBadgeBrushConverter x:Key="ModelKindBadgeBrush" />
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelBadgeConverterTests" --no-restore 2>&1 | tail -10`
Expected: 11 PASS (1+1+1+1+1+8+1+1 = 14 test cases via Theory, count = 1+1+1+1+1+8+1+1)

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Views/Converters.cs \
  src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
  tests-wpf/ComfyUI.Manager.Tests/Views/ModelBadgeConverterTests.cs
git commit -m "feat(models): v0.6.20 T7 ModelNsfwBadge + ModelKindBadge converters"
```

---

### Task 8: ModelMarketplaceViewModel + View XAML

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs` (new)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Views/ModelMarketplaceViewLoadTests.cs` (new, STA-only — same pattern as `WorkflowMarketplaceViewLoadTests`)

**Interfaces:**
- Consumes: `ModelMarketplaceService` (T4), `ModelDownloader` (T5), `ModelFilesystemScanner` (T2), `AppLogger?` (existing), `Settings` (T1 — `ModelsDirectory`)
- Produces:
  - `ModelMarketplaceViewModel` — `ObservableCollection<ModelEntry> Models` + `SelectedVersions: ObservableCollection<ModelVersionEntry>` + `ObservableCollection<ModelKind> KindFilters` + `ActiveKindFilter: ModelKind?` + `Query: string` + `IsBusy: bool` + `ConsoleLog: ObservableCollection<string>` + `IsConsoleVisible: bool` + `RefreshCommand` + `DownloadSelectedCommand`
  - 3-state console visibility: `!_userHidden && (IsBusy || hasContent)` (same as `WorkflowMarketplaceViewModel` v0.6.19.x)
  - `DownloadSelectedCommand` calls `_downloader.BatchDownloadAsync(SelectedVersions.ToList(), ..., IProgress<string> log)` where log is wrapped in `Progress<string>` to capture UI `SynchronizationContext`

**Reuses:** `WorkflowMarketplaceViewModel` (v0.6.19) structure — same toolbar/filter strip/card grid/console panel pattern, adapted for model entry card with version list.

**Critical:**
- VM UI-bound awaits MUST NOT use `.ConfigureAwait(false)` (per `feedback_configureawait_false_placement.md` rule). Service-layer awaits inside `ModelDownloader` can use `.ConfigureAwait(false)` for thread-pool optimization, but VM-side awaits that continue with `Models.Clear()`/`Add()`/`SelectedVersions.Add()`/`ConsoleLog.Add()` must capture UI `SynchronizationContext`.

- [ ] **Step 1: Write failing tests for VM**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs
using System.Collections.ObjectModel;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class ModelMarketplaceViewModelTests
{
    private static ModelEntry MakeModel(int id, ModelKind kind, params (string vid, string name)[] versions)
    {
        return new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = id.ToString(),
            SourceUrl = $"https://civitai.com/models/{id}",
            Title = $"Model {id}",
            Kind = kind,
            NsfwKind = ModelNsfwKind.SFW,
            Versions = versions.Select(v => new ModelVersionEntry
            {
                SourceVersionId = v.vid,
                Name = v.name,
                PrimaryDownloadUrl = $"https://civitai.com/api/download/models/{v.vid}",
                SizeBytes = 1024,
                Files = new List<ModelFile> { new() { Name = "m.safetensors", SizeBytes = 1024, IsPrimary = true } }.AsReadOnly(),
                Parent = null!,
            }).ToList().AsReadOnly(),
        };
    }

    [Fact]
    public void Constructor_StartsEmpty()
    {
        var vm = new ModelMarketplaceViewModel(
            marketplaceService: null!,
            downloader: null!,
            scanner: null!,
            settings: null!,
            logger: null);
        Assert.Empty(vm.Models);
        Assert.Empty(vm.SelectedVersions);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public void KindFilters_ContainsAllModelKindValues()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(8, vm.KindFilters.Count);  // Unknown/Other + 7 explicit
        Assert.Contains(ModelKind.Checkpoint, vm.KindFilters);
        Assert.Contains(ModelKind.LORA, vm.KindFilters);
        Assert.Contains(ModelKind.VAE, vm.KindFilters);
    }

    [Fact]
    public void Query_Text_FiltersByTitle()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.Query = "Model 1";
        Assert.Single(vm.Models);
        Assert.Equal("1", vm.Models[0].SourceId);
    }

    [Fact]
    public void ActiveKindFilter_Set_FiltersByKind()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.ActiveKindFilter = ModelKind.LORA;
        Assert.Single(vm.Models);
        Assert.Equal(ModelKind.LORA, vm.Models[0].Kind);
    }

    [Fact]
    public void SelectedVersions_AddingVersion_FiresCollectionChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        var version = MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")).Versions[0];
        var changed = false;
        vm.SelectedVersions.CollectionChanged += (_, _) => changed = true;
        vm.SelectedVersions.Add(version);
        Assert.True(changed);
    }

    [Fact]
    public void ConsoleLog_AddLine_FiresIsConsoleVisibleChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsConsoleVisible)) changed = true;
        };
        vm.ConsoleLog.Add("hello");
        Assert.True(changed);
        Assert.True(vm.IsConsoleVisible);
    }

    [Fact]
    public void HideConsoleCommand_FiresPropertyChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("hello");
        Assert.True(vm.IsConsoleVisible);
        vm.HideConsoleCommand.Execute(null);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public void ClearConsoleCommand_RemovesAllLines()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("line 1");
        vm.ConsoleLog.Add("line 2");
        vm.ClearConsoleLogCommand.Execute(null);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);
    }
}

/// <summary>Mock marketplace — returns fixed list of models. Caller controls count via params.</summary>
internal sealed class MockModelMarketplaceService : ModelMarketplaceService
{
    private readonly List<ModelEntry> _entries;
    public MockModelMarketplaceService(params ModelEntry[] entries)
        : base(new HttpClient(), new List<IModelSource>(), null)
    {
        _entries = entries.ToList();
    }

    public override Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResultsPerSource, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelEntry>>(_entries);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewModelTests" --no-restore 2>&1 | tail -10`
Expected: 5+ compile errors (VM doesn't exist yet, MockModelMarketplaceService class doesn't exist)

- [ ] **Step 3: Write ModelMarketplaceViewModel**

Create `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class ModelMarketplaceViewModel : INotifyPropertyChanged
{
    private readonly ModelMarketplaceService _marketplace;
    private readonly ModelDownloader _downloader;
    private readonly ModelFilesystemScanner _scanner;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;
    private bool _userHiddenConsole;
    private string _query = "";
    private ModelKind? _activeKindFilter;

    public ObservableCollection<ModelEntry> Models { get; } = new();
    public ObservableCollection<ModelVersionEntry> SelectedVersions { get; } = new();
    public ObservableCollection<string> ConsoleLog { get; } = new();
    public ObservableCollection<ModelKind> KindFilters { get; } = new(Enum.GetValues<ModelKind>().Where(k => k != ModelKind.Unknown));

    public ICommand RefreshCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand ClearConsoleLogCommand { get; }
    public ICommand HideConsoleCommand { get; }
    public ICommand ToggleVersionSelectionCommand { get; }

    public ModelMarketplaceViewModel(
        ModelMarketplaceService marketplace,
        ModelDownloader downloader,
        ModelFilesystemScanner scanner,
        Settings settings,
        AppLogger? logger)
    {
        _marketplace = marketplace;
        _downloader = downloader;
        _scanner = scanner;
        _settings = settings;
        _logger = logger;
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        DownloadSelectedCommand = new RelayCommand(async _ => await DownloadSelectedAsync(), _ => SelectedVersions.Count > 0 && !IsBusy);
        ClearConsoleLogCommand = new RelayCommand(_ => { ConsoleLog.Clear(); });
        HideConsoleCommand = new RelayCommand(_ => { _userHiddenConsole = true; OnPropertyChanged(nameof(IsConsoleVisible)); });
        ToggleVersionSelectionCommand = new RelayCommand(p =>
        {
            if (p is ModelVersionEntry v)
            {
                if (SelectedVersions.Contains(v)) SelectedVersions.Remove(v);
                else SelectedVersions.Add(v);
            }
        });
        ConsoleLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsConsoleVisible));
    }

    public string Query
    {
        get => _query;
        set { _query = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public ModelKind? ActiveKindFilter
    {
        get => _activeKindFilter;
        set { _activeKindFilter = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConsoleVisible)); }
    }

    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    private List<ModelEntry> _allModels = new();

    public async Task RefreshAsync()
    {
        IsBusy = true;
        _userHiddenConsole = false;  // reset user-hidden on new refresh (3-state visibility)
        try
        {
            var results = await _marketplace.SearchAsync(_query, maxResultsPerSource: 50);
            _allModels = results.ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-marketplace", $"刷新失败: {ex.Message}");
            ConsoleLog.Add($"[错误] 刷新失败: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedVersions.Count == 0) return;
        IsBusy = true;
        _userHiddenConsole = false;
        try
        {
            var progress = new Progress<string>(line => ConsoleLog.Add(line));
            var versions = SelectedVersions.ToList();
            var summary = await _downloader.BatchDownloadAsync(
                versions, _settings.ModelsDirectory, progress);
            ConsoleLog.Add($"[完成] 成功 {summary.Succeeded},失败 {summary.Failed},耗时 {summary.TotalDuration.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            _logger?.Error("model-download", $"批量下载异常: {ex.Message}");
            ConsoleLog.Add($"[错误] {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        var filtered = _allModels.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_query))
            filtered = filtered.Where(m => m.Title.Contains(_query, StringComparison.OrdinalIgnoreCase));
        if (_activeKindFilter is { } k)
            filtered = filtered.Where(m => m.Kind == k);
        Models.Clear();
        foreach (var m in filtered) Models.Add(m);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Run VM tests to verify they pass**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewModelTests" --no-restore 2>&1 | tail -10`
Expected: 8 PASS

- [ ] **Step 5: Write STA load tests for View**

Create `tests-wpf/ComfyUI.Manager.Tests/Views/ModelMarketplaceViewLoadTests.cs`:

```csharp
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

[Collection("STA")]
public class ModelMarketplaceViewLoadTests
{
    [Fact]
    public void Load_EmptyVm_DoesNotThrow()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        var view = new ModelMarketplaceView { DataContext = vm };
        // Just exercising XAML parse path
        Assert.NotNull(view);
    }

    [Fact]
    public void Load_WithModels_DoesNotThrow()
    {
        var models = new[]
        {
            new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "Test", Kind = ModelKind.Checkpoint, NsfwKind = ModelNsfwKind.SFW,
                Versions = new List<ModelVersionEntry>().AsReadOnly() },
        };
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        foreach (var m in models) vm.Models.Add(m);
        var view = new ModelMarketplaceView { DataContext = vm };
        Assert.NotNull(view);
    }

    [Fact]
    public void Load_WithSelectedVersions_DoesNotThrow()
    {
        var entry = new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "T", Kind = ModelKind.Checkpoint, NsfwKind = ModelNsfwKind.SFW,
            Versions = new List<ModelVersionEntry>().AsReadOnly() };
        var v = new ModelVersionEntry { SourceVersionId = "v1", Name = "1.0", PrimaryDownloadUrl = "https://x", SizeBytes = 1, Files = new List<ModelFile>().AsReadOnly(), Parent = entry };
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.SelectedVersions.Add(v);
        var view = new ModelMarketplaceView { DataContext = vm };
        Assert.NotNull(view);
    }
}
```

- [ ] **Step 6: Run STA tests to verify they fail (compile error — View doesn't exist)**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewLoadTests" --no-restore 2>&1 | tail -10`
Expected: compile errors (View + STA collection don't exist)

- [ ] **Step 7: Write ModelMarketplaceView XAML**

Create `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml`:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.ModelMarketplaceView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             xmlns:models="clr-namespace:ComfyUI.Manager.Models"
             xmlns:behaviors="clr-namespace:ComfyUI.Manager.Behaviors"
             mc:Ignorable="d"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             d:DataContext="{d:DesignInstance Type=vm:ModelMarketplaceViewModel}">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>  <!-- toolbar -->
            <RowDefinition Height="Auto"/>  <!-- filter strip -->
            <RowDefinition Height="*"/>     <!-- card grid -->
            <RowDefinition Height="Auto"/>  <!-- console -->
        </Grid.RowDefinitions>

        <!-- Toolbar (refresh + download + search) -->
        <DockPanel Grid.Row="0" Margin="0,0,0,8" LastChildFill="True">
            <Button DockPanel.Dock="Left" Content="刷新" Command="{Binding RefreshCommand}" Margin="0,0,8,0" />
            <Button DockPanel.Dock="Left" Content="下载选中" Command="{Binding DownloadSelectedCommand}" Margin="0,0,8,0" />
            <TextBlock DockPanel.Dock="Left" VerticalAlignment="Center" Margin="0,0,12,0">
                <Run Text="选中: " />
                <Run Text="{Binding SelectedVersions.Count, Mode=OneWay}" FontWeight="Bold" />
            </TextBlock>
            <TextBox Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>

        <!-- Filter strip (kind chips) -->
        <ItemsControl Grid.Row="1" ItemsSource="{Binding KindFilters}" Margin="0,0,0,8">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <ToggleButton Content="{Binding}" Margin="2" Padding="8,4" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <!-- Card grid -->
        <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Models}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate DataType="{x:Type models:ModelEntry}">
                        <Border Width="220" Height="340" Margin="6" Padding="10" CornerRadius="8"
                                BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1">
                            <StackPanel>
                                <!-- title -->
                                <TextBlock Text="{Binding Title}" FontWeight="Bold" FontSize="14" TextWrapping="Wrap" />
                                <!-- kind + nsfw badges -->
                                <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                                    <Border CornerRadius="4" Padding="6,2" Margin="0,0,4,0"
                                            Background="{Binding Kind, Converter={StaticResource ModelKindBadgeBrush}}">
                                        <TextBlock Text="{Binding Kind}" Foreground="White" FontSize="11" />
                                    </Border>
                                    <Border CornerRadius="4" Padding="6,2"
                                            Background="{Binding NsfwKind, Converter={StaticResource ModelNsfwBadgeBrush}}">
                                        <TextBlock Text="{Binding NsfwKind, Converter={StaticResource ModelNsfwBadgeText}}" Foreground="White" FontSize="11" />
                                    </Border>
                                </StackPanel>
                                <!-- versions list with checkboxes -->
                                <ItemsControl ItemsSource="{Binding Versions}" Margin="0,8,0,0">
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate DataType="{x:Type models:ModelVersionEntry}">
                                            <CheckBox Content="{Binding Name}" Margin="0,2"
                                                      Command="{Binding DataContext.ToggleVersionSelectionCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                                      CommandParameter="{Binding}" />
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <!-- Console panel (3-state visibility) -->
        <Border Grid.Row="3" Margin="0,8,0,0" Padding="8" CornerRadius="6"
                BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
                Visibility="{Binding IsConsoleVisible, Converter={StaticResource BoolToVisibility}}">
            <DockPanel LastChildFill="True">
                <TextBlock DockPanel.Dock="Left" Text="{Binding ConsoleLog.Count, StringFormat='日志 ({0} 行)'}" FontWeight="Bold" VerticalAlignment="Center" />
                <Button DockPanel.Dock="Right" Content="✕" Command="{Binding HideConsoleCommand}" Width="24" Height="24" />
                <Button DockPanel.Dock="Right" Content="清空" Command="{Binding ClearConsoleLogCommand}" Width="48" Height="24" Margin="0,0,4,0" />
                <ScrollViewer Margin="8,0" Height="160" VerticalScrollBarVisibility="Auto">
                    <ItemsControl ItemsSource="{Binding ConsoleLog}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate><TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11" /></DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </DockPanel>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 8: Write View code-behind**

Create `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class ModelMarketplaceView : UserControl
{
    public ModelMarketplaceView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 9: Register STA collection**

In `tests-wpf/ComfyUI.Manager.Tests/` find the test collection definition (search `Collection("STA")` in existing test files). If missing, add to a shared `StaTestCollection.cs`:

```csharp
using Xunit;

namespace ComfyUI.Manager.Tests;

[CollectionDefinition("STA", DisableParallelization = true)]
public class StaTestCollection { }
```

If `WorkflowMarketplaceViewLoadTests` already defines this collection, just add `[Collection("STA")]` to the new test class.

- [ ] **Step 10: Run all T8 tests**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewModelTests|FullyQualifiedName~ModelMarketplaceViewLoadTests" --no-restore 2>&1 | tail -10`
Expected: 11 PASS (8 VM + 3 View STA)

- [ ] **Step 11: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs \
  src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml \
  src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml.cs \
  tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs \
  tests-wpf/ComfyUI.Manager.Tests/Views/ModelMarketplaceViewLoadTests.cs
git commit -m "feat(models): v0.6.20 T8 ModelMarketplaceViewModel + View XAML (kind chips + version checkboxes + console)"
```

---

### Task 9: MainViewModel + Sidebar + App DI + Env-Start Hook

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (add `ShowModelsCommand` + Models section in `MainSectionNameProvider`)
- Modify: `src-wpf/ComfyUI.Manager/Views/MainWindow.xaml` (add 9th sidebar RadioButton "模型市场")
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (add 2nd fire-and-forget model symlink hook after env-start)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (DI register: `ModelMarketplaceService` + `ModelDownloader` + `ModelFilesystemScanner` + `ModelSymlinker` + `CivitAiModelSource` + `HuggingFaceModelSource`)
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvironmentListViewModel.cs` (or the equivalent — find file by reading repo) — add `RefreshModelsSymlinksAsync(env)` invocation in `StartAsync` post-start hook (same fire-and-forget pattern as workflow hook)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelModelSectionTests.cs` (new)

**Interfaces:**
- Consumes: `ModelMarketplaceViewModel` (T8), `MainSectionNameProvider` (existing), `SidebarRadioButton` style (existing)
- Produces:
  - `MainViewModel.ShowModelsCommand` — sets `ActiveSection = "Models"` (new enum value, see `MainSectionNameProvider` refactor)
  - `MainWindow.xaml` sidebar RadioButton for "模型市场" — same pattern as the existing 8 entries (line ~70-110)
  - `App.xaml.cs` — registers `IModelSource` list with `CivitAiModelSource` (enabled per Settings) + `HuggingFaceModelSource` (always disabled in v0.6.20), plus `ModelMarketplaceService`, `ModelDownloader`, `ModelFilesystemScanner`, `ModelSymlinker`
  - `EnvironmentListViewModel.StartAsync` — after `ProcessLauncher.StartEnvAsync(...)` returns success, schedule second `Task.Run` that calls `_modelSymlinker.SyncToEnvAsync(envId, envRoot, ct)` (same try/catch WARN pattern as workflow hook)

**Critical:**
- VM UI-bound awaits (including the `ShowModelsCommand` body if it touches UI properties) MUST NOT use `.ConfigureAwait(false)`. Service-layer awaits inside `ModelSymlinker` can use `.ConfigureAwait(false)`.
- Fire-and-forget `Task.Run` MUST be wrapped in try/catch that logs WARN and never throws to the discarded task — pattern from v0.6.19 `WorkflowSymlinker.SyncToEnvAsync` integration.

- [ ] **Step 1: Write failing tests for MainViewModel Models section**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelModelSectionTests.cs
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainViewModelModelSectionTests
{
    [Fact]
    public void ShowModelsCommand_SetsActiveSectionToModels()
    {
        var vm = new MainViewModel(
            envList: null!, catalog: null!, settings: null!, workflowMarketplace: null!,
            dashboard: null!, diagnostics: null!);
        vm.ShowModelsCommand.Execute(null);
        Assert.Equal("Models", vm.ActiveSection);
    }

    [Fact]
    public void ActiveSection_DefaultsToFirstSection()
    {
        var vm = new MainViewModel(
            envList: null!, catalog: null!, settings: null!, workflowMarketplace: null!,
            dashboard: null!, diagnostics: null!);
        // The existing default should be preserved; just check that it's not "Models" initially
        Assert.NotEqual("Models", vm.ActiveSection);
    }
}
```

- [ ] **Step 2: Run tests to verify compile failure**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~MainViewModelModelSectionTests" --no-restore 2>&1 | tail -10`
Expected: compile errors — `ShowModelsCommand` doesn't exist; `MainViewModel` constructor signature may not match.

- [ ] **Step 3: Add `ShowModelsCommand` + `ActiveSection="Models"` to MainViewModel**

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:

1. Add a new `ShowModelsCommand` RelayCommand alongside existing `ShowWorkflowsCommand` / `ShowCatalogCommand` / etc.:
   ```csharp
   public ICommand ShowModelsCommand { get; }
   ```

2. In the constructor, after `ShowWorkflowsCommand = new RelayCommand(_ => ActiveSection = "Workflows");`:
   ```csharp
   ShowModelsCommand = new RelayCommand(_ => ActiveSection = "Models");
   ```

3. Confirm `ActiveSection` setter is already public — if private, change to public with setter that calls `OnPropertyChanged`.

- [ ] **Step 4: Add "模型市场" sidebar RadioButton in MainWindow.xaml**

After the existing 8 sidebar RadioButtons (find line by searching `工作流市场` in MainWindow.xaml), add:

```xml
<RadioButton Content="模型市场" Style="{StaticResource SidebarRadioButton}"
             Command="{Binding ShowModelsCommand}"
             IsChecked="{Binding ActiveSection, Converter={StaticResource SectionEquality}, ConverterParameter=Models}" />
```

- [ ] **Step 5: Wire ModelMarketplaceView into MainWindow content panel**

In `MainWindow.xaml` content area (after the WorkflowMarketplaceView DataTemplate), add a content presenter or DataTemplate that shows ModelMarketplaceView when `ActiveSection == "Models"`:

```xml
<DataTemplate DataType="{x:Type vm:ModelMarketplaceViewModel}">
    <views:ModelMarketplaceView />
</DataTemplate>
```

(Or use the existing ContentControl pattern that maps section name to view. If using ContentControl with DataTemplate selection, ensure the section's DataContext resolves to `ModelMarketplaceViewModel` — this requires `MainViewModel` to expose a `ModelsMarketplace` property of type `ModelMarketplaceViewModel`.)

Add property to `MainViewModel`:
```csharp
public ModelMarketplaceViewModel? ModelsMarketplace { get; init; }
```

And in `App.xaml.cs` DI wire-up: pass the constructed `ModelMarketplaceViewModel` instance into `MainViewModel` constructor as `ModelsMarketplace`.

- [ ] **Step 6: Register DI in App.xaml.cs**

In `src-wpf/ComfyUI.Manager/App.xaml.cs`, find the existing `ConfigureServices` (or equivalent) method that registers all services. Add the model marketplace registrations:

```csharp
// v0.6.20 T9:模型市场 DI 接入
services.AddSingleton<HttpClient>();
services.AddSingleton<ModelFilesystemScanner>();
services.AddSingleton<CivitAiModelSource>();
services.AddSingleton<HuggingFaceModelSource>();
services.AddSingleton<IModelSourceResolver, ModelSourceResolver>();  // wraps list + IsEnabled gating
services.AddSingleton<ModelMarketplaceService>();
services.AddSingleton<ModelDownloader>();
services.AddSingleton<ModelSymlinker>();
services.AddSingleton<ModelMarketplaceViewModel>();

// pass ModelsMarketplace into MainViewModel construction
services.AddSingleton<MainViewModel>(sp => new MainViewModel(
    sp.GetRequiredService<EnvironmentListViewModel>(),
    sp.GetRequiredService<CatalogViewModel>(),
    sp.GetRequiredService<SettingsViewModel>(),
    sp.GetRequiredService<WorkflowMarketplaceViewModel>(),
    sp.GetRequiredService<ModelMarketplaceViewModel>(),  // NEW
    sp.GetRequiredService<DashboardViewModel>(),
    sp.GetRequiredService<DiagnosticsViewModel>()));
```

**Note**: `IModelSourceResolver` is a small wrapper around the list of `IModelSource` instances + filters by `IsEnabled` flag. Define it inline:

```csharp
public interface IModelSourceResolver
{
    IReadOnlyList<IModelSource> ResolveEnabled();
}

public sealed class ModelSourceResolver : IModelSourceResolver
{
    private readonly IEnumerable<IModelSource> _sources;
    private readonly Settings _settings;
    public ModelSourceResolver(IEnumerable<IModelSource> sources, Settings settings)
    {
        _sources = sources;
        _settings = settings;
    }
    public IReadOnlyList<IModelSource> ResolveEnabled() => _sources
        .Where(s => s.SourceKind == ModelSourceKind.CivitAi ? _settings.ModelSourceCivitAiEnabled : s.IsEnabled)
        .ToList();
}
```

Place `IModelSourceResolver` + `ModelSourceResolver` in a new file `src-wpf/ComfyUI.Manager/Services/ModelSourceResolver.cs`. `ModelMarketplaceService` constructor should accept `IEnumerable<IModelSource>` and use `_resolver.ResolveEnabled()` to filter.

**Adjust T4** if `ModelMarketplaceService` constructor signature differs. Update plan inline.

- [ ] **Step 7: Add fire-and-forget model symlink hook in EnvironmentListViewModel.StartAsync**

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`, find the existing `StartAsync` method. After the existing workflow symlink fire-and-forget (search `_workflowSymlinker.SyncToEnvAsync` or similar pattern), add:

```csharp
// v0.6.20 T9:env 启动后,fire-and-forget 同步模型 junction
_ = Task.Run(async () =>
{
    try
    {
        await _modelSymlinker.SyncToEnvAsync(env.Id, env.PythonInterpreterPath ?? env.InstallPath, default);
    }
    catch (Exception ex)
    {
        _logger?.Warn("model-symlink", $"env '{env.Id}' junction sync 失败(忽略): {ex.Message}");
    }
});
```

(Adjust parameter names based on the actual `StartAsync` signature in the repo. The key idea is: after env-start success, schedule a Task.Run that calls ModelSymlinker.SyncToEnvAsync and swallows exceptions with WARN log.)

Also add `_modelSymlinker` field + ctor parameter:

```csharp
private readonly ModelSymlinker _modelSymlinker;

public EnvironmentListViewModel(/* existing params */, ModelSymlinker modelSymlinker, AppLogger? logger)
{
    // ...
    _modelSymlinker = modelSymlinker;
    _logger = logger;
}
```

Update all existing test constructors that pass to `EnvironmentListViewModel` to include the new `ModelSymlinker` parameter (pass `null!` or a real instance with mocks as appropriate).

- [ ] **Step 8: Run T9 tests**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~MainViewModelModelSectionTests" --no-restore 2>&1 | tail -10`
Expected: 2 PASS

- [ ] **Step 9: Run full suite to catch constructor regressions**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --no-restore 2>&1 | tail -20`
Expected: no regressions (all previously-passing tests still pass; EnvironmentListViewModel test ctor changes accommodated by T9 step 7)

- [ ] **Step 10: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
  src-wpf/ComfyUI.Manager/Views/MainWindow.xaml \
  src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
  src-wpf/ComfyUI.Manager/App.xaml.cs \
  src-wpf/ComfyUI.Manager/Services/ModelSourceResolver.cs \
  tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelModelSectionTests.cs
git commit -m "feat(models): v0.6.20 T9 sidebar 9th RadioButton + DI + env-start model symlink hook"
```

---

### Task 10: Final Review + MEMORY + Staging Rebuild + GUI Smoke

**Files:**
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_20_model_marketplace.md` (update final status)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (add v0.6.20 entry)

**Final review dispatch:**
Dispatch a code-reviewer subagent on the v0.6.20 branch diff (base = `4e4bf7b`, HEAD = current T9 commit) using the `requesting-code-review` skill. Apply any critical/important findings via fix waves before declaring SHIP-READY.

**Staging rebuild:** (BLOCKED until user closes their v0.6.19 staging exe PID 14212)
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"
```

**GUI smoke (after staging unblocked):**
1. Open app → verify 9 sidebar items including "模型市场"
2. Click "模型市场" → verify ModelMarketplaceView loads without error
3. Click "刷新" → verify Console shows "[完成]" with 0 lines (or model count)
4. Type "checkpoint" in search → verify list filters
5. Click a kind chip → verify filter applies
6. Check 2 versions on same model → verify both added to SelectedVersions
7. Click "下载选中" → verify Console shows progress
8. Verify file appears in `<projectRoot>/models/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/`
9. Verify meta.json sidecar exists with all required fields
10. Start an env → verify env-side junction appears at `<env>/ComfyUI/models/<kind>/<model-slug>-<id8>__<version-slug>-<vid8>`
11. Verify junction is valid: `dir <junction-path>` shows target
12. Open ComfyUI web UI → verify model loads from the linked path

- [ ] **Step 1: Dispatch final code review**

Dispatch using `requesting-code-review` skill:
```bash
git diff 4e4bf7b..HEAD --stat
git diff 4e4bf7b..HEAD > /tmp/v0.6.20-final.diff
```
Send diff + spec + plan to the reviewer. Apply findings via fix waves.

- [ ] **Step 2: Verify full test suite green**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests --no-restore 2>&1 | tail -10`
Expected: ~1470 PASS / 0 FAIL / 5 SKIP (post-SDD baseline from v0.6.19.x hotfix + ~49-55 new tests + 1 new SKIP for CivitAI real-fetch)

- [ ] **Step 3: Update MEMORY.md + project_v0_6_20_model_marketplace.md**

In `MEMORY.md` add a new top-level entry:
```
- [v0.6.20 模型市场](project_v0_6_20_model_marketplace.md) — ✓ SHIP-READY 2026-08-XX,<baseline + N> PASS,<branch> HEAD,<~XX files> +X/-Y;...
```

In `project_v0_6_20_model_marketplace.md`, update the "下个 session 第一步" section to:
- Replace with "✓ SHIP-READY 2026-08-XX" + commit hashes + final test counts
- Note GUI smoke status (12 steps 桌面待验证 or 已验证)

- [ ] **Step 4: Staging rebuild (blocked)**

Run the publish command above. If it fails with file lock on `ComfyUI Manager.exe`, prompt user to close their v0.6.19 staging exe and retry.

- [ ] **Step 5: GUI smoke (blocked until staging rebuilt)**

Walk through 12-step list above, document any issues, fix in fix wave, re-smoke.

- [ ] **Step 6: Commit memory updates**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  C:/Users/徐鹏/.claude/projects/D--ToolDevelop-ComfyUI/memory/project_v0_6_20_model_marketplace.md \
  C:/Users/徐鹏/.claude/projects/D--ToolDevelop-ComfyUI/memory/MEMORY.md
git commit -m "docs(memory): v0.6.20 model marketplace SHIP-READY status"
```

---

## Estimated Effort

- **T1**: ~30min (Settings changes + 4 tests)
- **T2**: ~1.5h (5 DTOs + ModelFilesystemScanner + 5 tests)
- **T3**: ~2.5h (IModelSource + CivitAI full impl + HF stub + 10 tests)
- **T4**: ~45min (MarketplaceService + 5 tests)
- **T5**: ~2h (ModelDownloader streaming + batch + atomic rename + 8 tests)
- **T6**: ~1.5h (ModelSymlinker + 5 tests)
- **T7**: ~45min (3 converters + 14 tests)
- **T8**: ~2h (ViewModel + View XAML + 11 tests)
- **T9**: ~1.5h (MainViewModel + sidebar + DI + env-start hook + 2 tests)
- **T10**: ~1h (final review + fix wave + MEMORY + staging)

**Total: ~13h (~13 commits)**

## Target Test Counts

- Post-v0.6.19.x baseline: ~1421 PASS / 0 FAIL / 4 SKIP
- v0.6.20 new tests: ~49-55 (4+5+10+5+8+5+14+11+2 = ~64, minus overlaps)
- Target: ~1470 PASS / 0 FAIL / 5 SKIP (1 new CivitAI real-fetch SKIP)

## Key Patterns to Reuse from v0.6.19

1. **IProgress<string> for log streaming** (v0.6.5.11 / v0.6.18.4): wrap in `Progress<string>` at VM boundary, capture UI SynchronizationContext.
2. **3-state visibility**: `!_userHidden && (IsBusy || hasContent)` for console panel.
3. **Fire-and-forget after env-start**: `Task.Run` + try/catch WARN log.
4. **Per-version selection**: `SelectedVersions: ObservableCollection<ModelVersionEntry>` (granular).
5. **Atomic file rename**: write `.partial`, `File.Move(.partial, final, overwrite: true)`.
6. **Slug + 8-char id + collision suffix**: `/<slug>-<id8>[-N]/`.
7. **HttpClient streaming**: `HttpCompletionOption.ResponseHeadersRead` + manual `CopyToAsync`.
8. **Junction on Windows, symlink on Linux/macOS**: same `JunctionLinker` helper from v0.6.19.
9. **VM UI-bound awaits MUST NOT use `.ConfigureAwait(false)`** (per `feedback_configureawait_false_placement.md` rule).
10. **`Path.GetDirectoryName(System.Environment.ProcessPath)` fallback** for relative path resolution.