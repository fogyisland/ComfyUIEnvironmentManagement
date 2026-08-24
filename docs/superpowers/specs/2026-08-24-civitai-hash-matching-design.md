# v1.0.0 CivitAI Hash-Based Local-Model Matching — Design Spec

> **Status:** approved (chat 2026-08-24, after user pivot from Diffusers/T12)
> **Path:** architectural (multi-strategy matching subsystem)
> **Author:** Claude + user collaboration
> **Supersedes:** T11 fuzzy-search-only path becomes last-resort fallback within new chain

## 1. Goal

Make the "查询 CivitAI" button on each local-model card resolve to the **correct CivitAI model 100% of the time**, regardless of how chaotically the user has renamed or moved the file on disk. Mechanism: SHA256 file-hash lookup as primary, with a chain of progressively weaker fallback strategies for the rare case when the model was never uploaded to Civitai by that exact binary.

## 2. Motivation

T11 (SHIP-READY `b03954e`) ships filename fuzzy search via `CivitAiLookupService.SearchByTitleAsync` + a 4-state modal picker/detail dialog. After desktop-verifying T11, the user pointed out that **filename-based search is fragile** — Civitai's own client uses SHA256 hashing for a reason: renaming a `.safetensors` file does not change its bytes, but it absolutely defeats title fuzzy matching.

User-provided design reference (verbatim from prompt):
- **Primary**: SHA256 → `GET /api/v1/model-versions/by-hash/{hash}` (single) or `POST /api/v1/model-versions/by-hash` (batch up to 100).
- **Cache**: SQLite by `(FilePath, SizeBytes, LastWriteTimeUtcTicks)` so unchanged files skip recomputation.
- **Fallback chain** when hash misses:
  1. Safetensors header metadata (`ss_sd_model_name` / `modelspec.title`) → fuzzy search.
  2. Companion `.civitai.info` sidecar JSON (Civitai Helper convention) → `GET /api/v1/models/{modelId}`.
  3. Filename fuzzy search (current T11 behavior, retained as last resort).
- **Auto-download cover**: write `<basename>.preview.png` next to model file on first match, so subsequent sidebar scans find it offline (T10 preview-image path already supports this).
- **Async + IProgress**: hash computation on `Task.Run` background thread, progress reported via `IProgress<string>` so the sidebar Console panel shows `[hash] 12/50 AnimateLCM.safetensors …` in real time.

## 3. Non-goals (YAGNI for v1.0.0)

- Reading safetensors header `modelspec.sai_model_id` (a direct numeric Civitai id some newer files embed) — YAGNI, hash match is enough for those.
- Generalizing to HuggingFace or ModelScope hash APIs (different ecosystems, deferred to v1.0.1+ if users ask).
- Mirroring the whole Civitai Helper plugin (we keep narrow focus on the lookup button).
- Auto-applying matched `trainedWords` to user prompts — out of scope.
- Migrating existing T11 fuzzy-search code into a different file layout (we extend it in place).

## 4. UX

### 4.1 Card badge — match status

Each `LocalModelCard` shows a small status indicator next to the kind badge:

| State | Badge | Meaning |
|-------|-------|---------|
| `Matched` | 🟢 green dot + "CivitAI" tooltip | Hash lookup succeeded at scan time. Click button → instant Detail. |
| `NotMatched` | 🔘 grey dot + "Not on CivitAI" tooltip | All 4 strategies failed. Click button still works, opens dialog with NoMatch. |
| `Pending` (during scan only) | ⏳ spinner | Hash compute in progress. Not persisted to display until scan finishes. |

Badge lives in the existing 80x80 left column (replaces or supplements kind badge when `Matched`). When `Matched`, left column shows the downloaded cover image (auto-saved as `<basename>.preview.png` per §4.2). When `NotMatched`, falls back to the existing kind badge.

### 4.2 Auto-downloaded cover

