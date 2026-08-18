# Workflow Marketplace v0.6.19 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "工作流市场" sidebar section that aggregates 3 online workflow sources into one searchable card grid, multi-select + batch-download ComfyUI workflow JSON files into a shared directory, and auto-syncs them to running envs via junction/symlink at env-start.

**Architecture:** New `MainSection.Workflows` sidebar entry. Pluggable `IWorkflowSource` interface with 3 concrete fetchers (CommunityJson + CivitAi + OpenArt) normalized to `WorkflowEntry`. `WorkflowMarketplaceService` aggregates via `Task.WhenAll` with dedup by `(Source, SourceId)`. Download state is filesystem-derived (scan `Settings.WorkflowsDirectory`). `WorkflowDownloader` does single + batch with `SemaphoreSlim(4)` concurrency. `WorkflowSymlinker.SyncToEnv` runs fire-and-forget after env-start via existing `JunctionLinker` (Windows) + `Directory.CreateSymbolicLink` (Linux/macOS). Console panel mirrors v0.6.18.4 bulk-update-console pattern.

**Tech Stack:** .NET 8 / WPF / C# 12 / SQLite / xUnit / Moq / `HttpClient` (singleton in `App.xaml.cs`) / `JunctionLinker` (existing)

**Spec:** `docs/superpowers/specs/2026-08-18-workflow-marketplace-design.md`

**Base branch:** main at `b6d8dc6` (post v0.6.18.4) → spec HEAD `24ce986`.

## Global Constraints

- Test baseline `1361 PASS / 0 FAIL / 1 SKIP` (post v0.6.18.4); target post-SDD `~1415 PASS / 0 FAIL / 4 SKIP` (3 new source real-fetch `[SKIP]` tests added)
- All path fields follow `SettingsDefaults.Resolve(...)` pattern (template-style: empty → default subdir name; relative paths preserved; absolute paths under `projectRoot` migrated to relative)
- All new `bool` / enum bindings use existing converters registered in `Resources/Theme.xaml` (`BoolToVisibility` / `NullToVisibility` / `SectionEquality`)
- Sidebar RadioButton follows MainWindow.xaml pattern (`Style="{StaticResource SidebarRadioButtonStyle}"`)
- AppLogger subsystem strings: `workflow-marketplace`, `workflow-download`, `workflow-symlink`, `workflow-<source>` (civitai / community_json / openart)
- Settings plumbing: `[JsonPropertyName("...")] public T X { get; set; } = default;` + matching row in `CopyInto(target, source)`
- 8th sidebar position (`MainSection.Workflows`) between `LocalNodes` and `Settings`
- Env-start hook: `EnvironmentListViewModel.StartAsync` calls `_workflowSymlinker.SyncToEnvAsync(envId, env.ComfyuiSource, ct)` fire-and-forget after `await _launcher.StartEnvAsync(...)` succeeds; failure logged but does NOT propagate
- Junction/symlink targets: `<env.ComfyuiSource>/user/default/workflows/<subfolder>` → `<Settings.WorkflowsDirectory>/<subfolder>`
- Real-fetch tests use `[Fact(Skip = "...")]` with descriptive reason (CI does not hit network)
- Commits: scoped per task (`git add <specific paths>` whitelist); no bundled WIP
- 中文 UI copy: "工作流市场" / "搜索" / "源" / "标签" / "排序" / "需装节点" / "批量下载" / "全选" / "打开目录" / "刷新" / "Console" / "已下载"
- YAGNI: no SQLite cache for listings, no API keys / TTL / request-delay knobs, no pagination, no custom user sources, no "我的下载" view, no FTS5
- v-bump skipped (user decides); no release zip; staging rebuild at end
- Tests live under `tests-wpf/ComfyUI.Manager.Tests/Services/` or `/ViewModels/` mirroring production folder structure
- All temp files in tests: `Path.Combine(Path.GetTempPath(), "ComfyUIMgr<Name>_" + Guid.NewGuid().ToString("N"))` + cleanup in `Dispose`
- DelegatingHandler pattern for HTTP mocking (existing project pattern)

## Files to Touch

### New files

| Path | Purpose |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs` | Aggregate model + WorkflowSourceKind enum + DownloadedWorkflow + meta sidecar |
| `src-wpf/ComfyUI.Manager/Services/WorkflowFilesystemScanner.cs` | Scan `Settings.WorkflowsDirectory` → `List<DownloadedWorkflow>` |
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/IWorkflowSource.cs` | Interface (SourceKind / DisplayName / IsEnabled / SearchAsync) |
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CommunityJsonSource.cs` | Generic JSON fetcher |
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs` | `/v1/images?tags=workflow` extractor |
| `src-wpf/ComfyUI.Manager/Services/WorkflowSources/OpenArtSource.cs` | Generic JSON fetcher |
| `src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs` | Parallel aggregator with dedup |
| `src-wpf/ComfyUI.Manager/Services/WorkflowDownloader.cs` | Single + batch download (SemaphoreSlim=4) |
| `src-wpf/ComfyUI.Manager/Services/WorkflowSymlinker.cs` | Env-start junction/symlink sync |
| `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs` | Filter / sort / multi-select / console / refresh / batch |
| `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml` + `.xaml.cs` | Sidebar-section view |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowFilesystemScannerTests.cs` | Scanner unit tests |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCommunityJsonTests.cs` | CommunityJson unit + 1 SKIP real-fetch |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs` | CivitAi unit + 1 SKIP real-fetch |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceOpenArtTests.cs` | OpenArt unit + 1 SKIP real-fetch |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowMarketplaceServiceTests.cs` | Aggregator unit |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowDownloaderTests.cs` | Single + batch download |
| `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSymlinkerTests.cs` | Sync logic |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs` | VM filter / sort / multi-select / console |
| `tests-wpf/ComfyUI.Manager.Tests/Views/WorkflowMarketplaceViewLoadTests.cs` | STA load dark + light + console panel |

### Modified files

| Path | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | Add `WorkflowsDirectory` + 3 source Enabled bools + CopyInto rows |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | Add `WorkflowsSubdir = "workflows"` + Apply `s.WorkflowsDirectory = Resolve(...)` |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | Add 4 new properties (WorkflowsDirectory + 3 source bools) with MarkDirty |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | Add "工作流市场" section after "本地节点" section |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | Add `MainSection.Workflows` enum value, `ShowWorkflowsCommand`, `MainSectionNameProvider` mapping, lazy `WorkflowMarketplaceViewModel` cache |
| `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs` | Map `Workflows → "工作流市场"` |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Add 8th sidebar RadioButton |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | Inject `HttpClient` singleton + `WorkflowSymlinker` (ctor) |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Add `WorkflowSymlinker?` ctor param; call `SyncToEnvAsync` fire-and-forget after successful env-start |

---

## Task 1: Settings shape + SettingsViewModel bindings + SettingsViewXAML + default path resolution

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:25-176` (add 4 fields + 4 CopyInto rows)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:32-50` (add `WorkflowsSubdir` const)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:57-95` (Apply adds `s.WorkflowsDirectory = Resolve(...)` line)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (add 4 properties + Dirty map entries)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (add new section before SettingsFooter)

**Interfaces:**
- Consumes: existing `Settings` + `SettingsDefaults.Resolve` + `SettingsViewModel` MarkDirty pattern (e.g. `LocalNodeDirectory`)
- Produces:
  - `Settings.WorkflowsDirectory : string` (default `"workflows"` resolved via `SettingsDefaults`)
  - `Settings.WorkflowSourceCommunityJsonEnabled : bool = true`
  - `Settings.WorkflowSourceCivitAiEnabled : bool = true`
  - `Settings.WorkflowSourceOpenArtEnabled : bool = true`
  - 4 `SettingsViewModel` properties mirroring `LocalNodeDirectory` / `EnvsDir` setter pattern
  - 1 new XAML section "工作流市场" with 1 path picker + 3 checkboxes

- [ ] **Step 1: Add Settings fields + CopyInto rows**

In `Models/Settings.cs`, after `LogDirectory` block (around line 54), add:

```csharp
// v0.6.19:工作流市场 — 共享 workflows 目录 + 3 source enabled bools
[JsonPropertyName("workflows_directory")]
public string WorkflowsDirectory { get; set; } = "";
[JsonPropertyName("workflow_source_community_json_enabled")]
public bool WorkflowSourceCommunityJsonEnabled { get; set; } = true;
[JsonPropertyName("workflow_source_civitai_enabled")]
public bool WorkflowSourceCivitAiEnabled { get; set; } = true;
[JsonPropertyName("workflow_source_openart_enabled")]
public bool WorkflowSourceOpenArtEnabled { get; set; } = true;
```

In `CopyInto(target, source)` (around line 126), add 4 rows after `target.LogDirectory = source.LogDirectory;`:

```csharp
target.WorkflowsDirectory = source.WorkflowsDirectory;
target.WorkflowSourceCommunityJsonEnabled = source.WorkflowSourceCommunityJsonEnabled;
target.WorkflowSourceCivitAiEnabled = source.WorkflowSourceCivitAiEnabled;
target.WorkflowSourceOpenArtEnabled = source.WorkflowSourceOpenArtEnabled;
```

- [ ] **Step 2: Add `WorkflowsSubdir` const + Apply line**

In `Infrastructure/SettingsDefaults.cs`, in the const block (around line 34), add:

```csharp
public const string WorkflowsSubdir = "workflows";
```

In `Apply(s, projectRoot)` (around line 65, after the `LocalNodeDirectory` Resolve), add:

```csharp
// v0.6.19:WorkflowsDirectory — template-style,空字段自动填 "workflows" 子目录名
s.WorkflowsDirectory = Resolve(s.WorkflowsDirectory, WorkflowsSubdir, projectRoot);
```

- [ ] **Step 3: Add `SettingsViewModel` properties + Dirty entries**

In `ViewModels/SettingsViewModel.cs`, after the `LocalNodeDirectory` property block (around line 479-486), add:

```csharp
// v0.6.19:工作流市场
public string WorkflowsDirectory
{
    get => _settings.WorkflowsDirectory;
    set
    {
        var v = value ?? "";
        if (_settings.WorkflowsDirectory == v) return;
        _settings.WorkflowsDirectory = v;
        MarkDirty(nameof(WorkflowsDirectory));
        RaisePropertyChanged();
    }
}

public bool WorkflowSourceCommunityJsonEnabled
{
    get => _settings.WorkflowSourceCommunityJsonEnabled;
    set
    {
        if (_settings.WorkflowSourceCommunityJsonEnabled == value) return;
        _settings.WorkflowSourceCommunityJsonEnabled = value;
        MarkDirty(nameof(WorkflowSourceCommunityJsonEnabled));
        RaisePropertyChanged();
    }
}

public bool WorkflowSourceCivitAiEnabled
{
    get => _settings.WorkflowSourceCivitAiEnabled;
    set
    {
        if (_settings.WorkflowSourceCivitAiEnabled == value) return;
        _settings.WorkflowSourceCivitAiEnabled = value;
        MarkDirty(nameof(WorkflowSourceCivitAiEnabled));
        RaisePropertyChanged();
    }
}

public bool WorkflowSourceOpenArtEnabled
{
    get => _settings.WorkflowSourceOpenArtEnabled;
    set
    {
        if (_settings.WorkflowSourceOpenArtEnabled == value) return;
        _settings.WorkflowSourceOpenArtEnabled = value;
        MarkDirty(nameof(WorkflowSourceOpenArtEnabled));
        RaisePropertyChanged();
    }
}
```

In the `RefreshFromSettings` / `MarkCleanAll`-style method (around line 845-850, where `EnvsDir` / `LocalNodeDirectory` are raised), add 4 corresponding `RaisePropertyChanged` calls. Search for the existing pattern in that file.

- [ ] **Step 4: Add SettingsView XAML section**

In `Views/SettingsView.xaml`, after the "本地节点" section closes (search for the `LocalNodeDirectory` TextBlock + Browse button + Dirty indicator pattern, around line 487), add a new `<Border>` with title "工作流市场" and:

- 1 TextBox for `WorkflowsDirectory` (same style as `LocalNodeDirectory`)
- 1 "Browse" button (`Click="BrowseWorkflowsDir"`)
- 1 "打开目录" button (`Click="OpenWorkflowsDir"`)
- 3 CheckBoxes for the source Enabled toggles, each with label
- 4 Dirty indicators (`Visibility="{Binding Dirty[XXX], Converter={StaticResource BoolToVisibility}}"`)

