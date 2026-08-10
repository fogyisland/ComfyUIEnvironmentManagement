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

/// <summary>
/// BoolToVisibilityConverter:bool → Visibility(true → Visible,false → Collapsed)。
/// 用于 "忙时显示" 类面板(如 CreateEnvDialog 进度面板 IsBusy=true → 显示)。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// ZeroCountToVisibilityConverter:int → Visibility(0 → Visible,>0 → Collapsed)。
/// 用于 "列表为空时显示提示" 模式(Gpus.Count == 0 时显示 "未检测到 Nvidia GPU")。
/// </summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public static readonly ZeroCountToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int n ? n : 0;
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// InverseZeroCountToVisibilityConverter:int → Visibility(0 → Collapsed,>0 → Visible)。
/// ZeroCountToVisibility 的反向版本,用于 "列表有数据时显示内容区"。
/// </summary>
public sealed class InverseZeroCountToVisibilityConverter : IValueConverter
{
    public static readonly InverseZeroCountToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int n ? n : 0;
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// BoolToEntryCountTextConverter:bool HasEntries + int Count → 字符串。
/// values[0]=true + values[1]=N → "共 {N} 个节点";values[0]=false → "加载中…"。
/// v0.6.11+ CatalogUI polish:替换裸 "HasEntries" 的 BoolToVisibility,显示具体计数。
/// 必须用 IMultiValueConverter — ConverterParameter 不是 DependencyProperty,无法接受 {Binding ...},
/// 即使能接受字面量 ConverterParameter=X 也以 string 到达,int test 永远 false。
/// XAML 绑定(MultiBinding pattern):
///   &lt;TextBlock&gt;
///       &lt;TextBlock.Text&gt;
///           &lt;MultiBinding Converter="{StaticResource BoolToEntryCountText}"&gt;
///               &lt;Binding Path="HasEntries" /&gt;
///               &lt;Binding Path="PagedEntries.Count" /&gt;
///           &lt;/MultiBinding&gt;
///       &lt;/TextBlock.Text&gt;
///   &lt;/TextBlock&gt;
/// </summary>
public sealed class BoolToEntryCountTextConverter : IMultiValueConverter
{
    public static readonly BoolToEntryCountTextConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var hasEntries = values.Length > 0 && values[0] is bool b && b;
        if (!hasEntries) return "加载中…";
        var count = values.Length > 1 && values[1] is int n ? n : 0;
        return $"共 {count} 个节点";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
