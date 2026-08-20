using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:模型市场 pluggable source interface。
/// T4 ModelMarketplaceService 拿 IEnumerable&lt;IModelSource&gt; 聚合,
/// T9 DI 注入 2 个 source (CivitAI full + HuggingFace stub)。
/// SearchAsync 由 source 内部 paginate 直到 maxResults 或 cursor=null。
/// v0.6.22+:SearchPageAsync 暴露单页 + nextCursor 给 UI 层做"加载更多"分页控制;
/// SearchAsync 改写成 SearchPageAsync 的循环包装,保持向后兼容。</summary>
public interface IModelSource
{
    /// <summary>Source 唯一标识(kind tag for badges / aggregation dedup)。</summary>
    ModelSourceKind SourceKind { get; }

    /// <summary>UI 显示名 (e.g. "CivitAI" / "HuggingFace")。</summary>
    string DisplayName { get; }

    /// <summary>Settings UI toggle。false → aggregator skip 该 source。</summary>
    bool IsEnabled { get; set; }

    /// <summary>单页搜索 + 显式 cursor 控制。cursor=null = 第一页。
    /// 返回 (本页条目, 下一页 cursor — null 表示已无更多)。
    /// Source 不自动 follow pagination — 由 caller 决定何时停。
    /// 抛 HttpRequestException 让 aggregator try/catch 隔离 source failure。
    /// sort/period 参数仅 CivitAI 使用;HF / 其他不支持的 source 直接忽略。</summary>
    Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct);

    /// <summary>便捷搜索:内部循环 SearchPageAsync 直到 results.Count == maxResults 或 cursor=null。
    /// 保留向后兼容 — 旧 service code 仍可用,新 service code 应优先用 SearchPageAsync 做 UI 显式分页。
    /// 使用 CivitAI 默认 sort=Newest / period=AllTime(对 HF 无意义但接口统一)。</summary>
    Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
