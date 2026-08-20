using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:CivitAI Models API fetcher.
/// Endpoint: https://civitai.com/api/v1/models?limit=100&amp;page=N&amp;nsfw=true&amp;sort=Newest
/// Pagination: 走 "metadata.nextPage" cursor 直到 null。
/// nsfw=true 全部拉回来,UI badge 区分 NSFW/Mature/SFW。
/// v0.6.22+:apiToken — Authorization: Bearer 注入所有 API 请求(用户 2026-08-20
/// 反馈"受限模型 / 敏感标记模型返 401/403")。镜像 HuggingFaceModelSource 模式:
/// 仅在 HTTPS baseUrl 下注入(防 token 通过 HTTP 镜像泄露)。</summary>
public class CivitAiModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private const int PageSize = 100;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    // v0.6.22+:factory 透传的 proxy 配置 — 仅用于 Console 调试日志显示,
    // 不参与 HTTP 请求(实际 proxy 由 HttpClientHandler.ApplyTo 决定)。
    private readonly HttpProxyConfig? _proxy;

    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;
    public string DisplayName => "CivitAI";
    public bool IsEnabled { get; set; } = true;

    public CivitAiModelSource(HttpClient http, string baseUrl, string apiToken, AppLogger? logger = null, HttpProxyConfig? proxy = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _apiToken = apiToken ?? "";
        _logger = logger;
        _proxy = proxy;
        if (baseUrl != "https://civitai.com")
        {
            _logger?.Info("model-civitai", $"using mirror: {baseUrl}");
        }
    }

    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct, bool includeNsfw = true, string? baseModel = null, IProgress<string>? progress = null)
    {
        // v0.6.22+:改成 SearchPageAsync 的循环包装 — 保留向后兼容(老 service code 仍可用)。
        // SearchAsync 是无 UI 上下文调用,使用 CivitAI 默认 Newest + AllTime(对 HF 无意义)。
        // includeNsfw / baseModel 透传到 SearchPageAsync。
        // progress(0.6.22+)透传给 SearchPageAsync — 只在首次 page 报告 URL,避免重复刷屏
        // (循环内的 next page 调 progress=null 跳过 Report)。
        var results = new List<ModelEntry>();
        string? cursor = null;
        const int maxPages = 10;  // hard cap to prevent runaway

        for (var pageCount = 1; pageCount <= maxPages && results.Count < maxResults; pageCount++)
        {
            var (entries, nextCursor) = await SearchPageAsync(
                query, cursor, PageSize, CivitAiSort.Newest, CivitAiPeriod.AllTime, ct, includeNsfw, baseModel,
                progress: pageCount == 1 ? progress : null);
            results.AddRange(entries);
            cursor = nextCursor;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return results.Take(maxResults).ToList();
    }

    /// <summary>
    /// v0.6.22+:UI 显式分页入口 — 单页 fetch + 返回 nextCursor。
    /// cursor=null = 第一页(不传 page 参数),否则 page={cursor} 让 CivitAI 续接。
    /// 返回 (本页 entries, 下一页 cursor — null 已无更多)。
    /// 失败(网络/JSON 错)直接抛出,由 aggregator 隔离。
    /// sort/period 参数透传给 API(用户 2026-08-20 反馈"搜索似乎只传关键词")。
    /// </summary>
    public async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct,
        bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null)
    {
        var url = BuildUrl(query, cursor, pageSize, sort, period, includeNsfw, baseModel);
        var cursorLabel = string.IsNullOrEmpty(cursor) ? "(none)" : cursor;
        var proxyInfo = FormatProxyInfo(_proxy);
        // v0.6.22+:report URL via progress sink — visible in VM Console panel,
        // 给用户"我真的把 baseModels=SDXL 1.0 传到 API 了"的可见证据(用户 2026-08-20
        // 反馈"感觉还是筛选,并没有将模型类型传递给 search api")。
        progress?.Report($"[URL] {url}");
        // v0.6.22++:rich debug log — proxy / port / status / bytes / duration / item count
        // (用户 2026-08-20 反馈"是否通过代理连接,连接的端口返回值,基本上结果最好都
        // 能显示" — 深 debug 信息)。
        var uri = new Uri(url);
        progress?.Report($"[CivitAI] → {uri.Host}:{uri.Port} ({uri.Scheme.ToUpper()}, {proxyInfo})");
        _logger?.Info("model-civitai", $"fetch page cursor={cursorLabel} sort={sort} period={period} nsfw={includeNsfw} bm={baseModel}: {url}");

        // v0.6.22+:per-request Authorization: Bearer(token 跟 HuggingFaceModelSource 同模式)。
        // 仅 HTTPS baseUrl 注入 — 防 HTTP 镜像明文泄露 token。
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        // v0.6.22++:stopwatch 包住 Send + Read 报告耗时,try/catch 让异常也走 progress
        // 报告错误细节(用户要"返回值"—— 失败时也明确告知,而不只是异常冒泡到 aggregator)。
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            progress?.Report($"[CivitAI] ← {(int)resp.StatusCode} {resp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");

            if (!resp.IsSuccessStatusCode)
            {
                // 失败但有响应体(HTML 错误页 / JSON 错误)— 让 user 看到 body 长度 + status code
                throw new HttpRequestException(
                    $"CivitAI 返回 {(int)resp.StatusCode} {resp.StatusCode},body {body.Length} bytes,耗时 {sw.ElapsedMilliseconds}ms");
            }

            var page = JsonSerializer.Deserialize<CivitAiPage>(body, JsonOpts);
            if (page?.Items is null || page.Items.Count == 0)
            {
                progress?.Report($"[CivitAI] 空结果集, 下一页: 无");
                return (Array.Empty<ModelEntry>(), null);
            }

            var entries = new List<ModelEntry>(page.Items.Count);
            foreach (var item in page.Items)
            {
                var entry = MapItemToEntry(item);
                if (entry is not null) entries.Add(entry);
            }
            progress?.Report($"[CivitAI] ✓ {entries.Count} 项, 下一页: {(page.Metadata?.NextPage is null ? "无" : "有")}");
            return (entries, page.Metadata?.NextPage);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            progress?.Report($"[CivitAI] ⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            progress?.Report($"[CivitAI] ✗ {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            throw;
        }
    }

    /// <summary>把 HttpProxyConfig 格式化成单行可读字符串,用于 Console 调试日志。
    /// null → "直连" / UseSystemProxy → "系统代理" / else "代理={Url}:{Port}"。
    /// 跟 <see cref="HttpProxyConfig.ApplyTo"/> 三种分支一一对应,user 看 log 立即知道走哪种 mode。</summary>
    internal static string FormatProxyInfo(HttpProxyConfig? proxy)
    {
        if (proxy is null || !proxy.Enabled) return "直连";
        if (proxy.UseSystemProxy) return "系统代理";
        if (string.IsNullOrWhiteSpace(proxy.Url) || proxy.Port <= 0 || proxy.Port > 65535) return "直连(无效配置)";
        return $"代理={proxy.Url}:{proxy.Port}";
    }

    private string BuildUrl(string query, string? cursor, int pageSize,
                            CivitAiSort sort, CivitAiPeriod period, bool includeNsfw, string? activeBaseModel)
    {
        var qs = new List<string>
        {
            $"limit={pageSize}",
            $"sort={sort}",         // v0.6.22+:enum 名 = API value ("Newest" / "Most Downloaded" …)
            $"nsfw={(includeNsfw ? "true" : "false")}",  // v0.6.22+:用户反馈"因为我们就需要完整的非NSFW数据" — NSFW 不光做筛选,还要做 API 透传。false 时 API 只返 Level 1(无 NSFW 内容)
            $"period={period}",     // v0.6.22+:时间窗 ("AllTime" / "Year" / "Month" …)
        };
        // v0.6.22+:baseModel 智能识别 — query 里的 "stable diffusion 1.5" / "sdxl" /
        // "flux pony" 等会映射到 CivitAI `baseModels=` filter 并从 query 里剥掉。
        // 根因:/api/v1/models?query=X 只在 name/tags/description 做 LIKE,完全
        // 不搜 baseModel 字段 → 网页侧栏用 Elasticsearch 全文匹配的结果数远超我们。
        // 加上 baseModels filter 后 + 把已识别的 keyword 从 query 剥掉(避免双层 narrow),
        // "stable diffusion 1.5" 命中数从 20+ → 几千。
        var (strippedQuery, detectedBaseModels) = DetectBaseModels(query);
        // v0.6.22+:合并 VM-supplied activeBaseModel 与 query-detected,用 HashSet 去重
        // (同 baseModel value 不重复出现)。activeBaseModel=null/空/"All" 跳过 —
        // 给用户"不过滤"的回退选项。
        var baseModels = new List<string>(detectedBaseModels);
        var seen = new HashSet<string>(detectedBaseModels, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeBaseModel) && !string.Equals(activeBaseModel, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add(activeBaseModel)) baseModels.Add(activeBaseModel);
        }
        if (baseModels.Count > 0)
        {
            qs.Add($"baseModels={Uri.EscapeDataString(string.Join(",", baseModels))}");
            _logger?.Info("model-civitai",
                $"检测到 base model 过滤: {string.Join(", ", baseModels)}; 剩余关键词: '{strippedQuery}'");
        }
        if (!string.IsNullOrWhiteSpace(strippedQuery)) qs.Add($"query={Uri.EscapeDataString(strippedQuery)}");
        if (!string.IsNullOrEmpty(cursor)) qs.Add($"page={Uri.EscapeDataString(cursor)}");
        // v0.6.22+ T7+ fix:_baseUrl 只到 host(offcial="https://civitai.com" 或用户镜像 URL),
        // v0.6.21 T2 commit 350d31f 改 ctor 注入 baseUrl 时漏加 /api/v1/models path,导致
        // 请求 URL = "https://civitai.com?limit=100..."(首页 + qs)而非 API endpoint,
        // 返 HTML 首页 + JSON parse 报错。trim trailing slash 是为兼容镜像 URL "https://x.com/"
        // (MirrorUrl 默认值 "https://hf-mirror.com" 已是无 slash,但用户手填可能带)。
        return $"{_baseUrl.TrimEnd('/')}/api/v1/models?{string.Join("&", qs)}";
    }

    /// <summary>
    /// v0.6.22+:识别用户 query 里的基础模型关键词,返回要附加到 <c>baseModels=</c> filter
    /// 的值 + 把已识别 keyword 从 query 剥掉剩下的文本(用作 <c>query=</c> 做 name/tag/desc LIKE)。
    /// 多个 keyword 同时识别 → 用逗号分隔多选(API 是 OR 语义)。
    /// 用 \b word boundary 防止 "cssd 1.5" 误匹配 "sd 1.5"、"stable diffusion 1.5x" 误匹配 "sd 1.5"。
    /// </summary>
    internal static (string StrippedQuery, IReadOnlyList<string> BaseModels) DetectBaseModels(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return (query ?? "", Array.Empty<string>());

        var q = query;
        var matched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 数组里较长 / 更具体的 keyword 必须排在前面 — 避免 "stable diffusion 3" 抢先
        // 匹配掉 "stable diffusion 3.5"。同一 baseModel 只识别一次(seen set)。
        foreach (var (kw, bm) in BaseModelAliases)
        {
            if (seen.Contains(bm)) continue;
            var pattern = $@"\b{Regex.Escape(kw)}\b";
            if (Regex.IsMatch(q, pattern, RegexOptions.IgnoreCase))
            {
                seen.Add(bm);
                matched.Add(bm);
                // Strip keyword + 周围空白(留下单空格让后续 Regex \s+ collapse)
                q = Regex.Replace(q, $@"\s*{Regex.Escape(kw)}\s*", " ",
                    RegexOptions.IgnoreCase);
            }
        }

        // 折叠剥 keyword 留下的多余空白
        q = Regex.Replace(q, @"\s+", " ").Trim();
        return (q, matched);
    }

    /// <summary>
    /// 用户输入 keyword → CivitAI baseModel 值的映射表。
    /// 顺序敏感:更具体 / 更长的 keyword 排前面(见 <see cref="DetectBaseModels"/> 注释)。
    /// 漏掉的 baseModel 加一行就行 — baseModel 值是 CivitAI 后台枚举控制的,基本稳定。
    /// </summary>
    private static readonly (string Keyword, string BaseModel)[] BaseModelAliases =
    {
        ("stable diffusion 3.5 large", "SD 3.5 Large"),
        ("stable diffusion 3.5 medium", "SD 3.5 Medium"),
        ("stable diffusion 3.5", "SD 3.5"),
        ("sd 3.5 large", "SD 3.5 Large"),
        ("sd 3.5 medium", "SD 3.5 Medium"),
        ("sd 3.5", "SD 3.5"),
        ("sd3.5", "SD 3.5"),
        ("stable diffusion 3", "SD 3"),
        ("sd 3", "SD 3"),
        ("stable diffusion 1.5", "SD 1.5"),
        ("sd 1.5", "SD 1.5"),
        ("sd1.5", "SD 1.5"),
        ("stable diffusion 1.4", "SD 1.4"),
        ("sd 1.4", "SD 1.4"),
        ("sd1.4", "SD 1.4"),
        ("stable diffusion 2.1", "SD 2.1"),
        ("sd 2.1", "SD 2.1"),
        ("sd2.1", "SD 2.1"),
        ("stable diffusion 2.0", "SD 2.0"),
        ("sd 2.0", "SD 2.0"),
        ("sd2.0", "SD 2.0"),
        ("stable diffusion xl", "SDXL 1.0"),
        ("sdxl 1.0", "SDXL 1.0"),
        ("sdxl", "SDXL 1.0"),
        ("sdxl 0.9", "SDXL 0.9"),
        ("flux.1 schnell", "Flux.1 Schnell"),
        ("flux schnell", "Flux.1 Schnell"),
        ("flux.1 s", "Flux.1 Schnell"),
        ("flux.1 dev", "Flux.1 D"),
        ("flux.1 d", "Flux.1 D"),
        ("flux dev", "Flux.1 D"),
        ("flux", "Flux.1 D"),  // 默认 flux = dev,用户可细化写 "flux schnell" 选 Schnell
        ("pony v6 xl", "Pony V6 XL"),
        ("pony v6", "Pony V6 XL"),
        ("pony", "Pony"),
        ("stable cascade", "Stable Cascade"),
        ("cascade", "Stable Cascade"),
        ("hidream", "HiDream"),
        ("kolors", "Kolors"),
        ("wan video", "Wan Video"),
        ("wan 2.1", "Wan Video"),
        ("wan", "Wan Video"),
        ("hunyuan video", "Hunyuan Video"),
        ("hunyuan", "Hunyuan Video"),
        ("cogvideox", "CogVideoX"),
        ("ltxv", "LTXV"),
        ("mochi", "Mochi"),
        ("pixart", "Pixart"),
        ("auraflow", "AuraFlow"),
    };

    private static ModelEntry? MapItemToEntry(CivitAiItem item)
    {
        if (item.Id is null || string.IsNullOrEmpty(item.Name)) return null;

        var versions = new List<ModelVersionEntry>();
        if (item.ModelVersions is not null)
        {
            foreach (var v in item.ModelVersions)
            {
                if (v.Id is null || v.Files is null || v.Files.Count == 0) continue;

                var files = v.Files.Select(f => new ModelFile
                {
                    Name = f.Name ?? "",
                    Format = f.Format ?? "Other",
                    SizeBytes = (long)((f.SizeKB ?? 0) * 1024L),
                    DownloadUrl = f.DownloadUrl ?? "",
                    IsPrimary = f.Primary == true,
                }).ToList();

                var primary = files.FirstOrDefault(f => f.IsPrimary) ?? files.First();
                versions.Add(new ModelVersionEntry
                {
                    Id = $"{ModelSourceKind.CivitAi}:{item.Id}:{v.Id}",
                    Parent = null!,  // set below
                    SourceVersionId = v.Id.ToString() ?? "",
                    Name = v.Name ?? $"v{v.Id}",
                    BaseModel = v.BaseModel,
                    SizeBytes = primary.SizeBytes,
                    PrimaryDownloadUrl = primary.DownloadUrl,
                    Files = files,
                    PublishedAt = v.PublishedAt,
                    IsEarlyAccess = v.EarlyAccessEnabled == true,
                });
            }
        }

        // First version's first image as preview
        var preview = item.ModelVersions?.FirstOrDefault()?.Images?.FirstOrDefault()?.Url;

        var entry = new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = item.Id.ToString() ?? "",
            SourceUrl = $"https://civitai.com/models/{item.Id}",
            Title = item.Name,
            Description = item.Description,
            Author = item.Creator?.Username,
            AuthorUrl = item.Creator?.Link,
            Kind = ModelKindExtensions.ParseKind(item.Type),
            BaseModel = item.ModelVersions?.FirstOrDefault()?.BaseModel,
            NsfwKind = ModelKindExtensions.ParseNsfwKind(item.NsfwLevel, item.Nsfw),
            NsfwLevel = item.NsfwLevel,
            DownloadCount = item.Stats?.DownloadCount,
            RatingCount = item.Stats?.RatingCount,
            RatingStars = item.Stats?.Rating,
            PublishedAt = item.PublishedAt,
            Tags = item.Tags ?? new List<string>(),
            PreviewImageUrl = preview,
            Versions = versions,
        };

        // Backfill Parent ref (can't do in init since entry not yet constructed)
        for (var i = 0; i < versions.Count; i++)
        {
            versions[i] = new ModelVersionEntry
            {
                Id = versions[i].Id,
                Parent = entry,
                SourceVersionId = versions[i].SourceVersionId,
                Name = versions[i].Name,
                BaseModel = versions[i].BaseModel,
                SizeBytes = versions[i].SizeBytes,
                PrimaryDownloadUrl = versions[i].PrimaryDownloadUrl,
                Files = versions[i].Files,
                PublishedAt = versions[i].PublishedAt,
                IsEarlyAccess = versions[i].IsEarlyAccess,
            };
        }

        return entry;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // DTO classes for CivitAI JSON (private)
    private class CivitAiPage
    {
        [JsonPropertyName("items")] public List<CivitAiItem>? Items { get; set; }
        [JsonPropertyName("metadata")] public CivitAiMetadata? Metadata { get; set; }
    }
    private class CivitAiMetadata
    {
        [JsonPropertyName("nextPage")] public string? NextPage { get; set; }
    }
    private class CivitAiItem
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
        [JsonPropertyName("nsfwLevel")] public int? NsfwLevel { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("stats")] public CivitAiStats? Stats { get; set; }
        [JsonPropertyName("creator")] public CivitAiCreator? Creator { get; set; }
        [JsonPropertyName("modelVersions")] public List<CivitAiVersion>? ModelVersions { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
    }
    private class CivitAiStats
    {
        [JsonPropertyName("downloadCount")] public int? DownloadCount { get; set; }
        [JsonPropertyName("ratingCount")] public int? RatingCount { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
    }
    private class CivitAiCreator
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
    }
    private class CivitAiVersion
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
        [JsonPropertyName("files")] public List<CivitAiFile>? Files { get; set; }
        [JsonPropertyName("images")] public List<CivitAiImage>? Images { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("earlyAccessEnabled")] public bool? EarlyAccessEnabled { get; set; }
    }
    private class CivitAiFile
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("sizeKB")] public double? SizeKB { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("primary")] public bool? Primary { get; set; }
    }
    private class CivitAiImage
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
