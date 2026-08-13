using System;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15: 进程级 rate limit 状态单例。CatalogRefreshService 撞 limit
/// 时 MarkBlocked，下次 refresh 入口 IsBlocked 检查跳过整个 stage 不浪费
/// GitHub 配额。IsBlocked 自动 unblock 已过期 stage（resetAt &lt;= now
/// 等同 Clear）。ResetUnix null 或过期 → 不记录。
/// </summary>
public interface IRateLimitState
{
    /// <summary>查 stage 是否在限流冷却中。返回 true 时 info 非 null。</summary>
    bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info);

    /// <summary>标记 stage 撞 rate limit。多次调用覆盖前次 reset time。</summary>
    void MarkBlocked(RateLimitStage stage, long? resetUnix, int partialCount, int totalCount);

    /// <summary>清除 stage 状态（refresh 成功完成时调）。</summary>
    void Clear(RateLimitStage stage);
}

/// <summary>
/// 单 stage 限流信息。ResetAt 仍未来 → IsBlocked 返回 true；已过 → 自动
/// 转 null。PartialCount / TotalCount 让 UI 显示 "X/Y partial results"。
/// </summary>
public record RateLimitBlockInfo(
    DateTimeOffset BlockedAt,
    DateTimeOffset ResetAt,
    int PartialCount,
    int TotalCount);