using System;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class RateLimitBannerViewModelTests
{
    [Fact]
    public void IsVisible_DefaultFalse()
    {
        var vm = new RateLimitBannerViewModel();
        Assert.False(vm.IsVisible);
        Assert.Equal("", vm.Title);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public void Show_WithVersionInfo_PopulatesTitleAndMessage()
    {
        var vm = new RateLimitBannerViewModel();
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var info = new RateLimitInfo(RateLimitStage.Version, Remaining: 0,
            ResetUnix: resetUnix, PartialCount: 100, TotalCount: 5000);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        Assert.Contains("节点版本", vm.Title);
        Assert.Contains("100/5000", vm.Message);
    }

    [Fact]
    public void Show_WithMetadataInfo_StageLabelIsMetadata()
    {
        var vm = new RateLimitBannerViewModel();
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var info = new RateLimitInfo(RateLimitStage.Metadata, Remaining: 0,
            ResetUnix: resetUnix, PartialCount: 50, TotalCount: 200);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.Contains("catalog metadata", vm.Title);
    }

    [Fact]
    public void Show_NoResetUnix_ShowsRemainingCount()
    {
        var vm = new RateLimitBannerViewModel();
        var info = new RateLimitInfo(RateLimitStage.Version, Remaining: 0,
            ResetUnix: null, PartialCount: 10, TotalCount: 100);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        Assert.Contains("10/100", vm.Message);
        Assert.Contains("剩余 0 次", vm.Message);
    }

    [Fact]
    public void DismissCommand_HidesBanner()
    {
        var vm = new RateLimitBannerViewModel();
        var info = new RateLimitInfo(RateLimitStage.Version, 0,
            DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), 10, 100);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        vm.DismissCommand.Execute(null);
        Assert.False(vm.IsVisible);
    }
}