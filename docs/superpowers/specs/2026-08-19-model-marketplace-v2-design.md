# Model Marketplace v0.6.21 — Design Spec (HF Source + Mirror + Token)

> **Status:** DRAFT — awaiting user review before plan/implementation.
> **Complements:** v0.6.20 (`docs/superpowers/specs/2026-08-18-model-marketplace-design.md`, SHIPPED `9d0bd0f`) — this spec adds **HuggingFace as a real source**, **per-source mirror toggle**, **HF API token**, and **marketplace toolbar source filter chips**. v0.6.20 ships with `CivitAiModelSource` only; v0.6.21 makes HF a peer of CivitAI with the same card/UI plumbing but distinct HTTP API + auth.

**Goal:** Extend the v0.6.20 模型市场 sidebar so users can enable **HuggingFace** as a second source (in addition to CivitAI), configure an **API token** for gated-model access, and route traffic through a **mirror URL** (default `https://hf-mirror.com` for HF; user-defined for CivitAI) when direct access is blocked or slow. Marketplace view adds a **source filter chip group** in the toolbar (mirrors the existing kind-chip pattern) so users can focus on one source or both without re-querying.

**Architecture (delta from v0.6.20):**
- New `IModelSource` impl `HuggingFaceModelSource` — replaces the v0.6.20 stub (returns empty list, no instantiation).
- New `ModelSourceFactory` (per-source factory) — reads `Settings`, picks base URL (mirror or official) and API token, instantiates `IModelSource` with the right configuration. Lives next to sources, not in DI container.
- `Settings` gains 6 new fields (see §5).
- `MainViewModel.ShowModels()` constructs sources via the factory and passes them into `ModelMarketplaceService`. **T4 aggregator's internal `IsEnabled` filter stays** (no resolver) — factory just skips construction when `*Enabled=false`, which is semantically equivalent to filtering.
- `ModelMarketplaceView` toolbar gains a `SourceChips` `ItemsControl` to the right of kind chips; clicking a chip toggles `ModelsMarketplaceVM.ShowOnlyCivitai` / `ShowOnlyHuggingFace` (in-memory filter, no re-query).

