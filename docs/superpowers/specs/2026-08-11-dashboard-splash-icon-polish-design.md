# Dashboard Welcome + Splash Progress + Icon Integration — Design Spec

**Date**: 2026-08-11
**Base**: `6ff31105` (v0.6.11+ env-list toggle buttons SHIP-READY, 831/0/1 baseline)
**Scope**: DashboardView Welcome overhaul + Splash multi-stage progress + icon.png/ComfyUI.png asset integration
**User intent (4 original + 1 follow-up requests)**:
1. 欢迎面板(主页)增加 GitHub 相关信息 — Dashboard hero GitHub strip
2. 同时增加针对不同版本增加的 Changelog — Dashboard Changelog card
3. 使用 asset 中的 ComfyUI 文件作为 Splash,然后三秒钟进度加载后消失 — Splash asset swap + multi-stage progress
4. 使用 asset 目录中的 Icon 图标作为应用程序标志,另外手动风格图片中各个 Logo 应用场景 — exe icon + sidebar/hero icon
5. 首页提供下载路径和地址 — Dashboard 📥 下载地址 section
6. 在加载的 Splash 界面设置加载动画,加载到 100% — Splash progress bars 0→100%
7. 背景去掉,背景就是一张图片 — Splash bg = ComfyUI.png, no Border fallback panel

**Approach**: **C. Heavy** (Dashboard restructure + Splash rewrite + icon integration + ChangelogParser + GitHubReleaseService live fetch + click-to-copy 下载路径 + BrowserLauncher 复用)