When hash lookup returns a result with `images[0].url`, and `<model_dir>/<basename>.preview.png` does NOT already exist, the scanner-side hash-resolution step downloads + writes the cover. Subsequent sidebar scans find it via the existing T10 preview-image logic (5 extensions including `.png`). User can also delete the local PNG to force re-download on next scan.

### 4.3 Button click UX

**Match at scan-time** (most common after first scan):
1. User clicks `[🔍 查询 CivitAI]`.
2. Dialog opens directly in **Detail** state (no Picker, no Searching spinner) — `card.MatchedDetail` was already populated at scan time.
3. User sees description + tags + versions + images, closes dialog.

**Match on-demand** (hash lookup failed at scan, user retries):
1. User clicks `[🔍 查询 CivitAI]`.
2. Dialog opens in **Searching** state with title `"Trying: AnimateLCM (hash → metadata → companion.json → filename)"`.
3. Orchestrator tries each strategy in order; first non-null wins.
4. Dialog transitions to **Detail** state with single candidate (no Picker — each strategy returns at most 1 candidate).
5. If all 4 fail, dialog transitions to **NoMatch** state with message `"Tried 4 strategies — no match"` + close button.

### 4.4 Scan progress in Console

The existing Console panel (used by v0.6.22++ rich logs and v0.6.19.x workflow marketplace) receives scan progress:
```
[scan] 已枚举 47 个模型
[hash] 1/47 AnimateLCM_sd15_t2v_lora.safetensors → A4F2… (1.2s)
[hash] 2/47 ControlNet_v1.safetensors → cache hit
…
[hash] 47/47 done in 28s
[match] 38/47 matched via hash, 9 fall back to on-demand
[match] 4/9 matched via metadata, 1/9 via .civitai.info, 4/9 unmatched
[preview] 38 covers downloaded
```

## 5. Architecture

### 5.1 New components

| Component | File | Responsibility |
|-----------|------|----------------|
| `IModelMatcher` | `Services/Civitai/IModelMatcher.cs` | Strategy interface. Each matcher declares a `Name` and `MatchAsync(card, ct)`. |
| `CivitaiHashMatcher` | `Services/Civitai/CivitaiHashMatcher.cs` | Primary. SHA256 → `/api/v1/model-versions/by-hash/{hash}`. Single-model response. |
| `SafetensorsMetadataMatcher` | `Services/Civitai/SafetensorsMetadataMatcher.cs` | Parse `.safetensors` header (8-byte length + JSON), extract `ss_sd_model_name` or `modelspec.title`, fuzzy-search via existing `CivitAiLookupService.SearchByTitleAsync`. |
| `CompanionJsonMatcher` | `Services/Civitai/CompanionJsonMatcher.cs` | Read `<basename>.civitai.info` JSON sidecar, extract `modelId`/`modelVersionId`, call `GetDetailAsync`. |
| `FilenameMatcher` | `Services/Civitai/FilenameMatcher.cs` | Wraps existing `SearchByTitleAsync(card.Title)`. Last-resort fallback. |
| `CivitaiMatcherOrchestrator` | `Services/Civitai/CivitaiMatcherOrchestrator.cs` | Chains the 4 matchers in order, returns first non-null `MatchResult` or `NoMatch`. |
| `CivitaiHashCache` | `Services/Civitai/CivitaiHashCache.cs` | SQLite-backed `(FilePath, SizeBytes, MtimeUtcTicks) → Sha256`. Persistent. Located in `%APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite`. |
| `ModelHasher` | `Services/Civitai/ModelHasher.cs` | Static helper: `ComputeSha256(filePath, ct)` streaming 1MB chunks. |
| `SafetensorsHeaderReader` | `Services/Civitai/SafetensorsHeaderReader.cs` | Static helper: `TryReadHeader(filePath, out string? modelName)` — parse first ~64KB, look for `__metadata__` JSON block. |

### 5.2 Modified components

