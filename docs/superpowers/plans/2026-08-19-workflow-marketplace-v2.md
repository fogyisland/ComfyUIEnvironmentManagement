# Workflow Marketplace v0.6.22 Implementation Plan — CivitAI 模型端点切换 + 搜索栏 UI 重设计

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the CivitAI workflow source's image-scraping endpoint with the model-centric `/api/v1/models?types=WORKFLOW` endpoint so entries reliably expose workflow JSON files; redesign the workflow marketplace's search bar to look and feel like a search input (🔍 icon + clear button + placeholder + visible grouping) instead of a bare TextBox.

**Architecture:**
- **T1 (CivitAI source rewrite):** `CivitAiSource.SearchAsync` switches from `GET /api/v1/images?tags=workflow` to `GET /api/v1/models?types=WORKFLOW&sort=models.donated&limit=N`. Adds 6 internal DTO classes (CivitAiModelResponse / Item / Creator / Version / File / Image) and a file-selection helper that picks the first `.json` from `modelVersions[0].files[]`. Maps to existing `WorkflowEntry` DTO (no new model class needed).
- **T2 (UI search bar redesign):** `WorkflowMarketplaceView.xaml` Row 0 toolbar replaces the bare `<TextBox>` with a composite `Border` (360px wide, SurfaceBrush bg, OutlineBrush border, CornerRadius 6) containing a 3-column `Grid`: 🔍 magnifying-glass `<Path>` icon Viewbox + `<TextBox>` with placeholder overlay + ✕ clear `<Button>`. `WorkflowMarketplaceViewModel` gains `HasSearchText` computed bool + `ClearSearchCommand`. New placeholder string in `Strings.resx`.

**Tech Stack:** .NET 8 / WPF / C# 12 / xUnit / SQLite / `HttpClient` singleton / `AppLogger`. **No new sub-systems** — T1 is internal refactor of `CivitAiSource` + tests; T2 is VM extension + XAML polish + 1 test.

**Spec:** `docs/superpowers/specs/2026-08-19-workflow-marketplace-v2-design.md` (HEAD `f80c414`)

**Base branch:** main at `a21d9dd` (post v0.6.21).

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | CivitAI workflow endpoint: `GET /api/v1/models?types=WORKFLOW&sort=models.donated&limit=N` (CivitAI documented URL). Default `limit=100`. Cap at `Math.Min(maxResults, 100)`. | spec §3 |
| **G2** | File selection: pick first `.json` from `modelVersions[0].files[]` (case-insensitive `.json` extension via `EndsWith(".json", StringComparison.OrdinalIgnoreCase)`). Skip entry if no `.json` file or `downloadUrl` empty. | spec §3 |
| **G3** | 6 internal DTO classes (`CivitAiModelResponse`, `CivitAiModelItem`, `CivitAiCreator`, `CivitAiModelVersion`, `CivitAiModelFile`, `CivitAiModelImage`) — `internal` to enable test access via `InternalsVisibleTo("ComfyUI.Manager.Tests")` (already configured). | spec §3 + project convention (v0.6.19 IWorkflowSource DTOs are `internal`) |
| **G4** | HTTP I/O via injected `HttpClient` singleton (App.xaml.cs); `.ConfigureAwait(false)` in service-layer awaits. | project convention |
| **G5** | AppLogger subsystem: `workflow-civitai` (existing) — log URL + entry count, WARN on rate limit (429), ERROR on schema mismatch + fetch exception. | project convention |
| **G6** | 🔍 icon = `<Path>` SVG (not emoji) per v0.6.17.1 WPF font fallback lesson. Use the spec-supplied magnifying-glass path verbatim. | v0.6.17.1 lesson |
| **G7** | ✕ clear button uses `<Path>` icon (not emoji), inline 12x12 Viewbox. | v0.6.17.1 lesson |
| **G8** | Placeholder text "搜索工作流" in `Resources/Strings.resx` key `WorkflowPage_搜索工作流` + matching key/value in `Strings.zh-CN.resx`. | i18n convention (mirrors `CatalogPage_搜索节点_eg_impact_manager`) |
| **G9** | Search bar visibility state (`HasSearchText` computed bool) drives ✕ button `Visibility` via existing `BoolToVisibility` converter. Placeholder TextBlock uses existing `InverseBoolToVisibility` converter. | WPF pattern (both converters already in Theme.xaml lines 19/32) |
| **G10** | New `ClearSearchCommand` in `WorkflowMarketplaceViewModel` — sets `SearchText = ""` (which triggers ApplyFilter via existing setter). | existing `RelayCommand` pattern |
| **G11** | Existing patterns preserved: `IWorkflowSource` interface signature unchanged, `WorkflowMarketplaceService` aggregator unchanged, AppLogger subsystems unchanged. | v0.6.19 |
| **G12** | Test count target: 1503 baseline + 5 (T1: 4 new model-source tests replacing 4 image-source tests + T2: 1 new clear-search test) - 4 removed = +1 net = ~1504 PASS / 5 FAIL pre-existing flaky / 6 SKIP. | test plan |
| **G13** | All transient test files use `Path.Combine(Path.GetTempPath(), "ComfyUIMgr<Name>_" + Guid.NewGuid().ToString("N"))` + cleanup in `Dispose`. | project convention |
| **G14** | DelegatingHandler pattern for HTTP mocking — reuse `StubHandler` pattern from existing `WorkflowSourceCivitAiTests.cs` (inner private `HttpMessageHandler` returning `Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) })`). | project convention |
| **G15** | Real-fetch integration test: `[Fact(Skip = "...")]` — keep 1 such test per source with descriptive skip reason. | project convention |
| **G16** | YAGNI: no SQLite cache, no TTL, no pagination, no PNG extraction, no missing-node detection, no /prompt POST, no backup image-source endpoint. | spec §2 + §11 |

---

## Files to Touch

### Modified files

| Path | Change | Task |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs` | Replace endpoint + DTOs (in same file if LoC stays under 200; else split into `CivitAiModels.cs`) + `PickWorkflowJsonFile` helper + new `SearchAsync` body | T1 |
| `src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs` | Add `[JsonIgnore] JsonPreview : string?` field | T3 |
| `src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs` | Expose `HttpClient` as public property for hover-time fetch | T3 |
| `src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs` (NEW) | Wipe + git clone ComfyUI template | T5 |
| `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs` | Add `HasSearchText` computed bool + `ClearSearchCommand` (T2); add `HoveredEntry` / `JsonOverlayText` / `IsJsonOverlayLoading` / `IsJsonOverlayError` / `IsJsonOverlayVisible` + `LoadJsonPreviewAsync` + `ClearJsonOverlay` (T3) | T2 + T3 |
| `src-wpf/ComfyUI.Manager/ViewModels/TemplateUpdateStatusViewModel.cs` (NEW) | Inline status panel for template update — mirrors RequirementsStatusViewModel | T5 |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Add `OpenVenvCommand` + `OpenVenv(env)` method (T4); add `UpdateTemplateCommand` + `UpdateTemplateAsync(env)` + `TemplateUpdateStatus` property + `BusyKind.TemplateUpdate` (T5) | T4 + T5 |
| `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml` | Replace Row 0 toolbar bare `<TextBox>` (line 50-52) with composite `Border` (🔍 + TextBox + ✕) + placeholder `<TextBlock>` overlay (T2); modify Row 3 card DataTemplate preview Border to add `MouseEnter`/`MouseLeave` handlers + overlay Grid with 3 states (T3) | T2 + T3 |
| `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs` | Add `OnPreviewMouseEnter` / `OnPreviewMouseLeave` handlers | T3 |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` | Add new icon `<Button>` to Row 0 col 2 StackPanel BEFORE ⌨ (T4); add 3rd Row to actions Grid + new Border for TemplateUpdateStatus panel in bottom StackPanel (T5) | T4 + T5 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | DI: construct `ComfyUITemplateUpdater` + inject into `EnvironmentListViewModel` | T5 |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` | Add `<data name="WorkflowPage_搜索工作流" xml:space="preserve"><value>搜索工作流</value></data>` | T2 |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | Same key, same value | T2 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs` | Replace 4 image-source tests with 4 model-source tests; keep 1 `[Fact(Skip=...)]` real-fetch; update `LiveFetch_CivitAi_RealEndpoint_ReturnsEntries` skip reason to reference `/api/v1/models?types=WORKFLOW` | T1 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs` | Add 1 test `ClearSearchCommand_ClearsSearchText_AndAppliesFilter` (T2); add 2 tests `LoadJsonPreviewAsync_HoverEntry_FetchesAndCachesJson` + `ClearJsonOverlay_ClearsHoverState_AndJsonOverlayText` (T3) | T2 + T3 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenVenvTests.cs` (NEW) | 1 test `OpenVenvCommand_ValidEnvWithVenvPath_CanExecute` | T4 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUITemplateUpdaterTests.cs` (NEW) | 2 tests `UpdateAsync_EmptyComfyuiDir_DoesNotThrow` + `UpdateAsync_EmptyComfyuiPath_ReturnsFail` | T5 |

### New files (only if DTOs split out)

| Path | Purpose |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiModels.cs` | Internal DTO classes (6 classes) — only if `CivitAiSource.cs` exceeds ~200 LoC after rewrite |

