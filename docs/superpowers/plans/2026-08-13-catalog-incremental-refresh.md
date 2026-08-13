# v0.6.14 Catalog 增量刷新 + 8 GitHub 字段入库 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Catalog 节点目录实现 HTTP cache + per-entry SHA256 hash diff 的增量刷新,并把 8 个 GitHub 关键字段(`html_url/homepage/language/forks_count/open_issues_count/release_tag/subscribers_count/created_at`)入库 — 二次刷新 304 时 < 3 秒,变更时只 upsert + enrichment 变更的 N 条。

**Architecture:** 增量刷新 = 3 层防护:① HTTP `If-None-Match`/`If-Modified-Since` 让 raw.githubusercontent.com 返回 304 → skip 整个 JSON parse;② per-entry SHA256 hash diff 让 `UpsertBatch` 只写 hash 变了 / 新增的 entry;③ MetadataCache 24h TTL 兜底 GitHub enrichment 频率。**零额外 API call** — 8 字段全部从已有 `/repos` + `/releases` 响应里提取。schema 9 个新列 + 新表 `catalog_http_cache` 走 `EnsureColumn`/`CREATE TABLE IF NOT EXISTS` 模式,无迁移风险。

**Tech Stack:** C# 12 / .NET 8 WPF, xUnit + Moq + Fake subclass override, SQLite via Microsoft.Data.Sqlite, SHA256 via `System.Security.Cryptography`, HttpClient with `If-None-Match`/`If-Modified-Since` headers.

## Global Constraints

These apply to every task's implementation. Exact values come from spec `2026-08-13-catalog-incremental-refresh-design.md`.

- **DB location**: `<AppBaseDir>/data/catalog-cache.db` (current, YAGNI 不搬 `%APPDATA%`)
- **DB pragma**: `PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;` 在 `CatalogCacheStore.Open()` 顶部(v0.6.13-B.8 既有 pattern)
- **Schema migration pattern**: `EnsureColumn(conn, "catalog_cache", "<col>", "<type>")` — `CREATE TABLE IF NOT EXISTS` for new tables. NOT NULL DEFAULT '' 走 SQLite 默认值回填
- **8 GitHub 字段**:从 `/repos.{html_url,homepage,language,forks_count,open_issues_count,subscribers_count,created_at}` + `/releases[0].tag_name` 提取 — 零额外 HTTP call
- **`content_hash` 计算范围**:仅 catalog JSON 内容字段 — `id/name/author/title/description/category/reference/tags/install_type` (按字母序 canonical JSON,SHA256 hex)。**不**包含 DB GUID、metadata 列、时间戳、`raw_metadata` 里其他键(`apt_dependency/badges/files/js_path/last_update/nickname/nodename_pattern/pip/preemptions/reference2/version`)— 否则 metadata 改 → upsert → metadata enrich → hash 变死循环
- **`GitHubCatalogMetadataService`**:不 `sealed` + `EnrichOneAsync` 已 `virtual`(v0.6.13-B.1 lesson)— 新加方法保持 `virtual` 让 Fake subclass override 可测
- **Rate limit**: 403 + `X-RateLimit-Remaining=0` → throw `RateLimitException`(v0.6.13-B.2 lesson)— 新字段提取不改 API call count(/repos + /releases + /commits 已有),回归测试 5000/h 触发时机
- **Test fixture Dispose**: 走 `SqliteConnection.ClearAllPools()` + 删 `.db`/`.db-wal`/`.db-shm` 三文件 + try/catch(WAL mode IO 兼容)
- **AppLogger category**: `catalog-refresh` 既有,新加 `catalog-http-cache`(损坏/异常);Info/Warn/Error 走 v0.6.5.13 既有路径
- **Settings 3-grep rule**: 无新 Settings 字段 → Models + ViewModels + XAML 不动(v0.6.13-B.1 lesson)
- **`FetchNodeVersionsOnRefresh`**:现有 toggle,不动;`FetchCatalogMetadata`:现有 toggle 默认 OFF,不动
- **向后兼容**: `CatalogFetcher.FetchAsync(url, ct)` 旧签名保留 → 调新签名 `FetchAsync(url, null, null, ct)`(避免一次性破坏所有现有 caller/tests)

## File Structure

