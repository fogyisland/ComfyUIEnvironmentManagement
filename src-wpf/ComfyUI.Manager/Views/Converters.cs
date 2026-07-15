using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

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
