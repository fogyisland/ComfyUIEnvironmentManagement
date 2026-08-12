---
date: 2026-08-12
topic: Catalog DB schema 补全 + GitHub metadata backfill
base_sha: 3ddca87
spec_status: DRAFT
plan_status: PENDING
---

# Catalog DB schema 补全 + GitHub metadata backfill — 设计

## Scope

v0.6.13 三 SDD 中的 **B(数据层)**:

1. **`catalog_cache` 表加 11 列 metadata**(License / Tags / Stars / Downloads / LastCommit / Readme / Changelog / Deprecated / PythonCompat / OsCompat / MetadataFetchedAt)
2. **`GitHubCatalogMetadataService`** 从 GitHub API 拉这些字段 → 写回 CatalogEntry
3. **本地 cache** 24h TTL,避免每次 refresh 都打 GitHub
4. **Settings toggle** `FetchCatalogMetadata` 控制是否启用

A(列表/详情 UI)和 C(安装前 warnings)是另两个 SDD,本 spec 不动 UI。

## 动机

当前 `catalog_cache` 表 + CatalogEntry 只有 `author` / `description` / `install_type` / `reference` / `last_update` / `pip_json` 6 个 typed 列(v0.6.7.4 加)。Catalog 列表/详情面板要展示:

- 列表卡片(tags / stars / downloads / last updated / python compat / deps)
- 详情面板(README / changelog / install count / dep graph / 已装版本对比)

这些字段都得从 GitHub API 拉(many 现存 entry 没有),并且要本地 cache 住避免限流。

## §1 `catalog_cache` 表新增 11 列

```sql
ALTER TABLE catalog_cache ADD COLUMN license TEXT;
ALTER TABLE catalog_cache ADD COLUMN tags_json TEXT;          -- JSON array,["img2img","controlnet",...]
ALTER TABLE catalog_cache ADD COLUMN stars INTEGER;
ALTER TABLE catalog_cache ADD COLUMN downloads INTEGER;       -- sum of releases[].assets[].download_count
ALTER TABLE catalog_cache ADD COLUMN last_commit TEXT;        -- ISO 8601 UTC
ALTER TABLE catalog_cache ADD COLUMN readme_markdown TEXT;    -- /repos/{o}/{r}/readme base64-decoded
ALTER TABLE catalog_cache ADD COLUMN latest_changelog TEXT;   -- /repos/{o}/{r}/releases/latest body
ALTER TABLE catalog_cache ADD COLUMN deprecated INTEGER;      -- 0/1 (= archived)
ALTER TABLE catalog_cache ADD COLUMN python_compat_json TEXT; -- JSON array,["3.10","3.11"]
ALTER TABLE catalog_cache ADD COLUMN os_compat_json TEXT;     -- JSON array,["windows","linux","macos"]
ALTER TABLE catalog_cache ADD COLUMN metadata_fetched_at TEXT;
```

**走 `CatalogCacheStore.EnsureColumn` 增量迁移** — 跟 v0.6.7.4 加 6 列同模式,PRAGMA table_info 检查列存否,不存在就 ALTER TABLE ADD COLUMN。

**新增索引**(如有需要):

```sql
CREATE INDEX IF NOT EXISTS idx_catalog_cache_stars ON catalog_cache(stars DESC);
CREATE INDEX IF NOT EXISTS idx_catalog_cache_downloads ON catalog_cache(downloads DESC);
CREATE INDEX IF NOT EXISTS idx_catalog_cache_deprecated ON catalog_cache(deprecated);
```

3 个索引对应列表卡片排序快捷路径(`ORDER BY stars DESC`, `ORDER BY downloads DESC`, `WHERE deprecated = 0`)。

## §2 `CatalogEntry` 加 11 个 property

`src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs`:

