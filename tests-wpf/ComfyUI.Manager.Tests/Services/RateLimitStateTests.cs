using System;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class RateLimitStateTests
{
    [Fact]
    public void IsBlocked_Default_ReturnsFalse()
    {
        var state = new RateLimitState();
        Assert.False(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void MarkBlocked_ThenIsBlocked_ReturnsTrueWithInfo()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, partialCount: 100, totalCount: 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.NotNull(info);
        Assert.Equal(100, info!.PartialCount);
        Assert.Equal(5000, info.TotalCount);
    }

    [Fact]
    public void MarkBlocked_ResetTimeInPast_DoesNotBlock()
    {
        var state = new RateLimitState();
        var pastReset = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, pastReset, 50, 5000);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out var info));
    }

    [Fact]
    public void MarkBlocked_NullReset_DoesNotBlock()
    {
        var state = new RateLimitState();
        state.MarkBlocked(RateLimitStage.Version, null, 50, 5000);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out _));
    }

    [Fact]
    public void MarkBlocked_MultipleStages_AreIndependent()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, 100, 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out _));
        Assert.False(state.IsBlocked(RateLimitStage.Metadata, out _));
    }

    [Fact]
    public void MarkBlocked_ThenClear_IsBlockedReturnsFalse()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, 100, 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out _));
        state.Clear(RateLimitStage.Version);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out _));
    }

    [Fact]
    public void MarkBlocked_Twice_TakesLatestResetTime()
    {
        var state = new RateLimitState();
        var firstReset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var secondReset = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, firstReset, 100, 5000);
        state.MarkBlocked(RateLimitStage.Version, secondReset, 200, 5500);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.Equal(200, info!.PartialCount);
        Assert.Equal(5500, info.TotalCount);
    }
}