**Reuse**:
- v0.6.8 Splash pattern (`SplashWindow` + `SplashViewModel` + TimerFactory seam + 3s min + 800ms fade)
- v0.6.11+ T4 (v0.6.11 SHIP-READY) `DashboardView.LatestRelease` GitHub fetch (extend, don't replace)
- v0.6.10 `BrowserLauncher` (Chrome fallback → default browser)
- v0.6.9.2 G4 rule (`Setter Property=... Value="{StaticResource ...}"` banned; DynamicResource only)
- v0.6.5.13 `AppLogger` (4 startup stages INFO log)

---

## Goals

- **G1**: Dashboard (left sidebar "主页") becomes a one-stop landing view: GitHub info (stars + release count + latest version) + per-version Changelog + 本地 staging 路径 + GitHub release URL
- **G2**: Changelog sourced from **live GitHub Releases API** (cached 24h, offline fallback) + parsed local `CHANGELOG.md` for detailed sections
- **G3**: Splash uses `asset/ComfyUI.png` as **the only background** (no Border fallback); shows **4-stage progress 0→100%** (init/db/theme/ready)
- **G4**: App icon uses `asset/icon.png` (1.2 MB master) → bake to `.ico` for EXE + small `icon-light.png`/`icon-dark.png` for sidebar/hero (theme-aware via DynamicResource)
- **G5**: 📥 下载地址 click-to-copy (clipboard) + click-to-open (explorer /select) + click-to-browse (BrowserLauncher)

## Non-Goals

- **N1**: No new GitHub live-fetch features beyond releases (no issues/PRs — defer)
- **N2**: No live download progress / auto-update from GitHub (out of scope; only displays static paths/URLs)
- **N3**: No multi-language UI changes (Chinese labels only; existing i18n rules preserved)
- **N4**: No changelog editor inside app (CHANGELOG.md is edited in repo, app only reads)
- **N5**: No splash skip-on-second-launch (always show 3s splash, current behavior preserved)

---

## Architecture & Components

### New services (4)

| Service | Role | Interfaces |
|---|---|---|
| **`Services/GitHubReleaseService.cs`** | Fetch live release list from `https://api.github.com/repos/fogyisland/ComfyUIEnvironmentManagement/releases?per_page=30`; cache to `release_cache.json` (24h TTL); offline fallback | `Task<IReadOnlyList<GitHubRelease>> FetchAsync(CancellationToken ct)` |
| **`Services/ChangelogParser.cs`** | Parse `CHANGELOG.md` at startup → list of `(version, date, sections)`; structured per PEP-style markdown | `IReadOnlyList<ChangelogEntry> Parse(string md);` |
| **`Services/AppIconService.cs`** | Resolve theme-aware icon URI from current `Palette.{Dark,Light}.xaml` resource lookup; returns `pack://application:,,,/asset/icon-{theme}.png` or fallback master | `Uri ResolveIconUri(string scene); // scene = "small"/"hero"/"exe"` |
| **`Infrastructure/MultiStageSplashProgress.cs`** | Weighted 4-stage progress reporter: each stage contributes % to total; thread-safe; bounded 0-100 | `void Report(Stage stage, int stagePercent); int TotalPercent { get; }` |

### New models

| Model | Fields |
|---|---|
| **`Models/GitHubRelease.cs`** | `string TagName` (e.g. "v0.6.11"), `string Name`, `DateTime PublishedAt`, `string HtmlUrl`, `bool IsPrerelease` |
| **`Models/ChangelogEntry.cs`** | `string Version`, `DateTime? Date`, `IReadOnlyList<string> BulletPoints`, `string RawMarkdown` |
| **`Models/Stage.cs`** (enum) | `Init = 0, LoadDatabase = 1, LoadTheme = 2, Ready = 3` |

### New commands (Dashboard)
- `CopyStagingPathCommand` (clipboard)
- `OpenStagingFolderCommand` (`explorer.exe /select,<path>`)
- `OpenReleaseUrlCommand` (BrowserLauncher)
- `ToggleChangelogExpandCommand` (show 5 vs all)

### Existing services reused
- `BrowserLauncher` (v0.6.10) — for OpenReleaseUrlCommand
- `AppLogger` (v0.6.5.13) — log each splash stage + asset load failures
- `HttpClient` (transient) — GitHub fetch with retry policy (3 retries, exponential backoff, 30s timeout)
- `Palette.{Dark,Light}.xaml` — theme-aware resource lookup for AppIconService

---

## Section A: DashboardView Redesign

### Layout (top → bottom)
```
┌──────────────────────────────────────────────────────┐
│ Hero Row (single row, full width, ~120px tall)        │
│ ┌────────┐ ┌──────────────────────────────────────┐ │
│ │ icon │ │ ComfyUI 多环境管理系统    [刷新]      │ │
│ │ 96x96 │ │ v0.6.11 (2026-08-11)                 │ │
│ │        │ │ ⭐ 123 stars · 📦 25 releases          │ │
│ └────────┘ └──────────────────────────────────────┘ │
├──────────────────────────────────────────────────────┤
│ ┌─────────────┬─────────────┐                        │
│ │ Env Stats   │ Node Count  │  ← existing 2 cards   │
│ ├─────────────┼─────────────┤                        │
│ │ Recent Ops  │ ✦ Latest+   │  ← Latest+Changelog  │
│ │             │ Changelog   │     merged card         │
│ └─────────────┴─────────────┘                        │
├──────────────────────────────────────────────────────┤
│ 📥 下载地址 (full-width section, NEW)                 │
│ ┌────────────────────────────────────────────────┐ │
│ │ 本地 staging:                                  │ │
│ │  release\staging\ComfyUI Manager\            │ │
│ │  ComfyUI.Manager.exe                          │ │
│ │  [📋 复制路径] [📂 打开文件夹]                │ │
│ │ ───────────────────────────────────────────── │ │
│ │ GitHub Release:                                │ │
│ │  github.com/.../releases/latest/v0.6.11.zip   │ │
│ │  [🌐 浏览器打开]                                │ │
│ └────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

### A.1 Hero row
- **Icon**: 96×96 Image, source = `AppIconService.ResolveIconUri("hero")` (returns `icon-light.png` for Light theme, `icon-dark.png` for Dark); fallback to single `icon.png` if variants missing
- **Title**: "ComfyUI 多环境管理系统" (FontSize=24, Bold)
- **Version + Date**: from `AppVersionInfo` (existing helper) + `LastSnapshot.LatestRelease` (existing)
- **GitHub strip**: ⭐ stars count + 📦 release count (single line, FontSize=11, Gray)
- **Refresh button**: existing pattern, on right
- **Snapshot time**: existing pattern, on right of refresh
- **Loading indicator**: existing pattern

### A.2 Existing 2 cards (no change)
- **Card 1** (top-left): Environment Stats (Running/Stopped/Undeployed/Total) — UNCHANGED
- **Card 2** (top-right): Node Count — UNCHANGED

### A.3 Existing card 3 + new card 4 merge
- **Card 3** (bottom-left): Recent Operations — UNCHANGED
- **Card 4** (bottom-right): **REPLACED** with "✦ Latest + Changelog" merged card
  - Header: "✦ 最新发布"
  - **Latest release sub-section**:
    - Large version (FontSize=28, Bold, PrimaryBrush) — `LastSnapshot.LatestRelease` (existing field)
    - Date (FontSize=11, Gray) — `GitHubRelease.PublishedAt`
    - "✦ Released" / "❌ Failed" badge (reuse `GitHubFailed` indicator)
  - **Changelog sub-section** (NEW):
    - ListBox showing last 5 entries by default (collapsed), expandable to all
    - Each row: version + date (small) + 3-5 bullet points
    - Current version entry highlighted (PrimaryBrush border, "✓ 已安装" badge)
    - "▼展开全部" toggle button (bottom)
  - **Empty state**: "📋 暂无 changelog 数据"

### A.4 📥 下载地址 section (NEW)
- Border card with full-width, similar styling to other cards (SurfaceBrush bg, CornerRadius=8, Padding=20)
- **Header**: "📥 下载地址" (FontSize=14, Bold)
- **Local staging row**:
  - Path: `release\staging\ComfyUI Manager\ComfyUI.Manager.exe` (resolved via `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "release", "staging", "ComfyUI Manager", "ComfyUI.Manager.exe"))`)
  - Works in: dev (`dotnet run`), staging exe (`release/staging/ComfyUI Manager/`), release zip (`publish/`)
  - Display as: `<TextBlock Text="{Binding StagingPath}" TextWrapping="Wrap" FontFamily="Consolas" />`
  - "📋 复制路径" button → `CopyStagingPathCommand` → `Clipboard.SetText(path)` (try/catch for STA STAThread)
  - "📂 打开文件夹" button → `OpenStagingFolderCommand` → `Process.Start("explorer.exe", "/select," + fullPath)`
- **GitHub Release row**:
  - URL: `https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases/latest`
  - Display as: `<TextBlock Text="{Binding ReleaseUrl}" FontFamily="Consolas" TextTrimming="CharacterEllipsis" />`
  - "🌐 浏览器打开" button → `OpenReleaseUrlCommand` → `BrowserLauncher.OpenWithChromeFallback(url)` (reuses v0.6.10 abstraction)
- **Network failure state**: grey out GitHub row + show "ⓘ 离线模式,使用缓存数据" badge

### A.5 DashboardViewModel extensions
- New properties:
  - `IReadOnlyList<ChangelogEntry> Changelog` (parsed from CHANGELOG.md, default = empty)
  - `bool IsChangelogExpanded` (default false; toggle)
  - `string StagingPath` (computed at Load)
  - `string ReleaseUrl` (constant)
  - `int? GitHubStars` (nullable; null = failed to fetch)
  - `int? GitHubReleaseCount` (nullable)
  - `LastChangelogSync` (DateTime?)
- New commands (above)
- Modified `LoadAsync`:
  - Parallel fetch: `_gitHubService.FetchAsync(ct)` + `Task.Run(() => _changelogParser.Parse(File.ReadAllText(changelogPath)))`
  - On GitHub fetch fail → keep cached + set `LastChangelogSync = cached.At` + `GitHubFailed = true`
  - On Changelog parse fail → fallback to hardcoded `HardcodedFallbackChangelog` (3 most recent entries)

---

## Section B: Splash Rewrite (Multi-Stage Progress)

### B.1 Layout (900×540)
```
┌────────────────────────────────────────────────┐
│                                                │
│  [asset/ComfyUI.png] (full bg, Stretch=        │
│   UniformToFill)                                │
│                                                │
│                                                │
│                                                │
│ ─────────────────────────────────────────────  │ ← bottom strip
│  [初始化服务...]      25% ░░░░░░░░░░░░░░░░░░░░ │
│  [加载数据库...]      50% ░░░░░░░░░░░░░░░░░░░░ │
│  [加载主题...]        75% ░░░░░░░░░░░░░░░░░░░░ │
│  [就绪  ]           100% ░░░░░░░░░░░░░░░░░░░░ │
└────────────────────────────────────────────────┘
```

### B.2 Background
- **Single image**: `<Image Source="pack://application:,,,/asset/ComfyUI.png" Stretch="UniformToFill" />` (no Border / no fallback bg color)
- If asset missing → log error via AppLogger + continue without splash (existing v0.6.8 fallback path preserved)

### B.3 Progress UI (bottom strip)
- Border with semi-transparent background (`#80000000`) at bottom 120px
- 4 rows: stage label (left, monospace FontFamily="Consolas", FontSize=12) + percent (right) + thin ProgressBar (5px high, PrimaryBrush fill)
- Rows fade-in as stage advances (200ms ease-out, Storyboard)
- Existing fade-out preserved: 3s minimum after `MainWindow.Loaded` + 800ms opacity fade

