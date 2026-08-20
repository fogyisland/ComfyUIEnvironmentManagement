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
