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
/// </summary>
public class CatalogRefreshService
{
    private readonly CatalogFetcher _fetcher;
    private readonly CatalogRepository _repo;
    private readonly Settings _settings;

    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings)
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
    }

    public virtual async Task<RefreshResult> RefreshAsync(
        IProgress<CatalogEntry>? progress = null,
        CancellationToken ct = default)
    {
        var src = _settings.QuerySources
            .FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        try
        {
            // FetchAsync 内部:HTTP GetStringAsync 后还有 sync 工作(JSON 反序列化 +
            // 3000+ 条目 parse),这些全跑在 await 返回的 UI 线程上 → UI 卡死。
            // 整段推 Task.Run + 单连接事务批量 Upsert(10-50x 快)。
            // Per-entry 回调走 Progress<CatalogEntry> 自动 marshall 回 UI 线程。
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            var url = src.Url;
            var count = await Task.Run(() =>
            {
                foreach (var e in entries) e.SourceUrl = url;
                return _repo.UpsertBatch(entries,
                    e => progress?.Report(e));
            }, ct);
            return RefreshResult.Ok(count);
        }
        catch (Exception ex)
        {
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }
}

public record RefreshResult(bool Success, int EntryCount, string? Error)
{
    public static RefreshResult Ok(int n) => new(true, n, null);
    public static RefreshResult Fail(string err) => new(false, 0, err);
}