### B.4 Multi-stage progress
- `MultiStageSplashProgress` (new, in `Infrastructure/`)
- 4 stages with weights: Init (25%), LoadDatabase (25%), LoadTheme (25%), Ready (25%)
- `Report(Stage stage, int stagePercent)` (stagePercent 0-100 within stage)
- `TotalPercent` = sum of (stage weight × stage percent / 100), clamped to 0-100
- Thread-safe (UI thread only via Dispatcher; tests use direct calls)

### B.5 Startup wiring (App.xaml.cs)
- Stage 1 (Init, 25%): after `_splash.Show()` returns, before any service ctor
- Stage 2 (LoadDatabase, 50%): after `EnvironmentRepository` ctor + `EnsureColumns` migration in `SqliteConnectionFactory`
- Stage 3 (LoadTheme, 75%): after `Theme.LoadPalette` + first DynamicResource resolution (i.e., after `MainWindow` XAML loaded)
- Stage 4 (Ready, 100%): after `MainWindow.Loaded` fires (existing)
- Each transition: `MultiStageSplashProgress.Report(stage, 100)` + AppLogger.Info($"splash stage: {stage} 100%")

### B.6 Behavior preservation
- 3s minimum visible time (existing TimerFactory seam)
- 800ms opacity fade out (existing Storyboard)
- NotifyMainWindowReady signal (existing)
- Alt+F4 closes splash without killing main (existing v0.6.9.1 fix preserved)