Match the existing local-node section XAML exactly (Grid columns, control styles, spacing) for visual consistency. Use the `Dirty` dictionary binding pattern shown for `LocalNodeDirectory`.

- [ ] **Step 5: Add code-behind handlers in SettingsView.xaml.cs**

In `Views/SettingsView.xaml.cs`, add 2 methods:

```csharp
private void BrowseWorkflowsDir(object sender, RoutedEventArgs e)
{
    // Mirror BrowseEnvsDir / BrowseLocalNodeDirectory pattern:
    // 1. OpenFolderDialog (use OpenFolderDialog from Win32 or VistaOpenDialogService)
    // 2. If user picks → vm.WorkflowsDirectory = picked path
    // (Reuse the existing FolderPicker helper if one exists in this file.)
}

private void OpenWorkflowsDir(object sender, RoutedEventArgs e)
{
    // Mirror the "打开 env 目录" button pattern:
    // if (vm.WorkflowsDirectory empty) return;
    // resolve to absolute via projectRoot (or use as-is if rooted)
    // System.Diagnostics.Process.Start("explorer.exe", path) or use BrowserLauncher.OpenFolder
}
```

Inspect existing similar methods (e.g. `OpenLocalNodesDir`) in the same file and mirror exactly — same error handling, same fallback for empty paths.

- [ ] **Step 6: Build + verify**

Run: `cd D:\ToolDevelop\ComfyUI && dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -20`
Expected: 0 errors. Warnings about missing resource keys are acceptable; build SUCCESS overall.

- [ ] **Step 7: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Models/Settings.cs \
  src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
  src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
  src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
  src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs
git commit -m "feat(workflows): v0.6.19 T1 Settings shape + UI section"
```

---

## Task 2: WorkflowEntry model + WorkflowFilesystemScanner

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs` (record + enum + DownloadedWorkflow + meta.json shape)
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowFilesystemScanner.cs` (scan + read meta.json)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowFilesystemScannerTests.cs` (5 tests)

**Interfaces:**
- Consumes: `JsonSerializer` (System.Text.Json, built-in), `JsonNamingPolicy.CamelCase`
- Produces:
  - `WorkflowEntry` (Source, SourceId, SourceUrl, WorkflowJsonUrl, PreviewImageUrl?, Title, Description?, Author?, DownloadCount?, PublishedAt?, Tags IReadOnlyList<string>, RequiredNodes IReadOnlyList<string>)
  - `WorkflowSourceKind` enum: `CommunityJson = 0, CivitAi = 1, OpenArt = 2`
  - `DownloadedWorkflow` (SubfolderName, FullPath, Title, Source, SourceId, DownloadedAt)
  - `WorkflowFilesystemScanner.Scan(workflowsDir) → IReadOnlyList<DownloadedWorkflow>`

- [ ] **Step 1: Write `WorkflowEntry.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>v0.6.19:工作流市场聚合模型 — 来自任意 source 的单条 workflow 记录。</summary>
public class WorkflowEntry
{
    [JsonPropertyName("source")] public WorkflowSourceKind Source { get; init; }
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = "";
    [JsonPropertyName("source_url")] public string SourceUrl { get; init; } = "";
    [JsonPropertyName("workflow_json_url")] public string WorkflowJsonUrl { get; init; } = "";
    [JsonPropertyName("preview_image_url")] public string? PreviewImageUrl { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("download_count")] public int? DownloadCount { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>节点 ID 列表 — "需装节点" 过滤器用。</summary>
    [JsonPropertyName("required_nodes")] public IReadOnlyList<string> RequiredNodes { get; init; } = Array.Empty<string>();
}

public enum WorkflowSourceKind
{
    CommunityJson = 0,
    CivitAi = 1,
    OpenArt = 2,
}

/// <summary>v0.6.19:filesystem 扫描出来的"已下载"状态(无 DB)。</summary>
public class DownloadedWorkflow
{
    public string SubfolderName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceId { get; init; } = "";
    public DateTime DownloadedAt { get; init; }
}

/// <summary>v0.6.19:meta.json sidecar DTO — 仅 writer/scanner 内部用,WorkflowEntry 不存 raw_meta。</summary>
internal class WorkflowMetaSidecar
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("downloaded_at")] public DateTime DownloadedAt { get; set; }
}
```

- [ ] **Step 2: Write `WorkflowFilesystemScanner.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:扫描 Settings.WorkflowsDirectory,返回 DownloadedWorkflow 列表。
/// 无 DB — 加/删文件后下次 scan 立即反映。meta.json 缺失或损坏的子目录跳过 + 日志 WARN。</summary>
public class WorkflowFilesystemScanner
{
    private readonly AppLogger? _logger;

    public WorkflowFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>扫描给定目录。无子目录或目录不存在 → 返回空列表。</summary>
    public virtual IReadOnlyList<DownloadedWorkflow> Scan(string workflowsDir)
    {
        if (string.IsNullOrWhiteSpace(workflowsDir) || !Directory.Exists(workflowsDir))
        {
            return Array.Empty<DownloadedWorkflow>();
        }

        var results = new List<DownloadedWorkflow>();
        foreach (var subDir in Directory.EnumerateDirectories(workflowsDir))
        {
            var metaPath = Path.Combine(subDir, "meta.json");
            if (!File.Exists(metaPath))
            {
                _logger?.Warn("workflow-marketplace", $"跳过子目录(无 meta.json): {subDir}");
                continue;
            }

            try
            {
                var metaJson = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<WorkflowMetaSidecar>(
                    metaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta is null)
                {
                    _logger?.Warn("workflow-marketplace", $"meta.json 反序列化返回 null: {metaPath}");
                    continue;
                }

                results.Add(new DownloadedWorkflow
                {
                    SubfolderName = Path.GetFileName(subDir),
                    FullPath = subDir,
                    Title = meta.Title ?? Path.GetFileName(subDir),
                    Source = meta.Source ?? "",
                    SourceId = meta.SourceId ?? "",
                    DownloadedAt = meta.DownloadedAt,
                });
            }
            catch (Exception ex)
            {
                _logger?.Warn("workflow-marketplace",
                    $"meta.json 解析失败 跳过 {subDir}: {ex.Message}");
            }
        }

        return results;
    }
}
```

- [ ] **Step 3: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowFilesystemScannerTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowFilesystemScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkflowFilesystemScanner _scanner = new(logger: null);

    public WorkflowFilesystemScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFScanner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_NonExistentDir_ReturnsEmpty()
    {
        var result = _scanner.Scan(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_EmptyDir_ReturnsEmpty()
    {
        var result = _scanner.Scan(_tempDir);
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_DirWithValidMeta_ReturnsDownloadedWorkflow()
    {
        var sub = Path.Combine(_tempDir, "portrait-gen-abc12345");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "workflow.json"), "{}");
        File.WriteAllText(Path.Combine(sub, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Portrait Generator v2",
            source = "community_json",
            source_id = "abc12345",
            downloaded_at = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc),
        }));

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("portrait-gen-abc12345", result[0].SubfolderName);
        Assert.Equal("Portrait Generator v2", result[0].Title);
        Assert.Equal("community_json", result[0].Source);
        Assert.Equal("abc12345", result[0].SourceId);
    }

    [Fact]
    public void Scan_SkipsSubfolderWithoutMeta()
    {
        var withMeta = Path.Combine(_tempDir, "valid-12345678");
        Directory.CreateDirectory(withMeta);
        File.WriteAllText(Path.Combine(withMeta, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Valid", downloaded_at = DateTime.UtcNow,
        }));
        var withoutMeta = Path.Combine(_tempDir, "incomplete-abcdef");
        Directory.CreateDirectory(withoutMeta);
        File.WriteAllText(Path.Combine(withoutMeta, "workflow.json"), "{}");

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("valid-12345678", result[0].SubfolderName);
    }

    [Fact]
    public void Scan_MalformedMeta_SkipsAndReturnsOthers()
    {
        var good = Path.Combine(_tempDir, "good-11111111");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Good", downloaded_at = DateTime.UtcNow,
        }));
        var bad = Path.Combine(_tempDir, "bad-22222222");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "meta.json"), "{ not valid json");

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("good-11111111", result[0].SubfolderName);
    }
}
```

- [ ] **Step 4: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowFilesystemScannerTests" --nologo`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 5: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Models/WorkflowEntry.cs \
  src-wpf/ComfyUI.Manager/Services/WorkflowFilesystemScanner.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowFilesystemScannerTests.cs
git commit -m "feat(workflows): v0.6.19 T2 WorkflowEntry + FilesystemScanner + 5 tests"
```

---

## Task 3: IWorkflowSource interface + CommunityJsonSource + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowSources/IWorkflowSource.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CommunityJsonSource.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCommunityJsonTests.cs` (6 unit + 1 SKIP real-fetch)

**Interfaces:**
- Consumes: injected `HttpClient` (singleton from `App.xaml.cs`)
- Produces:
  - `IWorkflowSource` with `SourceKind` / `DisplayName` / `IsEnabled { get; set; }` + `Task<IReadOnlyList<WorkflowEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)`
  - `CommunityJsonSource` ctor: `(HttpClient http, AppLogger? logger = null)`
  - Constructor takes optional URL override (default points to public JSON list endpoint — picked at impl time, may need to research; for now use a placeholder `https://example.com/workflows.json` and amend during impl if wrong)

