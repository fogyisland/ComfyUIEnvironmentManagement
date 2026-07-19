using System;
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

    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings,
        GitHubVersionService? versionService = null,
        NodeVersionRepository? versionRepo = null)
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
        _versionService = versionService;
        _versionRepo = versionRepo;
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
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        int versionCount = 0;
        try
        {
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            var url = src.Url;
            var count = await Task.Run(() =>
            {
                foreach (var e in entries) e.SourceUrl = url;
                return _repo.UpsertBatch(entries,
                    e => progress?.Report(e));
            }, ct);

            // 第二步:拉 GitHub 版本(如 token 已配)
            if (_versionService is not null && !string.IsNullOrWhiteSpace(_settings.GitHubToken))
            {
                var nodes = entries
                    .Select(e => (e.Id, ReferenceUrl: ExtractReference(e)))
                    .Where(t => !string.IsNullOrWhiteSpace(t.ReferenceUrl))
                    .ToList();
                var versions = await _versionService.FetchVersionsAsync(
                    nodes, _settings.GitHubToken, versionProgress, ct);
                if (versions.Count > 0)
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

            return RefreshResult.Ok(count, versionCount);
        }
        catch (OperationCanceledException)
        {
            return RefreshResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }

    private static string ExtractReference(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return "";
        if (entry.RawMetadata.TryGetValue("reference", out var r) && r is string rs)
            return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us)
            return us;
        return "";
    }
}

public record RefreshResult(bool Success, int EntryCount, int VersionCount, string? Error)
{
    public static RefreshResult Ok(int n, int v = 0) => new(true, n, v, null);
    public static RefreshResult Fail(string err) => new(false, 0, 0, err);
}
