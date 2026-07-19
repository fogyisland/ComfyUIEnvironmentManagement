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

    public virtual async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var src = _settings.QuerySources
            .FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        try
        {
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            // SQLite write loop OFF the UI SynchronizationContext.
            // 调用方是 WPF command handler(UI 线程),await 返回后 foreach
            // 会同步跑 3000+ 个 Upsert,UI 卡到完成才解封。
            // Task.Run 把整段 foreach 推到 thread pool,UI 立刻恢复。
            var url = src.Url;
            await Task.Run(() =>
            {
                foreach (var e in entries)
                {
                    e.SourceUrl = url;
                    _repo.Upsert(e);
                }
            }, ct);
            return RefreshResult.Ok(entries.Count);
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