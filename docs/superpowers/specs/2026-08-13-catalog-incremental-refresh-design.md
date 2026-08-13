# v0.6.14 Spec: Catalog 节点目录增量刷新 + 8 个 GitHub 关键字段入库

> **For agentic workers:** This is a design spec. Read once before writing the implementation plan; do not modify without user approval.

## 1. Goal

解决 v0.6.13-B 之后 Catalog 刷新仍然慢 + GitHub 字段不全的 2 个用户痛点:

1. **每次 refresh 都全量重写**:即使 catalog JSON 没变,5873 rows 全部 upsert 一遍 + 全部 GitHub 元数据 enrichment 重跑(`Settings.FetchCatalogMetadata=true` 时 ~9 分钟 5000/h 限流)。需要**增量刷新**:HTTP 304 跳过 JSON 解析;per-entry SHA256 hash diff 只 upsert 变了 hash 的条目;metadata enrichment 也跳过 hash 未变的条目。
2. **GitHub 关键字段缺失**:v0.6.13-B 入库了 11 个 GitHub metadata 列(stars/license/tags/downloads/last_commit/readme/changelog/deprecated/python_compat/os_compat/metadata_fetched_at),但还缺 8 个用户关心的:**html_url / homepage / language / forks_count / open_issues_count / release_tag / subscribers_count / created_at**。需要补齐,让 UI 后续能展示。

**用户原话**:
> "在节点目录中本地缓存上次的数据,点击刷新之后实现增量刷新,另外拉取其他的关键字段"
>
> "这个数据要入库的"

**Non-goals(本 spec 不做)**:
- 不实现 UI 上展示这 8 个新字段(列表项布局/详情面板排版留到 v0.6.15 SDD)
- 不实现"已删 entry 历史保留"(软删 `IsRemoved=1` 留 v0.6.15)
- 不实现 UI Toast/banner 显示 `+A ~U ⟳S -M`(只在 AppLogger log 输出)
- 不 bump version / 不发 release zip(per hotfix 偏好:本地 commit + 重建 staging)
- 不改 metadata 提取的 API call 数量(8 字段都从已有 `/repos` + `/releases` 响应里提取,**零额外 HTTP 调用**)
- 不实现"force full refresh" toggle(用户可手动删 `<staging>/data/catalog-cache.db` 强制全量)

## 2. Background

### 2.1 v0.6.13-B 现状(可复用基础)

- **`catalog_cache` 表**(24 列,5873 rows):`CatalogCacheStore.EnsureCatalogCacheDbSchema` 跑 `EnsureColumn` 加 11 metadata 列(`license / tags / stars / downloads / last_commit / readme_markdown / latest_changelog / deprecated / python_compat / os_compat / metadata_fetched_at`),沿用 v0.6.11 起既有的 `(source_url, package)` UNIQUE index。
- **`CatalogRepository.UpsertBatch`**:`INSERT ON CONFLICT(source_url, package) DO UPDATE` 一次写入一批,`BindUpsertParameters` 绑定 24 个参数。返回 INSERT count 给 `CatalogRefreshService.EntryCount`。
- **`CatalogFetcher.FetchAsync(url, ct)`**:无 HTTP cache 支持,每次都全量 GET,parse,反序列化 5873 entries 到 `List<CatalogEntry>`。每个 entry 的 `Id = Guid.NewGuid()`(DB-generated PK,每次 refresh 变),但 `(source_url, package)` UNIQUE 保证 dedup。
- **`CatalogRefreshService.RefreshAsync`**:3 步流水线 — ① 拉取 JSON 全量 upsert ② 拉版本(`FetchNodeVersionsOnRefresh`)③ enrichment metadata(`FetchCatalogMetadata`)。每个 step 独立,异常隔离(v0.6.13-B.2 hotfix 加 rate-limit fail-soft)。
- **`GitHubCatalogMetadataService.EnrichOneAsync`**:round 1 = 1 个 `/repos/{o}/{r}` 调用;round 2 = `/releases` + `/commits` 2 concurrent `Task.WhenAll`。`EnrichAsync` **不 `sealed`** + `EnrichOneAsync` **virtual**(v0.6.13-B.1 lesson 让 Fake subclass override 可测)。
- **`MetadataCache`**(`%APPDATA%/ComfyUI-Manager/catalog_metadata_cache.json`):24h TTL 本地 cache,`MetadataFetchedAt` 列复用做 server-side 过滤。
- **`Settings.FetchCatalogMetadata`**:default `false`,已 wired 到 `SettingsView.xaml` CheckBox + dirty-⚠(v0.6.13-B.1 hotfix)。

