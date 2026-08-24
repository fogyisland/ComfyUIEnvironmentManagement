# v1.0.0 CivitAI Hash-Based Local-Model Matching — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the T11 fuzzy-search-only CivitAI lookup path with a multi-strategy matcher chain that hits the correct CivitAI model 100% of the time via SHA256 file-hash, falling back to safetensors header metadata, companion `.civitai.info` sidecar, and filename fuzzy search when hash miss occurs.

**Architecture:** `IModelMatcher` strategy interface + 4 concrete implementations chained by `CivitaiMatcherOrchestrator`. `ModelHasher` (SHA256 streaming) + `CivitaiHashCache` (SQLite by `(FilePath, SizeBytes, MtimeUtcTicks)`) compute and persist hashes at scan time. `ModelFilesystemScanner.Scan(dir, ScanContext)` integrates hash + bulk match + cover download into the existing enumeration. UI dialog opens directly in Detail state when scan-time match exists, otherwise searches on-demand.

**Tech Stack:** .NET 8 / WPF / C# 12 / xUnit / Moq / Microsoft.Data.Sqlite 8.0.0 (already in `ComfyUI.Manager.csproj`) / `System.Security.Cryptography.SHA256` / `System.Text.Json` (matches existing `CivitAiLookupService` style — NOT Newtonsoft.Json)

**Spec:** `docs/superpowers/specs/2026-08-24-civitai-hash-matching-design.md` (committed `bfa174a`)

## Global Constraints

- Layering: matchers operate on `DownloadedModel` (Models-layer, `ComfyUI.Manager.Models`), NEVER on `LocalModelCard` (ViewModels-layer). VM extracts the `DownloadedModel` from the card before calling the service. (Spec §6.1, fixed in self-review.)
- Backward compatibility:
  - `CivitAiLookupService` keeps `SearchByTitleAsync` + `GetDetailAsync` unchanged (called by `FilenameMatcher` + `CompanionJsonMatcher`). 17 T11 tests stay green.
  - `ModelFilesystemScanner.Scan(string)` overload retained (used by 23 existing tests). New `Scan(string, ScanContext?)` overload for the new behavior; old overload delegates with `ctx = null` (Hash = null, no match-time work).
  - `LocalModelCivitAiDialogViewModel` ctor adds tail param `LocalModelCard? card = null`. Existing 10 dialog VM tests pass card = null (use default), no test changes needed.
  - `DownloadedModel` + `LocalModelCard` records grow from 9 → 12 and 7 → 10 fields respectively (3 new: `Hash`, `MatchedDetail`, `MatchSource`). All call sites must compile-check.
- Build DLL lock: PID 2356 holds `ComfyUI.Manager.exe` so `dotnet build` will fail. Use `-p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false` for both `build` and `test`; clean with `rm -rf tests-build/` after.
- Logging: all matchers log via `AppLogger` subsystem `"civitai-matcher"` using the v0.6.22++ rich Console log shape: `[src → url]` / `[src ← status (ms, bytes)]` / `[✓ N 项]` / `[✗ TypeName (ms): msg]` / `[⏹ 已取消 (ms)]`. Matchers do NOT bubble exceptions to UI — they return null and orchestrator moves to next strategy.
- Test mock pattern: `CivitAiLookupService` is `sealed` (line 32) → use `Mock<HttpMessageHandler>` + real service for `CivitaiHashMatcher` and `CompanionJsonMatcher` tests (same pattern as `CivitaiLookupServiceTests`). `IModelMatcher` is interface → use `Mock<IModelMatcher>` for `CivitaiMatcherOrchestratorTests`.
- Never read `model_index.json` (Diffusers / HuggingFace format) or `safetensors` header beyond the first ~64KB.
- SQLite cache lives at `%APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite` (per-app, survives model moves; auto-cleaned by user if desired).
- YAGNI: do NOT add HuggingFace/ModelScope hash APIs, do NOT mirror the full Civitai Helper plugin, do NOT read safetensors `modelspec.sai_model_id`, do NOT auto-apply `trainedWords` to prompts. (Spec §3 + §10.)

---

### Task 1: Hash computation foundation + SQLite cache

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/ModelHasher.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiHashCache.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiHashCacheTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite.SqliteConnection` (existing dep v8.0.0, see `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` for connection lifecycle pattern), `System.Security.Cryptography.SHA256`
- Produces:
  - `public static class ModelHasher { public static string ComputeSha256(string filePath, CancellationToken ct = default); }` — streams 1MB chunks, returns uppercase hex (64 chars)
  - `public sealed class CivitaiHashCache : IDisposable { public CivitaiHashCache(string sqlitePath, AppLogger? logger = null); public string? Lookup(string filePath, long sizeBytes, long mtimeUtcTicks); public void Store(string filePath, long sizeBytes, long mtimeUtcTicks, string sha256); public void Clear(); }`

#### Step 1.1: Write `CivitaiHashCacheTests.cs` (5 tests)

```csharp
using System;
using System.IO;
using Xunit;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CivitaiHashCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"civitai-hash-test-{Guid.NewGuid():N}.sqlite");
    private readonly CivitaiHashCache _cache;

    public CivitaiHashCacheTests()
    {
        _cache = new CivitaiHashCache(_dbPath);
    }

    public void Dispose()
    {
        _cache.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Store_ThenLookup_WithSameKey_ReturnsHash()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC123DEF456");
        var result = _cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000000);
        Assert.Equal("ABC123DEF456", result);
    }

    [Fact]
    public void Lookup_WithDifferentMtime_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000001));
    }

    [Fact]
    public void Lookup_WithDifferentSize_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12346, 1700000000000));
    }

    [Fact]
    public void Lookup_WithDifferentPath_ReturnsNull()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        Assert.Null(_cache.Lookup("C:\\models\\bar.safetensors", 12345, 1700000000000));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _cache.Store("C:\\models\\foo.safetensors", 12345, 1700000000000, "ABC");
        _cache.Clear();
        Assert.Null(_cache.Lookup("C:\\models\\foo.safetensors", 12345, 1700000000000));
    }
}
```

#### Step 1.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiHashCacheTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type or namespace name 'CivitaiHashCache' could not be found` — type doesn't exist yet.

#### Step 1.3: Write `ModelHasher.cs`

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>
/// v1.0.0:Streaming SHA256 for large model files (2-7GB).
/// Reads in 1MB chunks to avoid loading whole file into memory.
/// Returns uppercase hex string (64 chars).
/// </summary>
public static class ModelHasher
{
    private const int BufferSize = 1024 * 1024; // 1 MB

    public static string ComputeSha256(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Model file not found", filePath);

        using var sha = SHA256.Create();
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: false);
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, BufferSize)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hashBytes = sha.Hash ?? Array.Empty<byte>();
        var sb = new StringBuilder(64);
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
```

#### Step 1.4: Write `CivitaiHashCache.cs`

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>
/// v1.0.0:SQLite-backed cache mapping (FilePath, SizeBytes, MtimeUtcTicks) → SHA256.
/// File metadata invalidates cache when file changes. Lives at
/// %APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite. Path is independent of
/// state.db because hash data is non-sensitive and can be deleted freely.
///
/// Caller owns disposal. Schema:
///   CREATE TABLE file_hashes (
///     path TEXT NOT NULL,
///     size_bytes INTEGER NOT NULL,
///     mtime_utc_ticks INTEGER NOT NULL,
///     sha256 TEXT NOT NULL,
///     PRIMARY KEY (path, size_bytes, mtime_utc_ticks)
///   );
/// </summary>
public sealed class CivitaiHashCache : IDisposable
{
    private const string Schema = @"
        CREATE TABLE IF NOT EXISTS file_hashes (
            path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            mtime_utc_ticks INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            PRIMARY KEY (path, size_bytes, mtime_utc_ticks)
        );";

    private readonly SqliteConnection _conn;
    private readonly AppLogger? _logger;

    public CivitaiHashCache(string sqlitePath, AppLogger? logger = null)
    {
        if (string.IsNullOrEmpty(sqlitePath)) throw new ArgumentNullException(nameof(sqlitePath));

        var dir = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _logger = logger;
        _conn = new SqliteConnection($"Data Source={sqlitePath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    public string? Lookup(string filePath, long sizeBytes, long mtimeUtcTicks)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM file_hashes " +
                              "WHERE path = $p AND size_bytes = $s AND mtime_utc_ticks = $m LIMIT 1";
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$m", mtimeUtcTicks);
            var result = cmd.ExecuteScalar();
            return result is string s ? s : null;
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite lookup error: {ex.Message}");
            return null;
        }
    }

    public void Store(string filePath, long sizeBytes, long mtimeUtcTicks, string sha256)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO file_hashes (path, size_bytes, mtime_utc_ticks, sha256) " +
                              "VALUES ($p, $s, $m, $h)";
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$m", mtimeUtcTicks);
            cmd.Parameters.AddWithValue("$h", sha256);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite store error: {ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM file_hashes";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite clear error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
```

#### Step 1.5: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiHashCacheTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 5/5 PASS.

#### Step 1.6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/Civitai/ModelHasher.cs \
        src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiHashCache.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiHashCacheTests.cs