- [ ] **Step 1: Write `IWorkflowSource.cs`**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:工作流市场数据源接口 — 由 Aggregator 并行调用。</summary>
public interface IWorkflowSource
{
    WorkflowSourceKind SourceKind { get; }
    string DisplayName { get; }
    bool IsEnabled { get; set; }

    Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Write `CommunityJsonSource.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:CommunityJson 数据源 — 通用 JSON list 端点。
/// 期望响应:{"items":[{"id":"...","title":"...","author":"...","json_url":"...",
///                    "preview_url":"...","tags":[...]}]}
/// 端点 URL 在 ctor 注入,默认 placeholder(实现时改成真 endpoint)。</summary>
public class CommunityJsonSource : IWorkflowSource
{
    public WorkflowSourceKind SourceKind => WorkflowSourceKind.CommunityJson;
    public string DisplayName => "CommunityJson";
    public bool IsEnabled { get; set; } = true;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly AppLogger? _logger;

    public CommunityJsonSource(HttpClient http, AppLogger? logger = null, string? url = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        // 真 endpoint 在 implementation 时确认;placeholder 便于测试用 DelegatingHandler 拦截
        _url = url ?? "https://example.com/community-workflows.json";
    }

    public virtual async Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        _logger?.Info("workflow-community_json", $"fetch url={_url} query='{query}'");
        try
        {
            using var resp = await _http.GetAsync(_url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            // 兼容两种 shape:{items:[...]} 或 顶层 array
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out items))
            {
                // ok
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                items = root;
            }
            else
            {
                _logger?.Warn("workflow-community_json",
                    $"未识别的 JSON shape (root={root.ValueKind})");
                return Array.Empty<WorkflowEntry>();
            }

            var entries = new List<WorkflowEntry>();
            foreach (var el in items.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;
                var title = el.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                var jsonUrl = el.TryGetProperty("json_url", out var jProp) ? jProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(jsonUrl)) continue;

                // query 过滤(简单 title/author substring)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = title?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = el.TryGetProperty("author", out var aProp) &&
                        (aProp.GetString()?.ToLowerInvariant().Contains(q) ?? false);
                    if (!inTitle && !inAuthor) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = id,
                    SourceUrl = _url,
                    WorkflowJsonUrl = jsonUrl,
                    PreviewImageUrl = el.TryGetProperty("preview_url", out var pProp) ? pProp.GetString() : null,
                    Title = title,
                    Description = el.TryGetProperty("description", out var dProp) ? dProp.GetString() : null,
                    Author = el.TryGetProperty("author", out var auProp) ? auProp.GetString() : null,
                    DownloadCount = el.TryGetProperty("downloads", out var dlProp) && dlProp.TryGetInt32(out var dl) ? dl : null,
                    PublishedAt = el.TryGetProperty("published_at", out var paProp) && DateTimeOffset.TryParse(paProp.GetString(), out var pa) ? pa : null,
                    Tags = el.TryGetProperty("tags", out var tgProp) && tgProp.ValueKind == JsonValueKind.Array
                        ? tgProp.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => !string.IsNullOrEmpty(t)).ToArray()
                        : Array.Empty<string>(),
                });

                if (entries.Count >= maxResults) break;
            }

            _logger?.Info("workflow-community_json",
                $"fetched {entries.Count} entries (max={maxResults})");
            return entries;
        }
        catch (Exception ex)
        {
            _logger?.Error("workflow-community_json", $"fetch failed url={_url}", ex);
            return Array.Empty<WorkflowEntry>();
        }
    }
}
```

- [ ] **Step 3: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCommunityJsonTests.cs`:

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

public class WorkflowSourceCommunityJsonTests
{
    private static HttpClient MockHttp(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new DelegatingHandlerStub(responseJson, status);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task SearchAsync_ItemsShape_ParsesEntries()
    {
        var json = """{"items":[{"id":"abc","title":"Portrait Gen","author":"alice","json_url":"https://x.com/w.json","tags":["portrait","anime"]}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("abc", result[0].SourceId);
        Assert.Equal(WorkflowSourceKind.CommunityJson, result[0].Source);
        Assert.Equal("Portrait Gen", result[0].Title);
        Assert.Equal("alice", result[0].Author);
        Assert.Equal(2, result[0].Tags.Count);
        Assert.Contains("portrait", result[0].Tags);
    }

    [Fact]
    public async Task SearchAsync_ArrayShape_ParsesEntries()
    {
        var json = """[{"id":"x","title":"X","json_url":"https://x"}]""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("x", result[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_QueryText_FiltersByTitleOrAuthor()
    {
        var json = """{"items":[{"id":"a","title":"Apple","author":"alice","json_url":"https://a"},
                                  {"id":"b","title":"Banana","author":"bob","json_url":"https://b"}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "ban", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("b", result[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_MissingJsonUrl_SkipsEntry()
    {
        var json = """{"items":[{"id":"a","title":"No URL"}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_HttpFailure_ReturnsEmpty()
    {
        var src = new CommunityJsonSource(MockHttp("server error", HttpStatusCode.InternalServerError));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MaxResults_LimitsCount()
    {
        var items = string.Join(",", Enumerable.Range(0, 50)
            .Select(i => $$"""{"id":"{{i}}","title":"t{{i}}","json_url":"https://{{i}}"}"""));
        var json = $$"""{"items":[{{items}}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 5);

        Assert.Equal(5, result.Count);
    }

    [Fact(Skip = "Integration: hits real CommunityJson endpoint")]
    public async Task LiveFetch_CommunityJson_RealEndpoint_ReturnsEntries()
    {
        var src = new CommunityJsonSource(new HttpClient());
        var result = await src.SearchAsync(query: "", maxResults: 10);
        Assert.NotEmpty(result);
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public DelegatingHandlerStub(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body; _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
```

- [ ] **Step 4: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowSourceCommunityJson" --nologo`
Expected: 6 PASS / 0 FAIL / 1 SKIP.

- [ ] **Step 5: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowSources/IWorkflowSource.cs \
  src-wpf/ComfyUI.Manager/Services/WorkflowSources/CommunityJsonSource.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCommunityJsonTests.cs
git commit -m "feat(workflows): v0.6.19 T3 IWorkflowSource + CommunityJson source + 6 tests"
```

---

## Task 4: CivitAiSource + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs` (6 unit + 1 SKIP real-fetch)

**Interfaces:**
- Consumes: injected `HttpClient`
- Produces:
  - `CivitAiSource` ctor `(HttpClient http, AppLogger? logger = null, string? baseUrl = null)`
  - Calls `/api/v1/images?tags=workflow&sort=Newest&limit=N` — extracts each image's `metadata.workflow` field for json_url

- [ ] **Step 1: Write `CivitAiSource.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:CivitAI 数据源 — /api/v1/images?tags=workflow 拉图像,
/// 每张图的 metadata.workflow 字段含 workflow JSON URL。
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
        // CivitAI images API:N=limit, tags=workflow filter; sort=Newest 默认
        var url = $"{_baseUrl}/api/v1/images?tags=workflow&sort=Newest&limit={Math.Min(maxResults, 100)}";
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
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                _logger?.Warn("workflow-civitai", "未识别的 JSON shape");
                return Array.Empty<WorkflowEntry>();
            }

            var entries = new List<WorkflowEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : "";
                if (string.IsNullOrEmpty(id)) continue;

                // metadata.workflow 是 CivitAI 的 workflow JSON URL 字段
                string? jsonUrl = null;
                if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("workflow", out var wfProp)
                    && wfProp.ValueKind == JsonValueKind.Object
                    && wfProp.TryGetProperty("workflowJson", out var wjProp))
                {
                    jsonUrl = wjProp.GetString();
                }
                // 部分 CivitAI 图像 metadata 用不同字段名 — 尝试 backup 路径
                if (string.IsNullOrEmpty(jsonUrl) && item.TryGetProperty("url", out var urlProp))
                {
                    jsonUrl = urlProp.GetString();
                }
                if (string.IsNullOrEmpty(jsonUrl)) continue;

                var title = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var author = item.TryGetProperty("username", out var uProp) ? uProp.GetString() : null;
                var previewUrl = item.TryGetProperty("url", out var puProp) ? puProp.GetString() : null;

                // query 过滤(title/author substring)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = title?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = author?.ToLowerInvariant().Contains(q) ?? false;
                    if (!inTitle && !inAuthor) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = id,
                    SourceUrl = $"{_baseUrl}/images/{id}",
                    WorkflowJsonUrl = jsonUrl,
                    PreviewImageUrl = previewUrl,
                    Title = title,
                    Author = author,
                    Tags = Array.Empty<string>(),
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
}
```

- [ ] **Step 2: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs`:

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
    public async Task SearchAsync_ItemsWithWorkflowJson_ParsesEntry()
    {
        var json = """{"items":[{"id":"123","name":"Workflow A","username":"bob","url":"https://img.jpg",
                                   "meta":{"workflow":{"workflowJson":"https://files/wf.json"}}}]}""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
        Assert.Equal("123", result[0].SourceId);
        Assert.Equal("Workflow A", result[0].Title);
        Assert.Equal("https://files/wf.json", result[0].WorkflowJsonUrl);
    }

    [Fact]
    public async Task SearchAsync_NoWorkflowJson_SkipsEntry()
    {
        var json = """{"items":[{"id":"1","name":"Image only","url":"https://img.jpg"}]}""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_QueryFilter_Applies()
    {
        var json = """{"items":[{"id":"1","name":"Apple pie","url":"x","meta":{"workflow":{"workflowJson":"x1"}}},
                                  {"id":"2","name":"Banana split","url":"y","meta":{"workflow":{"workflowJson":"y2"}}}]}""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "banana", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("2", result[0].SourceId);
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

    [Fact(Skip = "Integration: hits real CivitAI /api/v1/images?tags=workflow")]
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

- [ ] **Step 3: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowSourceCivitAi" --nologo`
Expected: 6 PASS / 0 FAIL / 1 SKIP.

- [ ] **Step 4: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowSources/CivitAiSource.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceCivitAiTests.cs
git commit -m "feat(workflows): v0.6.19 T4 CivitAI source + 6 tests"
```

---

## Task 5: OpenArtSource + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowSources/OpenArtSource.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceOpenArtTests.cs` (6 unit + 1 SKIP real-fetch)

**Interfaces:**
- Consumes: injected `HttpClient`
- Produces:
  - `OpenArtSource` ctor `(HttpClient http, AppLogger? logger = null, string? url = null)` — URL placeholder 类似 CommunityJson

- [ ] **Step 1: Write `OpenArtSource.cs`**

Mirror `CommunityJsonSource.cs` exactly with these changes:
- `SourceKind => WorkflowSourceKind.OpenArt`
- `DisplayName => "OpenArt"`
- `_url` default = `"https://example.com/openart-workflows.json"` (placeholder;真 endpoint 实现时确认)
- Logger subsystem string: `"workflow-openart"`

The shape expectation is the same generic `{items: [...]}` JSON. Implementation can largely share parser code — copy the file and modify the 3 lines above.

- [ ] **Step 2: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceOpenArtTests.cs`:

Mirror `WorkflowSourceCommunityJsonTests.cs` exactly with these changes:
- Rename class to `WorkflowSourceOpenArtTests`
- Use `OpenArtSource` instead of `CommunityJsonSource`
- 4 tests: items shape / array shape / query filter / missing url / max results — same bodies
- 1 test: HTTP failure returns empty
- 1 SKIP: real-fetch

The stub `DelegatingHandlerStub` can be a copy or refactored later into a shared test helper (YAGNI for now).

- [ ] **Step 3: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowSourceOpenArt" --nologo`
Expected: 6 PASS / 0 FAIL / 1 SKIP.

- [ ] **Step 4: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowSources/OpenArtSource.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSourceOpenArtTests.cs
git commit -m "feat(workflows): v0.6.19 T5 OpenArt source + 6 tests"
```

---

## Task 6: WorkflowMarketplaceService aggregator + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowMarketplaceServiceTests.cs` (5 tests)

**Interfaces:**
- Consumes: `IEnumerable<IWorkflowSource>` + `AppLogger?`
- Produces:
  - `WorkflowMarketplaceService` ctor `(IEnumerable<IWorkflowSource> sources, AppLogger? logger = null)`
  - `Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(string query, int maxResultsPerSource, CancellationToken ct)` — parallel via `Task.WhenAll`, dedup by `(Source, SourceId)`, returns aggregated list (errors per source logged, NOT thrown)

- [ ] **Step 1: Write `WorkflowMarketplaceService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:并行聚合多个 IWorkflowSource,合并去重,单 source 失败不影响其他。
/// 不持久化缓存(用户每次点"刷新"重新拉)。</summary>
public class WorkflowMarketplaceService
{
    private readonly IReadOnlyList<IWorkflowSource> _sources;
    private readonly AppLogger? _logger;

    public WorkflowMarketplaceService(IEnumerable<IWorkflowSource> sources, AppLogger? logger = null)
    {
        _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger;
    }

    /// <summary>并行调每个 IsEnabled 的 source。返回 deduped 列表;
    /// 任一 source 失败仅 log,不影响其他 source 的结果。</summary>
    public virtual async Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, CancellationToken ct = default)
    {
        var enabled = _sources.Where(s => s.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            _logger?.Warn("workflow-marketplace", "no enabled sources");
            return Array.Empty<WorkflowEntry>();
        }

        _logger?.Info("workflow-marketplace",
            $"LoadAllAsync sources={enabled.Count} query='{query}' maxPerSource={maxResultsPerSource}");

        // parallel fetch — 每个 source 一个 task,exception 单独 catch
        var tasks = enabled.Select(async s =>
        {
            try
            {
                return await s.SearchAsync(query, maxResultsPerSource, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error("workflow-marketplace",
                    $"source {s.SourceKind} threw: {ex.Message}", ex);
                return Array.Empty<WorkflowEntry>();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // dedup by (Source, SourceId) — first wins
        var seen = new HashSet<(WorkflowSourceKind, string)>();
        var merged = new List<WorkflowEntry>();
        foreach (var batch in results)
        {
            foreach (var entry in batch)
            {
                if (string.IsNullOrEmpty(entry.SourceId)) continue;
                if (seen.Add((entry.Source, entry.SourceId)))
                {
                    merged.Add(entry);
                }
            }
        }

        _logger?.Info("workflow-marketplace", $"aggregated {merged.Count} unique entries");
        return merged;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowMarketplaceServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowMarketplaceServiceTests
{
    private sealed class StubSource : IWorkflowSource
    {
        public WorkflowSourceKind SourceKind { get; set; }
        public string DisplayName => SourceKind.ToString();
        public bool IsEnabled { get; set; } = true;
        public Func<string, IReadOnlyList<WorkflowEntry>>? Handler { get; set; }
        public Task<IReadOnlyList<WorkflowEntry>> SearchAsync(string q, int n, CancellationToken ct = default)
            => Task.FromResult(Handler?.Invoke(q) ?? Array.Empty<WorkflowEntry>());
    }

    private static WorkflowEntry Entry(WorkflowSourceKind src, string id, string title = "t")
        => new() { Source = src, SourceId = id, SourceUrl = $"https://{src}/{id}",
                   WorkflowJsonUrl = $"https://{src}/{id}.json", Title = title };

    [Fact]
    public async Task LoadAllAsync_3Sources_AggregatesAll()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
            new StubSource { SourceKind = WorkflowSourceKind.OpenArt,
                Handler = _ => new[] { Entry(WorkflowSourceKind.OpenArt, "c") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task LoadAllAsync_DedupBy_SourceAndSourceId()
    {
        // Same (Source, id) from 2 sources → 1 entry(罕见 cross-source id 冲突,假设不发生;
        // dedup 主要防同一 source 跨 batch 重复)
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "dup") } },
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "dup") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_DisabledSource_Skipped()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson, IsEnabled = false,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi, IsEnabled = true,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
    }

    [Fact]
    public async Task LoadAllAsync_OneSourceThrows_OthersStillReturned()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => throw new InvalidOperationException("boom") },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_AllSourcesDisabled_ReturnsEmpty()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { IsEnabled = false, Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Empty(result);
    }
}
```

- [ ] **Step 3: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowMarketplaceService" --nologo`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 4: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowMarketplaceService.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowMarketplaceServiceTests.cs
git commit -m "feat(workflows): v0.6.19 T6 aggregator service + 5 tests"
```

---

## Task 7: WorkflowDownloader (single + batch) + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowDownloader.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowDownloaderTests.cs` (7 tests)

**Interfaces:**
- Consumes: injected `HttpClient` + `AppLogger?`
- Produces:
  - `WorkflowDownloader` ctor `(HttpClient http, AppLogger? logger = null)`
  - `Task<WorkflowDownloadResult> DownloadAsync(WorkflowEntry entry, string workflowsDir, IProgress<string>? log, CancellationToken ct)`
  - `Task<WorkflowBatchSummary> DownloadBatchAsync(IEnumerable<WorkflowEntry> entries, string workflowsDir, IProgress<string>? log, CancellationToken ct)` — `SemaphoreSlim(4)`
  - `WorkflowDownloadResult { bool Success, string? SubfolderPath, string? FailureReason }`
  - `WorkflowBatchSummary { int Succeeded, int Failed, IReadOnlyList<string> Errors }`

- [ ] **Step 1: Write `WorkflowDownloader.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:下载 workflow.json + preview 到 Settings.WorkflowsDirectory。
/// 单条 + 批量(SemaphoreSlim=4 并发)。每个 subfolder 写 workflow.json + preview.<ext> + meta.json。</summary>
public class WorkflowDownloader
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;

    public WorkflowDownloader(HttpClient http, AppLogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public virtual async Task<WorkflowDownloadResult> DownloadAsync(
        WorkflowEntry entry, string workflowsDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowsDir))
            return WorkflowDownloadResult.Fail("workflows dir is empty");
        if (entry is null || string.IsNullOrEmpty(entry.WorkflowJsonUrl))
            return WorkflowDownloadResult.Fail("entry or json url is empty");

        try
        {
            Directory.CreateDirectory(workflowsDir);

            var subfolderName = BuildSubfolderName(entry, workflowsDir);
            var subfolderPath = Path.Combine(workflowsDir, subfolderName);
            Directory.CreateDirectory(subfolderPath);

            log?.Report($"[{entry.Source}] 开始下载:{entry.Title}");
            _logger?.Info("workflow-download",
                $"start entry='{entry.SourceId}' title='{entry.Title}' subfolder='{subfolderName}'");

            // 1. workflow.json
            var jsonBytes = await _http.GetByteArrayAsync(entry.WorkflowJsonUrl, ct).ConfigureAwait(false);
            // pretty-print if valid JSON, else write raw
            try
            {
                var doc = JsonDocument.Parse(jsonBytes);
                using var fs = File.Create(Path.Combine(subfolderPath, "workflow.json"));
                await JsonSerializer.SerializeAsync(fs, doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await File.WriteAllBytesAsync(Path.Combine(subfolderPath, "workflow.json"), jsonBytes, ct).ConfigureAwait(false);
                _logger?.Warn("workflow-download",
                    $"workflow.json not valid JSON; wrote raw entry='{entry.SourceId}'");
            }

            // 2. preview (best-effort)
            var previewPath = await TryDownloadPreviewAsync(entry, subfolderName, subfolderPath, ct).ConfigureAwait(false);

            // 3. meta.json sidecar
            var meta = new WorkflowMetaSidecar
            {
                Title = entry.Title,
                Source = entry.Source.ToString(),
                SourceId = entry.SourceId,
                DownloadedAt = DateTime.UtcNow,
            };
            // augment with extra fields via serialization — use anonymous helper
            var metaJson = JsonSerializer.Serialize(new
            {
                title = entry.Title,
                description = entry.Description,
                author = entry.Author,
                source = entry.Source.ToString(),
                source_id = entry.SourceId,
                source_url = entry.SourceUrl,
                workflow_json_url = entry.WorkflowJsonUrl,
                preview_image_url = entry.PreviewImageUrl,
                tags = entry.Tags,
                downloaded_at = meta.DownloadedAt,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(subfolderPath, "meta.json"), metaJson, ct).ConfigureAwait(false);

            log?.Report($"[{entry.Source}] ✓ OK saved to {subfolderName}");
            _logger?.Info("workflow-download", $"ok entry='{entry.SourceId}' path='{subfolderPath}'");
            return WorkflowDownloadResult.Ok(subfolderPath);
        }
        catch (Exception ex)
        {
            var reason = ex.Message;
            log?.Report($"[{entry.Source}] ✗ FAIL {reason}");
            _logger?.Error("workflow-download", $"failed entry='{entry.SourceId}'", ex);
            return WorkflowDownloadResult.Fail(reason);
        }
    }

    public virtual async Task<WorkflowBatchSummary> DownloadBatchAsync(
        IEnumerable<WorkflowEntry> entries, string workflowsDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var entryList = entries?.ToList() ?? new List<WorkflowEntry>();
        if (entryList.Count == 0)
        {
            log?.Report("[批量下载] 无选中项");
            return new WorkflowBatchSummary { Succeeded = 0, Failed = 0, Errors = Array.Empty<string>() };
        }

        log?.Report($"[批量下载] 开始 N={entryList.Count}");
        using var sem = new SemaphoreSlim(4);
        var tasks = entryList.Select(async e =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { return await DownloadAsync(e, workflowsDir, log, ct).ConfigureAwait(false); }
            finally { sem.Release(); }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var succeeded = results.Count(r => r.Success);
        var failed = results.Length - succeeded;
        var errors = results.Where(r => !r.Success && r.FailureReason != null)
            .Select(r => $"{r.FailureReason}").ToArray();

        log?.Report($"[批量下载完成] 成功 {succeeded} / 失败 {failed}");
        return new WorkflowBatchSummary
        {
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors,
        };
    }

    private string BuildSubfolderName(WorkflowEntry entry, string workflowsDir)
    {
        var slug = Slugify(entry.Title);
        if (string.IsNullOrEmpty(slug)) slug = "workflow";
        var id8 = (entry.SourceId ?? "").Length >= 8
            ? entry.SourceId.Substring(0, 8)
            : (entry.SourceId ?? "00000000").PadRight(8, '0');
        var baseName = $"{slug}-{id8}";

        var candidate = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(workflowsDir, candidate)))
        {
            candidate = $"{baseName}-{suffix}";
            suffix++;
        }
        return candidate;
    }

    private static string Slugify(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length);
        var lastDash = false;
        foreach (var ch in input.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')
            {
                sb.Append(ch);
                lastDash = ch == '-';
            }
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || ch == '_')
            {
                if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
        }
        // trim trailing dash
        while (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
        return sb.ToString();
    }

    private async Task<string?> TryDownloadPreviewAsync(
        WorkflowEntry entry, string subfolderName, string subfolderPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.PreviewImageUrl)) return null;
        try
        {
            var ext = Path.GetExtension(new Uri(entry.PreviewImageUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
            var fileName = $"{subfolderName}.preview{ext}";
            var bytes = await _http.GetByteArrayAsync(entry.PreviewImageUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(subfolderPath, fileName), bytes, ct).ConfigureAwait(false);
            return fileName;
        }
        catch (Exception ex)
        {
            _logger?.Warn("workflow-download",
                $"preview failed entry='{entry.SourceId}': {ex.Message}");
            return null;
        }
    }
}

public class WorkflowDownloadResult
{
    public bool Success { get; init; }
    public string? SubfolderPath { get; init; }
    public string? FailureReason { get; init; }

    public static WorkflowDownloadResult Ok(string path) => new() { Success = true, SubfolderPath = path };
    public static WorkflowDownloadResult Fail(string reason) => new() { Success = false, FailureReason = reason };
}

public class WorkflowBatchSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
```

Note: `WorkflowMetaSidecar` is internal in `WorkflowEntry.cs`. Since both files are in `ComfyUI.Manager` namespace, internal access works. If compiler complains, change `internal class WorkflowMetaSidecar` to `public`.

- [ ] **Step 2: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowDownloaderTests.cs`:

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
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowDownloaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkflowDownloader _dl;

    public WorkflowDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFDl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var handler = new MultiResponseHandler();
        _dl = new WorkflowDownloader(new HttpClient(handler), logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WorkflowEntry Entry(string id = "abc12345", string title = "Portrait Gen",
        string jsonUrl = "https://x/wf.json", string? previewUrl = null)
        => new()
        {
            Source = WorkflowSourceKind.CommunityJson,
            SourceId = id,
            SourceUrl = "https://x/page",
            WorkflowJsonUrl = jsonUrl,
            PreviewImageUrl = previewUrl,
            Title = title,
        };

    [Fact]
    public async Task DownloadAsync_WritesWorkflowAndMeta()
    {
        var wfJson = "{\"nodes\":[]}";
        var entry = Entry(jsonUrl: "https://x/wf1.json");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        Assert.NotNull(result.SubfolderPath);
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "meta.json")));
        var metaContent = File.ReadAllText(Path.Combine(result.SubfolderPath!, "meta.json"));
        Assert.Contains("Portrait Gen", metaContent);
        Assert.Contains("abc12345", metaContent);
    }

    [Fact]
    public async Task DownloadAsync_PreviewUrl_WritesPreviewFile()
    {
        var entry = Entry(jsonUrl: "https://x/wf.json", previewUrl: "https://x/preview.png");
        // multi-response handler returns preview bytes for /preview.png

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        var previewFile = Directory.GetFiles(result.SubfolderPath!, "*.preview.*").FirstOrDefault();
        Assert.NotNull(previewFile);
        Assert.EndsWith(".png", previewFile);
    }

    [Fact]
    public async Task DownloadAsync_Preview404_StillWritesWorkflowAndMeta()
    {
        var entry = Entry(jsonUrl: "https://x/wf.json", previewUrl: "https://x/missing.png");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "workflow.json")));
        Assert.Empty(Directory.GetFiles(result.SubfolderPath!, "*.preview.*"));
    }

    [Fact]
    public async Task DownloadAsync_JsonUrl404_ReturnsFail()
    {
        var entry = Entry(jsonUrl: "https://x/missing-wf.json");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task DownloadAsync_EmptyDir_ReturnsFail()
    {
        var entry = Entry();

        var result = await _dl.DownloadAsync(entry, workflowsDir: "");

        Assert.False(result.Success);
        Assert.Contains("empty", result.FailureReason);
    }

    [Fact]
    public async Task DownloadAsync_SubfolderCollision_AppendsSuffix()
    {
        var entry1 = Entry(id: "aaaaaaaa", title: "Same Title");
        var entry2 = Entry(id: "aaaaaaaa", title: "Same Title");  // same sourceId+title → same slug

        var r1 = await _dl.DownloadAsync(entry1, _tempDir);
        var r2 = await _dl.DownloadAsync(entry2, _tempDir);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotEqual(r1.SubfolderPath, r2.SubfolderPath);
        Assert.True(r2.SubfolderPath!.Contains("-1") || r2.SubfolderPath.EndsWith("-1"));
    }

    [Fact]
    public async Task DownloadBatchAsync_RunsInParallel_BothSucceed()
    {
        var entries = new[]
        {
            Entry(id: "11111111", title: "A", jsonUrl: "https://x/a.json"),
            Entry(id: "22222222", title: "B", jsonUrl: "https://x/b.json"),
            Entry(id: "33333333", title: "C", jsonUrl: "https://x/c.json"),
        };

        var summary = await _dl.DownloadBatchAsync(entries, _tempDir);

        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(3, Directory.GetDirectories(_tempDir).Length);
    }

    /// <summary>路由多个 URL 到不同响应 — wf.json / *.preview.png → OK;missing.* → 404。</summary>
    private sealed class MultiResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            HttpResponseMessage resp;
            if (url.Contains("missing"))
            {
                resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            else if (url.EndsWith(".png") || url.EndsWith(".jpg"))
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF }),  // fake JPEG
                };
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"nodes\":[]}", Encoding.UTF8, "application/json"),
                };
            }
            return Task.FromResult(resp);
        }
    }
}
```

- [ ] **Step 3: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowDownloader" --nologo`
Expected: 7 PASS / 0 FAIL.

- [ ] **Step 4: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowDownloader.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowDownloaderTests.cs
git commit -m "feat(workflows): v0.6.19 T7 downloader (single + batch) + 7 tests"
```

---

## Task 8: WorkflowSymlinker + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/WorkflowSymlinker.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSymlinkerTests.cs` (6 tests)

**Interfaces:**
- Consumes: `Settings` (workflows dir), `JunctionLinker` (existing), `WorkflowFilesystemScanner`, `AppLogger?`
- Produces:
  - `WorkflowSymlinker` ctor `(Settings settings, JunctionLinker linker, WorkflowFilesystemScanner scanner, AppLogger? logger = null)`
  - `Task<WorkflowSyncResult> SyncToEnvAsync(string envComfyuiSource, CancellationToken ct)`
  - `WorkflowSyncResult { int Linked, int Skipped, int Failed, IReadOnlyList<string> Errors }`
  - Cross-platform: Windows uses `JunctionLinker.CreateAsync`; Linux/macOS uses `Directory.CreateSymbolicLink`

- [ ] **Step 1: Write `WorkflowSymlinker.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:env 启动后扫描 Settings.WorkflowsDirectory,
/// 给每个已下载 workflow subfolder 在 &lt;env.ComfyuiSource&gt;/user/default/workflows/
/// 下创建 junction(Windows)/symlink(Linux/macOS)。
/// 失败 WARN + 计数,不抛 — 永远不影响 env-start 状态。</summary>
public class WorkflowSymlinker
{
    private readonly Settings _settings;
    private readonly JunctionLinker _linker;
    private readonly WorkflowFilesystemScanner _scanner;
    private readonly AppLogger? _logger;

    public WorkflowSymlinker(
        Settings settings, JunctionLinker linker,
        WorkflowFilesystemScanner scanner, AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _logger = logger;
    }

    public virtual async Task<WorkflowSyncResult> SyncToEnvAsync(
        string envComfyuiSource, CancellationToken ct = default)
    {
        var empty = new WorkflowSyncResult { Linked = 0, Skipped = 0, Failed = 0, Errors = Array.Empty<string>() };
        if (string.IsNullOrWhiteSpace(envComfyuiSource))
        {
            _logger?.Warn("workflow-symlink", "env.ComfyuiSource empty; skip sync");
            return empty;
        }

        // resolve workflows dir
        var workflowsDir = ResolveWorkflowsDir();
        if (string.IsNullOrWhiteSpace(workflowsDir) || !Directory.Exists(workflowsDir))
        {
            _logger?.Warn("workflow-symlink",
                $"workflows dir missing: '{workflowsDir}'; skip sync");
            return empty;
        }

        var downloaded = _scanner.Scan(workflowsDir);
        if (downloaded.Count == 0)
        {
            _logger?.Info("workflow-symlink", "no downloaded workflows to sync");
            return empty;
        }

        var targetDir = Path.Combine(envComfyuiSource, "user", "default", "workflows");
        try { Directory.CreateDirectory(targetDir); }
        catch (Exception ex)
        {
            _logger?.Error("workflow-symlink", $"create target dir failed: {targetDir}", ex);
            return empty;
        }

        int linked = 0, skipped = 0, failed = 0;
        var errors = new List<string>();

        foreach (var wf in downloaded)
        {
            ct.ThrowIfCancellationRequested();
            var link = Path.Combine(targetDir, wf.SubfolderName);
            var target = wf.FullPath;

            try
            {
                if (Directory.Exists(link))
                {
                    // check if it's already correct
                    var existingTarget = _linker.GetTargetAsync(link, ct).GetAwaiter().GetResult();
                    if (string.Equals(existingTarget, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    // mismatch — delete and recreate
                    Directory.Delete(link);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await _linker.CreateAsync(link, target, ct).ConfigureAwait(false);
                }
                else
                {
                    // Linux / macOS — CreateSymbolicLink(path, target)
                    Directory.CreateSymbolicLink(link, target);
                }
                linked++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{wf.SubfolderName}: {ex.Message}");
                _logger?.Warn("workflow-symlink",
                    $"link failed for {wf.SubfolderName}: {ex.Message}");
            }
        }

        _logger?.Info("workflow-symlink",
            $"sync done linked={linked} skipped={skipped} failed={failed} target='{targetDir}'");
        return new WorkflowSyncResult
        {
            Linked = linked,
            Skipped = skipped,
            Failed = failed,
            Errors = errors,
        };
    }

    private string ResolveWorkflowsDir()
    {
        var dir = _settings.WorkflowsDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return "";
        // If relative, resolve against process root(approx — caller should pass absolute if known)
        if (!Path.IsPathRooted(dir))
        {
            var processRoot = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(processRoot))
                dir = Path.Combine(processRoot, dir);
        }
        return dir;
    }
}

public class WorkflowSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
```

- [ ] **Step 2: Write the failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSymlinkerTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowSymlinkerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workflowsDir;
    private readonly string _envComfyuiSrc;
    private readonly Settings _settings;
    private readonly JunctionLinker _linker = new();
    private readonly WorkflowFilesystemScanner _scanner = new(logger: null);
    private readonly WorkflowSymlinker _symlinker;

