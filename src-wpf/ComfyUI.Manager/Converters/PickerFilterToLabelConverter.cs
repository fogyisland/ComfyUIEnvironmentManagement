using System;
using System.Globalization;
using System.Windows.Data;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// PickerFilter enum → 显示用中文标签。
/// 集中一处维护显示文案,XAML 只引用 "PickerFilterToLabel"。
/// </summary>
public sealed class PickerFilterToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PickerFilter f)
        {
            return f switch
            {
                PickerFilter.All => "全部",
                PickerFilter.NotInstalled => "未装",
                PickerFilter.Installed => "已装",
                PickerFilter.Outdated => "已过时",
                _ => f.ToString(),
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("One-way binding only");
    }
}