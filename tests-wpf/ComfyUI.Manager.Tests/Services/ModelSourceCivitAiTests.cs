using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceCivitAiTests
{
    private static HttpClient CreateClient(DelegatingHandlerStub handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("https://civitai.com/") };
    }

    [Fact]
    public async Task SearchAsync_ValidJson_ReturnsParsedEntries()
    {
        var json = """
        {
          "items": [
            {
              "id": 12345,
              "name": "Realistic Vision",
              "type": "Checkpoint",
              "nsfw": false,
              "nsfwLevel": 0,
              "tags": ["portrait"],
              "stats": {"downloadCount": 1000, "ratingCount": 50, "rating": 4.5},
              "creator": {"username": "AuthorA"},
              "modelVersions": [
                {
                  "id": 67890,
                  "name": "v5.0 fp16",
                  "baseModel": "SD 1.5",
                  "files": [
                    {"name": "model.safetensors", "format": "Safe Tensor", "sizeKB": 6789012, "downloadUrl": "https://cdn.example.com/a.safetensors", "primary": true}
                  ],
                  "images": [{"url": "https://cdn.example.com/preview.jpg"}],
                  "publishedAt": "2026-08-01T00:00:00.000Z",
                  "earlyAccessEnabled": false
                }
              ]
            }
          ],
          "metadata": {"nextPage": null}
        }
        """;
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "") { IsEnabled = true };

        var entries = await source.SearchAsync("", maxResults: 50, ct: default);

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("12345", e.SourceId);
        Assert.Equal("Realistic Vision", e.Title);
        Assert.Equal(ModelKind.Checkpoint, e.Kind);
        Assert.Equal(ModelNsfwKind.SFW, e.NsfwKind);
        Assert.Equal("AuthorA", e.Author);
        Assert.Equal(1000, e.DownloadCount);
        Assert.Equal(4.5, e.RatingStars);
        Assert.Single(e.Versions);
        var v = e.Versions[0];
        Assert.Equal("CivitAi:12345:67890", v.Id);
        Assert.Equal("v5.0 fp16", v.Name);
        Assert.Equal("SD 1.5", v.BaseModel);
        Assert.Equal(6789012L * 1024, v.SizeBytes);
        Assert.Equal("https://cdn.example.com/a.safetensors", v.PrimaryDownloadUrl);
        Assert.False(v.IsEarlyAccess);
        Assert.Single(v.Files);
        Assert.True(v.Files[0].IsPrimary);
        Assert.Equal("Safe Tensor", v.Files[0].Format);
        Assert.Equal("https://cdn.example.com/preview.jpg", e.PreviewImageUrl);
    }

    // —— v0.6.22+ baseModel 智能识别 ——

    [Fact]
    public void DetectBaseModels_StableDiffusion15_StripsKeywordAndReturnsFilter()
    {
        // 用户输入 "stable diffusion 1.5" → baseModels=SD 1.5 filter;query 剥空(原 keyword 已生效)。
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("stable diffusion 1.5");
        Assert.Equal("", stripped);
        Assert.Equal(new[] { "SD 1.5" }, bases);
    }

    [Fact]
    public void DetectBaseModels_StableDiffusion15Lora_KeepsLoraAsQuery()
    {
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("stable diffusion 1.5 lora");
        Assert.Equal("lora", stripped);
        Assert.Equal(new[] { "SD 1.5" }, bases);
    }

    [Fact]
    public void DetectBaseModels_MultipleKeywords_ReturnsMultipleFilters()
    {
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("sdxl pony checkpoint");
        Assert.Equal("checkpoint", stripped);
        Assert.Equal(new[] { "SDXL 1.0", "Pony" }, bases);
    }

    [Fact]
    public void DetectBaseModels_NoKeyword_ReturnsOriginalQuery()
    {
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("realistic vision v5");
        Assert.Equal("realistic vision v5", stripped);
        Assert.Empty(bases);
    }

    [Fact]
    public void DetectBaseModels_EmptyAndNull_ReturnsEmpty()
    {
        Assert.Equal(("", System.Array.Empty<string>()), CivitAiModelSource.DetectBaseModels(""));
        Assert.Equal(("", System.Array.Empty<string>()), CivitAiModelSource.DetectBaseModels(null!));
    }

    [Fact]
    public void DetectBaseModels_WordBoundary_DoesNotFalseMatch()
    {
        // "cssd 1.5" 不该匹配 "sd 1.5" — \b 防止子串误命中
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("cssd 1.5");
        Assert.Equal("cssd 1.5", stripped);
        Assert.Empty(bases);

        // "stable diffusion 1.5x" 不该匹配 "stable diffusion 1.5"
        var (stripped2, bases2) = CivitAiModelSource.DetectBaseModels("stable diffusion 1.5x");
        Assert.Equal("stable diffusion 1.5x", stripped2);
        Assert.Empty(bases2);
    }

    [Fact]
    public void DetectBaseModels_CaseInsensitive_MatchesAnyCase()
    {
        var (_, bases) = CivitAiModelSource.DetectBaseModels("STABLE DIFFUSION 1.5");
        Assert.Equal(new[] { "SD 1.5" }, bases);

        var (_, bases2) = CivitAiModelSource.DetectBaseModels("Stable Diffusion 1.5");
        Assert.Equal(new[] { "SD 1.5" }, bases2);
    }

    [Fact]
    public void DetectBaseModels_MoreSpecificFirst_DoesNotShadow()
    {
        // "stable diffusion 3.5" 必须先于 "stable diffusion 3" 匹配,避免 3.5 被错认成 3。
        var (stripped, bases) = CivitAiModelSource.DetectBaseModels("stable diffusion 3.5 large lora");
        Assert.Equal("lora", stripped);
        Assert.Equal(new[] { "SD 3.5 Large" }, bases);
    }

    [Fact]
    public async Task SearchAsync_WithBaseModelKeyword_AppendsBaseModelsFilter()
    {
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("stable diffusion 1.5 lora", 50, default);

        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        // Uri.ToString() 解码 %20 回空格 — 既校验百分号编码形式,也校验裸空格形式(.NET Uri 行为)。
        Assert.True(url.Contains("baseModels=SD 1.5") || url.Contains("baseModels=SD%201.5"),
            $"URL 应包含 baseModels=SD 1.5,实际: {url}");
        Assert.True(url.Contains("query=lora") || url.Contains("query=lora"),
            $"URL 应包含 query=lora,实际: {url}");
        Assert.DoesNotContain("stable", url);
    }

    [Fact]
    public async Task SearchAsync_WithoutBaseModelKeyword_OmitsBaseModelsFilter()
    {
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("realistic vision", 50, default);

        var req = Assert.Single(handler.Requests);
        Assert.DoesNotContain("baseModels=", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchAsync_ActiveBaseModel_AppendsBaseModelsFilter()
    {
        // v0.6.22+:用户 2026-08-20 反馈"模型参数是不是也可以传递?也就是 base model
        // 列出常规可用的 Model 类型"。VM chip 选 SDXL_1_0 → SearchAsync baseModel="SDXL 1.0"
        // → API URL 包含 baseModels=SDXL+1.0。
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("realistic", 50, default, true, "SDXL 1.0");

        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        Assert.True(url.Contains("baseModels=SDXL 1.0") || url.Contains("baseModels=SDXL%201.0"),
            $"URL 应包含 baseModels=SDXL 1.0,实际: {url}");
        Assert.True(url.Contains("query=realistic"), $"URL 应保留 query=realistic,实际: {url}");
    }

    [Fact]
    public async Task SearchAsync_ActiveBaseModel_MergedWithQueryDetected()
    {
        // v0.6.22+:query 内 "sd 1.5" 自动识别 + activeBaseModel "Pony V6 XL" → 两者合并
        // baseModels=SD 1.5,Pony V6 XL(API OR 语义),query 已剥除 sd 1.5 关键字。
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("sd 1.5 lora", 50, default, true, "Pony V6 XL");

        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        // 两个 baseModel 都应在 URL 里(逗号分隔,CivitAI 多选 OR)
        Assert.Contains("baseModels=", url);
        Assert.True(url.Contains("SD 1.5") || url.Contains("SD%201.5"),
            $"URL 应包含 SD 1.5,实际: {url}");
        Assert.True(url.Contains("Pony V6 XL") || url.Contains("Pony%20V6%20XL"),
            $"URL 应包含 Pony V6 XL,实际: {url}");
        // query 内 "sd 1.5" 已剥掉,只剩 "lora"
        Assert.Contains("query=lora", url);
        Assert.DoesNotContain("sd%201.5", url);  // 不应再在 query 里出现
    }

    [Fact]
    public async Task SearchAsync_ActiveBaseModelSameAsQueryDetected_DedupesInUrl()
    {
        // v0.6.22+:query 内 "sd 1.5" + activeBaseModel "SD 1.5" 同值 → URL 里 baseModels 只出现一次。
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("sd 1.5 lora", 50, default, true, "SD 1.5");

        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        // 1 个 baseModels= 参数(同值去重),不应重复两次
        var occurrences = System.Text.RegularExpressions.Regex.Matches(url, "baseModels=").Count;
        Assert.Equal(1, occurrences);
        Assert.True(url.Contains("SD 1.5") || url.Contains("SD%201.5"),
            $"URL 应包含 SD 1.5,实际: {url}");
    }

    [Fact]
    public async Task SearchAsync_ActiveBaseModelNull_OnlyQueryDetectionUsed()
    {
        // activeBaseModel=null → 只用 query 自动识别(SD 1.5)。
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("sd 1.5 lora", 50, default, true, null);

        var req = Assert.Single(handler.Requests);
        var url = req.RequestUri!.ToString();
        Assert.True(url.Contains("baseModels=SD 1.5") || url.Contains("baseModels=SD%201.5"),
            $"URL 应包含 baseModels=SD 1.5,实际: {url}");
        Assert.Contains("query=lora", url);
    }

    [Fact]
    public async Task SearchAsync_NsfwLevel2_ParsedAsMature()
    {
        var json = """{"items": [{"id": 1, "name": "Mature Model", "type": "LORA", "nsfwLevel": 2, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelNsfwKind.Mature, entries[0].NsfwKind);
    }

    [Fact]
    public async Task SearchAsync_NsfwLevel3_ParsedAsNSFW()
    {
        var json = """{"items": [{"id": 2, "name": "NSFW Model", "type": "Checkpoint", "nsfwLevel": 3, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelNsfwKind.NSFW, entries[0].NsfwKind);
    }

    [Fact]
    public async Task SearchAsync_TypeLORA_ParsedAsLORA()
    {
        var json = """{"items": [{"id": 3, "name": "Lora", "type": "LORA", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelKind.LORA, entries[0].Kind);
    }

    [Fact]
    public async Task SearchAsync_TypeUnknown_FallsToOther()
    {
        var json = """{"items": [{"id": 4, "name": "Unknown", "type": "MotionModule", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        var entries = await source.SearchAsync("", 50, default);

        Assert.Equal(ModelKind.Other, entries[0].Kind);
    }

    [Fact]
    public async Task SearchAsync_NextPage_PaginatesUntilNull()
    {
        var page1 = """{"items": [{"id": 1, "name": "A", "type": "Checkpoint", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": "abc"}}""";
        var page2 = """{"items": [{"id": 2, "name": "B", "type": "Checkpoint", "nsfwLevel": 0, "modelVersions": []}], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(page1, page2);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        var entries = await source.SearchAsync("", maxResults: 100, default);

        Assert.Equal(2, entries.Count);
        Assert.Equal("1", entries[0].SourceId);
        Assert.Equal("2", entries[1].SourceId);
    }

    [Fact]
    public async Task SearchAsync_HttpError_Throws()
    {
        var handler = new DelegatingHandlerStub(HttpStatusCode.InternalServerError, "");
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await Assert.ThrowsAsync<HttpRequestException>(() => source.SearchAsync("", 50, default));
    }

    [Fact(Skip = "Real network endpoint; CI does not hit network. Run manually to verify CivitAI still public.")]
    public async Task LiveFetch_RealEndpoint_ReturnsEntries()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://civitai.com/") };
        var source = new CivitAiModelSource(client, "https://civitai.com", "");
        var entries = await source.SearchAsync("", 5, default);
        Assert.NotEmpty(entries);
    }

    // —— v0.6.22+:CivitAI API token — Authorization: Bearer 注入 ——
    // 受限 / NSFW / 标记敏感模型 401/403 解决。镜像 HuggingFaceModelSource 同款测试模式。

    [Fact]
    public async Task SearchAsync_WithToken_SendsBearerHeader()
    {
        // token 非空 + baseUrl HTTPS → 每个 request 应带 Authorization: Bearer {token}
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "civ_test_token_abc");

        await source.SearchAsync("test", 1, default);

        Assert.NotEmpty(handler.Requests);
        Assert.Contains(handler.Requests, r =>
            r.Headers.Authorization?.Scheme == "Bearer" &&
            r.Headers.Authorization?.Parameter == "civ_test_token_abc");
    }

    [Fact]
    public async Task SearchAsync_NoToken_NoAuthHeader()
    {
        // token 空 → 不应发 Authorization header(避免空值触发上游鉴权解析报错)
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("test", 1, default);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task SearchAsync_HttpMirrorWithToken_DoesNotLeakBearerHeader()
    {
        // 防泄露:HTTP 镜像 URL 即使配了 token 也不注入(防明文传 token)
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        // baseUrl=http://civitai-mirror.example → 非 HTTPS → 不注入
        var source = new CivitAiModelSource(CreateClient(handler), "http://civitai-mirror.example", "civ_secret_token");

        await source.SearchAsync("test", 1, default);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    // —— v0.6.22+:NSFW 在 API 层透传 ——
    // 用户 2026-08-20 反馈"因为我们就需要完整的非NSFW数据" — includeNsfw=false 时
    // 应打 ?nsfw=false,而不是 post-filter 缓存的子集。

    [Fact]
    public async Task SearchAsync_IncludeNsfwFalse_SendsNsfwFalseQueryString()
    {
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("test", 1, default, includeNsfw: false);

        var req = Assert.Single(handler.Requests);
        // nsfw=false(不要 nsfw=true 也不要 nsfw=false 别的值)
        Assert.True(req.RequestUri!.Query.Contains("nsfw=false") || req.RequestUri!.Query.Contains("nsfw=False"),
            $"URL 应包含 nsfw=false,实际: {req.RequestUri.Query}");
        Assert.DoesNotContain("nsfw=true", req.RequestUri!.Query);
    }

    [Fact]
    public async Task SearchAsync_IncludeNsfwTrue_SendsNsfwTrueQueryString()
    {
        var json = """{"items": [], "metadata": {"nextPage": null}}""";
        var handler = new DelegatingHandlerStub(json);
        var source = new CivitAiModelSource(CreateClient(handler), "https://civitai.com", "");

        await source.SearchAsync("test", 1, default, includeNsfw: true);

        var req = Assert.Single(handler.Requests);
        Assert.True(req.RequestUri!.Query.Contains("nsfw=true") || req.RequestUri!.Query.Contains("nsfw=True"),
            $"URL 应包含 nsfw=true,实际: {req.RequestUri.Query}");
    }
}

internal class DelegatingHandlerStub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode, string)> _responses = new();
    // v0.6.22+:记录每个发出的 request — 让测试能验证 Authorization: Bearer header。
    public List<HttpRequestMessage> Requests { get; } = new();

    public DelegatingHandlerStub(string body)
    {
        _responses.Enqueue((HttpStatusCode.OK, body));
    }

    public DelegatingHandlerStub(params string[] bodies)
    {
        foreach (var b in bodies) _responses.Enqueue((HttpStatusCode.OK, b));
    }

    public DelegatingHandlerStub(HttpStatusCode code, string body)
    {
        _responses.Enqueue((code, body));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var (code, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}