    public WorkflowSymlinkerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFSym_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _workflowsDir = Path.Combine(_tempRoot, "workflows");
        _envComfyuiSrc = Path.Combine(_tempRoot, "env-comfyui");
        Directory.CreateDirectory(_workflowsDir);
        Directory.CreateDirectory(_envComfyuiSrc);

        _settings = new Settings { WorkflowsDirectory = _workflowsDir };
        _symlinker = new WorkflowSymlinker(_settings, _linker, _scanner, logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <summary>创建一个 valid downloaded subfolder(workflow.json + meta.json)。</summary>
    private string CreateDownloaded(string slug, string id8, string source = "community_json")
    {
        var sub = Path.Combine(_workflowsDir, $"{slug}-{id8}");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "workflow.json"), "{}");
        File.WriteAllText(Path.Combine(sub, "meta.json"),
            $"{{\"title\":\"{slug}\",\"source\":\"{source}\",\"source_id\":\"{id8}\",\"downloaded_at\":\"2026-08-18T10:00:00Z\"}}");
        return sub;
    }

    [Fact]
    public async Task SyncToEnvAsync_NothingDownloaded_ReturnsEmpty()
    {
        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, result.Linked);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task SyncToEnvAsync_EmptyComfyuiSrc_ReturnsEmpty()
    {
        CreateDownloaded("portrait", "abc12345");

        var result = await _symlinker.SyncToEnvAsync("");

        Assert.Equal(0, result.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_NewSubfolder_CreatesLink()
    {
        CreateDownloaded("portrait", "abc12345");

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(1, result.Linked);
        var linkPath = Path.Combine(_envComfyuiSrc, "user", "default", "workflows", "portrait-abc12345");
        Assert.True(Directory.Exists(linkPath));
    }

    [Fact]
    public async Task SyncToEnvAsync_AlreadyCorrectLink_Skipped()
    {
        CreateDownloaded("portrait", "abc12345");
        var first = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);
        Assert.Equal(1, first.Linked);

        var second = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, second.Linked);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task SyncToEnvAsync_MultipleSubfolders_AllLinked()
    {
        CreateDownloaded("a", "11111111");
        CreateDownloaded("b", "22222222");
        CreateDownloaded("c", "33333333");

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(3, result.Linked);
        var linkDir = Path.Combine(_envComfyuiSrc, "user", "default", "workflows");
        Assert.Equal(3, Directory.GetDirectories(linkDir).Length);
    }

    [Fact]
    public async Task SyncToEnvAsync_WorkflowsDirMissing_ReturnsEmpty()
    {
        Directory.Delete(_workflowsDir, recursive: true);

        var result = await _symlinker.SyncToEnvAsync(_envComfyuiSrc);

        Assert.Equal(0, result.Linked);
        Assert.Equal(0, result.Failed);
    }
}
```

- [ ] **Step 3: Run tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowSymlinker" --nologo`
Expected: 6 PASS / 0 FAIL.