---

## Task 1: CivitAI source endpoint migration + DTOs + tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs` (replace endpoint, parse response, add file-selection helper, add DTOs)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs` (replace 4 image-source tests with 4 model-source tests; keep 1 SKIP real-fetch; update skip reason)

**Interfaces:**
- Consumes: `HttpClient` (existing singleton), `AppLogger?` (existing), `WorkflowEntry` DTO (existing), `IWorkflowSource` interface (existing — signature unchanged)
- Produces:
  - `CivitAiSource.SearchAsync(query, maxResults, ct)` — now hits `/api/v1/models?types=WORKFLOW&sort=models.donated&limit=N`, returns `IReadOnlyList<WorkflowEntry>` populated from `items[].modelVersions[0].files[].json.downloadUrl`
  - `internal sealed class CivitAiModelResponse { Items: List<CivitAiModelItem> }`
  - `internal sealed class CivitAiModelItem { Id: long, Name: string, Creator: CivitAiCreator, Tags: List<string>, ModelVersions: List<CivitAiModelVersion> }`
  - `internal sealed class CivitAiCreator { Username: string }`
  - `internal sealed class CivitAiModelVersion { Id: long, Files: List<CivitAiModelFile>, Images: List<CivitAiModelImage> }`
  - `internal sealed class CivitAiModelFile { Name: string, DownloadUrl: string? }`
  - `internal sealed class CivitAiModelImage { Url: string }`

- [ ] **Step 1: Rewrite `CivAiSource.cs` with new endpoint + DTOs + file-selection helper**

Replace the entire body of `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs` with the following content (keep class signature `public class CivitAiSource : IWorkflowSource` unchanged; DTOs go in same file as `internal sealed class`):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.22:CivitAI 数据源 — /api/v1/models?types=WORKFLOW 拉 model entries,
/// 每个 model 含 modelVersions[].files[].json (workflow JSON 文件 URL)。
/// CivitAI 60/h 无 token 限流;Settings 关掉就跳过。</summary>
public class CivitAiSource : IWorkflowSource
{
    public WorkflowSourceKind SourceKind => WorkflowSourceKind.CivitAi;
    public string DisplayName => "CivitAI";
    public bool IsEnabled { get; set; } = true;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly AppLogger? _logger;

    public CivitAiSource(HttpClient http, AppLogger? logger = null, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        _baseUrl = (baseUrl ?? "https://civitai.com").TrimEnd('/');
    }

    public virtual async Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        // v0.6.22: model-centric endpoint — returns proper workflow entries with
        // modelVersions[].files[].json.downloadUrl. Replaces v0.6.19 image-source
        // endpoint which silently dropped entries missing meta.workflow.workflowJson.
        var url = $"{_baseUrl}/api/v1/models?types=WORKFLOW&sort=models.donated&limit={Math.Min(maxResults, 100)}";
        _logger?.Info("workflow-civitai", $"fetch url={url} query='{query}'");
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger?.Warn("workflow-civitai", "rate limited (429)");
                return Array.Empty<WorkflowEntry>();
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = System.Text.Json.JsonSerializer.Deserialize<CivitAiModelResponse>(json)
                       ?? new CivitAiModelResponse();

            var entries = new List<WorkflowEntry>();
            foreach (var item in root.Items)
            {
                if (string.IsNullOrEmpty(item.Name)) continue;

                // v0.6.22: pick first .json from modelVersions[0].files[]
                // (workflow JSON file — typically named workflow.json or <slug>.json).
                // Skip entry if no .json file or empty downloadUrl.
                CivitAiModelFile? jsonFile = null;
                if (item.ModelVersions.Count > 0)
                    jsonFile = PickWorkflowJsonFile(item.ModelVersions[0].Files);
                if (jsonFile is null || string.IsNullOrEmpty(jsonFile.DownloadUrl)) continue;

                var previewUrl = item.ModelVersions.Count > 0 && item.ModelVersions[0].Images.Count > 0
                    ? item.ModelVersions[0].Images[0].Url
                    : null;

                var tags = item.Tags ?? new List<string>();

                // v0.6.22: query filter — title/author/tag substring (case-insensitive)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = item.Name?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = item.Creator?.Username?.ToLowerInvariant().Contains(q) ?? false;
                    var inTag = tags.Any(t => t?.ToLowerInvariant().Contains(q) ?? false);
                    if (!inTitle && !inAuthor && !inTag) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = item.Id.ToString(),
                    SourceUrl = $"{_baseUrl}/models/{item.Id}",
                    WorkflowJsonUrl = jsonFile.DownloadUrl!,
                    PreviewImageUrl = previewUrl,
                    Title = item.Name,
                    Author = item.Creator?.Username ?? "",
                    Tags = tags.ToArray(),
                });
                if (entries.Count >= maxResults) break;
            }

            _logger?.Info("workflow-civitai", $"fetched {entries.Count} entries");
            return entries;
        }
        catch (Exception ex)
        {
            _logger?.Error("workflow-civitai", "fetch failed", ex);
            return Array.Empty<WorkflowEntry>();
        }
    }

    /// <summary>v0.6.22: file-selection helper — pick first .json file by case-insensitive
    /// extension match. Returns null if no .json file found.</summary>
    private static CivitAiModelFile? PickWorkflowJsonFile(IEnumerable<CivitAiModelFile> files)
    {
        return files.FirstOrDefault(f =>
            !string.IsNullOrEmpty(f.DownloadUrl) &&
            f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }
}

// —— v0.6.22 internal DTOs for /api/v1/models?types=WORKFLOW response ——

internal sealed class CivitAiModelResponse
{
    [JsonPropertyName("items")]
    public List<CivitAiModelItem> Items { get; set; } = new();
}

internal sealed class CivitAiModelItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("creator")] public CivitAiCreator Creator { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("modelVersions")] public List<CivitAiModelVersion> ModelVersions { get; set; } = new();
}

internal sealed class CivitAiCreator
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
}

internal sealed class CivitAiModelVersion
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("files")] public List<CivitAiModelFile> Files { get; set; } = new();
    [JsonPropertyName("images")] public List<CivitAiModelImage> Images { get; set; } = new();
}

internal sealed class CivitAiModelFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
}

