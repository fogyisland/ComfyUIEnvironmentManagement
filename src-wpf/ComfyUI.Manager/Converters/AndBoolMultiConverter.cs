using System;
using System.Globalization;
using System.Windows.Data;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v0.6.22+:通用 bool AND 多值 converter。MultiBinding values[*] → bool a &amp;&amp; bool b &amp;&amp; ...
/// 任意值非 bool(默认/异常)→ false。ConvertBack 单向(UI 不写回 VM)。
/// 当前用途:Settings 访问代理段 URL/Port 输入框 IsEnabled = HttpProxyEnabled AND !HttpProxyUseSystemProxy。</summary>
public sealed class AndBoolMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null) return false;
        foreach (var v in values)
        {
            if (v is not bool b || !b) return false;
        }
        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        // 单向,UI 不会改回 VM
        var result = new object[targetTypes.Length];
        for (var i = 0; i < result.Length; i++) result[i] = Binding.DoNothing;
        return result;
    }
}