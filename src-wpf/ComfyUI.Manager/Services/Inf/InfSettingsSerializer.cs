using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Inf;

/// <summary>
/// v1.0.0.1 (settings-to-inf):Settings POCO ↔ INF flat dict 的双向映射。
/// 基于 <see cref="JsonPropertyNameAttribute"/> 反射遍历 Settings 属性:
///   - 简单类型(string / bool / int / enum / nullable<这些>)→ ToString()
///   - 复杂类型(List / Dict / 其他 class)→ JSON-encode 到单值
///
/// 这样 settings.inf 里 ~95% 字段是原生 key=value(跟 sidebar.inf 同样风格),
/// 复杂字段(List<...> / Dict<...>)JSON-encode 到单值,parser 反序列化还原。
///
/// 复用现有 <see cref="JsonPropertyName"/> 命名(snake_case)作为 INF key ——
/// 已有的老 settings.json 用户字段名直接当 INF key,迁移无损。
/// </summary>
public static class InfSettingsSerializer
{
    /// <summary>序列化 Settings 用于写 INF。null 值字段跳过。</summary>
    public static IReadOnlyDictionary<string, string> SerializeToDict(Settings s)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var prop in EnumerateProps(typeof(Settings)))
        {
            var key = prop.JsonName!;
            var value = prop.Info.GetValue(s);
            if (value is null) continue;

            if (IsComplexType(prop.Info.PropertyType))
            {
                dict[key] = JsonSerializer.Serialize(value, prop.Info.PropertyType, JsonOpts);
            }
            else
            {
                dict[key] = value.ToString() ?? "";
            }
        }

        return dict;
    }

    /// <summary>应用 dict 到 <paramref name="s"/>。无法解析的字段跳过(保留 default)。</summary>
    public static void ApplyDictToSettings(Settings s, IReadOnlyDictionary<string, string> dict)
    {
        foreach (var prop in EnumerateProps(typeof(Settings)))
        {
            var key = prop.JsonName!;
            if (!dict.TryGetValue(key, out var raw)) continue;

            try
            {
                if (IsComplexType(prop.Info.PropertyType))
                {
                    var obj = JsonSerializer.Deserialize(raw, prop.Info.PropertyType, JsonOpts);
                    if (obj is not null) prop.Info.SetValue(s, obj);
                }
                else
                {
                    var targetType = Nullable.GetUnderlyingType(prop.Info.PropertyType) ?? prop.Info.PropertyType;
                    var obj = ConvertTo(raw, targetType);
                    if (obj is not null) prop.Info.SetValue(s, obj);
                }
            }
            catch
            {
                // 单字段坏数据 → 跳过,保留 default。Settings 是用户配置,不抛错更友好。
            }
        }
    }

    /// <summary>
    /// 判断是否"复杂类型" — List / Dict / 任何 class(string/bool/int/enum 之外)。
    /// </summary>
    private static bool IsComplexType(Type t)
    {
        if (t == typeof(string)) return false;
        if (t.IsEnum) return false;
        if (t.IsPrimitive) return false;
        if (Nullable.GetUnderlyingType(t) is Type ut && (ut.IsPrimitive || ut.IsEnum || ut == typeof(string)))
            return false;
        return true;
    }

    /// <summary>
    /// 把 raw string 转成目标类型。覆盖 string/bool/int/long/double/decimal/enum。
    /// </summary>
    private static object? ConvertTo(string raw, Type targetType)
    {
        if (targetType == typeof(string)) return raw;
        if (targetType == typeof(bool)) return bool.Parse(raw);
        if (targetType == typeof(int)) return int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(long)) return long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(decimal)) return decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType.IsEnum) return Enum.Parse(targetType, raw, ignoreCase: true);
        return Convert.ChangeType(raw, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>迭代 [JsonPropertyName] 标注的可读写属性。</summary>
    private static IEnumerable<(PropertyInfo Info, string? JsonName)> EnumerateProps(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                return (p, attr?.Name);
            })
            .Where(x => x.Name is not null);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 复杂值序列化进 INF 单值 — 不要 indent,否则 INF 文件会被嵌入多行 JSON。
        WriteIndented = false,
        // 枚举当字符串(JSON → 反序列化回来也认字符串)。
        Converters = { new JsonStringEnumConverter() },
    };
}