```csharp
// v0.6.13-B: GitHub metadata 抓取后填回的 11 个字段
[JsonPropertyName("license")]
public string? License { get; set; }
[JsonPropertyName("tags")]
public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
[JsonPropertyName("stars")]
public int Stars { get; set; }
[JsonPropertyName("downloads")]
public int Downloads { get; set; }
[JsonPropertyName("last_commit")]
public string? LastCommit { get; set; }   // ISO 8601 UTC
[JsonPropertyName("readme_markdown")]
public string? ReadmeMarkdown { get; set; }
[JsonPropertyName("latest_changelog")]
public string? LatestChangelog { get; set; }
[JsonPropertyName("deprecated")]
public bool Deprecated { get; set; }
[JsonPropertyName("python_compat")]
public IReadOnlyList<string> PythonCompat { get; set; } = Array.Empty<string>();
[JsonPropertyName("os_compat")]
public IReadOnlyList<string> OsCompat { get; set; } = Array.Empty<string>();
[JsonPropertyName("metadata_fetched_at")]
public string? MetadataFetchedAt { get; set; }  // ISO 8601 UTC
```

`Tags` / `PythonCompat` / `OsCompat` 是 `IReadOnlyList<string>`,SQLite 用 `*_json` 列存 JSON array 字符串,跟 `pip_json` 同模式。

`Deprecated` 默认 `false`(0),SQLite INTEGER 存 0/1。

## §3 `CatalogRepository` CRUD 同步更新

`CatalogCacheColumns` 加 11 列:

```csharp
private const string CatalogCacheColumns =
    "id, source_url, package, raw_metadata, cached_at, expires_at, " +
    "latest_version, author, description, install_type, reference, last_update, pip_json, " +
    "license, tags_json, stars, downloads, last_commit, readme_markdown, " +
    "latest_changelog, deprecated, python_compat_json, os_compat_json, metadata_fetched_at";
```

`Read(SqliteDataReader)` 加 11 列读取(column index 13-23):

```csharp
License = reader.IsDBNull(13) ? null : reader.GetString(13),
Tags = ParseStringArray(reader, 14),
Stars = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
Downloads = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
LastCommit = reader.IsDBNull(17) ? null : reader.GetString(17),
ReadmeMarkdown = reader.IsDBNull(18) ? null : reader.GetString(18),
LatestChangelog = reader.IsDBNull(19) ? null : reader.GetString(19),
Deprecated = !reader.IsDBNull(20) && reader.GetInt32(20) != 0,
PythonCompat = ParseStringArray(reader, 21),
OsCompat = ParseStringArray(reader, 22),
MetadataFetchedAt = reader.IsDBNull(23) ? null : reader.GetString(23),
```

`ParseStringArray(SqliteDataReader, int)` helper:

```csharp
private static IReadOnlyList<string> ParseStringArray(SqliteDataReader r, int i)
{
    if (r.IsDBNull(i)) return Array.Empty<string>();
    var json = r.GetString(i);
    if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
    try
    {
        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions)
            ?? new List<string>();
    }
    catch
    {
        return Array.Empty<string>();
    }
}
```

`UpsertCommandText` + `BindUpsertParameters` + `UpsertBatch` 加 11 个 param(`@license`, `@tags_json`, `@stars`, `@downloads`, `@last_commit`, `@readme_markdown`, `@latest_changelog`, `@deprecated`, `@python_compat_json`, `@os_compat_json`, `@metadata_fetched_at`)。

`ON CONFLICT(source_url, package) DO UPDATE SET` 同步加 11 个 `excluded.X`。

## §4 `GitHubCatalogMetadataService` (sealed)

新文件 `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs`:

```csharp
public sealed class GitHubCatalogMetadataService
{
    private readonly HttpClient _http;
    private readonly MetadataCache _cache;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;
    private static readonly SemaphoreSlim ConcurrencyGate = new(5);

    public GitHubCatalogMetadataService(
        HttpClient http,
        MetadataCache cache,
        Settings settings,
        AppLogger? logger = null)
    { _http = http; _cache = cache; _settings = settings; _logger = logger; }

    /// <summary>
    /// v0.6.13-B: 拉每 entry 的 GitHub metadata(License/Tags/Stars/Downloads/
    /// LastCommit/Readme/Changelog/Deprecated/PythonCompat/OsCompat),填回
    /// entry 字段 + 写本地 cache。Skip non-GitHub reference。
    /// </summary>
    public async Task<int> EnrichAsync(
        IList<CatalogEntry> entries,
        IProgress<MetadataFetchProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record MetadataFetchProgress(int Done, int Total, string CurrentPackage);
```

