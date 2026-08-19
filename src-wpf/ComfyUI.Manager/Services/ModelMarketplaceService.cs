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
    {
        var enabled = _sources.Where(s => s.IsEnabled).ToList();
        var tasks = enabled.Select(async src =>
        {
            try
            {
                var entries = await src.SearchAsync(query, maxResultsPerSource, ct);
                _logger?.Info("model-marketplace", $"[{src.DisplayName}] fetched {entries.Count} entries");
                return (src.SourceKind, entries);
            }
            catch (Exception ex)
            {
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
        return merged;
    }
}