### 2.2 用户预期

- **增量刷新"快"**:第一次 refresh 拉全量(等价 v0.6.13-B,~7 秒 fetch + ~2 秒 upsert 5873 rows + 用户开 `FetchCatalogMetadata` 时 ~9 分钟 metadata)。第二次 refresh 如果 catalog JSON 没变 → **< 3 秒**(304 + 1 SQL `SELECT COUNT(*)` + 0 upsert + 0 metadata)。如果有 N 条变更 → 只 upsert N 条 + 只 enrichment N 条(estimated ~2ms × N rows upsert + metadata enrichment 按 N 比例缩放)。
- **GitHub 字段全**:DB 里 19 列 metadata(11 旧 + 8 新),后续 UI 按需展示。当前 spec 不动 UI。
- **数据安全**:失败重试可恢复(etag 保留),不会"半新半旧"。

## 3. Design

### 3.1 Schema 变更

#### 3.1.1 `catalog_cache` 加 9 列(沿用 `EnsureColumn` 模式)

`CatalogCacheStore.EnsureCatalogCacheDbSchema` 末尾追加 9 次 `EnsureColumn` 调用:

```csharp
EnsureColumn(conn, "catalog_cache", "content_hash",   "TEXT NOT NULL DEFAULT ''");
EnsureColumn(conn, "catalog_cache", "html_url",        "TEXT");
EnsureColumn(conn, "catalog_cache", "homepage",        "TEXT");
EnsureColumn(conn, "catalog_cache", "language",        "TEXT");
EnsureColumn(conn, "catalog_cache", "forks_count",     "INTEGER");
EnsureColumn(conn, "catalog_cache", "open_issues_count","INTEGER");
EnsureColumn(conn, "catalog_cache", "release_tag",     "TEXT");
EnsureColumn(conn, "catalog_cache", "subscribers_count","INTEGER");
EnsureColumn(conn, "catalog_cache", "created_at",      "TEXT");
```

| 列 | 类型 | 来源 | 入 hash? |
|----|------|------|----------|
| `content_hash` | TEXT NOT NULL DEFAULT '' | 派生 SHA256 | - |
| `html_url` | TEXT | `/repos.{html_url}` | 否 |
| `homepage` | TEXT | `/repos.homepage` | 否 |
| `language` | TEXT | `/repos.language` | 否 |
| `forks_count` | INTEGER | `/repos.forks_count` | 否 |
| `open_issues_count` | INTEGER | `/repos.open_issues_count` | 否 |
| `release_tag` | TEXT | `/releases[0].tag_name` | 否 |
| `subscribers_count` | INTEGER | `/repos.subscribers_count` | 否 |
| `created_at` | TEXT | `/repos.created_at` | 否 |

**`content_hash` 计算范围**(v0.6.14 关键约束):
catalog JSON 的"内容字段"(canonical JSON,SHA256 hex)。`CatalogEntryHasher.ComputeHash(entry)` 输入字典(按 key 字母序排序):

```csharp
var canonical = new SortedDictionary<string, object?>
{
    ["id"] = entry.RawMetadata.GetValueOrDefault("id"),
    ["name"] = entry.Package,
    ["author"] = entry.RawMetadata.GetValueOrDefault("author"),
    ["title"] = entry.RawMetadata.GetValueOrDefault("title"),
    ["description"] = entry.RawMetadata.GetValueOrDefault("description"),
    ["category"] = entry.RawMetadata.GetValueOrDefault("category"),
    ["reference"] = entry.RawMetadata.GetValueOrDefault("reference"),
    ["tags"] = entry.RawMetadata.GetValueOrDefault("tags"),
    ["install_type"] = entry.RawMetadata.GetValueOrDefault("install_type"),
};
return Convert.ToHexString(SHA256.HashData(
    JsonSerializer.SerializeToUtf8Bytes(canonical,
        new JsonSerializerOptions { WriteIndented = false })));
```

