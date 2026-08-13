using System;
using System.Globalization;
using System.Windows.Data;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.14 picker hotfix(after GUI smoke):templated RadioButton 不能用
/// <c>ConverterParameter={Binding ...}</c>(WPF 把 Binding 实例赋给 ConverterParameter
/// → XamlParseException at FrameworkTemplate.LoadTemplateXaml),所以改用
/// IMultiValueConverter 拿两个 binding (Filter + ActiveFilter) 直接比。
///
/// <para>
/// XAML:
/// <code>
///   &lt;RadioButton.IsChecked&gt;
///     &lt;MultiBinding Converter="{StaticResource PickerFilterEqualsConverter}"&gt;
///       &lt;Binding Path="Filter" /&gt;
///       &lt;Binding Path="DataContext.ActiveFilter"
///                RelativeSource="{RelativeSource AncestorType=Window}" /&gt;
///     &lt;/MultiBinding&gt;
///   &lt;/RadioButton.IsChecked&gt;
/// </code>
/// </para>
/// </summary>
public sealed class PickerFilterEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length != 2) return false;
        if (values[0] is PickerFilter lhs && values[1] is PickerFilter rhs)
            return lhs == rhs;
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        // RadioButton 用户点击改 VM.ActiveFilter 走 Click handler,不通过 ConvertBack。
        return new object[] { Binding.DoNothing, Binding.DoNothing };
    }
}