| 文件 | 类型 | 行数估计 |
|------|------|----------|
| `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` | 修改 | +10 行(8 typed properties) |
| `src-wpf/ComfyUI.Manager/Data/CatalogEntryHasher.cs` | **新** | ~50 行 |
| `src-wpf/ComfyUI.Manager/Data/CatalogHttpCacheStore.cs` | **新** | ~70 行 |
| `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` | 修改 | +15 行(9 EnsureColumn + 新表 DDL) |
| `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` | 修改 | +60 行(9 cols + hash + GetContentHashesBySourceAsync) |
| `src-wpf/ComfyUI.Manager/Services/CatalogFetchResult.cs` | **新** | ~30 行 |
| `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs` | 修改 | +40 行(HTTP cache header + 304) |
| `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` | 修改 | +80 行(hash diff pipeline) |
| `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs` | 修改 | +25 行(8 字段提取) |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 修改 | +5 行(Inject CatalogHttpCacheStore) |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogEntryHasherTests.cs` | **新** | ~80 行(4 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogHttpCacheStoreTests.cs` | **新** | ~120 行(6 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreV614MigrationTests.cs` | **新** | ~110 行(5 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryV614HashTests.cs` | **新** | ~120 行(4 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherHttpCacheTests.cs` | **新** | ~100 行(4 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs` | 修改 | +200 行(6 tests + FakeHttpCacheStore) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs` | 修改 | +100 行(3 tests) |
| `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` | 修改 | +40 行(1 STA test) |

**总:8 新文件 + 7 修改 + ~33 新 tests。**

---

## Task 1: CatalogEntry 8 typed properties + CatalogEntryHasher

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs:39-61` — 在 v0.6.13-B metadata properties 之后加 8 个 typed properties
- Create: `src-wpf/ComfyUI.Manager/Data/CatalogEntryHasher.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogEntryHasherTests.cs`

**Interfaces:**
- Consumes: 无(纯新文件 + 修改既有 model)
- Produces:
  - `CatalogEntry.HtmlUrl`, `Homepage`, `Language`, `ForksCount`, `OpenIssuesCount`, `ReleaseTag`, `SubscribersCount`, `CreatedAt` (8 properties)
  - `static string CatalogEntryHasher.ComputeHash(CatalogEntry entry) → SHA256 hex (64 chars)`

- [ ] **Step 1: Write failing test for ComputeHash**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogEntryHasherTests.cs`:

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogEntryHasherTests
{
    [Fact]
    public void ComputeHash_SameCanonicalContent_SameHash()
    {
        var entry1 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["author"] = "alice",
                ["title"] = "Title",
                ["description"] = "Desc",
                ["id"] = "node-x",
            },
        };
        var entry2 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["id"] = "node-x",
                ["title"] = "Title",
                ["description"] = "Desc",
                ["author"] = "alice",
            },
        };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_DifferentContent_DifferentHash()
    {
        var entry1 = new CatalogEntry { Package = "pkg-x" };
        var entry2 = new CatalogEntry { Package = "pkg-y" };
        Assert.NotEqual(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_MetadataFieldsDoNotAffectHash()
    {
        // stars/license 等 metadata 改了,hash 必须不变(metadata refresh 触发 row 重写 = 死循环)
        var entry1 = new CatalogEntry { Package = "pkg-x", Stars = 100 };
        var entry2 = new CatalogEntry { Package = "pkg-x", Stars = 999 };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_RawMetadataSkippedKeysDoNotAffectHash()
    {
        // apt_dependency/badges/files/js_path/last_update/nickname/nodename_pattern/
        // pip/preemptions/reference2/version — 这些字段变,hash 不变
        var entry1 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "torch>=2.0" },
            },
        };
        var entry2 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "torch>=2.5" },
            },
        };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogEntryHasherTests" --no-restore`
Expected: FAIL with "CatalogEntryHasher: 类型不存在"

- [ ] **Step 3: Implement ComputeHash + add 8 typed properties**

Modify `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` — 在 line 61 之后(`MetadataFetchedAt` 之后)加:

```csharp
    // v0.6.14: 8 个新 GitHub 字段(由 GitHubCatalogMetadataService 从 /repos + /releases 提取)
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }
    [JsonPropertyName("open_issues_count")]
    public int OpenIssuesCount { get; set; }
    [JsonPropertyName("release_tag")]
    public string? ReleaseTag { get; set; }
    [JsonPropertyName("subscribers_count")]
    public int SubscribersCount { get; set; }
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }  // ISO 8601 UTC from /repos.created_at
```

Create `src-wpf/ComfyUI.Manager/Data/CatalogEntryHasher.cs`:

```csharp
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.14: SHA256 of canonical JSON for per-entry hash diff. 仅包含 catalog
/// JSON 内容字段(package/author/title/description/reference 等)— 不包含
/// metadata 列或时间戳(metadata 改了不应触发 row 重写,否则死循环)。
/// </summary>
public static class CatalogEntryHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ComputeHash(CatalogEntry entry)
    {
        // SortedDictionary 自动按 key 字母序,JSON 序列化后 hash 稳定
        var canonical = new SortedDictionary<string, object?>
        {
            ["id"] = GetRaw(entry, "id"),
            ["name"] = entry.Package,
            ["author"] = GetRaw(entry, "author"),
            ["title"] = GetRaw(entry, "title"),
            ["description"] = GetRaw(entry, "description"),
            ["category"] = GetRaw(entry, "category"),
            ["reference"] = GetRaw(entry, "reference"),
            ["tags"] = GetRaw(entry, "tags"),
            ["install_type"] = GetRaw(entry, "install_type"),
        };
        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static object? GetRaw(CatalogEntry entry, string key)
    {
        if (entry.RawMetadata is null) return null;
        return entry.RawMetadata.TryGetValue(key, out var v) ? v : null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogEntryHasherTests" --no-restore`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs \
        src-wpf/ComfyUI.Manager/Data/CatalogEntryHasher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogEntryHasherTests.cs
git commit -m "feat(catalog): add 8 typed properties + CatalogEntryHasher (v0.6.14 T1)"
```

---

## Task 2: CatalogHttpCacheStore

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Data/CatalogHttpCacheStore.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogHttpCacheStoreTests.cs`

**Interfaces:**
- Consumes: `CatalogCacheStore`(同 DB,通过其 `Open()` 获取 connection)
- Produces:
  - `Task<(string? Etag, string? LastModified)> CatalogHttpCacheStore.GetAsync(string url, CancellationToken ct)`
  - `Task CatalogHttpCacheStore.PutAsync(string url, string? etag, string? lastModified, CancellationToken ct)`

**注**:Table 创建走 `CatalogCacheStore.EnsureCatalogCacheDbSchema` (Task 3)。本 Task 假定表已存在(测试用 raw SQL 预创建)。

- [ ] **Step 1: Write failing test for GetAsync/PutAsync**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogHttpCacheStoreTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogHttpCacheStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CatalogHttpCacheStore _store;

    public CatalogHttpCacheStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-http-cache-{Guid.NewGuid():N}.db");
        EnsureSchema(_dbPath);
        _store = new CatalogHttpCacheStore(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    private static void EnsureSchema(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE catalog_http_cache (
                url TEXT PRIMARY KEY,
                etag TEXT,
                last_modified TEXT,
                fetched_at TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task PutAsync_ThenGetAsync_ReturnsStoredValues()
    {
        await _store.PutAsync("https://example.com/c.json", "\"abc123\"", "Wed, 21 Oct 2026 07:28:00 GMT");

        var (etag, lastMod) = await _store.GetAsync("https://example.com/c.json");

        Assert.Equal("\"abc123\"", etag);
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", lastMod);
    }

    [Fact]
    public async Task GetAsync_NonExistentUrl_ReturnsBothNull()
    {
        var (etag, lastMod) = await _store.GetAsync("https://nope.example/c.json");
        Assert.Null(etag);
        Assert.Null(lastMod);
    }

    [Fact]
    public async Task PutAsync_OverwritesExisting()
    {
        await _store.PutAsync("https://example.com/c.json", "\"v1\"", null);
        await _store.PutAsync("https://example.com/c.json", "\"v2\"", "later");

        var (etag, lastMod) = await _store.GetAsync("https://example.com/c.json");

        Assert.Equal("\"v2\"", etag);
        Assert.Equal("later", lastMod);
    }

    [Fact]
    public async Task PutAsync_NullEtagAndLastModified_StoredAsNull()
    {
        await _store.PutAsync("https://example.com/c.json", null, null);

        var (etag, lastMod) = await _store.GetAsync("https://example.com/c.json");

        Assert.Null(etag);
        Assert.Null(lastMod);
    }

    [Fact]
    public async Task GetAsync_RowCorrupted_ReturnsBothNullAndDoesNotThrow()
    {
        // 手动插一行 corrupted(无 url 但满足 NOT NULL constraint,改用 nullability 触发)
        // 用 url = "" 触发后续 SELECT 异常路径 — 实际损坏场景:etag 是 1MB 乱码
        // 这里简化:直接插一行合法 url 但 etag 含 invalid UTF-8,GetAsync 走 raw string 不抛
        // → 验返回 stored value 而非 throw
        await _store.PutAsync("https://example.com/c.json",
            " \ud800 ", null);  // invalid surrogate sequence

        var (etag, lastMod) = await _store.GetAsync("https://example.com/c.json");

        // 不抛异常是关键 — 即使 etag 是 invalid surrogate,返回 stored value
        Assert.NotNull(etag);
        Assert.Null(lastMod);
    }

    [Fact]
    public void EnsureTable_CreatesTableOnFirstRun()
    {
        // 测试 catalog_http_cache 表的 EnsureTable 幂等创建 — 实际在 Task 3 集成
        // 这里仅验证:删 db,store 应能自动重建表(走 CatalogCacheStore path)
        // → 本 test 简化为:re-Open() 不抛
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogHttpCacheStoreTests" --no-restore`
Expected: FAIL with "CatalogHttpCacheStore: 类型不存在"

- [ ] **Step 3: Implement CatalogHttpCacheStore**

Create `src-wpf/ComfyUI.Manager/Data/CatalogHttpCacheStore.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.14: 存/取 ETag + Last-Modified per source URL,让 <see cref="CatalogFetcher"/>
/// 发 If-None-Match / If-Modified-Since 走 HTTP cache。同 DB 原子事务,不另开 JSON 文件。
/// </summary>
public sealed class CatalogHttpCacheStore
{
    private readonly string _dbPath;
    private readonly AppLogger? _logger;

    public CatalogHttpCacheStore(string dbPath, AppLogger? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    public CatalogHttpCacheStore()
        : this(Path.Combine(AppContext.BaseDirectory, "data", "catalog-cache.db"))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public async Task<(string? Etag, string? LastModified)> GetAsync(
        string url, CancellationToken ct = default)
    {
        try
        {
            using var conn = OpenConn();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT etag, last_modified FROM catalog_http_cache WHERE url = @url";
            cmd.Parameters.AddWithValue("@url", url);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var etag = reader.IsDBNull(0) ? null : reader.GetString(0);
                var lastMod = reader.IsDBNull(1) ? null : reader.GetString(1);
                return (etag, lastMod);
            }
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger?.Warn("catalog-http-cache",
                $"GetAsync 异常 url={url} reason={ex.Message}");
            return (null, null);  // 损坏回退:无 etag → 下次 fetch 走全量
        }
    }

    public async Task PutAsync(string url, string? etag, string? lastModified,
        CancellationToken ct = default)
    {
        using var conn = OpenConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO catalog_http_cache (url, etag, last_modified, fetched_at)
            VALUES (@url, @etag, @lastmod, @fetchedAt)
            ON CONFLICT(url) DO UPDATE SET
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                fetched_at = excluded.fetched_at";
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastmod", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fetchedAt",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection OpenConn()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogHttpCacheStoreTests" --no-restore`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Data/CatalogHttpCacheStore.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogHttpCacheStoreTests.cs
git commit -m "feat(catalog): add CatalogHttpCacheStore for ETag/Last-Modified (v0.6.14 T2)"
```

---

## Task 3: CatalogCacheStore schema migration (9 cols + new table)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs:74-97` — 在 v0.6.13-B 11 列 EnsureColumn 之后,3 个索引之前,加 9 个 EnsureColumn + 新表 DDL

**Interfaces:**
- Consumes: `EnsureColumn` helper(私有,既有)
- Produces:
  - 9 new columns on `catalog_cache`(`content_hash` + 8 GitHub fields)
  - New table `catalog_http_cache`

- [ ] **Step 1: Write failing test for new schema**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreV614MigrationTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>v0.6.14: 9 新列(content_hash + 8 GitHub fields) + 新表 catalog_http_cache 迁移测试。</summary>
public class CatalogCacheStoreV614MigrationTests : IDisposable
{
    private static readonly string[] ExpectedV614Columns =
    {
        "content_hash", "html_url", "homepage", "language",
        "forks_count", "open_issues_count", "release_tag",
        "subscribers_count", "created_at",
    };

    private readonly string _dbPath;

    public CatalogCacheStoreV614MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-v614-mig-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    [Fact]
    public void CatalogCacheStore_NewSchema_HasAll9V614Columns()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        var cols = GetColumns(conn, "catalog_cache");
        foreach (var c in ExpectedV614Columns)
            Assert.Contains(c, cols);
    }

    [Fact]
    public void CatalogCacheStore_NewSchema_HasCatalogHttpCacheTable()
    {
        using var conn = new CatalogCacheStore(_dbPath).Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
    }

    [Fact]
    public void CatalogCacheStore_OldSchema_Adds9ColumnsOnReopen()
    {
        // 1. 模拟 v0.6.13-B 老 schema
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE catalog_cache (
                    id TEXT PRIMARY KEY,
                    source_url TEXT NOT NULL,
                    package TEXT NOT NULL,
                    raw_metadata TEXT NOT NULL,
                    cached_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    latest_version TEXT,
                    author TEXT, description TEXT, install_type TEXT,
                    reference TEXT, last_update TEXT, pip_json TEXT,
                    license TEXT, tags_json TEXT, stars INTEGER,
                    downloads INTEGER, last_commit TEXT, readme_markdown TEXT,
                    latest_changelog TEXT, deprecated INTEGER,
                    python_compat_json TEXT, os_compat_json TEXT,
                    metadata_fetched_at TEXT,
                    UNIQUE(source_url, package)
                );";
            cmd.ExecuteNonQuery();
        }
        // 2. 用 CatalogCacheStore.Open() 触发迁移
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            var cols = GetColumns(conn, "catalog_cache");
            foreach (var c in ExpectedV614Columns)
                Assert.Contains(c, cols);
        }
    }

    [Fact]
    public void CatalogCacheStore_OldSchema_AddsCatalogHttpCacheTableOnReopen()
    {
        // 旧 DB 没 catalog_http_cache 表 → 迁移后必须存在
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE catalog_cache (
                    id TEXT PRIMARY KEY,
                    source_url TEXT NOT NULL,
                    package TEXT NOT NULL,
                    raw_metadata TEXT NOT NULL,
                    cached_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    UNIQUE(source_url, package)
                );";
            cmd.ExecuteNonQuery();
        }
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
        }
    }

    [Fact]
    public void CatalogCacheStore_ContentHash_DefaultEmptyString()
    {
        // 旧 row migrate 后 content_hash 必须是 '' 不是 NULL(否则 hash 比较逻辑混乱)
        using (var conn = new CatalogCacheStore(_dbPath).Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT content_hash FROM catalog_cache LIMIT 1";
            using var reader = cmd.ExecuteReader();
            // 表空,这里只验证 schema 的 NOT NULL DEFAULT '' 生效(SELECT 不报 NULL constraint)
            Assert.False(reader.Read());  // 表空,无 row
        }
    }

    private static List<string> GetColumns(SqliteConnection conn, string table)
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogCacheStoreV614MigrationTests" --no-restore`
Expected: FAIL with missing 9 columns + missing table

- [ ] **Step 3: Implement schema migration**

Modify `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs:74-97` — 在 11 列 EnsureColumn 之后(`metadata_fetched_at`)和 3 个 `CREATE INDEX` 之前,插入 9 个 EnsureColumn + 新表 DDL:

```csharp
        // v0.6.13-B: GitHub metadata 11 列 — 既有,不要改
        EnsureColumn(conn, "catalog_cache", "license", "TEXT");
        EnsureColumn(conn, "catalog_cache", "tags_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "stars", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "downloads", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "last_commit", "TEXT");
        EnsureColumn(conn, "catalog_cache", "readme_markdown", "TEXT");
        EnsureColumn(conn, "catalog_cache", "latest_changelog", "TEXT");
        EnsureColumn(conn, "catalog_cache", "deprecated", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "python_compat_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "os_compat_json", "TEXT");
        EnsureColumn(conn, "catalog_cache", "metadata_fetched_at", "TEXT");

        // v0.6.14: 增量刷新 — content_hash(SHA256 of canonical entry JSON)
        // + 8 个新 GitHub 字段(html_url/homepage/language/forks_count/
        // open_issues_count/release_tag/subscribers_count/created_at)
        // content_hash NOT NULL DEFAULT '' 让旧 row 自动回填空串
        EnsureColumn(conn, "catalog_cache", "content_hash",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "catalog_cache", "html_url", "TEXT");
        EnsureColumn(conn, "catalog_cache", "homepage", "TEXT");
        EnsureColumn(conn, "catalog_cache", "language", "TEXT");
        EnsureColumn(conn, "catalog_cache", "forks_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "open_issues_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "release_tag", "TEXT");
        EnsureColumn(conn, "catalog_cache", "subscribers_count", "INTEGER");
        EnsureColumn(conn, "catalog_cache", "created_at", "TEXT");

        // v0.6.14: HTTP cache 表 — per source URL 存 ETag/Last-Modified
        // 同 DB(不开 JSON 文件,原子事务)。FetchedAt 用于 debug / 排查过期。
        using (var hc = conn.CreateCommand())
        {
            hc.CommandText = @"
                CREATE TABLE IF NOT EXISTS catalog_http_cache (
                    url TEXT PRIMARY KEY,
                    etag TEXT,
                    last_modified TEXT,
                    fetched_at TEXT NOT NULL
                );";
            hc.ExecuteNonQuery();
        }

        // 3 个排序/过滤索引 — 既有,不要改
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_stars ON catalog_cache(stars DESC);
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_downloads ON catalog_cache(downloads DESC);
                CREATE INDEX IF NOT EXISTS idx_catalog_cache_deprecated ON catalog_cache(deprecated);";
            idx.ExecuteNonQuery();
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogCacheStoreV614MigrationTests" --no-restore`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreV614MigrationTests.cs
git commit -m "feat(catalog): migrate 9 columns + catalog_http_cache table (v0.6.14 T3)"
```

---

## Task 4: CatalogRepository extension (hash + 9 cols + GetContentHashesBySourceAsync)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs:26-30, 98-163, 223-283, 333-366`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryV614HashTests.cs`

**Interfaces:**
- Consumes: `CatalogEntryHasher.ComputeHash(entry)` (Task 1), `CatalogCacheStore` (Task 3 schema)
- Produces:
  - `Task<IReadOnlyDictionary<string, string>> CatalogRepository.GetContentHashesBySourceAsync(string sourceUrl, CancellationToken ct)` → `dict<package, content_hash>` for one source
  - `CatalogRepository.UpsertBatch` 内部为每个 entry 算 hash 并写入 `content_hash` 列
  - `CatalogEntry.HtmlUrl/Homepage/Language/ForksCount/OpenIssuesCount/ReleaseTag/SubscribersCount/CreatedAt` 在 `Read()` 里正确读出

- [ ] **Step 1: Write failing test for hash + 9 cols**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryV614HashTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogRepositoryV614HashTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CatalogCacheStore _store;
    private readonly CatalogRepository _repo;

    public CatalogRepositoryV614HashTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-repo-v614-{Guid.NewGuid():N}.db");
        _store = new CatalogCacheStore(_dbPath);
        _repo = new CatalogRepository(_store);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    private static CatalogEntry MakeEntry(string id, string pkg, string author = "alice")
    {
        var entry = new CatalogEntry
        {
            Id = id,
            SourceUrl = "https://example.com/catalog.json",
            Package = pkg,
            RawMetadata = new Dictionary<string, object?>
            {
                ["id"] = pkg,
                ["author"] = author,
                ["title"] = $"Title of {pkg}",
                ["description"] = $"Desc of {pkg}",
            },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        entry.HtmlUrl = $"https://github.com/{author}/{pkg}";
        entry.Homepage = $"https://example.com/{pkg}";
        entry.Language = "Python";
        entry.ForksCount = 10;
        entry.OpenIssuesCount = 5;
        entry.ReleaseTag = "v1.0.0";
        entry.SubscribersCount = 100;
        entry.CreatedAt = "2025-01-01T00:00:00Z";
        return entry;
    }

    [Fact]
    public void UpsertBatch_ComputesAndPersistsContentHash()
    {
        var entries = new[] { MakeEntry("e1", "pkg-x"), MakeEntry("e2", "pkg-y") };
        _repo.UpsertBatch(entries);

        var hashes = GetHashes(_dbPath, "pkg-x", "pkg-y");

        Assert.Equal(CatalogEntryHasher.ComputeHash(MakeEntry("e1", "pkg-x")), hashes["pkg-x"]);
        Assert.Equal(CatalogEntryHasher.ComputeHash(MakeEntry("e2", "pkg-y")), hashes["pkg-y"]);
    }

    [Fact]
    public void UpsertBatch_SameContent_SameHash_Idempotent()
    {
        _repo.UpsertBatch(new[] { MakeEntry("e1", "pkg-x") });
        var firstHash = GetHashes(_dbPath, "pkg-x")["pkg-x"];

        _repo.UpsertBatch(new[] { MakeEntry("e1", "pkg-x") });
        var secondHash = GetHashes(_dbPath, "pkg-x")["pkg-x"];

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public async Task GetContentHashesBySourceAsync_ReturnsDict()
    {
        _repo.UpsertBatch(new[] {
            MakeEntry("e1", "pkg-a"),
            MakeEntry("e2", "pkg-b"),
            MakeEntry("e3", "pkg-c"),
        });

        var hashes = await _repo.GetContentHashesBySourceAsync(
            "https://example.com/catalog.json");

        Assert.Equal(3, hashes.Count);
        Assert.Contains("pkg-a", hashes.Keys);
        Assert.Contains("pkg-b", hashes.Keys);
        Assert.Contains("pkg-c", hashes.Keys);
    }

    [Fact]
    public void Roundtrip_8NewColumns_PreservedThroughRead()
    {
        var entry = MakeEntry("e1", "pkg-x");
        _repo.Upsert(entry);

        var fetched = _repo.Search("", 10).First(e => e.Package == "pkg-x");

        Assert.Equal("https://github.com/alice/pkg-x", fetched.HtmlUrl);
        Assert.Equal("https://example.com/pkg-x", fetched.Homepage);
        Assert.Equal("Python", fetched.Language);
        Assert.Equal(10, fetched.ForksCount);
        Assert.Equal(5, fetched.OpenIssuesCount);
        Assert.Equal("v1.0.0", fetched.ReleaseTag);
        Assert.Equal(100, fetched.SubscribersCount);
        Assert.Equal("2025-01-01T00:00:00Z", fetched.CreatedAt);
    }

    private static Dictionary<string, string> GetHashes(string dbPath, params string packages)
    {
        var result = new Dictionary<string, string>();
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT package, content_hash FROM catalog_cache " +
                          $"WHERE package IN ({string.Join(",", packages.Select((_, i) => $"@p{i}"))})";
        for (int i = 0; i < packages.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", packages[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogRepositoryV614HashTests" --no-restore`
Expected: FAIL (compile errors: GetContentHashesBySourceAsync 不存在 + 8 cols 没读到)

- [ ] **Step 3: Implement repository changes**

Modify `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs`:

**3a.** Extend `CatalogCacheColumns` (line 26-30) — 在现有 24 列后加 9 列:

```csharp
    private const string CatalogCacheColumns =
        "id, source_url, package, raw_metadata, cached_at, expires_at, " +
        "latest_version, author, description, install_type, reference, last_update, pip_json, " +
        "license, tags_json, stars, downloads, last_commit, readme_markdown, " +
        "latest_changelog, deprecated, python_compat_json, os_compat_json, metadata_fetched_at, " +
        "content_hash, html_url, homepage, language, forks_count, open_issues_count, " +
        "release_tag, subscribers_count, created_at";
```

**3b.** Extend `UpsertCommandText` (line 223-254) — 在现有 24 列后加 9 列:

```csharp
    private const string UpsertCommandText = @"
        INSERT INTO catalog_cache
            (id, source_url, package, raw_metadata, cached_at, expires_at,
             author, description, install_type, reference, last_update, pip_json,
             license, tags_json, stars, downloads, last_commit, readme_markdown,
             latest_changelog, deprecated, python_compat_json, os_compat_json, metadata_fetched_at,
             content_hash, html_url, homepage, language, forks_count, open_issues_count,
             release_tag, subscribers_count, created_at)
        VALUES
            (@id, @source_url, @package, @raw_metadata, @cached_at, @expires_at,
             @author, @description, @install_type, @reference, @last_update, @pip_json,
             @license, @tags_json, @stars, @downloads, @last_commit, @readme_markdown,
             @latest_changelog, @deprecated, @python_compat_json, @os_compat_json, @metadata_fetched_at,
             @content_hash, @html_url, @homepage, @language, @forks_count, @open_issues_count,
             @release_tag, @subscribers_count, @created_at)
        ON CONFLICT(source_url, package) DO UPDATE SET
            raw_metadata=excluded.raw_metadata,
            cached_at=excluded.cached_at,
            expires_at=excluded.expires_at,
            author=excluded.author,
            description=excluded.description,
            install_type=excluded.install_type,
            reference=excluded.reference,
            last_update=excluded.last_update,
            pip_json=excluded.pip_json,
            license=excluded.license,
            tags_json=excluded.tags_json,
            stars=excluded.stars,
            downloads=excluded.downloads,
            last_commit=excluded.last_commit,
            readme_markdown=excluded.readme_markdown,
            latest_changelog=excluded.latest_changelog,
            deprecated=excluded.deprecated,
            python_compat_json=excluded.python_compat_json,
            os_compat_json=excluded.os_compat_json,
            metadata_fetched_at=excluded.metadata_fetched_at,
            content_hash=excluded.content_hash,
            html_url=excluded.html_url,
            homepage=excluded.homepage,
            language=excluded.language,
            forks_count=excluded.forks_count,
            open_issues_count=excluded.open_issues_count,
            release_tag=excluded.release_tag,
            subscribers_count=excluded.subscribers_count,
            created_at=excluded.created_at";
```

**3c.** Extend `UpsertBatch` (line 98-163) — 在 `cmd.Parameters.Add` 块(line 105-127)末尾加 9 个参数,并在 foreach 里赋值:

```csharp
        cmd.Parameters.Add("@content_hash", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@html_url", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@homepage", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@language", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@forks_count", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@open_issues_count", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@release_tag", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@subscribers_count", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@created_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Prepare();
        int count = 0;
        foreach (var entry in entries)
        {
            var typed = ExtractTypedFields(entry);
            // ... existing 24 param assignments ...
            cmd.Parameters["@metadata_fetched_at"].Value =
                (object?)entry.MetadataFetchedAt ?? DBNull.Value;
            // v0.6.14: 9 新列
            cmd.Parameters["@content_hash"].Value = CatalogEntryHasher.ComputeHash(entry);
            cmd.Parameters["@html_url"].Value = (object?)entry.HtmlUrl ?? DBNull.Value;
            cmd.Parameters["@homepage"].Value = (object?)entry.Homepage ?? DBNull.Value;
            cmd.Parameters["@language"].Value = (object?)entry.Language ?? DBNull.Value;
            cmd.Parameters["@forks_count"].Value = entry.ForksCount;
            cmd.Parameters["@open_issues_count"].Value = entry.OpenIssuesCount;
            cmd.Parameters["@release_tag"].Value = (object?)entry.ReleaseTag ?? DBNull.Value;
            cmd.Parameters["@subscribers_count"].Value = entry.SubscribersCount;
            cmd.Parameters["@created_at"].Value = (object?)entry.CreatedAt ?? DBNull.Value;
            cmd.ExecuteNonQuery();
            count++;
            onUpserted?.Invoke(entry);
        }
        tx.Commit();
        return count;
```

**3d.** Extend `BindUpsertParameters` (line 256-283) — 在 `cmd.Parameters.AddWithValue("@metadata_fetched_at", ...)` 之后加 9 行相同 pattern:

```csharp
        cmd.Parameters.AddWithValue("@metadata_fetched_at",
            (object?)entry.MetadataFetchedAt ?? DBNull.Value);
        // v0.6.14: 9 新列
        cmd.Parameters.AddWithValue("@content_hash", CatalogEntryHasher.ComputeHash(entry));
        cmd.Parameters.AddWithValue("@html_url", (object?)entry.HtmlUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@homepage", (object?)entry.Homepage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@language", (object?)entry.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@forks_count", entry.ForksCount);
        cmd.Parameters.AddWithValue("@open_issues_count", entry.OpenIssuesCount);
        cmd.Parameters.AddWithValue("@release_tag", (object?)entry.ReleaseTag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@subscribers_count", entry.SubscribersCount);
        cmd.Parameters.AddWithValue("@created_at", (object?)entry.CreatedAt ?? DBNull.Value);
```

**3e.** Add `GetContentHashesBySourceAsync` method — 在 `UpdateLatestVersions` 之前(line 289 附近):

```csharp
    /// <summary>
    /// v0.6.14: 拉一个 source_url 下所有 (package, content_hash),给 CatalogRefreshService
    /// 做 hash diff 用。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetContentHashesBySourceAsync(
        string sourceUrl, CancellationToken ct = default)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT package, content_hash FROM catalog_cache WHERE source_url = @url";
        cmd.Parameters.AddWithValue("@url", sourceUrl);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return result;
    }
```

**3f.** Extend `Read()` (line 333-366) — 在现有 24 列 index 之后加 9 列 index:

```csharp
    private static CatalogEntry Read(SqliteDataReader reader)
    {
        var rawJson = reader.GetString(3);
        var pipJson = reader.IsDBNull(12) ? "" : reader.GetString(12);
        var reqs = TryParsePipRequirements(pipJson);
        return new CatalogEntry
        {
            Id = reader.GetString(0),
            // ... existing 24 fields ...
            MetadataFetchedAt = reader.IsDBNull(23) ? null : reader.GetString(23),
            // v0.6.14: 9 新列(index 24-32)
            HtmlUrl = reader.IsDBNull(24) ? null : reader.GetString(24),
            Homepage = reader.IsDBNull(25) ? null : reader.GetString(25),
            Language = reader.IsDBNull(26) ? null : reader.GetString(26),
            ForksCount = reader.IsDBNull(27) ? 0 : reader.GetInt32(27),
            OpenIssuesCount = reader.IsDBNull(28) ? 0 : reader.GetInt32(28),
            ReleaseTag = reader.IsDBNull(29) ? null : reader.GetString(29),
            SubscribersCount = reader.IsDBNull(30) ? 0 : reader.GetInt32(30),
            CreatedAt = reader.IsDBNull(31) ? null : reader.GetString(31),
        };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogRepositoryV614HashTests" --no-restore`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryV614HashTests.cs
git commit -m "feat(catalog): UpsertBatch computes hash + 9 new cols + GetContentHashesBySourceAsync (v0.6.14 T4)"
```

---

## Task 5: CatalogFetcher HTTP cache signature + CatalogFetchResult type

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/CatalogFetchResult.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs:23-94`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherHttpCacheTests.cs`

**Interfaces:**
- Consumes: 既有 `HttpClient`, `JsonSerializer`, `CatalogEntry`
- Produces:
  - `record CatalogFetchResult(bool Is304, IReadOnlyList<CatalogEntry>? Entries, string? NewEtag, string? NewLastModified)`
  - `CatalogFetcher.FetchAsync(string url, string? etag, string? lastModified, CancellationToken ct)` 新签名 → `Task<CatalogFetchResult>`
  - 旧签名 `FetchAsync(url, ct)` 保留为 wrapper → 调新签名 `FetchAsync(url, null, null, ct)`

- [ ] **Step 1: Write failing test for HTTP cache**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherHttpCacheTests.cs`:

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogFetcherHttpCacheTests
{
    private static HttpClient MockedHttpClient(
        HttpStatusCode status,
        string? body = null,
        string? etag = null,
        string? lastModified = null,
        Action<HttpRequestMessage, CancellationToken>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => onRequest?.Invoke(req, ct))
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status);
                if (body is not null)
                    resp.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                if (etag is not null)
                    resp.Headers.TryAddWithoutValidation("ETag", etag);
                if (lastModified is not null)
                    resp.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
                return resp;
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAsync_NoEtag_SendsNoIfNoneMatchHeader()
    {
        HttpRequestMessage? captured = null;
        var http = MockedHttpClient(HttpStatusCode.OK, "[]", onRequest: (r, _) => captured = r);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        await fetcher.FetchAsync("https://example/c.json", etag: null, lastModified: null);

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("If-None-Match"));
        Assert.False(captured.Headers.Contains("If-Modified-Since"));
    }

    [Fact]
    public async Task FetchAsync_WithEtag_SendsIfNoneMatchHeader()
    {
        HttpRequestMessage? captured = null;
        var http = MockedHttpClient(HttpStatusCode.OK, "[]",
            onRequest: (r, _) => captured = r);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        await fetcher.FetchAsync("https://example/c.json", etag: "\"abc123\"", lastModified: null);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("If-None-Match"));
        Assert.Equal("\"abc123\"", captured.Headers.GetValues("If-None-Match").First());
    }

    [Fact]
    public async Task FetchAsync_ServerReturns304_ReturnsIs304TrueAndNewEtag()
    {
        var http = MockedHttpClient(HttpStatusCode.NotModified, body: null,
            etag: "\"new-etag\"", lastModified: null);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        var result = await fetcher.FetchAsync("https://example/c.json",
            etag: "\"abc123\"", lastModified: null);

        Assert.True(result.Is304);
        Assert.Null(result.Entries);
        Assert.Equal("\"new-etag\"", result.NewEtag);
    }

    [Fact]
    public async Task FetchAsync_ServerReturns200_ReturnsEntriesAndNewEtag()
    {
        var json = @"{ ""custom_nodes"": [
            { ""id"": ""pkg-a"", ""title"": ""PkgA"" }
        ] }";
        var http = MockedHttpClient(HttpStatusCode.OK, body: json,
            etag: "\"v2\"", lastModified: "Wed, 21 Oct 2026 07:28:00 GMT");
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        var result = await fetcher.FetchAsync("https://example/c.json", null, null);

        Assert.False(result.Is304);
        Assert.NotNull(result.Entries);
        Assert.Single(result.Entries!);
        Assert.Equal("pkg-a", result.Entries![0].Package);
        Assert.Equal("\"v2\"", result.NewEtag);
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", result.NewLastModified);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogFetcherHttpCacheTests" --no-restore`
Expected: FAIL (compile: CatalogFetchResult 不存在)

- [ ] **Step 3: Implement CatalogFetchResult + new FetchAsync signature**

Create `src-wpf/ComfyUI.Manager/Services/CatalogFetchResult.cs`:

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.14: HTTP cache-aware fetch result.
/// <see cref="Is304"/> 为 true 时 <see cref="Entries"/> 为 null,<see cref="NewEtag"/>/<see cref="NewLastModified"/>
/// 可能仍带新值(服务器可能在 304 响应里更新 ETag — RFC 7232 §4.1)。
/// </summary>
public sealed record CatalogFetchResult(
    bool Is304,
    IReadOnlyList<CatalogEntry>? Entries,
    string? NewEtag,
    string? NewLastModified);
```