**不包含**在 hash 内:
- DB-generated `Id`(GUID,每次 refresh 变)
- 19 个 metadata 列(11 v0.6.13-B + 8 新 — 这 19 列任何一列变都不应触发 row 重写)
- 时间戳(`CachedAt` / `ExpiresAt` / `MetadataFetchedAt`)
- `raw_metadata` 里其他未列字段(`apt_dependency` / `badges` / `files` / `js_path` / `last_update` / `nickname` / `nodename_pattern` / `pip` / `preemptions` / `reference2` / `version`)— 这些是 metadata 范畴或衍生数据,改变不算"entry 内容变";若发现某些应纳入,本 spec 之外另起迭代

**为什么 metadata 不入 hash**:metadata 刷新(stars 涨了、license 改了)应该 enrichment 但不应触发 row upsert,否则死循环(hash 变 → upsert → metadata enrichment → hash 变)。

#### 3.1.2 新表 `catalog_http_cache`

同 DB 内(不开新文件),在 `EnsureCatalogCacheDbSchema` 末尾加:

```sql
CREATE TABLE IF NOT EXISTS catalog_http_cache (
    url            TEXT PRIMARY KEY,
    etag           TEXT,
    last_modified  TEXT,
    fetched_at     TEXT NOT NULL
);
```

**索引**:无新索引。Hash diff 查询走既有 `(source_url, package)` UNIQUE index(v0.6.11 起)。Removed-entry 检测全表 scan(5883 rows 毫秒级,可接受)。

#### 3.1.3 缓存位置 — 保持 `<AppBaseDir>/data/catalog-cache.db`

不搬 `%APPDATA%/ComfyUI-Manager/`(YAGNI):当前已是 `<AppBaseDir>/data/`,跟 v0.6.7.5 节点安装 diff 的 DB 同位置;搬要写一次性迁移代码,获益是"升级跨用户账号更安全",但 v0.6.14 不解决跨升级问题。如果未来要搬,加 v0.6.15 一条 `IF NOT EXISTS schema_v2` 渐进迁移。

### 3.2 架构(组件 + 职责)

| 组件 | 类型 | 职责 |
|------|------|------|
| `Data/CatalogHttpCacheStore.cs` | **新** (~60 行) | 单表 `catalog_http_cache` 存/取 ETag/Last-Modified per URL |
| `Data/CatalogCacheStore.cs` | 修改 | `EnsureCatalogCacheDbSchema` 加 9 列 + 新表 DDL |
| `Data/CatalogRepository.cs` | 修改 | `UpsertCommandText` + `BindUpsertParameters` 加 9 列;`UpsertBatch` 每 entry 算 hash;新增 `GetContentHashesBySourceAsync(url)` |
| `Services/CatalogFetcher.cs` | 修改 | 签名 `FetchAsync(url, etag?, lastModified?, ct)` 返回 `CatalogFetchResult { Is304, Entries?, NewETag, NewLastModified }`;304 时 `Entries=null NewETag=null NewLastModified=null` |
| `Services/CatalogRefreshService.cs` | 修改 | 新增 step 2.5 "hash diff + selective upsert";`CatalogRefreshResult` 加 `AddedCount/UpdatedCount/SkippedCount/DeletedCount` 4 字段 |
| `Services/GitHubCatalogMetadataService.cs` | 修改 | `EnrichOneAsync` 从已有 /repos 响应多解析 7 字段;round 2 已有 /releases 多解析 `tag_name` → `release_tag`(**零额外 HTTP call**) |
| `Data/CatalogEntryHasher.cs` | **新** (~40 行) | `ComputeHash(CatalogEntry) → string` SHA256 of canonical JSON |
| `MetadataCache` | **不变** | 24h TTL,hash 不变不调 metadata 自动 skip |
| `Settings.FetchCatalogMetadata` | **不变** | 已 wired default OFF |
| `SettingsView.xaml` | **不变** | 无新 Settings 字段 → 3-grep rule 不触发 |

**关键约束(从 v0.6.13-B lessons):**
- `GitHubCatalogMetadataService` 不 `sealed` + `EnrichOneAsync` 保持 `virtual`(v0.6.13-B.1 lesson)—让 Fake subclass override 可测
- 19 cols 提取不改 API call count(round 1=1 /repos,round 2=2 /releases+/commits),5000/h 限流触发时机需回归测试(v0.6.13-B.2 lesson)
- `EnsureColumn` 迁移走 9 次 ALTER TABLE,SQlite NOT NULL DEFAULT '' 走默认值回填,不动老数据