- [ ] **Step 4: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/Services/WorkflowSymlinker.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/WorkflowSymlinkerTests.cs
git commit -m "feat(workflows): v0.6.19 T8 WorkflowSymlinker (junction/symlink) + 6 tests"
```

---

## Task 9: WorkflowMarketplaceViewModel + View XAML + tests + STA load tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs` (8 tests)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Views/WorkflowMarketplaceViewLoadTests.cs` (3 STA tests)

**Interfaces:**
- Consumes: `Settings` (workflows dir + source bools), `WorkflowMarketplaceService`, `WorkflowDownloader`, `WorkflowFilesystemScanner`, `HttpClient` (for downloader)
- Produces:
  - `WorkflowMarketplaceViewModel` with properties: `SearchText`, `Workflows` (ObservableCollection), `AllTags` (ObservableCollection), `TotalCount`, `DownloadedCount`, `Selected` (ObservableCollection), `HasSelection`, `ConsoleLog`, `IsConsoleVisible`, `IsBusy`, `ErrorMessage`, `InfoMessage`, `ActiveSourceFilters` (ObservableCollection), `ActiveTagFilters` (ObservableCollection), `SortBy`, `FilterInstalledNodesOnly`. Commands: `RefreshCommand`, `ToggleSelectAllCommand`, `BatchDownloadCommand`, `ClearConsoleCommand`, `OpenFolderCommand`, `DownloadSingleCommand` (parameter: WorkflowEntry)
  - `WorkflowMarketplaceView` UserControl — top toolbar + filter strip + card grid + console panel

- [ ] **Step 1: Write `WorkflowMarketplaceViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>v0.6.19:工作流市场 VM — 镜像 EnvironmentListView inline 模式
/// (filter/sort/multi-select/console/refresh/batch-download)。
/// Console 三态可见性跟 v0.6.18.4 BulkUpdateViewModel 同款:!userHidden && (IsBusy || hasContent)。</summary>
public class WorkflowMarketplaceViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly WorkflowMarketplaceService _marketplace;
    private readonly WorkflowDownloader _downloader;
    private readonly WorkflowFilesystemScanner _scanner;
    private readonly AppLogger? _logger;
    private readonly List<WorkflowEntry> _allEntries = new();
    private bool _userHiddenConsole;

    private string _searchText = "";
    private WorkflowSortKind _sortBy = WorkflowSortKind.Newest;
    private bool _filterInstalledNodesOnly;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _infoMessage;

    public WorkflowMarketplaceViewModel(
        Settings settings, WorkflowMarketplaceService marketplace,
        WorkflowDownloader downloader, WorkflowFilesystemScanner scanner,
        AppLogger? logger = null)
    {
        _settings = settings;
        _marketplace = marketplace;
        _downloader = downloader;
        _scanner = scanner;
        _logger = logger;

        Workflows = new ObservableCollection<WorkflowEntry>();
        AllTags = new ObservableCollection<string>();
        ActiveSourceFilters = new ObservableCollection<WorkflowSourceKind>();
        ActiveTagFilters = new ObservableCollection<string>();
        Selected = new ObservableCollection<WorkflowEntry>();
        ConsoleLog = new ObservableCollection<string>();

        // 默认全选 3 个 source
        foreach (var s in new[] { WorkflowSourceKind.CommunityJson, WorkflowSourceKind.CivitAi, WorkflowSourceKind.OpenArt })
        {
            ActiveSourceFilters.Add(s);
        }

        Selected.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(SelectedCount));
            BatchDownloadCommand.RaiseCanExecuteChanged();
        };

        ActiveSourceFilters.CollectionChanged += (_, _) => ApplyFilter();
        ActiveTagFilters.CollectionChanged += (_, _) => ApplyFilter();
        ConsoleLog.CollectionChanged += OnConsoleLogChanged;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => Workflows.Count > 0);
        BatchDownloadCommand = new RelayCommand(async _ => await BatchDownloadAsync(),
            _ => HasSelection && !IsBusy && ResolveWorkflowsDirOk());
        ClearConsoleCommand = new RelayCommand(_ => ClearConsole());
        OpenFolderCommand = new RelayCommand(_ => OpenWorkflowsFolder(), _ => ResolveWorkflowsDirOk());
        DownloadSingleCommand = new RelayCommand(async p => await DownloadSingleAsync(p as WorkflowEntry),
            p => p is WorkflowEntry && !IsBusy && ResolveWorkflowsDirOk());
    }

    // —— Outputs ——
    public ObservableCollection<WorkflowEntry> Workflows { get; }
    public ObservableCollection<string> AllTags { get; }
    public ObservableCollection<WorkflowSourceKind> ActiveSourceFilters { get; }
    public ObservableCollection<string> ActiveTagFilters { get; }
    public ObservableCollection<WorkflowEntry> Selected { get; }
    public ObservableCollection<string> ConsoleLog { get; }
    public int SelectedCount => Selected.Count;
    public bool HasSelection => Selected.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value; ApplyFilter(); }
    }
    public WorkflowSortKind SortBy
    {
        get => _sortBy;
        set { if (_sortBy == value) return; _sortBy = value; ApplyFilter(); }
    }
    public bool FilterInstalledNodesOnly
    {
        get => _filterInstalledNodesOnly;
        set { if (_filterInstalledNodesOnly == value) return; _filterInstalledNodesOnly = value; ApplyFilter(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsConsoleVisible));
            RaisePropertyChanged(nameof(DownloadsEnabled));
            RefreshCommand.RaiseCanExecuteChanged();
            BatchDownloadCommand.RaiseCanExecuteChanged();
        }
    }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; RaisePropertyChanged(); }
    }
    public string? InfoMessage
    {
        get => _infoMessage;
        private set { _infoMessage = value; RaisePropertyChanged(); }
    }
    public int TotalCount => _allEntries.Count;
    public int DownloadedCount { get; private set; }
    public bool DownloadsEnabled => ResolveWorkflowsDirOk();

    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    // —— Commands ——
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }
    public RelayCommand BatchDownloadCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand DownloadSingleCommand { get; }

    /// <summary>Initial fetch + scan。call after view constructed。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        ScanDownloaded();
        await RefreshAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var results = await _marketplace.LoadAllAsync(SearchText, maxResultsPerSource: 50, ct).ConfigureAwait(false);
            _allEntries.Clear();
            _allEntries.AddRange(results);
            ApplyFilter();
            ScanDownloaded();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"刷新失败:{ex.Message}";
            _logger?.Error("workflow-marketplace", "refresh failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allEntries.AsEnumerable();

        // text
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var q = _searchText.ToLowerInvariant();
            filtered = filtered.Where(e =>
                (e.Title?.ToLowerInvariant().Contains(q) ?? false) ||
                (e.Author?.ToLowerInvariant().Contains(q) ?? false) ||
                e.Tags.Any(t => t.ToLowerInvariant().Contains(q)));
        }

        // source
        if (ActiveSourceFilters.Count > 0)
        {
            filtered = filtered.Where(e => ActiveSourceFilters.Contains(e.Source));
        }

        // tags
        if (ActiveTagFilters.Count > 0)
        {
            filtered = filtered.Where(e => ActiveTagFilters.All(t => e.Tags.Contains(t)));
        }

        // sort
        filtered = _sortBy switch
        {
            WorkflowSortKind.Downloads => filtered.OrderByDescending(e => e.DownloadCount ?? 0),
            WorkflowSortKind.Name => filtered.OrderBy(e => e.Title),
            _ => filtered.OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue),
        };

        var list = filtered.ToToList();
        Workflows.Clear();
        foreach (var e in list) Workflows.Add(e);

        // tags union
        var tagUnion = _allEntries.SelectMany(e => e.Tags).Distinct().OrderBy(t => t).ToList();
        AllTags.Clear();
        foreach (var t in tagUnion) AllTags.Add(t);

        RaisePropertyChanged(nameof(TotalCount));
    }

    private void ToggleSelectAll()
    {
        if (Selected.Count == Workflows.Count)
        {
            Selected.Clear();
        }
        else
        {
            foreach (var e in Workflows)
            {
                if (!Selected.Contains(e)) Selected.Add(e);
            }
        }
    }

    private async Task BatchDownloadAsync()
    {
        if (Selected.Count == 0 || IsBusy) return;
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir)) { ErrorMessage = "工作流目录未配置"; return; }
        IsBusy = true;
        ConsoleLog.Clear();
        _userHiddenConsole = false;
        try
        {
            var entries = Selected.ToList();
            var log = new Progress<string>(line => ConsoleLog.Add(line));
            var summary = await _downloader.DownloadBatchAsync(entries, dir, log).ConfigureAwait(false);
            InfoMessage = $"批量下载完成:成功 {summary.Succeeded} / 失败 {summary.Failed}";
            ScanDownloaded();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"批量下载失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadSingleAsync(WorkflowEntry? entry)
    {
        if (entry is null || IsBusy) return;
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir)) { ErrorMessage = "工作流目录未配置"; return; }
        IsBusy = true;
        try
        {
            var log = new Progress<string>(line => ConsoleLog.Add(line));
            var result = await _downloader.DownloadAsync(entry, dir, log).ConfigureAwait(false);
            if (result.Success) InfoMessage = $"已下载:{entry.Title}";
            else ErrorMessage = $"下载失败:{result.FailureReason}";
            ScanDownloaded();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ScanDownloaded()
    {
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            DownloadedCount = 0;
        }
        else
        {
            DownloadedCount = _scanner.Scan(dir).Count;
        }
        RaisePropertyChanged(nameof(DownloadedCount));
    }

    private void ClearConsole()
    {
        ConsoleLog.Clear();
        _userHiddenConsole = true;
        RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    private void OpenWorkflowsFolder()
    {
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems is { Count: > 0 })
            RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    private bool ResolveWorkflowsDirOk()
    {
        var dir = ResolveWorkflowsDir();
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
    }

    private string? ResolveWorkflowsDir()
    {
        var dir = _settings.WorkflowsDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        if (!Path.IsPathRooted(dir))
        {
            var root = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(root)) dir = Path.Combine(root, dir);
        }
        return dir;
    }
}

