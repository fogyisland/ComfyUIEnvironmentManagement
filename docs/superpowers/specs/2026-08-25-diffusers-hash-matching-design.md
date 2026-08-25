# Diffusers Hash Matching & Detection Quality

**Date:** 2026-08-25
**Branch:** main (continuation of v1.0.0 T13 series)
**Related specs:**
- `2026-08-24-civitai-hash-matching-design.md` (T13 — single-file hash chain)
- `2026-08-24-local-models-sidebar-design.md` (T1-T12 — scanner + UI)

## 1. Background

T12 (`b8a45cd7` shipped in T13-6, see scanner:103-132) added **Diffusers folder detection**: when a subdirectory contains `model_index.json`, the scanner emits a single `DownloadedModel` with `Kind = ModelKind.Diffusers`, `FullPath = <folder>`, and skips recursive per-file scanning.

T13 added the **4-strategy hash-matching chain** (Hash → SafetensorsMetadata → CompanionJson → FilenameFuzzy) for single-file models. The chain only fires when `File.Exists(m.FullPath)` — Diffusers folders (`Directory.Exists` only) are silently skipped.

**Result:** Diffusers folders appear in the local-models sidebar with grey status dot and no CivitAI matching. This is a gap the user has explicitly called out: "AI 模型存在两种主要形态:单文件与 文件夹/多文件(Diffusers model_index.json + unet/text_encoder + 自定义权重目录)".

The model marketplace `KindFilters` test fixture (`ModelMarketplaceViewModelTests.KindFilters_ContainsAllModelKindValues`, line 78) expects 8 ModelKind values but T12 added `Diffusers` making 9. Pre-existing breakage; fix as part of this work.

## 2. Goals

- Apply T13's 4-strategy hash chain to Diffusers folders
- Cover images downloaded for matched Diffusers folders
- Robust detection: tolerate invalid `model_index.json`, symlinks, missing `name` field
- Fix pre-existing `KindFilters` test fixture

## 3. Non-goals

