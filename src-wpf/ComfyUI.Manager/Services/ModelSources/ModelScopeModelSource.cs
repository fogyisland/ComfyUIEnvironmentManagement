using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.22.x:魔搭 ModelScope /api/v1/models fetcher。
/// Endpoint: <c>GET {baseUrl}/api/v1/models?PageNumber=N&amp;PageSize=M&amp;Search=q</c>。
/// Pagination: cursor=null = 第 1 页(传 PageNumber=1),否则 PageNumber=int(cursor)+1。
/// 末页 = (PageNumber * PageSize) >= TotalCount → nextCursor=null。
/// 2-round detail:列表 schema 不带 file size/url,需要 2 次请求:
///   1. SearchPageAsync 拉列表(快,N 条)
///   2. 串行 await N 次 GetModelDetailAsync(id) 拿 Revision[0].Files[0]
/// spec 决策:2-round 串行简单 + 单 entry 失败隔离(其他 entry 正常返);
/// N ≤ 20 接受 5-10 秒延迟,后续可改并行。
/// sort/period/baseModel/IncludeNsfw 接收但 no-op(API 无对应字段)。</summary>
public class ModelScopeModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    private readonly HttpProxyConfig? _proxy;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ModelSourceKind SourceKind => ModelSourceKind.ModelScope;
    public string DisplayName => "ModelScope";
    public bool IsEnabled { get; set; } = true;

    public ModelScopeModelSource(HttpClient http, string baseUrl, string apiToken,
        AppLogger? logger = null, HttpProxyConfig? proxy = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiToken = apiToken ?? "";
        _logger = logger;
        _proxy = proxy;
        if (_baseUrl != "https://www.modelscope.cn")
        {
            _logger?.Info("model-modelscope", $"using mirror: {_baseUrl}");
        }
    }

    /// <summary>SearchAsync(向后兼容):SearchPageAsync 的循环包装,直到 results.Count
    /// == maxResults 或 nextCursor=null。maxPages=10 硬上限防 runaway。
    /// progress 只在首次 page 报告 URL(progress=null 跳过 Report),镜像 CivitAI 模式。</summary>
    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults,
        CancellationToken ct, bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null)
    {
        var results = new List<ModelEntry>();
        string? cursor = null;
        const int maxPages = 10;
        for (var pageNum = 1; pageNum <= maxPages && results.Count < maxResults; pageNum++)
        {
            var (entries, nextCursor) = await SearchPageAsync(
                query, cursor, pageSize: 20, CivitAiSort.Newest, CivitAiPeriod.AllTime,
                ct, includeNsfw, baseModel,
                progress: pageNum == 1 ? progress : null);
            results.AddRange(entries);
            cursor = nextCursor;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return results.Take(maxResults).ToList();
    }

    /// <summary>UI 显式分页入口。cursor=null = 第 1 页(PageNumber=1)。
    /// 返回 (entries, 下一页 cursor — null 已无更多)。
    /// cursor 编码 = 0-based page index 的字符串;末页 = PageNumber*PageSize >= TotalCount。
    /// 失败:列表抛 HttpRequestException 由 aggregator 隔离;单 entry 详情失败仅丢该 entry。
    /// sort/period/baseModel/includeNsfw 接收但 no-op(API 无对应字段)。</summary>
    public async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct,
        bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null)
    {
        var pageNumber = string.IsNullOrEmpty(cursor) ? 1
            : (int.TryParse(cursor, out var n) ? n + 1 : 1);
        var url = BuildUrl(query, pageNumber, pageSize);
        var uri = new Uri(url);
        var proxyInfo = FormatProxyInfo(_proxy);
        progress?.Report($"[URL] {url}");
        progress?.Report($"[ModelScope] → {uri.Host}:{uri.Port} ({uri.Scheme.ToUpper()}, {proxyInfo})");
        _logger?.Info("model-modelscope", $"fetch page {pageNumber} query='{query}': {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        var sw = Stopwatch.StartNew();
        ModelScopeDtos.ModelsResponse? resp;
        try
        {
            var httpResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            progress?.Report($"[ModelScope] ← {(int)httpResp.StatusCode} {httpResp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");
            if (!httpResp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"ModelScope 返回 {(int)httpResp.StatusCode},body {body.Length} bytes,耗时 {sw.ElapsedMilliseconds}ms");
            }
            resp = JsonSerializer.Deserialize<ModelScopeDtos.ModelsResponse>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"ModelScope response JSON parse 失败: {ex.Message}", ex);
        }
        if (resp?.Data?.Model is not { } page)
        {
            // 空 envelope — 视为末页(让 SearchAsync 循环自然终止),而不是抛错污染 caller。
            progress?.Report($"[ModelScope] 空响应, 下一页: 无");
            return (Array.Empty<ModelEntry>(), null);
        }

        // 2-round:串行 await 每个 entry 的详情,拿 Revision[0].Files[0] 的 size + url
        var entries = new List<ModelEntry>(page.Models.Count);
        for (var i = 0; i < page.Models.Count; i++)
        {
            var item = page.Models[i];
            var (entry, versionList) = MapListItemToEntry(item);
            try
            {
                await FillEntryFromDetailAsync(entry, versionList, item.Id, ct);
            }
            catch (Exception ex)
            {
                // 单 entry 详情失败:entry 仍返,但 Versions[0].PrimaryDownloadUrl=null + SizeBytes=0
                _logger?.Warn("model-modelscope", $"detail fetch 失败 id={item.Id}: {ex.Message}");
                progress?.Report($"[ModelScope] ✗ id={item.Id} detail 失败: {ex.GetType().Name}");
                if (versionList.Count > 0)
                {
                    var placeholder = versionList[0];
                    versionList[0] = new ModelVersionEntry
                    {
                        Id = placeholder.Id,
                        Parent = placeholder.Parent,
                        Name = placeholder.Name,
                        BaseModel = placeholder.BaseModel,
                        PrimaryDownloadUrl = null!,
                        SizeBytes = 0,
                        Files = Array.Empty<ModelFile>(),
                    };
                }
            }
            entries.Add(entry);
        }
        var morePages = page.PageNumber * page.PageSize < page.TotalCount;
        var nextCursor = morePages ? page.PageNumber.ToString() : null;
        progress?.Report($"[ModelScope] ✓ {entries.Count} 项, 下一页: {(nextCursor is null ? "无" : "有")}");
        return (entries, nextCursor);
    }

    private string BuildUrl(string query, int pageNumber, int pageSize)
    {
        // Uri.EscapeDataString 跟 System.Web.HttpUtility.UrlEncode 行为对齐
        // (空格 → %20,中文 → %E4%B8%AD%E6%96%87),且免去 System.Web 引用。
        var q = Uri.EscapeDataString(query ?? "");
        return $"{_baseUrl}/api/v1/models?PageNumber={pageNumber}&PageSize={pageSize}&Search={q}";
    }

    private static (ModelEntry entry, List<ModelVersionEntry> versionList) MapListItemToEntry(ModelScopeDtos.ModelItem item)
    {
        var tags = item.Tags ?? new List<string>();
        var kind = MapTagsToKind(tags.ToArray());
        var entry = new ModelEntry
        {
            Source = ModelSourceKind.ModelScope,
            SourceId = item.Id.ToString(),
            // Title 优先用 ChineseName(空 fallback Name)— 用户中文体验
            Title = !string.IsNullOrWhiteSpace(item.ChineseName) ? item.ChineseName : item.Name,
            Author = item.Owner?.DisplayName ?? item.Owner?.Name ?? "",
            Kind = kind,
            NsfwKind = ModelNsfwKind.SFW,  // v0.6.22.x:API 无 NSFW 字段,默认 SFW
            Tags = tags,
            Description = item.Description ?? "",
            PreviewImageUrl = "",  // 列表 schema 无 preview URL
            BaseModel = "",  // API 无此字段
            DownloadCount = item.Downloads,
        };
        var versionList = new List<ModelVersionEntry>
        {
            new()
            {
                Parent = entry,
                Id = $"ModelScope:{item.Id}:{item.DefaultRevision}",
                Name = item.DefaultRevision,
                BaseModel = "",
                PrimaryDownloadUrl = null!,   // 由 2-round detail 填充
                SizeBytes = 0,
            }
        };
        return (WithVersions(entry, versionList), versionList);
    }

    /// <summary>ModelEntry.Versions 是 init-only IReadOnlyList;复制原 entry 的字段,
    /// 替换 Versions 为指定的可变 list。Test seam — 不暴露为 public API。</summary>
    private static ModelEntry WithVersions(ModelEntry src, List<ModelVersionEntry> versions)
    {
        return new ModelEntry
        {
            Source = src.Source,
            SourceId = src.SourceId,
            SourceUrl = src.SourceUrl,
            Title = src.Title,
            Description = src.Description,
            Author = src.Author,
            AuthorUrl = src.AuthorUrl,
            Kind = src.Kind,
            BaseModel = src.BaseModel,
            NsfwKind = src.NsfwKind,
            NsfwLevel = src.NsfwLevel,
            DownloadCount = src.DownloadCount,
            RatingCount = src.RatingCount,
            RatingStars = src.RatingStars,
            PublishedAt = src.PublishedAt,
            Tags = src.Tags,
            PreviewImageUrl = src.PreviewImageUrl,
            Versions = versions,
        };
    }

    /// <summary>Kind 推断表 — spec §"Kind 推断表"。Tag 大小写不敏感匹配。
    /// 多个 match 时按以下优先级(lora > checkpoint > 其他,避免 checkpoint 覆盖 lora)。
    /// internal static — 测试用反射调。</summary>
    internal static ModelKind MapTagsToKind(string[] tags)
    {
        if (tags is null || tags.Length == 0) return ModelKind.Other;
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("lora")) return ModelKind.LORA;
        if (set.Contains("hypernetwork")) return ModelKind.Hypernetwork;
        if (set.Contains("textual-inversion") || set.Contains("embeddings")) return ModelKind.TextualInversion;
        if (set.Contains("checkpoint")) return ModelKind.Checkpoint;
        if (set.Contains("unet")) return ModelKind.Other;
        if (set.Contains("text-encoder") || set.Contains("clip")) return ModelKind.Other;
        if (set.Contains("vae")) return ModelKind.VAE;
        if (set.Contains("controlnet")) return ModelKind.Controlnet;
        if (set.Contains("upscaler") || set.Contains("esrgan") || set.Contains("real-esrgan")) return ModelKind.Upscaler;
        return ModelKind.Other;
    }

    private async Task FillEntryFromDetailAsync(ModelEntry entry, List<ModelVersionEntry> versionList, long id,
        CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/v1/models/{id}";
        var httpResp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!httpResp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ModelScope detail 返回 {(int)httpResp.StatusCode},body {body.Length} bytes");
        }
        var detail = JsonSerializer.Deserialize<ModelScopeDtos.ModelDetailResponse>(body, JsonOpts);
        var firstFile = detail?.Data?.Revision?.FirstOrDefault()?.Files?.FirstOrDefault();
        if (firstFile is null || versionList.Count == 0) return;
        var placeholder = versionList[0];
        versionList[0] = new ModelVersionEntry
        {
            Id = placeholder.Id,
            Parent = placeholder.Parent,
            Name = placeholder.Name,
            BaseModel = placeholder.BaseModel,
            PrimaryDownloadUrl = firstFile.DownloadUrl,
            SizeBytes = firstFile.Size,
            Files = new List<ModelFile>
            {
                new()
                {
                    Name = firstFile.Name,
                    DownloadUrl = firstFile.DownloadUrl,
                    SizeBytes = firstFile.Size,
                    IsPrimary = true,
                },
            },
        };
    }

    private static string FormatProxyInfo(HttpProxyConfig? proxy)
    {
        if (proxy is null || !proxy.Enabled) return "直连";
        if (proxy.UseSystemProxy) return "系统代理";
        if (string.IsNullOrWhiteSpace(proxy.Url) || proxy.Port <= 0 || proxy.Port > 65535) return "直连(无效配置)";
        return $"代理={proxy.Url}:{proxy.Port}";
    }
}