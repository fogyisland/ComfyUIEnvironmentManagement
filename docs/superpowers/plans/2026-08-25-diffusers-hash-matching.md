# Diffusers Hash Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply T13's 4-strategy hash chain to Diffusers folders (currently silently skipped), parse `model_index.json["name"]` as Title, and fix the pre-existing `KindFilters` test fixture (8 → 9 after T12 added `Diffusers` enum).

**Architecture:** Add `FindCanonicalHashFile` helper with 8-level priority (well-known paths → largest file by extension preference); extend `HashAndMatch` directory branch (cache key = folder path + total folder size + newest file mtime); extend Diffusers detection block to parse JSON `name` field; bump one stale assertion.

**Tech Stack:** .NET 8 / xUnit / Moq / `Microsoft.Data.Sqlite` 8.0.0

**Spec:** `docs/superpowers/specs/2026-08-25-diffusers-hash-matching-design.md`

## Global Constraints

- Scanner namespace: `ComfyUI.Manager.Services`; tests namespace: `ComfyUI.Manager.Tests.Services`
- Subsystem log string: `"model-scanner"` for scanner warnings (matches existing pattern at scanner:140/151/169)
- `DownloadedModel` is class with 12 init-only properties (`Models/ModelEntry.cs:181-204`); use the existing `CopyWith` helper at scanner:314-332 — do NOT introduce `with` expressions
- `LocalModelCard` IS positional record at `Models/ModelEntry.cs:236-251` — adding fields requires updating every `new LocalModelCard(...)` callsite; this plan adds NO new fields to either type
- Cache key shape `(path, size_bytes, mtime_utc_ticks)` from `CivitaiHashCache.Lookup(string path, long sizeBytes, long mtimeUtcTicks)` adapts for folders: path = folder dir, size_bytes = total folder size (sum of file lengths), mtime_utc_ticks = newest file mtime UTC ticks
- 23 existing scanner tests + 3 existing ScanContext tests must continue to pass unchanged
- Subsystem `"civitai-matcher"` for orchestrator logging (do NOT change — `CivitaiMatcherOrchestrator:17`)
- No new files. No DB schema changes. No new public types. `FindCanonicalHashFile` will be `internal static` (precedent: `BrowserLauncher.cs:83`); visible to tests via existing `InternalsVisibleTo("ComfyUI.Manager.Tests")` at `ComfyUI.Manager.csproj:57`
- `TaskList` baseline: 1924 PASS / 3 FAIL pre-existing flaky / 6 SKIP / 1933 total (per T13-7 SHIP-READY). Target: 1924 + 12 new = ~1936 PASS / 3 FAIL / 6 SKIP

---

### Task 1: FindCanonicalHashFile helper (8-level priority)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (add helper near other `Find*` helpers around line 240)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs` (append 5 tests after the T12 Diffusers section ending at line 512)

**Interfaces:**
- Produces: `internal static string? ModelFilesystemScanner.FindCanonicalHashFile(string dir)` — returns absolute file path to hash, or `null` if no matchable file exists

**Priority order (first match wins):**
1. `<dir>/unet/diffusion_pytorch_model.safetensors`
2. `<dir>/transformer/diffusion_pytorch_model.safetensors`
3. `<dir>/unet/diffusion_pytorch_model.bin`
4-7. Largest file in folder (recursive) by extension preference: `.safetensors` > `.bin` > `.ckpt` > `.pt`
8. `null` (no matchable file)

- [ ] **Step 1: Write the failing tests**

Append after the closing `}` of the last T12 Diffusers test (`Scan_DiffusersFolder_KindDirIsCheckpoints_StillDetected` at line 512) in `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs`:

```csharp
    // -------- Diffusers hash chain (T-D1): FindCanonicalHashFile helper tests --------

    [Fact]
    public void FindCanonicalHashFile_PrefersUnetSafetensors()
    {
        // unet/diffusion_pytorch_model.safetensors + vae/diffusion_pytorch_model.safetensors → unet (priority 1 wins)
        var dir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(dir, "unet");
        var vaeDir = Path.Combine(dir, "vae");
        Directory.CreateDirectory(unetDir);
        Directory.CreateDirectory(vaeDir);
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        var vaeFile = Path.Combine(vaeDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllText(unetFile, "unet");
        File.WriteAllText(vaeFile, "vae");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(unetFile, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToTransformerSafetensors()
    {
        // No unet. transformer/diffusion_pytorch_model.safetensors exists → transformer (priority 2)
        var dir = Path.Combine(_tmp, "diffusers", "flux-base");
        var transformerDir = Path.Combine(dir, "transformer");
        Directory.CreateDirectory(transformerDir);
        var transformerFile = Path.Combine(transformerDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllText(transformerFile, "transformer");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(transformerFile, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToUnetBin()
    {
        // No safetensors. unet/diffusion_pytorch_model.bin exists → unet bin (priority 3)
        var dir = Path.Combine(_tmp, "diffusers", "sd15-legacy");
        var unetDir = Path.Combine(dir, "unet");
        Directory.CreateDirectory(unetDir);
        var unetBin = Path.Combine(unetDir, "diffusion_pytorch_model.bin");
        File.WriteAllText(unetBin, "unet-bin");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(unetBin, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToLargestSafetensors()
    {
        // No well-known paths. 3 .safetensors files of sizes 100/500/200 → largest (500) wins
        var dir = Path.Combine(_tmp, "diffusers", "custom-layout");
        var sub1 = Path.Combine(dir, "sub1");
        var sub2 = Path.Combine(dir, "sub2");
        var sub3 = Path.Combine(dir, "sub3");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);
        Directory.CreateDirectory(sub3);
        var small1 = Path.Combine(sub1, "a.safetensors");
        var large = Path.Combine(sub2, "b.safetensors");
        var small2 = Path.Combine(sub3, "c.safetensors");
        File.WriteAllBytes(small1, new byte[100]);
        File.WriteAllBytes(large, new byte[500]);
        File.WriteAllBytes(small2, new byte[200]);

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(large, result);
    }

    [Fact]
    public void FindCanonicalHashFile_NoMatchableFiles_ReturnsNull()
    {
        // Only config files (model_index.json + json sidecars), no model files → null
        var dir = Path.Combine(_tmp, "diffusers", "config-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model_index.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "tokenizer_config.json"), "{}");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Null(result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~FindCanonicalHashFile" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 5 tests FAIL with `CS0103: The name 'FindCanonicalHashFile' does not exist in the current context` (or similar — the helper doesn't exist yet).

- [ ] **Step 3: Implement FindCanonicalHashFile helper**

In `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs`, add this method after the `FindFirstPngInDir` helper (after line 237, before `InferKind` at line 239):

```csharp
    /// <summary>v1.0.0 T-D1:Select a single file to hash from a Diffusers folder.
    /// Priority order (first match wins):
    /// 1. <c>unet/diffusion_pytorch_model.safetensors</c> (SD 1.5 / SDXL canonical)
    /// 2. <c>transformer/diffusion_pytorch_model.safetensors</c> (FLUX-style)
    /// 3. <c>unet/diffusion_pytorch_model.bin</c> (legacy .bin variant)
    /// 4-7. Largest file in folder (recursive) by extension preference:
    ///      <c>.safetensors</c> → <c>.bin</c> → <c>.ckpt</c> → <c>.pt</c>
    /// 8. None → return <c>null</c> (orchestrator may still match via safetensors/companion/filename).
    /// Internal so tests can call directly via <c>InternalsVisibleTo</c>.</summary>
    internal static string? FindCanonicalHashFile(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return null;

        foreach (var rel in new[]
        {
            "unet/diffusion_pytorch_model.safetensors",
            "transformer/diffusion_pytorch_model.safetensors",
            "unet/diffusion_pytorch_model.bin",
        })
        {
            var p = Path.Combine(dirPath, rel);
            if (File.Exists(p)) return p;
        }

        foreach (var ext in new[] { ".safetensors", ".bin", ".ckpt", ".pt" })
        {
            string? largest = null;
            long maxLen = -1;
            foreach (var f in Directory.EnumerateFiles(dirPath, "*" + ext, SearchOption.AllDirectories))
            {
                var len = new FileInfo(f).Length;
                if (len > maxLen) { maxLen = len; largest = f; }
            }
            if (largest is not null) return largest;
        }

        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run the same filter command from Step 2.

Expected: 5 tests PASS. Existing scanner tests unchanged (156/158 focused scope + 5 new = 161/163, with 2 SKIP from prior runs).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs
git commit -m "feat(v1.0.0): T-D1 FindCanonicalHashFile 8-level priority helper"
```

---

### Task 2: HashAndMatch directory branch + Diffusers folder integration tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (replace the `File.Exists` guard at line 265; rewrite the hash compute block at lines 263-281 to support directory target)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs` (append 3 tests after the existing 3 at line 81)

**Interfaces:**
- Consumes: `FindCanonicalHashFile(string dir)` from Task 1
- Consumes: `ModelHasher.ComputeSha256(string filePath, CancellationToken ct = default)` from `Services/Civitai/ModelHasher.cs:18`
- Consumes: `CivitaiHashCache.Lookup(string path, long sizeBytes, long mtimeUtcTicks)` and `.Store(...)` (already wired at scanner:268/278)
- Produces: `DownloadedModel.Hash` populated when Diffusers folder has a hashable file; `ctx.HashCache` contains an entry keyed by `(folderPath, totalFolderSize, newestFileMtimeUtcTicks)`

**Cache key for folders:** `path = m.FullPath` (folder dir), `size_bytes = sum of FileInfo.Length across all files (recursive)`, `mtime_utc_ticks = max of File.GetLastWriteTimeUtc.Ticks across all files (recursive)`. Empty folder → `size_bytes = 0`, `mtime_utc_ticks = 0`.

- [ ] **Step 1: Write the failing tests**

Append after the closing `}` of `Scan_WithContext_NoCacheHit_ComputesAndStoresHash` (the last test ending around line 81) in `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs`:

```csharp
    // -------- Diffusers folder hash chain (T-D2): directory branch tests --------

    [Fact]
    public void Scan_DiffusersFolder_WithContext_ComputesHashFromUnetFile()
    {
        // Diffusers folder with unet/diffusion_pytorch_model.safetensors → hash matches what
        // ModelHasher.ComputeSha256 produces from the unet file (not the folder, not model_index.json).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        Directory.CreateDirectory(unetDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.Equal(ModelKind.Diffusers, result[0].Kind);
        Assert.Equal(diffusersDir, result[0].FullPath);
        var expectedHash = ModelHasher.ComputeSha256(unetFile);
        Assert.Equal(expectedHash, result[0].Hash);
    }

    [Fact]
    public void Scan_DiffusersFolder_WithContext_CacheKeyUsesFolderPathAndTotalSize()
    {
        // After scan, cache has an entry keyed by (folderPath, totalFolderSize, newestMtimeUtcTicks).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        var teDir = Path.Combine(diffusersDir, "text_encoder");
        Directory.CreateDirectory(unetDir);
        Directory.CreateDirectory(teDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        var teFile = Path.Combine(teDir, "model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 100, 200, 300 });
        File.WriteAllBytes(teFile, new byte[] { 400, 500 });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        // Compute expected (folderPath, totalSize, newestMtimeUtcTicks)
        var totalSize = new FileInfo(unetFile).Length + new FileInfo(teFile).Length
            + new FileInfo(Path.Combine(diffusersDir, "model_index.json")).Length;
        var newestMtime = new[] { unetFile, teFile, Path.Combine(diffusersDir, "model_index.json") }
            .Select(f => new FileInfo(f).LastWriteTimeUtc.Ticks).Max();
        var cached = cache.Lookup(diffusersDir, totalSize, newestMtime);
        Assert.NotNull(cached);
        Assert.Equal(result[0].Hash, cached);
    }

    [Fact]
    public void Scan_DiffusersFolder_WithContext_RunsOrchestratorWithHashedModel()
    {
        // Mock IModelMatcher → orchestrator → ctx.Matcher. Verify matcher received the Diffusers
        // model with Hash populated (so chain strategies see it as a real hash hit).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        Directory.CreateDirectory(unetDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 1, 2, 3 });

        DownloadedModel? capturedModel = null;
        var matchMock = new Mock<IModelMatcher>();
        matchMock.SetupGet(m => m.Name).Returns("Hash");
        matchMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                 .Callback<DownloadedModel, CancellationToken>((dm, _) => capturedModel = dm)
                 .ReturnsAsync((MatchResult?)null);

        var orchestrator = new CivitaiMatcherOrchestrator(new IModelMatcher[] { matchMock.Object });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = orchestrator };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.NotNull(capturedModel);
        Assert.Equal(ModelKind.Diffusers, capturedModel!.Kind);
        Assert.Equal(diffusersDir, capturedModel.FullPath);
        Assert.NotNull(capturedModel.Hash);
        Assert.Equal(64, capturedModel.Hash!.Length);   // SHA256 hex
    }
```

This requires `using Moq;` at the top of the test file (the file currently does not import Moq — add it).

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~Scan_DiffusersFolder_WithContext" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 3 tests FAIL — current HashAndMatch silently skips directories via the `!File.Exists(m.FullPath)` guard at scanner:265, so `result[0].Hash` is `null`.

- [ ] **Step 3: Extend HashAndMatch for directory branch**

Replace the existing per-iteration block inside the `Parallel.For` in `HashAndMatch` at `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs:259-287` (the lambda body only — keep the Parallel.For wrapper unchanged). The new lambda body:

```csharp
            try
            {
                var m = byIndex[k]!;
                if (m.Hash is not null || ctx.HashCache is null) return;
                if (string.IsNullOrEmpty(m.FullPath)) return;

                // v1.0.0 T-D2:resolve hash target — file for single-file models, canonical file inside
                // Diffusers folder for multi-file. Skip if neither exists.
                string? hashTarget;
                long sizeBytes;
                long mtimeTicks;
                if (File.Exists(m.FullPath))
                {
                    hashTarget = m.FullPath;
                    var info = new FileInfo(m.FullPath);
                    sizeBytes = info.Length;
                    mtimeTicks = info.LastWriteTimeUtc.Ticks;
                }
                else if (Directory.Exists(m.FullPath))
                {
                    hashTarget = FindCanonicalHashFile(m.FullPath);
                    if (hashTarget is null) return;   // no hashable file — orchestrator may still match other strategies
                    var files = Directory.EnumerateFiles(m.FullPath, "*", SearchOption.AllDirectories).ToList();
                    sizeBytes = files.Sum(f => new FileInfo(f).Length);
                    mtimeTicks = files.Count > 0
                        ? files.Max(f => new FileInfo(f).LastWriteTimeUtc.Ticks)
                        : 0;
                }
                else
                {
                    return;
                }

                // Cache key uses the folder path for Diffusers (so it survives adding/removing files).
                // For single-file models, m.FullPath == hashTarget so the cache key is unchanged.
                var cached = ctx.HashCache.Lookup(m.FullPath, sizeBytes, mtimeTicks);
                string hash;
                if (cached is not null)
                {
                    hash = cached;
                    ctx.Progress?.Report($"[hash] cache hit: {Path.GetFileName(hashTarget)}");
                }
                else
                {
                    hash = ModelHasher.ComputeSha256(hashTarget);
                    ctx.HashCache.Store(m.FullPath, sizeBytes, mtimeTicks, hash);
                    ctx.Progress?.Report($"[hash] computed: {Path.GetFileName(hashTarget)} → {hash[..8]}…");
                }
                byIndex[k] = CopyWith(m, hash: hash);
            }
            catch (Exception ex)
            {
                ctx.Progress?.Report($"[scan] ⚠ hash failed: {byIndex[k]!.FullPath} {ex.GetType().Name}: {ex.Message}");
            }
```

Note: This adds `using System.Linq;` to the using directives at the top of the file (already present at line 5).

- [ ] **Step 4: Run tests to verify they pass**

Run the same filter command from Step 2.

Expected: 3 tests PASS. Existing 3 ScanContext tests + 5 FindCanonicalHashFile tests + 5 T12 Diffusers tests all still pass.

Then run the full focused scope to confirm no regressions:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~Civitai|FullyQualifiedName~LocalModels|FullyQualifiedName~ModelFilesystemScanner" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 156 + 5 (Task 1) + 3 (Task 2) = 164 PASS / 2 SKIP. No new regressions.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs
git commit -m "feat(v1.0.0): T-D2 HashAndMatch directory branch for Diffusers folders"
```

---

### Task 3: model_index.json name field extraction (4 tests)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (replace the Diffusers detection block at lines 107-132)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs` (append 4 tests after the 5 FindCanonicalHashFile tests)

**Interfaces:**
- Produces: `DownloadedModel.Title` set to `model_index.json["name"]` (string, non-empty) when present, else folder name (current behavior at scanner:120)
- Symlink loops: wrap the `Directory.EnumerateFiles` block in try/catch and skip folder on exception (spec §7 risk)
- Invalid JSON: log warning at `"model-scanner"` subsystem, fall back to folder name

- [ ] **Step 1: Write the failing tests**

Append after the 5 FindCanonicalHashFile tests in `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs`:

```csharp
    // -------- Diffusers model_index.json name field (T-D3): Title extraction tests --------

    [Fact]
    public void Scan_DiffusersFolder_NameFieldFromModelIndexJson_UsedAsTitle()
    {
        // model_index.json has top-level "name" field → Title = that name (not folder name)
        var diffusersDir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        Directory.CreateDirectory(diffusersDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"),
            "{\"name\": \"SDXL Base 1.0\", \"_class_name\": \"StableDiffusionXLPipeline\"}");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("SDXL Base 1.0", result[0].Title);
        Assert.Equal(diffusersDir, result[0].FullPath);
    }

    [Fact]
    public void Scan_DiffusersFolder_NoNameField_UsesFolderNameAsTitle()
    {
        // model_index.json exists but no "name" field → fall back to folder name (T12 behavior)
        var diffusersDir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        Directory.CreateDirectory(diffusersDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"),
            "{\"_class_name\": \"StableDiffusionXLPipeline\"}");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("sdxl-base", result[0].Title);
    }

    [Fact]
    public void Scan_DiffusersFolder_InvalidModelIndexJson_FallsBackToFolderName()
    {
        // model_index.json with invalid JSON → tolerate, fall back to folder name
        var diffusersDir = Path.Combine(_tmp, "diffusers", "broken");
        Directory.CreateDirectory(diffusersDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{invalid json");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("broken", result[0].Title);
    }

    [Fact]
    public void Scan_DiffusersFolder_EmptyModelIndexJson_FallsBackToFolderName()
    {
        // model_index.json is empty (zero bytes) → no name → fall back to folder name
        var diffusersDir = Path.Combine(_tmp, "diffusers", "empty");
        Directory.CreateDirectory(diffusersDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("empty", result[0].Title);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~Scan_DiffusersFolder_(NameField|NoNameField|InvalidModelIndex|EmptyModelIndex)" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 4 tests FAIL — current scanner always uses folder name for Diffusers Title (scanner:120).

- [ ] **Step 3: Extend Diffusers detection block to parse name field**

Replace the existing Diffusers detection block at `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs:107-132` with:

```csharp
                if (File.Exists(Path.Combine(modelDir, "model_index.json")))
                {
                    var subdirName = Path.GetFileName(modelDir);
                    // 跳过 hidden dirs(.DS_Store, .git 等)
                    if (!subdirName.StartsWith("."))
                    {
                        var title = ResolveDiffusersTitle(modelDir, subdirName);
                        DateTime latestMtime;
                        try
                        {
                            latestMtime = Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories)
                                .Select(File.GetLastWriteTime)
                                .DefaultIfEmpty(DateTime.MinValue)
                                .Max();
                        }
                        catch (Exception ex)
                        {
                            // v1.0.0 T-D3:symlink loops or perms errors — skip folder, don't crash scan
                            _logger?.Warn("model-scanner",
                                $"skip {modelDir}: enumerate failed {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }
                        var previewPath = FindFirstPngInDir(modelDir);
                        results.Add(new DownloadedModel
                        {
                            Title = title,
                            SubfolderName = kindName,
                            FullPath = modelDir,                                      // 目录路径,不是文件路径
                            Kind = ModelKind.Diffusers,                               // 强类型 = Diffusers
                            Source = "Local",
                            SourceId = $"local:{kindName}/{subdirName}".ToLowerInvariant(),
                            SourceVersionId = "",
                            DownloadedAt = latestMtime,                               // 子目录内最新文件 mtime(递归)
                            PreviewImagePath = previewPath,                           // subdir 内字典序 first .png
                        });
                    }
                    continue;   // 跳过后续 meta.json 路径 + 3-level per-file 扫描
                }
```

Then add this helper method (near `FindCanonicalHashFile` from Task 1):

```csharp
    /// <summary>v1.0.0 T-D3:Extract Title from <c>model_index.json["name"]</c>.
    /// Falls back to <paramref name="fallbackName"/> (folder name) when:
    /// file is empty, JSON is invalid, or <c>name</c> field is missing/empty/non-string.
    /// Logs warning at <c>"model-scanner"</c> on invalid JSON; silent on missing field.</summary>
    private static string ResolveDiffusersTitle(string modelDir, string fallbackName)
    {
        var indexPath = Path.Combine(modelDir, "model_index.json");
        try
        {
            var json = File.ReadAllText(indexPath);
            if (string.IsNullOrWhiteSpace(json)) return fallbackName;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(nameEl.GetString()))
            {
                return nameEl.GetString()!;
            }
        }
        catch (Exception ex)
        {
            // Invalid JSON or IO error — fall back, log so user can investigate
            // (use Console.WriteLine to avoid coupling to AppLogger here; the diffusers folder is
            // still detected, just with folder name as Title)
            Console.WriteLine($"[model-scanner] WARN: invalid model_index.json at {modelDir}: {ex.GetType().Name}: {ex.Message}");
        }
        return fallbackName;
    }
```

Note: This requires `using System.Text.Json;` at the top of the file (already present at line 7) and `JsonDocument`/`JsonValueKind` from same namespace.

The spec says "tolerate invalid JSON, log warning at 'model-scanner', use folder name" — using `Console.WriteLine` here is acceptable since `AppLogger` is not in scope for a static helper. If AppLogger access is preferred, pass `_logger` as a parameter and replace the Console.WriteLine with `_logger?.Warn(...)`. **Decision: pass `_logger` as parameter** to match the spec's subsystem string exactly. Replace the helper signature with:

```csharp
    private static string ResolveDiffusersTitle(string modelDir, string fallbackName, AppLogger? logger = null)
```

And replace `Console.WriteLine` with `logger?.Warn("model-scanner", $"invalid model_index.json at {modelDir}: {ex.GetType().Name}: {ex.Message}")`. Update the call site at line ~115 to pass `_logger`.

- [ ] **Step 4: Run tests to verify they pass**

Run the same filter command from Step 2.

Expected: 4 tests PASS.

Then run the full focused scope to confirm no regressions:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~Civitai|FullyQualifiedName~LocalModels|FullyQualifiedName~ModelFilesystemScanner" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 156 + 5 (Task 1) + 3 (Task 2) + 4 (Task 3) = 168 PASS / 2 SKIP. No regressions in the 23 existing scanner tests (5 T12 + 5 FindCanonicalHashFile + 13 standard layout/flat/preview).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerStandardLayoutTests.cs
git commit -m "feat(v1.0.0): T-D3 model_index.json name field + symlink loop guard"
```

---

### Task 4: KindFilters fixture update (1 test, 8 → 9)

**Files:**
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs:73-83` (single assertion update + comment refresh)

**Context:** T12 added `ModelKind.Diffusers` enum value but did not update the `KindFilters_ContainsAllModelKindValues` fixture, which still expects 8 visible filters. With 9 enum values minus the excluded `Unknown`, the correct count is now 8... wait — `KindFilters` excludes `Unknown` per spec §6.4: "9 enum values, 1 (Unknown) excluded = 8 visible filters". The test comment says 8 and the assertion is `Assert.Equal(8, vm.KindFilters.Count)`. But the spec says the test fixture is wrong (pre-existing breakage from T12).

Looking at the test fixture code (lines 73-83):
```csharp
public void KindFilters_ContainsAllModelKindValues()
{
    var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
    // 9 enum values, 1 (Unknown) excluded = 8 visible filters
    Assert.Equal(8, vm.KindFilters.Count);
    Assert.Contains(ModelKind.Checkpoint, vm.KindFilters);
    Assert.Contains(ModelKind.LORA, vm.KindFilters);
    Assert.Contains(ModelKind.VAE, vm.KindFilters);
    Assert.DoesNotContain(ModelKind.Unknown, vm.KindFilters);
}
```

The comment says "9 enum values, 1 (Unknown) excluded = 8 visible filters" but the actual count would be 9 if the Diffusers enum was added (9 total minus Unknown = 8... no wait, 9 total minus Unknown = 8 means there were 9 enum values already, and Unknown is excluded → 8 filters. With Diffusers added, total = 10, minus Unknown = 9).

Actually, let me reason from the spec and existing code:
- Pre-T12: 8 enum values total (Checkpoint/LORA/VAE/Controlnet/TextualInversion/Upscaler/Hypernetwork/Other/Unknown) — that's 9 actually
- The fixture comment "9 enum values, 1 (Unknown) excluded = 8 visible filters" suggests the pre-T12 baseline was 9 enums minus Unknown = 8 filters
- T12 added Diffusers: now 10 enums total, minus Unknown = 9 visible filters
- Test expects 8 but actual is 9 → pre-existing breakage

So the fix is `Assert.Equal(8, ...)` → `Assert.Equal(9, ...)`, comment "9 enum values" → "10 enum values, 1 (Unknown) excluded = 9 visible filters", and add `Assert.Contains(ModelKind.Diffusers, vm.KindFilters)`.

- [ ] **Step 1: Update the test fixture**

Replace lines 73-83 in `tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs` with:

```csharp
    [Fact]
    public void KindFilters_ContainsAllModelKindValues()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        // 10 enum values (T12 added Diffusers), 1 (Unknown) excluded = 9 visible filters
        Assert.Equal(9, vm.KindFilters.Count);
        Assert.Contains(ModelKind.Checkpoint, vm.KindFilters);
        Assert.Contains(ModelKind.LORA, vm.KindFilters);
        Assert.Contains(ModelKind.VAE, vm.KindFilters);
        Assert.Contains(ModelKind.Diffusers, vm.KindFilters);   // v1.0.0 T-D4:T12 added Diffusers
        Assert.DoesNotContain(ModelKind.Unknown, vm.KindFilters);
    }
