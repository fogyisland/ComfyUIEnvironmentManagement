using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.17.1:DataTrigger 不能在 Value 上挂 <c>{Binding}</c>(Value 不是
/// DependencyProperty — WPF 启动时 XamlParseException,见 WPF Event Log 1026)。
/// 正确做法是把 ActiveStartStatusEnvId(VM 单值) + this.Id(行 Environment 单值)
/// 包成 <see cref="MultiBinding"/> + 本 converter,返回 bool 给 DataTrigger.Value。
/// values[0] = activeEnvId(string?),values[1] = currentEnvId(string)。相等 → true。
///
/// 仅 Convert(VM → view),ConvertBack 不需要(DataTrigger 单向)。
/// </summary>
public sealed class StringEqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        // 单向,UI 不会改回 VM
        return new object[] { Binding.DoNothing, Binding.DoNothing };
    }
}