| Component | Change |
|-----------|--------|
| `CivitAiLookupService` | Add `LookupByHashAsync(sha256, ct) → CivitAiDetailDto?` (single result, 404 returns null) + `MatchAsync(LocalModelCard, ct) → MatchResult?` (delegates to orchestrator). Keep `SearchByTitleAsync` + `GetDetailAsync` unchanged (still used by `FilenameMatcher` + `CompanionJsonMatcher`). |
| `ModelFilesystemScanner` | After enumeration, accept `ScanContext { HashCache, IModelMatcher, IProgress<string> }`. Hash all files (parallel, max 4), emit `DownloadedModel(Hash=…)`, run matcher for each hashed card, populate `MatchedDetail`. |
| `LocalModelCard` | New fields: `Hash: string?`, `MatchedDetail: CivitAiDetailDto?`, `MatchSource: MatchSource?`. |
| `LocalModelsViewModel` | `ReloadAsync(IProgress<string>?)` signature accepts progress. `LookupCivitAiCommand.Execute` calls `service.MatchAsync(card)` not `SearchByTitleAsync(title)`. |
| `LocalModelCivitAiDialogViewModel` | Add 5th constructor parameter `LocalModelCard` so the dialog can read pre-matched `MatchedDetail` to skip Searching state. |
| `LocalModelsView.xaml` | Add small status dot to card (Visible only when `Matched` or `NotMatched`). Hide spinner row; existing kind-badge fallback already handles "no cover image". |

### 5.3 Data flow — happy path (cold cache, hash hit)

```
User clicks 刷新
   ↓
LocalModelsViewModel.ReloadAsync(progress)
   ↓
ModelFilesystemScanner.Scan(dir, ctx)
   │ (existing) raw enumeration → List<DownloadedModel> with Hash=null
   │ (new) for each file:
   │    ├─ CachedHash? yes → set DownloadedModel.Hash
   │    └─ no → ModelHasher.ComputeSha256(file) + cache insert + set Hash
   │       progress.Report($"[hash] {i}/{n} {basename} → {hash8chars} ({elapsedMs}ms)")
   │ (new) for each hashed DownloadedModel (batches of 100):
   │    └─ CivitaiMatcherOrchestrator.MatchByHashAsync(card.Hash)
   │       └─ CivitaiHashMatcher → /api/v1/model-versions/by-hash/{hash}
   │       └─ if 200 → MatchResult(Hash, detail, coverUrl)
   │       └─ download coverUrl → write <basename>.preview.png
   │       └─ populate DownloadedModel.MatchedDetail
   ↓
LocalModelsViewModel.GroupToCards(raw)
   ↓
Cards rendered: 🟢 Matched badge + downloaded cover image (or 🔘 + kind badge fallback)
```

### 5.4 Data flow — lookup button click

```
User clicks [🔍 查询 CivitAI]
   ↓
LocalModelCivitAiDialogViewModel dialog opens with (service, card=LocalModelCard, logger)
   ↓
if card.MatchedDetail != null (matched at scan time):
   → State = Detail, Detail = card.MatchedDetail, ready immediately
else:
   → State = Searching
   → service.MatchAsync(card.RawDownloadedModel, ct)   ← passes DownloadedModel (Models-layer), NOT LocalModelCard
   → Orchestrator tries:
       1. CivitaiHashMatcher (only if DownloadedModel.Hash != null AND no prior scan-time match)
       2. SafetensorsMetadataMatcher (reads .safetensors header at DownloadedModel.FullPath)
       3. CompanionJsonMatcher (reads <basename>.civitai.info next to DownloadedModel.FullPath)
       4. FilenameMatcher (uses DownloadedModel.Title)
   → first non-null MatchResult wins
   → State = Detail
   → if all null: State = NoMatch with "Tried 4 strategies" message
   → if MatchResult.CoverImageUrl != null AND <basename>.preview.png missing:
       → fire-and-forget download + write
```

