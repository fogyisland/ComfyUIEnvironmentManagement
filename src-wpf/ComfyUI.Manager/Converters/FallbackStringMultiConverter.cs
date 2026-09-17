using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0.x (2026-09-17) T46i.2:多 binding fallback — 返回第一个非空非 null 的 string。
/// 用于 LocalModelsView card Title 绑定:
/// <c>MatchedDetail?.Title ?? scannerTitle</c>,DataTrigger 不能在 Value 上挂 binding
/// (v0.6.17.1 lesson) → 必须用 MultiBinding + 本 converter 在 VM 前先 fallback。
///
/// values 顺序:优先值在前,fallback 在后。空字符串视作 null(走 fallback)。
///
/// 仅 Convert(单向),ConvertBack 返回 Binding.DoNothing。
/// </summary>
public sealed class FallbackStringMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null) return string.Empty;
        foreach (var v in values)
        {
            if (v is null) continue;
            var s = v.ToString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        // 单向,UI 不会改回 VM
        return new object[] { Binding.DoNothing };
    }
}