**2 轮轮询策略**(每 entry):

| Round | Endpoint | Fields |
|---|---|---|
| 1 | `GET /repos/{owner}/{repo}` | license.spdx_id, stargazers_count, archived, topics, pushed_at, default_branch |
| 2a | `GET /repos/{owner}/{repo}/readme` | readme(content base64) |
| 2b | `GET /repos/{owner}/{repo}/commits/latest` | sha, commit.author.date |
| 2c | `GET /repos/{owner}/{r}/releases/latest` | body(changelog), assets[].download_count |

Round 2 三个 endpoint 并发(用 Task.WhenAll),失败 fail-soft 跳过单个。

**失败/降级策略**:

| 情况 | 处理 |
|---|---|
| HTTP 404(repo 删了/private) | 整 entry 跳过,Info log |
| HTTP 403 + `X-RateLimit-Remaining: 0` | **立即 stop** 整个 batch,Info log warning("rate limit hit,resume on next refresh"),throw `RateLimitException` 让上层 RefreshAsync 捕获并 Info log |
| HTTP 5xx(503/502/504) | 指数退避重试 3 次(1s, 2s, 4s),仍 fail → skip entry,Info log |
| readme/release/commits 404(可能没 release) | 字段留空(null),不 fail |
| JSON 解析失败 | skip entry,Info log "JSON parse error" |
| Token 空(未鉴权) | GitHub 限流 60/h,但 24h TTL cache 意味着每天最多 1 batch,实际不会触发 |
| Token 非空(鉴权) | 5000/h,本 SDD 1000+ entry batch + 4 endpoint = 4000+ request,可能接近 limit → 用 concurrency 5 + round 2 并发 |

**并发 5** via `ConcurrencyGate.WaitAsync(ct)` + `Release()`。每 entry 串行进入 gate,但 5 个 entry 同时跑。

**PythonCompat / OsCompat 探测**(Round 2b commits + Round 1 default_branch 之外):

- `PythonCompat`:从 `/repos/{o}/{r}/contents/setup.py` 或 `pyproject.toml` 文件存在性 + GitHub Action workflow 文件名(`python-3.10.yml` 等)推断。**MVP 简化**:直接读 `setup.py` 的 `python_requires` 字段,JSON 解析失败 → 留空数组(`[]` 表示未知)
- `OsCompat`:从 `.github/workflows/*.yml` 文件名探测(`windows-latest` / `ubuntu-latest` / `macos-latest`)→ 数组。MVP 简化:**始终返回 `["windows", "linux", "macos"]`**(无脑全部兼容),后续 SDD 真做解析

**MVP 简化范围**(G9 YAGNI):

- PythonCompat 仅 best-effort 解析,失败空数组
- OsCompat **始终 `["windows", "linux", "macos"]`**(绝大多数 ComfyUI 节点 3 平台都跑)
- 不解析 workflow 文件名(等 v0.6.13.x 真需要再补)

## §5 本地 cache `MetadataCache` (sealed)

新文件 `src-wpf/ComfyUI.Manager/Services/MetadataCache.cs`:

```csharp
public sealed class MetadataCache
{
    public string FilePath { get; }  // %APPDATA%/ComfyUI-Manager/catalog_metadata_cache.json

    public MetadataCache() : this(DefaultPath()) { }
    public MetadataCache(string filePath) { FilePath = filePath; }

    public Task<CachedMetadata?> TryGetAsync(string repoKey, CancellationToken ct = default);
    public Task SaveAsync(string repoKey, CachedMetadata data, CancellationToken ct = default);
}

public sealed record CachedMetadata(
    string? License, IReadOnlyList<string> Tags, int Stars, int Downloads,
    string? LastCommit, string? ReadmeMarkdown, string? LatestChangelog,
    bool Deprecated, IReadOnlyList<string> PythonCompat, IReadOnlyList<string> OsCompat,
    DateTime FetchedAt);
```

**缓存文件路径**:`%APPDATA%/ComfyUI-Manager/catalog_metadata_cache.json`(跟 project 惯例 `Settings.json` 同目录)