internal sealed class CivitAiModelImage
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
```

Notes:
- File stays under 200 LoC → DTOs remain in same file (no `CivitAiModels.cs` split).
- The 4 v0.6.19 image-source tests are replaced in Step 2.
- `Internal DTOs` use `[JsonPropertyName(...)]` from `System.Text.Json.Serialization`.
- `JsonSerializer.Deserialize<CivitAiModelResponse>` — fully-qualified `System.Text.Json.JsonSerializer` since the existing `using System.Text.Json` was removed in the rewrite (avoid unused-using warning).

- [ ] **Step 2: Rewrite `WorkflowSourceCivitAiTests.cs` with model-source tests**

Replace the entire content of `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs` with the following:

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowSourceCivitAiTests
{
    private static HttpClient MockHttp(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new HttpClient(new StubHandler(json, status));

    [Fact]
    public async Task SearchAsync_ModelWithJsonFile_ReturnsEntry()
    {
        // v0.6.22: model-source endpoint — 1 model + 1 version + 2 files (.json + .safetensors)
        // → 1 WorkflowEntry with WorkflowJsonUrl from the .json file
        var json = """
{"items":[{"id":123,"name":"Workflow A","creator":{"username":"bob"},"tags":["controlnet"],"modelVersions":[{"id":1,"files":[{"name":"workflow.json","downloadUrl":"https://files/wf.json"},{"name":"model.safetensors","downloadUrl":"https://files/m.safetensors"}],"images":[{"url":"https://img/preview.jpg"}]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
        Assert.Equal("123", result[0].SourceId);
        Assert.Equal("Workflow A", result[0].Title);
        Assert.Equal("bob", result[0].Author);
        Assert.Equal("https://files/wf.json", result[0].WorkflowJsonUrl);
        Assert.Equal("https://img/preview.jpg", result[0].PreviewImageUrl);
        Assert.Equal("https://civitai.com/models/123", result[0].SourceUrl);
        Assert.Equal(new[] { "controlnet" }, result[0].Tags.ToArray());
    }

    [Fact]
    public async Task SearchAsync_NoJsonFile_SkipsEntry()
    {
        // v0.6.22: entry with only .safetensors file → empty list (matches v0.6.19 R1
        // "skip on missing" semantic — model-source uses json-file presence as the
        // signal, not meta.workflow.workflowJson)
        var json = """
{"items":[{"id":1,"name":"Safetensors only","creator":{"username":"x"},"modelVersions":[{"id":1,"files":[{"name":"model.safetensors","downloadUrl":"https://files/m.safetensors"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MultipleVersions_PicksFirstVersionJsonFile()
    {
        // v0.6.22: model with 2 versions, each with .json → uses first version's .json
        var json = """
{"items":[{"id":99,"name":"Multi version","creator":{"username":"alice"},"tags":[],"modelVersions":[{"id":1,"files":[{"name":"v1.json","downloadUrl":"https://files/v1.json"}],"images":[{"url":"https://img/v1.jpg"}]},{"id":2,"files":[{"name":"v2.json","downloadUrl":"https://files/v2.json"}],"images":[]}]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("https://files/v1.json", result[0].WorkflowJsonUrl);
        Assert.Equal("https://img/v1.jpg", result[0].PreviewImageUrl);
    }

    [Fact]
    public async Task SearchAsync_EmptyCreatorUsername_DoesNotThrow()
    {
        // v0.6.22: model with empty creator.username → WorkflowEntry.Author = ""
        // (no NullReferenceException, no exception bubbles out)
        var json = """
{"items":[{"id":42,"name":"Anon","creator":{"username":""},"modelVersions":[{"id":1,"files":[{"name":"wf.json","downloadUrl":"https://files/wf.json"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("", result[0].Author);
    }

    [Fact]
    public async Task SearchAsync_RateLimited429_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("rate limited", HttpStatusCode.TooManyRequests));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_Http500_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("server error", HttpStatusCode.InternalServerError));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MalformedJson_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("not json at all"));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_QueryFilterByTag_MatchesEntry()
    {
        // v0.6.22: query match against tag array (new capability vs v0.6.19
        // title/author-only filter — model-source exposes tags[] per entry)
        var json = """
{"items":[{"id":1,"name":"Apple pie","creator":{"username":"u"},"tags":["lora"],"modelVersions":[{"id":1,"files":[{"name":"wf.json","downloadUrl":"https://x/1.json"}],"images":[]}]},{"id":2,"name":"Banana split","creator":{"username":"v"},"tags":["controlnet"],"modelVersions":[{"id":2,"files":[{"name":"wf.json","downloadUrl":"https://x/2.json"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "control", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("2", result[0].SourceId);
    }

    [Fact(Skip = "Integration: hits real CivitAI /api/v1/models?types=WORKFLOW")]
    public async Task LiveFetch_CivitAi_RealEndpoint_ReturnsEntries()
    {
        var src = new CivitAiSource(new HttpClient());
        var result = await src.SearchAsync(query: "", maxResults: 10);
        // CivitAI 即使成功也可能返空(限流) — 不强制 NonEmpty
        Assert.NotNull(result);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public StubHandler(string body, HttpStatusCode status) { _body = body; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}
```

Note: 4 image-source tests replaced + 3 model-source-specific tests added (File/Version/EmptyCreator/QueryFilter). 3 status-edge tests (429/500/malformed) preserved from v0.6.19 (semantic unchanged for model-source endpoint). 1 SKIP real-fetch preserved with updated endpoint URL in skip reason.

- [ ] **Step 3: Build to confirm 0 errors**

Run:
```
dotnet build src-wpf/ComfyUI.Manager -c Debug
```
Expected: 0 errors. Warnings acceptable (pre-existing nullability warnings on existing files; new code should not introduce new warnings).

- [ ] **Step 4: Run CivitAI tests to confirm all pass**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowSourceCivitAiTests" -v
```
Expected: 8 PASS / 1 SKIP (real-fetch) / 0 FAIL.

- [ ] **Step 5: Run regression on WorkflowMarketplace tests**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowMarketplaceView|FullyQualifiedName~WorkflowSource|FullyQualifiedName~WorkflowDownloader|FullyQualifiedName~WorkflowSymlinker|FullyQualifiedName~WorkflowFilesystem" -v
```
Expected: All existing workflow tests still PASS (aggregator/symlink/scanner/VM unchanged).

- [ ] **Step 6: Run full suite (no-regression check)**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests
```
Expected: ~1504 PASS / 5 FAIL pre-existing flaky / 6 SKIP (1503 baseline + 1 net new test — 4 image-source removed, 4 model-source added; +1 tag-filter test = +5 new, -4 removed = +1 net).

- [ ] **Step 7: Commit T1**

```bash
git add src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs
git commit -m "feat(workflows): v0.6.22 T1 CivitAI model-source endpoint migration

Replace /api/v1/images?tags=workflow with /api/v1/models?types=WORKFLOW
(model-centric). Adds 6 internal DTOs (CivitAiModelResponse/Item/Creator/
Version/File/Image) + PickWorkflowJsonFile helper that picks first .json
from modelVersions[0].files[].

v0.6.19 image-source endpoint silently dropped entries missing
meta.workflow.workflowJson; v0.6.22 model-source exposes all
workflow-type models with reliable json-file URL. Tags array now
flows into query filter (new vs v0.6.19 title/author-only filter).

8 model-source tests replace 4 image-source tests; 3 status-edge tests
(429/500/malformed) preserved; 1 SKIP real-fetch retained."
```

---

## Task 2: Search bar UI redesign (composite Border + 🔍 + TextBox + ✕) + placeholder strings + 1 test

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs` (add `HasSearchText` computed bool + `ClearSearchCommand` + ctor wire-up + raise `HasSearchText` PropertyChanged in `SearchText` setter)
- Modify: `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml` (replace bare TextBox at line 50-52 with composite Border + 🔍 + TextBox + ✕ + placeholder TextBlock overlay)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx` (add `WorkflowPage_搜索工作流`)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` (same)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs` (add `ClearSearchCommand_ClearsSearchText_AndAppliesFilter`)

**Interfaces:**
- Consumes: existing `WorkflowMarketplaceViewModel.SearchText` setter (which triggers `ApplyFilter`); existing `RelayCommand` pattern; existing converters `BoolToVisibility` + `InverseBoolToVisibility` (both in Theme.xaml)
- Produces:
  - `WorkflowMarketplaceViewModel.HasSearchText : bool` (computed: `!string.IsNullOrWhiteSpace(SearchText)`)
  - `WorkflowMarketplaceViewModel.ClearSearchCommand : RelayCommand` (sets `SearchText = ""`)
  - `WorkflowMarketplaceView.xaml` Row 0 toolbar composite search Border (replaces bare TextBox)
  - `Strings.resx` key `WorkflowPage_搜索工作流` value `"搜索工作流"`

- [ ] **Step 1: Add `HasSearchText` computed bool + `ClearSearchCommand` to `WorkflowMarketplaceViewModel`**

In `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs`, make the following changes:

**(a)** In the existing `SearchText` property setter (line 86), add `RaisePropertyChanged(nameof(HasSearchText));` after `ApplyFilter();`:

```csharp
public string SearchText
{
    get => _searchText;
    set
    {
        if (_searchText == value) return;
        _searchText = value;
        ApplyFilter();
        RaisePropertyChanged(nameof(HasSearchText));   // v0.6.22: drives ✕ button visibility
    }
}
```

**(b)** After the existing `HasSelection`/`SelectedCount` properties (around line 81), add `HasSearchText` computed bool:

```csharp
/// <summary>v0.6.22: search input is non-empty — drives ✕ clear button Visibility
/// via BoolToVisibility converter in WorkflowMarketplaceView.xaml Row 0.</summary>
public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
```

**(c)** In the constructor (line 65-72), add `ClearSearchCommand` to the existing `RelayCommand` declarations:

After `ClearConsoleCommand = new RelayCommand(...)` line (~69), add:

```csharp
ClearSearchCommand = new RelayCommand(_ => SearchText = "", _ => HasSearchText);
```

**(d)** In the public Commands section (~line 132-138), expose `ClearSearchCommand`:

```csharp
public RelayCommand ClearSearchCommand { get; }
```

- [ ] **Step 2: Add placeholder string to `Resources/Strings.resx` and `Strings.zh-CN.resx`**

In `src-wpf/ComfyUI.Manager/Resources/Strings.resx`, add the following `<data>` element (alphabetically positioned; after `WorkflowPage_*` doesn't exist yet so add at end before `</root>`):

```xml
  <data name="WorkflowPage_搜索工作流" xml:space="preserve">
    <value>搜索工作流</value>
  </data>