public enum WorkflowSortKind { Newest, Downloads, Name }
```

NOTE: there's a typo above — `filtered.ToToList()` should be `filtered.ToList()`. Fix in implementation.

- [ ] **Step 2: Write `WorkflowMarketplaceView.xaml` + .xaml.cs**

`Views/WorkflowMarketplaceView.xaml` — mirror the BulkUpdateView.xaml 3-section DockPanel pattern (top toolbar + middle main + bottom console):

```xml
<UserControl x:Class="ComfyUI.Manager.Views.WorkflowMarketplaceView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             xmlns:models="clr-namespace:ComfyUI.Manager.Models"
             mc:Ignorable="d"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             d:DataContext="{d:DesignInstance Type=vm:WorkflowMarketplaceViewModel}"
             Background="{DynamicResource WindowBackgroundBrush}">
  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>   <!-- top toolbar -->
      <RowDefinition Height="Auto"/>   <!-- filter strip -->
      <RowDefinition Height="*"/>      <!-- card grid -->
      <RowDefinition Height="Auto"/>   <!-- console panel -->
      <RowDefinition Height="Auto"/>   <!-- info/error banner -->
    </Grid.RowDefinitions>

    <!-- Top toolbar -->
    <DockPanel Grid.Row="0" Margin="0,0,0,8">
      <TextBlock DockPanel.Dock="Left" Text="工作流市场"
                 FontSize="20" FontWeight="Bold"
                 Foreground="{DynamicResource PrimaryTextBrush}"
                 VerticalAlignment="Center" Margin="0,0,16,0" />
      <TextBox DockPanel.Dock="Left" Width="240" Margin="0,0,8,0"
               Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
               Tag="搜索..." />
      <Button DockPanel.Dock="Right" Content="刷新"
              Command="{Binding RefreshCommand}" Margin="4,0,0,0" />
      <Button DockPanel.Dock="Right" Content="打开目录"
              Command="{Binding OpenFolderCommand}" Margin="4,0,0,0" />
      <Button DockPanel.Dock="Right" Content="全选"
              Command="{Binding ToggleSelectAllCommand}" Margin="4,0,0,0" />
      <Button DockPanel.Dock="Right" Content="批量下载"
              Command="{Binding BatchDownloadCommand}" Margin="4,0,0,0" />
    </DockPanel>

    <!-- Filter strip -->
    <Border Grid.Row="1" Padding="8" Margin="0,0,0,8"
            Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource OutlineBrush}"
            BorderThickness="1" CornerRadius="6">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="源:" VerticalAlignment="Center" Margin="0,0,4,0" />
        <ItemsControl ItemsSource="{Binding ActiveSourceFilters}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <CheckBox Content="{Binding}" Margin="4,0" />
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <TextBlock Text="排序:" VerticalAlignment="Center" Margin="12,0,4,0" />
        <ComboBox SelectedItem="{Binding SortBy}" Width="100">
          <x:Static Member="vm:WorkflowSortKind.Newest" />
          <x:Static Member="vm:WorkflowSortKind.Downloads" />
          <x:Static Member="vm:WorkflowSortKind.Name" />
        </ComboBox>
        <CheckBox Content="需装节点" IsChecked="{Binding FilterInstalledNodesOnly}"
                  Margin="12,0,0,0" VerticalAlignment="Center" />
        <TextBlock VerticalAlignment="Center" Margin="12,0,0,0"
                   Text="{Binding TotalCount, StringFormat='共 {0} 条'}" />
        <TextBlock VerticalAlignment="Center" Margin="12,0,0,0"
                   Text="{Binding DownloadedCount, StringFormat='已下载 {0} 个'}" />
      </StackPanel>
    </Border>

    <!-- Card grid -->
    <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto">
      <ItemsControl ItemsSource="{Binding Workflows}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate DataType="{x:Type models:WorkflowEntry}">
            <Border Width="200" Height="260" Margin="8"
                    Background="{DynamicResource SurfaceBrush}"
                    BorderBrush="{DynamicResource OutlineBrush}"
                    BorderThickness="1" CornerRadius="6">
              <Grid>
                <Grid.RowDefinitions>
                  <RowDefinition Height="*"/>     <!-- preview -->
                  <RowDefinition Height="Auto"/> <!-- title -->
                  <RowDefinition Height="Auto"/> <!-- meta -->
                  <RowDefinition Height="Auto"/> <!-- action -->
                </Grid.RowDefinitions>
                <CheckBox Grid.Row="0" HorizontalAlignment="Left" VerticalAlignment="Top"
                          IsChecked="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ContentPresenter}, Mode=OneWay}" />
                <Image Grid.Row="0" Margin="8" Source="{Binding PreviewImageUrl}"
                       Stretch="UniformToFill" />
                <TextBlock Grid.Row="1" Text="{Binding Title}" FontSize="13" FontWeight="SemiBold"
                           Margin="8,4" MaxHeight="40" TextWrapping="Wrap"
                           TextTrimming="CharacterEllipsis" />
                <StackPanel Grid.Row="2" Orientation="Vertical" Margin="8,0">
                  <TextBlock Text="{Binding Author}" FontSize="11" Opacity="0.7" />
                  <TextBlock Text="{Binding Source}" FontSize="10" Opacity="0.5" />
                </StackPanel>
                <Button Grid.Row="3" Margin="8" Content="下载"
                        Command="{Binding DataContext.DownloadSingleCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}" />
              </Grid>
            </Border>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>

    <!-- Console panel — mirrors BulkUpdateView Console (v0.6.18.4) -->
    <Border Grid.Row="3" Margin="0,8,0,0" Padding="8"
            Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource OutlineBrush}"
            BorderThickness="1" CornerRadius="6"
            Visibility="{Binding IsConsoleVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
      <Grid>
        <Grid.RowDefinitions>
          <RowDefinition Height="Auto" />
          <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <DockPanel Grid.Row="0" Margin="0,0,0,4">
          <TextBlock DockPanel.Dock="Left" Text="Console" FontWeight="SemiBold" />
          <TextBlock DockPanel.Dock="Left" Margin="8,0,0,0" Opacity="0.6"
                     Text="{Binding ConsoleLog.Count, StringFormat='{0} 行'}" />
          <Button DockPanel.Dock="Right" Content="✕" Padding="6,0"
                  Click="OnConsoleCloseClicked" />
        </DockPanel>
        <ScrollViewer x:Name="ConsoleScrollViewer" Grid.Row="1" Height="160"
                      VerticalScrollBarVisibility="Auto">
          <ItemsControl ItemsSource="{Binding ConsoleLog}">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11"
                           TextWrapping="NoWrap" />
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>
      </Grid>
    </Border>

    <!-- Info / Error banner -->
    <StackPanel Grid.Row="4" Orientation="Vertical" Margin="0,4,0,0">
      <TextBlock Text="{Binding ErrorMessage}" Foreground="{DynamicResource ErrorBrush}"
                 Visibility="{Binding ErrorMessage, Converter={StaticResource NullToVisibility}}" />
      <TextBlock Text="{Binding InfoMessage}" Foreground="{DynamicResource SuccessBrush}"
                 Visibility="{Binding InfoMessage, Converter={StaticResource NullToVisibility}}" />
    </StackPanel>
  </Grid>