**24h TTL**:`FetchedAt + 24h < DateTime.UtcNow` → 视作 stale,call `EnrichAsync` 时强制重拉(stale entry 被 service 跳过 cache 命中,直接走 round 1 + round 2)

**文件格式**(v1 schema,JSON):

```json
{
  "version": 1,
  "entries": {
    "ltdrdata/comfyui-impact-pack": {
      "license": "GPL-3.0",
      "tags": ["impact", "detector", "sam"],
      "stars": 1234,
      "downloads": 56789,
      "last_commit": "2026-08-10T12:34:56Z",
      "readme_markdown": "# ComfyUI Impact Pack\n\n...",
      "latest_changelog": "## v1.2.3\n...",
      "deprecated": false,
      "python_compat": ["3.10", "3.11"],
      "os_compat": ["windows", "linux", "macos"],
      "fetched_at": "2026-08-12T03:00:00Z"
    }
  }
}
```

**Atomic write**:写 `temp.json` → `File.Move(temp, FilePath, overwrite: true)`(Windows `File.Move` overwrite 在 .NET 5+ 支持)。

**Migration 路径**:文件不存在 or `version != 1` → 当 v1 创建(空 entries)。

## §6 Settings 字段 + CopyInto

`Models/Settings.cs` 加:

```csharp
/// <summary>
/// v0.6.13-B: 开关 gate 控制 refresh 时是否拉 GitHub metadata(License/Tags/
/// Stars/Downloads/LastCommit/Readme/Changelog/Deprecated)。默认 false 保持
/// 向后兼容(跟 v0.6.11 T3 FetchNodeVersionsOnRefresh 同 pattern,避免没配
/// token 的用户被限流 60/h)。
/// </summary>
[JsonPropertyName("fetch_catalog_metadata")]
public bool FetchCatalogMetadata { get; set; }
```

`CopyInto` + `SettingsDefaults.Apply` 同步加(`FetchCatalogMetadata = false` 兜底)。

## §7 `CatalogRefreshService.RefreshAsync` 接 metadata service

第 2 步(已有 GitHub version 拉取)后面加第 3 步:

```csharp
// 第三步: 拉 GitHub metadata(License/Tags/Stars/...)(开关 gate 开启时)
if (_metadataService is not null && _settings.FetchCatalogMetadata)
{
    var metaProgress = new Progress<MetadataFetchProgress>(p =>
        metadataProgress?.Report(p));
    var metaCount = await _metadataService.EnrichAsync(entries, metaProgress, ct);
    _logger?.Info("catalog-refresh",
        $"metadata enrich count={metaCount} duration_ms={sw.ElapsedMilliseconds}");
    if (metaCount > 0)
    {
        // 写回 SQLite(11 个新列)
        await Task.Run(() => _repo.UpsertBatch(entries), ct);
    }
}
```

ctor 加可选 `GitHubCatalogMetadataService? metadataService = null` 参数,跟 `_versionService` 同 pattern(不破坏既有测试)。

## §8 Progress + UI 不做

`MetadataFetchProgress` 暴露给上层 (`CatalogViewModel` 的 Refresh command),但本 SDD **不做** UI 进度条(A SDD 才做,见 `2026-08-13-catalog-ui-fields-design.md` 后续 spec)。Refresh command 拿到的 progress 暂时 `_logger?.Info` 落到 Logs/operation-catalog-refresh.log 即可。

UI 不动(列表卡片 / 详情面板都用空 / null 显示 `"未知"` placeholder)。

## §9 依赖注入 (App.xaml.cs)

```csharp
var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ComfyUI-Manager/0.6.13");
var metaCache = new MetadataCache();
var metaService = new GitHubCatalogMetadataService(httpClient, metaCache, settings, logger);

var catalogRefreshService = new CatalogRefreshService(
    fetcher, repo, settings,
    versionService, versionRepo,
    metaService,  // v0.6.13-B
    logger);
```

HttpClient 复用(单实例),跟既有 `GitHubVersionService` 同 pattern(它已经在 ctor 里 new HttpClient,本 SDD 抽到 DI 顶层,既不破坏既有 path 也避免重复 socket)。