---

## Section C: Icon Integration

### C.1 Build pipeline
- **Manual bake** (NOT runtime): use `magick` or equivalent tool to generate `icon.ico` (256×256 master + 48×48 + 32×32 + 16×16 sizes) from `asset/icon.png`
- Single `icon.ico` for EXE (Windows auto-selects size based on display)
- `icon-light.png` + `icon-dark.png` for sidebar/hero (theme-aware)
- If `icon-light.png` is hard to derive (icon has shadow/highlights), **fallback to single `icon.png`** (works on both themes) — YAGNI until tested

### C.2 Asset files
| File | Source | Size | Used by |
|---|---|---|---|
| `asset/icon.png` (existing) | user-provided master | 1.2 MB | sidebar/hero fallback |
| `asset/icon.ico` (NEW) | baked from icon.png | ~50 KB | EXE file icon + Window.Icon |
| `asset/icon-light.png` (NEW, optional) | derived from icon.png | ~50 KB | sidebar/hero Light theme |
| `asset/icon-dark.png` (NEW, optional) | derived from icon.png | ~50 KB | sidebar/hero Dark theme |

### C.3 Application scenes

| Scene | How | Icon resolution |
|---|---|---|
| **EXE file icon** (explorer, taskbar, Alt-Tab) | csproj `<ApplicationIcon>asset/icon.ico</ApplicationIcon>` | OS reads from EXE header |
| **Window title bar** (MainWindow, AboutDialog, Splash, all dialogs) | `<Window Icon="pack://application:,,,/asset/icon.ico">` in each XAML | WPF resolves from pack URI |
| **Sidebar header** (MainWindow line 75-79) | `<Image Source="{DynamicResource SidebarIconUri}" Width="24" Height="24" />` next to title text | AppIconService lookup |
| **Dashboard hero** (per Section A.1) | `<Image Source="{DynamicResource HeroIconUri}" Width="96" Height="96" />` | AppIconService lookup |

