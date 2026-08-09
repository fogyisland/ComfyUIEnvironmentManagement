using System;
using System.Globalization;
using System.Windows.Data;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// Compares MainViewModel.CurrentSection with an enum name supplied as the converter parameter.
/// </summary>
public sealed class SectionEqualityToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is MainSection section
            && parameter is string name
            && section.ToString() == name;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("One-way binding only");
    }
}
