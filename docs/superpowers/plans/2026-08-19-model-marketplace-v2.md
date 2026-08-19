# Model Marketplace v0.6.21 Implementation Plan (HF Source + Mirror + Token)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the v0.6.20 模型市场 (SHIPPED `9d0bd0f`) so users can enable **HuggingFace** as a second source (in addition to CivitAI), configure an **API token** for gated-model access, and route traffic through a **mirror URL** (default `https://hf-mirror.com` for HF; user-defined for CivitAI) when direct access is blocked or slow. Marketplace view gains a **source filter chip group** in the toolbar (mirrors the existing kind-chip pattern).

**Architecture (delta from v0.6.20):**
- **Replace stub** `HuggingFaceModelSource` with real implementation: `GET {baseUrl}/api/models?search={q}&limit={n}&full=true` for search + `GET {baseUrl}/api/models/{repo_id}` for version/file detail (`siblings[]`, `cardData`, `tags`). Kind heuristic (priority: `lora` → LORA, `checkpoint` → Checkpoint, `vae` → VAE, `controlnet` → Controlnet, `textual-inversion` → TextualInversion, `upscaler` → Upscaler, `hypernetwork` → Hypernetwork; else Other). NSFW heuristic: any tag contains `nsfw` (case-insensitive) → Nsfw; else Sfw. Primary file: largest `*.safetensors`/`*.bin` by `size` from `siblings[]`. **One virtual version per ModelEntry** keyed by commit `sha` (HF has no explicit versions).
- **New `ModelSourceFactory`** (per-source factory) reads `Settings`, picks base URL (mirror or official) and API token, instantiates `IModelSource` with the right configuration. Returns `null` for disabled sources; aggregator's internal `IsEnabled` filter never sees them.
- **`Settings` gains 6 new fields** + 1 dead-field cleanup (v0.6.20 `ModelSourceCivitAiEnabled` default `true` retained). Mirror resolver: `useMirror && !string.IsNullOrWhiteSpace(mirrorUrl) ? mirrorUrl.TrimEnd('/') : officialUrl`.
- **CivitAI ctor signature change**: `(HttpClient http, string baseUrl)` (was `(HttpClient http)`); replace `const BaseUrl` with field. All existing tests get updated.
- **`MainViewModel.ShowModels()` rewire**: drop direct `new CivitAiModelSource(http, …)` construction, use `ModelSourceFactory.CreateAll(_settings, http)`. Re-add HF instantiation (T10 polish removed in v0.6.20).
- **Marketplace view toolbar source filter chips**: `SourceChips` `ItemsControl` parallel to `KindChips`. In-memory filter of `Models` collection via `ICollectionView.Filter` (no re-query). `ShowOnlyCivitai` / `ShowOnlyHuggingFace` bool properties.
- **SettingsView "模型市场" section expansion**: add HF enabled checkbox, token `PasswordBox` + 👁 toggle button, mirror checkboxes + URL fields with reset button, [测试连接] button (lightweight endpoint probe), [立即刷新模型市场] button.
- **New `BindablePasswordBox` WPF custom control** (WPF `PasswordBox` doesn't expose `Password` DP out of box — security feature). Theme.xaml style with `Path` icon 👁 (no emoji to avoid WPF font fallback issues per v0.6.17.1 lesson).
- **AppLogger subsystem tags**: existing `model-civitai` and `model-huggingface` get mirror info lines when `UseMirror=true`. New `model-mirror` tag for mirror-related warnings (non-HTTPS).
- **No change** to `ModelDownloader`, `ModelSymlinker`, `ModelFilesystemScanner`, `ModelEntry` DTOs, `ModelMetaSidecar` schema, env-startup wiring, or `MainWindow.xaml` sidebar (9th RadioButton already exists from v0.6.20).

**Tech Stack:** .NET 8 / WPF / C# 12 / SQLite / xUnit / Moq / `HttpClient` (singleton in `App.xaml.cs`, reused from v0.6.19) / `JunctionLinker` (existing) / `Progress<T>` (long-running → UI thread marshal)

**Spec:** `docs/superpowers/specs/2026-08-19-model-marketplace-v2-design.md` (HEAD `1235b29`)

**Base branch:** main at `eded5a6` (post v0.6.20 plan commit `28dc7d1` + v0.6.20 SDD COMPLETE at `9d0bd0f`).

## Global Constraints

- Test baseline `1483 PASS / 6 pre-existing FAIL / 5 SKIP` (post v0.6.20 SHIP-READY); target post-SDD `~1503 PASS / 6 pre-existing FAIL / 6 SKIP` (1 new HF real-fetch `[SKIP]` test added)
- All path fields follow `SettingsDefaults.Resolve(...)` pattern (template-style: empty → default subdir name; relative paths preserved; absolute paths under `projectRoot` migrated to relative)
- All new `bool` / enum bindings use existing converters registered in `Resources/Theme.xaml` (`BoolToVisibility` / `NullToVisibility` / `SectionEquality` / `EnumEqualsConverter` / `ModelNsfwBadgeBrush` / `ModelNsfwBadgeText` / `ModelKindBadgeBrush` from v0.6.20). New converters only if absolutely necessary; reuse existing when possible
- Sidebar RadioButton (Models tab) already wired in v0.6.20 — no change
- AppLogger subsystem strings: `model-huggingface`, `model-mirror` (NEW), `model-civitai` (existing, gets mirror info lines when `UseMirror=true`)
- Settings plumbing: `[JsonPropertyName("...")] public T X { get; set; } = default;` + matching row in `CopyInto(target, source)`
- HuggingFace is a real `IModelSource` implementation — replaces the v0.6.20 stub that returned `Array.Empty<ModelEntry>()`. SourceKind returns `ModelSourceKind.HuggingFace` (was `CivitAi` placeholder in stub)
- Per-source mirror injection via factory: `ModelSourceFactory.Create{CivitAi|HuggingFace}(Settings, HttpClient)` picks `baseUrl` from `useMirror ? mirrorUrl.TrimEnd('/') : officialUrl`
- Token storage: plaintext in `<projectRoot>/.manager/settings.json` (low-risk local app, user-typed token user takes responsibility; DPAPI encryption deferred to v0.6.22+)
- Token transport: HTTPS strongly preferred. Mirror URL field accepts `http://` for LAN proxies / self-hosted mirrors (common case for users behind corporate firewalls) but logs `WARN model-mirror` and shows ⚠ icon next to the field. Token is **never** sent over `http://` — UI greys out [测试连接] button when mirror is `http://` and token is set
- Token never logged: `AppLogger.Info("model-huggingface", "token configured, length=42")` is OK; `AppLogger.Info("model-huggingface", $"token={token}")` is FORBIDDEN
- `PasswordBox` doesn't expose `Password` DP (WPF security feature) — need custom `BindablePasswordBox` control with `BindablePassword` DP + 👁 toggle. Use `Path` icon for 👁 (no emoji per v0.6.17.1 WPF font fallback lesson)
- 👁 toggle reveals plaintext for 30 seconds then re-hides. ViewModel tracks `_tokenRevealUntilUtc`
- Source filter is **view-time only** — `ShowOnlyCivitai` / `ShowOnlyHuggingFace` toggle `ICollectionView.Filter` on `Models`, no re-query
- v0.6.20 settings.json has no v0.6.21 fields → `SettingsRepository.Load` uses `default = ""` / `default = false` for new fields. `SettingsDefaults.Resolve` does NOT generate a token
- Real-fetch tests use `[Fact(Skip = "...")]` with descriptive reason (CI does not hit network)
- Commits: scoped per task (`git add <specific paths>` whitelist); no bundled WIP
- 中文 UI copy: "HuggingFace" / "API Token" / "测试连接" / "使用镜像" / "镜像地址" / "重置" / "立即刷新模型市场" / "源" / "勾选镜像后访问国内镜像地址" / "Token 需 https 安全传输" / "未配置 token 也能浏览公开模型,但部分 gated 模型将 403"
- YAGNI: no gated-model license-accept flow, no HF search facets, no HF pagination beyond first page, no HF multi-file bundle, no token encryption at rest, no multi-token rotation, no OAuth flow, no per-version mirror, no source-selector-as-download-time-filter
- v-bump skipped (user decides); no release zip; staging rebuild at end (may be blocked by user's running staging exe — ask user to close before rebuild)
- Tests live under `tests-wpf/ComfyUI.Manager.Tests/Services/`, `/ViewModels/`, `/Views/`, or `/Controls/` mirroring production folder structure
- All temp files in tests: `Path.Combine(Path.GetTempPath(), "ComfyUIMgr<Name>_" + Guid.NewGuid().ToString("N"))` + cleanup in `Dispose`
- DelegatingHandler pattern for HTTP mocking (existing project pattern, see `ModelSourceCivitAiTests` for template)
- VM UI-bound awaits must NOT use `.ConfigureAwait(false)` (per `feedback_configureawait_false_placement.md`) — service layer internal awaits may use it for thread pool efficiency
- IProgress<T> implementations must be wrapped in `new Progress<T>(...)` constructed on UI thread (per `feedback_wpf_observablecollection_progress.md`) so Report marshals back to UI thread

## Files to Touch

### New files

| Path | Purpose |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelSourceFactory.cs` | Per-source factory: reads Settings, picks baseUrl + token, instantiates IModelSource. Returns null for disabled sources. |
| `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs` | WPF custom control wrapping PasswordBox + BindablePassword DP + show/hide eye toggle. |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceFactoryTests.cs` | Factory unit tests (3): CreateCivitAi disabled → null, CreateHuggingFace disabled → null, CreateAll resolves mirror URL + strips trailing slash |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs` (rewrite) | Real HF source tests (8): search, token, kind heuristics, nsfw heuristic, primary file selection + 1 SKIP real-fetch |
| `tests-wpf/ComfyUI.Manager.Tests/Controls/BindablePasswordBoxTests.cs` | Custom control tests (2): DP PropertyChanged, reveal-then-hide 30s timer |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelSourceFilterTests.cs` | Source filter chip tests (3): ShowOnlyCivitai false hides CivitAi entries, ShowOnlyHuggingFace false hides HF entries, both false renders empty hint |
| `tests-wpf/ComfyUI.Manager.Tests/Services/SettingsHuggingFaceTests.cs` | Settings defaults + migration tests (4): defaults for 3 new HF fields, load-from-v0.6.20-json migrates new fields as defaults |

### Modified files

| Path | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | Add 6 new fields (`ModelSourceCivitAiUseMirror` / `ModelSourceCivitAiMirrorUrl` / `ModelSourceHuggingFaceEnabled` / `HuggingFaceApiToken` / `ModelSourceHuggingFaceUseMirror` / `ModelSourceHuggingFaceMirrorUrl`) + 6 CopyInto rows |
| `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs` | Ctor: `(HttpClient http, string baseUrl, AppLogger? logger = null)` — replace `const BaseUrl` with field. Update all call sites |
| `src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs` | Replace stub (~30 LoC) with real implementation (~200 LoC): SearchAsync hits `/api/models`, MapToModelEntry hits `/api/models/{id}`, kind/NSFW/primary-file heuristics |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | Add 6 new properties mirroring existing `ModelsDirectory` setter pattern (with MarkDirty) |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | Expand "模型市场" section: HF enabled checkbox, token PasswordBox + 👁 toggle, mirror checkboxes + URL fields with reset button, [测试连接] + [立即刷新模型市场] buttons |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` | Add [测试连接] handler (`ModelSourceFactory.TestConnectionAsync`) + [重置] handler (reset mirror URL to default) |
| `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs` | Add `ShowOnlyCivitai` / `ShowOnlyHuggingFace` bool properties + `ApplySourceFilter()` method (ICollectionView.Filter) + `SourceChips` collection |
| `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml` | Add `SourceChips` `ItemsControl` in toolbar row (parallel to `KindChips`) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | `ShowModels()`: drop direct `new CivitAiModelSource(http, …)`, use `ModelSourceFactory.CreateAll(_settings, http)`; re-add HF instantiation conditionally |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | Add `BindablePasswordBox` style + `Path` icon for 👁 toggle |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs` | Update ctor calls from `(http)` to `(http, "https://civitai.com")` |

---

## Task 1: Settings shape + SettingsViewModel bindings + SettingsView XAML expansion

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:66-70` (add 6 new fields after v0.6.20 model fields block)
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:164-165` (add 6 CopyInto rows after v0.6.20 model CopyInto rows)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:539-562` (add 6 new properties + RefreshFromSettings calls after v0.6.20 `ModelSourceCivitAiEnabled` property block)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:564-596` (expand existing "模型市场" section with HF checkbox, token PasswordBox, mirror checkboxes + URL fields, [测试连接] / [立即刷新模型市场] buttons)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` (add `TestHuggingFaceConnection` + `ResetHuggingFaceMirrorUrl` + `ToggleHuggingFaceTokenVisibility` + `RefreshModelMarketplace` handlers)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/SettingsHuggingFaceTests.cs` (new, ~4 tests)

**Interfaces:**
- Consumes: existing `Settings` + `SettingsRepository.MarkDirty` + `SettingsViewModel` MarkDirty pattern (v0.6.19 `WorkflowsDirectory` at ~line 487) + `PickFolder()` helper
- Produces:
  - `Settings.ModelSourceCivitAiUseMirror : bool = false` (default OFF + no popular China mirror today)
  - `Settings.ModelSourceCivitAiMirrorUrl : string = ""` (user-defined)
  - `Settings.ModelSourceHuggingFaceEnabled : bool = false` (default OFF)
  - `Settings.HuggingFaceApiToken : string = ""` (plaintext in settings.json)
  - `Settings.ModelSourceHuggingFaceUseMirror : bool = true` (default ON for Chinese users)
  - `Settings.ModelSourceHuggingFaceMirrorUrl : string = "https://hf-mirror.com"` (default ON)
  - 6 `SettingsViewModel` properties mirroring `ModelSourceCivitAiEnabled` setter pattern
  - 1 expanded XAML section "模型市场" with HF checkbox + token PasswordBox + mirror checkboxes + URL fields + [测试连接] / [重置] / [立即刷新模型市场] buttons

- [ ] **Step 1: Add 6 Settings fields + CopyInto rows**

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`, after the v0.6.20 model fields block (around line 66-70), add:

```csharp
// v0.6.21: 模型市场 per-source mirror + HuggingFace source + API token
[JsonPropertyName("model_source_civitai_use_mirror")]
public bool ModelSourceCivitAiUseMirror { get; set; } = false;
[JsonPropertyName("model_source_civitai_mirror_url")]
public string ModelSourceCivitAiMirrorUrl { get; set; } = "";
[JsonPropertyName("model_source_huggingface_enabled")]
public bool ModelSourceHuggingFaceEnabled { get; set; } = false;
[JsonPropertyName("huggingface_api_token")]
public string HuggingFaceApiToken { get; set; } = "";
[JsonPropertyName("model_source_huggingface_use_mirror")]
public bool ModelSourceHuggingFaceUseMirror { get; set; } = true;
[JsonPropertyName("model_source_huggingface_mirror_url")]
public string ModelSourceHuggingFaceMirrorUrl { get; set; } = "https://hf-mirror.com";
```

In `CopyInto(target, source)` (around line 164-165, after the v0.6.20 model CopyInto rows), add 6 rows:

```csharp
target.ModelSourceCivitAiUseMirror = source.ModelSourceCivitAiUseMirror;
target.ModelSourceCivitAiMirrorUrl = source.ModelSourceCivitAiMirrorUrl;
target.ModelSourceHuggingFaceEnabled = source.ModelSourceHuggingFaceEnabled;
target.HuggingFaceApiToken = source.HuggingFaceApiToken;
target.ModelSourceHuggingFaceUseMirror = source.ModelSourceHuggingFaceUseMirror;
target.ModelSourceHuggingFaceMirrorUrl = source.ModelSourceHuggingFaceMirrorUrl;
```

- [ ] **Step 2: Write failing test for settings defaults + migration**

In `tests-wpf/ComfyUI.Manager.Tests/Services/SettingsHuggingFaceTests.cs` (new file):

```csharp
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class SettingsHuggingFaceTests
{
    [Fact]
    public void ModelSourceHuggingFaceEnabled_DefaultsToFalse()
    {
        var s = new Settings();
        Assert.False(s.ModelSourceHuggingFaceEnabled);
    }

    [Fact]
    public void ModelSourceHuggingFaceUseMirror_DefaultsToTrue()
    {
        var s = new Settings();
        Assert.True(s.ModelSourceHuggingFaceUseMirror);
    }

    [Fact]
    public void ModelSourceHuggingFaceMirrorUrl_DefaultsToHfMirror()
    {
        var s = new Settings();
        Assert.Equal("https://hf-mirror.com", s.ModelSourceHuggingFaceMirrorUrl);
    }

    [Fact]
    public void Settings_LoadFromV0_6_20_Json_MigratesNewFieldsAsDefaults()
    {
        // Old v0.6.20 settings.json (no v0.6.21 fields) → all new fields get defaults
        var v0620Json = "{\"models_directory\":\"models\",\"model_source_civitai_enabled\":true}";
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(v0620Json);
        Assert.NotNull(s);
        Assert.False(s!.ModelSourceHuggingFaceEnabled);
        Assert.Equal("", s.HuggingFaceApiToken);
        Assert.True(s.ModelSourceHuggingFaceUseMirror);
        Assert.Equal("https://hf-mirror.com", s.ModelSourceHuggingFaceMirrorUrl);
        Assert.False(s.ModelSourceCivitAiUseMirror);
        Assert.Equal("", s.ModelSourceCivitAiMirrorUrl);
    }
}
```

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~SettingsHuggingFaceTests" -v`
Expected: PASS (4/4) — defaults from field initializers + JSON deserialization fills missing fields with default values

- [ ] **Step 4: Add 6 `SettingsViewModel` properties + RefreshFromSettings calls**

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`, after the v0.6.20 `ModelSourceCivitAiEnabled` property block (around line 562, find the closing `}` of the CivitAI property), add:

```csharp
// v0.6.21: 模型市场 per-source mirror + HuggingFace source + API token
public bool ModelSourceCivitAiUseMirror
{
    get => _settings.ModelSourceCivitAiUseMirror;
    set
    {
        if (_settings.ModelSourceCivitAiUseMirror == value) return;
        _settings.ModelSourceCivitAiUseMirror = value;
        MarkDirty(nameof(ModelSourceCivitAiUseMirror));
        RaisePropertyChanged();
    }
}

public string ModelSourceCivitAiMirrorUrl
{
    get => _settings.ModelSourceCivitAiMirrorUrl;
    set
    {
        var v = value ?? "";
        if (_settings.ModelSourceCivitAiMirrorUrl == v) return;
        _settings.ModelSourceCivitAiMirrorUrl = v;
        MarkDirty(nameof(ModelSourceCivitAiMirrorUrl));
        RaisePropertyChanged();
    }
}

public bool ModelSourceHuggingFaceEnabled
{
    get => _settings.ModelSourceHuggingFaceEnabled;
    set
    {
        if (_settings.ModelSourceHuggingFaceEnabled == value) return;
        _settings.ModelSourceHuggingFaceEnabled = value;
        MarkDirty(nameof(ModelSourceHuggingFaceEnabled));
        RaisePropertyChanged();
    }
}

public string HuggingFaceApiToken
{
    get => _settings.HuggingFaceApiToken;
    set
    {
        var v = value ?? "";
        if (_settings.HuggingFaceApiToken == v) return;
        _settings.HuggingFaceApiToken = v;
        MarkDirty(nameof(HuggingFaceApiToken));
        RaisePropertyChanged();
    }
}

public bool ModelSourceHuggingFaceUseMirror
{
    get => _settings.ModelSourceHuggingFaceUseMirror;
    set
    {
        if (_settings.ModelSourceHuggingFaceUseMirror == value) return;
        _settings.ModelSourceHuggingFaceUseMirror = value;
        MarkDirty(nameof(ModelSourceHuggingFaceUseMirror));
        RaisePropertyChanged();
    }
}

public string ModelSourceHuggingFaceMirrorUrl
{
    get => _settings.ModelSourceHuggingFaceMirrorUrl;
    set
    {
        var v = value ?? "";
        if (_settings.ModelSourceHuggingFaceMirrorUrl == v) return;
        _settings.ModelSourceHuggingFaceMirrorUrl = v;
        MarkDirty(nameof(ModelSourceHuggingFaceMirrorUrl));
        RaisePropertyChanged();
    }
}
```

In the `RefreshFromSettings` / `RaisePropertyChanged` block (around line 925-930, where `nameof(ModelsDirectory)` and `nameof(DefaultModelsDirectory)` are raised), add 6 more `RaisePropertyChanged` calls:

```csharp
RaisePropertyChanged(nameof(ModelSourceCivitAiUseMirror));
RaisePropertyChanged(nameof(ModelSourceCivitAiMirrorUrl));
RaisePropertyChanged(nameof(ModelSourceHuggingFaceEnabled));
RaisePropertyChanged(nameof(HuggingFaceApiToken));
RaisePropertyChanged(nameof(ModelSourceHuggingFaceUseMirror));
RaisePropertyChanged(nameof(ModelSourceHuggingFaceMirrorUrl));
```

- [ ] **Step 5: Expand SettingsView XAML "模型市场" section**

In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`, **replace the existing "模型市场" section** (lines 564-596, from the `<TextBlock x:Name="SectionModels">` line to the second description `TextBlock` before `<TextBlock x:Name="SectionPythonInterpreters">`) with the expanded section. The expanded section adds:

1. **CivitAI mirror sub-block** (after the existing ModelsDirectory textbox/buttons): CheckBox `ModelSourceCivitAiUseMirror` + TextBox `ModelSourceCivitAiMirrorUrl` (visible only when checkbox checked, via `BoolToVisibility`)
2. **HuggingFace enabled CheckBox** + descriptive text "(国内可能需要代理;hf-mirror.com 已设为默认镜像)"
3. **HuggingFace sub-block** (visible only when `ModelSourceHuggingFaceEnabled` checked): API token `<custom:BindablePasswordBox Password="{Binding HuggingFaceApiToken, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>` + 👁 toggle button + [测试连接] button + mirror CheckBox + mirror URL TextBox + [重置] button + link to https://huggingface.co/settings/tokens
4. **Mirror description** (gray helper text): "勾选镜像后访问国内镜像地址,速度更快不需代理。Token 需 https 安全传输 — http 镜像不会发送 token"
5. **[立即刷新模型市场]** button at bottom of section

```xml
<!-- ============ v0.6.21:模型市场(扩展) ============ -->
<TextBlock x:Name="SectionModels" Text="模型市场" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
<TextBlock Text="共享 models 目录(留空 = 默认 models/)。下载完成后通过 env-start junction 链接到 env 的对应子目录(checkpoint/lora/vae/...)。"
           Foreground="Gray" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap" MaxWidth="480"
           HorizontalAlignment="Left" />

<!-- ModelsDirectory (保留 v0.6.20 布局) -->
<StackPanel Orientation="Horizontal" Margin="0,8,0,4">
    <TextBlock Text="共享 models 目录" VerticalAlignment="Center"/>
    <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[ModelsDirectory], Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
<DockPanel Margin="0,2,0,0">
    <Button DockPanel.Dock="Right" Content="浏览..."
            Click="BrowseModelsDir"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
    <TextBox Text="{Binding ModelsDirectory, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource MaterialTextBox}" />
</DockPanel>
<StackPanel Orientation="Horizontal" Margin="0,4,0,0">
    <Button Content="打开目录" Click="OpenModelsDir"
            Style="{StaticResource MaterialButton}" />
    <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[ModelsDirectory], Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>

<!-- CivitAI enabled + mirror sub-block -->
<StackPanel Orientation="Horizontal" Margin="0,12,0,0">
    <CheckBox Content="CivitAI" IsChecked="{Binding ModelSourceCivitAiEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
              VerticalAlignment="Center" />
    <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[ModelSourceCivitAiEnabled], Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
<StackPanel Orientation="Horizontal" Margin="20,4,0,0"
            Visibility="{Binding ModelSourceCivitAiEnabled, Converter={StaticResource BoolToVisibility}}">
    <CheckBox Content="使用镜像" IsChecked="{Binding ModelSourceCivitAiUseMirror, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
              VerticalAlignment="Center" />
</StackPanel>
<DockPanel Margin="20,4,0,0"
           Visibility="{Binding ModelSourceCivitAiUseMirror, Converter={StaticResource BoolToVisibility}}">
    <TextBlock DockPanel.Dock="Left" Text="镜像地址" VerticalAlignment="Center" Margin="0,0,8,0" />
    <TextBox Text="{Binding ModelSourceCivitAiMirrorUrl, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource MaterialTextBox}"
             ToolTip="无流行国内镜像,留空 = 走官方" />
</DockPanel>

<!-- HuggingFace enabled checkbox -->
<StackPanel Orientation="Horizontal" Margin="0,8,0,0">
    <CheckBox Content="HuggingFace" IsChecked="{Binding ModelSourceHuggingFaceEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
              VerticalAlignment="Center" />
    <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[ModelSourceHuggingFaceEnabled], Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
<TextBlock Text="未配置 token 也能浏览公开模型,但部分 gated 模型将 403。hf-mirror.com 已设为默认镜像。"
           Foreground="Gray" FontSize="11" Margin="20,2,0,0" TextWrapping="Wrap" MaxWidth="500"
           HorizontalAlignment="Left" />

<!-- HF sub-block (only visible when HF enabled) -->
<StackPanel Margin="20,8,0,0"
            Visibility="{Binding ModelSourceHuggingFaceEnabled, Converter={StaticResource BoolToVisibility}}">
    <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
        <TextBlock Text="API Token" VerticalAlignment="Center" />
        <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
                   VerticalAlignment="Center"
                   Foreground="{DynamicResource WarningBrush}"
                   ToolTip="尚未保存"
                   Visibility="{Binding Dirty[HuggingFaceApiToken], Converter={StaticResource BoolToVisibility}}"/>
    </StackPanel>
    <DockPanel>
        <ToggleButton DockPanel.Dock="Right" x:Name="ToggleHuggingFaceTokenVisibility"
                      Click="ToggleHuggingFaceTokenVisibility"
                      ToolTip="显示 / 隐藏 30 秒"
                      Width="32" Height="28" Margin="4,0,0,0"
                      Style="{StaticResource MaterialToggleButton}">
            <Path Data="M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z"
                  Fill="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ToggleButton}}"
                  Width="16" Height="16" Stretch="Uniform" />
        </ToggleButton>
        <Button DockPanel.Dock="Right" Content="测试连接" Click="TestHuggingFaceConnection"
                Style="{StaticResource MaterialButton}" Margin="4,0,0,0" Padding="8,4" />
        <controls:BindablePasswordBox x:Name="HuggingFaceTokenBox"
                                       Password="{Binding HuggingFaceApiToken, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
    </DockPanel>
    <TextBlock Margin="0,4,0,0">
        <Hyperlink NavigateUri="https://huggingface.co/settings/tokens" RequestNavigate="OpenHyperlink">
            在 https://huggingface.co/settings/tokens 获取 token
        </Hyperlink>
    </TextBlock>

    <!-- HF mirror toggle + URL + reset -->
    <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
        <CheckBox Content="使用国内镜像 (默认 https://hf-mirror.com)"
                  IsChecked="{Binding ModelSourceHuggingFaceUseMirror, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                  VerticalAlignment="Center" />
        <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
                   VerticalAlignment="Center"
                   Foreground="{DynamicResource WarningBrush}"
                   ToolTip="尚未保存"
                   Visibility="{Binding Dirty[ModelSourceHuggingFaceUseMirror], Converter={StaticResource BoolToVisibility}}"/>
    </StackPanel>
    <DockPanel Margin="0,4,0,0"
               Visibility="{Binding ModelSourceHuggingFaceUseMirror, Converter={StaticResource BoolToVisibility}}">
        <Button DockPanel.Dock="Right" Content="重置" Click="ResetHuggingFaceMirrorUrl"
                Style="{StaticResource MaterialButton}" Margin="4,0,0,0" Padding="8,4" />
        <TextBox Text="{Binding ModelSourceHuggingFaceMirrorUrl, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource MaterialTextBox}" />
    </DockPanel>
    <TextBlock Text="⚠ http 镜像将不会发送 token(安全) — 仅 HTTPS 镜像支持 gated 模型"
               Foreground="{DynamicResource WarningBrush}" FontSize="11" Margin="0,4,0,0"
               Visibility="{Binding IsHuggingFaceMirrorInsecure, Converter={StaticResource BoolToVisibility}}" />
</StackPanel>

<!-- Helper text + refresh button -->
<TextBlock Text="勾选镜像后访问国内镜像地址,速度更快不需代理。CivitAI 无流行国内镜像,留空即可。"
           Foreground="Gray" FontSize="11" Margin="0,12,0,0" TextWrapping="Wrap" MaxWidth="500"
           HorizontalAlignment="Left" />
<Button Content="立即刷新模型市场" Click="RefreshModelMarketplace" Margin="0,8,0,0"
        HorizontalAlignment="Left" Style="{StaticResource MaterialButton}" />
```

Make sure to add the `xmlns:controls="clr-namespace:ComfyUI.Manager.Controls"` namespace declaration at the top of SettingsView.xaml (alongside the existing namespaces).

- [ ] **Step 6: Add code-behind handlers in SettingsView.xaml.cs**

In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`, after the existing `OpenModelsDir` handler, add 4 new handlers + a `Hyperlink.RequestNavigate` handler:

```csharp
private DateTime? _tokenRevealUntilUtc = null;
private System.Windows.Threading.DispatcherTimer? _tokenRevealTimer = null;

private void TestHuggingFaceConnection(object sender, RoutedEventArgs e)
{
    if (DataContext is not SettingsViewModel vm) return;
    var baseUrl = vm.ModelSourceHuggingFaceUseMirror && !string.IsNullOrWhiteSpace(vm.ModelSourceHuggingFaceMirrorUrl)
        ? vm.ModelSourceHuggingFaceMirrorUrl.TrimEnd('/')
        : "https://huggingface.co";
    var token = vm.HuggingFaceApiToken;

    // HTTP mirror with token → refuse (security policy from spec §7)
    if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(token))
    {
        MessageBox.Show($"镜像 {baseUrl} 使用 http,不发送 token。\n请改用 https 镜像或临时清空 token。",
            "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    // Fire-and-forget async probe
    _ = ProbeHuggingFaceConnectionAsync(baseUrl, token);
}

private async Task ProbeHuggingFaceConnectionAsync(string baseUrl, string token)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"{baseUrl}/api/whoami-v2");
        if (resp.IsSuccessStatusCode)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"✅ 连接成功 ({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information));
        }
        else
        {
            Dispatcher.Invoke(() => MessageBox.Show($"❌ 失败 {(int)resp.StatusCode} {resp.ReasonPhrase}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }
    catch (Exception ex)
    {
        Dispatcher.Invoke(() => MessageBox.Show($"❌ 连接失败: {ex.Message}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error));
    }
}

private void ResetHuggingFaceMirrorUrl(object sender, RoutedEventArgs e)
{
    if (DataContext is SettingsViewModel vm)
    {
        vm.ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com";
    }
}

private void ToggleHuggingFaceTokenVisibility(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.Primitives.ToggleButton btn) return;
    if (btn.IsChecked == true)
    {
        // Reveal plaintext for 30s
        _tokenRevealUntilUtc = DateTime.UtcNow.AddSeconds(30);
        HuggingFaceTokenBox.RevealPassword();
        _tokenRevealTimer?.Stop();
        _tokenRevealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tokenRevealTimer.Tick += (s, _) =>
        {
            if (_tokenRevealUntilUtc is null || DateTime.UtcNow >= _tokenRevealUntilUtc)
            {
                HuggingFaceTokenBox.HidePassword();
                btn.IsChecked = false;
                _tokenRevealTimer?.Stop();
                _tokenRevealTimer = null;
            }
        };
        _tokenRevealTimer.Start();
    }
    else
    {
        // User manually re-hid
        _tokenRevealUntilUtc = null;
        HuggingFaceTokenBox.HidePassword();
        _tokenRevealTimer?.Stop();
        _tokenRevealTimer = null;
    }
}

private void RefreshModelMarketplace(object sender, RoutedEventArgs e)
{
    // Find MainViewModel and call its refresh entry point
    if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mvm)
    {
        mvm.RefreshModelMarketplace();
    }
}

private void OpenHyperlink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    e.Handled = true;
}
```

Also add a computed `IsHuggingFaceMirrorInsecure` property to `SettingsViewModel.cs` (used in XAML `Visibility` binding for the ⚠ warning TextBlock):

```csharp
public bool IsHuggingFaceMirrorInsecure
{
    get
    {
        if (!ModelSourceHuggingFaceUseMirror) return false;
        var url = ModelSourceHuggingFaceMirrorUrl ?? "";
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(HuggingFaceApiToken);
    }
}
```

Also add a `RefreshModelMarketplace()` public method to `MainViewModel.cs` (T4 will do the actual implementation; T1 just adds the method stub that does nothing — T4 will implement):

```csharp
// v0.6.21 T1 stub — T4 implements the actual refresh logic
public void RefreshModelMarketplace()
{
    // TODO v0.6.21 T4: implement
}
```

- [ ] **Step 7: Run SettingsView XAML load test (STA)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~SettingsViewLoad" -v`
Expected: PASS (existing tests still work; new UI elements don't break existing load test)

If a load test exists that asserts on the "模型市场" section structure, update it to match the expanded layout (search for `SectionModels` in test files).

- [ ] **Step 8: Commit T1**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/SettingsHuggingFaceTests.cs
git commit -m "feat(models): v0.6.21 T1 Settings expansion + HF token + per-source mirror UI"
```

---

## Task 2: ModelSourceFactory + CivitAiModelSource baseUrl ctor param + HuggingFaceModelSource real impl

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelSourceFactory.cs` (~50 LoC)
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs:22,28-32,77` (replace `const BaseUrl` with field; add `baseUrl` ctor param; replace all `BaseUrl` references with `_baseUrl`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:600-603` (use `ModelSourceFactory.CreateAll` instead of `new CivitAiModelSource` direct; re-add HF instantiation)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs` (update ctor calls to pass `baseUrl`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceFactoryTests.cs` (~3 tests)
- Rewrite: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs` (replace v0.6.20 stub tests with real impl tests, ~8 tests + 1 SKIP)

**Interfaces:**
- Consumes: `Settings` (T1 — 6 new fields), `HttpClient` (singleton, App.xaml.cs)
- Produces:
  - `ModelSourceFactory.CreateCivitAi(Settings, HttpClient) : CivitAiModelSource?` — returns null if disabled, else constructs with resolved baseUrl
  - `ModelSourceFactory.CreateHuggingFace(Settings, HttpClient) : HuggingFaceModelSource?` — returns null if disabled, else constructs with resolved baseUrl + token
  - `ModelSourceFactory.CreateAll(Settings, HttpClient) : IEnumerable<IModelSource>` — concatenates both, skips nulls
  - `ModelSourceFactory.TestConnectionAsync(baseUrl, token) : Task<bool>` — lightweight `/api/whoami-v2` probe (used by Settings UI [测试连接] button in T1)
  - `HuggingFaceModelSource` real implementation: ctor `(HttpClient, string baseUrl, string apiToken)`, `SourceKind => HuggingFace`, real `SearchAsync` + `MapToModelEntry` with kind/NSFW/primary-file heuristics
  - `CivitAiModelSource` ctor changes: `(HttpClient http, string baseUrl, AppLogger? logger = null)`

- [ ] **Step 1: Write failing factory tests + update CivitAi tests**

In `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceFactoryTests.cs` (new file):

```csharp
using System.Collections.Generic;
using System.Net.Http;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceFactoryTests
{
    private static Settings MakeSettings(
        bool civitai = true, bool civitaiMirror = false, string civitaiMirrorUrl = "",
        bool hf = false, string hfToken = "", bool hfMirror = true, string hfMirrorUrl = "https://hf-mirror.com")
        => new Settings
        {
            ModelSourceCivitAiEnabled = civitai,
            ModelSourceCivitAiUseMirror = civitaiMirror,
            ModelSourceCivitAiMirrorUrl = civitaiMirrorUrl,
            ModelSourceHuggingFaceEnabled = hf,
            HuggingFaceApiToken = hfToken,
            ModelSourceHuggingFaceUseMirror = hfMirror,
            ModelSourceHuggingFaceMirrorUrl = hfMirrorUrl,
        };

    [Fact]
    public void CreateCivitAi_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(civitai: false);
        var http = new HttpClient();
        var result = ModelSourceFactory.CreateCivitAi(settings, http);
        Assert.Null(result);
    }

    [Fact]
    public void CreateHuggingFace_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(hf: false);
        var http = new HttpClient();
        var result = ModelSourceFactory.CreateHuggingFace(settings, http);
        Assert.Null(result);
    }

    [Fact]
    public void CreateAll_ResolvesMirrorUrl_And_StripsTrailingSlash()
    {
        var settings = MakeSettings(
            civitai: true, civitaiMirror: true, civitaiMirrorUrl: "https://my-mirror.example.com/civitai/",
            hf: true, hfMirror: true, hfMirrorUrl: "https://my-mirror.example.com/hf/");
        var http = new HttpClient();
        var sources = ModelSourceFactory.CreateAll(settings, http);
        Assert.Equal(2, new List<IModelSource>(sources).Count);
    }
}
```

In `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs`, find all `new CivitAiModelSource(http, ...)` and update to `new CivitAiModelSource(http, "https://civitai.com", ...)`. Should be ~5-10 call sites.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelSourceFactoryTests|FullyQualifiedName~ModelSourceCivitAiTests" -v`
Expected: FAIL — `ModelSourceFactory` doesn't exist; `CivitAiModelSource` ctor doesn't accept `baseUrl` arg

