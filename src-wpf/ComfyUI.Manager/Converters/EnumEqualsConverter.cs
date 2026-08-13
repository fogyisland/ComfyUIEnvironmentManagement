using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// 比较 bound value 跟 ConverterParameter 是否相等(同一枚举值)。
/// 用于 RadioButton IsChecked 双向绑 enum 值 — RadioButton.IsChecked 是 bool,
/// 所以 Convert 把 "value == parameter" 折成 bool,ConvertBack 把 bool 转回 enum。
///
/// pattern 跟 SectionEqualityToBoolConverter 一致,但 SectionEqualityToBoolConverter
/// 写死 enum 名字符串比对,不可复用。这里加通用的 enum equality converter。
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        // Enum 直接 Equals 同值返 true;底层 type 不同会 false。
        return value.Equals(parameter);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // RadioButton IsChecked → bool → 参数就是绑定的 enum value,
        // 由 binding system 直接 set ActiveFilter;这里只需保证非 null 返回 parameter。
        if (value is bool b && b) return parameter!;
        return Binding.DoNothing;
    }
}