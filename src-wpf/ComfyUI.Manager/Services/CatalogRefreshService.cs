using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// CatalogRefreshService:Settings 和 Catalog 两个页面共享的"从 active
/// QuerySource 拉 catalog JSON → 写 SQLite"流程。失败时不抛,返回
/// RefreshResult.Fail(reason)。
///
/// v0.6.14 起是 5 阶段增量流程:
///   1) 读 HTTP cache(etag / last-modified)→ 2) conditional fetch
///      (304 直接短路返回,不动 DB)→ 3) 写回新 etag → 4) per-entry hash diff
///      (Added / Updated / Skipped)+ 只 upsert 变了的 → 5) 硬删 JSON 里
///      消失的 entry(cascade node_versions)。
/// 之后照旧跑 version fetch 和 metadata enrichment(各自开关 gate)。
/// </summary>
public class CatalogRefreshService
{
    private readonly CatalogFetcher _fetcher;
    private readonly CatalogRepository _repo;
    private readonly NodeVersionRepository? _versionRepo;
    private readonly GitHubVersionService? _versionService;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;
    private readonly GitHubCatalogMetadataService? _metadataService;
    private readonly CatalogHttpCacheStore? _httpCacheStore;  // v0.6.14

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
        int addedCount = 0, updatedCount = 0, skippedCount = 0, deletedCount = 0;
        try
        {
            // ===== Step 1: HTTP cache-aware conditional fetch =====
            var (etag, lastMod) = _httpCacheStore is not null
                ? await _httpCacheStore.GetAsync(src.Url, ct).ConfigureAwait(false)
                : (null, null);
            var fetchResult = await _fetcher
                .FetchAsync(src.Url, etag, lastMod, ct).ConfigureAwait(false);

            // ===== Step 1.5: 304 短路 —— 服务器说没变,一行 DB 都不动 =====
            // 注意必须在 UpsertBatch 之前 return:304 时 Entries 是 null。
            if (fetchResult.Is304)
            {
                // 现有 rows 全算 "skipped"(它们都没被重写)
                var existing = await _repo
                    .GetContentHashesBySourceAsync(src.Url, ct).ConfigureAwait(false);
                skippedCount = existing.Count;
                // RFC 7232 §4.1:304 响应也可以带新 ETag(服务器轮换 validator),带了就存
                if (fetchResult.NewEtag is not null || fetchResult.NewLastModified is not null)
                {
                    await SaveHttpCacheAsync(src.Url,
                        fetchResult.NewEtag ?? etag,
                        fetchResult.NewLastModified ?? lastMod, ct).ConfigureAwait(false);
                }
                _logger?.Info("catalog-refresh",
                    $"no changes (304) skipped_count={skippedCount} duration_ms={sw.ElapsedMilliseconds}");
                return RefreshResult.Ok(n: 0, added: 0, updated: 0,
                    skipped: skippedCount, deleted: 0);
            }

            // ===== Step 1.6: 200 —— 写回新 validator 供下次 conditional fetch =====
            await SaveHttpCacheAsync(src.Url,
                fetchResult.NewEtag, fetchResult.NewLastModified, ct).ConfigureAwait(false);

            var entries = fetchResult.Entries is null
                ? new List<CatalogEntry>()
                : fetchResult.Entries.ToList();
            var url = src.Url;
            foreach (var e in entries) e.SourceUrl = url;

            // ===== Step 2: per-entry hash diff + selective upsert =====
            var existingHashes = await _repo
                .GetContentHashesBySourceAsync(src.Url, ct).ConfigureAwait(false);
            var toUpsert = new List<CatalogEntry>();
            var jsonPackages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                jsonPackages.Add(e.Package);
                var newHash = CatalogEntryHasher.ComputeHash(e);
                if (!existingHashes.TryGetValue(e.Package, out var existingHash))
                {
                    addedCount++;          // DB 没这 package → Added
                    toUpsert.Add(e);
                }
                else if (!string.Equals(existingHash, newHash, StringComparison.Ordinal))
                {
                    updatedCount++;        // hash 变了 → Updated
                    toUpsert.Add(e);
                }
                else
                {
                    skippedCount++;        // hash 一致 → Skipped,不碰 DB
                }
            }