- Diffusers download / install (only local scan + match)
- Diffusers card UI redesign (existing 2-col landscape card works)
- Per-file card emission for Diffusers subdirectories (one folder = one card)
- Multi-hash composite for Diffusers (CivitAI's `/by-hash` endpoint takes single hash)
- Cache invalidation strategy change (existing composite key `(path, size_bytes, mtime_utc_ticks)` adapts naturally)

## 4. Architecture

### 4.1 Canonical hash file selection

For each Diffusers folder, the hash chain needs **one file to hash**. Selection priority (first match wins):

1. `<dir>/unet/diffusion_pytorch_model.safetensors` — CivitAI's canonical hash source for SD 1.5 / SDXL Diffusers models
2. `<dir>/transformer/diffusion_pytorch_model.safetensors` — FLUX-style architecture
3. `<dir>/unet/diffusion_pytorch_model.bin` — older `.bin` variant
4. Else: largest `.safetensors` file in folder (recursive)
5. Else: largest `.bin` in folder
6. Else: largest `.ckpt` in folder
7. Else: largest `.pt` in folder
8. Else: no hash; orchestrator continues to safetensors-metadata + companion + filename matchers

Rationale: CivitAI's Diffusers `model_versions[].files[]` exposes per-component hashes (unet, text_encoder, vae). The unet's hash is what CivitAI's `model_hash` field points to for Diffusers. Lacking the unet path (some repos use different layouts like `model/` or `components/`), fall back to the largest safetensors which is statistically the unet.

### 4.2 HashAndMatch extension

`ModelFilesystemScanner.HashAndMatch` at line 252 currently:

```csharp
if (string.IsNullOrEmpty(m.FullPath) || !File.Exists(m.FullPath)) return;
```

Extend to:

```csharp
if (string.IsNullOrEmpty(m.FullPath)) return;
string? hashTarget;
if (File.Exists(m.FullPath)) hashTarget = m.FullPath;
else if (Directory.Exists(m.FullPath)) hashTarget = FindCanonicalHashFile(m.FullPath);
else return;
if (hashTarget is null) return; // skip hash, but matchers may still run via Safetensors/Companion/Filename
```

Hash compute and cache use `hashTarget` (file path) instead of `m.FullPath`. Cache key path field stays as `m.FullPath` (folder path for Diffusers) to keep diffusers invalidation working.

### 4.3 Cache key for Diffusers folders

Composite key `(path, size_bytes, mtime_utc_ticks)` adapts:
- `path`: folder path for Diffusers, file path otherwise
- `size_bytes`: total folder size (sum of all files) for Diffusers, file size otherwise
- `mtime_utc_ticks`: latest mtime across all files for Diffusers, file mtime otherwise

When user adds/removes files in a Diffusers folder, both `size_bytes` and `mtime_utc_ticks` change → cache miss → re-hash. Correct behavior.

### 4.4 model_index.json `name` field

Diffusers detection already verifies `model_index.json` exists. Extend to parse JSON and extract `name` (HF Diffusers optional field):

- If `name` exists and is non-empty: use as `Title`
- Else: folder name (existing behavior)

Invalid JSON: tolerate, log warning at `"model-scanner"`, use folder name. The folder shape (presence of `model_index.json`) is the trigger, not the JSON content.

### 4.5 Detection quality

- **Symlinks**: `Directory.EnumerateDirectories` follows symlinks on Windows by default. Keep current behavior (user may symlink external model dirs into `<modelsRoot>`).
- **Hidden dirs** (`.DS_Store`, `.git`): already skipped via `if (!subdirName.StartsWith("."))` at scanner:111.
- **Invalid `model_index.json`**: tolerate, log warning, still treat as Diffusers.
- **Empty `model_index.json`** (zero bytes): treat as "name field absent" → use folder name.

### 4.6 UI

No layout changes. Diffusers card automatically gets status dot from existing T13 wiring (`MatchedDetail` → green/grey brush via `MatchStatusToBrushConverter`). Title uses `model_index.json["name"]` or folder name.

## 5. Files modified

| File | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` | Add `FindCanonicalHashFile` helper; extend `HashAndMatch` for directory branch; extend Diffusers detection block to parse `model_index.json["name"]` |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs` | Add 4 tests for `model_index.json` name field handling |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs` | Add 3 tests for Diffusers folder hash matching |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs` | Update `KindFilters_ContainsAllModelKindValues` from `8` to `9` |

No new files. No DB schema changes. No new public types.

## 6. Tests

### 6.1 New tests — `FindCanonicalHashFile`

- `FindCanonicalHashFile_PrefersUnetSafetensors` — folder has both `unet/diffusion_pytorch_model.safetensors` and `vae/diffusion_pytorch_model.safetensors`; expect unet path
- `FindCanonicalHashFile_FallsBackToTransformerSafetensors` — no unet, has `transformer/diffusion_pytorch_model.safetensors`; expect transformer path
- `FindCanonicalHashFile_FallsBackToUnetBin` — no safetensors, has `unet/diffusion_pytorch_model.bin`; expect unet bin path
- `FindCanonicalHashFile_FallsBackToLargestSafetensors` — folder has 3 .safetensors of sizes 100MB, 500MB, 200MB; expect 500MB path
- `FindCanonicalHashFile_NoMatchableFiles_ReturnsNull` — folder has only config files; expect null

### 6.2 New tests — `HashAndMatch` for Diffusers

- `HashAndMatch_DiffusersFolder_ComputesHashFromUnetFile`
- `HashAndMatch_DiffusersFolder_CacheKeyUsesFolderPathAndTotalSize`
- `HashAndMatch_DiffusersFolder_RunsOrchestratorWithHashedModel`

### 6.3 New tests — `model_index.json` parsing

- `Scan_DiffusersFolder_NameFieldFromModelIndexJson_UsedAsTitle`
- `Scan_DiffusersFolder_NoNameField_UsesFolderNameAsTitle`
- `Scan_DiffusersFolder_InvalidModelIndexJson_FallsBackToFolderName`
- `Scan_DiffusersFolder_EmptyModelIndexJson_FallsBackToFolderName`

### 6.4 Updated tests

- `ModelMarketplaceViewModelTests.KindFilters_ContainsAllModelKindValues`: change `Assert.Equal(8, vm.KindFilters.Count)` → `Assert.Equal(9, ...)`. Pre-existing breakage from T12.

### 6.5 Existing tests

All 5 existing Diffusers tests in `ModelFilesystemScannerStandardLayoutTests.cs` (T12 detection tests) + 1 in `ModelFilesystemScannerTests.cs` (meta.json path unaffected) + 1 in `LocalModelsViewModelTests.cs` (GroupToCards pass-through) should still pass.

## 7. Risks

- **Cache invalidation on existing entries**: Users with existing `CivitaiHashCache` entries for file paths won't have folder-path entries. First scan after upgrade will populate folder-path entries. Old file-path entries linger harmlessly.
- **Symlink loops**: If user creates a symlink loop, `Directory.EnumerateFiles` will throw. Wrap in try/catch (already done at scanner:267 `catch (Exception ex)`). Log warning, skip folder.
- **Cover download for Diffusers**: Existing `TryDownloadCover` uses `Path.GetFileNameWithoutExtension(model.FullPath)` which returns folder name for directories. Saves `<foldername>.preview.png` next to the folder (i.e., in the parent `<kind>` dir). Acceptable — the cover image is associated with the folder visually.

## 8. Out of scope (parked for future)

- Diffusers download from CivitAI marketplace (parallel to v0.6.20 marketplace)
- Diffusers folder browser / file list UI
- Multi-hash composite matching (CivitAI doesn't support)
- Recursive card emission for Diffusers subdirectories
- Diffusers-specific KindChip filter (currently hidden from filter chips — see T12 design decision)

## 9. Success criteria

- All new tests pass (12 new + 1 updated)
- All existing tests pass (no regression)
- 0 Critical findings in final review
- Full suite: ~1936 PASS / 3 FAIL pre-existing flaky / 6 SKIP / 1945 total