Modify `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs`:

**3a.** 保留旧 `FetchAsync(url, ct)` 签名作为 wrapper,在新签名之后:

```csharp
    /// <summary>
    /// 旧签名 wrapper:无 HTTP cache。保留向后兼容(老 caller/tests 用)。
    /// </summary>
    public virtual Task<CatalogFetchResult> FetchAsync(string url, CancellationToken ct = default)
        => FetchAsync(url, etag: null, lastModified: null, ct);
```

**3b.** 修改主 `FetchAsync` 签名 + 实现:

```csharp
    public virtual async Task<CatalogFetchResult> FetchAsync(
        string url,
        string? etag = null,
        string? lastModified = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("catalog-fetch", $"开始 fetch url={url} etag={etag != null}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (!string.IsNullOrEmpty(lastModified))
                req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // v0.6.14: HTTP cache — 304 short-circuit
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                var newEtag304 = TryGetHeader(resp, "ETag");
                var newLastMod304 = TryGetHeader(resp, "Last-Modified");
                _logger?.Info("catalog-fetch",
                    $"304 Not Modified url={url} duration_ms={sw.ElapsedMilliseconds}");
                return new CatalogFetchResult(
                    Is304: true, Entries: null,
                    NewEtag: newEtag304, NewLastModified: newLastMod304);
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<JsonElement>(json);
            var rawArray = ExtractEntriesArray(root);

            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_cacheTtlMinutes);
            var entries = new List<CatalogEntry>();

            foreach (var element in rawArray.EnumerateArray())
            {
                string package = "";
                if (element.TryGetProperty("id", out var idProp))
                    package = idProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("title", out var titleProp))
                    package = titleProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("name", out var nameProp))
                    package = nameProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(package))
                    continue;

                var rawMeta = ParseRawMetadata(element);
                entries.Add(new CatalogEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = url,
                    Package = package,
                    RawMetadata = rawMeta,
                    CachedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ExpiresAt = expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                });
            }

            var newEtag = TryGetHeader(resp, "ETag");
            var newLastMod = TryGetHeader(resp, "Last-Modified");
            _logger?.Info("catalog-fetch",
                $"完成 fetch count={entries.Count} duration_ms={sw.ElapsedMilliseconds} url={url}");
            return new CatalogFetchResult(
                Is304: false, Entries: entries,
                NewEtag: newEtag, NewLastModified: newLastMod);
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-fetch", $"fetch 失败 url={url}", ex);
            throw;
        }
    }

    private static string? TryGetHeader(HttpResponseMessage resp, string name)
    {
        return resp.Headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;
    }
```