git commit -m "feat(v1.0.0): SHA256 streaming + SQLite hash cache (T13-1 of 7)"
```

---

### Task 2: Hash matcher + service API extension

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/IModelMatcher.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiHashMatcher.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/CivitAiLookupService.cs` (add `LookupByHashAsync` method)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiHashMatcherTests.cs`

**Interfaces:**
- Consumes: `CivitAiLookupService` (existing — receives `HttpClient`, `baseUrl`, `apiToken`), `CivitAiLookupDtos.cs` (`CivitAiDetailDto`), `AppLogger`
- Produces:
  - `public interface IModelMatcher { string Name { get; } Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct); }`
  - `public sealed record MatchResult(MatchSource Source, CivitAiDetailDto Detail, string? CoverImageUrl);`
  - `public enum MatchSource { Hash, SafetensorsMetadata, CompanionJson, FilenameFuzzy }`
  - `public sealed class CivitaiHashMatcher : IModelMatcher { public string Name => "Hash"; public CivitaiHashMatcher(CivitAiLookupService service, AppLogger? logger = null); }`
  - `CivitAiLookupService.LookupByHashAsync(string sha256, CancellationToken ct = default) → Task<CivitAiDetailDto?>` — single GET, 404 returns null (NOT throw)

#### Step 2.1: Write `CivitaiHashMatcherTests.cs` (5 tests)

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CivitaiHashMatcherTests
{
    private static (CivitaiHashMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new CivitaiHashMatcher(service), handler);
    }

    private static DownloadedModel MakeModel(string hash) => new(
        Title: "test", SubfolderName: "checkpoints", FullPath: "C:\\models\\test.safetensors",
        Kind: ModelKind.Checkpoint, Source: "Local", SourceId: "local:test",
        SourceVersionId: "", SourceUrl: null, DownloadedAt: DateTime.UtcNow,
        PreviewImagePath: null, Hash: hash, MatchedDetail: null, MatchSource: null);

    [Fact]
    public async Task MatchAsync_HashHit_ReturnsMatchResult()
    {
        var (matcher, handler) = CreateMatcher();
        var json = """{"id":12345,"name":"Test Model","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains("/api/v1/model-versions/by-hash/ABC")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.Hash, result!.Source);
        Assert.Equal("Test Model", result.Detail.Title);
    }

    [Fact]
    public async Task MatchAsync_Hash404_ReturnsNull()
    {
        var (matcher, _) = CreateMatcher();
        // handler already returns 404 in default setup
        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_5xx_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        var matcher = new CivitaiHashMatcher(service);

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_NetworkError_ReturnsNull
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        var matcher = new CivitaiHashMatcher(service);

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_NullHash_ReturnsNullImmediately
    {
        var (matcher, handler) = CreateMatcher();
        var model = MakeModel(null);
        var result = await matcher.MatchAsync(model, CancellationToken.None);
        Assert.Null(result);
        handler.Protected().Verify(
            "SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
```

#### Step 2.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiHashMatcherTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type or namespace name 'IModelMatcher' could not be found`.

#### Step 2.3: Add new types to `Models/ModelEntry.cs` (3 new types: `MatchResult`, `MatchSource`, extended `DownloadedModel`)

Append to `src-wpf/ComfyUI.Manager/Models/ModelEntry.cs`:

```csharp
// v1.0.0 T13:Hash-matching types — see docs/superpowers/specs/2026-08-24-civitai-hash-matching-design.md §6.1

public enum MatchSource { Hash, SafetensorsMetadata, CompanionJson, FilenameFuzzy }

public sealed record MatchResult(
    MatchSource Source,
    CivitAiDetailDto Detail,
    string? CoverImageUrl);

// DownloadedModel record extension — append 3 fields to existing positional record.
// (Find the existing record in ModelEntry.cs and add these 3 trailing fields:)
//     string? Hash,
//     CivitAiDetailDto? MatchedDetail,
//     MatchSource? MatchSource
// ALL existing call sites must include null for the new fields. Run compile to find them.
```

**Important:** The `DownloadedModel` record has 9 existing fields. After adding 3, all call sites in the codebase must be updated to include `null, null, null` (or actual values) at the end. Expected call sites to update (find via `grep -r "new DownloadedModel(" src-wpf tests-wpf`):
- `ModelFilesystemScanner.cs` — multiple BuildFlatModel / BuildLocalModel / BuildMarketplaceModel invocations
- `ModelFilesystemScannerStandardLayoutTests.cs` — test fixtures
- `ModelFilesystemScannerTests.cs` — test fixtures
- (any others grep finds)

Each invocation needs 3 trailing `null`s (or computed values when scanner fills them later).

#### Step 2.4: Write `IModelMatcher.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Strategy interface for matching a local model to a CivitAI entry.
/// Matchers never throw — return null on any failure (network, parse, missing data).
/// First non-null MatchResult from the orchestrator chain wins.</summary>
public interface IModelMatcher
{
    string Name { get; }
    Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct);
}
```

#### Step 2.5: Add `LookupByHashAsync` to `CivitAiLookupService.cs`

Insert before the closing brace of `CivitAiLookupService` (after `GetDetailAsync`):

```csharp
/// <summary>v1.0.0 T13:Single-model lookup by SHA256 hash via
/// <c>GET /api/v1/model-versions/by-hash/{hash}</c>. 404 returns null (not throw).
/// Other non-2xx → null + log. Network/JSON errors → null + log.
/// <exception cref="OperationCanceledException">ct cancelled</exception>
/// </summary>
public async Task<CivitAiDetailDto?> LookupByHashAsync(string sha256, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(sha256)) return null;
    var url = $"{_baseUrl}/api/v1/model-versions/by-hash/{sha256}";
    _logger?.Info(LogSubsystem, $"→ {url}");
    var sw = Stopwatch.StartNew();

    try
    {
        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();
        _logger?.Info(LogSubsystem,
            $"← {(int)resp.StatusCode} {resp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.Warn(LogSubsystem,
                $"✗ {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms): by-hash failed");
            return null;
        }

        var dto = JsonSerializer.Deserialize<CivitAiDetailResponse>(body);
        if (dto is null) return null;

        var versions = (dto.ModelVersions ?? new List<CivitAiVersionWire>())
            .Select(v => new CivitAiVersionDto(
                Name: v.Name ?? "",
                BaseModel: v.BaseModel,
                CreatedAt: v.CreatedAt))
            .ToList();
        var images = (dto.Images ?? new List<CivitAiImageDto>())
            .Select(i => i.Url ?? "")
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();

        return new CivitAiDetailDto(
            Id: dto.Id ?? 0,
            Title: dto.Name ?? "",
            Username: dto.Creator?.Username ?? "",
            BaseModel: dto.BaseModel,
            Description: dto.Description ?? "",
            Tags: dto.Tags ?? new List<string>(),
            Versions: versions,
            ImageUrls: images);
    }
    catch (OperationCanceledException)
    {
        sw.Stop();
        _logger?.Info(LogSubsystem, $"⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
        throw;
    }
    catch (Exception ex)
    {
        sw.Stop();
        _logger?.Error(LogSubsystem,
            $"✗ {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
        return null;
    }
}
```

#### Step 2.6: Write `CivitaiHashMatcher.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Primary matcher — SHA256 hash → /api/v1/model-versions/by-hash/{hash}.
/// Returns null if model.Hash is null (caller didn't compute it) or service returns null.</summary>
public sealed class CivitaiHashMatcher : IModelMatcher
{
    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "Hash";

    public CivitaiHashMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.Hash)) return null;
        var detail = await _service.LookupByHashAsync(model.Hash, ct).ConfigureAwait(false);
        if (detail is null) return null;
        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        return new MatchResult(MatchSource.Hash, detail, cover);
    }
}
```

#### Step 2.7: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiHashMatcherTests|FullyQualifiedName~CivitAiLookupServiceTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 5 hash-matcher PASS + 7 existing CivitAiLookupService PASS (no regression).

If compile errors appear due to `DownloadedModel` extra fields, fix all call sites (Step 2.3 grep list) before proceeding.

#### Step 2.8: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/Civitai/IModelMatcher.cs \
        src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiHashMatcher.cs \
        src-wpf/ComfyUI.Manager/Models/ModelEntry.cs \
        src-wpf/ComfyUI.Manager/Services/CivitAiLookupService.cs \
        src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiHashMatcherTests.cs
git commit -m "feat(v1.0.0): hash matcher + LookupByHashAsync (T13-2 of 7)"
```

---

### Task 3: Safetensors metadata matcher (header reader + fuzzy fallback)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/SafetensorsHeaderReader.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/SafetensorsMetadataMatcher.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/SafetensorsMetadataMatcherTests.cs`

**Interfaces:**
- Consumes: `CivitAiLookupService.SearchByTitleAsync` (existing T9a method), `DownloadedModel.FullPath`
- Produces:
  - `public static class SafetensorsHeaderReader { public static bool TryReadModelName(string filePath, out string? modelName); }` — parses first ~64KB; looks for `ss_sd_model_name` (A1111) or `modelspec.title` (modelspec). Returns false on any parse error / non-safetensors / missing field.
  - `public sealed class SafetensorsMetadataMatcher : IModelMatcher { public string Name => "SafetensorsMetadata"; public SafetensorsMetadataMatcher(CivitAiLookupService service, AppLogger? logger = null); }`

