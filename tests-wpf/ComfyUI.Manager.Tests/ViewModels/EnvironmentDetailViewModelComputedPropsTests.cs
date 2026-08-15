using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentDetailViewModelComputedPropsTests
{
    [Fact]
    public void FormatRelative_Null_ReturnsUnknown()
    {
        Assert.Equal("未知", EnvironmentDetailViewModel.FormatRelative(null));
    }

    [Fact]
    public void FormatRelative_JustNow_FormatsCorrectly()
    {
        var now = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        Assert.Equal("刚刚", EnvironmentDetailViewModel.FormatRelative(now));
    }

    [Fact]
    public void FormatRelative_TwoMinutesAgo_FormatsCorrectly()
    {
        var twoMinAgo = System.DateTime.UtcNow.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        Assert.Equal("2 分钟前", EnvironmentDetailViewModel.FormatRelative(twoMinAgo));
    }
}