### C.4 AppIconService resolution
- Input: scene string ("small"/"hero"/"exe")
- Lookup current palette (Dark or Light) via `Application.Current.Resources["PaletteName"]` or similar
- Return URI:
  - scene="exe" → `pack://application:,,,/asset/icon.ico` (always, regardless of theme)
  - scene="small"+"Dark" → `pack://application:,,,/asset/icon-dark.png` else `icon-light.png`
  - scene="hero" → same as "small"
  - missing variant → fallback to `pack://application:,,,/asset/icon.png` (single master)
- Cached per (scene, theme) pair; no per-frame lookup

### C.5 G4 compliance
- Icon URIs in XAML use `DynamicResource` (not StaticResource — theme switching works)
- No `Setter Property="Icon" Value="{StaticResource ...}"` pattern anywhere (G4 banned)
- Brushes follow existing pattern (PrimaryBrush / OnSurfaceBrush — already DynamicResource)

### C.6 Files modified for icon
- `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` — `<ApplicationIcon>asset/icon.ico</ApplicationIcon>` (line 10)
- `src-wpf/ComfyUI.Manager/App.xaml` — add `<BitmapImage x:Key="SidebarIconUri" UriSource="..."/>` (NO — use `AppIconService` instead; XAML stays pure)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` — `Icon="pack://application:,,,/asset/icon.ico"` + sidebar header Image
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` — `Icon="pack://application:,,,/asset/icon.ico"`
- `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml` — `Icon="pack://application:,,,/asset/icon.ico"`
- All other dialogs — same `Icon` attr (7 dialogs)

---

## Data Flow

### C.1 Startup sequence (with progress)
```
[App.OnStartup]
  ├─ SplashWindow.Show() (0ms)
  ├─ [Stage 1: Init, 25%] → MultiStageSplashProgress.Report(Init, 100)
  ├─ ctor: EnvironmentRepository → SqliteConnectionFactory.EnsureColumns
  ├─ [Stage 2: LoadDatabase, 50%] → MultiStageSplashProgress.Report(LoadDatabase, 100)
  ├─ ctor: MainViewModel → Theme.LoadPalette
  ├─ [Stage 3: LoadTheme, 75%] → MultiStageSplashProgress.Report(LoadTheme, 100)
  ├─ MainWindow.Show() + MainWindow.Loaded
  ├─ [Stage 4: Ready, 100%] → MultiStageSplashProgress.Report(Ready, 100)
  ├─ SplashViewModel.NotifyMainWindowReady() → 3s timer starts
  └─ [After 3s] → SplashViewModel.FadeOut() → 800ms opacity fade → Close
```

### C.2 DashboardViewModel.Load flow
```
[DashboardViewModel.RefreshAsync]
  ├─ Task.WhenAll:
  │   ├─ GitHubReleaseService.FetchAsync(ct)
  │   │   ├─ cache hit (< 24h) → return cached
  │   │   ├─ cache miss → HTTP GET → parse → write cache → return
  │   │   └─ network fail → return last cached + GitHubFailed=true
  │   └─ Task.Run(() => ChangelogParser.Parse(File.ReadAllText(CHANGELOG.md)))
  │       ├─ success → return parsed entries
  │       └─ parse fail → return HardcodedFallbackChangelog (3 entries)
  ├─ Update: LatestRelease, Changelog, GitHubStars, GitHubReleaseCount
  └─ On exception → GitHubFailed=true, log via AppLogger.Error
```