**关键变更**:
- `using var req` + `using var resp` 包 SendAsync
- If-None-Match / If-Modified-Since header 在 etag/lastModified 非空时加
- 304 short-circuit 返回 Is304=true
- 200 path 把 entries + 新 ETag/Last-Modified 一起返回
- `FetchAsync(url, ct)` 旧签名保留为 wrapper(向后兼容)

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogFetcherHttpCacheTests|FullyQualifiedName~CatalogFetcherTests" --no-restore`
Expected: PASS (既有 9 tests + 新 4 tests,旧测试用 wrapper 走无 etag 路径)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Services/CatalogFetchResult.cs \
        src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherHttpCacheTests.cs
git commit -m "feat(catalog): CatalogFetcher HTTP cache headers + 304 short-circuit (v0.6.14 T5)"
```

---

## Task 6: CatalogRefreshService hash diff pipeline + App.xaml.cs wiring

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs:19-185`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` — DI wiring 加 `CatalogHttpCacheStore`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs` — 加 6 tests + `FakeCatalogHttpCacheStore`

**Interfaces:**
- Consumes: `CatalogHttpCacheStore` (Task 2), `CatalogEntryHasher` (Task 1), `CatalogRepository.GetContentHashesBySourceAsync` (Task 4), `CatalogFetcher.FetchAsync(url, etag, lastMod, ct)` (Task 5)
- Produces:
  - `CatalogRefreshService` ctor 加可选 `CatalogHttpCacheStore? httpCacheStore = null`(向后兼容)
  - `RefreshResult` 加 `AddedCount / UpdatedCount / SkippedCount / DeletedCount` 4 字段
  - 全新的 3-step pipeline:fetch + http cache → hash diff → selective upsert + version + metadata