```

- [ ] **Step 2: Run test to verify it passes**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~KindFilters_ContainsAllModelKindValues" --no-restore -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false --nologo
```

Expected: 1 test PASS.

- [ ] **Step 3: Commit**

```bash
git add tests-wpf/ComfyUI.Manager.Tests/ViewModels/ModelMarketplaceViewModelTests.cs
git commit -m "test(v1.0.0): T-D4 KindFilters fixture 8 → 9 (T12 added Diffusers)"
```

---

## Self-Review

**1. Spec coverage:**
- §4.1 (8-level priority) → Task 1
- §4.2 (HashAndMatch extension) → Task 2
- §4.3 (cache key adaptation) → Task 2 (cache key uses folder path + total folder size + newest mtime)
- §4.4 (model_index.json name field) → Task 3
- §4.5 (detection quality: symlinks, hidden dirs, invalid JSON, empty JSON) → Task 3 (symlink loop guard via try/catch + invalid JSON tolerance + empty JSON handling; hidden dir skip already exists at scanner:111)
- §4.6 (UI: no changes) → no task (confirmed)
- §6.1 (5 FindCanonicalHashFile tests) → Task 1
- §6.2 (3 HashAndMatch Diffusers tests) → Task 2
- §6.3 (4 model_index.json tests) → Task 3
- §6.4 (KindFilters test update) → Task 4
- §6.5 (existing tests still pass) → verified in each task's Step 4 with focused-scope full run
- §7 (risks: cache invalidation, symlink loops, cover download) → Tasks 2 + 3

