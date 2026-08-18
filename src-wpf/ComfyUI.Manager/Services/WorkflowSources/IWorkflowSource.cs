using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:工作流市场数据源接口 — 由 Aggregator 并行调用。</summary>
public interface IWorkflowSource
{
    WorkflowSourceKind SourceKind { get; }
    string DisplayName { get; }
    bool IsEnabled { get; set; }

    Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct = default);
}