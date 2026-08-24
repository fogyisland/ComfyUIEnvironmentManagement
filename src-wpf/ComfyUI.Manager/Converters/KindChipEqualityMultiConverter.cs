using System;
using System.Globalization;
using System.Windows.Data;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0 T2 fix r1:kind chip RadioButton.IsChecked 用 IMultiValueConverter 替代
/// IValueConverter + ConverterParameter。v0.6.14 picker hotfix (after GUI smoke) 已经踩过
/// 同样的坑:templated RadioButton 不能用 <c>ConverterParameter={Binding ...}</c>(WPF 把
/// Binding 实例赋给 ConverterParameter → XamlParseException at
/// FrameworkTemplate.LoadTemplateXaml),所以改用 IMultiValueConverter 拿两个 binding
/// (VM.ActiveChip + the chip item) 直接比。
///
/// <para>
/// XAML:
/// <code>
///   &lt;RadioButton.IsChecked&gt;
///     &lt;MultiBinding Converter="{StaticResource KindChipEquality}" Mode="OneWay"&gt;
///       &lt;Binding Path="DataContext.ActiveChip"
///                RelativeSource="{RelativeSource AncestorType=UserControl}" /&gt;
///       &lt;Binding /&gt;  &lt;!-- the chip item itself --&gt;
///     &lt;/MultiBinding&gt;
///   &lt;/RadioButton.IsChecked&gt;
/// </code>
/// </para>
/// </summary>
public sealed class KindChipEqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length != 2) return false;
        if (values[0] is null || values[1] is null) return false;
        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        // RadioButton 用户点击改 VM.ActiveChip 走 Checked code-behind,不通过 ConvertBack。
        return new object[] { Binding.DoNothing, Binding.DoNothing };
    }
}