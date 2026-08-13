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
/// 流程:1) catalog fetch → upsert;2) 如果 settings.GitHubToken 非空,
///      对每个 GitHub reference 并发拉 latest version tag → upsert。
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

    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings,
        GitHubVersionService? versionService = null,
        NodeVersionRepository? versionRepo = null,
        AppLogger? logger = null,
        GitHubCatalogMetadataService? metadataService = null)
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
        _versionService = versionService;
        _versionRepo = versionRepo;
        _logger = logger;
        _metadataService = metadataService;
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
        try
        {
            var fetchResult = await _fetcher.FetchAsync(src.Url, ct);
            // v0.6.14: FetchAsync 返回 CatalogFetchResult,Entries 在 200 时是 IReadOnlyList。
            // 后端 UpsertBatch/EnrichAsync 需要 IList<CatalogEntry>,toList 转一次。
            var entries = fetchResult.Entries is null
                ? new List<CatalogEntry>()
                : fetchResult.Entries.ToList();
            var url = src.Url;
            var count = await Task.Run(() =>
            {
                foreach (var e in entries) e.SourceUrl = url;
                return _repo.UpsertBatch(entries,
                    e => progress?.Report(e));
            }, ct);

            // 第二步:拉 GitHub 版本(如开关 gate 开启—— v0.6.11 T3)
            // 用户没配 token 也能拉,只要勾"刷新时拉取节点版本"开关;空 token
            // = 未鉴权(GitHub 限流 60/h)。
            // v0.6.13-B.2 hotfix:GitHubVersionService 检测 403 + X-RateLimit-Remaining=0
            // → throw RateLimitException → 顶层 catch fail-soft + Warn 日志,refresh
            // ~60s 完成不再 9 分钟。
            if (_versionService is not null && _settings.FetchNodeVersionsOnRefresh)
            {
                var nodes = entries
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
                    versionCount = await Task.Run(() =>
                    {
                        // 1) 写完整历史(10 个/node)到 node_versions
                        if (_versionRepo is not null)
                        {
                            _versionRepo.UpsertBatch(
                                versions.SelectMany(kv =>
                                    kv.Value.Select(v => (kv.Key, v))));
                        }
                        // 2) 更新 catalog_cache.latest_version 列(每个 node 取
                        //    第一个非 prerelease,fallback 到第一个)
                        return _repo.UpdateLatestVersions(
                            versions.Select(kv => (
                                kv.Key,
                                kv.Value.FirstOrDefault(v => !v.IsPrerelease)?.Tag
                                    ?? kv.Value.FirstOrDefault()?.Tag
                                    ?? "")));
                    }, ct);
                }
            }

            // 第三步:拉 GitHub metadata(开关 gate 开启时,v0.6.13-B)
            int metadataCount = 0;
            if (_metadataService is not null && _settings.FetchCatalogMetadata)
            {
                try
                {
                    var metaProgress = new Progress<MetadataFetchProgress>(p =>
                        _logger?.Info("catalog-metadata",
                            $"progress done={p.Done}/{p.Total} current={p.CurrentPackage}"));
                    metadataCount = await _metadataService.EnrichAsync(entries, metaProgress, ct);
                    _logger?.Info("catalog-metadata",
                        $"enrich done count={metadataCount}");
                    if (metadataCount > 0)
                    {
                        // 写回 SQLite(11 个新列)
                        await Task.Run(() => _repo.UpsertBatch(entries), ct);
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
                $"完成 refresh upsert_count={count} version_count={versionCount} metadata_count={metadataCount} duration_ms={sw.ElapsedMilliseconds}");
            return RefreshResult.Ok(count, versionCount, metadataCount);
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

public record RefreshResult(bool Success, int EntryCount, int VersionCount, int MetadataCount, string? Error = null)
{
    public static RefreshResult Ok(int n, int v = 0, int m = 0) => new(true, n, v, m, null);
    public static RefreshResult Fail(string err) => new(false, 0, 0, 0, err);
}
