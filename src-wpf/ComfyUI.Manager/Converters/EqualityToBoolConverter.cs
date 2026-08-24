using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0:本地模型视图 (T2) 用 — value (e.g. VM.ActiveChip) 与 ConverterParameter (e.g. the chip item) 引用相等 → true。
/// RadioButton.IsChecked 用 OneWay 绑定(用户点击 → code-behind 改 VM.ActiveChip setter,不走 ConvertBack),
/// 所以 ConvertBack 抛 NotSupportedException。
/// </summary>
public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) ?? false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("EqualityToBoolConverter is OneWay only — RadioButton click updates VM via code-behind.");
    }
}
