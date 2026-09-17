using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:非 null → true,用于 ContextMenu IsEnabled 绑定 MatchedDetail。
/// <c>Copy CivitAI 页面 URL</c> 菜单项仅在卡片有 MatchedDetail(CivitAI 查过)时启用,否则灰显。
/// ConvertBack 走 throw — 单向 binding,不需要反推。
/// </summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NotNullToBoolConverter is one-way only");
}