### 5.5 Reused components

| Component | Reused as |
|-----------|-----------|
| `CivitAiLookupService.SearchByTitleAsync` | Called by `FilenameMatcher` (no API change) |
| `CivitAiLookupService.GetDetailAsync` | Called by `CompanionJsonMatcher` (no API change) |
| `CivitAiLookupNotFoundException` | Caught by `CivitaiHashMatcher` to return null instead of throw |
| `HttpProxyConfig.ApplyTo` | Applied in `MainViewModel.TryCreateCivitAiLookupService` for all matchers (HttpClient shared) |
| `AppLogger` (subsystem=`civitai-matcher`) | All matchers log via `[src →/←]` pattern (v0.6.22++ rich Console log) |

## 6. Interfaces

### 6.1 New types

```csharp
// Services/Civitai/IModelMatcher.cs
public interface IModelMatcher
{
    string Name { get; }
    Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct);
}

// Services/Civitai/CivitaiMatcherOrchestrator.cs
public sealed class CivitaiMatcherOrchestrator
{
    public CivitaiMatcherOrchestrator(
        CivitaiHashMatcher hash,
        SafetensorsMetadataMatcher metadata,
        CompanionJsonMatcher companion,
        FilenameMatcher filename,
        AppLogger? logger = null);

    public Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct);

    // Bulk for scan-time: only hash matcher, batches API calls.
    public Task<IReadOnlyDictionary<string, MatchResult>> MatchByHashBatchAsync(
        IReadOnlyList<DownloadedModel> models, CancellationToken ct);
}

// Services/Civitai/CivitaiHashCache.cs
public sealed class CivitaiHashCache : IDisposable
{
    public CivitaiHashCache(string sqlitePath, AppLogger? logger = null);

    public string? Lookup(string filePath, long sizeBytes, long mtimeUtcTicks);
    public void Store(string filePath, long sizeBytes, long mtimeUtcTicks, string sha256);
    public void Clear();
}

// Models/ModelEntry.cs (additions)
public sealed record DownloadedModel(
    /* existing 9 fields */,
    string? Hash,                          // NEW: SHA256 if computed
    CivitAiDetailDto? MatchedDetail,       // NEW: scan-time match result
    MatchSource? MatchSource);             // NEW: how matched

public sealed record LocalModelCard(
    /* existing 7 fields */,
    string? Hash,                          // NEW
    CivitAiDetailDto? MatchedDetail,       // NEW
    MatchSource? MatchSource);             // NEW

public enum MatchSource { Hash, SafetensorsMetadata, CompanionJson, FilenameFuzzy }

public sealed record MatchResult(
    MatchSource Source,
    CivitAiDetailDto Detail,
    string? CoverImageUrl);
```

### 6.2 Modified types

```csharp
// Services/CivitAiLookupService.cs (additions)
public sealed class CivitAiLookupService
{
    // Existing: SearchByTitleAsync, GetDetailAsync — unchanged
    public Task<CivitAiDetailDto?> LookupByHashAsync(string sha256, CancellationToken ct = default);
    public Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct = default);
}

// Services/ModelFilesystemScanner.cs (additions)
public sealed class ScanContext
{
    public CivitaiHashCache HashCache { get; init; }
    public CivitaiMatcherOrchestrator Matcher { get; init; }
    public IProgress<string>? Progress { get; init; }
}

public IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx = null);
// (existing `Scan(string)` overload retained for 23 existing tests — uses ScanContext=null → Hash=null, no match-time behavior)
```

### 6.3 Backward compatibility

- `CivitAiLookupService` keeps `SearchByTitleAsync` and `GetDetailAsync` signatures; **17 T11 tests unchanged**.
- `ModelFilesystemScanner.Scan(string)` overload retained; **23 scanner tests unchanged** (all use single-arg).
- `LocalModelsViewModel` ctor tail param `CivitAiLookupService? lookup` stays (now wraps orchestrator inside, transparent to callers).
- `LocalModelCivitAiDialogViewModel` ctor adds 5th param `LocalModelCard? card = null` (default null = back-compat). Existing 10 dialog VM tests pass card=`null` (use default), no test changes needed.