### 3.3 Refresh 数据流

```
[User clicks Refresh]
        ↓
CatalogRefreshService.RefreshAsync()
        ↓
1. Get active source URL from Settings
        ↓
2. CatalogHttpCacheStore.GetAsync(url) → (etag?, lastModified?)
        ↓
3. CatalogFetcher.FetchAsync(url, etag, lastModified, ct) → CatalogFetchResult
        ↓
   ┌─────────────────────────────────────────────────────────────────┐
   │ Is304 → FULL short-circuit                                        │
   │   Log "[catalog-refresh] no changes (304) duration_ms=X"        │
   │   SELECT count(*) FROM catalog_cache WHERE source_url = ? → unchanged │
   │   Return Success=true, EntryCount=0,                             │
   │          SkippedCount=<unchanged>, AddedCount=0,                 │
   │          UpdatedCount=0, DeletedCount=0,                         │
   │          VersionCount=0, MetadataCount=0                         │
   │   → 跳过 step 4-9 + step 12-13 (version/metadata refresh)        │
   └─────────────────────────────────────────────────────────────────┘
        ↓ (Is200)
4. CatalogHttpCacheStore.PutAsync(url, newETag, newLastModified)
        ↓
5. Per entry: CatalogEntryHasher.ComputeHash(entry) → hash string
        ↓
6. CatalogRepository.GetContentHashesBySourceAsync(url)
   → dict<package, content_hash> existingHashes
        ↓
7. Classify entries (in JSON vs existingHashes):
   - Added (jsonPackage ∉ existingHashes)
   - Updated (jsonPackage ∈ existingHashes, hash differs)
   - Unchanged (jsonPackage ∈ existingHashes, hash matches)
        ↓
8. Upsert Added + Updated rows (with new content_hash) via CatalogRepository.UpsertBatch
        ↓
9. Detect removed: existingHashes.Keys ∖ jsonPackages
   → hard DELETE FROM catalog_cache WHERE source_url = ? AND package IN (removed)
   → hard DELETE FROM node_versions WHERE node_id IN (removed node ids)  -- cascade
        ↓
10. Log "[catalog-refresh] done: +{added} ~{updated} ⟳{unchanged} -{removed} duration_ms=X"
        ↓
11. Build partial CatalogRefreshResult { AddedCount, UpdatedCount, SkippedCount, DeletedCount }
        ↓
12. (existing step 2) VersionService.FetchVersionsAsync — gated by Settings.FetchNodeVersionsOnRefresh
        ↓
13. (existing step 3) GitHubCatalogMetadataService.EnrichAsync — gated by Settings.FetchCatalogMetadata;
    MetadataCache 24h TTL 决定是否真调 GitHub
        ↓
[Return CatalogRefreshResult with all counts]
```

**关键设计点:**
- **Hash diff + metadata enrichment 正交**:hash 决定是否写 row,MetadataCache 24h TTL 决定是否刷 metadata。"Unchanged hash" 不跳过 metadata(stars/license/latest version 独立于 catalog JSON 变化),**但 304 路径两者都跳过** — 304 表示 catalog 完全没动,metadata 也没理由动
- **304 跳过一切**:version fetch + metadata enrichment 都跳。下次 catalog 真变化才补(v0.6.13-B.2 已有 24h TTL 兜底)。后续 v0.6.15 可加 "force version refresh" toggle(YAGNI for v0.6.14)
- **Removed cascade**:`catalog_cache` + `node_versions` 双删(node_versions 是1:N 关系,孤儿行必须清)
- **304 路径仍返回 SkippedCount**:让 UI / 上层能感知"上次有多少 entries 没变" — 即使有 5000 rows 没变,SkippedCount=5000 也是有效信号

### 3.4 错误处理

