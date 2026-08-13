using System;
using System.Globalization;
using System.Windows.Data;
using ComfyUI.Manager.Converters;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Converters;

/// <summary>
/// v0.6.14 T2 R1:钉住 Critical 1 修法。
///
/// Critical 1:`ConverterParameter={Binding}` 在 WPF markup 评估时返回 Binding 实例
/// 不是绑定的值 → `EnumEqualsConverter.Convert` 永远返 false → chip 永远不亮。
/// 修法:converter 接受字符串 parameter(XAML 绑 <c>{Binding FilterName}</c> 拿 enum-name),
/// Enum.TryParse 到 enum 再跟 value 比对。这里直接 unit-test converter 的字符串解析逻辑。
/// </summary>
public class EnumEqualsConverterTests
{
    private readonly EnumEqualsConverter _converter = new();

    [Theory]
    [InlineData(PickerFilter.All, "All", true)]
    [InlineData(PickerFilter.NotInstalled, "NotInstalled", true)]
    [InlineData(PickerFilter.Installed, "Installed", true)]
    [InlineData(PickerFilter.Outdated, "Outdated", true)]
    [InlineData(PickerFilter.All, "NotInstalled", false)]
    [InlineData(PickerFilter.NotInstalled, "All", false)]
    [InlineData(PickerFilter.Installed, "Outdated", false)]
    public void Convert_StringParameter_MatchesParsedEnum(
        PickerFilter value, string param, bool expected)
    {
        var result = _converter.Convert(value, typeof(bool), param, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NullValue_ReturnsFalse()
    {
        var result = _converter.Convert(null, typeof(bool), "All", CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_NullParameter_ReturnsFalse()
    {
        var result = _converter.Convert(PickerFilter.All, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_NonStringParameter_ReturnsFalse()
    {
        // 防御性:如果 XAML 又写出 {Binding} 误传 Binding 实例,converter 不该抛
        // 也不该当成 enum 比对 — 直接返 false(让 chip 不亮,提示上层逻辑错了)。
        var result = _converter.Convert(PickerFilter.All, typeof(bool),
            new Binding(), CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_UnknownEnumName_ReturnsFalse()
    {
        // converter 收到 "Foo" → Enum.TryParse 返 false → 不抛,返 false。
        var result = _converter.Convert(PickerFilter.All, typeof(bool),
            "Foo", CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void ConvertBack_AlwaysReturnsDoNothing()
    {
        // RadioButton 用 OneWay + Click handler 改 ActiveFilter,不走 ConvertBack。
        // 这里钉死:任何输入都返 Binding.DoNothing,避免双向 binding 写入非法值。
        var result = _converter.ConvertBack(true, typeof(PickerFilter),
            "All", CultureInfo.InvariantCulture);
        Assert.Same(Binding.DoNothing, result);
    }
}