            int count = 0;
            if (toUpsert.Count > 0)
            {
                count = await Task.Run(
                    () => _repo.UpsertBatch(toUpsert, e => progress?.Report(e)), ct)
                    .ConfigureAwait(false);
            }

            // ===== Step 2.5: JSON 里消失的 entry → 硬删(cascade node_versions)=====
            var removedPackages = existingHashes.Keys
                .Where(p => !jsonPackages.Contains(p))
                .ToList();
            if (removedPackages.Count > 0)
            {
                deletedCount = await _repo.DeleteRemovedEntriesAsync(
                    src.Url, removedPackages, ct).ConfigureAwait(false);
            }

            // 第二步:拉 GitHub 版本(如开关 gate 开启—— v0.6.11 T3)
            // 用户没配 token 也能拉,只要勾"刷新时拉取节点版本"开关;空 token
            // = 未鉴权(GitHub 限流 60/h)。
            // v0.6.13-B.2 hotfix:GitHubVersionService 检测 403 + X-RateLimit-Remaining=0
            // → throw RateLimitException → 顶层 catch fail-soft + Warn 日志,refresh
            // ~60s 完成不再 9 分钟。
            // v0.6.14:只对 toUpsert(Added + Updated)拉版本 —— skipped 的 entry
            // latest_version 已经在 DB 里,重拉纯浪费 GitHub 配额。
            // v0.6.14 hotfix:GithubVersionService.FetchVersionsAsync 仍按 entry.Id
            // 键返回(上游不改),但下游写库必须按 (source_url, package) ——
            // entry.Id 是 CatalogFetcher 每次新分配的 Guid,跨 refresh 不稳定。
            // 这里 build id → (sourceUrl, package) 字典在结果回传时翻译成 stable
            // 三元组给 CatalogRepository.UpdateLatestVersions / NodeVersionRepository。
            if (_versionService is not null && _settings.FetchNodeVersionsOnRefresh)
            {
                var nodes = toUpsert
                    .Select(e => (e.Id, ReferenceUrl: ExtractReference(e)))
                    .Where(t => !string.IsNullOrWhiteSpace(t.ReferenceUrl))
                    .ToList();
                Dictionary<string, List<VersionInfo>>? versions = null;
                try
                {
                    versions = await _versionService.FetchVersionsAsync(
                        nodes, _settings.GitHubToken, versionProgress, ct);
                }
                catch (RateLimitException ex)
                {
                    _logger?.Warn("catalog-refresh",
                        $"GitHub rate limit hit on version fetch,refresh 返回部分结果: {ex.Message}");
                }
                if (versions is { Count: > 0 })
                {
                    // build id → (source_url, package) 用于把 versions 字典的结果翻译成 stable key
                    var idToKey = toUpsert
                        .Where(e => versions.ContainsKey(e.Id))
                        .GroupBy(e => e.Id)
                        .ToDictionary(
                            g => g.Key,
                            g => (SourceUrl: g.First().SourceUrl, Package: g.First().Package),
                            StringComparer.Ordinal);

                    versionCount = await Task.Run(() =>
                    {
                        // 1) 写完整历史(10 个/node)到 node_versions — 用 (source_url, package)
                        if (_versionRepo is not null)
                        {
                            var rowsForNodeVersions = new List<(string, string, VersionInfo)>();
                            foreach (var (id, vs) in versions)
                            {
                                if (!idToKey.TryGetValue(id, out var key)) continue;
                                foreach (var v in vs)
                                {
                                    rowsForNodeVersions.Add((key.SourceUrl, key.Package, v));
                                }
                            }
                            _versionRepo.UpsertBatch(rowsForNodeVersions);
                        }
                        // 2) 更新 catalog_cache.latest_version 列(每个 node 取
                        //    第一个非 prerelease,fallback 到第一个) — 按 (source_url, package)
                        var rowsForLatest = new List<(string SourceUrl, string Package, string Version)>();
                        foreach (var (id, vs) in versions)
                        {
                            if (!idToKey.TryGetValue(id, out var key)) continue;
                            var tag = vs.FirstOrDefault(v => !v.IsPrerelease)?.Tag
                                ?? vs.FirstOrDefault()?.Tag
                                ?? "";
                            rowsForLatest.Add((key.SourceUrl, key.Package, tag));
                        }
                        return _repo.UpdateLatestVersions(rowsForLatest);
                    }, ct);
                }
            }

