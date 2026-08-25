using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

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
/// v1.0.0 T10:NullToVisibilityConverter 的反向版本 — null → Visible,非 null → Collapsed。
/// 用于 "有预览图时显示 Image,无时显示 fallback badge" 模式。
/// 必须独立 converter — NullToVisibilityConverter 不支持 ConverterParameter=invert
/// (见 progress.md pre-flight ruling + Views/Converters.cs:32-44 实现)。
/// </summary>
public sealed class InverseNullToVisibilityConverter : IValueConverter
{
    public static readonly InverseNullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.19.x UI polish:WorkflowSourceKind → Brush,工作流卡片 source pill badge。
/// CommunityJson → PrimaryBrush(主题紫) / CivitAi → SecondaryBrush / OpenArt → SuccessBrush。
/// 用 Application.Current.TryFindResource 走 palette(light/dark 自动跟随);
/// 找不到资源时 fallback 到一个固定色,保证 XAML 解析不会 UnsetValue。
/// </summary>
public sealed class WorkflowSourceBadgeBrushConverter : IValueConverter
{
    public static readonly WorkflowSourceBadgeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            WorkflowSourceKind.CommunityJson => "PrimaryBrush",
            WorkflowSourceKind.CivitAi => "SecondaryBrush",
            WorkflowSourceKind.OpenArt => "SuccessBrush",
            _ => "OutlineBrush",
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));  // 默认灰
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.19.x UI polish:WorkflowSourceKind → string,工作流卡片 source pill 文案。
/// CommunityJson → "社区" / CivitAi → "CivitAI" / OpenArt → "OpenArt"。
/// </summary>
public sealed class WorkflowSourceBadgeTextConverter : IValueConverter
{
    public static readonly WorkflowSourceBadgeTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            WorkflowSourceKind.CommunityJson => "社区",
            WorkflowSourceKind.CivitAi => "CivitAI",
            WorkflowSourceKind.OpenArt => "OpenArt",
            _ => "?",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.20 T7:ModelNsfwKind → Brush。SFW=OutlineBrush(中灰),Mature=WarningBrush(橙),NSFW=ErrorBrush(红)。
/// palette fallback:ErrorBrush → (0xBA,0x1A,0x1A) 红,WarningBrush → (0xE6,0x7E,0x22) 橙,OutlineBrush → (0xCC,0xCC,0xCC) 灰。
/// </summary>
public sealed class ModelNsfwBadgeBrushConverter : IValueConverter
{
    public static readonly ModelNsfwBadgeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ModelNsfwKind.SFW => "OutlineBrush",
            ModelNsfwKind.Mature => "WarningBrush",
            ModelNsfwKind.NSFW => "ErrorBrush",
            _ => "OutlineBrush",
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return key switch
        {
            "ErrorBrush" => new SolidColorBrush(Color.FromRgb(0xBA, 0x1A, 0x1A)),
            "WarningBrush" => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
            _ => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.20 T7:ModelNsfwKind → string,NSFW badge pill 文案。SFW="SFW",Mature="Mature",NSFW="NSFW"。
/// </summary>
public sealed class ModelNsfwBadgeTextConverter : IValueConverter
{
    public static readonly ModelNsfwBadgeTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            ModelNsfwKind.SFW => "SFW",
            ModelNsfwKind.Mature => "Mature",
            ModelNsfwKind.NSFW => "NSFW",
            _ => "?",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// v0.6.20 T7:ModelKind → Brush(8 kind 各自的 palette 颜色)。
/// Checkpoint=PrimaryBrush,LORA=SecondaryBrush,VAE=TertiaryBrush,Controlnet=SuccessBrush,
/// TextualInversion=WarningBrush,Upscaler=InfoBrush,Hypernetwork=ErrorBrush,Other/Unknown=OutlineBrush。
/// v1.0.0 T12:Diffusers=WarningContainerBrush (palette 已定义,在 SettingsView/RateLimitBanner 用过,跟其他 kind 区分明显)。
/// palette fallback 9 种颜色全部硬编码,确保无 Application.Current 时 XAML 不会 UnsetValue。
/// </summary>
public sealed class ModelKindBadgeBrushConverter : IValueConverter
{
    public static readonly ModelKindBadgeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (key, fallback) = value switch
        {
            ModelKind.Checkpoint       => ("PrimaryBrush",         Color.FromRgb(0x67, 0x50, 0xA4)),
            ModelKind.LORA             => ("SecondaryBrush",       Color.FromRgb(0x4F, 0x6D, 0x8C)),
            ModelKind.VAE              => ("TertiaryBrush",        Color.FromRgb(0x6B, 0x8E, 0x23)),
            ModelKind.Controlnet       => ("SuccessBrush",         Color.FromRgb(0x38, 0x8E, 0x3C)),
            ModelKind.TextualInversion => ("WarningBrush",         Color.FromRgb(0xE6, 0x7E, 0x22)),
            ModelKind.Upscaler         => ("InfoBrush",            Color.FromRgb(0x19, 0x76, 0xD2)),
            ModelKind.Hypernetwork     => ("ErrorBrush",           Color.FromRgb(0xBA, 0x1A, 0x1A)),
            ModelKind.Diffusers        => ("WarningContainerBrush", Color.FromRgb(0xFF, 0xB3, 0x00)),  // amber gold (HF Diffusers 识别色)
            ModelKind.Other            => ("OutlineBrush",         Color.FromRgb(0x75, 0x75, 0x75)),
            _                          => ("OutlineBrush",         Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
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
/// SeverityToBrushConverter:ErrorSeverity → Brush(按 palette 资源键解析)。
/// Info → PrimaryBrush(主题色,light/dark 自动跟随)
/// Warn → WarningBrush(橙)
/// Error → ErrorBrush(红)
/// Critical → ErrorVariantBrush(深红)
/// 没找到资源时 fallback 到 ErrorBrush(永远有定义)。
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly SeverityToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ErrorSeverity.Info => "PrimaryBrush",
            ErrorSeverity.Warn => "WarningBrush",
            ErrorSeverity.Error => "ErrorBrush",
            ErrorSeverity.Critical => "ErrorVariantBrush",
            _ => "ErrorBrush",
        };
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Color.FromRgb(0xBA, 0x1A, 0x1A));  // 默认红
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

/// <summary>v1.0.0 T11:LocalModelCard.Source(string) == ConverterParameter(string) → Visible,
/// 不等 → Collapsed。EnumEqualsVisibilityConverter 跟 string 不匹配 — Source 是从
/// scanner 来的字符串(Local / CivitAi / HuggingFace 等)不是 enum,需要专门的 string 版本。
/// 用途:Local-source 卡片显示 [🔍 查询 CivitAI] 按钮,meta.json / civitai / hf 卡片隐藏。</summary>
public sealed class CardSourceVisibilityConverter : IValueConverter
{
    public static readonly CardSourceVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>v1.0.0 T13-7:本地模型卡片右下角 status dot 的填充颜色。
/// 输入 LocalModelCard.MatchedDetail (CivitAiDetailDto?):
///   - 非 null → 已 matched,显示 SuccessBrush(绿)
///   - null    → 未 matched(还没 scan 或 4 策略全 miss),显示 OutlineBrush(灰)
/// palette fallback:SuccessBrush → (0x38, 0x8E, 0x3C) 绿 / OutlineBrush → (0xCC, 0xCC, 0xCC) 灰。</summary>
public sealed class MatchStatusToBrushConverter : IValueConverter
{
    public static readonly MatchStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is CivitAiDetailDto ? "SuccessBrush" : "OutlineBrush";
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return key == "SuccessBrush"
            ? new SolidColorBrush(Color.FromRgb(0x38, 0x8E, 0x3C))
            : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>v1.0.0 T13-7:本地模型卡片右下角 status dot 的 tooltip 文本。
/// 输入 LocalModelCard.MatchSource (MatchSource?):
///   - Hash                → "Matched via SHA256 hash"
///   - SafetensorsMetadata → "Matched via safetensors metadata"
///   - CompanionJson       → "Matched via .civitai.info sidecar"
///   - FilenameFuzzy       → "Matched via filename fuzzy search"
///   - null                → "Not on CivitAI"(还没 scan 或 4 策略全 miss)</summary>
public sealed class MatchSourceToTooltipConverter : IValueConverter
{
    public static readonly MatchSourceToTooltipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            MatchSource.Hash => "Matched via SHA256 hash",
            MatchSource.SafetensorsMetadata => "Matched via safetensors metadata",
            MatchSource.CompanionJson => "Matched via .civitai.info sidecar",
            MatchSource.FilenameFuzzy => "Matched via filename fuzzy search",
            _ => "Not on CivitAI",
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}