## 7. Error handling

| Failure | Recovery | Logged as |
|---------|----------|-----------|
| Network error during hash API call | Matcher returns null, orchestrator moves to next strategy | `[civitai-matcher] ✗ HttpRequestException` |
| `/api/v1/model-versions/by-hash/{hash}` returns 404 | Matcher returns null (not exception) | `[civitai-matcher] ← 404 {hash8chars}` |
| JSON parse error in matcher response | Matcher returns null, orchestrator moves to next strategy | `[civitai-matcher] ✗ JsonException` |
| `.safetensors` header missing or non-JSON | `SafetensorsMetadataMatcher` returns null | `[civitai-matcher] safetensors header invalid: {path}` |
| `.civitai.info` file missing | `CompanionJsonMatcher` returns null (silent — common) | none (info-level only) |
| SQLite I/O error | `CivitaiHashCache` falls back to recompute, logs warn | `[civitai-hash-cache] ⚠ SQLite error: {msg}` |
| File deleted mid-scan | Skipped, logged warn, scan continues | `[scan] ⚠ file missing: {path}` |
| Permission denied during hash | Skipped, logged warn, scan continues | `[scan] ⚠ permission denied: {path}` |
| Cover image download fails | Logged warn, no dialog impact | `[civitai-matcher] ✗ preview download: {msg}` |
| `OperationCanceledException` | Rethrown to caller (user-initiated cancel) | `[civitai-matcher] ⏹ 已取消` |

**Never** do these:
- Bubble network errors to dialog UI (orchestrator absorbs them).
- Abort scan on one bad file (each hash is wrapped in try/catch).
- Crash on SQLite corruption (`PRAGMA integrity_check` at startup; auto-recover by recreating table).
- Throw from `MatchAsync` for non-network errors (always returns nullable `MatchResult`).

## 8. Testing

### 8.1 New tests (21 total across 5 files)

| File | Count | Coverage |
|------|-------|----------|
| `CivitaiHashMatcherTests.cs` | 5 | Hit → MatchResult, 404 → null, 5xx → null, network fail → null, ct cancel → throws |
| `SafetensorsMetadataMatcherTests.cs` | 4 | Header has `ss_sd_model_name` → fuzzy + select, header has `modelspec.title` → fuzzy + select, header missing/invalid → null, multi-model ambiguous → first hit |
| `CompanionJsonMatcherTests.cs` | 3 | Valid sidecar with modelId → detail, missing sidecar → null, modelId from sidecar → 404 → null |
| `CivitaiMatcherOrchestratorTests.cs` | 4 | Chain order (Hash hit wins), all-null → null, hash matcher called once when applicable, ct cancel propagates |
| `CivitaiHashCacheTests.cs` | 5 | Insert + lookup hit, miss (mtime changed), miss (size changed), miss (path changed), Clear removes all |

### 8.2 Modified tests

- `LocalModelsViewModelLookupTests.cs` — 7 existing tests must continue to pass (service API additive, ctor tail params).
- `LocalModelCivitAiDialogViewModelTests.cs` — existing 10 tests need NO changes (use ctor default `card=null`); add 3 new tests for pre-matched flow (card.MatchedDetail set → dialog opens in Detail state directly).

### 8.3 Regression baselines

- 17 T11 (Lookup + Dialog VM) tests continue PASS (after the 10 dialog VM updates).
- 23 scanner tests continue PASS (single-arg `Scan` overload retained).
- 12 LocalModelsViewModel tests continue PASS.
- 7 CivitAiLookupService tests continue PASS (existing 2 methods unchanged).

Expected full suite: ~1900+ PASS / 4 FAIL pre-existing flaky / 6 SKIP.

