using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>v0.6.22+:比较 bound enum value 跟 ConverterParameter(字符串解析到 enum)是否相等,
/// 相等 → Visible,不等 → Collapsed。
///
/// 用途:把整个 StackPanel / Border 的 Visibility 绑到 enum property,
/// 例 "ActiveSource == CivitAi" 才显示 sort/period filter row(HF 不支持)。
/// 参数跟 <see cref="EnumEqualsConverter"/> 一样 — enum name 字符串。</summary>
public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        if (parameter is not string paramStr) return Visibility.Collapsed;
        if (!Enum.TryParse(value.GetType(), paramStr, ignoreCase: false, out var parsed))
            return Visibility.Collapsed;
        return value.Equals(parsed) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}