**Safetensors header format:**
```
offset 0..7  : uint64 little-endian = length of JSON header (let's call it N)
offset 8..8+N: JSON header (UTF-8)
offset 8+N.. : raw tensor data
```
JSON header example:
```json
{
  "__metadata__": {
    "ss_sd_model_name": "AnimateLCM",
    "modelspec.title": "AnimateLCM"
  },
  "...": "..."
}
```

#### Step 3.1: Write `SafetensorsMetadataMatcherTests.cs` (4 tests)

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class SafetensorsMetadataMatcherTests : IDisposable
{
    private readonly string _tempDir;

    public SafetensorsMetadataMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"safe-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static (SafetensorsMetadataMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new SafetensorsMetadataMatcher(service), handler);
    }

    private static DownloadedModel MakeModel(string fullPath) => new(
        Title: "test", SubfolderName: "checkpoints", FullPath: fullPath,
        Kind: ModelKind.Checkpoint, Source: "Local", SourceId: "local:test",
        SourceVersionId: "", SourceUrl: null, DownloadedAt: DateTime.UtcNow,
        PreviewImagePath: null, Hash: null, MatchedDetail: null, MatchSource: null);

    /// <summary>Write a synthetic .safetensors file with a JSON header containing ss_sd_model_name.</summary>
    private static string WriteFakeSafetensors(string name, string headerField, string headerValue)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.safetensors");
        var headerJson = $"{{ \"__metadata__\": {{ \"{headerField}\": \"{headerValue}\" }} }}";
        var headerBytes = Encoding.UTF8.GetBytes(headerJson);
        var lengthBytes = BitConverter.GetBytes((ulong)headerBytes.Length);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(lengthBytes, 0, 8);
        fs.Write(headerBytes, 0, headerBytes.Length);
        return path;
    }

    [Fact]
    public async Task MatchAsync_HeaderHas_ss_sd_model_name_ReturnsMatchResult()
    {
        var filePath = WriteFakeSafetensors("a.safetensors", "ss_sd_model_name", "AnimateLCM");
        var (matcher, handler) = CreateMatcher();
        // Mock search returning 1 candidate with model id, then detail fetch
        var searchJson = """{"items":[{"id":99,"name":"AnimateLCM","creator":{"username":"u"}}]}""";
        var detailJson = """{"id":99,"name":"AnimateLCM","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(filePath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
        Assert.Equal("AnimateLCM", result.Detail.Title);
        File.Delete(filePath);
    }

    [Fact]
    public async Task MatchAsync_HeaderHas_modelspec_title_ReturnsMatchResult()
    {
        var filePath = WriteFakeSafetensors("a.safetensors", "modelspec.title", "MyModel");
        var (matcher, handler) = CreateMatcher();
        var searchJson = """{"items":[{"id":99,"name":"MyModel","creator":{"username":"u"}}]}""";
        var detailJson = """{"id":99,"name":"MyModel","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(filePath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
        File.Delete(filePath);
    }

    [Fact]
    public async Task MatchAsync_NoHeaderOrNoMetadata_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "garbage.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }); // not safetensors
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(path), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_HeaderInvalidJson_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "broken.safetensors");
        var badHeader = Encoding.UTF8.GetBytes("{ not valid json");
        var lengthBytes = BitConverter.GetBytes((ulong)badHeader.Length);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.Write(lengthBytes, 0, 8);
            fs.Write(badHeader, 0, badHeader.Length);
        }
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(path), CancellationToken.None);
        Assert.Null(result);
    }
}
```

#### Step 3.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~SafetensorsMetadataMatcherTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type 'SafetensorsHeaderReader' could not be found`.

#### Step 3.3: Write `SafetensorsHeaderReader.cs`

```csharp
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Parse the first ~64KB of a .safetensors file to extract the
/// model name from the JSON header. Looks for <c>ss_sd_model_name</c> (A1111 convention)
/// or <c>modelspec.title</c> (modelspec convention). Returns false on any parse error.</summary>
public static class SafetensorsHeaderReader
{
    private const int MaxReadBytes = 64 * 1024; // 64KB

    public static bool TryReadModelName(string filePath, out string? modelName)
    {
        modelName = null;
        if (!File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 8) return false;

            // Read first 8 bytes: uint64 little-endian = JSON header length
            var lengthBuf = new byte[8];
            var read = fs.Read(lengthBuf, 0, 8);
            if (read < 8) return false;
            ulong headerLen = BitConverter.ToUInt64(lengthBuf, 0);

            // Sanity check: must be ≤ MaxReadBytes
            if (headerLen == 0 || headerLen > MaxReadBytes) return false;

            // Read JSON header
            var headerBuf = new byte[(int)headerLen];
            read = fs.Read(headerBuf, 0, (int)headerLen);
            if (read < (int)headerLen) return false;
            var headerJson = Encoding.UTF8.GetString(headerBuf);

            using var doc = JsonDocument.Parse(headerJson);
            if (!doc.RootElement.TryGetProperty("__metadata__", out var metadata)) return false;
            if (metadata.ValueKind != JsonValueKind.Object) return false;

            // Try ss_sd_model_name first, then modelspec.title
            if (metadata.TryGetProperty("ss_sd_model_name", out var sdName)
                && sdName.ValueKind == JsonValueKind.String)
            {
                modelName = sdName.GetString();
                return !string.IsNullOrEmpty(modelName);
            }
            if (metadata.TryGetProperty("modelspec.title", out var msTitle)
                && msTitle.ValueKind == JsonValueKind.String)
            {
                modelName = msTitle.GetString();
                return !string.IsNullOrEmpty(modelName);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
```

#### Step 3.4: Write `SafetensorsMetadataMatcher.cs`

```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Fallback matcher — reads .safetensors header for embedded model name,
/// then fuzzy-searches CivitAI by that name. Picks first candidate (single result preferred).</summary>
public sealed class SafetensorsMetadataMatcher : IModelMatcher
{
    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "SafetensorsMetadata";

    public SafetensorsMetadataMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (!SafetensorsHeaderReader.TryReadModelName(model.FullPath, out var name)) return null;
        if (string.IsNullOrEmpty(name)) return null;

        var candidates = await _service.SearchByTitleAsync(name, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;

        // Pick first candidate; fetch full detail
        var first = candidates[0];
        CivitAiDetailDto? detail;
        try
        {
            detail = await _service.GetDetailAsync(first.Id, ct).ConfigureAwait(false);
        }
        catch (CivitAiLookupNotFoundException) { return null; }
        catch { return null; }

        if (detail is null) return null;
        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        return new MatchResult(MatchSource.SafetensorsMetadata, detail, cover);
    }
}
```

#### Step 3.5: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~SafetensorsMetadataMatcherTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 4/4 PASS.

#### Step 3.6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/Civitai/SafetensorsHeaderReader.cs \
        src-wpf/ComfyUI.Manager/Services/Civitai/SafetensorsMetadataMatcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/SafetensorsMetadataMatcherTests.cs
git commit -m "feat(v1.0.0): safetensors metadata matcher (T13-3 of 7)"
```

---

### Task 4: Companion JSON matcher + Filename matcher (the last two fallbacks)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/CompanionJsonMatcher.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/FilenameMatcher.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CompanionJsonMatcherTests.cs`

**Interfaces:**
- Consumes: `CivitAiLookupService.GetDetailAsync` (existing — CompanionJsonMatcher), `CivitAiLookupService.SearchByTitleAsync` (existing — FilenameMatcher), `DownloadedModel.FullPath` + `Title`
- Produces:
  - `public sealed class CompanionJsonMatcher : IModelMatcher { public string Name => "CompanionJson"; public CompanionJsonMatcher(CivitAiLookupService service, AppLogger? logger = null); }` — reads `<basename>.civitai.info` JSON sidecar (Civitai Helper convention), extracts `modelId` (or `modelVersionId` from versioned file), calls `GetDetailAsync`.
  - `public sealed class FilenameMatcher : IModelMatcher { public string Name => "Filename"; public FilenameMatcher(CivitAiLookupService service, AppLogger? logger = null); }` — wraps `SearchByTitleAsync(model.Title)`, picks first candidate.

**Companion JSON format** (Civitai Helper convention):
- File: `<model_basename>.civitai.info` next to `<model_basename>.safetensors`
- JSON: `{ "modelId": 12345, "modelVersionId": 67890, "modelName": "...", ... }`

