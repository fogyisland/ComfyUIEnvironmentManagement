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
/// StringNotEmptyToVisibilityConverter:string → Visibility(null/空 → Collapsed,其它 → Visible)。
/// v0.6.15.1 hotfix:LocalNodeListView URL 行 — 没 URL 时整个 TextBlock 隐藏,有 URL 时显示。
/// 跟 NullToVisibilityConverter 不同:这个判 null + empty string。
/// </summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public static readonly StringNotEmptyToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
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

/// <summary>
/// RelativeTimeConverter:string (ISO-8601) → string (相对时间 "刚刚"/"N 分钟前"/etc)。
/// 用于 DataGrid 列直接绑 LastScannedAt — DataGrid 没法绑到 static method,
/// 需要 IValueConverter。null/空/解析失败 → "未知"。
/// 阈值:&lt;60s=刚刚, &lt;60min=N 分钟前, &lt;24h=N 小时前, &lt;30d=N 天前, else=yyyy-MM-dd。
/// v0.6.15.7 T8:env-detail 加载时间列显示「相对时间」而非裸 ISO-8601。
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return "未知";
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return "未知";
        var delta = DateTime.UtcNow - dt;
        if (delta.TotalSeconds < 0) return "刚刚";
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} 天前";
        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
