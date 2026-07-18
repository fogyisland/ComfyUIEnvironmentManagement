using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ComfyUI.Manager.Views;

/// <summary>
/// NotBoolConverter:把 bool 取反(true → false, false → true)。
/// </summary>
public sealed class NotBoolConverter : IValueConverter
{
    public static readonly NotBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : DependencyProperty.UnsetValue;
    }
}

/// <summary>
/// NullToVisibilityConverter:null → Collapsed,非 null → Visible(用于显示 ErrorMessage)。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// BoolToBrushConverter:bool → Brush(active/inactive),用于视图切换按钮高亮。
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = value is bool b && b;
        if (active)
        {
            return new SolidColorBrush(Color.FromRgb(0x67, 0x50, 0xA4));
        }
        return new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// InverseBoolToVisibilityConverter:bool → Visibility(true → Collapsed,false → Visible)。
/// 用于 "空状态" 提示文本(没有数据时显示)。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
