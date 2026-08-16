using System;
using System.Globalization;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class RelativeTimeConverterTests
{
    private static object Convert(string? iso) =>
        RelativeTimeConverter.Instance.Convert(iso, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Null_ReturnsUnknown()
        => Assert.Equal("未知", Convert(null));

    [Fact]
    public void Empty_ReturnsUnknown()
        => Assert.Equal("未知", Convert(""));

    [Fact]
    public void InvalidFormat_ReturnsUnknown()
        => Assert.Equal("未知", Convert("not-a-date"));

    [Fact]
    public void LessThanSixtySeconds_ReturnsJustNow()
    {
        var iso = DateTime.UtcNow.AddSeconds(-30).ToString("o");
        Assert.Equal("刚刚", Convert(iso));
    }

    [Fact]
    public void TwoMinutesAgo_ReturnsRelativeChinese()
    {
        var iso = DateTime.UtcNow.AddMinutes(-2).ToString("o");
        Assert.Equal("2 分钟前", Convert(iso));
    }

    [Fact]
    public void FutureTime_ReturnsJustNow()
    {
        var iso = DateTime.UtcNow.AddMinutes(5).ToString("o");
        Assert.Equal("刚刚", Convert(iso));
    }

    [Fact]
    public void OldDate_ReturnsYyyyMmDd()
    {
        var iso = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc).ToString("o");
        Assert.Equal("2025-01-15", Convert(iso));
    }
}
