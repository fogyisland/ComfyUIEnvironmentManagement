using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.22+:通用 bool 取反 converter。true → false,false → true;非 bool → false。
/// 当前用途:Settings 访问代理段 URL/Port 输入框 IsEnabled = HttpProxyEnabled AND !HttpProxyUseSystemProxy,
/// 用本 converter 喂 HttpProxyUseSystemProxy 取反给 AndBoolMultiConverter。
/// </summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}