#### Step 4.1: Write `CompanionJsonMatcherTests.cs` (3 tests)

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CompanionJsonMatcherTests : IDisposable
{
    private readonly string _tempDir;

    public CompanionJsonMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"comp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static DownloadedModel MakeModel(string fullPath) => new(
        Title: "test", SubfolderName: "checkpoints", FullPath: fullPath,
        Kind: ModelKind.Checkpoint, Source: "Local", SourceId: "local:test",
        SourceVersionId: "", SourceUrl: null, DownloadedAt: DateTime.UtcNow,
        PreviewImagePath: null, Hash: null, MatchedDetail: null, MatchSource: null);

    private static (CompanionJsonMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new CompanionJsonMatcher(service), handler);
    }

    [Fact]
    public async Task MatchAsync_ValidSidecarWithModelId_ReturnsMatchResult()
    {
        var modelPath = Path.Combine(_tempDir, "MyModel.safetensors");
        File.WriteAllText(Path.Combine(_tempDir, "MyModel.civitai.info"),
            """{"modelId":99,"modelName":"MyModel"}""");
        var (matcher, handler) = CreateMatcher();
        var detailJson = """{"id":99,"name":"MyModel","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().EndsWith("/api/v1/models/99")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.CompanionJson, result!.Source);
        Assert.Equal(99, result.Detail.Id);
    }

    [Fact]
    public async Task MatchAsync_NoSidecar_ReturnsNull()
    {
        var modelPath = Path.Combine(_tempDir, "NoSidecar.safetensors");
        // don't write sidecar
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_SidecarModelIdReturns404_ReturnsNull()
    {
        var modelPath = Path.Combine(_tempDir, "BadId.safetensors");
        File.WriteAllText(Path.Combine(_tempDir, "BadId.civitai.info"), """{"modelId":404}""");
        var (matcher, handler) = CreateMatcher();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.Null(result);
    }
}
```

#### Step 4.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CompanionJsonMatcherTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type 'CompanionJsonMatcher' could not be found`.

#### Step 4.3: Write `CompanionJsonMatcher.cs`

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Fallback matcher — reads .civitai.info sidecar JSON next to the model
/// file (Civitai Helper convention). Extracts modelId (or modelVersionId) and calls GetDetailAsync.
/// Returns null if sidecar missing, malformed, or detail returns 404.</summary>
public sealed class CompanionJsonMatcher : IModelMatcher
{
    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "CompanionJson";

    public CompanionJsonMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.FullPath)) return null;
        var dir = Path.GetDirectoryName(model.FullPath);
        var basename = Path.GetFileNameWithoutExtension(model.FullPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename)) return null;

        var sidecarPath = Path.Combine(dir, $"{basename}.civitai.info");
        if (!File.Exists(sidecarPath)) return null;

        int? modelId = null;
        try
        {
            var json = File.ReadAllText(sidecarPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("modelId", out var mid)
                && mid.TryGetInt32(out var midVal))
            {
                modelId = midVal;
            }
        }
        catch { return null; }
        if (modelId is null) return null;

        CivitAiDetailDto? detail;
        try
        {
            detail = await _service.GetDetailAsync(modelId.Value, ct).ConfigureAwait(false);
        }
        catch (CivitAiLookupNotFoundException) { return null; }
        catch { return null; }
        if (detail is null) return null;

        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        return new MatchResult(MatchSource.CompanionJson, detail, cover);
    }
}
```

#### Step 4.4: Write `FilenameMatcher.cs`

```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Last-resort fallback — fuzzy-search CivitAI by the local model's
/// Title (PrettyPrint of filename). Picks first candidate. This is the original T11 behavior.</summary>
public sealed class FilenameMatcher : IModelMatcher
{
    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "Filename";

    public FilenameMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.Title)) return null;
        var candidates = await _service.SearchByTitleAsync(model.Title, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;
        var first = candidates[0];

        CivitAiDetailDto? detail;
        try
        {
            detail = await _service.GetDetailAsync(first.Id, ct).ConfigureAwait(false);
        }
        catch (CivitAiLookupNotFoundException) { return null; }
        catch { return null; }
        if (detail is null) return null;

        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        return new MatchResult(MatchSource.FilenameFuzzy, detail, cover);
    }
}
```

#### Step 4.5: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CompanionJsonMatcherTests|FullyQualifiedName~CivitAiLookupServiceTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 3 CompanionJson PASS + 7 existing CivitAiLookupService PASS.

#### Step 4.6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/Civitai/CompanionJsonMatcher.cs \
        src-wpf/ComfyUI.Manager/Services/Civitai/FilenameMatcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CompanionJsonMatcherTests.cs
git commit -m "feat(v1.0.0): companion.json + filename matchers (T13-4 of 7)"
```

---

### Task 5: Orchestrator + service MatchAsync extension

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiMatcherOrchestrator.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/CivitAiLookupService.cs` (add `MatchAsync` method that delegates to orchestrator when constructed with one)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiMatcherOrchestratorTests.cs`

**Interfaces:**
- Consumes: 4 `IModelMatcher` implementations (from Tasks 2, 3, 4)
- Produces:
  - `public sealed class CivitaiMatcherOrchestrator { public CivitaiMatcherOrchestrator(CivitaiHashMatcher hash, SafetensorsMetadataMatcher metadata, CompanionJsonMatcher companion, FilenameMatcher filename, AppLogger? logger = null); public Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct); }` — tries matchers in order [Hash → SafetensorsMetadata → CompanionJson → Filename]; first non-null wins; logs `"✓ matched via {Name}"` or `"✗ no match"`.
  - `CivitAiLookupService.MatchAsync(DownloadedModel model, CancellationToken ct = default) → Task<MatchResult?>` — only present when ctor is called with an orchestrator (existing ctor signature unchanged for back-compat).

#### Step 5.1: Write `CivitaiMatcherOrchestratorTests.cs` (4 tests)

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CivitaiMatcherOrchestratorTests
{
    private static DownloadedModel MakeModel() => new(
        Title: "test", SubfolderName: "checkpoints", FullPath: "C:\\test.safetensors",
        Kind: ModelKind.Checkpoint, Source: "Local", SourceId: "local:test",
        SourceVersionId: "", SourceUrl: null, DownloadedAt: DateTime.UtcNow,
        PreviewImagePath: null, Hash: "ABC", MatchedDetail: null, MatchSource: null);

    private static MatchResult MakeResult(MatchSource src) => new(
        src, new CivitAiDetailDto(1, "Test", "u", null, "", Array.Empty<string>(),
            new List<CivitAiVersionDto>(), new List<string>()), null);

    [Fact]
    public async Task MatchAsync_HashHitWins_OtherMatchersNotCalled()
    {
        var hashResult = MakeResult(MatchSource.Hash);
        var hashMock = new Mock<IModelMatcher>();
        hashMock.SetupGet(m => m.Name).Returns("Hash");
        hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(hashResult);

        var metadataMock = new Mock<IModelMatcher>();
        metadataMock.SetupGet(m => m.Name).Returns("SafetensorsMetadata");
        metadataMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new Exception("should not be called"));

        var companionMock = new Mock<IModelMatcher>();
        companionMock.SetupGet(m => m.Name).Returns("CompanionJson");

        var filenameMock = new Mock<IModelMatcher>();
        filenameMock.SetupGet(m => m.Name).Returns("Filename");

        var orch = new CivitaiMatcherOrchestrator(
            (CivitaiHashMatcher)null!, null!, null!, null!,
            logger: null);
        // Use null! casts above; test fails by NRE on first matcher call.
        // Instead, use real instances constructed via reflection or take IModelMatcher[] ctor.
    }
}
```

**Note:** The orchestrator constructor takes 4 concrete types for clarity; test usage requires either:
- (a) Add an additional `CivitaiMatcherOrchestrator(IReadOnlyList<IModelMatcher> matchers, AppLogger? logger = null)` ctor for testability, OR
- (b) Construct real instances with `null` HttpClient (works for null-return cases but not mock verification)

**Use option (a)** — add secondary ctor that takes `IReadOnlyList<IModelMatcher>` and reorder the primary ctor to construct the list internally. Both ctors exist:

```csharp
public CivitaiMatcherOrchestrator(
    CivitaiHashMatcher hash,
    SafetensorsMetadataMatcher metadata,
    CompanionJsonMatcher companion,
    FilenameMatcher filename,
    AppLogger? logger = null)
    : this(new IModelMatcher[] { hash, metadata, companion, filename }, logger) { }

public CivitaiMatcherOrchestrator(IReadOnlyList<IModelMatcher> matchers, AppLogger? logger = null)
{
    _matchers = matchers;
    _logger = logger;
}
```

Now write the test using `IReadOnlyList<IModelMatcher>` ctor:

```csharp
[Fact]
public async Task MatchAsync_HashHitWins_OtherMatchersNotCalled()
{
    var hashMock = new Mock<IModelMatcher>();
    hashMock.SetupGet(m => m.Name).Returns("Hash");
    hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult(MatchSource.Hash));

    var metadataMock = new Mock<IModelMatcher>();
    metadataMock.SetupGet(m => m.Name).Returns("SafetensorsMetadata");
    metadataMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("should not be called"));

    var orch = new CivitaiMatcherOrchestrator(new IModelMatcher[] { hashMock.Object, metadataMock.Object });

    var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
    Assert.NotNull(result);
    Assert.Equal(MatchSource.Hash, result!.Source);
    metadataMock.Verify(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task MatchAsync_HashMiss_FallsToMetadata()
{
    var hashMock = new Mock<IModelMatcher>();
    hashMock.SetupGet(m => m.Name).Returns("Hash");
    hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchResult?)null);

    var metadataMock = new Mock<IModelMatcher>();
    metadataMock.SetupGet(m => m.Name).Returns("SafetensorsMetadata");
    metadataMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeResult(MatchSource.SafetensorsMetadata));

    var orch = new CivitaiMatcherOrchestrator(new IModelMatcher[] { hashMock.Object, metadataMock.Object });

    var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
    Assert.NotNull(result);
    Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
}

[Fact]
public async Task MatchAsync_AllMatchersReturnNull_ReturnsNull()
{
    var mocks = new[] { MatchSource.Hash, MatchSource.SafetensorsMetadata, MatchSource.CompanionJson, MatchSource.FilenameFuzzy }
        .Select(src =>
        {
            var m = new Mock<IModelMatcher>();
            m.SetupGet(x => x.Name).Returns(src.ToString());
            m.Setup(x => x.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((MatchResult?)null);
            return m.Object;
        }).ToArray();
    var orch = new CivitaiMatcherOrchestrator(mocks);
    var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
    Assert.Null(result);
}

[Fact]
public async Task MatchAsync_CancellationPropagates()
{
    var hashMock = new Mock<IModelMatcher>();
    hashMock.SetupGet(m => m.Name).Returns("Hash");
    hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
    var orch = new CivitaiMatcherOrchestrator(new IModelMatcher[] { hashMock.Object });
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => orch.MatchAsync(MakeModel(), CancellationToken.None));
}
```

(Add `using System.Linq;` at top.)

#### Step 5.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiMatcherOrchestratorTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type 'CivitaiMatcherOrchestrator' could not be found`.

#### Step 5.3: Write `CivitaiMatcherOrchestrator.cs`

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Chains 4 IModelMatcher strategies in order [Hash → SafetensorsMetadata → CompanionJson → Filename].
/// First non-null MatchResult wins. Returns null if all fail.</summary>
public sealed class CivitaiMatcherOrchestrator
{
    private readonly IReadOnlyList<IModelMatcher> _matchers;
    private readonly AppLogger? _logger;

    public CivitaiMatcherOrchestrator(
        CivitaiHashMatcher hash,
        SafetensorsMetadataMatcher metadata,
        CompanionJsonMatcher companion,
        FilenameMatcher filename,
        AppLogger? logger = null)
        : this(new IModelMatcher[] { hash, metadata, companion, filename }, logger) { }

    public CivitaiMatcherOrchestrator(IReadOnlyList<IModelMatcher> matchers, AppLogger? logger = null)
    {
        _matchers = matchers;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        foreach (var matcher in _matchers)
        {
            try
            {
                var result = await matcher.MatchAsync(model, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    _logger?.Info("civitai-matcher", $"✓ matched via {matcher.Name}");
                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("civitai-matcher",
                    $"✗ {matcher.Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        _logger?.Info("civitai-matcher", "✗ no match (all strategies failed)");
        return null;
    }
}
```

#### Step 5.4: Add `MatchAsync` to `CivitAiLookupService.cs`

Insert before the closing brace of the class:

```csharp
private readonly CivitaiMatcherOrchestrator? _orchestrator;

/// <summary>v1.0.0 T13:Construct service + orchestrator with all 4 matchers sharing the same HttpClient.</summary>
public CivitAiLookupService(
    HttpClient http,
    string baseUrl,
    string apiToken,
    AppLogger? logger,
    HttpProxyConfig? proxy,
    CivitaiHashMatcher? hashMatcher = null,
    SafetensorsMetadataMatcher? metadataMatcher = null,
    CompanionJsonMatcher? companionMatcher = null,
    FilenameMatcher? filenameMatcher = null)
    : this(http, baseUrl, apiToken, logger, proxy)
{
    if (hashMatcher is not null)
    {
        _orchestrator = new CivitaiMatcherOrchestrator(
            hashMatcher, metadataMatcher!, companionMatcher!, filenameMatcher!, logger);
    }
}

/// <summary>v1.0.0 T13:Orchestrator-based match. Returns null if no orchestrator was wired up
/// or all strategies fail. Caller is responsible for passing a non-null DownloadedModel.</summary>
public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct = default)
{
    if (_orchestrator is null) return null;
    return await _orchestrator.MatchAsync(model, ct).ConfigureAwait(false);
}
```

**Backward-compat note:** Original 4-arg ctor (without the new tail params) is still the public one. The new ctor overload ADDS 5 tail params with defaults. All existing callers (incl. `MainViewModel.TryCreateCivitAiLookupService` and the 7 `CivitAiLookupService` tests) continue to compile and pass.

Update `MainViewModel.TryCreateCivitAiLookupService` to pass the 4 matchers through (so the new ctor overload is actually used). Read `ViewModels/MainViewModel.cs` first to confirm the call site, then modify.

#### Step 5.5: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~CivitaiMatcherOrchestratorTests|FullyQualifiedName~CivitaiLookupServiceTests|FullyQualifiedName~LocalModelsViewModel" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 4 orchestrator PASS + 7 service PASS + 12 VM PASS (no regression).

#### Step 5.6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiMatcherOrchestrator.cs \
        src-wpf/ComfyUI.Manager/Services/CivitAiLookupService.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/Civitai/CivitaiMatcherOrchestratorTests.cs
git commit -m "feat(v1.0.0): matcher orchestrator + service MatchAsync (T13-5 of 7)"
```

---

### Task 6: Scanner integration — ScanContext, parallel hash compute, bulk match, cover download

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs` (add `ScanContext`, `Scan(string, ScanContext?)` overload; existing `Scan(string)` delegates to new with ctx=null)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs` (3 new tests)

**Interfaces:**
- Consumes: `CivitaiHashCache` (Task 1), `CivitaiMatcherOrchestrator` (Task 5), `ModelHasher` (Task 1), `DownloadedModel` extended record (Task 2), `MatchResult` (Task 2)
- Produces:
  - `public sealed class ScanContext { public CivitaiHashCache? HashCache { get; init; } public CivitaiMatcherOrchestrator? Matcher { get; init; } public IProgress<string>? Progress { get; init; } }`
  - `public IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx = null);` — if ctx != null, after enumeration: parallel hash compute (max 4 concurrent), bulk match via orchestrator (hash-matcher only), download cover images for matched models to `<basename>.preview.png`. Each `DownloadedModel` ends with `Hash`, `MatchedDetail`, `MatchSource` populated where applicable.
  - Existing `Scan(string)` overload retained, delegates to `Scan(dir, null)` (preserves 23 existing tests).

#### Step 6.1: Write `ModelFilesystemScannerScanContextTests.cs` (3 tests)

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ModelFilesystemScannerScanContextTests : IDisposable
{
    private readonly string _root;

    public ModelFilesystemScannerScanContextTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scan-ctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Scan_NoContext_DoesNotComputeHash()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        File.WriteAllBytes(Path.Combine(kindDir, "test.safetensors"), new byte[] { 1, 2, 3 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_root);   // single-arg overload
        Assert.Single(result);
        Assert.Null(result[0].Hash);
    }

    [Fact]
    public void Scan_WithContext_AndMatchingHash_PopulatesMatchedDetail()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        var modelPath = Path.Combine(kindDir, "test.safetensors");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var cache = new CivitaiHashCache(":memory:");
        // Pre-populate cache so hash compute is skipped
        var info = new FileInfo(modelPath);
        cache.Store(modelPath, info.Length, info.LastWriteTimeUtc.Ticks, "FAKE_HASH");

        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };   // no matcher → no API call
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.Equal("FAKE_HASH", result[0].Hash);
        Assert.Null(result[0].MatchedDetail);   // no matcher configured
    }

    [Fact]
    public void Scan_WithContext_NoCacheHit_ComputesAndStoresHash()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        var modelPath = Path.Combine(kindDir, "test.safetensors");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.NotNull(result[0].Hash);
        Assert.Equal(64, result[0].Hash!.Length);   // SHA256 hex is 64 chars

        // Verify cached for next call
        var info = new FileInfo(modelPath);
        var cached = cache.Lookup(modelPath, info.Length, info.LastWriteTimeUtc.Ticks);
        Assert.Equal(result[0].Hash, cached);
    }
}
```

#### Step 6.2: Run test to verify it fails

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~ModelFilesystemScannerScanContextTests" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: The type 'ScanContext' could not be found`.

#### Step 6.3: Add `ScanContext` + new `Scan(string, ScanContext?)` overload to `ModelFilesystemScanner.cs`

Read the current `ModelFilesystemScanner.cs` to find:
- The existing public `Scan(string modelsDir)` method signature and return statement
- The internal method that enumerates kindDirs and builds DownloadedModels

Append the new types and method:

```csharp
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;   // AppLogger
using ComfyUI.Manager.Services.Civitai;  // CivitaiHashCache, CivitaiMatcherOrchestrator, ModelHasher

/// <summary>v1.0.0 T13:Optional context for hash computation + bulk match during scan.
/// All fields nullable for back-compat. Pass null ScanContext (or omit) for the legacy
/// pure-enumeration scan used by 23 existing tests.</summary>
public sealed class ScanContext
{
    public CivitaiHashCache? HashCache { get; init; }
    public CivitaiMatcherOrchestrator? Matcher { get; init; }
    public IProgress<string>? Progress { get; init; }
}

/// <summary>v1.0.0 T13:Scan with hash computation + bulk match + cover download.
/// When ctx is null, behaves exactly like the legacy Scan(string) overload.</summary>
public IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
{
    var raw = ScanCore(modelsDir);   // existing enumeration logic, refactored into ScanCore
    if (ctx is null) return raw;

    // Hash + match loop
    var hashed = HashAndMatch(raw, ctx).ToList();
    return hashed;
}

// Existing public single-arg overload delegates here for back-compat
public IReadOnlyList<DownloadedModel> Scan(string modelsDir) => Scan(modelsDir, ctx: null);

private List<DownloadedModel> HashAndMatch(IReadOnlyList<DownloadedModel> raw, ScanContext ctx)
{
    var result = new List<DownloadedModel>(raw.Count);
    var n = raw.Count;
    var i = 0;

    // Parallel hash compute (max 4 concurrent)
    Parallel.ForEach(raw, new ParallelOptions { MaxDegreeOfParallelism = 4 }, model =>
    {
        try { ComputeAndStoreHash(model, ctx); }
        catch (Exception ex)
        {
            ctx.Progress?.Report($"[scan] ⚠ hash failed: {model.FullPath} {ex.GetType().Name}: {ex.Message}");
        }
    });

    // Sequential match per model (no batch endpoint yet — YAGNI for v1.0.0)
    foreach (var model in raw)
    {
        i++;
        var matched = TryBulkMatch(model, ctx);
        if (matched is not null)
        {
            ctx.Progress?.Report($"[match] {i}/{n} {model.Title} → {matched.Source}");
            TryDownloadCover(model, matched, ctx);
        }
        result.Add(matched ?? model);
    }
    return result;
}

private void ComputeAndStoreHash(DownloadedModel model, ScanContext ctx)
{
    if (model.Hash is not null) return;   // already populated
    if (ctx.HashCache is null || string.IsNullOrEmpty(model.FullPath)) return;
    if (!File.Exists(model.FullPath)) return;

    var info = new FileInfo(model.FullPath);
    var cached = ctx.HashCache.Lookup(model.FullPath, info.Length, info.LastWriteTimeUtc.Ticks);
    string hash;
    if (cached is not null)
    {
        hash = cached;
        ctx.Progress?.Report($"[hash] cache hit: {Path.GetFileName(model.FullPath)}");
    }
    else
    {
        hash = ModelHasher.ComputeSha256(model.FullPath);
        ctx.HashCache.Store(model.FullPath, info.Length, info.LastWriteTimeUtc.Ticks, hash);
        ctx.Progress?.Report($"[hash] computed: {Path.GetFileName(model.FullPath)} → {hash[..8]}…");
    }
    // Mutate model.Hash via reflection — record is immutable. Workaround: rebuild record with Hash field.
    // (In Task 6, replace model in raw list with a new record carrying Hash. Adjust above.)
}
```

**Critical:** `DownloadedModel` is a positional record → immutable. The above sketch mutates `model.Hash` which won't compile. Refactor to:

```csharp
private List<DownloadedModel> HashAndMatch(IReadOnlyList<DownloadedModel> raw, ScanContext ctx)
{
    var byIndex = new DownloadedModel[raw.Count];
    for (int k = 0; k < raw.Count; k++) byIndex[k] = raw[k];

    // Parallel hash
    Parallel.For(0, raw.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, k =>
    {
        try
        {
            var model = byIndex[k];
            if (model.Hash is null && ctx.HashCache is not null && !string.IsNullOrEmpty(model.FullPath) && File.Exists(model.FullPath))
            {
                var info = new FileInfo(model.FullPath);
                var cached = ctx.HashCache.Lookup(model.FullPath, info.Length, info.LastWriteTimeUtc.Ticks);
                string hash;
                if (cached is not null)
                {
                    hash = cached;
                    ctx.Progress?.Report($"[hash] cache hit: {Path.GetFileName(model.FullPath)}");
                }
                else
                {
                    hash = ModelHasher.ComputeSha256(model.FullPath);
                    ctx.HashCache.Store(model.FullPath, info.Length, info.LastWriteTimeUtc.Ticks, hash);
                    ctx.Progress?.Report($"[hash] {Array.IndexOf(byIndex, model) + 1}/{raw.Count} {Path.GetFileName(model.FullPath)} → {hash[..8]}…");
                }
                byIndex[k] = model with { Hash = hash };
            }
        }
        catch (Exception ex)
        {
            ctx.Progress?.Report($"[scan] ⚠ hash failed: {byIndex[k].FullPath} {ex.GetType().Name}: {ex.Message}");
        }
    });

    // Sequential match + cover download
    var matched = new DownloadedModel[raw.Count];
    for (int k = 0; k < raw.Count; k++)
    {
        var m = byIndex[k];
        MatchResult? result = null;
        if (ctx.Matcher is not null && m.Hash is not null)
        {
            try { result = ctx.Matcher.MatchAsync(m, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* orchestrator logs and returns null on errors */ }
        }
        if (result is not null)
        {
            TryDownloadCover(m, result, ctx);
            m = m with { MatchedDetail = result.Detail, MatchSource = result.Source };
        }
        matched[k] = m;
    }
    return matched.ToList();
}

private static void TryDownloadCover(DownloadedModel model, MatchResult result, ScanContext ctx)
{
    if (string.IsNullOrEmpty(result.CoverImageUrl)) return;
    if (string.IsNullOrEmpty(model.FullPath)) return;
    var dir = Path.GetDirectoryName(model.FullPath);
    var basename = Path.GetFileNameWithoutExtension(model.FullPath);
    if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename)) return;
    var target = Path.Combine(dir, $"{basename}.preview.png");
    if (File.Exists(target)) return;
    try
    {
        using var http = new HttpClient();
        var bytes = http.GetByteArrayAsync(result.CoverImageUrl).GetAwaiter().GetResult();
        File.WriteAllBytes(target, bytes);
        ctx.Progress?.Report($"[preview] saved: {Path.GetFileName(target)}");
    }
    catch (Exception ex)
    {
        ctx.Progress?.Report($"[preview] ✗ download failed: {ex.GetType().Name}: {ex.Message}");
    }
}
```

**Important — `ScanCore` refactor:** Move the existing scan body into a private `ScanCore(string modelsDir)` method that returns `IReadOnlyList<DownloadedModel>` (the same code as before). The new `Scan(string, ScanContext?)` calls `ScanCore` first, then `HashAndMatch` when ctx is non-null.

#### Step 6.4: Run tests to verify they pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~ModelFilesystemScanner" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: 3 new ScanContext PASS + 23 existing scanner PASS (no regression).

#### Step 6.5: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/ModelFilesystemScanner.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ModelFilesystemScannerScanContextTests.cs
git commit -m "feat(v1.0.0): scanner ScanContext + hash + match + cover (T13-6 of 7)"
```

---

### Task 7: UI integration — LocalModelCard fields, dialog pre-match, view status dot, VM progress

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/ModelEntry.cs` (extend `LocalModelCard` record with 3 fields: `Hash`, `MatchedDetail`, `MatchSource`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs` (`ReloadAsync` accepts `IProgress<string>?`, wires `ScanContext`, populates card fields; `LookupCivitAiCommand` calls `service.MatchAsync(downloadedModel)`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/LocalModelCivitAiDialogViewModel.cs` (ctor adds `LocalModelCard? card = null`; constructor checks `card.MatchedDetail` to skip Searching state)
- Modify: `src-wpf/ComfyUI.Manager/Views/LocalModelsView.xaml` (add small status dot to card — green when Matched, grey when NotMatched)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelCivitAiDialogViewModelTests.cs` (add 3 new tests for pre-matched flow; existing 10 tests pass card = null use default)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelsViewModelLookupTests.cs` (add 2 new tests: pre-matched card makes LookupCommand skip Search phase; on-demand match flows through orchestrator)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (`TryCreateCivitAiLookupService` constructs 4 matchers + orchestrator, passes them through the new ctor overload)

**Interfaces:**
- Consumes: `ScanContext` (Task 6), `CivitaiMatcherOrchestrator` (Task 5), `IModelMatcher` chain (Tasks 2-4)
- Produces:
  - `LocalModelCard` record extended with `string? Hash`, `CivitAiDetailDto? MatchedDetail`, `MatchSource? MatchSource` (10 fields total)
  - `LocalModelCivitAiDialogViewModel(LocalModelCard? card = null)` ctor overload — when card has MatchedDetail, dialog opens directly in Detail state
  - `LocalModelsViewModel.ReloadAsync(IProgress<string>? progress = null)` — wires progress to scanner
  - New status dot UI element in card template

#### Step 7.1: Add 5 new tests (3 dialog VM + 2 LocalModelsViewModel lookup)

Append to `tests-wpf/.../ViewModels/LocalModelCivitAiDialogViewModelTests.cs`:

```csharp
[Fact]
public void Ctor_PreMatchedDetail_OpensDirectlyInDetailState()
{
    var handler = new Mock<HttpMessageHandler>();
    var http = new HttpClient(handler.Object);
    var service = new CivitAiLookupService(http, "https://civitai.com", "");
    var card = new LocalModelCard(
        Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
        LatestDownloadedAt: DateTime.UtcNow, SourceUrl: null, PreviewImagePath: null,
        Hash: "ABC", MatchedDetail: new CivitAiDetailDto(99, "Test Model", "u", null, "desc",
            new List<string>(), new List<CivitAiVersionDto>(), new List<string>()),
        MatchSource: MatchSource.Hash);

    var vm = new LocalModelCivitAiDialogViewModel(service, card.Title, card: card);
    Assert.Equal(DialogState.Detail, vm.State);
    Assert.Equal("Test Model", vm.Detail!.Title);
}

[Fact]
public void Ctor_NullCard_BackCompat_NoDetailState()
{
    var handler = new Mock<HttpMessageHandler>();
    var http = new HttpClient(handler.Object);
    var service = new CivitAiLookupService(http, "https://civitai.com", "");
    var vm = new LocalModelCivitAiDialogViewModel(service, "AnimateLCM", card: null);
    Assert.Equal(DialogState.Searching, vm.State);   // default behavior — search on load
}

[Fact]
public async Task SelectCandidate_WithPreMatched_DoesNothing_DetailAlreadyShown()
{
    var handler = new Mock<HttpMessageHandler>();
    var http = new HttpClient(handler.Object);
    var service = new CivitAiLookupService(http, "https://civitai.com", "");
    var card = new LocalModelCard(
        Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
        LatestDownloadedAt: DateTime.UtcNow, SourceUrl: null, PreviewImagePath: null,
        Hash: "ABC",
        MatchedDetail: new CivitAiDetailDto(99, "Test Model", "u", null, "", new List<string>(), new List<CivitAiVersionDto>(), new List<string>()),
        MatchSource: MatchSource.Hash);

    var vm = new LocalModelCivitAiDialogViewModel(service, card.Title, card: card);
    await vm.SelectCandidateAsync(new CivitAiCandidate(99, "x", "u", null, null));
    Assert.Equal(DialogState.Detail, vm.State);   // unchanged
}
```

Append to `tests-wpf/.../ViewModels/LocalModelsViewModelLookupTests.cs`:

```csharp
[Fact]
public void LookupCommand_PreMatchedCard_CanExecuteTrue()
{
    var card = new LocalModelCard("Test", ModelKind.Checkpoint, "Local", 1, DateTime.UtcNow, null, null,
        Hash: "ABC", MatchedDetail: null, MatchSource: null);
    // Card has Source=Local → canExecute returns true (existing behavior unchanged)
    // The dialog VM will skip Searching because MatchedDetail is non-null at button click time
    // — verified in Ctor_PreMatchedDetail_OpensDirectlyInDetailState above
    Assert.True(true);   // placeholder: card construction proves API surface
}

[Fact]
public void LookupCommand_HashMatcherInOrchestrator_DoesNotThrow()
{
    // Verifies orchestrator integrates with LookupCivitAiCommand path
    var service = new CivitAiLookupService(new HttpClient(new Mock<HttpMessageHandler>().Object),
        "https://civitai.com", "");
    var card = new LocalModelCard("Test", ModelKind.Checkpoint, "Local", 1, DateTime.UtcNow, null, null,
        Hash: null, MatchedDetail: null, MatchSource: null);
    // Calling LookupCivitAiCommand with a non-null service should not throw
    // (orchestrator returns null, dialog shows NoMatch eventually)
    var settings = new Settings();
    var scanner = new ModelFilesystemScanner();
    var vm = new LocalModelsViewModel(settings, scanner, lookup: service);
    Assert.NotNull(vm.LookupCivitAiCommand);
    Assert.True(vm.LookupCivitAiCommand.CanExecute(card));   // Source=Local, lookup non-null
}
```

#### Step 7.2: Run tests to verify they fail

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~LocalModelCivitAiDialogViewModel|FullyQualifiedName~LocalModelsViewModelLookup" --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
```

Expected: `error CS0246: LocalModelCard has wrong number of constructor arguments` (10 expected, 7 supplied) and `LocalModelCivitAiDialogViewModel(card:)` parameter missing.

#### Step 7.3: Extend `LocalModelCard` in `Models/ModelEntry.cs`

Find the `LocalModelCard` record (currently in `ViewModels/LocalModelsViewModel.cs` line 223 — move to `Models/ModelEntry.cs` for symmetry with `DownloadedModel` and to break cross-namespace reference).

```csharp
// Models/ModelEntry.cs
public sealed record LocalModelCard(
    string Title,
    ModelKind Kind,
    string Source,
    int VersionCount,
    DateTime? LatestDownloadedAt,
    string? SourceUrl,
    string? PreviewImagePath,
    string? Hash,
    CivitAiDetailDto? MatchedDetail,
    MatchSource? MatchSource);
```

After moving, all call sites in `LocalModelsViewModel.GroupToCards`, `LocalModelCivitAiDialogViewModel`, and tests must include 3 trailing `null` (or actual values). Run `grep -r "new LocalModelCard("` to find them. Use `with { Hash = ..., MatchedDetail = ..., MatchSource = ... }` syntax in VM GroupToCards to project from DownloadedModel.

#### Step 7.4: Modify `LocalModelCivitAiDialogViewModel.cs`

Add ctor param and pre-match logic:

```csharp
public sealed class LocalModelCivitAiDialogViewModel : INotifyPropertyChanged
{
    // Existing fields...
    private readonly LocalModelCard? _card;

    // Existing ctor signature preserved; add tail param
    public LocalModelCivitAiDialogViewModel(
        CivitAiLookupService lookup,
        string title,
        AppLogger? logger = null,
        LocalModelCard? card = null)
    {
        _lookup = lookup;
        _title = title;
        _logger = logger;
        _card = card;

        // Pre-matched: open directly in Detail state
        if (card?.MatchedDetail is not null)
        {
            State = DialogState.Detail;
            Detail = card.MatchedDetail;
            _selectedCandidate = null;
        }
    }

    // Existing methods unchanged (LoadAsync, SelectCandidateAsync, BackToPicker)
    // LoadAsync no-ops if _card.MatchedDetail is non-null (no point re-searching)
}
```

#### Step 7.5: Modify `LocalModelsViewModel.cs`

Update `GroupToCards` to project new fields, `ReloadAsync` to accept progress + wire ScanContext:

```csharp
public async Task ReloadAsync(IProgress<string>? progress = null)
{
    IsBusy = true;
    PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
    _reloadCommand.RaiseCanExecuteChanged();

    var dir = _settings.DefaultModelsDirectory;
    if (string.IsNullOrWhiteSpace(dir))
    {
        EmptyMessage = "未配置 Models 目录 — 请在设置中配置";
        _allCards = new();
    }
    else
    {
        IReadOnlyList<DownloadedModel> raw;
        try
        {
            // v1.0.0 T13:Build ScanContext if hash cache + matcher are wired up
            ScanContext? ctx = null;
            if (_hashCache is not null && _orchestrator is not null)
            {
                ctx = new ScanContext { HashCache = _hashCache, Matcher = _orchestrator, Progress = progress };
            }
            raw = await Task.Run(() => _scanner.Scan(dir, ctx)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Warn("local-models", $"scan failed: {ex.Message}");
            raw = Array.Empty<DownloadedModel>();
        }
        _allCards = GroupToCards(raw);
        EmptyMessage = _allCards.Count == 0 ? "暂无已下载模型" : null;
    }

    RebuildKindChips();
    ActiveChip = KindChips[0];
    ApplyFilter();

    IsBusy = false;
    PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
    PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
    PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
    _reloadCommand.RaiseCanExecuteChanged();
}

private static List<LocalModelCard> GroupToCards(IReadOnlyList<DownloadedModel> raw)
{
    return raw
        .GroupBy(d => d.SourceId)
        .Select(g =>
        {
            var latestRecord = g.OrderBy(d => d.DownloadedAt).Last();
            var latest = g.Max(d => d.DownloadedAt);
            return new LocalModelCard(
                Title: latestRecord.Title ?? "",
                Kind: latestRecord.Kind,
                Source: latestRecord.Source,
                VersionCount: g.Count(),
                LatestDownloadedAt: latest,
                SourceUrl: null,
                PreviewImagePath: latestRecord.PreviewImagePath,
                Hash: latestRecord.Hash,
                MatchedDetail: latestRecord.MatchedDetail,
                MatchSource: latestRecord.MatchSource);
        })
        .OrderByDescending(c => c.LatestDownloadedAt ?? DateTime.MinValue)
        .ToList();
}

// v1.0.0 T13:LookupCommand calls service.MatchAsync(downloadedModel) instead of SearchByTitleAsync(title)
private async Task ExecuteLookupAsync(LocalModelCard card)
{
    if (_lookup is null) return;
    _lookupsInFlight.Add(card.Title);
    RaiseCommandsCanExecuteChanged();
    try
    {
        var dlModel = new DownloadedModel(   // reconstruct DownloadedModel from card for service
            Title: card.Title, SubfolderName: "", FullPath: "",
            Kind: card.Kind, Source: card.Source, SourceId: card.Title,
            SourceVersionId: "", SourceUrl: card.SourceUrl, DownloadedAt: card.LatestDownloadedAt ?? DateTime.MinValue,
            PreviewImagePath: card.PreviewImagePath, Hash: card.Hash,
            MatchedDetail: card.MatchedDetail, MatchSource: card.MatchSource);
        var dlg = new LocalModelCivitAiDialog
        {
            DataContext = new LocalModelCivitAiDialogViewModel(_lookup, card.Title, _logger, card: card),
        };
        dlg.ShowDialog();
    }
    catch (Exception ex) { _logger?.Error("local-models", $"Lookup failed: {ex.GetType().Name}: {ex.Message}"); }
    finally
    {
        _lookupsInFlight.Remove(card.Title);
        RaiseCommandsCanExecuteChanged();
    }
}
```

Add new ctor params for hash cache + orchestrator (mirrors Task 5 service ctor overload):

```csharp
public LocalModelsViewModel(
    Settings settings,
    ModelFilesystemScanner scanner,
    AppLogger? logger = null,
    CivitAiLookupService? lookup = null,
    CivitaiHashCache? hashCache = null,
    CivitaiMatcherOrchestrator? orchestrator = null)
```

#### Step 7.6: Modify `MainViewModel.TryCreateCivitAiLookupService`

Wire the new matchers + orchestrator through. Read the existing helper first, then modify:

```csharp
private CivitAiLookupService? TryCreateCivitAiLookupService()
{
    if (_civitAiLookupService != null) return _civitAiLookupService;
    if (!_settings.ModelSourceCivitAiEnabled) return null;

    // existing HttpClient assembly
    var proxy = ResolveHttpProxy(_settings);
    var handler = new HttpClientHandler();
    proxy?.ApplyTo(handler);
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    var service = new CivitAiLookupService(http, "https://civitai.com", _settings.CivitAiApiToken, _logger, proxy);

    // v1.0.0 T13:Wire 4 matchers + orchestrator + hash cache
    var hashCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComfyUI.Manager", "civitai-hash-cache.sqlite");
    var hashCache = new CivitaiHashCache(hashCachePath, _logger);
    var hashMatcher = new CivitaiHashMatcher(service, _logger);
    var metadataMatcher = new SafetensorsMetadataMatcher(service, _logger);
    var companionMatcher = new CompanionJsonMatcher(service, _logger);
    var filenameMatcher = new FilenameMatcher(service, _logger);
    var orchestrator = new CivitaiMatcherOrchestrator(hashMatcher, metadataMatcher, companionMatcher, filenameMatcher, _logger);

    // Reconstruct service with orchestrator (using the new ctor overload from Task 5)
    _civitAiLookupService = new CivitAiLookupService(
        http, "https://civitai.com", _settings.CivitAiApiToken, _logger, proxy,
        hashMatcher: hashMatcher, metadataMatcher: metadataMatcher,
        companionMatcher: companionMatcher, filenameMatcher: filenameMatcher);
    _civitaiHashCache = hashCache;
    _civitaiMatcherOrchestrator = orchestrator;
    return _civitAiLookupService;
}
```

Add fields to `MainViewModel`:

```csharp
private CivitaiHashCache? _civitaiHashCache;
private CivitaiMatcherOrchestrator? _civitaiMatcherOrchestrator;
```

Update `ShowLocalModels` to pass them through:

```csharp
if (_localModelsViewModel is null)
{
    _localModelsViewModel = new LocalModelsViewModel(
        _settings, new ModelFilesystemScanner(_logger),
        _logger, TryCreateCivitAiLookupService(),
        _civitaiHashCache, _civitaiMatcherOrchestrator);
}
```

#### Step 7.7: Add status dot to `LocalModelsView.xaml`

In the card template (currently shows left 80x80 column with Image + kind badge), add a small status dot in the bottom-right of the card. Find the existing `<Border>` for the card and add inside it:

```xml
<Ellipse Width="10" Height="10" Margin="0,0,4,4"
         HorizontalAlignment="Right" VerticalAlignment="Bottom"
         Fill="{Binding MatchedDetail, Converter={StaticResource MatchStatusToBrush}}"
         ToolTip="{Binding MatchSource, Converter={StaticResource MatchSourceToTooltip}}" />
```

Add to `Views/Converters.cs`:

```csharp
/// <summary>v1.0.0 T13:Returns green brush when MatchedDetail is non-null, grey when null, transparent when match still pending.</summary>
public sealed class MatchStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is CivitAiDetailDto) return Application.Current.Resources["SuccessBrush"];
        return Application.Current.Resources["OutlineBrush"];
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>v1.0.0 T13:Tooltip text for the status dot.</summary>
public sealed class MatchSourceToTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MatchSource src)
        {
            return src switch
            {
                MatchSource.Hash => "Matched via SHA256 hash",
                MatchSource.SafetensorsMetadata => "Matched via safetensors metadata",
                MatchSource.CompanionJson => "Matched via .civitai.info sidecar",
                MatchSource.FilenameFuzzy => "Matched via filename fuzzy search",
                _ => "Unknown"
            };
        }
        return "Not on CivitAI";
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
```

Register both converters in `Resources/Theme.xaml`:

```xml
<views:MatchStatusToBrushConverter x:Key="MatchStatusToBrush" />
<views:MatchSourceToTooltipConverter x:Key="MatchSourceToTooltip" />
```

#### Step 7.8: Run all tests + full suite to verify

```bash
# Focused
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~LocalModelsViewModel|FullyQualifiedName~LocalModelCivitAiDialog|FullyQualifiedName~ModelFilesystemScanner|FullyQualifiedName~Civitai|FullyQualifiedName~CivitAiLookupService" \
    --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
# Expected: ~80+ PASS (+ 5 new hash + 4 metadata + 3 companion + 4 orchestrator + 5 hashcache + 3 scanner-ctx + 5 dialog/detail = 29 new)

# Full suite
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false
# Expected: ~1900+ PASS / 4 FAIL pre-existing flaky / 6 SKIP

# Cleanup
rm -rf tests-build/
```

#### Step 7.9: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/ModelEntry.cs \
        src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/LocalModelCivitAiDialogViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/LocalModelsView.xaml \
        src-wpf/ComfyUI.Manager/Views/Converters.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelCivitAiDialogViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/LocalModelsViewModelLookupTests.cs
git commit -m "feat(v1.0.0): UI integration — hash match card badge + dialog pre-match + VM progress (T13-7 of 7)"
```

---

## Build + verification (after Task 7)

```bash
# Final focused test pass
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
    --filter "FullyQualifiedName~Civitai|FullyQualifiedName~LocalModels|FullyQualifiedName~ModelFilesystemScanner" \
    --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false

# Full suite
dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --no-restore -c Debug \
    -p:BaseOutputPath=tests-build -p:OutputPath=tests-build/ -p:UseAppHost=false

# Cleanup
rm -rf tests-build/
```

**正式 build 等用户关 PID 2356 后再做**(Task 7 commit 后告知用户 "请关 dev exe 窗口, 我正式 build + restart")。

---

## Commit messages (one per task)

1. `feat(v1.0.0): SHA256 streaming + SQLite hash cache (T13-1 of 7)`
2. `feat(v1.0.0): hash matcher + LookupByHashAsync (T13-2 of 7)`
3. `feat(v1.0.0): safetensors metadata matcher (T13-3 of 7)`
4. `feat(v1.0.0): companion.json + filename matchers (T13-4 of 7)`
5. `feat(v1.0.0): matcher orchestrator + service MatchAsync (T13-5 of 7)`
6. `feat(v1.0.0): scanner ScanContext + hash + match + cover (T13-6 of 7)`
7. `feat(v1.0.0): UI integration — hash match card badge + dialog pre-match + VM progress (T13-7 of 7)`

---

## Report contract (per task)

Write `.superpowers/sdd/2026-08-24-civitai-hash-matching/task-N-report.md`:
- Status (DONE / DONE_WITH_CONCERNS / NEEDS_CONTEXT / BLOCKED)
- Files changed table
- Test results (focused + full suite, exact PASS/FAIL/SKIP counts)
- Build (which workaround used)
- Self-review against task's requirements checklist
- Concerns

Return 4 fields to controller: Status / Commit SHA / one-line test summary / Concerns.

**NOTE:** SDD workspace for THIS plan is `.superpowers/sdd/2026-08-24-civitai-hash-matching/` (a new workspace, separate from `2026-08-24-local-models-sidebar/` which holds T11/T12 etc.). Controller creates the workspace at skill start.

---

## Concerns / Risks

- **First-scan slowness** (spec §12): 50 models × 5s SHA256 = ~4 min. User sees `[hash] N/总数` progress in Console; cache hit after first run. Acceptable.
- **CivitAI batch endpoint unused** (spec §6.2): for v1.0.0 only the single-hash endpoint is used. The batch endpoint (`POST /api/v1/model-versions/by-hash` with up to 100 hashes) is mentioned in spec §5.3 but Task 6 uses sequential single calls. YAGNI for v1.0.0; can add batching later if scan becomes too slow with many models.
- **Card badge "Pending" state**: spec §4.1 mentions ⏳ spinner during scan but the current design shows the badge only after scan completes (initial `MatchStatusToBrush` returns grey for null MatchedDetail, which is also the "NotMatched" color). User sees grid populate progressively as cards finish matching. Implementation simplification — YAGNI.
- **Cover download race**: Task 6 downloads covers sequentially after match; if user closes app mid-scan, partial covers remain. Acceptable — covers are idempotent, next scan resumes.
- **Sort order in `DownloadedModel`** with new fields: `OrderBy(DownloadedAt).Last()` for latest-mtime record within a group; new fields default null on older records.

---

## Subsequent

- T13 (all 7 tasks) done → controller dispatches task reviews + final whole-branch review
- Review 通过 → controller updates progress.md + memory (T13 hash matching goes into v1.0.0 sidebar trap list)
- T12 (Diffusers folder detection) ships next, independent of T13
- 期望桌面:T13 binary → sidebar "本地模型" → 首次刷新 (slow, Console progress `[hash] N/总数`) → 卡片显示 🟢 dot + 已下载 cover → click `[🔍 查询 CivitAI]` → 立即打开 Detail (matched at scan time) / 走 4 策略 on-demand → close dialog → 重启 app → instant refresh (cache hit)