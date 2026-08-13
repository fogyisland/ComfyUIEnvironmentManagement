using System.Globalization;
using ComfyUI.Manager.Converters;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Converters;

/// <summary>
/// v0.6.14 picker hotfix(after GUI smoke):ConverterParameter={Binding FilterName}
/// 在 DataTemplate 里的 RadioButton 上 runtime 崩(XamlParseException at
/// FrameworkTemplate.LoadTemplateXaml)。改用 IMultiValueConverter
/// PickerFilterEqualsConverter,MultiBinding 喂 Filter + ActiveFilter 两值。
///
/// 这里 unit-test 钉住 converter 比对逻辑。
/// </summary>
public class PickerFilterEqualsConverterTests
{
    private readonly PickerFilterEqualsConverter _converter = new();

    [Theory]
    [InlineData(PickerFilter.All, PickerFilter.All, true)]
    [InlineData(PickerFilter.NotInstalled, PickerFilter.NotInstalled, true)]
    [InlineData(PickerFilter.Installed, PickerFilter.Installed, true)]
    [InlineData(PickerFilter.Outdated, PickerFilter.Outdated, true)]
    [InlineData(PickerFilter.All, PickerFilter.NotInstalled, false)]
    [InlineData(PickerFilter.NotInstalled, PickerFilter.Installed, false)]
    [InlineData(PickerFilter.Installed, PickerFilter.Outdated, false)]
    public void Convert_BothEnums_ReturnsEquality(PickerFilter filter, PickerFilter active, bool expected)
    {
        var result = _converter.Convert(
            new object[] { filter, active }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_WrongValuesCount_ReturnsFalse()
    {
        var result = _converter.Convert(
            new object[] { PickerFilter.All }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_WrongTypes_ReturnsFalse()
    {
        var result = _converter.Convert(
            new object[] { "All", PickerFilter.NotInstalled }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_NullValues_ReturnsFalse()
    {
        var result = _converter.Convert(
            null!, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = _converter.ConvertBack(
            true, new[] { typeof(PickerFilter), typeof(PickerFilter) }, null, CultureInfo.InvariantCulture);
        Assert.Equal(2, result.Length);
        Assert.Equal(System.Windows.Data.Binding.DoNothing, result[0]);
        Assert.Equal(System.Windows.Data.Binding.DoNothing, result[1]);
    }
}