**Tech stack:** Same as v0.6.20 (WPF .NET 8 / C# 12 / xUnit / SQLite / HttpClient singleton / JunctionLinker / AppLogger). **New sub-systems** for AppLogger tags: `model-huggingface`, `model-mirror`. **New WPF control**: `BindablePasswordBox` (custom control wrapping `PasswordBox` + `BindablePassword` DP + show/hide eye toggle).

**base SHA:** `eded5a6` (post v0.6.20 plan commit, v0.6.20 SDD COMPLETE).

---

## 1. Background & user request

User original message (verbatim, this brainstorm `2026-08-19`):

> "模型市场，我觉得可以这么来，在右边选择civital和huggingface，回馈的数据以卡片图展示，同时会给出足够的信息。如果在源选择Huggingface 需要api的token设置，则可以设置中增加这些内容，另外设置的源中支持源和镜像的勾选，源是huaggingface 提示要求代理，勾选镜像之后访问国内镜像地址。这些分析下设置"

User-clarified decisions (during this brainstorm, AskUserQuestion `2026-08-19`):
- **Scope:** v0.6.21 (next minor version; not v0.6.20 hotfix). Start plan now.
- **CivitAI mirror:** **also add** — keep per-source mirror toggle pattern consistent across both sources. Default OFF + empty URL (no popular China mirror today; user can fill their own).
- **Token UI:** `PasswordBox` + 👁 toggle for plaintext (industry standard for IDE / banking apps).

This v0.6.21 spec was **anticipated** in v0.6.20 §1 user decisions: "v0.6.20 = CivitAI Models API only. HuggingFace and other sources = v0.6.21+ (interfaces stubbed now, not implemented)."

---

## 2. Scope

### In scope (v0.6.21)

- **`HuggingFaceModelSource` real implementation** — hits `https://huggingface.co/api/models?search={q}&limit={n}` for search and `https://huggingface.co/api/models/{repo_id}` for version/file detail.
- **HF API token field** in Settings (`HuggingFaceApiToken`, plaintext in `settings.json`).
- **Per-source mirror toggle** for both CivitAI and HF:
  - `ModelSourceCivitAiUseMirror` (default OFF) + `ModelSourceCivitAiMirrorUrl` (default empty)
  - `ModelSourceHuggingFaceUseMirror` (default ON) + `ModelSourceHuggingFaceMirrorUrl` (default `https://hf-mirror.com`)
- **Mirror injection at source construction** — `ModelSourceFactory.CreateCivitAi(Settings, HttpClient)` and `CreateHuggingFace(Settings, HttpClient)` pick base URL + token from Settings and pass them to source constructors.
- **SettingsView "模型市场" section expansion** — add 6 new UI controls (HF enabled checkbox, token PasswordBox + eye toggle, mirror checkboxes + URL fields with reset button).
- **Marketplace view toolbar source filter chips** — `SourceChips` ItemsControl, parallel to existing `KindChips`. In-memory filter of `Models` collection; no re-query.
- **HF kind mapping heuristic** — map HF `tags` (e.g. `text-to-image`, `lora`, `checkpoint`, `vae`, `controlnet`, `diffusers`) to our 8 `ModelKind` enum values; unknown → `Other`.
- **HF NSFW heuristic** — tag containing `nsfw` → `Nsfw`; otherwise `Sfw`. HF does not natively tag NSFW, so this is best-effort.
- **HF primary file selection** — per model detail, pick the largest `*.safetensors` or `*.bin` from `siblings[]`. No multi-file bundle support in v0.6.21 (carry-over from v0.6.20 §13 out-of-scope).
- **HF author/source label** — `entry.Author = model.id` (e.g. `stabilityai/stable-diffusion-xl-base-1.0`) since HF has no separate "author" field — the repo ID IS the canonical identifier.
- **`ModelSourceKind.HuggingFace` added to enum** — and the v0.6.20 stub `HuggingFaceModelSource.SourceKind` finally returns `ModelSourceKind.HuggingFace` instead of the placeholder `CivitAi` it has been returning.
- **Re-instantiate HF in `MainViewModel.ShowModels()`** — v0.6.20 T10 polish removed the line; v0.6.21 adds it back conditionally on `Settings.ModelSourceHuggingFaceEnabled`.
- **`ModelMarketplaceService` source filter integration** — when `Settings.ModelSource{HuggingFace|CivitAi}Enabled=false`, factory skips construction → aggregator never sees the disabled source → identical end-state to v0.6.20's internal `IsEnabled` filter. No code change to aggregator.
- **5 settings persistence helpers** — `SettingsDefaults.Resolve` defaults + `MarkDirty` includes new fields (pattern from v0.6.18.x).
- **AppLogger sub-system tags** — `model-huggingface`, `model-mirror`. Existing `model-civitai` tag gets mirror info lines when `UseMirror=true`.
- **3 new converters** — `StringToVisibilityConverter` (already exists from v0.6.18.x for env running warning; reuse) and 2 new mirror toggle converters if needed (likely not — use built-in WPF bool→visibility).
- **BindablePasswordBox** — new WPF custom control `src-wpf/ComfyUI.Manager/Controls/BindablePasswordBox.cs` + theme resource. ~50 LoC.

### Out of scope (v0.6.21)

Carried over from v0.6.20 §13 plus new for v0.6.21:
- **HF gated-model flow** — token works for private/gated model access; v0.6.21 only adds the field + sends it as `Authorization: Bearer hf_xxx` header. UI for accepting gated-model errors ("You need to accept the license") is deferred to v0.6.22+.
- **HF search facets** — no author filter, no tag filter, no pipeline-tag filter, no downloads/likes sort. Search is full-text only on `id`/`description`.
- **HF pagination beyond first page** — first page (up to ~100 results) only; v0.6.20 also no-pagination.
- **HF multi-file bundle** — primary file only; spec §13 v0.6.20 carry-over.
- **CivitAI Chinese mirror** — no popular China mirror exists; users must self-host or use generic HTTPS proxy (set via `ModelSourceCivitAiMirrorUrl`).
- **Token encryption at rest** — plaintext in `.manager/settings.json`. Risk-acceptable for local app; user-typed token user takes responsibility. Encrypted storage (DPAPI) deferred to v0.6.22+ if requested.
- **Source selector as download-time filter** — v0.6.21 chips are view-time filter only. Changing source filter does not re-download anything already on disk.
- **Per-version mirror** — mirror is per-source, not per-version. All HF queries use one base URL.
- **Multi-token rotation** — single token per source.
- **OAuth flow** — paste token manually. Browser-redirect OAuth flow deferred.

---

## 3. Architecture (delta from v0.6.20)

```
                          ┌─────────────────────────────────────────┐
                          │            Settings (settings.json)     │
                          │  ModelSourceCivitAiEnabled              │
                          │  ModelSourceCivitAiUseMirror            │
                          │  ModelSourceCivitAiMirrorUrl            │
                          │  ModelSourceHuggingFaceEnabled          │
                          │  HuggingFaceApiToken                     │
                          │  ModelSourceHuggingFaceUseMirror         │
                          │  ModelSourceHuggingFaceMirrorUrl        │
                          └────────────────┬────────────────────────┘
                                           │ reads
                                  ┌────────▼──────────┐
                                  │ ModelSourceFactory │
                                  │ CreateCivitAi(s,h) │  ──► CivitAiModelSource(http, baseUrl)
                                  │ CreateHF(s,h)      │  ──► HuggingFaceModelSource(http, baseUrl, token)
                                  └────────┬──────────┘
                                           │ constructs + returns
                                  ┌────────▼──────────────────────────┐
                                  │ IEnumerable<IModelSource>          │
                                  │ (only enabled, deduped by Factory) │
                                  └────────┬──────────────────────────┘
                                           │
                                  ┌────────▼──────────────────────────┐
                                  │ ModelMarketplaceService            │
                                  │ (T4 aggregator, unchanged)        │
                                  │ LoadAllAsync → Task.WhenAll        │
                                  └────────┬──────────────────────────┘
                                           │
                                  ┌────────▼──────────────────────────┐
                                  │ ModelMarketplaceViewModel          │
                                  │ + SourceChips filter (NEW)         │
                                  │ + ShowOnlyCivitai / ShowOnlyHF     │
                                  └────────┬──────────────────────────┘
                                           │ filter in-memory
                                  ┌────────▼──────────────────────────┐
                                  │ ModelMarketplaceView (XAML)        │
                                  │ + SourceChips ItemsControl         │
                                  └────────────────────────────────────┘
```

**No change** to `ModelDownloader`, `ModelSymlinker`, `ModelFilesystemScanner`, `ModelEntry` DTOs, `ModelMetaSidecar` schema, env-startup wiring, or `MainWindow.xaml` sidebar (the 9th RadioButton already exists from v0.6.20).

---

## 4. Data model additions

### `Models/Settings.cs` (6 new fields + 1 dead-field cleanup)

```csharp
// Existing — keep
public string ModelsDirectory { get; set; } = "";
public bool ModelSourceCivitAiEnabled { get; set; } = true;

// NEW v0.6.21
public bool ModelSourceCivitAiUseMirror { get; set; } = false;
public string ModelSourceCivitAiMirrorUrl { get; set; } = "";       // no popular China mirror; user-defined

public bool ModelSourceHuggingFaceEnabled { get; set; } = false;     // default OFF
public string HuggingFaceApiToken { get; set; } = "";                 // plaintext in settings.json
public bool ModelSourceHuggingFaceUseMirror { get; set; } = true;     // default ON for Chinese users
public string ModelSourceHuggingFaceMirrorUrl { get; set; } = "https://hf-mirror.com";
```

**Mirror resolver rule** (applied in `ModelSourceFactory`):
```csharp
string ResolveBaseUrl(bool useMirror, string mirrorUrl, string officialUrl)
    => useMirror && !string.IsNullOrWhiteSpace(mirrorUrl) ? mirrorUrl.TrimEnd('/') : officialUrl;
```

### `Models/ModelSourceKind.cs` (no change)

The enum already has `HuggingFace` defined from v0.6.20 (T2 file). The stub was returning `CivitAi` as a placeholder; v0.6.21 finally returns `HuggingFace`. No enum value changes.

### `Services/ModelSources/HuggingFaceModelSource.cs` (rewrite, ~200 LoC)

Real implementation (replaces v0.6.20 stub at ~30 LoC). Constructor:
```csharp
public HuggingFaceModelSource(HttpClient http, string baseUrl, string apiToken)
```

- `baseUrl` = `https://huggingface.co` or `https://hf-mirror.com` (or user-defined)
- `apiToken` = empty string for anonymous; `hf_xxxxx` for authenticated. Sent as `Authorization: Bearer hf_xxx` header when non-empty.
- `SourceKind => ModelSourceKind.HuggingFace` (was `CivitAi` in stub — fix here)
- `DisplayName => "HuggingFace"`
- `IsEnabled => !string.IsNullOrEmpty(apiToken) ? true : true` — always enabled when constructed (Factory decides enabled via construction)

`SearchAsync(query, maxResults, ct)`:
- `GET {baseUrl}/api/models?search={urlEncodedQuery}&limit={maxResults}&full=true`
- If `apiToken` non-empty: `request.Headers.Authorization = "Bearer {apiToken}"`
- Response: JSON array `[{"id": "stabilityai/sdxl-base", "tags": [...], "downloads": N, "lastModified": "..."}, ...]`
- Each item → `MapToModelEntry(item, ct)` (calls `GET {baseUrl}/api/models/{id}` for `siblings[]` + version detail)

`MapToModelEntry(repoSummary, ct)`:
1. Fetch `GET {baseUrl}/api/models/{id}` (model detail) — get `siblings: [{rfilename, size?}]`, `cardData`, `tags`
2. **Kind heuristic**: scan `tags` for first match (priority order): `"lora"` → `LORA`, `"checkpoint"` → `Checkpoint`, `"vae"` → `VAE`, `"controlnet"` → `Controlnet`, `"textual-inversion"` → `TextualInversion`, `"upscaler"` → `Upscaler`, `"hypernetwork"` → `Hypernetwork`. Else → `Other`.
3. **NSFW heuristic**: any tag contains `nsfw` (case-insensitive) → `Nsfw`. Else → `Sfw`. (No `Mature` tier for HF — best-effort binary.)
4. **Primary file**: from `siblings`, pick largest by `size` (fallback: first `.safetensors` or `.bin` if size missing)
5. **Version**: HF does not have explicit "versions" like CivitAI. v0.6.21 maps each `ModelEntry` to **one virtual version** keyed by the commit `sha` (from `cardData` or current `lastModified`). `ModelVersionEntry.Id = "HuggingFace:{repo_id}:{sha}"`. Future v0.6.22+ can add multi-version support if needed.
6. **File metadata**: `{PrimaryFilename, SizeBytes, Sha256?}` from `siblings[]`. `SizeBytes` is the primary file's size.
7. **Author = repo id** (e.g. `stabilityai`); **Source = `ModelSourceKind.HuggingFace`**; **SourceId = repo id** (the canonical HF identifier).

### `Services/ModelSources/ModelSourceFactory.cs` (NEW, ~50 LoC)

```csharp
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

    private static string ResolveBaseUrl(bool useMirror, string mirrorUrl, string officialUrl)
        => useMirror && !string.IsNullOrWhiteSpace(mirrorUrl)
            ? mirrorUrl.TrimEnd('/')
            : officialUrl;
}
```

`CivitAiModelSource` ctor signature changes from `(HttpClient http)` to `(HttpClient http, string baseUrl)` — non-breaking for existing tests because tests construct with default baseUrl or explicit URL via factory helper.

### `Controls/BindablePasswordBox.cs` (NEW, ~60 LoC)

WPF `PasswordBox` doesn't expose a `Password` DP out of the box (security). Custom control needed for binding to `Settings.HuggingFaceApiToken`:

```csharp
public class BindablePasswordBox : Control
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(nameof(Password), typeof(string), typeof(BindablePasswordBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    // Internal PasswordBox child control + code-behind hook for show/hide toggle.
}
```

Theme.xaml style with the 👁 toggle button (renders as `Path` icon, no emoji to avoid WPF font fallback issues — see v0.6.17.1 lesson).

---

## 5. Settings UI expansion

`Views/SettingsView.xaml` "模型市场" section (carries over v0.6.20 T1 + adds v0.6.21):

```
模型市场
├ 模型存储目录 [_______________________] [浏览]
│
├ 启用的源
│ │
│ ├ [✓] CivitAI
│ │  (推荐,无需配置 — 匿名访问约 60 req/h)
│ │  └ [ ] 使用镜像
│ │     └ 镜像地址 [____________________]  (无流行国内镜像,留空用官方)
│ │
│ └ [ ] HuggingFace
│    (国内可能需要代理;hf-mirror.com 已设为默认镜像)
│    │
│    ├ API Token: [●●●●●●●●●●●●●●●●] [👁] [测试连接]
│    │ → https://huggingface.co/settings/tokens 获取
│    │
│    └ [✓] 使用国内镜像
│       └ 镜像地址 [https://hf-mirror.com      ] [重置]
│
├ 镜像说明
│  勾选镜像后访问国内镜像地址,速度更快不需代理。
│  CivitAI 无流行国内镜像,留空即可。
│
└ [打开模型目录] [立即刷新模型市场]
```

**Interactions**:
- Toggling `ModelSourceHuggingFaceEnabled` ON requires token to be non-empty for some users, but **not enforced** — UI shows ⚠ if both are unchecked OR if HF enabled + token empty: "未配置 token 也能浏览公开模型,但部分 gated 模型将 403"
- 👁 toggle switches between `PasswordBox` (default) and `TextBox` with `FontFamily="Consolas"` for copy-paste convenience
- [测试连接] button → `var client = new HttpClient(); client.GetAsync(baseUrl + "/api/whoami-v2")` (or equivalent lightweight endpoint) → ✅/❌ status icon next to button. ~5s timeout.
- [重置] button next to HF mirror URL → sets `ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com"`
- [立即刷新模型市场] button at bottom (NEW) → calls `MainViewModel.RefreshMarketplaceAsync()` — forces re-query even if Settings change didn't trigger refresh (which it does automatically, but this is the manual kick).

---

## 6. Marketplace view toolbar source chips

`Views/ModelMarketplaceView.xaml` toolbar Row 0 (existing kind chips at Row 1, NEW source chips at Row 0 right side):

```
[搜索框] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 源: [✓ CivitAI] [✓ HF] ┊  kind: [全部] [Checkpoint] [...]
```

Or as inline chips next to kind chips (single row when toolbar is wide enough):

```
[搜索框] ━━━━━━━━━━━━━━━━━━━━━━━━━━ 源 [✓CivitAI][✓HF]  kind [全部][Checkpoint][LORA][VAE]...
```

**Behavior** (`ModelMarketplaceViewModel`):
```csharp
private bool _showOnlyCivitai = true;
private bool _showOnlyHuggingFace = true;
public bool ShowOnlyCivitai { get => _showOnlyCivitai; set { ...; ApplySourceFilter(); } }
public bool ShowOnlyHuggingFace { get => _showOnlyHuggingFace; set { ...; ApplySourceFilter(); } }

private void ApplySourceFilter()
{
    // Apply ICollectionView.Filter on Models collection — entry.Source ∈ visible sources
    var view = CollectionViewSource.GetDefaultView(Models);
    view.Filter = m => ((ModelEntry)m).Source switch
    {
        ModelSourceKind.CivitAi => ShowOnlyCivitai,
        ModelSourceKind.HuggingFace => ShowOnlyHuggingFace,
        _ => true
    };
}
```

**Edge case**: User toggles BOTH sources OFF → `Models` becomes empty; `IsEmpty` already handles showing "no results" hint. (v0.6.20 already has this state — just need to add the empty hint for "all sources hidden".)

---

## 7. Token handling & security

- **Storage**: plaintext in `<projectRoot>/.manager/settings.json` (existing local data dir from v0.6.16).
- **Transport**: HTTPS strongly preferred (`https://huggingface.co`, `https://hf-mirror.com` — both TLS). Mirror URL field in UI accepts `http://` for **LAN proxies / self-hosted mirrors** (common case for users behind corporate firewalls) but logs `WARN model-mirror "non-HTTPS mirror URL — credentials and downloads may be exposed"` and shows ⚠ icon next to the field. Token is **never** sent over `http://` — UI greys out the [测试连接] button when mirror is `http://` and token is set.
- **In-memory**: `Settings.HuggingFaceApiToken` lives in `Settings` instance held by `BindablePasswordBox` binding + `HuggingFaceModelSource` constructor. Released when Settings reload from disk or app exits.
- **Logs**: never log token. `AppLogger.Info("model-huggingface", "token configured, length=42")` is OK; `AppLogger.Info("model-huggingface", $"token={token}")` is FORBIDDEN.
- **UI**: `PasswordBox` by default; 👁 toggle reveals plaintext for 30 seconds then re-hides. ViewModel tracks `_tokenRevealUntilUtc`.
- **Migration**: existing v0.6.20 settings.json has no `HuggingFaceApiToken` field → `SettingsRepository.Load` uses `default = ""` for new fields. `SettingsDefaults.Resolve` does NOT generate a token.

---

## 8. Test plan (~21 new tests)

### T1 Settings expansion (4 tests, file `SettingsTests.cs`)
- `ModelSourceHuggingFaceEnabled_DefaultsToFalse`
- `ModelSourceHuggingFaceUseMirror_DefaultsToTrue`
- `ModelSourceHuggingFaceMirrorUrl_DefaultsToHfMirror`
- `Settings_LoadFromV0_6_20_Json_MigratesNewFieldsAsDefaults` (regression — no breakage of old configs)

### T2 ModelSourceFactory (3 tests, file `ModelSourceFactoryTests.cs`)
- `CreateCivitAi_Disabled_ReturnsNull`
- `CreateHuggingFace_Disabled_ReturnsNull`
- `CreateAll_ResolvesMirrorUrl_And_StripsTrailingSlash`

### T3 HuggingFaceModelSource (8 tests, file `HuggingFaceModelSourceTests.cs`)
- `SearchAsync_EmptyQuery_HitsBaseUrl`
- `SearchAsync_WithToken_SendsBearerHeader`
- `MapToModelEntry_TagsContainsLora_MapsToLoraKind`
- `MapToModelEntry_TagsContainsCheckpoint_MapsToCheckpointKind`
- `MapToModelEntry_UnknownKindTags_MapsToOther`
- `MapToModelEntry_TagContainsNsfw_SetsNsfwRating`
- `MapToModelEntry_NoNsfwTag_SetsSfwRating`
- `MapToModelEntry_SiblingsList_PicksLargestSafetensors` + 1 SKIP real-fetch

### T4 BindablePasswordBox (2 tests, file `BindablePasswordBoxTests.cs`)
- `SetPassword_DpProperty_RaisesChangeNotification`
- `PasswordCharToggle_RevealsPlaintext_For30Seconds`

### T5 Marketplace source filter (3 tests, file `ModelMarketplaceViewModelSourceFilterTests.cs`)
- `ShowOnlyCivitai_False_HidesCivitaiEntries`
- `ShowOnlyHuggingFace_False_HidesHuggingFaceEntries`
- `BothFalse_RendersEmptyHint`

**Total: 20 PASS + 1 SKIP (live HF fetch).** Combined with v0.6.20 baseline ~1483 tests = **~1503 PASS / 6 pre-existing FAIL / 6 SKIP**.

---

## 9. Risks & mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| HF API rate-limit lower than expected for anonymous users (3-5 req/s) | High | Low | Default OFF — only users who explicitly enable HF are affected. Token bumps to 100+ req/s. |
| HF mirror (`hf-mirror.com`) goes down or changes URL | Medium | Medium | UI has `[重置]` button + editable URL field. User can switch to alt mirror or disable mirror. |
| CivitAI mirror (user-defined) is malicious MITM | Medium | High | Only TLS URLs accepted (UI warns on `http://`). User assumes risk for self-hosted mirrors. Document in Help. |
| HF API response schema changes | Low | High | Tagged HF version (`@huggingface_hub` Python lib uses schema version). Wrap parse in try/catch + log WARN, skip entry. v0.6.22+ revisit if schema changes break >10% of models. |
| Token leaks via process dump | Low | Medium | Process is local-only; user takes responsibility. DPAPI encryption deferred to v0.6.22+. |
| HF gated model 403 not handled gracefully | Medium | Low | UI shows error in card ("Access requires token") — `entry.DownloadStatus = AccessDenied` enum. v0.6.21 minimal handling. |
| BindablePasswordBox binding breaks for non-ASCII tokens | Low | Low | Test with multi-byte chars. WPF handles Unicode in PasswordBox natively. |
| Settings field rename breaks old settings.json | Low | High | New fields added, none renamed. Old configs migrate via default values. |

---

## 10. Resolved decisions (from brainstorm `2026-08-19`)

| Decision | Choice | Rationale |
|---|---|---|
| Scope version | **v0.6.21** | Next minor version (not v0.6.20 hotfix). Spec §13 v0.6.20 already defers HF to v0.6.21+. |
| CivitAI mirror toggle | **Yes, per-source general** | Consistent UX. Default OFF + empty URL (no popular China mirror today). |
| Token UI display | **PasswordBox + 👁 toggle** | Industry standard for IDE / banking apps. Settings file stores plaintext; UI masks by default. |
| HF default ON | **No, default OFF** | First-time users won't see HF unless they explicitly enable (avoids confusion from empty results). |
| Mirror URL editable | **Yes, with `[重置]` button** | User may want self-hosted mirror; button recovers from typos. |
| Token must be set to enable HF | **No, optional** | HF works for public models without token. Token enables higher rate limit + gated models. UI shows ⚠ warning. |
| Source filter scope | **View-time only** | In-memory filter; changing chip doesn't re-query or re-download. Same UX as kind chips. |

---

## 11. Out of scope (carry-overs from v0.6.20 + new for v0.6.21)

- HF gated-model license flow (accept button + cookie) — v0.6.22+
- HF search facets (author, tag, pipeline_tag, downloads sort) — v0.6.22+
- HF pagination beyond first page — v0.6.22+
- HF multi-file bundle (lora bundle + VAE bundle) — v0.6.22+
- CivitAI Chinese mirror — none exists; user-defined only
- Token encryption at rest (DPAPI / OS keychain) — v0.6.22+
- Multi-token rotation per source — v0.6.22+
- OAuth flow for token (browser redirect) — never
- Source selector as download-time filter — never (view-time only)
- Per-version mirror — never (per-source only)
- HF model detail page (full markdown card, parameters, license) — v0.6.22+

---

## 12. Implementation outline (for plan)

When the spec is approved, the v0.6.21 plan will follow the same SDD pattern as v0.6.20 — **5 tasks**:

- **T1**: Settings fields (6 new) + SettingsDefaults + SettingsRepository.MarkDirty + Settings UI expansion (PasswordBox + mirror toggles) — ~80 LoC + 4 tests
- **T2**: ModelSourceFactory + CivitAiModelSource ctor baseUrl param + HuggingFaceModelSource rewrite + model detail fetching + kind/NSFW heuristics + primary file selection — ~280 LoC + 11 tests
- **T3**: BindablePasswordBox custom control + Theme.xaml style — ~60 LoC + 2 tests
- **T4**: MainViewModel.ShowModels() re-wire + SourceChips in marketplace toolbar + filter logic in ViewModel — ~80 LoC + 3 tests
- **T5**: Final review (whole-branch) + fix wave + MEMORY + staging + GUI smoke

**Estimated effort**: 4-6h implement + 2h review + 1h MEMORY/staging = **7-9h total**, ship-ready by EOD `2026-08-20` or `2026-08-21`.

---

**Spec end. Awaiting user review.**