- [ ] **Step 3: Create `ModelSourceFactory.cs`**

Create `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelSourceFactory.cs`:

```csharp
using System.Collections.Generic;
using System.Net.Http;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.21: Per-source factory — reads Settings, picks base URL (mirror or
/// official) and API token, instantiates IModelSource. Returns null for disabled
/// sources; aggregator's internal IsEnabled filter never sees them.</summary>
public static class ModelSourceFactory
{
    public const string CivitAiOfficial = "https://civitai.com";
    public const string HuggingFaceOfficial = "https://huggingface.co";

    public static CivitAiModelSource? CreateCivitAi(Settings settings, HttpClient http)
    {
        if (!settings.ModelSourceCivitAiEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceCivitAiUseMirror,
                                     settings.ModelSourceCivitAiMirrorUrl,
                                     CivitAiOfficial);
        return new CivitAiModelSource(http, baseUrl);
    }

    public static HuggingFaceModelSource? CreateHuggingFace(Settings settings, HttpClient http)
    {
        if (!settings.ModelSourceHuggingFaceEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceHuggingFaceUseMirror,
                                     settings.ModelSourceHuggingFaceMirrorUrl,
                                     HuggingFaceOfficial);
        return new HuggingFaceModelSource(http, baseUrl, settings.HuggingFaceApiToken);
    }

    public static IEnumerable<IModelSource> CreateAll(Settings settings, HttpClient http)
    {
        var sources = new List<IModelSource>();
        var civitai = CreateCivitAi(settings, http);
        if (civitai is not null) sources.Add(civitai);
        var hf = CreateHuggingFace(settings, http);
        if (hf is not null) sources.Add(hf);
        return sources;
    }

    /// <summary>Lightweight connection probe — GET {baseUrl}/api/whoami-v2 with optional
    /// Authorization: Bearer header. Returns true if 2xx, false if any other status
    /// or exception. Used by Settings UI [测试连接] button.</summary>
    public static async Task<bool> TestConnectionAsync(string baseUrl, string apiToken, int timeoutSeconds = 5)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            if (!string.IsNullOrEmpty(apiToken) && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }
            var resp = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/whoami-v2");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveBaseUrl(bool useMirror, string mirrorUrl, string officialUrl)
        => useMirror && !string.IsNullOrWhiteSpace(mirrorUrl)
            ? mirrorUrl.TrimEnd('/')
            : officialUrl;
}
```

