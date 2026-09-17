using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:本地文件路径 → BitmapImage(150px 同步 Decode + Freeze)。
/// 路径 null/空/不存在/损坏 → 返回 null(view 走 placeholder 灰块)。
/// T48 改异步懒加载(本 task 同步 OK 因 Cards/List 视图不用此 converter,
/// 仅 ThumbnailsView 用)。
/// </summary>
public sealed class LocalPathToBitmapConverter : IValueConverter
{
    private const int DecodePixelWidth = 150;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;  // 同步加载 + 立即释放文件锁
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;  // 损坏的图片 → placeholder
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}