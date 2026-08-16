using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.15.10:bool → 字符串(由 ConverterParameter 决定 true/false 映射)。
/// ConverterParameter 格式 "trueText|falseText",例如 "存在|无"、"是|否"、"✓|✗"。
/// 缺 / 格式错 fallback 返 parameter 自身,免 XAML 抛 binding 错。
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var paramStr = parameter as string ?? "";
        var parts = paramStr.Split('|', 2);
        var trueText = parts.Length > 0 ? parts[0] : "true";
        var falseText = parts.Length > 1 ? parts[1] : "false";
        return value is true ? trueText : falseText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Row details 里的 Run.Text 是 OneWay,不需要 ConvertBack。
        return Binding.DoNothing;
    }
}