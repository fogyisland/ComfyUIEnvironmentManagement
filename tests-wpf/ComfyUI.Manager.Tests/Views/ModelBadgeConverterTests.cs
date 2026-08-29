using System;
using System.Globalization;
using System.Windows.Media;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class ModelBadgeConverterTests
{
    [Theory]
    [InlineData(ModelNsfwKind.SFW, "SFW")]
    [InlineData(ModelNsfwKind.Mature, "Mature")]
    [InlineData(ModelNsfwKind.NSFW, "NSFW")]
    public void NsfwBadgeText_ReturnsCorrectString(ModelNsfwKind kind, string expected)
    {
        var converter = ModelNsfwBadgeTextConverter.Instance;
        var result = converter.Convert(kind, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NsfwBadgeText_UnknownEnum_ReturnsQuestionMark()
    {
        var converter = ModelNsfwBadgeTextConverter.Instance;
        var result = converter.Convert((ModelNsfwKind)999, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("?", result);
    }

    [Fact]
    public void NsfwBadgeBrush_Nsfw_ReturnsErrorVariantBrush()
    {
        // 接受 3 种合法返回:
        // 1. Palette.Light 的 ErrorBrush = #B3261E = (0xB3, 0x26, 0x1E)
        // 2. Palette.Dark  的 ErrorBrush = #CF6679 = (0xCF, 0x66, 0x79)
        // 3. Application.Current 为 null 时的 fallback = (0xBA, 0x1A, 0x1A)
        // full-suite 跑时其他 WPF test 加载 Theme.xaml 把 palette 装进 Application.Current,
        // converter 走 TryFindResource → 返 palette brush (light/dark 取决于加载的 palette),
        // 不是 fallback。isolated 跑时 Application.Current 是 null → 走 fallback。
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.NSFW, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        var expected = new[]
        {
            Color.FromRgb(0xBA, 0x1A, 0x1A),  // fallback
            Color.FromRgb(0xB3, 0x26, 0x1E),  // Palette.Light ErrorColor
            Color.FromRgb(0xCF, 0x66, 0x79),  // Palette.Dark  ErrorColor
        };
        Assert.Contains(brush.Color, expected);
    }

    [Fact]
    public void NsfwBadgeBrush_Mature_ReturnsWarningBrush()
    {
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.Mature, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void NsfwBadgeBrush_Sfw_ReturnsOutlineBrush()
    {
        var converter = ModelNsfwBadgeBrushConverter.Instance;
        var result = converter.Convert(ModelNsfwKind.SFW, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Theory]
    [InlineData(ModelKind.Checkpoint)]
    [InlineData(ModelKind.LORA)]
    [InlineData(ModelKind.VAE)]
    [InlineData(ModelKind.Controlnet)]
    [InlineData(ModelKind.TextualInversion)]
    [InlineData(ModelKind.Upscaler)]
    [InlineData(ModelKind.Hypernetwork)]
    [InlineData(ModelKind.Other)]
    public void KindBadgeBrush_AllKinds_ReturnNonNullBrush(ModelKind kind)
    {
        var converter = ModelKindBadgeBrushConverter.Instance;
        var result = converter.Convert(kind, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void KindBadgeBrush_UnknownKind_ReturnsOutlineFallback()
    {
        var converter = ModelKindBadgeBrushConverter.Instance;
        var result = converter.Convert((ModelKind)999, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void AllConverters_ConvertBack_ThrowNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            ModelNsfwBadgeBrushConverter.Instance.ConvertBack(null, typeof(Brush), null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() =>
            ModelNsfwBadgeTextConverter.Instance.ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() =>
            ModelKindBadgeBrushConverter.Instance.ConvertBack(null, typeof(Brush), null, CultureInfo.InvariantCulture));
    }
}
