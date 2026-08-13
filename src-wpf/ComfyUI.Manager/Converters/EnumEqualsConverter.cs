using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.14 picker redesign:比较 bound value 跟 ConverterParameter(字符串解析到 enum)是否相等。
/// 用于 RadioButton IsChecked OneWay 绑 enum 值 — RadioButton.IsChecked 是 bool,
/// 所以 Convert 把 "value == parsed-parameter" 折成 bool。ConvertBack 不需要
/// (RadioButton.Checked 通过命令/事件设 ActiveFilter,不走 ConvertBack)。
///
/// <para>
/// ConverterParameter 是字符串(enum name),XAML 里写 <c>ConverterParameter="All"</c>。
/// 这样不需要 {Binding} 当 parameter(那会在 WPF markup evaluation 里返 Binding 实例,
/// 永远不等于 enum 值 — Critical 1 的踩坑)。这也是 MainWindow.xaml SidebarRadioButton
/// 用的 SectionEqualityToBoolConverter 同款 pattern。
/// </para>
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        if (parameter is not string paramStr) return false;
        if (!Enum.TryParse(value.GetType(), paramStr, ignoreCase: false, out var parsed))
            return false;
        return value.Equals(parsed);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // RadioButton 用 OneWay binding(从 VM → RadioButton.IsChecked)。
        // 用户点击 RadioButton 由 Checked 事件或 Command 改 VM,不走 ConvertBack。
        return Binding.DoNothing;
    }
}
