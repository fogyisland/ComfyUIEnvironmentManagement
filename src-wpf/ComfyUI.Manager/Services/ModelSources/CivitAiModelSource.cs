using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:CivitAI Models API fetcher.
/// Endpoint: https://civitai.com/api/v1/models?limit=100&amp;page=N&amp;nsfw=true&amp;sort=Newest
/// Pagination: 走 "metadata.nextPage" cursor 直到 null。
/// nsfw=true 全部拉回来,UI badge 区分 NSFW/Mature/SFW。</summary>
public class CivitAiModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private const int PageSize = 100;
    private readonly string _baseUrl;

    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;
    public string DisplayName => "CivitAI";
    public bool IsEnabled { get; set; } = true;

    public CivitAiModelSource(HttpClient http, string baseUrl, AppLogger? logger = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
        if (baseUrl != "https://civitai.com")
        {
            _logger?.Info("model-civitai", $"using mirror: {baseUrl}");
        }
    }

    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        // v0.6.22+:改成 SearchPageAsync 的循环包装 — 保留向后兼容(老 service code 仍可用)。
        // SearchAsync 是无 UI 上下文调用,使用 CivitAI 默认 Newest + AllTime(对 HF 无意义)。
        var results = new List<ModelEntry>();
        string? cursor = null;
        const int maxPages = 10;  // hard cap to prevent runaway

        for (var pageCount = 1; pageCount <= maxPages && results.Count < maxResults; pageCount++)
        {
            var (entries, nextCursor) = await SearchPageAsync(
                query, cursor, PageSize, CivitAiSort.Newest, CivitAiPeriod.AllTime, ct);
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
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct)
    {
        var url = BuildUrl(query, cursor, pageSize, sort, period);
        var cursorLabel = string.IsNullOrEmpty(cursor) ? "(none)" : cursor;
        _logger?.Info("model-civitai", $"fetch page cursor={cursorLabel} sort={sort} period={period}: {url}");

        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var page = JsonSerializer.Deserialize<CivitAiPage>(body, JsonOpts);
        if (page?.Items is null || page.Items.Count == 0)
            return (Array.Empty<ModelEntry>(), null);

        var entries = new List<ModelEntry>(page.Items.Count);
        foreach (var item in page.Items)
        {
            var entry = MapItemToEntry(item);
            if (entry is not null) entries.Add(entry);
        }

        return (entries, page.Metadata?.NextPage);
    }

    private string BuildUrl(string query, string? cursor, int pageSize,
                            CivitAiSort sort, CivitAiPeriod period)
    {
        var qs = new List<string>
        {
            $"limit={pageSize}",
            $"sort={sort}",         // v0.6.22+:enum 名 = API value ("Newest" / "Most Downloaded" …)
            "nsfw=true",            // 全部拉回来,UI 分类
            $"period={period}",     // v0.6.22+:时间窗 ("AllTime" / "Year" / "Month" …)
        };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"query={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrEmpty(cursor)) qs.Add($"page={Uri.EscapeDataString(cursor)}");
        // v0.6.22+ T7+ fix:_baseUrl 只到 host(offcial="https://civitai.com" 或用户镜像 URL),
        // v0.6.21 T2 commit 350d31f 改 ctor 注入 baseUrl 时漏加 /api/v1/models path,导致
        // 请求 URL = "https://civitai.com?limit=100..."(首页 + qs)而非 API endpoint,
        // 返 HTML 首页 + JSON parse 报错。trim trailing slash 是为兼容镜像 URL "https://x.com/"
        // (MirrorUrl 默认值 "https://hf-mirror.com" 已是无 slash,但用户手填可能带)。
        return $"{_baseUrl.TrimEnd('/')}/api/v1/models?{string.Join("&", qs)}";
    }

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
