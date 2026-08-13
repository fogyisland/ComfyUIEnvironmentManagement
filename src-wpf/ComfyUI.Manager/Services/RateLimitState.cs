using System;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15: 进程单例 RateLimitState 实现。无 using / dispose，
/// GC 兜底（生命周期 = 进程生命周期）。所有访问经 lock 保护，
/// IsBlocked 顺便清理过期 entries。
/// </summary>
public sealed class RateLimitState : IRateLimitState
{
    private readonly object _lock = new();
    private RateLimitBlockInfo? _version;
    private RateLimitBlockInfo? _metadata;

    public bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info)
    {
        lock (_lock)
        {
            var current = GetSlot(stage);
            // reset time 已过 → 自动 unblock（等同 Clear）
            if (current is not null && current.ResetAt <= DateTimeOffset.Now)
            {
                SetSlot(stage, null);
                current = null;
            }
            info = current;
            return current is not null;
        }
    }

    public void MarkBlocked(RateLimitStage stage, long? resetUnix, int partialCount, int totalCount)
    {
        if (resetUnix is null) return;  // 没拿到 reset time 不记录
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value);
        if (resetAt <= DateTimeOffset.Now) return;  // 已过期不记录
        lock (_lock)
        {
            SetSlot(stage, new RateLimitBlockInfo(DateTimeOffset.Now, resetAt, partialCount, totalCount));
        }
    }

    public void Clear(RateLimitStage stage)
    {
        lock (_lock)
        {
            SetSlot(stage, null);
        }
    }

    private RateLimitBlockInfo? GetSlot(RateLimitStage stage) => stage switch
    {
        RateLimitStage.Version => _version,
        RateLimitStage.Metadata => _metadata,
        _ => null,
    };

    private void SetSlot(RateLimitStage stage, RateLimitBlockInfo? value)
    {
        if (stage == RateLimitStage.Version) _version = value;
        else _metadata = value;
    }
}