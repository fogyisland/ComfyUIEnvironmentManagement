using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.20:模型市场聚合服务。T3 注入 1+ IModelSource (CivitAI full + HuggingFace stub),
/// T4 负责:filter IsEnabled → Task.WhenAll 并行 fetch → per-source try/catch 隔离失败 →
/// HashSet&lt;(ModelSourceKind, string)&gt; dedup (避免不同 source 同 numeric id 碰撞) → 合并返回。
/// T5 (ModelDownloader) / T8 (ModelMarketplaceViewModel) 依赖 LoadAllAsync 的精确签名。
/// </summary>
public class ModelMarketplaceService
{
    private readonly IReadOnlyList<IModelSource> _sources;
    private readonly AppLogger? _logger;

    public ModelMarketplaceService(IEnumerable<IModelSource> sources, AppLogger? logger = null)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(string query, int maxResultsPerSource, CancellationToken ct = default)
        => await LoadAllAsync(query, maxResultsPerSource, sourceFilter: null, progress: null, includeNsfw: true, baseModel: null, ct);

    /// <summary>v0.6.22 T6:加 sourceFilter 单源查询 — UI 改成 source 单选 radio 后,
    /// VM 只查选中的 source(避免被禁用的 source 拉白)。null = 查全部 enabled(旧行为兼容)。</summary>
    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, ModelSourceKind? sourceFilter = null, CancellationToken ct = default)
        => await LoadAllAsync(query, maxResultsPerSource, sourceFilter, progress: null, includeNsfw: true, baseModel: null, ct);

    /// <summary>
    /// v0.6.22 T6+:加 <paramref name="progress"/> 可选参数 — VM 用 Progress&lt;string&gt; 推 UI 状态:
    /// 启动 / per-source 完成或失败 / 合并后总数。null = 静默(向后兼容)。
    /// Progress&lt;string&gt;.ctor 捕获 SynchronizationContext — VM 端 await 后 ConsoleLog.Add 自动 marshal 回 UI 线程。
    /// v0.6.22+:加 includeNsfw — VM.toggle 后重 fetch 把 NSFW 选项透传到所有 source。
    /// </summary>
    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
        IProgress<string>? progress, CancellationToken ct = default)
        => await LoadAllAsync(query, maxResultsPerSource, sourceFilter, progress, includeNsfw: true, baseModel: null, ct);

    /// <summary>
    /// v0.6.22+:includeNsfw 透传 source 层(用户 2026-08-20 反馈"因为我们就需要完整的非
    /// NSFW数据")。CivitAI 走 <c>?nsfw=true|false</c>;HF post-filter 等价处理。
    /// 默认 true 保持向后兼容。
    /// </summary>
    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
        IProgress<string>? progress, bool includeNsfw, string? baseModel, CancellationToken ct = default)
    {
        var enabled = _sources.Where(s => s.IsEnabled && (sourceFilter is null || s.SourceKind == sourceFilter)).ToList();
        progress?.Report($"[开始] 启用源: {(enabled.Count == 0 ? "(无)" : string.Join(", ", enabled.Select(s => s.DisplayName)))} bm={baseModel ?? "(无)"}");
        var tasks = enabled.Select(async src =>
        {
            try
            {
                var entries = await src.SearchAsync(query, maxResultsPerSource, ct, includeNsfw, baseModel);
                progress?.Report($"[{src.DisplayName}] 完成, {entries.Count} 条");
                _logger?.Info("model-marketplace", $"[{src.DisplayName}] fetched {entries.Count} entries (nsfw={includeNsfw} bm={baseModel})");
                return (src.SourceKind, entries);
            }
            catch (Exception ex)
            {
                progress?.Report($"[{src.DisplayName}] 失败: {ex.Message}");
                _logger?.Error("model-marketplace", $"[{src.DisplayName}] failed: {ex.Message}");
                return (src.SourceKind, (IReadOnlyList<ModelEntry>)Array.Empty<ModelEntry>());
            }
        });
        var results = await Task.WhenAll(tasks);

        var seen = new HashSet<(ModelSourceKind, string)>();
        var merged = new List<ModelEntry>();
        foreach (var (_, entries) in results)
        {
            foreach (var e in entries)
            {
                if (seen.Add((e.Source, e.SourceId)))
                    merged.Add(e);
            }
        }
        progress?.Report($"[合并] 共 {merged.Count} 条(去重后)");
        return merged;
    }

    /// <summary>
    /// v0.6.22+:UI 显式分页入口 — 单页 fetch + nextCursor 透传。
    /// 与 <see cref="LoadAllAsync"/> 区别:不内部循环 paginate,只 fetch 一页,
    /// 由 VM 维护 cursor 状态在用户点击 "加载更多" 时再调一次。
    /// cursor=null 表示第一页;返回的 nextCursor=null 表示当前 source 已无更多。
    /// dedup 跟 <see cref="LoadAllAsync"/> 一样(per-source 自动 dedup,但 UI 跨多次调用需 VM 自己合并 —
    /// 这次接口暂不返回 dedup'd key,VM 端用 HashSet 跟踪已显示 id 即可)。
    /// sort/period 参数透传给 enabled sources(用户 2026-08-20 反馈"搜索似乎只传关键词")。
    /// </summary>
    public virtual async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> LoadPageAsync(
        string query, string? cursor, int pageSize, ModelSourceKind? sourceFilter,
        CivitAiSort sort, CivitAiPeriod period,
        IProgress<string>? progress, bool includeNsfw = true, string? baseModel = null, CancellationToken ct = default)
    {
        var enabled = _sources.Where(s => s.IsEnabled && (sourceFilter is null || s.SourceKind == sourceFilter)).ToList();
        progress?.Report($"[加载更多] 源: {(enabled.Count == 0 ? "(无)" : string.Join(", ", enabled.Select(s => s.DisplayName)))} nsfw={includeNsfw} bm={baseModel ?? "(无)"}");
        if (enabled.Count == 0) return (Array.Empty<ModelEntry>(), null);

        // 单源场景(当前 UI radio 永远只选一个 source):直接返回该 source 的 (entries, nextCursor)。
        // 多源场景 future work — 现在聚合语义不明确("merge 多 source 的 cursor" 没意义),
        // 此处退化为只取第一个 enabled source 的结果。
        var src = enabled[0];
        try
        {
            var (entries, nextCursor) = await src.SearchPageAsync(query, cursor, pageSize, sort, period, ct, includeNsfw, baseModel);
            progress?.Report($"[{src.DisplayName}] +{entries.Count} 条, 下一页={(nextCursor is null ? "(无)" : "有")}");
            return (entries, nextCursor);
        }
        catch (Exception ex)
        {
            progress?.Report($"[{src.DisplayName}] 失败: {ex.Message}");
            _logger?.Error("model-marketplace", $"[{src.DisplayName}] page fetch failed: {ex.Message}");
            return (Array.Empty<ModelEntry>(), null);
        }
    }
}