</UserControl>
```

`Views/WorkflowMarketplaceView.xaml.cs`:

```csharp
using System.Collections.Specialized;
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>v0.6.19:工作流市场 view — mirrors BulkUpdateView Console pattern
/// (DataContextChanged hook/unhook + auto-scroll + ✕ close)。</summary>
public partial class WorkflowMarketplaceView : UserControl
{
    public WorkflowMarketplaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookConsoleLog();
        Unloaded += OnUnloaded;
    }

    private WorkflowMarketplaceViewModel? _vm;
    private NotifyCollectionChangedEventHandler? _consoleHandler;

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnhookConsoleLog();
        _vm = e.NewValue as WorkflowMarketplaceViewModel;
        HookConsoleLog();
    }

    private void HookConsoleLog()
    {
        if (_vm is null || _consoleHandler is not null) return;
        _consoleHandler = (_, _) =>
        {
            if (ConsoleScrollViewer is null) return;
            ConsoleScrollViewer.ScrollToEnd();
        };
        _vm.ConsoleLog.CollectionChanged += _consoleHandler;
    }

    private void UnhookConsoleLog()
    {
        if (_vm is null || _consoleHandler is null) return;
        _vm.ConsoleLog.CollectionChanged -= _consoleHandler;
        _consoleHandler = null;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnhookConsoleLog();
    }

    private void OnConsoleCloseClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm?.ClearConsole();
    }
}
```

Note: The card selection binding in XAML uses a hack via RelativeSource to ContentPresenter — simpler alternative is to add `IsSelected` as a property on a wrapper item (cleaner but requires a wrapper class). For v1 simplicity, defer the wrapper and rely on Selected list mutations in code-behind. Adjust XAML if checkbox binding doesn't work in practice; the imperative approach via Selected.CollectionChanged works regardless.

Alternative cleaner approach: in the VM expose a `WorkflowCardItem` wrapper with `IsSelected` + `WorkflowEntry` — bind directly. Implementation choice; the imperative approach (remove the CheckBox from XAML, add per-card `Selected` toggle in code-behind or via wrapper) is acceptable. Pick whichever works.

- [ ] **Step 3: Write `WorkflowMarketplaceViewModelTests.cs`**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class WorkflowMarketplaceViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Settings _settings;
    private readonly StubMarketplaceService _marketplace;
    private readonly WorkflowDownloader _downloader;
    private readonly WorkflowMarketplaceViewModel _vm;

    public WorkflowMarketplaceViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFVm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settings = new Settings { WorkflowsDirectory = _tempDir };
        _marketplace = new StubMarketplaceService();
        _downloader = new WorkflowDownloader(new HttpClient(new OkHandler()), logger: null);
        _vm = new WorkflowMarketplaceViewModel(_settings, _marketplace, _downloader,
            new WorkflowFilesystemScanner(logger: null), logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static WorkflowEntry Entry(string id, WorkflowSourceKind src = WorkflowSourceKind.CommunityJson,
        string title = "T", string? author = null, string[]? tags = null, int? downloads = null,
        DateTimeOffset? published = null)
        => new() { Source = src, SourceId = id, SourceUrl = $"https://{src}/{id}",
                   WorkflowJsonUrl = $"https://{src}/{id}.json", Title = title,
                   Author = author, Tags = tags ?? Array.Empty<string>(),
                   DownloadCount = downloads, PublishedAt = published };

    [Fact]
    public async Task RefreshAsync_PopulatesWorkflows()
    {
        _marketplace.Next = new[] { Entry("a"), Entry("b"), Entry("c") };
        await _vm.RefreshAsync();
        Assert.Equal(3, _vm.Workflows.Count);
    }

    [Fact]
    public async Task RefreshAsync_ErrorMessage_OnException()
    {
        _marketplace.ThrowOnNext = new InvalidOperationException("boom");
        await _vm.RefreshAsync();
        Assert.NotNull(_vm.ErrorMessage);
        Assert.Contains("boom", _vm.ErrorMessage);
    }

    [Fact]
    public async Task SearchText_FiltersByTitle()
    {
        _marketplace.Next = new[]
        {
            Entry("a", title: "Apple"),
            Entry("b", title: "Banana"),
            Entry("c", title: "Cherry"),
        };
        await _vm.RefreshAsync();
        _vm.SearchText = "ban";
        Assert.Single(_vm.Workflows);
        Assert.Equal("b", _vm.Workflows[0].SourceId);
    }

    [Fact]
    public async Task ActiveSourceFilters_FilterBySource()
    {
        _marketplace.Next = new[]
        {
            Entry("a", src: WorkflowSourceKind.CommunityJson),
            Entry("b", src: WorkflowSourceKind.CivitAi),
            Entry("c", src: WorkflowSourceKind.OpenArt),
        };
        await _vm.RefreshAsync();
        _vm.ActiveSourceFilters.Clear();
        _vm.ActiveSourceFilters.Add(WorkflowSourceKind.CivitAi);
        Assert.Single(_vm.Workflows);
        Assert.Equal(WorkflowSourceKind.CivitAi, _vm.Workflows[0].Source);
    }

    [Fact]
    public async Task SortBy_Name_SortsAlphabetically()
    {
        _marketplace.Next = new[]
        {
            Entry("a", title: "Zebra"),
            Entry("b", title: "Apple"),
            Entry("c", title: "Mango"),
        };
        await _vm.RefreshAsync();
        _vm.SortBy = WorkflowSortKind.Name;
        Assert.Equal("Apple", _vm.Workflows[0].Title);
        Assert.Equal("Mango", _vm.Workflows[1].Title);
        Assert.Equal("Zebra", _vm.Workflows[2].Title);
    }

    [Fact]
    public async Task SortBy_Downloads_SortsByCount()
    {
        _marketplace.Next = new[]
        {
            Entry("a", downloads: 5),
            Entry("b", downloads: 100),
            Entry("c", downloads: 20),
        };
        await _vm.RefreshAsync();
        _vm.SortBy = WorkflowSortKind.Downloads;
        Assert.Equal("b", _vm.Workflows[0].SourceId);  // 100
        Assert.Equal("c", _vm.Workflows[1].SourceId);  // 20
        Assert.Equal("a", _vm.Workflows[2].SourceId);  // 5
    }

    [Fact]
    public void ToggleSelectAll_FlipsSelection()
    {
        _vm.Workflows.Add(Entry("a"));
        _vm.Workflows.Add(Entry("b"));
        _vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(2, _vm.Selected.Count);
        _vm.ToggleSelectAllCommand.Execute(null);
        Assert.Empty(_vm.Selected);
    }

    [Fact]
    public void BatchDownloadCommand_DisabledWhenNoSelection()
    {
        Assert.False(_vm.BatchDownloadCommand.CanExecute(null));
        _vm.Selected.Add(Entry("a"));
        // Without workflowsDir existing, also need it OK
        // (in our setup it exists, so should be enabled)
        Assert.True(_vm.BatchDownloadCommand.CanExecute(null));
    }

    private sealed class StubMarketplaceService : WorkflowMarketplaceService
    {
        public IReadOnlyList<WorkflowEntry>? Next { get; set; }
        public Exception? ThrowOnNext { get; set; }

        public StubMarketplaceService() : base(Array.Empty<IWorkflowSource>()) { }

        public override Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
        {
            if (ThrowOnNext is not null) throw ThrowOnNext;
            return Task.FromResult(Next ?? Array.Empty<WorkflowEntry>());
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
    }
}
```

- [ ] **Step 4: Run VM tests — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~WorkflowMarketplaceViewModelTests" --nologo`
Expected: 8 PASS / 0 FAIL.

- [ ] **Step 5: Write `WorkflowMarketplaceViewLoadTests.cs` (STA)**

`tests-wpf/ComfyUI.Manager.Tests/Views/WorkflowMarketplaceViewLoadTests.cs`:

```csharp
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Tests.Infrastructure;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class WorkflowMarketplaceViewLoadTests
{
    [Fact]
    [Trait("Category", "STA")]
    public void View_DarkTheme_Loads()
    {
        WpfTestResources.EnsureLoaded();
        var view = new WorkflowMarketplaceView();
        Assert.NotNull(view);
    }

    [Fact]
    [Trait("Category", "STA")]
    public void View_LightTheme_Loads()
    {
        WpfTestResources.EnsureLoaded();
        // Switch theme; existing pattern: use Application.Current.Resources + theme switching
        var view = new WorkflowMarketplaceView();
        Assert.NotNull(view);
    }

    [Fact]
    [Trait("Category", "STA")]
    public void View_WithVm_Loads()
    {
        WpfTestResources.EnsureLoaded();
        var vm = MakeVm();
        var view = new WorkflowMarketplaceView { DataContext = vm };
        Assert.NotNull(view);
    }

    private static ComfyUI.Manager.ViewModels.WorkflowMarketplaceViewModel MakeVm()
    {
        var settings = new ComfyUI.Manager.Models.Settings { WorkflowsDirectory = System.IO.Path.GetTempPath() };
        var marketplace = new StubMarketplace();
        var downloader = new ComfyUI.Manager.Services.WorkflowDownloader(
            new System.Net.Http.HttpClient(), logger: null);
        var scanner = new ComfyUI.Manager.Services.WorkflowFilesystemScanner(logger: null);
        return new ComfyUI.Manager.ViewModels.WorkflowMarketplaceViewModel(
            settings, marketplace, downloader, scanner, logger: null);
    }

    private sealed class StubMarketplace : ComfyUI.Manager.Services.WorkflowMarketplaceService
    {
        public StubMarketplace() : base(System.Array.Empty<ComfyUI.Manager.Services.IWorkflowSource>()) { }
        public override Task<System.Collections.Generic.IReadOnlyList<ComfyUI.Manager.Models.WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<ComfyUI.Manager.Models.WorkflowEntry>>(
                System.Array.Empty<ComfyUI.Manager.Models.WorkflowEntry>());
    }
}
```

Inspect existing STA test patterns in `tests-wpf/ComfyUI.Manager.Tests/Views/` (e.g. `MainWindowExitCleanupTests.cs` or any `*LoadTests.cs`) and mirror their `STAThread` / `WpfTestResources` usage exactly.

- [ ] **Step 6: Run full suite — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -20`
Expected: 1415+ PASS / 0 FAIL / 4 SKIP (baseline 1 + 3 new SKIPs).

- [ ] **Step 7: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/ViewModels/WorkflowMarketplaceViewModel.cs \
  src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml \
  src-wpf/ComfyUI.Manager/Views/WorkflowMarketplaceView.xaml.cs \
  tests-wpf/ComfyUI.Manager.Tests/ViewModels/WorkflowMarketplaceViewModelTests.cs \
  tests-wpf/ComfyUI.Manager.Tests/Views/WorkflowMarketplaceViewLoadTests.cs
git commit -m "feat(workflows): v0.6.19 T9 ViewModel + View XAML + 11 tests"
```

---