            // 第三步:拉 GitHub metadata(开关 gate 开启时,v0.6.13-B)
            // v0.6.14:同样只 enrich toUpsert —— MetadataCache(T7)对没变的 entry
            // 本来也会命中缓存,这里直接不传省一轮循环。
            int metadataCount = 0;
            if (_metadataService is not null && _settings.FetchCatalogMetadata && toUpsert.Count > 0)
            {
                try
                {
                    var metaProgress = new Progress<MetadataFetchProgress>(p =>
                        _logger?.Info("catalog-metadata",
                            $"progress done={p.Done}/{p.Total} current={p.CurrentPackage}"));
                    metadataCount = await _metadataService.EnrichAsync(toUpsert, metaProgress, ct);
                    _logger?.Info("catalog-metadata",
                        $"enrich done count={metadataCount}");
                    if (metadataCount > 0)
                    {
                        // 写回 SQLite(metadata 列)
                        await Task.Run(() => _repo.UpsertBatch(toUpsert), ct);
                    }
                }
                catch (RateLimitException ex)
                {
                    _logger?.Warn("catalog-metadata", $"rate limit hit,resume on next refresh: {ex.Message}");
                    metadataCount = 0;
                }
                catch (Exception ex)
                {
                    _logger?.Warn("catalog-metadata", $"metadata enrich fail (non-fatal): {ex.Message}");
                }
            }

            _logger?.Info("catalog-refresh",
                $"完成 refresh upsert_count={count} version_count={versionCount} metadata_count={metadataCount} " +
                $"added={addedCount} updated={updatedCount} skipped={skippedCount} deleted={deletedCount} " +
                $"duration_ms={sw.ElapsedMilliseconds}");
            return RefreshResult.Ok(count, versionCount, metadataCount,
                addedCount, updatedCount, skippedCount, deletedCount);
        }
        catch (OperationCanceledException)
        {
            _logger?.Warn("catalog-refresh", "refresh 已取消");
            return RefreshResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-refresh", $"refresh 失败 url={src.Url}", ex);
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }

    /// <summary>
    /// v0.6.14: 写回 ETag / Last-Modified。HTTP cache 纯属提速手段 —— 写失败
    /// (旧 DB 还没建 catalog_http_cache 表、磁盘满、并发锁)绝不能让整个
    /// refresh 失败,所以这里吞掉非取消异常只记 Warn。
    /// </summary>
    private async Task SaveHttpCacheAsync(
        string url, string? etag, string? lastModified, CancellationToken ct)
    {
        if (_httpCacheStore is null) return;
        try
        {
            await _httpCacheStore.PutAsync(url, etag, lastModified, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn("catalog-http-cache",
                $"PutAsync 失败(非致命,下次走全量 fetch) url={url} reason={ex.Message}");
        }
    }

    internal static string ExtractReference(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return "";
        if (entry.RawMetadata.TryGetValue("reference", out var r) && r is string rs && !string.IsNullOrEmpty(rs))
            return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us && !string.IsNullOrEmpty(us))
            return us;
        if (entry.RawMetadata.TryGetValue("repository", out var repo) && repo is string repos && !string.IsNullOrEmpty(repos))
            return repos;
        return "";
    }
}

/// <summary>
/// Refresh 结果。<paramref name="EntryCount"/> 是实际 upsert 的行数(v0.6.14 起
/// 只含变了的 entry,不再是 catalog 总条数)。
/// v0.6.14 加的 4 个计数把一次 refresh 拆开:Added(DB 里没有)/ Updated(hash 变了)
/// / Skipped(hash 一致,没碰 DB)/ Deleted(JSON 里消失,已硬删)。
/// </summary>
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