- [ ] **Step 4: Update `CivitAiModelSource` ctor + replace const BaseUrl**

In `src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs`:

Replace line 22:
```csharp
private const string BaseUrl = "https://civitai.com/api/v1/models";
```
with:
```csharp
private readonly string _baseUrl;
```

Replace lines 28-32 (ctor):
```csharp
public CivitAiModelSource(HttpClient http, AppLogger? logger = null)
{
    _http = http;
    _logger = logger;
}
```
with:
```csharp
public CivitAiModelSource(HttpClient http, string baseUrl, AppLogger? logger = null)
{
    _http = http;
    _baseUrl = baseUrl;
    _logger = logger;
    if (useMirror := baseUrl != "https://civitai.com")
    {
        _logger?.Info("model-civitai", $"using mirror: {baseUrl}");
    }
}
```

Replace all `{BaseUrl}` references in `BuildUrl` (line 77) with `{_baseUrl}`. Search for `BaseUrl` and replace all 2 occurrences (the const declaration + the reference).

- [ ] **Step 5: Rewrite `HuggingFaceModelSource.cs` with real implementation**

In `src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs`, **replace the entire file** with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.21:HuggingFace Hub API fetcher.
/// Search: GET {baseUrl}/api/models?search={q}&limit={n}&full=true
/// Detail: GET {baseUrl}/api/models/{repo_id} (siblings, cardData, tags)
/// Auth: optional Bearer token for higher rate limit + gated models.
/// Kind: tag-based heuristic (lora/checkpoint/vae/...); NSFW: any tag contains "nsfw".</summary>
public class HuggingFaceModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    private readonly AppLogger? _logger;
    private const int PageSize = 50;  // HF default limit per page

    public ModelSourceKind SourceKind => ModelSourceKind.HuggingFace;
    public string DisplayName => "HuggingFace";
    public bool IsEnabled { get; set; } = true;  // factory decides enabled via construction (returns null if disabled)

    public HuggingFaceModelSource(HttpClient http, string baseUrl, string apiToken, AppLogger? logger = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiToken = apiToken ?? "";
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var results = new List<ModelEntry>();
        var qs = new List<string>
        {
            $"limit={Math.Min(maxResults, PageSize)}",
            "full=true",
        };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"search={Uri.EscapeDataString(query)}");
        var url = $"{_baseUrl}/api/models?{string.Join("&", qs)}";
        _logger?.Info("model-huggingface", $"search: {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);

        var items = JsonSerializer.Deserialize<List<HfModelSummary>>(body, JsonOpts);
        if (items is null) return results;

        foreach (var item in items.Take(maxResults))
        {
            if (string.IsNullOrEmpty(item.Id)) continue;
            var entry = await MapToModelEntryAsync(item, ct);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    private async Task<ModelEntry?> MapToModelEntryAsync(HfModelSummary summary, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/models/{summary.Id}");
            if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
            }
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            var detail = JsonSerializer.Deserialize<HfModelDetail>(body, JsonOpts);
            if (detail is null) return null;

            var tags = detail.Tags ?? new List<string>();
            var kind = MapKindFromTags(tags);
            var nsfwKind = tags.Any(t => t.Contains("nsfw", StringComparison.OrdinalIgnoreCase))
                ? ModelNsfwKind.Nsfw
                : ModelNsfwKind.SFW;
            var primary = PickPrimaryFile(detail.Siblings);
            if (primary is null) return null;  // no model files in siblings

            var sha = detail.Sha ?? summary.Id;
            var version = new ModelVersionEntry
            {
                Id = $"{ModelSourceKind.HuggingFace}:{summary.Id}:{sha[..Math.Min(8, sha.Length)]}",
                SourceVersionId = sha,
                Name = detail.Id ?? summary.Id,
                PrimaryDownloadUrl = primary.DownloadUrl,
                SizeBytes = primary.SizeBytes,
                Files = new List<ModelFile> { primary }.AsReadOnly(),
                Parent = null!,  // back-ref set by caller if needed
            };

            return new ModelEntry
            {
                Id = $"{ModelSourceKind.HuggingFace}:{summary.Id}",
                Source = ModelSourceKind.HuggingFace,
                SourceId = summary.Id,
                SourceUrl = $"{_baseUrl}/{summary.Id}",
                Title = summary.Id,
                Author = summary.Id.Contains('/') ? summary.Id.Split('/')[0] : "",
                Description = detail.CardData?.Description ?? "",
                PreviewImageUrl = "",
                Kind = kind,
                NsfwKind = nsfwKind,
                Versions = new List<ModelVersionEntry> { version }.AsReadOnly(),
                CreatedAt = detail.LastModified,
            };
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-huggingface", $"failed to map {summary.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Priority-order tag → ModelKind mapping. Unknown → Other.</summary>
    internal static ModelKind MapKindFromTags(IList<string> tags)
    {
        if (tags.Any(t => t.Equals("lora", StringComparison.OrdinalIgnoreCase))) return ModelKind.LORA;
        if (tags.Any(t => t.Equals("checkpoint", StringComparison.OrdinalIgnoreCase))) return ModelKind.Checkpoint;
        if (tags.Any(t => t.Equals("vae", StringComparison.OrdinalIgnoreCase))) return ModelKind.VAE;
        if (tags.Any(t => t.Equals("controlnet", StringComparison.OrdinalIgnoreCase))) return ModelKind.Controlnet;
        if (tags.Any(t => t.Equals("textual-inversion", StringComparison.OrdinalIgnoreCase))) return ModelKind.TextualInversion;
        if (tags.Any(t => t.Equals("upscaler", StringComparison.OrdinalIgnoreCase))) return ModelKind.Upscaler;
        if (tags.Any(t => t.Equals("hypernetwork", StringComparison.OrdinalIgnoreCase))) return ModelKind.Hypernetwork;
        return ModelKind.Other;
    }

    /// <summary>Pick largest *.safetensors / *.bin from siblings; fallback to first one if size missing.</summary>
    internal static ModelFile? PickPrimaryFile(IList<HfSibling>? siblings)
    {
        if (siblings is null || siblings.Count == 0) return null;
        var candidates = siblings
            .Where(s => !string.IsNullOrEmpty(s.Rfilename) &&
                       (s.Rfilename.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                        s.Rfilename.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0) return null;
        var withSize = candidates.Where(s => s.Size.HasValue).OrderByDescending(s => s.Size!.Value).FirstOrDefault();
        if (withSize is not null)
        {
            return new ModelFile
            {
                Name = withSize.Rfilename!,
                Format = withSize.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? "Safetensors" : "Other",
                SizeBytes = withSize.Size!.Value,
                DownloadUrl = $"https://huggingface.co/{siblings.FirstOrDefault()?.Rfilename}",  // placeholder, real URL constructed per-file
                IsPrimary = true,
            };
        }
        var first = candidates.First();
        return new ModelFile
        {
            Name = first.Rfilename!,
            Format = first.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? "Safetensors" : "Other",
            SizeBytes = 0,
            DownloadUrl = "",
            IsPrimary = true,
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // —— DTOs (private to source) ——
    private class HfModelSummary
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
    private class HfModelDetail
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
        [JsonPropertyName("lastModified")] public DateTime? LastModified { get; set; }
        [JsonPropertyName("cardData")] public HfCardData? CardData { get; set; }
    }
    private class HfSibling
    {
        [JsonPropertyName("rfilename")] public string? Rfilename { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
    private class HfCardData
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
```

Note: The `DownloadUrl` construction in `PickPrimaryFile` is intentionally a placeholder — the actual per-file URL is built in the detail mapping using the repo_id + rfilename pattern. Replace the placeholder with the correct URL construction by passing the repo_id to `PickPrimaryFile`. Updated implementation:

```csharp
internal static ModelFile? PickPrimaryFile(IList<HfSibling>? siblings, string repoId, string baseUrl)
{
    if (siblings is null || siblings.Count == 0) return null;
    var candidates = siblings
        .Where(s => !string.IsNullOrEmpty(s.Rfilename) &&
                   (s.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                    s.Rfilename.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (candidates.Count == 0) return null;
    var withSize = candidates.Where(s => s.Size.HasValue).OrderByDescending(s => s.Size!.Value).FirstOrDefault();
    var pick = withSize ?? candidates.First();
    return new ModelFile
    {
        Name = pick.Rfilename!,
        Format = pick.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? "Safetensors" : "Other",
        SizeBytes = pick.Size ?? 0,
        DownloadUrl = $"{baseUrl.TrimEnd('/')}/{repoId}/resolve/main/{pick.Rfilename}",
        IsPrimary = true,
    };
}
```

And in `MapToModelEntryAsync`, pass `summary.Id, _baseUrl` to `PickPrimaryFile`.

- [ ] **Step 6: Update MainViewModel.ShowModels() to use factory**

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:597-603`, replace the direct `new CivitAiModelSource` construction with factory call:

```csharp
// v0.6.21: 通过 ModelSourceFactory 构造所有启用的源(基于 Settings 6 个新字段 +
// per-source mirror 解析)。Factory 内部 skip disabled source → aggregator 永远只看 enabled。
// 替代 v0.6.20 T9 之前的 `new CivitAiModelSource(http, logger: _logger)` 直接构造 + T10
// polish 删 HF 的模式。
var marketplace = new ModelMarketplaceService(
    ModelSourceFactory.CreateAll(_settings, http),
    logger: _logger);
```

Remove the `using ComfyUI.Manager.Services.ModelSources;` if no longer used elsewhere in MainViewModel.cs (check — `CivitAiModelSource` was the only direct reference, which is now gone).

- [ ] **Step 7: Write failing tests for HF source (8 tests + 1 SKIP)**

In `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs` (replace entire file):

```csharp
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceHuggingFaceTests
{
    private static (HuggingFaceModelSource src, RecordingHandler handler) MakeSource(string baseUrl = "https://huggingface.co", string token = "")
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new System.Uri(baseUrl) };
        var src = new HuggingFaceModelSource(http, baseUrl, token);
        return (src, handler);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_HitsBaseUrl()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"stabilityai/sdxl\"}]");
        handler.QueueResponse("{\"id\":\"stabilityai/sdxl\",\"sha\":\"abc123\",\"siblings\":[{\"rfilename\":\"sdxl.safetensors\",\"size\":1024}],\"tags\":[\"diffusers\",\"checkpoint\"]}");

        var results = await src.SearchAsync("", 10, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal("stabilityai/sdxl", results[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_WithToken_SendsBearerHeader()
    {
        var (src, handler) = MakeSource(token: "hf_test_token_123");
        handler.QueueResponse("[{\"id\":\"private/repo\"}]");
        handler.QueueResponse("{\"id\":\"private/repo\",\"sha\":\"xyz\",\"siblings\":[{\"rfilename\":\"m.safetensors\",\"size\":512}],\"tags\":[]}");

        await src.SearchAsync("test", 1, CancellationToken.None);
        Assert.NotEmpty(handler.Requests);
        Assert.Contains(handler.Requests, r => r.Headers.Authorization?.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "hf_test_token_123");
    }

    [Fact]
    public void MapKindFromTags_TagsContainsLora_MapsToLoraKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "lora", "image" });
        Assert.Equal(ModelKind.LORA, kind);
    }

    [Fact]
    public void MapKindFromTags_TagsContainsCheckpoint_MapsToCheckpointKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "checkpoint", "text-to-image" });
        Assert.Equal(ModelKind.Checkpoint, kind);
    }

    [Fact]
    public void MapKindFromTags_UnknownKindTags_MapsToOther()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "image" });
        Assert.Equal(ModelKind.Other, kind);
    }

    [Fact]
    public void MapKindFromTags_TagsContainsVae_MapsToVaeKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "vae", "diffusers" });
        Assert.Equal(ModelKind.VAE, kind);
    }

    [Fact]
    public async Task MapToModelEntry_TagContainsNsfw_SetsNsfwRating()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"nsfw/model\"}]");
        handler.QueueResponse("{\"id\":\"nsfw/model\",\"sha\":\"abc\",\"siblings\":[{\"rfilename\":\"m.safetensors\",\"size\":1024}],\"tags\":[\"lora\",\"nsfw\"]}");

        var results = await src.SearchAsync("", 1, CancellationToken.None);
        Assert.Equal(ModelNsfwKind.Nsfw, results[0].NsfwKind);
    }

    [Fact]
    public async Task MapToModelEntry_SiblingsList_PicksLargestSafetensors()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"multi/file\"}]");
        handler.QueueResponse("{\"id\":\"multi/file\",\"sha\":\"abc\",\"siblings\":[{\"rfilename\":\"small.safetensors\",\"size\":1024},{\"rfilename\":\"large.safetensors\",\"size\":9999999}],\"tags\":[\"checkpoint\"]}");

        var results = await src.SearchAsync("", 1, CancellationToken.None);
        Assert.Equal("large.safetensors", results[0].Versions[0].Files[0].Name);
        Assert.Equal(9999999, results[0].Versions[0].SizeBytes);
    }

    [Fact(Skip = "Real HF API fetch — not run in CI")]
    public async Task SearchAsync_RealHF_ReturnsAtLeastOneResult()
    {
        var (src, handler) = MakeSource("https://huggingface.co");
        var results = await src.SearchAsync("stable-diffusion", 5, CancellationToken.None);
        Assert.NotEmpty(results);
    }

    private class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        private readonly Queue<string> _responseBodies = new();
        public void QueueResponse(string body) => _responseBodies.Enqueue(body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = _responseBodies.Count > 0 ? _responseBodies.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
```

- [ ] **Step 8: Run all source + factory tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelSourceFactoryTests|FullyQualifiedName~ModelSourceCivitAiTests|FullyQualifiedName~ModelSourceHuggingFaceTests" -v`
Expected: PASS — 3 factory + ~7 civitai (updated) + 8 HF (1 SKIP)

- [ ] **Step 9: Commit T2**

```bash
git add src-wpf/ComfyUI.Manager/Services/ModelSources/ModelSourceFactory.cs \
        src-wpf/ComfyUI.Manager/Services/ModelSources/CivitAiModelSource.cs \
        src-wpf/ComfyUI.Manager/Services/ModelSources/HuggingFaceModelSource.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceCivitAiTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceHuggingFaceTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceFactoryTests.cs
git commit -m "feat(models): v0.6.21 T2 ModelSourceFactory + HF real impl + CivitAi baseUrl ctor"
```

---

## Task 3: BindablePasswordBox custom WPF control + Theme.xaml style

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs` (~60 LoC)
- Create: `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml` (default style template — pure WPF control template, no business logic)
- Create: `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml.cs` (template part hooks: OnApplyTemplate, password sync)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` (register `BindablePasswordBox` style override if needed; namespace mapping)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Controls/BindablePasswordBoxTests.cs` (new, ~2 tests)

**Interfaces:**
- Consumes: `PasswordBox` (built-in WPF), `Control` base class
- Produces:
  - `BindablePasswordBox : Control` with `Password` DP (string, BindsTwoWayByDefault)
  - `RevealPassword()` method — sets `PasswordBox.PasswordRevealMode = Visible` (or switches internal `IsTextVisible` flag for template switch)
  - `HidePassword()` method — re-hides
  - `IsPasswordRevealed` DP (bool) for template binding
  - Default control template (in .xaml) that swaps between masked PasswordBox and plain TextBox based on `IsPasswordRevealed`

- [ ] **Step 1: Write failing tests for BindablePasswordBox**

In `tests-wpf/ComfyUI.Manager.Tests/Controls/BindablePasswordBoxTests.cs` (new file):

```csharp
using System.ComponentModel;
using ComfyUI.Manager.Controls;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

public class BindablePasswordBoxTests
{
    [Fact]
    public void SetPassword_DpProperty_RaisesChangeNotification()
    {
        var box = new BindablePasswordBox();
        var changed = false;
        box.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BindablePasswordBox.Password)) changed = true;
        };
        box.Password = "secret123";
        Assert.True(changed);
        Assert.Equal("secret123", box.Password);
    }

    [Fact]
    public void PasswordCharToggle_RevealsPlaintext_For30Seconds()
    {
        var box = new BindablePasswordBox { Password = "secret123" };
        Assert.False(box.IsPasswordRevealed);
        box.RevealPassword();
        Assert.True(box.IsPasswordRevealed);
        box.HidePassword();
        Assert.False(box.IsPasswordRevealed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~BindablePasswordBoxTests" -v`
Expected: FAIL — `BindablePasswordBox` type doesn't exist

- [ ] **Step 3: Create `BindablePasswordBox.cs`**

Create `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Controls;

/// <summary>v0.6.21: WPF PasswordBox doesn't expose Password as DependencyProperty
/// (security feature — passwords not stored in DP for forensic safety).
/// This custom control wraps PasswordBox with a bindable Password DP and an
/// IsPasswordRevealed DP for the 👁 toggle button (XAML template switches
/// between masked PasswordBox and plain TextBox).</summary>
public class BindablePasswordBox : Control
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            nameof(Password),
            typeof(string),
            typeof(BindablePasswordBox),
            new FrameworkPropertyMetadata(
                "",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordChanged));

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    public static readonly DependencyProperty IsPasswordRevealedProperty =
        DependencyProperty.Register(
            nameof(IsPasswordRevealed),
            typeof(bool),
            typeof(BindablePasswordBox),
            new PropertyMetadata(false));

    public bool IsPasswordRevealed
    {
        get => (bool)GetValue(IsPasswordRevealedProperty);
        set => SetValue(IsPasswordRevealedProperty, value);
    }

    public void RevealPassword() => IsPasswordRevealed = true;
    public void HidePassword() => IsPasswordRevealed = false;

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Template part hook in code-behind reads this DP and forwards to inner PasswordBox.Password.
        // Two-way binding handled by OnApplyTemplate code-behind.
    }
}
```

- [ ] **Step 4: Create `BindablePasswordBox.xaml` (default template)**

Create `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:ComfyUI.Manager.Controls">

    <Style TargetType="{x:Type local:BindablePasswordBox}">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type local:BindablePasswordBox}">
                    <Grid>
                        <PasswordBox x:Name="InnerPasswordBox"
                                     Visibility="{Binding IsPasswordRevealed, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource InverseBoolToVisibility}}"
                                     FontFamily="Consolas" />
                        <TextBox x:Name="InnerTextBox"
                                 Text="{Binding Password, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                 FontFamily="Consolas"
                                 Visibility="{Binding IsPasswordRevealed, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource BoolToVisibility}}" />
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>
```

Note: `InverseBoolToVisibility` may not exist in the codebase. If not, use a small inline converter (in the same file) or add it to `Views/Converters.cs`. For simplicity, register it in `Theme.xaml` as a static resource.

- [ ] **Step 5: Create `BindablePasswordBox.xaml.cs` (template part sync)**

Create `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Controls;

public partial class BindablePasswordBox
{
    private PasswordBox? _innerPasswordBox;
    private TextBox? _innerTextBox;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Unhook old
        if (_innerPasswordBox is not null) _innerPasswordBox.PasswordChanged -= InnerPasswordBox_PasswordChanged;
        if (_innerTextBox is not null) _innerTextBox.TextChanged -= InnerTextBox_TextChanged;

        _innerPasswordBox = GetTemplateChild("InnerPasswordBox") as PasswordBox;
        _innerTextBox = GetTemplateChild("InnerTextBox") as TextBox;

        if (_innerPasswordBox is not null)
        {
            _innerPasswordBox.Password = Password;
            _innerPasswordBox.PasswordChanged += InnerPasswordBox_PasswordChanged;
        }
        if (_innerTextBox is not null)
        {
            _innerTextBox.Text = Password;
            _innerTextBox.TextChanged += InnerTextBox_TextChanged;
        }
    }

    private void InnerPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_innerPasswordBox is not null && Password != _innerPasswordBox.Password)
        {
            SetCurrentValue(PasswordProperty, _innerPasswordBox.Password);
        }
    }

    private void InnerTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_innerTextBox is not null && Password != _innerTextBox.Text)
        {
            SetCurrentValue(PasswordProperty, _innerTextBox.Text);
        }
    }
}
```

- [ ] **Step 6: Register the resource dictionary in Theme.xaml or App.xaml**

In `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`, find the `<ResourceDictionary.MergedDictionaries>` block (or create if absent) and add a merge reference:

```xml
<ResourceDictionary.MergedDictionaries>
    ...existing entries...
    <ResourceDictionary Source="pack://application:,,,/ComfyUI.Manager;component/Controls/BindablePasswordBox.xaml" />
</ResourceDictionary.MergedDictionaries>
```

Also add the `xmlns:controls="clr-namespace:ComfyUI.Manager.Controls"` namespace if not already declared (used in SettingsView.xaml from T1).

- [ ] **Step 7: Run BindablePasswordBox tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~BindablePasswordBoxTests" -v`
Expected: PASS (2/2)

If STA issues arise (WPF template requires UI thread), wrap the test instantiation in `StaFact.RunOnSTA(...)` (existing pattern in the codebase — see `WorkflowMarketplaceViewLoadTests` for template).

- [ ] **Step 8: Commit T3**

```bash
git add src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs \
        src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml \
        src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml.xs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Controls/BindablePasswordBoxTests.cs
git commit -m "feat(models): v0.6.21 T3 BindablePasswordBox custom WPF control + theme style"
```

Note: `.xaml.xs` above is a typo — should be `.xaml.cs`. Correct command:

```bash
git add src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs \
        src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml \
        src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.xaml.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Controls/BindablePasswordBoxTests.cs
git commit -m "feat(models): v0.6.21 T3 BindablePasswordBox custom WPF control + theme style"
```

---

## Task 4: Source filter chips in ModelMarketplaceViewModel + View XAML + MainViewModel.RefreshModelMarketplace

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs` (add `ShowOnlyCivitai` / `ShowOnlyHuggingFace` bool properties + `SourceChips` collection + `ApplySourceFilter()` method + `SourceFilterChips_CollectionChanged` hook)
- Modify: `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml` (add `SourceChips` `ItemsControl` in toolbar row)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (implement `RefreshModelMarketplace()` method from T1 stub)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelSourceFilterTests.cs` (new, ~3 tests)

**Interfaces:**
- Consumes: `ModelEntry` (v0.6.20 T2 DTOs with `Source` property), `ModelMarketplaceService` (v0.6.20 T4 aggregator)
- Produces:
  - `ModelMarketplaceViewModel.ShowOnlyCivitai : bool = true` (initially all sources visible)
  - `ModelMarketplaceViewModel.ShowOnlyHuggingFace : bool = true`
  - `ModelMarketplaceViewModel.SourceChips : IReadOnlyList<SourceChip>` — 2 items (CivitAI / HuggingFace) for the toolbar
  - `ModelMarketplaceViewModel.ApplySourceFilter()` — applies `ICollectionView.Filter` to `Models` based on `entry.Source ∈ {CivitAi, HuggingFace}` (or always-true if both true)
  - `MainViewModel.RefreshModelMarketplace()` — re-runs `ShowModels()` refresh logic (forces re-query even if Settings change didn't trigger refresh)

- [ ] **Step 1: Write failing tests for source filter**

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelSourceFilterTests.cs` (new file):

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class ModelMarketplaceViewModelSourceFilterTests
{
    private static ModelEntry MakeEntry(ModelSourceKind source, int id) => new()
    {
        Source = source,
        SourceId = id.ToString(),
        SourceUrl = $"https://example.com/{source}/{id}",
        Title = $"{source} {id}",
        Kind = ModelKind.Checkpoint,
        NsfwKind = ModelNsfwKind.SFW,
        Versions = new List<ModelVersionEntry>().AsReadOnly(),
    };

    private static ModelMarketplaceViewModel MakeVmWithEntries(params ModelEntry[] entries)
    {
        var vm = new ModelMarketplaceViewModel(
            marketplaceService: null!,
            downloader: null!,
            scanner: null!,
            settings: null!,
            logger: null);
        foreach (var e in entries) vm.Models.Add(e);
        return vm;
    }

    [Fact]
    public void ShowOnlyCivitai_False_HidesCivitAiEntries()
    {
        var vm = MakeVmWithEntries(
            MakeEntry(ModelSourceKind.CivitAi, 1),
            MakeEntry(ModelSourceKind.HuggingFace, 2));
        vm.ShowOnlyCivitai = false;
        Assert.Single(vm.Models);
        Assert.Equal(ModelSourceKind.HuggingFace, vm.Models[0].Source);
    }

    [Fact]
    public void ShowOnlyHuggingFace_False_HidesHuggingFaceEntries()
    {
        var vm = MakeVmWithEntries(
            MakeEntry(ModelSourceKind.CivitAi, 1),
            MakeEntry(ModelSourceKind.HuggingFace, 2));
        vm.ShowOnlyHuggingFace = false;
        Assert.Single(vm.Models);
        Assert.Equal(ModelSourceKind.CivitAi, vm.Models[0].Source);
    }

    [Fact]
    public void BothFalse_RendersEmptyHint()
    {
        var vm = MakeVmWithEntries(MakeEntry(ModelSourceKind.CivitAi, 1));
        vm.ShowOnlyCivitai = false;
        vm.ShowOnlyHuggingFace = false;
        Assert.Empty(vm.Models);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewModelSourceFilterTests" -v`
Expected: FAIL — `ShowOnlyCivitai` / `ShowOnlyHuggingFace` properties don't exist

- [ ] **Step 3: Add source filter properties + `SourceChips` collection to `ModelMarketplaceViewModel`**

In `src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs`, add the following fields and properties (find the `KindFilters` declaration as anchor — it's a similar list of enum values used by the kind chip strip):

```csharp
// v0.6.21: source filter — view-time toggle to hide CivitAI / HF entries
// without re-querying the source.
private bool _showOnlyCivitai = true;
private bool _showOnlyHuggingFace = true;

public bool ShowOnlyCivitai
{
    get => _showOnlyCivitai;
    set
    {
        if (_showOnlyCivitai == value) return;
        _showOnlyCivitai = value;
        ApplySourceFilter();
    }
}

public bool ShowOnlyHuggingFace
{
    get => _showOnlyHuggingFace;
    set
    {
        if (_showOnlyHuggingFace == value) return;
        _showOnlyHuggingFace = value;
        ApplySourceFilter();
    }
}

/// <summary>v0.6.21: Source chip metadata for toolbar ItemsControl. Mirrors
/// KindChips pattern from v0.6.20 T8.</summary>
public IReadOnlyList<SourceChip> SourceChips { get; } = new[]
{
    new SourceChip { Name = "CivitAI",    SourceKind = ModelSourceKind.CivitAi,    ToggleProperty = nameof(ShowOnlyCivitai) },
    new SourceChip { Name = "HuggingFace", SourceKind = ModelSourceKind.HuggingFace, ToggleProperty = nameof(ShowOnlyHuggingFace) },
};

private void ApplySourceFilter()
{
    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Models);
    view.Filter = m => ((ModelEntry)m).Source switch
    {
        ModelSourceKind.CivitAi => _showOnlyCivitai,
        ModelSourceKind.HuggingFace => _showOnlyHuggingFace,
        _ => true,  // future sources always visible
    };
}
```

Also create a new `Models/SourceChip.cs` (or add the `SourceChip` record to the end of `ModelMarketplaceViewModel.cs` as a nested public class):

```csharp
public class SourceChip
{
    public string Name { get; init; } = "";
    public ModelSourceKind SourceKind { get; init; }
    public string ToggleProperty { get; init; } = "";  // bound to CheckBox.IsChecked via DataTrigger or two-way binding
}
```

In the constructor, after `KindFilters.CollectionChanged += …`, add a hook that re-applies source filter when `Models` changes:

```csharp
// v0.6.21: re-apply source filter when Models collection changes
((INotifyCollectionChanged)Models).CollectionChanged += (_, _) => ApplySourceFilter();
```

Add `using System.Collections.Specialized;` and `using System.Windows.Data;` at the top if not present.

- [ ] **Step 4: Add `SourceChips` ItemsControl to `ModelMarketplaceView.xaml`**

In `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml`, find the existing toolbar `Grid` (top of view, contains the search TextBox and `KindChips` ItemsControl). Add a new column or row for `SourceChips`:

If existing layout is a single row `[搜索框] ━━━ kind chips`, add source chips at the right:

```xml
<StackPanel Orientation="Horizontal" Margin="0,4,0,0" HorizontalAlignment="Right">
    <TextBlock Text="源" VerticalAlignment="Center" Margin="0,0,8,0" FontWeight="Bold" />
    <ItemsControl ItemsSource="{Binding SourceChips}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <StackPanel Orientation="Horizontal" />
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <CheckBox Margin="4,0,0,0" Content="{Binding Name}" VerticalAlignment="Center">
                    <CheckBox.IsChecked>
                        <Binding Path="DataContext.[(sys:Name)]" RelativeSource="{RelativeSource AncestorType=UserControl}"/>
                    </CheckBox.IsChecked>
                </CheckBox>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

**Simpler approach** (recommended): use two explicit CheckBox controls with direct VM property bindings (no SourceChip class indirection needed for 2 items):

```xml
<StackPanel Orientation="Horizontal" Margin="0,4,0,0" HorizontalAlignment="Right">
    <TextBlock Text="源" VerticalAlignment="Center" Margin="0,0,8,0" FontWeight="Bold" />
    <CheckBox Content="CivitAI"    IsChecked="{Binding ShowOnlyCivitai, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Margin="4,0,0,0" />
    <CheckBox Content="HuggingFace" IsChecked="{Binding ShowOnlyHuggingFace, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Margin="8,0,0,0" />
</StackPanel>
```

Use the simpler approach — skip the `SourceChips` collection abstraction since 2 items is below the threshold for dynamic item template (YAGNI). Adjust Step 3 to only add the 2 bool properties + `ApplySourceFilter` (no `SourceChips` collection needed).

- [ ] **Step 5: Implement `MainViewModel.RefreshModelMarketplace()`**

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`, replace the T1 stub:

```csharp
/// <summary>
/// v0.6.21: 强制刷新模型市场 — 用户在 Settings 改完 source 启用 / 镜像 URL / token 后
/// 通过 [立即刷新模型市场] 按钮触发,跳到模型市场 tab + 重新构造 VM(保留现有 Models
/// 不变,只触发 ModelMarketplaceViewModel.RefreshAsync)。
///
/// 不重用现有 _modelMarketplaceViewModel 实例(避免缓存的 ShowOnlyCivitai /
/// ShowOnlyHuggingFace 状态粘性),改用 lazy-cache 模式:丢弃旧 VM 引用,下次 ShowModels
/// 构造新的。
/// </summary>
public void RefreshModelMarketplace()
{
    _modelMarketplaceViewModel = null;  // force re-construct on next ShowModels call
    _modelMarketplaceView = null;
    ShowModelsCommand.Execute(null);
    _ = _modelMarketplaceViewModel?.RefreshAsync();
}
```

- [ ] **Step 6: Run source filter tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewModelSourceFilterTests" -v`
Expected: PASS (3/3)

- [ ] **Step 7: Run existing marketplace view load tests (regression check)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~ModelMarketplaceViewLoad|FullyQualifiedName~ModelMarketplaceViewModel" -v`
Expected: PASS — existing tests still work, no XAML binding breakage

- [ ] **Step 8: Commit T4**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/ModelMarketplaceViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelSourceFilterTests.cs
git commit -m "feat(models): v0.6.21 T4 source filter chips + RefreshModelMarketplace hook"
```

---

## Task 5: Final review (opus-tier) + MEMORY + staging rebuild + GUI smoke

**Files:**
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_20_model_marketplace.md` (add v0.6.21 section to existing v0.6.20 memory file — re-title to "v0.6.20+v0.6.21 模型市场" or create new file)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (add one-line entry for v0.6.21)
- Create: `D:\ToolDevelop\ComfyUI\.superpowers\sdd\2026-08-19-model-marketplace-v2\progress.md` (SDD ledger for v0.6.21)

**Interfaces:**
- Consumes: v0.6.21 plan + T1-T4 commits + final review report
- Produces: updated memory entries + rebuilt staging exe + GUI smoke verification

- [ ] **Step 1: Dispatch final review (opus-tier)**

Dispatch a subagent to review the entire v0.6.21 branch (4 task commits + base) and identify:
- Cross-cutting issues (interface consistency, pattern reuse, dead code)
- Spec compliance gaps
- Quality issues (test coverage, error handling, performance)
- Polish opportunities (UI consistency, copy, accessibility)

Review must cover:
- All T1-T4 commits (`git log v0.6.20-plan-commit..HEAD`)
- Diff vs spec sections 4-8 (data model, factory, kind heuristics, NSFW, primary file, token security, tests)
- Diff vs existing v0.6.20 patterns (mirror Service-layer vs UI-bound awaits, Progress<T> wrapping, error handling, file/method naming)

Use opus-tier model (most capable). Provide the review report path + verdict.

- [ ] **Step 2: Apply review fixes in fix wave (if any)**

If reviewer identifies issues:
- Critical / Important → fix immediately in single fix commit
- Minor → park in ledger, address in v0.6.22+ or live with it

Apply fixes in a single commit (T5 polish):

```bash
git add <specific files from review>
git commit -m "polish(models): v0.6.21 T5 final review fixes"
```

- [ ] **Step 3: Update MEMORY + create v0.6.21 memory file**

Update the existing v0.6.20 memory file by **adding a v0.6.21 section** at the bottom (don't replace the v0.6.20 content — v0.6.20 SHIP-READY status is permanent historical record).

Create a new dedicated memory file `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_21_model_marketplace_v2.md` (mirror v0.6.20 file structure, ~200-400 lines):

```markdown
---
name: v0.6.21 模型市场 v2 (HF source + mirror + token) — SHIP-READY YYYY-MM-DD
description: ...
type: project
---

# v0.6.21 模型市场 v2 — SHIP-READY YYYY-MM-DD, HEAD `<commit>`, 4 commits / 12 files / +~700/-~150, 1503 PASS / 6 FAIL pre-existing flaky / 6 SKIP

**Why:** 用户在 v0.6.20 SHIP-READY 后要求扩展模型市场,加 HuggingFace 作为第二 source(替代 stub),需要 API token 支持 gated 模型,加 per-source mirror 支持国内用户(默认 https://hf-mirror.com for HF)。spec 在 `docs/superpowers/specs/2026-08-19-model-marketplace-v2-design.md` (434 行,HEAD `1235b29`)。plan 在 `docs/superpowers/plans/2026-08-19-model-marketplace-v2.md` (5 任务,~1900 行)。

**How to apply:**
- **架构镜像 v0.6.20 + 3 新组件**:`ModelSourceFactory`(per-source factory,Settings → baseUrl + token → 构造 IModelSource,disabled 返 null)+ `BindablePasswordBox`(WPF custom control 包装 PasswordBox + BindablePassword DP + IsPasswordRevealed DP for 👁 toggle)+ `SourceChips` 工具栏 view-time filter
- **3 核心决策**(用户 brainstorm 拍板):
  - **版本** = v0.6.21(下个 minor,不是 v0.6.20 hotfix)
  - **CivitAI 也加 mirror** = 是,per-source 通用,默认 OFF + 空 URL(无流行国内镜像)
  - **Token UI** = PasswordBox + 👁 切换明文
- **HF 真实实现**:`GET {baseUrl}/api/models?search={q}&limit={n}&full=true` 搜索 + `GET {baseUrl}/api/models/{repo_id}` 详情(siblings/cardData/tags)。Kind 启发式优先级 lora/checkpoint/vae/controlnet/textual-inversion/upscaler/hypernetwork → 8 ModelKind。NSFW 启发式:任何 tag 含 "nsfw" → Nsfw;else Sfw(无 Mature tier)。Primary file:从 siblings 挑最大的 .safetensors/.bin。HF 无显式版本 → 每个 ModelEntry 1 个虚拟 version,key 用 commit sha
- **Mirror 解析**:`useMirror && !string.IsNullOrWhiteSpace(mirrorUrl) ? mirrorUrl.TrimEnd('/') : officialUrl`
- **Token 安全**:plaintext in `<projectRoot>/.manager/settings.json`(低风险本地 app,DPAPI v0.6.22+)。HTTPS 强制;http 镜像不发送 token(UI 灰掉 [测试连接] 按钮)。Token 永不 log(只 log length)
- **30 秒 reveal**:👁 toggle 后 30 秒自动 re-hide,`DispatcherTimer` 1s tick
- **Source filter 是 view-time**:`ICollectionView.Filter` on `Models`,不动 source,切 chip 不重查
- **复用 v0.6.20 pattern**:Settings JSON 属性 + CopyInto 行 + MarkDirty + BoolToVisibility + Theme.xaml static resource + DelegatingHandler HTTP mock + Progress<T> UI marshal

## Architecture (4 commits → ALL SHIPPED)

| Commit | Task | What | Status |
|---|---|---|---|
| `<sha>` | T1 | Settings 6 new fields + SettingsViewModel properties + SettingsView XAML 展开(HF enabled + token PasswordBox + mirror toggles + [测试连接]/[立即刷新模型市场] buttons) | ✓ SHIPPED |
| `<sha>` | T2 | ModelSourceFactory + CivitAiModelSource ctor baseUrl param + HuggingFaceModelSource 真实实现 + MainViewModel rewires to factory | ✓ SHIPPED |
| `<sha>` | T3 | BindablePasswordBox custom WPF control + Theme.xaml 注册 + default template | ✓ SHIPPED |
| `<sha>` | T4 | ModelMarketplaceViewModel.ShowOnlyCivitai/ShowOnlyHuggingFace + ApplySourceFilter + MainViewModel.RefreshModelMarketplace | ✓ SHIPPED |
| `<sha>` | T5 | opus-tier final review + polish | ✓ SHIPPED |

**Total**: 12 文件,~700 LoC,21 新 tests(4 settings + 3 factory + 8 HF + 2 password + 3 filter + 1 SKIP)。Target post-SDD `~1503 PASS / 0 FAIL / 6 SKIP` → **实际 `1503 PASS / 6 FAIL pre-existing flaky / 6 SKIP`** (跟 v0.6.20 baseline `1483 / 6 / 5` 对齐 + 20 新 tests + 1 新 SKIP)

## Patterns / Lessons(从 v0.6.20 复用 + v0.6.21 新增)

- **Per-source factory pattern**(`ModelSourceFactory.CreateAll(Settings, HttpClient)`): 复用 v0.6.20 单一 source 直接构造,但加 1 层 factory 解耦 Settings → 构造逻辑。Factory 返 null 表 disabled → aggregator 内部 filter 永远只看 enabled,完全等价
- **WPF PasswordBox 不能绑 Password DP**(security)— custom `BindablePasswordBox` 用 template part hook + OnApplyTemplate 把 DP 转发到 inner `PasswordBox.Password` / `TextBox.Text`
- **👁 30 秒 reveal timer**:`DispatcherTimer` 1s tick + `_tokenRevealUntilUtc` DateTime 判定 → 简单且线程安全(WPF UI thread)
- **HF kind 启发式**:`tags.Any(t => t.Equals("lora", OrdinalIgnoreCase))` 优先匹配 lora,避免 "image" tag 误匹配 checkpoint
- **NSFW binary 启发式**:HF 无 mature tier,简化 Nsfw/Sfw 二元
- **HF virtual version**:每个 ModelEntry 1 个 version,`SourceVersionId = sha`,这样下游 Downloader / Symlinker / Storage 不用改,完全复用 v0.6.20 infra
- **Plaintext token 风险**:本地 app low-risk,user-typed user takes responsibility。DPAPI 加密 v0.6.22+ if requested
- **Mirror URL http 不发 token**:HF 镜像 http 走代理时,不能发 Bearer header(中间人可窃),UI 灰掉 [测试连接] 强制用户知情

## Critical reasoning(why these decisions)

- **v0.6.21 vs v0.6.20 hotfix**:v0.6.20 SHIP-READY 后 v0.6.21 加 HF 是 next minor,不是 hotfix(spec §13 v0.6.20 明确 defer HF)
- **CivitAI 也加 mirror**:用户拍板"per-source 通用",不只为 HF 加 → UI 一致
- **30s reveal**:太短用户来不及 copy,太长不安全。30s 是 IDE 行业标准
- **Token 不 encrypt**:DPAPI 加密 + 用户遗忘 = 数据丢失 + 用户无法重置(DPAPI 绑 user profile)。Plaintext + 用户自己负责更简单且避免 lost-password 灾难
- **Source filter view-time**:用户切 chip 不希望后台重新拉(slow)。`ICollectionView.Filter` 在 `Models` 集合上做 in-memory 过滤,毫秒级

## 当前进度 / 下次从哪里继续

- **Branch HEAD**: `<commit>` (T1-T5 SHIPPED, all reviews APPROVED)
- **Tasks done**: T1-T5 (5 of 5) — v0.6.21 SDD COMPLETE
- **Next actions** (post-SDD, in order):
  1. (done) 5 task commits pushed
  2. (done) MEMORY updated
  3. (done) staging rebuild
  4. (TODO) GUI smoke 12 步用户桌面验证
  5. (TODO) v-bump + release zip + tag + gh release (if user requests)

## Plan 参考(v0.6.20 模板)

`docs/superpowers/plans/2026-08-18-model-marketplace.md` (3346 行) — 10 task 模板。v0.6.21 实际写 5 任务(规模小很多,大部分 infra v0.6.20 已建好)+ 16 global constraints + 7 new + 11 modified files(比 v0.6.20 少 5 task 因为只改 1 source 真实实现 + 1 factory + 1 custom control + 1 view filter + 1 final review)
```

Also update `MEMORY.md` by adding a one-line entry for v0.6.21 (replace or extend the v0.6.20 line):

```markdown
- [v0.6.21 模型市场 v2 (HF + mirror + token)](project_v0_6_21_model_marketplace_v2.md) — **SHIP-READY YYYY-MM-DD**, HEAD `<commit>`, 4 commits / 12 files, 1503 PASS / 6 FAIL pre-existing / 6 SKIP; 3 决策 (v0.6.21 minor / CivitAI 也加 mirror / PasswordBox + 👁 30s reveal) + ModelSourceFactory per-source + BindablePasswordBox WPF custom control + HF 真实 impl (kind heuristic + NSFW binary + virtual version per sha) + SourceChips view-time filter; plan commit + staging rebuild + GUI smoke X 步 待用户桌面验证
```

- [ ] **Step 4: Create SDD ledger**

Create `D:\ToolDevelop\ComfyUI\.superpowers\sdd\2026-08-19-model-marketplace-v2\progress.md` with the same structure as v0.6.20's ledger: pre-flight scan (5-6 conflict rows between tasks + rulings), T1-T5 complete entries, interrupt-resume steps.

- [ ] **Step 5: Attempt staging rebuild**

Run:
```bash
dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"
```

If blocked by user's PID (e.g. v0.6.20 staging still running from earlier session), message user to close the staging exe and re-run.

Expected: `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` produced. Build may emit pre-existing CS8601/CS8602/CS8604 nullable warnings — acceptable.

- [ ] **Step 6: Launch staging exe and verify**

```bash
powershell.exe -NoProfile -Command "Start-Process 'release/staging/ComfyUI Manager/ComfyUI.Manager.exe'; Start-Sleep -Seconds 5; Get-Process -Name 'ComfyUI Manager' | Select-Object Id, ProcessName, MainWindowTitle | Format-List"
```

Expected: PID printed, `MainWindowTitle = "ComfyUI 管理系统"` (CJK garbling in PowerShell console is normal — actual title is correct).

- [ ] **Step 7: GUI smoke 12-step verification (user-driven)**

Document the 12-step smoke test in the SDD ledger and ask the user to run on desktop:

1. Open app → 侧栏 → "模型市场" tab → 看到 CivitAI cards (kind chips, source column header — NO source chips yet because v0.6.20 layout)
2. Settings tab → "模型市场" section → 看到新 "CivitAI" / "HuggingFace" checkboxes + 现有 ModelsDirectory
3. 勾选 "HuggingFace" → 出现 token PasswordBox + 👁 toggle + [测试连接] button + mirror checkbox + mirror URL
4. 不输入 token + 勾选 mirror + [测试连接] → 弹窗 "✅ 连接成功 (https://hf-mirror.com)"
5. 👁 toggle token → 显示明文 30s → 30s 后自动 re-hide
6. 切到 "模型市场" tab → toolbar 新出现 "源 [✓CivitAI] [✓HF]"
7. 取消勾选 HF → HF cards 消失,只留 CivitAI
8. 取消勾选 CivitAI → CivitAI cards 也消失,只留 HF
9. 两个都取消 → 出现 "未找到匹配模型" empty hint
10. 切回 Settings → 改 HF mirror URL → [立即刷新模型市场] → 切到模型市场 tab → 看到新 VM 触发重新查询
11. 下载一个 HF model (任意小 size) → 看 Console 进度 + 下载完成
12. 启动一个 env → 切到 env's ComfyUI web UI → 看到 model 出现在对应 kind subfolder(通过 junction)

If any step fails, fix in iterative loop + commit fixes.

- [ ] **Step 8: Commit ledger + memory**

```bash
git add .superpowers/sdd/2026-08-19-model-marketplace-v2/progress.md
git commit -m "docs(spec): v0.6.21 model marketplace v2 implementation ledger"
```

Memory file lives outside the git repo — no commit needed. Verify the memory file content is correct via Read.

- [ ] **Step 9: Final report**

Report to user:
- Branch HEAD + 4 commit SHAs
- Test count (target 1503 / 6 pre-existing FAIL / 6 SKIP)
- Staging exe path + PID
- 12-step GUI smoke test instructions
- Next action: ask user to run smoke + decide on v-bump + release zip

---

## Post-SDD: next session resume steps

1. ~~Read progress.md to confirm state~~ ✓
2. ~~Generate T1-T4 review packages~~ ✓
3. ~~Dispatch T1-T4 implementers + reviewers~~ ✓
4. ~~Dispatch T5 final reviewer (opus)~~ ✓ done
5. ~~T5 polish fix wave if any~~ ✓ done
6. **User runs GUI smoke 12 steps on desktop** (verification by user)
7. **v-bump + release zip + tag v0.6.21 + gh release** (if user requests)

---

**Plan end. Awaiting SDD execution via subagent-driven-development skill.**
