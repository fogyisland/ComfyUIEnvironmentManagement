using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// CatalogFetcher:HTTP GET 一个 catalog JSON URL,解析为 <see cref="CatalogEntry"/> 列表。
///
/// JSON 解析策略(宽松):
/// - 每个 row 必须有 <c>id</c> 或 <c>name</c> 字段;否则跳过该 row。
/// - <c>id</c> 优先,缺失则用 <c>name</c>;都缺跳过。
/// - 整个原始 row 序列化为 <see cref="CatalogEntry.RawMetadata"/>(后续 UI/服务可能用)。
///
/// 失败:
/// - HTTP 失败 / timeout → 抛 <see cref="HttpRequestException"/>(caller 处理)。
/// - 顶层 JSON 不是 array → 抛 <see cref="JsonException"/>。
/// </summary>
public class CatalogFetcher
{
    private readonly HttpClient _http;
    private readonly int _cacheTtlMinutes;

    public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheTtlMinutes = cacheTtlMinutes;
    }

    public virtual async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(url, ct);
        var rawArray = JsonSerializer.Deserialize<List<JsonElement>>(json)
            ?? new List<JsonElement>();

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_cacheTtlMinutes);
        var entries = new List<CatalogEntry>();

        foreach (var element in rawArray)
        {
            string package = "";
            if (element.TryGetProperty("id", out var idProp))
            {
                package = idProp.GetString() ?? "";
            }
            if (string.IsNullOrEmpty(package) &&
                element.TryGetProperty("name", out var nameProp))
            {
                package = nameProp.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;  // 跳过无 id/name 的 row
            }

            var rawMeta = ParseRawMetadata(element);

            entries.Add(new CatalogEntry
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = url,
                Package = package,
                RawMetadata = rawMeta,
                CachedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ExpiresAt = expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
        }

        return entries;
    }

    /// <summary>
    /// Recursively convert a <see cref="JsonElement"/> into native CLR types so that
    /// downstream consumers see <see cref="string"/>, <see cref="double"/>, <see cref="bool"/>,
    /// <see cref="Dictionary{TKey,TValue}"/>, or <see cref="List{T}"/> — not <c>JsonElement</c>.
    /// </summary>
    private static Dictionary<string, object?> ParseRawMetadata(JsonElement element)
    {
        var result = new Dictionary<string, object?>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = ConvertJsonValue(prop.Value);
        }
        return result;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var l)) return l;
                return value.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>();
                foreach (var prop in value.EnumerateObject())
                {
                    obj[prop.Name] = ConvertJsonValue(prop.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in value.EnumerateArray())
                {
                    list.Add(ConvertJsonValue(item));
                }
                return list;
            default:
                return null;
        }
    }
}