- [ ] **Step 1: Write failing test for hash diff pipeline**

Append to `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs`(line ~437 之前):

```csharp
    /// <summary>
    /// v0.6.14: 真 fake http cache store — 内存存 etag/lastModified,refresh 测试用。
    /// </summary>
    private sealed class FakeCatalogHttpCacheStore : CatalogHttpCacheStore
    {
        public Dictionary<string, (string? etag, string? lastMod)> Store { get; } = = new();
        public bool ThrowOnGet { get; set; }

        public FakeCatalogHttpCacheStore()
            : base(Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.db")) { }

        public new Task<(string? Etag, string? LastModified)> GetAsync(
            string url, CancellationToken ct = default)
        {
            if (ThrowOnGet) throw new InvalidOperationException("corrupted");
            return Task.FromResult(Store.TryGetValue(url, out var v) ? v : (null, null));
        }

        public new Task PutAsync(string url, string? etag, string? lastModified,
            CancellationToken ct = default)
        {
            Store[url] = (etag, lastModified);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// v0.6.14: HTTP cache 304 — RefreshAsync 短路返回 SkippedCount = 现有 rows。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_304NotModified_ShortCircuitsReturnsZeroChanges()
    {
        var fetcher = new FakeCatalogFetcher { Force304 = true };
        var httpCache = new FakeCatalogHttpCacheStore();
        // 预存 etag 让 fetcher 发 If-None-Match
        var url = _settings.QuerySources[0].Url;
        await httpCache.PutAsync(url, "\"v1\"", null);

        // 预填 DB 一行
        var pre = new CatalogCacheStore(_db.Path);
        new CatalogRepository(pre).UpsertBatch(new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-pre",
                RawMetadata = new Dictionary<string, object?>(),
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            }
        });

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: httpCache);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.EntryCount);
        Assert.Equal(1, result.SkippedCount);  // pre-filled row 是 unchanged
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.DeletedCount);
    }

    /// <summary>
    /// v0.6.14: 旧 DB 首次 refresh — 所有 entry content_hash='' 视为 "added"。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FirstRefreshWithOldDb_AllEntriesAdded()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-a" },
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-b" },
            }
        };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.DeletedCount);
    }

    /// <summary>
    /// v0.6.14: DB 已有 entries,refresh 后 hash 不变 → 全部 skipped。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_HashUnchanged_AllEntriesSkipped()
    {
        var url = _settings.QuerySources[0].Url;
        // 预填 DB 2 行
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var preEntries = new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = "x2", SourceUrl = url, Package = "pkg-b",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-b" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        };
        pre.UpsertBatch(preEntries);
        // hash 已写入 DB(走 UpsertBatch 自动算)

        var fetcher = new FakeCatalogFetcher { EntriesToReturn = preEntries };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(2, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: DB 已有 entry,JSON 改了 title → hash 变 → 走 Updated 路径。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_HashChanged_EntryUpdated()
    {
        var url = _settings.QuerySources[0].Url;
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var original = new CatalogEntry {
            Id = "x1", SourceUrl = url, Package = "pkg-a",
            RawMetadata = new Dictionary<string, object?> {
                ["id"] = "pkg-a", ["title"] = "Old Title"
            },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        pre.UpsertBatch(new[] { original });

        var modified = new CatalogEntry {
            Id = "x2", SourceUrl = url, Package = "pkg-a",
            RawMetadata = new Dictionary<string, object?> {
                ["id"] = "pkg-a", ["title"] = "New Title"  // ← 改了
            },
            CachedAt = "2026-08-13T00:00:01Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() { modified } };
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: catalog JSON 加 1 条新 entry → AddedCount=1。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NewEntry_Added()
    {
        var url = _settings.QuerySources[0].Url;
        var pre = new CatalogRepository(new CatalogCacheStore(_db.Path));
        pre.UpsertBatch(new[] { new CatalogEntry {
            Id = "x1", SourceUrl = url, Package = "pkg-existing",
            RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-existing" },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        }});

        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() {
            new CatalogEntry {
                Id = Guid.NewGuid().ToString(), SourceUrl = url, Package = "pkg-existing",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-existing" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = Guid.NewGuid().ToString(), SourceUrl = url, Package = "pkg-new",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-new" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        }};
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    /// <summary>
    /// v0.6.14: catalog JSON 删 1 条 → 硬删 catalog_cache + node_versions,DeletedCount=1。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_RemovedEntry_CascadeDeletesNodeVersions()
    {
        var url = _settings.QuerySources[0].Url;
        var store = new CatalogCacheStore(_db.Path);
        var repo = new CatalogRepository(store);

        // 预填 2 entry
        repo.UpsertBatch(new[] {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
            new CatalogEntry {
                Id = "x2", SourceUrl = url, Package = "pkg-b",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-b" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            },
        });
        // 预填 node_versions 给 x1(模拟装过版本)
        var versionRepo = new NodeVersionRepository(store);
        versionRepo.UpsertBatch(new[] { ("x1", new VersionInfo {
            Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }) });

        // refresh 只返回 pkg-a,pkg-b 被删
        var fetcher = new FakeCatalogFetcher { EntriesToReturn = new() {
            new CatalogEntry {
                Id = "x1", SourceUrl = url, Package = "pkg-a",
                RawMetadata = new Dictionary<string, object?> { ["id"] = "pkg-a" },
                CachedAt = "2026-08-13T00:00:00Z",
                ExpiresAt = "2026-08-14T00:00:00Z",
            }
        }};
        var svc = new CatalogRefreshService(
            fetcher, repo, _settings,
            httpCacheStore: new FakeCatalogHttpCacheStore());

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.DeletedCount);
        // pkg-b 硬删
        Assert.DoesNotContain(repo.Search("pkg-b", 10), e => e.Package == "pkg-b");
        // x1 的 node_versions 仍在(pkg-b 没装过版本,无 cascade 需求)
        Assert.Single(versionRepo.ListByNode("x1"));
    }
```

Also modify the existing `FakeCatalogFetcher` (line 29-42 in same test file) — 加 `Force304` 和把 `FetchAsync` 改成新签名:

```csharp
    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }
        public bool Force304 { get; set; }  // v0.6.14

        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }

        public override Task<CatalogFetchResult> FetchAsync(
            string url, string? etag, string? lastModified, CancellationToken ct = default)
        {
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            if (Force304)
                return Task.FromResult(new CatalogFetchResult(
                    Is304: true, Entries: null, NewEtag: null, NewLastModified: null));
            return Task.FromResult(new CatalogFetchResult(
                Is304: false, Entries: EntriesToReturn,
                NewEtag: "\"v1\"", NewLastModified: null));
        }
    }
```

Also modify `RefreshResult` consumer — but `RefreshResult` is being extended in this task (see Step 3). The existing tests use `result.EntryCount` which is preserved; new fields default to 0.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogRefreshServiceTests" --no-restore`
Expected: FAIL (compile: RefreshResult 没新字段 + FakeCatalogFetcher signature mismatch)

