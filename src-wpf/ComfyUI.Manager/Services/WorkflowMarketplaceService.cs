using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:并行聚合多个 IWorkflowSource,合并去重,单 source 失败不影响其他。
/// 不持久化缓存(用户每次点"刷新"重新拉)。</summary>
public class WorkflowMarketplaceService
{
    private readonly IReadOnlyList<IWorkflowSource> _sources;
    private readonly AppLogger? _logger;

    public WorkflowMarketplaceService(IEnumerable<IWorkflowSource> sources, AppLogger? logger = null)
    {
        _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger;
    }

    /// <summary>并行调每个 IsEnabled 的 source。返回 deduped 列表;
    /// 任一 source 失败仅 log,不影响其他 source 的结果。</summary>
    public virtual async Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
        string query, int maxResultsPerSource, CancellationToken ct = default)
    {
        var enabled = _sources.Where(s => s.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            _logger?.Warn("workflow-marketplace", "no enabled sources");
            return Array.Empty<WorkflowEntry>();
        }

        _logger?.Info("workflow-marketplace",
            $"LoadAllAsync sources={enabled.Count} query='{query}' maxPerSource={maxResultsPerSource}");

        // parallel fetch — 每个 source 一个 task,exception 单独 catch
        var tasks = enabled.Select(async s =>
        {
            try
            {
                return await s.SearchAsync(query, maxResultsPerSource, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error("workflow-marketplace",
                    $"source {s.SourceKind} threw: {ex.Message}", ex);
                return Array.Empty<WorkflowEntry>();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // dedup by (Source, SourceId) — first wins
        var seen = new HashSet<(WorkflowSourceKind, string)>();
        var merged = new List<WorkflowEntry>();
        foreach (var batch in results)
        {
            foreach (var entry in batch)
            {
                if (string.IsNullOrEmpty(entry.SourceId)) continue;
                if (seen.Add((entry.Source, entry.SourceId)))
                {
                    merged.Add(entry);
                }
            }
        }

        _logger?.Info("workflow-marketplace", $"aggregated {merged.Count} unique entries");
        return merged;
    }
}