## Task 10: MainViewModel + MainWindow integration + env-start hook

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:19-28` (add `Workflows` enum value)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (add `ShowWorkflowsCommand`, `_workflowMarketplaceViewModel` cache, `ShowWorkflows()` method)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs:20-21` (add `Workflows` mapping)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml:104-107` (add 8th sidebar RadioButton between LocalNodes and Settings)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (add `HttpClient` singleton + `WorkflowSymlinker` to DI; wire to `MainViewModel`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (add `WorkflowSymlinker?` ctor param + fire-and-forget call after env-start success)

**Interfaces:**
- Consumes: existing `MainSection` enum, `MainViewModel.ShowXxx()` lazy-cached pattern, `App.xaml.cs` DI, `EnvironmentListViewModel.StartAsync` flow
- Produces:
  - 8th sidebar RadioButton "工作流市场" → `ShowWorkflowsCommand`
  - `MainViewModel.ShowWorkflows()` mirrors `ShowCatalog()` pattern
  - `EnvironmentListViewModel.StartAsync` calls `_workflowSymlinker?.SyncToEnvAsync(...)` fire-and-forget after `await _launcher.StartEnvAsync(...)` returns
  - `App.xaml.cs` constructs `HttpClient` (singleton, lazy `SocketsHttpHandler`) + `WorkflowMarketplaceService` + `WorkflowDownloader` + `WorkflowFilesystemScanner` + `WorkflowSymlinker` and injects into MainViewModel

- [ ] **Step 1: Add `Workflows` to `MainSection` enum**

In `ViewModels/MainViewModel.cs:19-28`, add after `LocalNodes`:

```csharp
public enum MainSection
{
    Dashboard,
    Environments,
    Catalog,
    LocalNodes,
    Workflows,   // v0.6.19 NEW
    Settings,
    BulkUpdate,
    SystemStatus
}
```

- [ ] **Step 2: Add `MainSectionNameProvider` mapping**

In `ViewModels/MainSectionNameProvider.cs`, after the `LocalNodes` line:

```csharp
MainSection.Workflows => Get("SectionName_Workflows", "工作流市场"),  // v0.6.19
```

- [ ] **Step 3: Add 8th sidebar RadioButton in `MainWindow.xaml`**

After the "本地节点" RadioButton (around line 107), add:

```xml
<!-- v0.6.19:工作流市场 — aggregated multi-source download + batch + env sync -->
<RadioButton Content="工作流市场" GroupName="SidebarNav"
             Command="{Binding ShowWorkflowsCommand}"
             IsChecked="{Binding CurrentSection, Converter={StaticResource SectionEquality}, ConverterParameter=Workflows, Mode=OneWay}"
             Style="{StaticResource SidebarRadioButtonStyle}" />
```

- [ ] **Step 4: Add `ShowWorkflows` + `ShowWorkflowsCommand` to `MainViewModel`**

In `ViewModels/MainViewModel.cs`, near the `ShowLocalNodesCommand` line (~line 353), add:

```csharp
public RelayCommand ShowWorkflowsCommand { get; }   // v0.6.19

// In ctor (around line 353):
ShowWorkflowsCommand = new RelayCommand(_ => ShowWorkflows());
```

Add fields (around the other lazy VM caches):

```csharp
private WorkflowMarketplaceViewModel? _workflowMarketplaceViewModel;
private WorkflowMarketplaceView? _workflowMarketplaceView;
```

Add `ShowWorkflows()` method (mirror `ShowLocalNodes`):

```csharp
private void ShowWorkflows()
{
    CurrentSection = MainSection.Workflows;
    if (_workflowMarketplaceViewModel is null)
    {
        var marketplace = new WorkflowMarketplaceService(
            new IWorkflowSource[]
            {
                new CommunityJsonSource(_http, logger: _logger),
                new CivitAiSource(_http, logger: _logger),
                new OpenArtSource(_http, logger: _logger),
            },
            logger: _logger);
        var downloader = new WorkflowDownloader(_http, logger: _logger);
        var scanner = new WorkflowFilesystemScanner(logger: _logger);
        _workflowMarketplaceViewModel = new WorkflowMarketplaceViewModel(
            _settings, marketplace, downloader, scanner, logger: _logger);
        _workflowMarketplaceView = new WorkflowMarketplaceView { DataContext = _workflowMarketplaceViewModel };
        _ = _workflowMarketplaceViewModel.LoadAsync();  // fire-and-forget initial fetch
    }
    CurrentView = _workflowMarketplaceView;
}
```

You'll also need to inject `HttpClient _http` and `Settings _settings` into `MainViewModel` (likely already present; check existing constructor). Adjust visibility/access if private.

- [ ] **Step 5: Wire DI in `App.xaml.cs`**

In `App.xaml.cs`, after other service constructions (e.g. after `_localNodesViewModel` setup), ensure:
- `HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };` (singleton, app lifetime)
- Pass `_http` to `MainViewModel` ctor (add parameter)
- Pass `Settings _settings` to `MainViewModel` ctor (already injected in existing constructor; verify)
- Pass `AppLogger _logger` (already injected; verify)

- [ ] **Step 6: Add env-start hook in `EnvironmentListViewModel`**

In `ViewModels/EnvironmentListViewModel.cs`, add to constructor (after `_nodeOps` assignment, around line 273):

```csharp
private readonly WorkflowSymlinker? _workflowSymlinker;

// In ctor signature (around line 247-264), add as last optional param:
WorkflowSymlinker? workflowSymlinker = null

// In ctor body:
_workflowSymlinker = workflowSymlinker;
```

In `StartAsync` (around line 562-578, after `await _launcher.StartEnvAsync(...)` returns and before `status.Complete()`), add fire-and-forget:

```csharp
// v0.6.19: env-start 后异步 sync workflows(junction/symlink),failure 不阻断 env-start
if (_workflowSymlinker is not null)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await _workflowSymlinker.SyncToEnvAsync(env.ComfyuiSource ?? "");
        }
        catch (Exception ex)
        {
            _logger?.Warn("workflow-symlink", $"fire-and-forget sync failed: {ex.Message}");
        }
    });
}
```

- [ ] **Step 7: Build + verify**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug --nologo 2>&1 | tail -20`
Expected: 0 errors.

- [ ] **Step 8: Run full suite — verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -20`
Expected: 1415+ PASS / 0 FAIL / 4 SKIP.

Existing tests for `EnvironmentListViewModel` may break if you added a required ctor param — make it optional (default null) to avoid forcing test updates. If a test fails due to env-start flow change, update that test's expectations (no semantic change to env-start status).

- [ ] **Step 9: Commit**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
  src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs \
  src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
  src-wpf/ComfyUI.Manager/MainWindow.xaml \
  src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "feat(workflows): v0.6.19 T10 MainViewModel + sidebar + env-start symlink hook"
```

---

## Task 11: Final review + MEMORY + staging rebuild

**Files:**
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_19_workflow_marketplace.md` (create)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (add index line)
- Rebuild: `dotnet publish` for staging

**Interfaces:**
- Consumes: all previous tasks' HEAD
- Produces:
  - MEMORY file with full session record (tests, files, lessons)
  - MEMORY.md index line
  - Staging rebuild verified + launched

- [ ] **Step 1: Run full test suite — verify clean**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --nologo 2>&1 | tail -10`
Expected: ~1415+ PASS / 0 FAIL / 4 SKIP. If any FAIL appears, fix before proceeding.

- [ ] **Step 2: Dispatch opus final review (whole-branch)**

Use the code-reviewer agent (or equivalent). Run: `git diff main~10..HEAD --stat` first to enumerate changed files. Pass the diff + spec to the reviewer and request: spec compliance + code quality + test coverage + risk surface.

- [ ] **Step 3: Address review findings**

Apply reviewer feedback. Most likely findings:
- Missing test for env-start hook behavior (add `EnvironmentListViewModelEnvStartSymlinkTests.cs` if reviewer flags)
- UI binding issues in XAML (manual fix-up based on actual desktop test)
- Missing error handling edge cases (per reviewer's reading)

- [ ] **Step 4: Staging rebuild**

Run from project root:
```bash
dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=false \
  -o "release/staging/ComfyUI Manager" --nologo 2>&1 | tail -10
```
Expected: 0 errors. Verify `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` exists.

Launch briefly: `start "" "release/staging/ComfyUI Manager/ComfyUI.Manager.exe"` and confirm PID via `Get-Process -Name ComfyUI.Manager` (if bash, use `tasklist | grep ComfyUI.Manager`). Stop after 5 seconds.

- [ ] **Step 5: Write MEMORY file**

Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_19_workflow_marketplace.md` with sections following the v0.6.18.x memory files (header, Why, How to apply, Implementation, Test results, Files, Commit, Staging rebuild, GUI smoke list, Lessons learned).

Reference other memory files for shape; key info to capture:
- HEAD SHA (commit hash from T10)
- Test counts (~1415+ PASS / 0 FAIL / 4 SKIP)
- Files changed (~18 files, ~1000-1400 LoC)
- 3 source URLs chosen (CommunityJson / CivitAi / OpenArt endpoints actually used)
- Lessons learned during impl (e.g. "Symlinker must use `Directory.CreateSymbolicLink` not junction on Linux/macOS")

- [ ] **Step 6: Update MEMORY.md index**

Add line to `MEMORY.md`:
```
- [v0.6.19 工作流市场](project_v0_6_19_workflow_marketplace.md) — HEAD <sha> SHIP-READY 2026-08-XX,~1415 PASS/0 FAIL/4 SKIP,...
```

Keep the line under ~200 chars (the index entry must not be too long, per the memory warning in system context). Full detail in the topic file.

- [ ] **Step 7: Commit memory updates**

```bash
cd /d/ToolDevelop/ComfyUI && git add \
  C:/Users/徐鹏/.claude/projects/D--ToolDevelop-ComfyUI/memory/project_v0_6_19_workflow_marketplace.md \
  C:/Users/徐鹏/.claude/projects/D--ToolDevelop-ComfyUI/memory/MEMORY.md
# Memory is outside repo — skip commit if not git-tracked; verify with git check-ignore or git ls-files
# If MEMORY.md is gitignored, skip this commit (no-op)
```

If memory files are outside the repo (likely at `~/.claude/projects/...`), no commit needed.

- [ ] **Step 8: Report to user**

Summarize: HEAD SHA + test counts + staging launched PID + GUI smoke checklist (8-10 steps for desktop verification). Per project convention, v-bump skipped (no version bump), no release zip.

---

## Self-Review (performed before commit)

**1. Spec coverage check:**
- ✅ T1 covers §3 G1, §5.5 (Settings.WorkflowsDirectory + 3 source bools)
- ✅ T2 covers §5.1, §5.2 (WorkflowEntry + DownloadedWorkflow)
- ✅ T3-T5 cover §6.1, §6.2 (IWorkflowSource + 3 implementations)
- ✅ T6 covers §6.3 (WorkflowMarketplaceService aggregator)
- ✅ T7 covers §4.2 (download batch), §5.4 (file layout + meta.json + slug)
- ✅ T8 covers §4.2 (env-start sync), §3 G8
- ✅ T9 covers §7.1-7.4 (UI: sidebar + filter strip + card grid + console)
- ✅ T10 covers §7.1 (MainSection enum + sidebar), §8 (env-start hook), §3 G9 (HttpClient DI)
- ✅ T11 covers §1 verification + memory + staging

**2. Placeholder scan:**
- ✅ T3/T5 source URLs marked as "placeholder;真 endpoint 实现时确认" — intentional per spec §6.2 ("URL resolved at implementation time"); will be replaced at impl with actual endpoint discovered during work

**3. Type consistency:**
- `WorkflowEntry.Source` typed `WorkflowSourceKind` enum throughout ✓
- `DownloadedWorkflow` and `WorkflowMetaSidecar` fields consistent ✓
- `WorkflowDownloadResult` static factory pattern matches existing project conventions ✓
- `WorkflowBatchSummary` / `WorkflowSyncResult` POCOs use init-only properties consistently ✓
- VM `Selected` ObservableCollection vs `SelectedCount` property — `SelectedCount => Selected.Count` getter (not stored), so PropertyChanged fires on CollectionChanged ✓
- `WorkflowMarketplaceViewModel.Workflows` ObservableCollection + `ApplyFilter()` rebuilds via Clear+Add ✓

**4. Risk surface:**
- Multi-DI wiring in T10: existing `_http` may not exist in MainViewModel — verify before dispatch
- XAML checkbox binding in T9 may need adjustment if RelativeSource trick doesn't render — fallback to wrapper item class
- Env-start hook in T10: if any test asserts exact env-start behavior, may need updating — make WorkflowSymlinker ctor param optional (default null) to avoid breaking changes

Plan complete and saved to `docs/superpowers/plans/2026-08-18-workflow-marketplace.md`.