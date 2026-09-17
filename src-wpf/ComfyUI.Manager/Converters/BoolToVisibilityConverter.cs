using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>v1.0.0.x (2026-09-17) T47:true → Visible / false → Collapsed。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}