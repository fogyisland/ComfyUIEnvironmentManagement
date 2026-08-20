using System;
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
    /// sort/period 参数仅 CivitAI 使用;HF / 其他不支持的 source 直接忽略。
    /// includeNsfw(0.6.22+):NSFW 透传到 API(<c>?nsfw=true|false</c>)— false 时 source
    /// 应在结果级别排除 NSFW/Mature(用户 2026-08-20 反馈"因为我们就需要完整的非NSFW数据"。
    /// 仅在 API 层能切的 source(CivitAI)使用此参数;HF 内部 post-filter 等价处理。
    /// baseModel(0.6.22+):VM UI chip strip 显式选的 base model(用户 2026-08-20 反馈
    /// "模型参数是不是也可以传递?也就是 base model 列出常规可用的 Model 类型")。
    /// null/空 = 不附加显式 base model filter(只靠 query 自动识别)。
    /// CivitAI 与 query-detected baseModels 合并作为 <c>?baseModels=</c> filter。
    /// 默认 <c>null</c> 保持向后兼容。
    /// progress(0.6.22+):可选 progress sink — source 应在构造完 URL 后
    /// <c>progress?.Report($"[URL] {url}")</c> 报告真实 HTTP URL,让 VM Console
    /// 面板展示给用户(用户 2026-08-20 反馈"感觉还是筛选,并没有将模型类型传递
    /// 给 search api" — 需要可见的 URL 证据)。null = 静默(向后兼容)。</summary>
    Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct,
        bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null);

    /// <summary>便捷搜索:内部循环 SearchPageAsync 直到 results.Count == maxResults 或 cursor=null。
    /// 保留向后兼容 — 旧 service code 仍可用,新 service code 应优先用 SearchPageAsync 做 UI 显式分页。
    /// 使用 CivitAI 默认 sort=Newest / period=AllTime(对 HF 无意义但接口统一)。
    /// includeNsfw(0.6.22+)透传到 SearchPageAsync — 默认 true 保持向后兼容。
    /// baseModel(0.6.22+)透传到 SearchPageAsync — CivitAI 跟 query-detected 合并作为
    /// <c>?baseModels=</c> filter,HF 接收但 no-op。</summary>
    Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct, bool includeNsfw = true, string? baseModel = null, IProgress<string>? progress = null);
}
