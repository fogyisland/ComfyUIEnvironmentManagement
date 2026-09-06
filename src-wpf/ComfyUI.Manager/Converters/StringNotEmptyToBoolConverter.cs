using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-directory:String → Bool 转换器
/// 非空 string → true(给 IsEnabled 等),空 → false。
/// SettingsView line 226 "扫描"按钮 NodelistDirectory 路径非空时启用。
/// </summary>
public sealed class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
