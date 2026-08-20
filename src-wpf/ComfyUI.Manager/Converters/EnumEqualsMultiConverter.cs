using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>v0.6.22+:MultiBinding 比较两个 enum 值是否相等(返 bool)。
///
/// 用途:ToggleButton chip 高亮 — DataTrigger.Value 不能挂 {Binding}(WPF 启动崩
/// v0.6.17.1 lesson),需要 "当前 chip 值 == VM.ActiveSort" 的 bool 比较。
/// MultiBinding + IMultiValueConverter 是这个问题的标准解法。
///
/// values[0] = ItemsControl 当前 item(枚举值),values[1] = VM.ActiveSort。
/// 类型必须相同,否则返 false。</summary>
public sealed class EnumEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var left = values[0];
        var right = values[1];
        if (left is null || right is null) return false;
        if (left.GetType() != right.GetType()) return false;
        return left.Equals(right);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("EnumEqualsMultiConverter is OneWay only");
}