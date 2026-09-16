using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace ComfyUI.Manager.Converters;

/// <summary>
/// v1.0.0.x (2026-09-16) T43i.7+user「时间格式 中间的 T 去掉」:
/// SQLite 存的是 .NET ISO 8601 序列化格式(`yyyy-MM-ddTHH:mm:ss.fffffff+08:00` 或
/// `yyyy-MM-ddTHH:mm:ss.fffZ`),UI 直接打印中间 T + 时区后缀太丑。转换器把它解析成
/// `yyyy-MM-dd HH:mm:ss`(空格替 T,去时区后缀 + 亚秒精度)。
/// 失败 / null / 空 → 透传空字符串(UI 走 no-data 路径,不显 NaN)。
/// 用法:`{Binding UpdatedAt, Converter={StaticResource IsoDateTimeConverter}}`。
/// </summary>
public sealed class IsoDateTimeConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return s;  // 解析失败 → 原样输出(常见:某些 entry 数据脏,总比丢信息好)
        }
        return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("IsoDateTimeConverter is one-way only");

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}