| 失败模式 | v0.6.14 行为 |
|----------|--------------|
| Network 5xx / DNS / timeout | top-level catch → `Success=false, Error="拉取失败: …"`;`CatalogHttpCacheStore` 不更新(etag 保留供下次重试) |
| Malformed JSON | `CatalogFetcher` throws → top-level catch → `Success=false` |
| DB write fail(constraint / lock / disk full) | `UpsertBatch` throws → top-level catch → `Success=false`;已 commit 的 batch 保留 |
| GitHub rate limit(403 + X-RateLimit-Remaining=0) | `RateLimitException` → step 3 catch → Warn log + versionCount=0 → Success=true(v0.6.13-B.2 lesson)— hash diff 在 step 2.5 之前,不受影响 |
| `catalog_http_cache` row 损坏 | `CatalogHttpCacheStore.GetAsync` 异常 → log Warn + 返回 `(null, null)` → 无 etag 发送 → 回退全量 fetch |
| catalog JSON 含重复 `(source_url, package)` | catch `SqliteException`(UNIQUE 约束违反)+ log Warn + skip 重复 entry(首个保留) |
| Concurrent refresh(用户连点 2 次) | CancellationToken pattern 已有 — 第二 click 触发 cancel |
| Hash 计算极端值(null fields / serializer 异常) | `JsonSerializer.Serialize(canonical)` 容忍 null;万一抛 → top-level catch → Success=false |

**关键不变量:**
- 任何失败 → `Success=false` + `Error` 字段
- `catalog_http_cache` **只在成功 200 + 解析成功后才更新** — 失败时不污染 cache
- 失败后 DB 处于"上次成功"状态 — 不半新半旧
- 失败重试可恢复:etag 还在,下次走 304 路径

**日志系统集成(v0.6.5.13 AppLogger 已接入):**
- 失败 catch → `AppLogger.Warn("catalog-refresh", "<reason>")`
- 成功 → `AppLogger.Info("catalog-refresh", "done: +A ~U ⟳S -D duration_ms=X")`
- HTTP cache 损坏 → `AppLogger.Warn("catalog-http-cache", "<reason>")`
- 304 short-circuit → `AppLogger.Info("catalog-refresh", "no changes (304) duration_ms=X")`

### 3.5 测试 + 验证

**新增测试 (~25,baseline 951 → 期望 ~976 PASS / 0 FAIL / 1 SKIP):**

| Test 类 | 数量 | 关键覆盖 |
|---------|------|----------|
| `CatalogHttpCacheStoreTests`(新文件) | 6 | Put/Get round-trip, 不存在 URL 返回 null, overwrite, null etag, row 损坏回退, EnsureTable |
| `CatalogEntryHasherTests`(新文件) | 4 | 同内容同 hash, 不同内容不同 hash, **metadata 列不影响 hash**(防死循环), key 顺序不影响 hash |
| `CatalogFetcherTests`(扩展) | 3 | 无 etag 不发 If-None-Match, 有 etag 发 header, **304 vs 200 返回** |
| `CatalogRepositoryTests`(扩展) | 3 | UpsertBatch 算 hash, 同内容 idempotent, `GetContentHashesBySourceAsync` 返回 dict |
| `CatalogRefreshServiceTests`(扩展) | 6 | **304 全 skip**, 旧 DB 首次全 added, **hash 不变 skipped**, hash 变 updated, 新 entry added, **删 entry + node_versions cascade** |
| `GitHubCatalogMetadataServiceTests`(扩展) | 3 | 8 字段从已有 JSON 提取(**零新 API call**), Fake subclass override, null-safe(响应可能 missing fields) |
| `CatalogViewLoadTests`(扩展,STA) | 1 | v0.6.14 schema 迁移后 CatalogView 加载无 XAML 异常 |

**测试设施:**
- `FakeCatalogFetcher` 升级支持 `EtagToReturn` / `LastModifiedToReturn` / `Force304` 三个新 property
- 新 `FakeCatalogHttpCacheStore` 让 refresh 测试可控(避免每次都搭真 DB)
- `TestDb` 已有 schema 自动迁移(`EnsureCatalogCacheDbSchema` 跑新表 + 9 列)
- 沿用 v0.6.13-B `Fake subclass override` pattern:`GitHubCatalogMetadataService` 不 sealed + `EnrichAsync` virtual → 测试 override 新字段提取逻辑

