using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:模型市场 pluggable source interface。
/// T4 ModelMarketplaceService 拿 IEnumerable&lt;IModelSource&gt; 聚合,
/// T9 DI 注入 2 个 source (CivitAI full + HuggingFace stub)。
/// SearchAsync 由 source 内部 paginate 直到 maxResults 或 cursor=null。</summary>
public interface IModelSource
{
    /// <summary>Source 唯一标识(kind tag for badges / aggregation dedup)。</summary>
    ModelSourceKind SourceKind { get; }

    /// <summary>UI 显示名 (e.g. "CivitAI" / "HuggingFace")。</summary>
    string DisplayName { get; }

    /// <summary>Settings UI toggle。false → aggregator skip 该 source。</summary>
    bool IsEnabled { get; set; }

    /// <summary>搜索接口(query 可空表示 list-all)。
    /// 实现者负责 paginate 直到 results.Count == maxResults 或内部 cursor 用尽。
    /// 抛 HttpRequestException 让 aggregator try/catch 隔离 source failure。</summary>
    Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