## §10 Tests (17 target)

### Unit tests

`tests-wpf/ComfyUI.Manager.Tests/Services/MetadataCacheTests.cs` (新建,4 tests):

- `MetadataCache_FreshEntry_ReturnsCachedData` — 写 1 entry,FetchedAt=now,TryGet 返回数据
- `MetadataCache_StaleEntry_ReturnsNull` — FetchedAt=now-25h,TryGet 返回 null(stale)
- `MetadataCache_MissingFile_ReturnsNull` — 文件不存在,TryGet 返回 null
- `MetadataCache_AtomicWrite_NoPartialFile` — 模拟中断(写一半),验证 filePath 没残留 temp

`tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs` (新建,7 tests):

- `EnrichAsync_GitHubRef_FetchesAllFields` — FakeHttpMessageHandler mock round 1 + round 2,验 entry 11 字段填回 + cache 写盘
- `EnrichAsync_NonGithubRef_Skipped` — reference = `"https://gitlab.com/foo/bar"`,entry 不动 + 0 HTTP call
- `EnrichAsync_RateLimit_ThrowsAndStops` — 403 + `X-RateLimit-Remaining: 0`,验 throw + 后面的 entry 不打
- `EnrichAsync_RetryOn503` — 第 1 次 503,第 2 次 200,验 entry 填回 + 总 HTTP call = 2
- `EnrichAsync_ReadmeNotFound_LeavesFieldNull` — round 2a 404,ReadmeMarkdown=null 但其他字段填
- `EnrichAsync_TagsFlattened` — topics 数组 ["img2img","controlnet"] → Tags = ["img2img","controlnet"]
- `EnrichAsync_DownloadsSummed` — releases.latest assets 有 3 个,download_count 累加

`tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceMetadataTests.cs` (新建,3 tests):

- `RefreshAsync_MetadataDisabled_DoesNotCallMetadataService` — `FetchCatalogMetadata=false`,metadataService 不被 invoke
- `RefreshAsync_MetadataEnabled_EnrichesAndUpdatesRepo` — 1 entry + 假 GitHub,验证 `repo.UpsertBatch` 被调且 entry 含新字段
- `RefreshAsync_MetadataThrows_DoesNotFailWholeRefresh` — metadataService throw RateLimitException,RefreshResult.Ok 仍返回(只 metadataCount=0)

### STA load tests

`tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` (既有,加 1 test):

- `CatalogView_AllNewColumnsPresent_RendersWithoutException` — mock 2 entries 全字段填上 + 1 entry 全字段 null,view Load 不抛

### Schema migration test

`tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreMetadataMigrationTests.cs` (新建,2 tests):

- `CatalogCacheStore_OldSchema_AddsNewColumns` — 用旧 schema 创建 db(无新列),Open() 触发 ALTER TABLE,验证 PRAGMA table_info 有 11 个新列
- `CatalogCacheStore_NewSchema_NoOp` — 新 schema 已包含所有列,Open() 不再 ALTER

**总计**:4 + 7 + 3 + 1 + 2 = **17 tests**

## §11 改动文件

- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` — 11 个 property
- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — `FetchCatalogMetadata` 字段
- `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` — 11 个 `EnsureColumn` + 3 索引
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` — `CatalogCacheColumns` + `Read` + `UpsertCommandText` + `BindUpsertParameters` + `UpsertBatch` 同步加 11 列
- `src-wpf/ComfyUI.Manager/Services/MetadataCache.cs` — 新建
- `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs` — 新建
- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` — ctor + RefreshAsync 第 3 步
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — DI 接线(HttpClient / MetadataCache / GitHubCatalogMetadataService)
- `tests-wpf/ComfyUI.Manager.Tests/Services/MetadataCacheTests.cs` — 新建
- `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubCatalogMetadataServiceTests.cs` — 新建
- `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceMetadataTests.cs` — 新建
- `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreMetadataMigrationTests.cs` — 新建
- `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` — +1 STA test

## §12 风险

| 风险 | 缓解 |
|---|---|
| 1000+ entry × 4 endpoint = 4000+ GitHub request 接近 5000/h 限流 | concurrency 5 + 24h TTL cache + 用户可选关闭(FetchCatalogMetadata) |
| 首次 refresh metadata 用户等待 5-10 分钟 | 加 INFO log + 用户可中断(下次 refresh 续) |
| GitHub API 改动字段类型 | defensive parsing(每个字段 try/catch → null) |
| MetadataCache 文件损坏 | version=1 + try/catch 读 → 失败当空 cache |
| 同时跑 refresh + 用户编辑节点 | CatalogEntry 字段是 in-memory 改,UpsertBatch 一次性写,SQLite 单 connection 不冲突 |
| 老 cache db 没新列 → 启动崩溃 | EnsureColumn 增量迁移,新加 11 列,旧 db 自动升级 |
| 测试网络依赖 | FakeHttpMessageHandler 全 mock,不真打 GitHub |
| Tests 增加 17 个跑 5-10s | 现有 924 测试基线 6 min,新加 17 个 ~30s 增量可接受 |

## §13 YAGNI 划线 (G9)

- 不做 UI 字段展示(A SDD 范围)
- 不做安装前 warnings(C SDD 范围)
- 不做 dep graph 解析(C SDD 真需要时做)
- 不做 OsCompat 真解析 workflow 文件名(MVP 默认 3 平台全包)
- 不做 PythonCompat 真解析 setup.py(MVP 留空数组 + Info log 失败)
- 不做 metadata diff(对比上次 fetched_at,只看 changed)
- 不做 telemetry(用户用了多少 cache hit/miss)
- 不做 batched write partial progress(只 Progress<MetadataFetchProgress> 整体)

## §14 Carry-forward (不阻塞,下轮可做)

- PythonCompat 真解析(`setup_requires` / `pyproject.toml [project].requires-python`)
- OsCompat 从 `.github/workflows/*.yml` 探测(去掉 MVP 三平台兜底)
- MetadataCache 加 stats(hit/miss count)给 Settings 面板
- Install 前 warnings 接 PythonCompat / OsCompat(C SDD)
- Dep graph from `requirements.txt` / `pyproject.toml`(C SDD)

## §15 验证

```bash
# 1. Schema migration(老 db 启动 + 新 db 启动)
# 2. 单元测试
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MetadataCache|FullyQualifiedName~GitHubCatalogMetadataService|FullyQualifiedName~CatalogRefreshServiceMetadata|FullyQualifiedName~CatalogCacheStoreMetadata" -v minimal   # 16 PASS
# 3. STA load test
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal   # 现有 +1 = N PASS
# 4. 全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 924 + 17 = 941 PASS (1 pre-existing FAIL flake)
# 5. Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
# 6. GUI smoke (用户在 staging 上跑)
# 启动 → Settings 勾"刷新时拉取节点 metadata" → Catalog → Refresh → Logs 看到 "[catalog-metadata] ..." 行
# 取消勾 → Refresh → Logs 看不到 metadata INFO 行
# 第二次 refresh → Logs 看到 "[catalog-metadata] cache hit X" 快很多
# 详情面板/列表卡片仍显示空(UI A SDD 才接,本 SDD 数据层已 ready)
```

## §16 跟 v0.6.7.4 / v0.6.13-A / v0.6.13-C 的边界

- **v0.6.7.4**(已 SHIP `5595383`):ExtractReference 3-key + list card `latest:` 行 + 6 typed 列。本 SDD 在它基础上加 11 列 metadata。
- **v0.6.13-A**(后续 SDD,UI):列表卡片 badges(tags/stars/downloads/last_updated/python_compat) + 详情面板 metadata group + README group + changelog group。**本 SDD 数据就位后 A 只需接 XAML binding,不动 schema。**
- **v0.6.13-C**(后续 SDD,Warn):安装前 dialog 用 PythonCompat / OsCompat / Deprecated 字段。**本 SDD 数据就位后 C 只需接 dialog 逻辑,不动 schema。**

3 SDD **并行独立**,但有数据依赖 → A + C 等 B 数据就位才能完整测。B 独立 ship 后 A + C 各自独立 ship。