### C.3 Click-to-copy flow
```
[DashboardViewModel.CopyStagingPathCommand]
  ├─ try { Clipboard.SetText(StagingPath); }
  ├─ catch (Exception ex) when (Clipboard access denied) {
  │     AppLogger.Warn("clipboard access denied: {ex.Message}");
  │     ShowTransientBanner("复制失败,请手动复制");
  │   }
  └─ ShowTransientBanner("已复制: " + truncatedPath) (2s fade)
```

---

## Error Handling

| Failure | UX behavior |
|---|---|
| `asset/ComfyUI.png` missing | Skip splash, log AppLogger.Warn, main window shows immediately (existing v0.6.8 fallback) |
| `asset/icon.ico` missing | Use existing `app.ico` placeholder (current 1×1 pixel) — log Warn, no UI change |
| GitHub fetch fail (timeout / 4xx / 5xx) | Reuse cached + show "ⓘ 离线模式" badge; never throw to UI |
| CHANGELOG.md missing | Use `HardcodedFallbackChangelog` (3 most recent entries hardcoded in code) |
| Changelog parse fail | Same fallback |
| Clipboard.SetText throws | Catch + log Warn + show "复制失败" transient banner |
| explorer.exe not found | Catch + log Warn + show "打开失败" transient banner |
| BrowserLauncher fail | Existing fallback chain (Chrome → default browser → ErrorBanner) |
| Stage progress called after splash disposed | No-op (existing `_disposed` idempotent lock) |
| Stage percent out of range | Clamp to 0-100 (existing pattern from RequirementsInstallerTests) |

---

## Testing Strategy

### Unit tests (new)
- `Services/GitHubReleaseServiceTests.cs` (5 tests):
  - `FetchAsync_CacheHit_ReturnsCachedWithoutHttp`
  - `FetchAsync_CacheMiss_PerformsHttp_ParsesAndPersists`
  - `FetchAsync_NetworkTimeout_ReturnsLastCached_SetsGitHubFailed`
  - `FetchAsync_InvalidJson_LogsAndThrows`
  - `FetchAsync_EmptyResponse_ReturnsEmptyList`
- `Services/ChangelogParserTests.cs` (4 tests):
  - `Parse_StandardMarkdown_ReturnsOrderedEntries`
  - `Parse_NestedBullets_PreservesHierarchy`
  - `Parse_EmptyInput_ReturnsEmpty`
  - `Parse_MissingVersion_ReturnsRawAsSingle`
- `Services/AppIconServiceTests.cs` (3 tests):
  - `ResolveIconUri_DarkTheme_ReturnsDarkPng`
  - `ResolveIconUri_LightTheme_ReturnsLightPng`
  - `ResolveIconUri_MissingVariant_FallsBackToMaster`
- `Infrastructure/MultiStageSplashProgressTests.cs` (4 tests):
  - `Report_WeightedSum_ComputesTotalPercent`
  - `Report_ClampToValidRange`
  - `Report_OutOfOrderStage_DoesNotRegress`
  - `Report_AfterDisposed_NoOp`

### Integration tests (modified)
- `Views/SplashWindowLoadTests.cs` (NEW STA load test):
  - `SplashWindow_NewMultiStageLayout_DoesNotThrow` — verify asset path resolves + progress UI renders
- `ViewModels/DashboardViewModelTests.cs` (+6 tests):
  - `RefreshAsync_FetchesBothGitHubAndChangelog_InParallel`
  - `RefreshAsync_GitHubFail_PreservesCachedChangelog`
  - `RefreshAsync_ChangelogMissing_UsesHardcodedFallback`
  - `CopyStagingPathCommand_ClipboardSuccess_LogsInfo`
  - `OpenStagingFolderCommand_ExplorerSuccess_LogsInfo`
  - `ToggleChangelogExpandCommand_TogglesIsChangelogExpanded`

### Manual smoke (user desktop)
- 12-step GUI smoke checklist (Appendix A below)

### Test count delta (estimate)
- New: 5 + 4 + 3 + 4 = 16 unit tests + 1 STA + 6 VM = 23 tests
- Modified: 0 (DashboardViewModelTests grows by 6 inline additions)
- Final scoreboard estimate: ~854 PASS / 0 FAIL / 1 SKIP (831 + 23)

