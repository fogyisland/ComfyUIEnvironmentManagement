namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15: 跨 service → UI 边界的 rate limit 事件 record。
/// CatalogRefreshService 撞 limit 时构造 → IProgress&lt;RateLimitInfo&gt;.Report
/// 给 RateLimitBannerViewModel.Show()；同时 MarkBlocked 到 IRateLimitState
/// 让下次 refresh 入口能跳过。
/// </summary>
/// <param name="Stage">哪个 stage 撞了（Version / Metadata）</param>
/// <param name="Remaining">GitHub X-RateLimit-Remaining（0 = 用尽）</param>
/// <param name="ResetUnix">X-RateLimit-Reset（unix 秒）。null = 响应头未带</param>
/// <param name="PartialCount">本次拉取已成功的 entry 数</param>
/// <param name="TotalCount">本次本应拉取的总 entry 数</param>
public record RateLimitInfo(
    RateLimitStage Stage,
    long Remaining,
    long? ResetUnix,
    int PartialCount,
    int TotalCount);