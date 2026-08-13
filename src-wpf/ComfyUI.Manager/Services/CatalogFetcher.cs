using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly AppLogger? _logger;

    public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60, AppLogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheTtlMinutes = cacheTtlMinutes;
        _logger = logger;
    }

    public virtual async Task<CatalogFetchResult> FetchAsync(
        string url,
        string? etag,
        string? lastModified,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("catalog-fetch", $"开始 fetch url={url} etag={etag != null}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (!string.IsNullOrEmpty(lastModified))
                req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // v0.6.14: HTTP cache — 304 short-circuit
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                var newEtag304 = TryGetHeader(resp, "ETag");
                var newLastMod304 = TryGetHeader(resp, "Last-Modified");
                _logger?.Info("catalog-fetch",
                    $"304 Not Modified url={url} duration_ms={sw.ElapsedMilliseconds}");
                return new CatalogFetchResult(
                    Is304: true, Entries: null,
                    NewEtag: newEtag304, NewLastModified: newLastMod304);
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            var rawArray = ExtractEntriesArray(root);

            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_cacheTtlMinutes);
            var entries = new List<CatalogEntry>();

            foreach (var element in rawArray.EnumerateArray())
            {
                string package = "";
                if (element.TryGetProperty("id", out var idProp))
                {
                    package = idProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("title", out var titleProp))
                {
                    package = titleProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("name", out var nameProp))
                {
                    package = nameProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package))
                {
                    continue;  // 跳过无 id/title/name 的 row
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

            var newEtag = TryGetHeader(resp, "ETag");
            var newLastMod = TryGetHeader(resp, "Last-Modified");
            _logger?.Info("catalog-fetch",
                $"完成 fetch count={entries.Count} duration_ms={sw.ElapsedMilliseconds} url={url}");
            return new CatalogFetchResult(
                Is304: false, Entries: entries,
                NewEtag: newEtag, NewLastModified: newLastMod);
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-fetch", $"fetch 失败 url={url}", ex);
            throw;
        }
    }

    /// <summary>
    /// 旧签名 wrapper:无 HTTP cache。保留向后兼容(老 caller/tests 用)。
    /// </summary>
    public virtual Task<CatalogFetchResult> FetchAsync(string url, CancellationToken ct = default)
        => FetchAsync(url, etag: null, lastModified: null, ct);

    private static string? TryGetHeader(HttpResponseMessage resp, string name)
    {
        // v0.6.14: 一些 header(如 Last-Modified)被 .NET 归类为 content header,实际
        // 存在 resp.Content.Headers 而不是 resp.Headers。两侧都查。
        if (resp.Headers.TryGetValues(name, out var vals))
            return vals.FirstOrDefault();
        if (resp.Content?.Headers.TryGetValues(name, out var cvals) == true)
            return cvals.FirstOrDefault();
        return null;
    }

    /// <summary>
    /// Extract the entries array from the JSON root. Supports:
    /// - 顶层 array(<c>[{{...}}]</c>)— 旧 fixture 格式
    /// - 顶层 object 含 <c>custom_nodes</c> 数组(<c>{{ "custom_nodes": [...] }}</c>)— 真实默认 URL 格式
    /// 其他顶层形状抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    private static JsonElement ExtractEntriesArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("custom_nodes", out var customNodes)
                && customNodes.ValueKind == JsonValueKind.Array)
            {
                return customNodes;
            }
            throw new InvalidOperationException(
                "Catalog JSON root object does not contain a 'custom_nodes' array. " +
                "Supported shapes: bare array, or { \"custom_nodes\": [...] }.");
        }
        throw new InvalidOperationException(
            $"Catalog JSON root must be array or object; got {root.ValueKind}.");
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