---

## File Structure

### NEW files (10)
| Path | Lines (est) |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/GitHubReleaseService.cs` | 120 |
| `src-wpf/ComfyUI.Manager/Services/ChangelogParser.cs` | 90 |
| `src-wpf/ComfyUI.Manager/Services/AppIconService.cs` | 70 |
| `src-wpf/ComfyUI.Manager/Infrastructure/MultiStageSplashProgress.cs` | 80 |
| `src-wpf/ComfyUI.Manager/Models/GitHubRelease.cs` | 30 |
| `src-wpf/ComfyUI.Manager/Models/ChangelogEntry.cs` | 25 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs` | 180 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs` | 120 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/AppIconServiceTests.cs` | 100 |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs` | 110 |
| `tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs` | 60 (NEW STA load test, added to existing test file? or new file) |
| `CHANGELOG.md` (repo root) | ~80 (3-5 most recent versions) |
| `asset/icon.ico` | binary (~50 KB, baked) |

### MODIFIED files (8)
| Path | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` | `<ApplicationIcon>` line update |
| `src-wpf/ComfyUI.Manager/App.xaml` | no change (uses code-behind for icon resolution) |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | splash progress wiring (4 stages) |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | `Icon` attr + sidebar header Image |
| `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` | full redesign (hero + cards + 下载地址) |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` | asset swap + progress UI |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs` | multi-stage handler |
| `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs` | extended data + commands |
| `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs` | extended with multi-stage |
| All 7 dialogs (.xaml): AboutDialog, BaseEnvProfilePickerDialog, BaseEnvProgressDialog, BulkUpdateDialog, CatalogEntryPickerDialog, ConfirmDialog, CreateEnvDialog, InstallDialog, LogViewerDialog, NodeInstallDiffWarningDialog | add `Icon="pack://application:,,,/asset/icon.ico"` |

### UNTOUCHED files (per project freeze conventions)
- All SQLite schema files
- `Settings.cs`, `appsettings.json`
- All existing service files (BaseEnvInstaller, RequirementsInstaller, etc.)
- All existing dialog XAML .cs code-behind (only add `Icon` attr in .xaml)
- Resx files (no new strings; Chinese labels inline per project convention)

---

## Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| **R1** | GitHub API rate limit (60/h unauthenticated) | Cache 24h TTL; batch fetch with `per_page=30`; offline fallback; never throw |
| **R2** | ChangelogParser fragile to MD format drift | Use simple regex (no MD library); fallback to hardcoded list on parse fail |
| **R3** | `Clipboard.SetText` requires STAThread; can throw | Wrap in try/catch + show transient banner (existing ErrorBanner pattern) |
| **R4** | Splash asset 2.4 MB adds to publish size (zip/staging exe) | Acceptable (one-time asset; user explicitly chose this image); log size via AppLogger |
| **R5** | Multi-stage progress may exceed 100% (caller bug) | Clamp in MultiStageSplashProgress; test covers |
| **R6** | `icon.ico` multi-size generation tool unavailable | Provide manual PowerShell + ImageMagick commands in plan; verify in T1 prep |
| **R7** | 4-stage progress makes splash feel slow | Each stage takes <500ms in practice; total = existing 3s minimum + minor overhead |
| **R8** | Dashboard redesign breaks existing tests | All existing DashboardViewModelTests assertions preserved (additive new props, no removed) |
| **R9** | `explorer.exe /select,<path>` fails on paths with spaces | Quote full path arg; test with paths containing spaces |
| **R10** | Theme-variant icon derivation requires image editing tool | Default to single icon.png (works on both themes); defer variants until visual test confirms need |
| **R11** | Add 4 `Icon` attrs to 10 dialogs = 10 lines of churn | One-line Edit each; trivial; can be batched in T4 |
| **R12** | Card 4 "Latest+Changelog" merge breaks visual rhythm | Test layout in STA + user GUI smoke (step 5-7) |

---

## Carry-forward