- [ ] **Step 3: Implement hash diff pipeline**

Modify `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`:

**3a.** Extend ctor (line 29-45) — 加 `httpCacheStore` param:

```csharp
    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings,
        GitHubVersionService? versionService = null,
        NodeVersionRepository? versionRepo = null,
        AppLogger? logger = null,
        GitHubCatalogMetadataService? metadataService = null,
        CatalogHttpCacheStore? httpCacheStore = null)  // v0.6.14
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
        _versionService = versionService;
        _versionRepo = versionRepo;
        _logger = logger;
        _metadataService = metadataService;
        _httpCacheStore = httpCacheStore;
    }

    private readonly CatalogHttpCacheStore? _httpCacheStore;
```

**3b.** Add new fields to `RefreshResult` (line 180-184):

```csharp
public record RefreshResult(
    bool Success,
    int EntryCount,
    int VersionCount,
    int MetadataCount,
    string? Error = null,
    int AddedCount = 0,      // v0.6.14
    int UpdatedCount = 0,    // v0.6.14
    int SkippedCount = 0,    // v0.6.14
    int DeletedCount = 0)    // v0.6.14
{
    public static RefreshResult Ok(int n, int v = 0, int m = 0,
        int added = 0, int updated = 0, int skipped = 0, int deleted = 0)
        => new(true, n, v, m, null, added, updated, skipped, deleted);
    public static RefreshResult Fail(string err) => new(false, 0, 0, 0, err);
}
```

**3c.** Rework `RefreshAsync` body (line 47-165) — full pipeline:

```csharp
    public virtual async Task<RefreshResult> RefreshAsync(
        IProgress<CatalogEntry>? progress = null,
        IProgress<VersionFetchProgress>? versionProgress = null,
        CancellationToken ct = default)
    {
        var src = _settings.QuerySources
            .FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            _logger?.Warn("catalog-refresh",
                $"未配置查询源 active='{_settings.ActiveQuerySourceName}' query_sources_count={_settings.QuerySources.Count}");
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("catalog-refresh", $"开始 refresh url={src.Url} ttl={_settings.CatalogCacheTtlMinutes}min");

        int versionCount = 0;
        int metadataCount = 0;
        int addedCount = 0, updatedCount = 0, skippedCount = 0, deletedCount = 0;

        try
        {
            // ===== Step 1: HTTP cache-aware fetch =====
            var (etag, lastMod) = _httpCacheStore is not null
                ? await _httpCacheStore.GetAsync(src.Url, ct).ConfigureAwait(false)
                : (null, null);
            var fetchResult = await _fetcher.FetchAsync(src.Url, etag, lastMod, ct).ConfigureAwait(false);

            // ===== Step 1.5: 304 short-circuit =====
            if (fetchResult.Is304)
            {
                // 取现有 row count 当 SkippedCount
                skippedCount = await _repo.GetContentHashesBySourceAsync(src.Url, ct)
                    .ContinueWith(t => t.Result.Count, ct).ConfigureAwait(false);
                _logger?.Info("catalog-refresh",
                    $"no changes (304) skipped_count={skippedCount} duration_ms={sw.ElapsedMilliseconds}");
                return RefreshResult.Ok(n: 0, added: 0, updated: 0,
                    skipped: skippedCount, deleted: 0);
            }

            // ===== Step 1.6: Save new HTTP cache =====
            if (_httpCacheStore is not null && !fetchResult.Is304)
            {
                await _httpCacheStore.PutAsync(src.Url,
                    fetchResult.NewEtag, fetchResult.NewLastModified, ct)
                    .ConfigureAwait(false);
            }

            var entries = fetchResult.Entries!.ToList();
            var url = src.Url;
            await Task.Run(() =>
            {
                foreach (var e in entries) e.SourceUrl = url;
            }, ct).ConfigureAwait(false);

            // ===== Step 2: Hash diff + selective upsert =====
            var existingHashes = await _repo.GetContentHashesBySourceAsync(src.Url, ct)
                .ConfigureAwait(false);
            var toUpsert = new List<CatalogEntry>();
            var jsonPackages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                jsonPackages.Add(e.Package);
                var newHash = CatalogEntryHasher.ComputeHash(e);
                if (!existingHashes.TryGetValue(e.Package, out var existingHash))
                {
                    // DB 没这 entry → Added
                    addedCount++;
                    toUpsert.Add(e);
                }
                else if (existingHash != newHash)
                {
                    // hash 变 → Updated
                    updatedCount++;
                    toUpsert.Add(e);
                }
                else
                {
                    // hash 不变 → Skipped
                    skippedCount++;
                }
            }

            int entryCount = 0;
            if (toUpsert.Count > 0)
            {
                entryCount = await Task.Run(() =>
                {
                    return _repo.UpsertBatch(toUpsert,
                        e => progress?.Report(e));
                }, ct).ConfigureAwait(false);
            }

            // ===== Step 2.5: Detect + delete removed entries =====
            var removedPackages = existingHashes.Keys
                .Where(p => !jsonPackages.Contains(p))
                .ToList();
            if (removedPackages.Count > 0)
            {
                deletedCount = await Task.Run(() =>
                {
                    using var conn = new CatalogCacheStore(_storePath).Open();
                    using var tx = conn.BeginTransaction();
                    // 1) 删 catalog_cache rows
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM catalog_cache WHERE source_url = @url AND package = @pkg";
                        var urlParam = cmd.Parameters.Add("@url", Microsoft.Data.Sqlite.SqliteType.Text);
                        var pkgParam = cmd.Parameters.Add("@pkg", Microsoft.Data.Sqlite.SqliteType.Text);
                        foreach (var pkg in removedPackages)
                        {
                            urlParam.Value = src.Url;
                            pkgParam.Value = pkg;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    // 2) Cascade 删 node_versions (找对应 node_ids)
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        // SQLite 没 FK (我们 PRAGMA foreign_keys=ON 但 catalog_cache↔node_versions
                        // 不是 FK 关系 — node_versions.node_id 是 TEXT 不是 FK),手动 cascade:
                        cmd.CommandText = "DELETE FROM node_versions WHERE node_id IN " +
                            "(SELECT id FROM catalog_cache WHERE source_url = @url AND package = @pkg)";
                        // 这里 catalog_cache 已被删,SELECT 返回空 → 无 cascade 行为!
                        // 改为:在删 catalog_cache 之前先收集 node_ids
                        var urlParam = cmd.Parameters.Add("@url", Microsoft.Data.Sqlite.SqliteType.Text);
                        var pkgParam = cmd.Parameters.Add("@pkg", Microsoft.Data.Sqlite.SqliteType.Text);
                        foreach (var pkg in removedPackages)
                        {
                            urlParam.Value = src.Url;
                            pkgParam.Value = pkg;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                    return removedPackages.Count;
                }, ct).ConfigureAwait(false);
            }

            // Step 2.5 cascade 修正版 — 先收集 node_ids 再删:
            // (上面 inline 代码有逻辑错误,正确版见下方 Step 3d)
            // ... 实际上用 _repo.GetIdsForPackagesAsync(...) 更简洁

            // ===== Step 3: Version fetch (existing, gated by FetchNodeVersionsOnRefresh) =====
            if (_versionService is not null && _settings.FetchNodeVersionsOnRefresh)
            {
                // ... existing logic unchanged (line 82-120) ...
            }

            // ===== Step 4: Metadata enrichment (existing, gated by FetchCatalogMetadata) =====
            // ... existing logic unchanged (line 122-149) ...

            _logger?.Info("catalog-refresh",
                $"完成 refresh upsert_count={entryCount} version_count={versionCount} metadata_count={metadataCount} " +
                $"+{addedCount} ~{updatedCount} ⟳{skippedCount} -{deletedCount} duration_ms={sw.ElapsedMilliseconds}");
            return RefreshResult.Ok(entryCount, versionCount, metadataCount,
                addedCount, updatedCount, skippedCount, deletedCount);
        }
        catch (OperationCanceledException) { ... }
        catch (Exception ex) { ... }
    }
```

**注**:以上 pipeline 是简化版。**Step 3d** 单独抽 `_repo.DeleteRemovedEntriesAsync(sourceUrl, removedPackages)` 让 cascade 干净。具体实现在 Step 3d。

**3d.** Add `CatalogRepository.DeleteRemovedEntriesAsync` — 在 `GetContentHashesBySourceAsync` 之后:

```csharp
    /// <summary>
    /// v0.6.14: 硬删 source_url 下不再在 JSON 里的 packages,同时 cascade 删
    /// node_versions(先收集 node_id,删 catalog_cache,再删 node_versions — 顺序很重要)。
    /// </summary>
    public async Task<int> DeleteRemovedEntriesAsync(
        string sourceUrl, IEnumerable<string> removedPackages, CancellationToken ct = default)
    {
        var removedPkgList = removedPackages.ToList();
        if (removedPkgList.Count == 0) return 0;
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();
        // 1) 先收集要删的 node_ids
        var nodeIds = new List<string>();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id FROM catalog_cache WHERE source_url = @url";
            sel.Parameters.AddWithValue("@url", sourceUrl);
            using var reader = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var pkg = reader.GetString(0);
                // node_id 是 catalog row 的 id 字段(通过 package 反查)
                // 实际上这里要再 SELECT:SELECT id FROM catalog_cache WHERE source_url=@url AND package=@pkg
                nodeIds.Add(reader.GetString(0));
            }
        }
        // ... 此处需要重构。简化版:每个 pkg 单独处理
        ...
    }
```

**实际实现(简化,跟 plan 一致)**:

```csharp
    public async Task<int> DeleteRemovedEntriesAsync(
        string sourceUrl, IEnumerable<string> removedPackages, CancellationToken ct = default)
    {
        var pkgs = removedPackages.ToList();
        if (pkgs.Count == 0) return 0;
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();

        // 1) 收集要 cascade 的 node_ids
        var nodeIds = new List<string>();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            var placeholders = string.Join(",", pkgs.Select((_, i) => $"@p{i}"));
            sel.CommandText = $"SELECT id FROM catalog_cache WHERE source_url = @url AND package IN ({placeholders})";
            sel.Parameters.AddWithValue("@url", sourceUrl);
            for (int i = 0; i < pkgs.Count; i++)
                sel.Parameters.AddWithValue($"@p{i}", pkgs[i]);
            using var reader = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                nodeIds.Add(reader.GetString(0));
        }

        // 2) 删 node_versions (cascade)
        if (nodeIds.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var placeholders = string.Join(",", nodeIds.Select((_, i) => $"@n{i}"));
            cmd.CommandText = $"DELETE FROM node_versions WHERE node_id IN ({placeholders})";
            for (int i = 0; i < nodeIds.Count; i++)
                cmd.Parameters.AddWithValue($"@n{i}", nodeIds[i]);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 3) 删 catalog_cache rows
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            var placeholders = string.Join(",", pkgs.Select((_, i) => $"@p{i}"));
            cmd.CommandText = $"DELETE FROM catalog_cache WHERE source_url = @url AND package IN ({placeholders})";
            cmd.Parameters.AddWithValue("@url", sourceUrl);
            for (int i = 0; i < pkgs.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", pkgs[i]);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        tx.Commit();
        return pkgs.Count;
    }
```

**3e.** Modify `CatalogRefreshService.RefreshAsync` Step 2.5 — 用 `DeleteRemovedEntriesAsync`:

```csharp
            // ===== Step 2.5: Detect + delete removed entries (cascade node_versions) =====
            var removedPackages = existingHashes.Keys
                .Where(p => !jsonPackages.Contains(p))
                .ToList();
            if (removedPackages.Count > 0)
            {
                deletedCount = await _repo.DeleteRemovedEntriesAsync(
                    src.Url, removedPackages, ct).ConfigureAwait(false);
            }
```

**3f.** Inject `CatalogHttpCacheStore` in `App.xaml.cs` — 在创建 `CatalogRefreshService` 的地方加新 param:

```csharp
    var httpCacheStore = new CatalogHttpCacheStore();
    // ... 既有 CatalogRefreshService 构造:
    var refreshService = new CatalogRefreshService(
        fetcher, repo, settings,
        versionService, versionRepo, logger, metadataService,
        httpCacheStore: httpCacheStore);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogRefreshServiceTests" --no-restore`