**集成 + 回归:**
- 全 pipeline 集成测试:mock HTTP server(`Mock<HttpMessageHandler>`)→ real `CatalogFetcher` + real `CatalogHttpCacheStore` + real `CatalogRepository` → 验 hash diff 端到端
- **951 existing tests 必须继续 PASS**(增量修改,无 breaking)
- **回归 v0.6.13-B.2**:`RefreshAsync_VersionServiceThrowsRateLimit_SucceedsWithZeroCount` 确认 hash diff 不干扰 rate limit catch
- **回归 v0.6.13-B.1**:`FetchCatalogMetadata` toggle 仍 default OFF(用户桌面默认关,加列后 metadata columns 仍空直到用户开 toggle + 跑 refresh)
- **回归限流回归**:19 cols 提取不改 API call count(/repos + /releases + /commits 已有),parse time +50%,确认 5883 entries 仍能 1 round 跑完(5000/h token 触发时机不变)

**关键 lessons 复述(v0.6.13-B):**
- `EnrichAsync` 保持 `virtual` 让 Fake override(B.1)
- 8 字段 strict null-check(`/repos` 响应可能 missing fields)
- 5000/h 限流回归测试(B.2)
- `TestDb` Dispose 走 `SqliteConnection.ClearAllPools()` + recursive Directory.Delete(B.8)

### 3.6 迁移路径

**Schema 迁移**(首次 v0.6.14 启动):
1. `EnsureCatalogCacheDbSchema` 跑 `CREATE TABLE IF NOT EXISTS catalog_http_cache` + 9 次 `EnsureColumn`
2. 旧 DB(无 v0.6.14 列):SQLite 自动 `ALTER TABLE ADD COLUMN` + NotNull DEFAULT '' 回填,**不动老数据**
3. 启动完成,DB schema v0.6.14-ready,旧 rows `content_hash=''`(已知)

**首次 v0.6.14 refresh**(用户桌面 staging 验):
1. 旧 5873 rows `content_hash=''` → 全部视为 "changed" → 全量 upsert + 算 hash
2. 完成后所有 row 都有 hash → 等价一次**强制全量 refresh**
3. 后续 refresh → hash diff 生效,真正增量

**降级路径**(用户回到 v0.6.13-B):
- v0.6.13-B 不识 `content_hash` + 8 GitHub cols → SQLite `ALTER TABLE` 不删列无害
- v0.6.13-B 不识 `catalog_http_cache` 表 → `EnsureCatalogCacheDbSchema` 用 `IF NOT EXISTS` 不冲突
- 行为:v0.6.13-B 写 row 时不写新列(SQLite 不要求所有列)— 9 个新列变回 NULL,无害
- 不需要任何降级 patch

## 4. Carry-forward (deferred, not in scope)

以下不在本 spec,留 v0.6.15+:
- **C1**: UI 展示 8 个新 GitHub 字段(catalog tile badge、detail panel 新行)— 跟 v0.6.10.2 catalog detail card 风格
- **C2**: 软删 `IsRemoved=1` 保留历史(本 spec 硬删)— 等用户要"我之前装过但 catalog 删了"的历史
- **C3**: UI Toast / StatusBar 显示 `+A ~U ⟳S -M` 刷新摘要(本 spec 仅 AppLogger log)— 等用户要"刷新后我没看到提示"
- **C4**: "force full refresh" toggle / 按钮(用户删 DB 太 hacky)— 等用户要"hash 算错了想全量重算"
- **C5**: 缓存位置从 `<AppBaseDir>/data/` 搬 `%APPDATA%/ComfyUI-Manager/` — 等用户升级体验问题
- **C6**: hash 算法可配置(SHA256 → Blake3)— 不需要,SHA256 性能足够
- **C7**: 304 路径仍跑 metadata enrichment(MetadataCache 24h TTL 决定)— 当前 skip,等用户要"refresh 后 metadata 总是最新"
- **C8**: `release_tag` 单独调 `/releases/latest` endpoint 替代从 `/releases[0]` 提取 — 零收益,YAGNI
- **C9**: PythonCompat / OsCompat 真实解析(setup.py / pyproject.toml / .github/workflows)— v0.6.13-A/C scope 已 deferred

## 5. Open questions

无 — 6 个原 open question 全部通过方案 A 决议(本 spec):