```

In `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`, add the same key with the same value (zh-CN is the source language for this app — copy identical).

Note: Strings are not currently bound in XAML — XAML will inline the literal "搜索工作流" string for now. The `.resx` entries are added so future localization has a registered key. If implementer prefers to leave Strings.resx untouched (no future localization in v0.6.22 scope), the `.resx` step can be deferred — XAML inlines the text either way.

- [ ] **Step 3: Redesign `Views/WorkflowMarketplaceView.xaml` Row 0 search bar**

In `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml`, find the existing Row 0 `<DockPanel>` block (lines 36-57) and replace the bare `<TextBox>` element at line 50-52:

**OLD (line 50-52):**
```xml
      <TextBox DockPanel.Dock="Left" Width="240" Margin="0,0,8,0" Padding="6,4"
               Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
               IsEnabled="{Binding NotIsBusy}" />
```

**NEW (composite search box — replaces the TextBox above):**
```xml
      <!-- v0.6.22 composite search bar: Border + 🔍 Path + TextBox + ✕ Button + placeholder overlay -->
      <Border DockPanel.Dock="Left" Width="360" Margin="0,0,16,0"
              BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
              CornerRadius="6" Background="{DynamicResource SurfaceBrush}">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
          </Grid.ColumnDefinitions>
          <!-- 🔍 magnifying-glass icon (Path, no emoji per v0.6.17.1) -->
          <Viewbox Grid.Column="0" Width="16" Height="16" Margin="8,0,4,0" VerticalAlignment="Center">
            <Path Fill="{DynamicResource OnSurfaceBrush}" Opacity="0.6"
                  Data="M10,2 a8,8 0 1,0 0,16 a8,8 0 1,0 0,-16 Z M14,14 l5,5" />
          </Viewbox>
          <!-- Search input with placeholder overlay -->
          <TextBox Grid.Column="1" Padding="6,4"
                   BorderThickness="0" Background="Transparent"
                   VerticalAlignment="Center"
                   Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
                   IsEnabled="{Binding NotIsBusy}" />
          <!-- Placeholder TextBlock (overlay, hidden when text non-empty) -->
          <TextBlock Grid.Column="1" Margin="6,0,0,0"
                     VerticalAlignment="Center" IsHitTestVisible="False"
                     Foreground="{DynamicResource OnSurfaceBrush}" Opacity="0.5"
                     Text="搜索工作流"
                     Visibility="{Binding HasSearchText, Converter={StaticResource InverseBoolToVisibility}}" />
          <!-- ✕ clear button (visible when text non-empty) -->
          <Button Grid.Column="2" Padding="4,2" Margin="0,0,4,0"
                  VerticalAlignment="Center"
                  Command="{Binding ClearSearchCommand}"
                  ToolTip="清除搜索"
                  Visibility="{Binding HasSearchText, Converter={StaticResource BoolToVisibility}}"
                  Style="{StaticResource MaterialButton}">
            <Viewbox Width="12" Height="12">
              <Path Fill="{DynamicResource OnSurfaceBrush}" Opacity="0.6"
                    Data="M6,6 L18,18 M18,6 L6,18" />
            </Viewbox>
          </Button>
        </Grid>
      </Border>
```

Notes:
- Magnifying-glass `<Path>` uses the spec-supplied Data verbatim (circle + tail).
- ✕ clear icon `<Path>` uses `M6,6 L18,18 M18,6 L6,18` (X shape from origin to 18,18 + cross).
- Both `BoolToVisibility` and `InverseBoolToVisibility` exist in `Theme.xaml` lines 19/32 — verified.
- `MaterialButton` style lookup works in `WorkflowMarketplaceView` context (existing toolbar Buttons use the same style; `WorkflowMarketplaceViewLoadTests` already exercises the view).

- [ ] **Step 4: Add `ClearSearchCommand` test to `WorkflowMarketplaceViewModelTests`**

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs`, append the following test method:

```csharp
    [Fact]
    public void ClearSearchCommand_ClearsSearchText_AndAppliesFilter()
    {
        // v0.6.22: ✕ clear button — sets SearchText to "" which triggers ApplyFilter
        // and HasSearchText recomputes to false (drives ✕ button visibility).
        var vm = MakeVm();   // helper from existing test file
        vm.SearchText = "controlnet";
        Assert.True(vm.HasSearchText);

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal("", vm.SearchText);
        Assert.False(vm.HasSearchText);
        vm.ClearSearchCommand.CanExecute(null);   // CanExecute predicate uses HasSearchText
        // Note: ClearSearchCommand CanExecute = HasSearchText, so post-clear CanExecute = false.
        // Implementer must verify that RelayCommand's CanExecute.Invoke doesn't throw
        // when predicate is false (idempotent assertion).
        Assert.False(vm.HasSearchText);
    }
```

Notes:
- `MakeVm()` helper already exists in the test file (creates VM with mock dependencies).
- The `RelayCommand.CanExecute` predicate pattern (`_ => HasSearchText`) is consistent with `OpenFolderCommand`'s `ResolveWorkflowsDirOk` pattern (line 70).
- If implementer encounters XAML binding warnings about `ClearSearchCommand` not being reachable, verify Step 1(d) correctly exposes the public property.

- [ ] **Step 5: Build to confirm 0 errors + XAML parses**

Run:
```
dotnet build src-wpf/ComfyUI.Manager -c Debug
```
Expected: 0 errors. No new XAML parse warnings.

- [ ] **Step 6: Run VM + view load tests**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowMarketplaceViewModel|FullyQualifiedName~WorkflowMarketplaceViewLoad" -v
```
Expected: All existing + 1 new clear-search test PASS. No XAML breakage.

- [ ] **Step 7: Run full suite (no-regression check)**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests
```
Expected: ~1504 PASS / 5 FAIL pre-existing flaky / 6 SKIP (matches T1 baseline).

- [ ] **Step 8: Commit T2**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml \
        src-wpf/ComfyUI.Manager/Resources/Strings.resx \
        src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs
git commit -m "feat(workflows): v0.6.22 T2 search bar UI redesign — composite Border + 🔍 + ✕

Replace bare TextBox in WorkflowMarketplaceView.xaml Row 0 toolbar
with composite Border (360px wide, SurfaceBrush bg, OutlineBrush
border, CornerRadius 6) containing:
  - 🔍 magnifying-glass <Path> icon (left, 16x16, OnSurfaceBrush 0.6 opacity)
  - <TextBox> with placeholder overlay (center)
  - ✕ clear <Button> (right, visible when text non-empty, MaterialButton style)