- **CF1**: GitHub stars + release count cache TTL = 24h (consider reducing to 6h or 1h based on usage)
- **CF2**: ChangelogParser only parses top-level bullets (deeper hierarchy = separate spec if needed)
- **CF3**: Light/Dark icon variants — defer until visual smoke confirms single icon.png doesn't work on both themes
- **CF4**: `HardcodedFallbackChangelog` (3 entries) — manual update each release cycle (or extract from git log)
- **CF5**: Splash progress bars could expand to 8 sub-stages (init logger + init repo + init catalog cache + init db + load themes + render main + ...) — YAGNI until 4 stages prove insufficient

---

## Verification (end-to-end)

```bash
# Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# Tests
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build       # ~854/0/1

# Staging rebuild
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal                          # 0/0
```

---

## Appendix A: GUI Smoke Checklist (user desktop)

1. 启动 staging → Splash 900×540 显示 **ComfyUI.png** 全屏背景(无 Border 背景色) + 底部 4 行进度条初始为空
2. Splash 进度条在 ~1s 内 0% → 25% (初始化服务) → 50% (加载数据库) → 75% (加载主题) → 100% (就绪)
3. Splash 满 3s + 800ms 渐变消失 → MainWindow 显示
4. 侧栏点 "主页" → Dashboard 顶部 hero 行显示:96×96 大图标 + 标题 + 版本 + GitHub stars/release count strip
5. Dashboard 4 卡片正常显示:Env Stats / Node Count / Recent Ops / **✦ Latest+Changelog(merged)**
6. Card 4 "Latest+Changelog" 显示当前版本 + changelog 列表 5 条 + "▼展开全部" 按钮
7. 点 "▼展开全部" → 显示所有 releases(应该有 ~25 条)
8. 当前版本(v0.6.11)条目的 PrimaryBrush 边框高亮 + "✓ 已安装" 徽章
9. Dashboard 底部 "📥 下载地址" 卡片显示 staging exe 路径 + 3 个按钮
10. 点 "📋 复制路径" → 剪贴板成功复制 + 短暂 toast "已复制"
11. 点 "📂 打开文件夹" → 资源管理器打开 + 选中 ComfyUI.Manager.exe
12. 点 "🌐 浏览器打开" → Chrome 优先打开 GitHub release 页面
13. 切换主题(暗↔亮)→ Splash 图标 / Dashboard hero 图标 / 侧栏标题图标跟随主题(若 variants 已 bake)
14. AboutDialog(菜单 "关于" → 关于...) → 标题栏左侧显示 icon.ico
15. GitHub 离线场景(断网后重启)→ "ⓘ 离线模式" 徽章 + 上次同步时间
16. CHANGELOG.md 缺失场景 → 仍能显示 3 条 hardcoded fallback changelog

---

## Spec Self-Review

**Placeholder scan**: No "TBD" / "TODO" / "implement later" — all sections concrete.

**Internal consistency**:
- Section A.4 references `BrowserLauncher` which is v0.6.10 SHIP-READY ✓
- Section B references v0.6.8 Splash pattern (TimerFactory seam, NotifyMainWindowReady, FadeCompleted) which exist ✓
- Section C references v0.6.9.2 G4 rule (DynamicResource only) which is established ✓
- Section A.1 / C.4 both reference `AppIconService` — single source ✓

**Scope check**: 1 spec, focused on Dashboard + Splash + Icon integration. Could be split into 3 specs but they share `AppIconService` + `DashboardViewModel`/`MainWindow.xaml` + `App.xaml.cs` wiring, so splitting would create more cross-spec dependencies. KEEP AS 1 SPEC.

**Ambiguity check**:
- "Various logo scenes" (user's original "手动风格图片中各个 Logo 应用场景") — resolved to: EXE icon + Window title bar (10 dialogs) + Sidebar header + Dashboard hero
- "Download path and address" — resolved to: 本地 staging 路径 + GitHub release URL
- "Loading animation to 100%" — resolved to: 4-stage progress weighted 0→100%

All requirements traceable to a Section above.

---

**END OF SPEC**

**Next step**: User reviews this spec file (path: `docs/superpowers/specs/2026-08-11-dashboard-splash-icon-polish-design.md`) and approves, then I invoke writing-plans skill to create the implementation plan.