| 原 Q | 决议 |
|------|------|
| 缓存位置:搬 `%APPDATA%` 还是保持 `<AppBaseDir>/data/`? | **保持** `<AppBaseDir>/data/`(YAGNI,v0.6.15+ 再搬) |
| 删除条目策略:硬删 / 软删 / archive? | **硬删**(cascade `node_versions`)— 简单 + DB 干净 |
| 新增/删除感知 UI:Toast / summary line? | **仅 AppLogger log**(无 UI)— 留 C3 |
| 首次刷新无 hash 时:回填还是全当作新? | **全当作新**(等价一次强制全量 refresh)— 自动迁移 |
| HTTP 缓存存哪里:DB 表 / JSON 文件? | **新表 `catalog_http_cache` 在同 DB**— 原子 + 事务 |
| metadata 跳过条件:hash 不变 / hash+ref 都变? | **304 路径全 skip,200 路径 metadata 不受 hash diff 影响**(走 MetadataCache 24h TTL)— 正交 |

## 6. 验收标准 (Acceptance Criteria)

- [ ] AC-1: 旧 DB(无 v0.6.14 列)在 staging 启动后 schema 自动迁移,新 9 列 + 新表创建成功
- [ ] AC-2: 首次 v0.6.14 refresh 后 `content_hash` 列全部非空,等价一次强制全量 refresh
- [ ] AC-3: 二次 refresh 在 catalog JSON 未变时返回 `Success=true, EntryCount=0, SkippedCount=5873, others=0`,纯本地 SQL `SELECT COUNT(*)` + log,**耗时 < 3 秒**(不计外网 DNS / TCP,本地计时)
- [ ] AC-4: 二次 refresh 在 catalog JSON 有 N 条变更时,只 upsert N 条 + 只 enrichment N 条,`AddedCount+UpdatedCount=N, SkippedCount=5873-N`
- [ ] AC-5: catalog JSON 删除某 entry 后 refresh,该 entry 从 `catalog_cache` + `node_versions` 双删,`DeletedCount` 含它
- [ ] AC-6: 8 个新 GitHub 字段全部入库(catalog-cache.db SELECT 验证),`html_url` / `homepage` / `language` / `release_tag` / `created_at` 非空,`forks_count` / `open_issues_count` / `subscribers_count` ≥ 0
- [ ] AC-7: `Settings.FetchCatalogMetadata=false`(默认)时,refresh 仍跑 hash diff + upsert,**不跑** metadata enrichment(用户桌面默认体验)
- [ ] AC-8: `Settings.FetchCatalogMetadata=true` 时,refresh 跑 hash diff + upsert + metadata enrichment(只 enrichment Added+Updated,Unchanged 走 MetadataCache 24h TTL)
- [ ] AC-9: 951 existing tests + ~25 new tests = ~976 PASS / 0 FAIL / 1 SKIP(1 pre-existing flake 不回归)
- [ ] AC-10: GUI smoke 桌面 staging 验 5 步:
  1. **启动**:删除 `<staging>/data/catalog-cache.db`(模拟首次安装),启动 staging,`Logs/2026-08-13.log` 出现 `[app-startup]` + schema 迁移 9 列 + 新表 INFO 行
  2. **首次刷新**:点 Catalog 页 Refresh,等 7-10 秒。日志应见 `[catalog-refresh] 开始 refresh` + `[catalog-fetch] 完成 fetch count=5883 duration_ms~6500` + `[catalog-refresh] 完成 refresh upsert_count=5883 ... duration_ms~7000`。DB 验证:`SELECT COUNT(*) WHERE content_hash != ''` = 5883(全量 upsert 等价强制刷新)
  3. **二次刷新(未变)**:立即点 Refresh,等 < 3 秒。日志应见 `[catalog-fetch] 完成 fetch count=0 ... 304` + `[catalog-refresh] 完成 refresh upsert_count=0 ... skipped_count=5883 duration_ms<3000`
  4. **二次刷新(有变)**:修改 catalog JSON 加 1 条新 entry + 改 1 条老 entry 的 title,点 Refresh,等 ~5 秒。日志应见 `+1 ~1 ⟳5881 -0`(Added=1, Updated=1, Unchanged=5881, Deleted=0)
  5. **删 entry 验证**:从 catalog JSON 删 1 条,点 Refresh,日志见 `+0 ~0 ⟳5882 -1`;DB 验证该 entry 从 `catalog_cache` + `node_versions` 双删

  (前提:`Settings.FetchCatalogMetadata=false` 默认,metadata enrichment 不跑,日志只含 catalog 相关行。若用户开 toggle,需再验 metadata 字段非空 — 见 AC-6/AC-8)