**2. Placeholder scan:** No "TBD", "TODO", "implement later", "fill in details", "add appropriate error handling". All code blocks contain actual implementation.

**3. Type consistency:**
- `FindCanonicalHashFile` signature: `internal static string? FindCanonicalHashFile(string dirPath)` — Task 1 produces this, Task 2 consumes it ✓
- `CopyWith` signature unchanged from T13-6 (`scanner:314-332`) ✓
- `DownloadedModel` 12 fields unchanged ✓
- `LocalModelCard` 10 fields unchanged ✓
- `ModelHasher.ComputeSha256(string filePath, CancellationToken ct = default)` matches usage in Task 2 ✓
- `CivitaiHashCache.Lookup/Store` signatures match usage in Task 2 ✓
- `CivitaiMatcherOrchestrator(IReadOnlyList<IModelMatcher>, AppLogger?)` matches Task 2 Step 3 usage ✓
- `Mock<IModelMatcher>` setup matches existing `CivitaiMatcherOrchestratorTests.cs:43-60` pattern ✓
- `ResolveDiffusersTitle(string modelDir, string fallbackName, AppLogger? logger = null)` — used internally, no external callsite ✓

**4. Spec self-check vs reality:**
- Spec §4.1 has 8 priority levels (1-3 well-known + 4-7 largest by ext preference + 8 none). Task 1 implements priorities 1-3 + priorities 4-7 as a single extension-preference loop. **Spec §6.1 tests only cover priorities 1, 2, 3, 4 (.safetensors only), 8** — priorities 5/6/7 (.bin/.ckpt/.pt largest) share code path with priority 4 and are implicitly tested. Acceptable per test-economy.
- Spec §4.5 symlink risk: scan:267 catch in Parallel.For doesn't cover the `latestMtime` enumeration. **Task 3 adds the missing try/catch** (spec §7 acknowledges this risk). Going slightly beyond spec to harden pre-existing T12 code path.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-25-diffusers-hash-matching.md`. Four tasks, four commits, ~1936 PASS target.

Per user's prior `feedback_default_to_yes.md` and session memory `尽可能的不问yes`: proceeding with **Subagent-Driven** execution (recommended in writing-plans skill — fresh subagent per task + per-task review). No further confirmation gate.
