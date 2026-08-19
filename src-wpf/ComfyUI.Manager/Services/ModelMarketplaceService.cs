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
        => await LoadAllAsync(query, maxResultsPerSource, sourceFilter: null, progress: null, ct);

    /// <summary>v0.6.22 T6:加 sourceFilter 单源查询 — UI 改成 source 单选 radio 后,
    /// VM 只查选中的 source(避免被禁用的 source 拉白)。null = 查全部 enabled(旧行为兼容)。</summary>
    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, ModelSourceKind? sourceFilter = null, CancellationToken ct = default)
        => await LoadAllAsync(query, maxResultsPerSource, sourceFilter, progress: null, ct);

    /// <summary>
    /// v0.6.22 T6+:加 <paramref name="progress"/> 可选参数 — VM 用 Progress&lt;string&gt; 推 UI 状态:
    /// 启动 / per-source 完成或失败 / 合并后总数。null = 静默(向后兼容)。
    /// Progress&lt;string&gt;.ctor 捕获 SynchronizationContext — VM 端 await 后 ConsoleLog.Add 自动 marshal 回 UI 线程。
    /// </summary>
    public virtual async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var enabled = _sources.Where(s => s.IsEnabled && (sourceFilter is null || s.SourceKind == sourceFilter)).ToList();
        progress?.Report($"[开始] 启用源: {(enabled.Count == 0 ? "(无)" : string.Join(", ", enabled.Select(s => s.DisplayName)))}");
        var tasks = enabled.Select(async src =>
        {
            try
            {
                var entries = await src.SearchAsync(query, maxResultsPerSource, ct);
                progress?.Report($"[{src.DisplayName}] 完成, {entries.Count} 条");
                _logger?.Info("model-marketplace", $"[{src.DisplayName}] fetched {entries.Count} entries");
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
}