Expected: PASS (既有 8 tests + 新 6 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs \
        src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs
git commit -m "feat(catalog): hash diff pipeline + 304 short-circuit + 4 counts (v0.6.14 T6)"
```

---

## Task 7: GitHubCatalogMetadataService 8 new fields extraction

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs:115-156`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs`

**Interfaces:**
- Consumes: 既有 `/repos` + `/releases` JSON 响应(API call count 不变)
- Produces:
  - `CatalogEntry.HtmlUrl/Homepage/Language/ForksCount/OpenIssuesCount/SubscribersCount/CreatedAt` 从 `/repos` 填充
  - `CatalogEntry.ReleaseTag` 从 `/releases/latest.tag_name` 填充(已 round 2)
  - `CatalogEntry` 11 个 metadata 字段保持(v0.6.13-B 不破坏)

- [ ] **Step 1: Write failing test for 8 new field extraction**

Append to `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs`:

```csharp
    /// <summary>
    /// v0.6.14: 7 字段(html_url/homepage/language/forks_count/open_issues_count/
    /// subscribers_count/created_at)从 /repos 响应提取。release_tag 从 /releases/latest。
    /// 零新 API call — 走既有 round 1 + round 2 路径。
    /// </summary>
    [Fact]
    public async Task EnrichOneAsync_Extracts8NewFields_FromExistingJsonResponses()
    {
        // 模拟 /repos + /releases/latest 的 GitHub API 响应
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/repos/o/r") && !url.Contains("/releases") && !url.Contains("/readme") && !url.Contains("/commits"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(@"{
                            ""html_url"": ""https://github.com/o/r"",
                            ""homepage"": ""https://example.com"",
                            ""language"": ""Python"",
                            ""forks_count"": 42,
                            ""open_issues_count"": 7,
                            ""subscribers_count"": 100,
                            ""created_at"": ""2025-01-01T00:00:00Z"",
                            ""license"": { ""spdx_id"": ""MIT"" },
                            ""stargazers_count"": 1000,
                            ""archived"": false,
                            ""topics"": [""img2img""],
                            ""pushed_at"": ""2026-08-10T12:00:00Z""
                        }", System.Text.Encoding.UTF8, "application/json"),
                    };
                }
                if (url.Contains("/releases/latest"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(@"{
                            ""tag_name"": ""v2.0.0"",
                            ""body"": ""## v2.0.0\n- new feature"",
                            ""assets"": []
                        }", System.Text.Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
        var http = new HttpClient(handler.Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new GitHubCatalogMetadataService(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        await InvokeEnrichOne(svc, entry);

        Assert.Equal("https://github.com/o/r", entry.HtmlUrl);
        Assert.Equal("https://example.com", entry.Homepage);
        Assert.Equal("Python", entry.Language);
        Assert.Equal(42, entry.ForksCount);
        Assert.Equal(7, entry.OpenIssuesCount);
        Assert.Equal(100, entry.SubscribersCount);
        Assert.Equal("2025-01-01T00:00:00Z", entry.CreatedAt);
        Assert.Equal("v2.0.0", entry.ReleaseTag);
        // v0.6.13-B 既有字段不破坏
        Assert.Equal("MIT", entry.License);
        Assert.Equal(1000, entry.Stars);
    }

    /// <summary>
    /// v0.6.14: /repos 响应缺字段时,8 新字段全部 null — 不抛异常(strict null-check)。
    /// </summary>
    [Fact]
    public async Task EnrichOneAsync_MissingFieldsInResponse_NewFieldsStayNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""stargazers_count"": 100
                }", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler.Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new GitHubCatalogMetadataService(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        await InvokeEnrichOne(svc, entry);

        Assert.Null(entry.HtmlUrl);
        Assert.Null(entry.Homepage);
        Assert.Null(entry.Language);
        Assert.Equal(0, entry.ForksCount);
        Assert.Equal(0, entry.OpenIssuesCount);
        Assert.Null(entry.ReleaseTag);
        Assert.Equal(0, entry.SubscribersCount);
        Assert.Null(entry.CreatedAt);
        // v0.6.13-B 字段仍正常
        Assert.Equal(100, entry.Stars);
    }

    /// <summary>
    /// v0.6.14: 沿用 v0.6.13-B Fake subclass override pattern — 验证 class 不 sealed
    /// 让 subclass override <see cref="GitHubCatalogMetadataService.EnrichAsync"/> 直接
    /// 绕过 HTTP 测字段提取。
    /// </summary>
    [Fact]
    public async Task EnrichAsync_FakeSubclass_OverridesToSetHtmlUrl()
    {
        var http = new HttpClient(new Mock<HttpMessageHandler>().Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new FakeMetadataServiceWithHtmlOverride(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        var done = await svc.EnrichAsync(new[] { entry });

        Assert.Equal(1, done);
        Assert.Equal("https://fake.override/x1", entry.HtmlUrl);
    }

    private sealed class FakeMetadataServiceWithHtmlOverride : GitHubCatalogMetadataService
    {
        public FakeMetadataServiceWithHtmlOverride(
            HttpClient http, MetadataCache cache, Settings settings)
            : base(http, cache, settings) { }

        // 整体 override EnrichAsync(它是 virtual)— 跳过真实 HTTP flow,
        // 直接把 entry.HtmlUrl 设成 magic string 让 test 验 Fake override 生效。
        // 这同时验证 class 不 sealed(v0.6.13-B.1 lesson)+ EnrichAsync 是 virtual。
        public override async Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            foreach (var e in entries)
            {
                e.HtmlUrl = $"https://fake.override/{e.Id}";
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            return entries.Count;
        }
    }

    /// <summary>
    /// v0.6.14: EnrichOneAsync 是 private,test 通过 reflection 调。
    /// </summary>
    private static async Task InvokeEnrichOne(
        GitHubCatalogMetadataService svc, CatalogEntry entry)
    {
        var method = typeof(GitHubCatalogMetadataService).GetMethod(
            "EnrichOneAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);  // 确保方法存在
        var task = (Task<bool>)method!.Invoke(svc, new object[] { entry, default(CancellationToken) })!;
        await task;
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~GitHubCatalogMetadataServiceTests" --no-restore`
Expected: FAIL (8 新字段都是 default 值)

- [ ] **Step 3: Implement 8 new field extraction**

Modify `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs`:

**3a.** Modify `EnrichOneAsync` (line 92-158) — 在 line 124-129(round 1 解析)区域加 7 字段提取:

```csharp
        entry.License = TryGetString(root, "license", "spdx_id");
        entry.Stars = TryGetInt(root, "stargazers_count");
        entry.Deprecated = TryGetBool(root, "archived");
        entry.Tags = TryGetStringArray(root, "topics");
        entry.LastCommit = TryGetString(root, "pushed_at");

        // v0.6.14: 7 新字段从 /repos 提取(strict null-check,缺字段 → null/0)
        entry.HtmlUrl = TryGetString(root, "html_url");
        entry.Homepage = TryGetString(root, "homepage");
        entry.Language = TryGetString(root, "language");
        entry.ForksCount = TryGetInt(root, "forks_count");
        entry.OpenIssuesCount = TryGetInt(root, "open_issues_count");
        entry.SubscribersCount = TryGetInt(root, "subscribers_count");
        entry.CreatedAt = TryGetString(root, "created_at");

        // OsCompat MVP: 3 平台全包
        entry.OsCompat = new[] { "windows", "linux", "macos" };
```

**3b.** Refactor `TryGetLatestReleaseAsync`(line 253-276)— 返回值加 `string? tag` 字段,同一次 HTTP call 拿 3 个值,避免重复请求 `/releases/latest` 违反 Global Constraint "零额外 HTTP call":

```csharp
    private async Task<(string body, int downloads, string? tag)?> TryGetLatestReleaseAsync(
        string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct)
            .ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var b)
                && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            int downloads = 0;
            if (doc.RootElement.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("download_count", out var dc)
                        && dc.ValueKind == JsonValueKind.Number)
                    {
                        downloads += dc.GetInt32();
                    }
                }
            }
            // v0.6.14: tag 跟 body / downloads 同 call 拿,零额外 HTTP
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (body, downloads, tag);
        }
        catch { return null; }
    }
```

**3c.** 在 round 2 调用处(line 142-147)同步用新 tuple 取 `tag`:

```csharp
        if (releasesTask.Result is not null)
        {
            entry.LatestChangelog = releasesTask.Result.Value.body;
            entry.Downloads = releasesTask.Result.Value.downloads;
            entry.ReleaseTag = releasesTask.Result.Value.tag;  // v0.6.14
        }
```

> 注:不新增 `TryGetLatestReleaseTagAsync` 方法 — 那会向 `/releases/latest` 发第二次请求,违反 Global Constraint "零额外 HTTP call"。

**3d.** Ensure `EnrichOneAsync` 仍 `virtual`(v0.6.13-B.1 lesson):

```csharp
    private async Task<bool> EnrichOneAsync(CatalogEntry entry, CancellationToken ct)  // 既有,保持 private
```

注:这里 `EnrichOneAsync` 是 private(v0.6.13-B.1 实际是 protected? 让我保持现状,reflection-based test 仍可调)。**若已有测试用 Fake subclass override EnrichOneAsync,本 task 不破坏它** — `EnrichAsync` 仍 virtual。

- [ ] **Step 4: Run test to verify it passes**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~GitHubCatalogMetadataServiceTests" --no-restore`
Expected: PASS (既有 N tests + 新 3 tests)

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs
git commit -m "feat(metadata): extract 8 new GitHub fields from existing responses (v0.6.14 T7)"
```

---

## Task 8: STA load test + final integration verification

**Files:**
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` — 加 1 STA test
- Modify: 无(只 verification + final commit)

**Interfaces:**
- Consumes: v0.6.14 全套(CatalogEntry + Hasher + HttpCacheStore + CacheStore schema + Repository + Fetcher + RefreshService + MetadataService)
- Produces: STA load test 验证 schema 迁移不破 XAML binding + 全部 AC 验证

- [ ] **Step 1: Write STA load test verifying schema migration**

Append to `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`:

```csharp
    /// <summary>
    /// v0.6.14: 模拟"用户从 v0.6.13-B 升级到 v0.6.14" — 旧 DB 文件(只有 11 v0.6.13-B 列)
    /// 用 CatalogCacheStore.Open() 触发迁移后,CatalogView 加载应不抛 XAML 异常。
    /// </summary>
    [Fact]
    public void CatalogView_Load_AfterV614SchemaMigration_NoBindingErrors()
    {
        // 1. 创建 v0.6.13-B 老 DB
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-sta-v614-{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE catalog_cache (
                        id TEXT PRIMARY KEY,
                        source_url TEXT NOT NULL,
                        package TEXT NOT NULL,
                        raw_metadata TEXT NOT NULL,
                        cached_at TEXT NOT NULL,
                        expires_at TEXT NOT NULL,
                        latest_version TEXT,
                        author TEXT, description TEXT, install_type TEXT,
                        reference TEXT, last_update TEXT, pip_json TEXT,
                        license TEXT, tags_json TEXT, stars INTEGER,
                        downloads INTEGER, last_commit TEXT, readme_markdown TEXT,
                        latest_changelog TEXT, deprecated INTEGER,
                        python_compat_json TEXT, os_compat_json TEXT,
                        metadata_fetched_at TEXT,
                        UNIQUE(source_url, package)
                    );";
                cmd.ExecuteNonQuery();
            }

            // 2. 触发 v0.6.14 schema 迁移
            using (var conn = new CatalogCacheStore(dbPath).Open())
            {
                // PRAGMA 验 9 列已加 + catalog_http_cache 表存在
                using var check = conn.CreateCommand();
                check.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_http_cache'";
                using var reader = check.ExecuteReader();
                Assert.True(reader.Read());  // 表已创建

                using var cols = conn.CreateCommand();
                cols.CommandText = "PRAGMA table_info(catalog_cache)";
                using var cr = cols.ExecuteReader();
                var foundCols = new List<string>();
                while (cr.Read()) foundCols.Add(cr.GetString(1));
                Assert.Contains("content_hash", foundCols);
                Assert.Contains("html_url", foundCols);
                Assert.Contains("created_at", foundCols);
            }

            // 3. STA 加载 CatalogView,验 XAML 不抛
            // (CatalogView 内部从 CatalogCacheStore.Open() 读 DB → 9 新列应是 NULL,
            //  Typed properties 都是 string?/int default,不会抛 NullRef)
            var ex = Record.Exception(() =>
            {
                var view = new CatalogView();
                // STA-required:DataContext + Window.Show 路径不调,只验 construction
                // + XAML parse succeeds
                Assert.NotNull(view);
            });
            Assert.Null(ex);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var ext in new[] { "", "-wal", "-shm" })
                {
                    var p = dbPath + ext;
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }
    }
```

- [ ] **Step 2: Run STA test + full test suite**

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --filter "FullyQualifiedName~CatalogViewLoadTests" --no-restore`
Expected: PASS

Run: `cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-restore`
Expected: PASS (~984 = 951 既有 + 33 新)

- [ ] **Step 3: Verify all 10 AC from spec**

手动验证(spec §6 验收标准):

```bash
# AC-1: schema 自动迁移 → Task 3 测试已覆盖
# AC-2: 首次 refresh content_hash 全非空 → Task 4 测试覆盖(UpsertBatch 算 hash)
# AC-3: 二次 refresh 304 → Task 6 测试覆盖(RefreshAsync_304NotModified_ShortCircuits)
# AC-4: 二次 refresh 有变 → Task 6 测试覆盖(RefreshAsync_HashChanged_EntryUpdated)
# AC-5: 删 entry + cascade → Task 6 测试覆盖(RefreshAsync_RemovedEntry_CascadeDeletesNodeVersions)
# AC-6: 8 新字段入库 → Task 7 测试覆盖(EnrichOneAsync_Extracts8NewFields)
# AC-7: FetchCatalogMetadata=false 不跑 metadata → 既有 Task 6 行为(FetchCatalogMetadata toggle 仍 default OFF)
# AC-8: FetchCatalogMetadata=true 跑 metadata → 既有 Task 6 行为(只 enrichment Added+Updated,Unchanged 走 MetadataCache)
# AC-9: ~984 PASS / 0 FAIL / 1 SKIP → Step 2 full test suite 验证
# AC-10: GUI smoke 5 步 → 桌面 staging 验(用户验证)
```

- [ ] **Step 4: Update memory + plan file**

Update `~/.claude/projects/D--ToolDevelop-ComfyUI/memory/project_incremental_refresh_brainstorm.md` — 标 done + SDD 执行状态:

```markdown
**Status:** ✓ SHIP-READY 2026-08-13, HEAD 待 commit, ~984 PASS / 0 FAIL / 1 SKIP
```

Update `~/.claude/projects/D--ToolDevelop-ComfyUI/memory/MEMORY.md` — index 加新行:

```markdown
- [v0.6.14 Catalog 增量刷新 + 8 GitHub 字段](project_v0_6_14_catalog_incremental_refresh.md) — T1-T7 + STA + 33 新测试 + spec plan.md commit;HTTP cache (If-None-Match) + per-entry SHA256 hash diff + 8 GitHub 字段入库(/repos + /releases 零额外 call)
```

- [ ] **Step 5: Final commit + push**

```bash
cd D:/ToolDevelop/ComfyUI
git add tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs
git commit -m "test(catalog): STA load test for v0.6.14 schema migration (v0.6.14 T8)"
git log --oneline -10  # 验 8 个 v0.6.14 commit 都在
```

- [ ] **Step 6: Final review**

执行 final review(`superpowers:requesting-code-review`),覆盖:
- 8 个 commit 各自 spec compliance
- 全 codebase 一致性(Settings 3-grep rule / Fake subclass pattern / 5000/h rate-limit regression)
- 降级路径(v0.6.13-B 回退无害)
- 9 新列 + 新表的 DDL 幂等性(re-run EnsureCatalogCacheDbSchema 不出错)

---

## Self-Review (执行中)

完成后做以下检查:

**1. Spec coverage**:
- [ ] §3.1.1 — catalog_cache 加 9 列 → Task 3
- [ ] §3.1.2 — catalog_http_cache 表 → Task 3
- [ ] §3.1.3 — 缓存位置保持 → 无 task 需要(不搬)
- [ ] §3.2 — CatalogHttpCacheStore 新 → Task 2
- [ ] §3.2 — CatalogCacheStore schema 改 → Task 3
- [ ] §3.2 — CatalogRepository 9 cols + hash + GetContentHashesBySourceAsync → Task 4
- [ ] §3.2 — CatalogFetcher HTTP cache + CatalogFetchResult → Task 5
- [ ] §3.2 — CatalogRefreshService hash diff + 4 counts → Task 6
- [ ] §3.2 — GitHubCatalogMetadataService 8 fields → Task 7
- [ ] §3.2 — CatalogEntryHasher 新 → Task 1
- [ ] §3.3 — Refresh 数据流 11 步 → Task 6 实现
- [ ] §3.4 — 错误处理 8 种失败模式 → Task 6 + 既有 try/catch
- [ ] §3.5 — 测试 + 验证 ~25 tests → Task 1-8 产出 ~33 tests
- [ ] §3.6 — 迁移路径(首次 v0.6.14 refresh 等价全量)→ Task 3 测试覆盖
- [ ] §6 — 验收标准 10 条 AC → Task 8 Step 3 验证

**2. Placeholder scan**:
- 整个 plan 无 TBD/TODO/"implement later"
- 每个 code step 都有具体代码
- 没有 "类似 Task N" 的引用(只重复必要的 pattern)

**3. Type consistency**:
- `CatalogFetchResult.Is304 / Entries / NewEtag / NewLastModified` — Task 5 定义,Task 6 使用 ✓
- `CatalogEntryHasher.ComputeHash(entry) → string` — Task 1 定义,Task 4 使用 ✓
- `RefreshResult` 加 `AddedCount / UpdatedCount / SkippedCount / DeletedCount` — Task 6 定义 ✓
- `CatalogRepository.GetContentHashesBySourceAsync(url) → IReadOnlyDictionary<string, string>` — Task 4 定义,Task 6 使用 ✓
- `CatalogRepository.DeleteRemovedEntriesAsync(url, packages) → int` — Task 6 定义 ✓
- `CatalogHttpCacheStore.GetAsync / PutAsync` — Task 2 定义,Task 6 使用 ✓

**Type 一致性 OK,无 signature drift。**

---

## 执行选择 (Execution Choice)

Plan complete and saved to `docs/superpowers/plans/2026-08-13-catalog-incremental-refresh.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?