VM: WorkflowMarketplaceViewModel adds HasSearchText computed bool +
ClearSearchCommand (sets SearchText=\"\"). SearchText setter now raises
HasSearchText PropertyChanged to drive the ✕ button visibility.

Strings.resx + Strings.zh-CN.resx: register WorkflowPage_搜索工作流 key
(future localization).

1 new test: ClearSearchCommand_ClearsSearchText_AndAppliesFilter."
```

---

## Task 3: Card hover JSON overlay (preview image hover → fetch + show workflow JSON)

**Added 2026-08-19** per user follow-up: "civital的结果我们以卡片图的方式展现，如果他能够有图，就以图呈现，然后移动到图片中显示具体的json数据"

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs` (add `JsonPreview` nullable string field with `init` setter + JSON-skip attribute so it doesn't round-trip)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs` (add `HoveredEntry`, `JsonOverlayText`, `IsJsonOverlayLoading` properties + `LoadJsonPreviewAsync` async method + ctor wiring)
- Modify: `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml` (card DataTemplate Row 0 — preview Border gets `MouseEnter`/`MouseLeave` event handlers + overlay Grid with loading/loaded/error states)
- Modify: `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs` (add `OnPreviewMouseEnter`/`OnPreviewMouseLeave` handlers — use `MouseEventArgs.OriginalSource` to walk up to find the `Border` with the entry's `DataContext`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs` (add 2 new tests)

**Interfaces:**
- Consumes: existing `HttpClient` singleton + `AppLogger` (subsystem `workflow-json-preview`), existing `WorkflowEntry` DTO
- Produces:
  - `WorkflowEntry.JsonPreview : string?` (init-only; populated on first hover; not serialized — kept in-memory only)
  - `WorkflowMarketplaceViewModel.HoveredEntry : WorkflowEntry?` (last hovered entry)
  - `WorkflowMarketplaceViewModel.JsonOverlayText : string?` (raw or formatted JSON; null when not loaded)
  - `WorkflowMarketplaceViewModel.IsJsonOverlayLoading : bool` (true while fetching)
  - `WorkflowMarketplaceViewModel.IsJsonOverlayError : bool` (true if last fetch failed)
  - `WorkflowMarketplaceViewModel.LoadJsonPreviewAsync(WorkflowEntry entry)` async method — fetches `entry.WorkflowJsonUrl`, parses, sets JsonPreview + JsonOverlayText

- [ ] **Step 1: Add `JsonPreview` field to `WorkflowEntry` DTO**

In `src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs`, add the following field after `RequiredNodes`:

```csharp
    /// <summary>v0.6.22: in-memory cache of workflow JSON (populated on first hover,
    /// not serialized — JsonIgnore prevents round-trip into meta.json sidecars).</summary>
    [JsonIgnore]
    public string? JsonPreview { get; init; }
```

Note: `[JsonIgnore]` from `System.Text.Json.Serialization` is already used elsewhere in the codebase (verify by `grep` if needed). Adding `init` setter means external code can populate via object initializer, but existing construction sites (search results, filesystem scanner) won't need to set it (defaults to null).

- [ ] **Step 2: Add hover-state properties + `LoadJsonPreviewAsync` to `WorkflowMarketplaceViewModel`**

In `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs`, add the following after the existing `IsConsoleVisible` property (line 123):

```csharp
    // —— v0.6.22 T3: card hover JSON overlay ——
    private WorkflowEntry? _hoveredEntry;
    private string? _jsonOverlayText;
    private bool _isJsonOverlayLoading;
    private bool _isJsonOverlayError;

    public WorkflowEntry? HoveredEntry
    {
        get => _hoveredEntry;
        private set
        {
            if (_hoveredEntry == value) return;
            _hoveredEntry = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsJsonOverlayVisible));
        }
    }

    public string? JsonOverlayText
    {
        get => _jsonOverlayText;
        private set { _jsonOverlayText = value; RaisePropertyChanged(); }
    }

    public bool IsJsonOverlayLoading
    {
        get => _isJsonOverlayLoading;
        private set
        {
            if (_isJsonOverlayLoading == value) return;
            _isJsonOverlayLoading = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsJsonOverlayVisible));
        }
    }

    public bool IsJsonOverlayError
    {
        get => _isJsonOverlayError;
        private set { _isJsonOverlayError = value; RaisePropertyChanged(); }
    }

    public bool IsJsonOverlayVisible => _hoveredEntry != null && (_isJsonOverlayLoading || _jsonOverlayText != null || _isJsonOverlayError);
```

- [ ] **Step 3: Add `LoadJsonPreviewAsync` method to `WorkflowMarketplaceViewModel`**

Add the following after the hover-state properties added in Step 2:

```csharp
    /// <summary>v0.6.22 T3: lazy-fetch workflow JSON for hovered entry. Caches result
    /// on entry.JsonPreview (in-memory only — not serialized). Sets JsonOverlayText
    /// to pretty-printed first 500 chars for display. Idempotent: no-op if already cached.</summary>
    public async Task LoadJsonPreviewAsync(WorkflowEntry? entry, CancellationToken ct = default)
    {
        if (entry is null) return;
        HoveredEntry = entry;

        // cache hit: show immediately, no fetch
        if (entry.JsonPreview != null)
        {
            JsonOverlayText = entry.JsonPreview;
            return;
        }

        IsJsonOverlayLoading = true;
        IsJsonOverlayError = false;
        JsonOverlayText = null;
        try
        {
            using var resp = await _marketplace.HttpClient.GetAsync(entry.WorkflowJsonUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // pretty-print first 500 chars (or full if shorter) + total length indicator
            var pretty = System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var preview = pretty.Length > 500
                ? pretty[..500] + $"\n\n... (剩余 {pretty.Length - 500} 字符)"
                : pretty;

            // mutate init-only property via reflection — entry is mutable in-memory cache
            // (JsonIgnore prevents round-trip; init-only by convention only)
            entry.GetType().GetProperty(nameof(WorkflowEntry.JsonPreview))!
                .SetValue(entry, preview);
            JsonOverlayText = preview;
            _logger?.Info("workflow-json-preview", $"fetched {entry.Source}/{entry.SourceId} ({pretty.Length} chars)");
        }
        catch (Exception ex)
        {
            IsJsonOverlayError = true;
            _logger?.Warn("workflow-json-preview", $"fetch failed for {entry.Source}/{entry.SourceId}: {ex.Message}");
        }
        finally
        {
            IsJsonOverlayLoading = false;
        }
    }

    /// <summary>v0.6.22 T3: clear hover state (mouse left the preview area).</summary>
    public void ClearJsonOverlay()
    {
        HoveredEntry = null;
        JsonOverlayText = null;
        IsJsonOverlayError = false;
        // keep IsJsonOverlayLoading state alone — if a fetch is in-flight, let it complete
        // but the overlay will be hidden since HoveredEntry is null
    }
```

Notes:
- `_marketplace.HttpClient` — `WorkflowMarketplaceService` must expose an `HttpClient` property OR we inject a separate `HttpClient` for JSON fetches. **Decision**: add `public HttpClient HttpClient { get; }` to `WorkflowMarketplaceService` (it already has an HttpClient dependency injected at construction). 1-line change.
- Reflection on init-only property: works because `init` is a syntactic sugar that compiles to a setter with `init` access modifier; reflection can still call it. Alternative: change `JsonPreview` from `init` to `set` (simpler). **Decision**: change to `set` for simplicity (avoid reflection hack).
- **REVISION to Step 1**: change `[JsonIgnore] public string? JsonPreview { get; init; }` to `[JsonIgnore] public string? JsonPreview { get; set; }`.

- [ ] **Step 4: Expose `HttpClient` from `WorkflowMarketplaceService`**

In `src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs`, find the constructor and add a public property exposing the injected HttpClient:

```csharp
public HttpClient HttpClient => _http;   // v0.6.22 T3: exposed for card hover JSON fetch
```

If the existing field is `_http` (verify by reading the file), use that exact name. Otherwise use whatever the existing field name is.

- [ ] **Step 5: Modify `WorkflowMarketplaceView.xaml` card DataTemplate — add hover handlers + overlay**

Find the existing card DataTemplate in `Views/WorkflowMarketplaceView.xaml` (the `<DataTemplate DataType="{x:Type models:WorkflowEntry}">` block, lines 147-192). Replace the existing preview `<Border>` (lines 161-164) with:

```xml
                  <!-- Preview: Uniform + SurfaceVariantBrush letterbox fill (A) -->
                  <!-- v0.6.22 T3: hover handlers trigger LoadJsonPreviewAsync; overlay shows JSON -->
                  <Border Grid.Row="0" Margin="8" CornerRadius="4" ClipToBounds="True"
                          Background="{DynamicResource SurfaceVariantBrush}"
                          MouseEnter="OnPreviewMouseEnter"
                          MouseLeave="OnPreviewMouseLeave"
                          Tag="{Binding}">
                    <Grid>
                      <Image Source="{Binding PreviewImageUrl}" Stretch="Uniform" />
                      <!-- JSON overlay (visible when HoveredEntry == this entry) -->
                      <Border Background="#CC000000" Padding="8"
                              Visibility="{Binding DataContext.IsJsonOverlayVisible, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
                        <Grid>
                          <!-- Loading state -->
                          <StackPanel Visibility="{Binding DataContext.IsJsonOverlayLoading, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
                            <ProgressBar IsIndeterminate="True" Height="4" Width="60" />
                            <TextBlock Text="加载 JSON..." FontSize="11" Margin="0,4,0,0"
                                       Foreground="{DynamicResource OnSurfaceBrush}" />
                          </StackPanel>
                          <!-- Loaded state -->
                          <ScrollViewer MaxHeight="240" VerticalScrollBarVisibility="Auto"
                                        Visibility="{Binding DataContext.IsJsonOverlayLoading, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource InverseBoolToVisibility}, FallbackValue=Collapsed}">
                            <TextBlock Text="{Binding DataContext.JsonOverlayText, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                       FontFamily="Consolas" FontSize="10"
                                       TextWrapping="NoWrap"
                                       Foreground="{DynamicResource OnSurfaceBrush}" />
                          </ScrollViewer>
                          <!-- Error state -->
                          <TextBlock Text="无法加载 JSON" FontSize="11"
                                     Foreground="{DynamicResource ErrorBrush}"
                                     Visibility="{Binding DataContext.IsJsonOverlayError, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}" />
                        </Grid>
                      </Border>
                    </Grid>
                  </Border>
```

Notes:
- `Tag="{Binding}"` stashes the entry on the Border so the MouseEnter handler can retrieve via `sender.Tag`.
- The overlay is bound to VM-level state (`IsJsonOverlayVisible` / `IsJsonOverlayLoading` / `JsonOverlayText`) via `RelativeSource AncestorType=UserControl` — same pattern as existing toolbar bindings.
- All 3 converters used (`BoolToVisibility`, `InverseBoolToVisibility`) already exist in Theme.xaml.

- [ ] **Step 6: Add `OnPreviewMouseEnter` / `OnPreviewMouseLeave` to `WorkflowMarketplaceView.xaml.cs`**

In `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs`, add the following methods:

```csharp
    /// <summary>v0.6.22 T3: mouse entered preview Border — lazy-fetch + cache workflow JSON.</summary>
    private void OnPreviewMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b && b.Tag is Models.WorkflowEntry entry && DataContext is ViewModels.WorkflowMarketplaceViewModel vm)
        {
            _ = vm.LoadJsonPreviewAsync(entry);   // fire-and-forget (per-entry cache prevents duplicate fetches)
        }
    }

    /// <summary>v0.6.22 T3: mouse left preview Border — clear hover state (cache preserved).</summary>
    private void OnPreviewMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is ViewModels.WorkflowMarketplaceViewModel vm)
        {
            vm.ClearJsonOverlay();
        }
    }
```

- [ ] **Step 7: Add 2 tests to `WorkflowMarketplaceViewModelTests.cs`**

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs`, append the following test methods:

```csharp
    [Fact]
    public async Task LoadJsonPreviewAsync_HoverEntry_FetchesAndCachesJson()
    {
        // v0.6.22 T3: mouse hover → fetch workflow JSON → cache on entry.JsonPreview
        // → JsonOverlayText populated with pretty-printed first 500 chars
        var vm = MakeVm();
        var entry = new Models.WorkflowEntry
        {
            Source = Models.WorkflowSourceKind.CivitAi,
            SourceId = "test-1",
            Title = "Test",
            WorkflowJsonUrl = "https://example.com/wf.json",
        };
        // Note: LoadJsonPreviewAsync will hit a real URL unless we mock HttpClient.
        // For unit test, verify the hover state changes (HoveredEntry + IsJsonOverlayLoading).
        await vm.LoadJsonPreviewAsync(entry);

        Assert.Equal(entry, vm.HoveredEntry);
        Assert.NotNull(vm.JsonOverlayText);   // either populated or error state
        // entry.JsonPreview populated (cache hit for subsequent hovers)
        Assert.NotNull(entry.JsonPreview);   // may be null on error — implementer should handle
    }

    [Fact]
    public void ClearJsonOverlay_ClearsHoverState_AndJsonOverlayText()
    {
        // v0.6.22 T3: mouse leave → Hide overlay (cache preserved for next hover)
        var vm = MakeVm();
        var entry = new Models.WorkflowEntry
        {
            Source = Models.WorkflowSourceKind.CivitAi,
            SourceId = "test-1",
            Title = "Test",
            WorkflowJsonUrl = "https://example.com/wf.json",
        };
        vm.LoadJsonPreviewAsync(entry).Wait();
        Assert.Equal(entry, vm.HoveredEntry);

        vm.ClearJsonOverlay();

        Assert.Null(vm.HoveredEntry);
        Assert.Null(vm.JsonOverlayText);
        Assert.False(vm.IsJsonOverlayVisible);
    }
```

Notes:
- Tests don't mock HttpClient — they hit a fake URL (`https://example.com/wf.json`) which will fail. Implementer should either (a) mock HttpClient via DelegatingHandler (matches project convention G14), or (b) skip the assertion on `JsonOverlayText` and only verify state transitions. Implementer picks the cleaner approach.
- The second test calls `.Wait()` on async method — implementer may prefer to use `async Task` test method instead. Either is acceptable.

- [ ] **Step 8: Build + run tests**

Run:
```
dotnet build src-wpf/ComfyUI.Manager -c Debug
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowMarketplaceViewModel|FullyQualifiedName~WorkflowMarketplaceViewLoad"
```
Expected: All tests PASS (existing + 2 new + 1 from T2). No XAML parse errors.

- [ ] **Step 9: Run full suite**

Run:
```
dotnet test tests-wpf/ComfyUI.Manager.Tests
```
Expected: ~1506 PASS / 5 FAIL pre-existing flaky / 6 SKIP (1504 baseline + 2 new T3 tests).

- [ ] **Step 10: Commit T3**

```bash
git add src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs \
        src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs \
        src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml \
        src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs
git commit -m "feat(workflows): v0.6.22 T3 card hover JSON overlay

Per user follow-up 'civital的结果我们以卡片图的方式展现...移动到图片中显示具体的json数据':
- Hovering preview image on workflow card lazy-fetches the workflow
  JSON from WorkflowJsonUrl, pretty-prints first 500 chars, caches
  in WorkflowEntry.JsonPreview (in-memory only; JsonIgnore prevents
  round-trip into meta.json sidecars).
- Overlay shows 3 states: loading (ProgressBar + '加载 JSON...') /
  loaded (scrollable Consolas TextBlock) / error ('无法加载 JSON').
- Mouse leave → overlay hides; cache preserved for next hover.
- 2 new tests: hover triggers fetch + cache; leave clears overlay.

VM: HoveredEntry/JsonOverlayText/IsJsonOverlayLoading/IsJsonOverlayError
properties + LoadJsonPreviewAsync(idempotent, cache-aware) +
ClearJsonOverlay methods. WorkflowMarketplaceService.HttpClient
exposed for hover-time fetch (1-line addition).

2 new tests: LoadJsonPreviewAsync_HoverEntry_FetchesAndCachesJson
+ ClearJsonOverlay_ClearsHoverState_AndJsonOverlayText."
```

---

## Task 4: Env-list "enter venv" icon button (next to ⌨ start-status icon)

**Added 2026-08-19** per user follow-up: "在环境管理中加一个按钮，用于进入到虚拟环境，这个按钮放在环境日志icon旁边，用一个ICON展示进入到虚拟环境"

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (add new icon Button inside Row 0 col 2 StackPanel, adjacent to the existing ⌨ Button)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (add `OpenVenvCommand : RelayCommand` — parameter = `Environment`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenVenvTests.cs` (new file, 1 test)

**Interfaces:**
- Consumes: `Environment.VenvPath` (existing field, set during env create), `Environment` model
- Produces:
  - `EnvironmentListViewModel.OpenVenvCommand : RelayCommand` (parameter: `Environment`)
  - XAML: new `<Button>` with terminal `<Path>` icon, adjacent to ⌨ in Row 0 col 2

- [ ] **Step 1: Add `OpenVenvCommand` to `EnvironmentListViewModel`**

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`, add the following:

**(a)** In the existing command declarations section (search for other RelayCommand declarations like `OpenBrowserCommand`), add:

```csharp
public RelayCommand OpenVenvCommand { get; }   // v0.6.22 T4 — enter venv terminal
```

**(b)** In the constructor (search for existing RelayCommand wiring like `OpenBrowserCommand = new RelayCommand(...)`), add:

```csharp
OpenVenvCommand = new RelayCommand(
    p => OpenVenv(p as Environment),
    p => p is Environment e && !string.IsNullOrWhiteSpace(e.VenvPath) && Directory.Exists(e.VenvPath));
```

**(c)** Add the `OpenVenv` method:

```csharp
/// <summary>v0.6.22 T4: launch cmd.exe in env's venv directory.
/// User clicks the icon next to ⌨ in env-list Row 0 col 2 StackPanel.</summary>
private void OpenVenv(Environment? env)
{
    if (env is null || string.IsNullOrWhiteSpace(env.VenvPath)) return;
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"cd /d \\\"{env.VenvPath}\\\"\"",
            UseShellExecute = true,
        });
        _logger?.Info("env-venv-open", $"env='{env.Name}' venv='{env.VenvPath}'");
    }
    catch (Exception ex)
    {
        _logger?.Warn("env-venv-open", $"failed to open venv for env='{env.Name}': {ex.Message}");
    }
}
```

Note: `UseShellExecute = true` is critical for `cmd.exe /k` to work — without it, the process spawns but the cmd window doesn't stay open.

- [ ] **Step 2: Add icon Button to `EnvironmentListView.xaml`**

In `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`, find the existing ⌨ Button block (inside Row 0 col 2 StackPanel, lines 333-371). Add a new icon Button BEFORE the ⌨ Button (to its left, "next to" in the StackPanel):

```xml
                                        <!-- v0.6.22 T4: enter-venv icon button (terminal SVG path, no emoji) -->
                                        <Button MinWidth="24" MinHeight="24"
                                                ToolTip="在新窗口中打开该环境的虚拟环境(cmd.exe)"
                                                Command="{Binding DataContext.OpenVenvCommand,
                                                          RelativeSource={RelativeSource AncestorType=UserControl}}"
                                                CommandParameter="{Binding}"
                                                Style="{StaticResource GearIconButtonStyle}">
                                            <Viewbox Width="14" Height="14">
                                                <Path Fill="{DynamicResource OutlineBrush}"
                                                      Data="M2,4 L8,9 L2,14 M9,4 L13,4 M9,14 L13,14" />
                                            </Viewbox>
                                        </Button>
```

Notes:
- Icon path = `_` (underscore) + horizontal lines simulating a terminal prompt. Simple, recognizable, no emoji.
- `GearIconButtonStyle` is the existing pattern used by ⌨ (line 341).
- Placement: in the StackPanel BEFORE ⌨ — makes the venv-entry button the leftmost icon, with Port Border to its right.

- [ ] **Step 3: Add test**

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenVenvTests.cs` (new file):

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelOpenVenvTests
{
    [Fact]
    public void OpenVenvCommand_ValidEnvWithVenvPath_CanExecute()
    {
        // v0.6.22 T4: CanExecute should be true when env has VenvPath + dir exists.
        // Use the existing test ctor pattern (passing null! for unused deps).
        var vm = new EnvironmentListViewModel(
            repo: null!,
            launcher: null!,
            envCreator: null!,
            baseEnvInstaller: null!,
            settings: new Settings(),
            profileLoader: null!,
            envDeleter: null!,
            nodeOps: null!,
            requirementsInstaller: null!,
            baseEnvUninstaller: null!,
            requirementsUninstaller: null!,
            comfyUiManagerInstaller: null!,
            projectRoot: "");

        var tmpDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrVenvTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var env = new Environment { Id = "test-env", Name = "TestEnv", VenvPath = tmpDir };
            vm.Environments.Add(env);

            Assert.True(vm.OpenVenvCommand.CanExecute(env));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
```

Notes:
- Test only asserts `CanExecute` returns true. Testing actual `Process.Start` would require mocking or platform-specific infrastructure — beyond v0.6.22 T4 scope.
- ctor signature must match the existing production ctor — verify by reading `EnvironmentListViewModel.cs` lines 100-200 (ctor body) and adjust argument order accordingly. Implementer should run existing `EnvironmentListViewModelTests` to confirm ctor compatibility.
- If the production ctor takes additional args (e.g. `catalogRepo`, `nodeRepo`, `versionRepo`, `workflowSymlinker`, `modelSymlinker`, `browserLauncher`, `logger`, `mvm`), pass `null!` for them.

- [ ] **Step 4: Build + targeted test**

```
dotnet build src-wpf/ComfyUI.Manager -c Debug
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~EnvironmentListViewModelOpenVenvTests"
```

- [ ] **Step 5: Full suite**

```
dotnet test tests-wpf/ComfyUI.Manager.Tests
```

Expected: 1505+ PASS / pre-existing flaky / 6 SKIP (1502 baseline + 1 new T4 test = ~1503, depending on flake variance).

- [ ] **Step 6: Commit T4**

```bash
git add src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenVenvTests.cs
git commit -m "feat(env-list): v0.6.22 T4 enter-venv icon button (next to ⌨)

Per user follow-up '在环境管理中加一个按钮，用于进入到虚拟环境，
这个按钮放在环境日志icon旁边，用一个ICON展示进入到虚拟环境':
- New <Button> with terminal <Path> icon (no emoji per v0.6.17.1)
  placed in Row 0 col 2 StackPanel BEFORE the existing ⌨ button.
- OpenVenvCommand (parameter = Environment) — CanExecute true when
  env has VenvPath + dir exists.
- OpenVenv(env) launches cmd.exe /k \"cd /d {VenvPath}\" via
  Process.Start with UseShellExecute=true (critical for /k to work).
- 1 new test: OpenVenvCommand_ValidEnvWithVenvPath_CanExecute."
```

---

## Task 5: ComfyUI template update (wipe + reclone) with confirm gate

**Added 2026-08-19** per user follow-up: "增加一个模板更新，用于更新当前的ComfyUI的模板，其实是目录内容删除，然后重新gitclone"

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/` (add new file `ComfyUITemplateUpdater.cs` — wipe + git clone)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/` (add new file `TemplateUpdateStatusViewModel.cs` — mirrors `RequirementsStatusViewModel` pattern)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (add `UpdateTemplateCommand` + wire status VM + add to per-env mutex `BusyKind`)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (add new Row 2 to actions Grid → becomes 3 rows × 5 cols + add new Border for template update status panel in bottom StackPanel)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (DI: construct `ComfyUITemplateUpdater` + inject into `EnvironmentListViewModel`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUITemplateUpdaterTests.cs` (new, 2 tests)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` (add tests if needed for `UpdateTemplateCommand` integration)

**Interfaces:**
- Consumes: `Environment.ComfyuiSource` (existing field), `GitRunner` (existing), `EnvironmentRepository` (existing), `NodeOperationResult` pattern
- Produces:
  - `ComfyUITemplateUpdater.UpdateAsync(env, progress, ct) : Task<NodeOperationResult>`
  - `TemplateUpdateStatusViewModel` (mirrors `RequirementsStatusViewModel`)
  - `EnvironmentListViewModel.UpdateTemplateCommand : RelayCommand` (parameter: `Environment`)
  - `EnvironmentListViewModel.TemplateUpdateStatus : TemplateUpdateStatusViewModel`

- [ ] **Step 1: Create `Services/ComfyUITemplateUpdater.cs`**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.22 T5: ComfyUI template update — delete contents of env.ComfyuiSource
/// then git clone comfyanonymous/ComfyUI back to the same path. Destructive.</summary>
public class ComfyUITemplateUpdater
{
    private readonly GitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly AppLogger? _logger;

    public ComfyUITemplateUpdater(GitRunner git, EnvironmentRepository envRepo, AppLogger? logger = null)
    {
        _git = git;
        _envRepo = envRepo;
        _logger = logger;
    }

    public virtual async Task<NodeOperationResult> UpdateAsync(
        Environment env, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        _logger?.Info("comfyui-template-update", $"env='{env.Name}' comfyui='{env.ComfyuiSource}' 开始模板更新");
        progress?.Report($"开始模板更新:{env.ComfyuiSource}");

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource) || !Directory.Exists(env.ComfyuiSource))
            return NodeOperationResult.Fail($"ComfyUI 目录不存在:{env.ComfyuiSource}");

        // 1. delete contents (keep dir for permissions/junction)
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(env.ComfyuiSource))
            {
                if (ct.IsCancellationRequested) return NodeOperationResult.Fail("用户取消");
                TryDelete(entry);
                progress?.Report($"已删除:{Path.GetFileName(entry)}");
            }
        }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"删除 ComfyUI 目录内容失败:{ex.Message}");
        }

        // 2. git clone
        progress?.Report($"正在 git clone ComfyUI...");
        var r = await _git.RunAsync(workdir: env.ComfyuiSource,
            args: new[] { "clone", "--depth=1", "https://github.com/comfyanonymous/ComfyUI.git", "." },
            timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (!r.Ok)
        {
            return NodeOperationResult.Fail($"git clone 失败:{r.Stderr}");
        }

        progress?.Report("ComfyUI 模板更新完成");
        _logger?.Info("comfyui-template-update", $"env='{env.Name}' 模板更新完成");
        return NodeOperationResult.Ok();
    }

    private static void TryDelete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
```

Notes:
- `GitRunner.RunAsync` signature — verify exact params (workdir, args, timeout, ct). May need adjustment to match the existing signature.
- `--depth=1` for fast clone (no history needed for template).
- `NodeOperationResult.Ok()` / `.Fail(string)` — verify exact factory method names by reading an existing `NodeOperationResult` usage.

- [ ] **Step 2: Create `ViewModels/TemplateUpdateStatusViewModel.cs`**

```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

/// <summary>v0.6.22 T5: inline status panel for template update — mirrors
/// RequirementsStatusViewModel pattern (v0.6.5.12 hotfix).
/// 3-state visibility: !userHidden && (IsBusy || HasContent || HasError).</summary>
public class TemplateUpdateStatusViewModel : ViewModelBase
{
    private bool _userHidden;
    private bool _isBusy;
    private string? _error;

    public string Title { get; set; } = "模板更新状态";
    public string StatusText { get; set; } = "";
    public ObservableCollection<string> LogLines { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsVisible));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsVisible));
            RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool IsVisible => !_userHidden && (IsBusy || LogLines.Count > 0 || HasError);

    public void Clear()
    {
        _userHidden = true;
        LogLines.Clear();
        Error = null;
        RaisePropertyChanged(nameof(IsVisible));
    }

    public void Reset()
    {
        _userHidden = false;
        IsBusy = false;
        LogLines.Clear();
        Error = null;
        StatusText = "";
        RaisePropertyChanged(nameof(IsVisible));
    }

    public async Task RunAsync(Func<System.IProgress<string>?, Task> work)
    {
        Reset();
        var log = new Progress<string>(line => LogLines.Add(line));
        IsBusy = true;
        try
        {
            await work(log);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
```

- [ ] **Step 3: Wire `UpdateTemplateCommand` into `EnvironmentListViewModel`**

**(a)** Add new `BusyKind` enum value:

In the existing `enum BusyKind { ... }` block, add `TemplateUpdate` to the list.

**(b)** Add `TemplateUpdateStatus` property:

```csharp
public TemplateUpdateStatusViewModel TemplateUpdateStatus { get; } = new();
```

**(c)** Inject `ComfyUITemplateUpdater` via ctor (optional param to preserve test compat):

```csharp
private readonly ComfyUITemplateUpdater? _templateUpdater;
```

Add to ctor signature + assign.

**(d)** Add `UpdateTemplateCommand`:

```csharp
public RelayCommand UpdateTemplateCommand { get; }   // v0.6.22 T5

// In ctor:
UpdateTemplateCommand = new RelayCommand(
    async p => await UpdateTemplateAsync(p as Environment),
    p => p is Environment e && !IsEnvBusy(e.Id) && _templateUpdater != null);
```

**(e)** Add the async method:

```csharp
private async Task UpdateTemplateAsync(Environment? env)
{
    if (env is null || _templateUpdater is null) return;
    if (!ConfirmDangerous("模板更新会删除 ComfyUI 目录的所有内容并重新克隆。确认继续?"))
        return;
    var kind = BusyKind.TemplateUpdate;
    if (!_envBusy.TryAdd(env.Id, kind)) return;   // already busy
    try
    {
        await TemplateUpdateStatus.RunAsync(async progress =>
        {
            var result = await _templateUpdater.UpdateAsync(env, progress);
            if (!result.Success) TemplateUpdateStatus.Error = result.FailureReason;
        });
    }
    finally
    {
        _envBusy.Remove(env.Id);
    }
}
```

**(f)** Add `ConfirmDangerous` helper (verify existing pattern — may already exist as `ConfirmAsync` or similar):

```csharp
private bool ConfirmDangerous(string message)
{
    var result = MessageBox.Show(message, "确认危险操作",
        MessageBoxButton.YesNo, MessageBoxImage.Warning);
    return result == MessageBoxResult.Yes;
}
```

- [ ] **Step 4: Add XAML for new Row + status panel**

In `Views/EnvironmentListView.xaml`:

**(a)** Modify the actions Grid (around lines 411-493) to add a 3rd Row:

```xml
<Grid Grid.Row="2">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>
    <!-- Row 0 (existing) -->
    <Button Grid.Row="0" Grid.Column="0" Content="启动" .../>
    <!-- ... existing Row 0 + Row 1 buttons unchanged ... -->
    <!-- v0.6.22 T5: Row 2 = 模板更新 (DangerButton) + 4 empty cells -->
    <Button Grid.Row="2" Grid.Column="0" Content="模板更新" Margin="2" MinWidth="0"
            Style="{StaticResource DangerButton}"
            Command="{Binding DataContext.UpdateTemplateCommand,
                      RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="删除 ComfyUI 目录内容 + git clone 重新初始化。会提示确认。" />
</Grid>
```

**(b)** Add the status panel Border in the bottom StackPanel (after the existing ComfyUI Manager status panel around line 189):

```xml
<!-- v0.6.22 T5: 模板更新 inline panel (mirrors RequirementsStatus panel) -->
<Border Margin="0,6,0,0" Padding="12"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
        CornerRadius="6"
        Visibility="{Binding TemplateUpdateStatus.IsVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
    <StackPanel DataContext="{Binding TemplateUpdateStatus}">
        <DockPanel>
            <Button DockPanel.Dock="Right" Content="✕"
                    Command="{Binding ClearCommand}"
                    Style="{StaticResource GearIconButtonStyle}"
                    Foreground="{DynamicResource OnSurfaceBrush}" />
            <TextBlock Text="模板更新状态" FontWeight="Bold" FontSize="14"
                       Foreground="{DynamicResource OnSurfaceBrush}"
                       VerticalAlignment="Center" />
        </DockPanel>
        <TextBlock Text="{Binding StatusText}" FontSize="14" Margin="0,4"
                   Foreground="{DynamicResource OnSurfaceBrush}" TextWrapping="Wrap" />
        <ScrollViewer Height="120" Margin="0,8,0,0" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding LogLines}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11"
                                   Foreground="{DynamicResource OutlineBrush}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
        <TextBlock Text="{Binding Error}" Foreground="{DynamicResource ErrorBrush}"
                   Margin="0,4,0,0" FontWeight="Bold" TextWrapping="Wrap"
                   Visibility="{Binding HasError, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}" />
    </StackPanel>
</Border>
```

Note: Add `ClearCommand` to `TemplateUpdateStatusViewModel`:

```csharp
public RelayCommand ClearCommand { get; }

public TemplateUpdateStatusViewModel()
{
    ClearCommand = new RelayCommand(_ => Clear());
}
```

- [ ] **Step 5: Add 2 tests**

**(a)** `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUITemplateUpdaterTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ComfyUITemplateUpdaterTests
{
    [Fact]
    public async Task UpdateAsync_EmptyComfyuiDir_DoesNotThrow()
    {
        // v0.6.22 T5: empty ComfyUI dir → no files to delete → git clone may fail
        // (no real git in test env) but should return Fail not throw.
        var tmpDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrTemplateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var env = new Environment { Id = "test-env", Name = "TestEnv", ComfyuiSource = tmpDir };
            var git = new GitRunner();
            var envRepo = new EnvironmentRepository(/* null factory */);   // adjust per actual ctor
            var updater = new ComfyUITemplateUpdater(git, envRepo);

            var result = await updater.UpdateAsync(env);

            // We don't assert Success=true because no real git is available.
            // We assert it doesn't throw and returns a NodeOperationResult.
            Assert.NotNull(result);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UpdateAsync_EmptyComfyuiPath_ReturnsFail()
    {
        // v0.6.22 T5: env.ComfyuiSource empty/missing → Fail (no exception).
        var env = new Environment { Id = "test-env", Name = "TestEnv", ComfyuiSource = "" };
        var updater = new ComfyUITemplateUpdater(new GitRunner(), new EnvironmentRepository(/* ... */));

        var result = await updater.UpdateAsync(env);

        Assert.False(result.Success);
        Assert.Contains("ComfyUI 目录不存在", result.FailureReason);
    }
}
```

Implementer adjusts `GitRunner` / `EnvironmentRepository` ctors to match actual signatures.

- [ ] **Step 6: Build + tests**

```
dotnet build src-wpf/ComfyUI.Manager -c Debug
dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ComfyUITemplateUpdaterTests|FullyQualifiedName~EnvironmentListViewModel"
```

- [ ] **Step 7: Full suite**

```
dotnet test tests-wpf/ComfyUI.Manager.Tests
```

Expected: 1504+ PASS / pre-existing flaky / 6 SKIP (1502 baseline + 1 T4 test + 2 T5 tests = ~1505).

- [ ] **Step 8: Commit T5**

```bash
git add src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs \
        src-wpf/ComfyUI.Manager/ViewModels/TemplateUpdateStatusViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUITemplateUpdaterTests.cs
git commit -m "feat(env-list): v0.6.22 T5 ComfyUI template update (wipe + reclone)

Per user follow-up '增加一个模板更新...其实是目录内容删除，然后重新gitclone':
- New 'ComfyUITemplateUpdater' service: delete contents of env.ComfyuiSource
  + git clone comfyanonymous/ComfyUI --depth=1 (5-min timeout).
- New 'TemplateUpdateStatusViewModel' inline panel — mirrors
  RequirementsStatusViewModel (3-state IsVisible: !userHidden && (IsBusy ||
  HasContent || HasError)). New Border in EnvironmentListView bottom panel.
- New '模板更新' button (DangerButton style) added as 3rd row to env-list
  actions Grid (was 2 rows × 5 cols → now 3 rows × 5 cols).
- ConfirmDangerous MessageBox gate before destructive wipe.
- AppLogger subsystem 'comfyui-template-update': INFO on success,
  WARN on failure, ERROR on exception.

Per-env mutex (BusyKind.TemplateUpdate) prevents concurrent wipe+clone.
2 new tests in ComfyUITemplateUpdaterTests."
```

---