### 8.4 Test infrastructure patterns

- **HashMatcher + CompanionJsonMatcher** — `Mock<HttpMessageHandler>` + real `CivitAiLookupService` (same pattern as T11 tests; service is `sealed`).
- **SafetensorsMetadataMatcher** — synthetic temp `.safetensors` files: write 8-byte length prefix (little-endian), then JSON header bytes. Mirror T7/T10 fixture pattern.
- **Orchestrator** — `Mock<IModelMatcher>` × 4 (interface is mockable, unlike the sealed service). Use `SetupSequence` for chain-order tests.
- **HashCache** — `new CivitaiHashCache(":memory:")` SQLite in-memory mode for fast tests.

## 9. Files

### New (8 files)

```
src-wpf/ComfyUI.Manager/Services/Civitai/
  IModelMatcher.cs
  CivitaiHashMatcher.cs
  SafetensorsMetadataMatcher.cs
  CompanionJsonMatcher.cs
  FilenameMatcher.cs
  CivitaiMatcherOrchestrator.cs
  CivitaiHashCache.cs
  ModelHasher.cs
  SafetensorsHeaderReader.cs

tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/
  CivitaiHashMatcherTests.cs
  SafetensorsMetadataMatcherTests.cs
  CompanionJsonMatcherTests.cs
  CivitaiMatcherOrchestratorTests.cs
  CivitaiHashCacheTests.cs
```

### Modified (5 files)

```
src-wpf/ComfyUI.Manager/Services/CivitAiLookupService.cs        (+ LookupByHashAsync, MatchAsync)
src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs      (+ ScanContext, post-enumeration hash + match loop)
src-wpf/ComfyUI.Manager/Models/ModelEntry.cs                    (+ Hash, MatchedDetail, MatchSource fields)
src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs      (+ progress param + MatchAsync call)
src-wpf/ComfyUI.Manager/ViewModels/LocalModelCivitAiDialogViewModel.cs  (+ LocalModelCard card param, skip Searching when matched)
src-wpf/ComfyUI.Manager/Views/LocalModelsView.xaml              (+ small status dot on card)

tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelCivitAiDialogViewModelTests.cs  (+ 5th param, 3 new pre-match tests)
```

## 10. Out of scope

- T12 (Diffusers folder detection) — deferred to next plan; T12 brief remains valid and ships independently after this feature lands.
- HuggingFace / ModelScope hash APIs.
- Auto-applying `trainedWords` to user prompts.
- Mirroring the full Civitai Helper plugin.
- SQLite cache encryption (hash data is non-sensitive).

## 11. Open questions resolved

| Question | Resolution |
|----------|------------|
| Replace T11 vs augment? | Hybrid — T11 retained as fallback only. |
| Hash timing? | Scan-time + SQLite cache + IProgress<string>. |
| Auto-download cover? | Yes, write `<basename>.preview.png` next to model. |
| Which fallback strategies? | All 3 (hash → safetensors header → companion .json → filename fuzzy). |
| T12 Diffusers status? | Defer to next plan. |
| Cache location? | `%APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite` (per-app, survives model moves). |
| Hash cache invalidation key? | `(FilePath, SizeBytes, LastWriteTimeUtcTicks)` — user's recommended pattern. |

## 12. Risks

- **First-scan slowness**: 50 models × 5s SHA256 = ~4 min. Acceptable for one-time + cache hit after. User sees Console progress.
- **CivitAI rate limit**: 100 hashes per batch endpoint. Stay below by batching conservatively (100/request).
- **Disk write for cover**: Side effect on user's modelsDir. Reversible (user can delete `<basename>.preview.png`).
- **Hash cache portability**: SQLite lives in `%APPDATA%`, not portable with models. If they move models to another machine, first scan recomputes (acceptable, slow but correct).
- **Safetensors header parsing edge cases**: Some files have non-JSON first block; reader must tolerate and return null gracefully.