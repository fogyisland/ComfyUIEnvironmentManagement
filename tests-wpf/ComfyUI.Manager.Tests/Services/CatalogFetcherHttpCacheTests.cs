using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogFetcherHttpCacheTests
{
    private static HttpClient MockedHttpClient(
        HttpStatusCode status,
        string? body = null,
        string? etag = null,
        string? lastModified = null,
        Action<HttpRequestMessage, CancellationToken>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => onRequest?.Invoke(req, ct))
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status);
                if (body is not null)
                    resp.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                if (etag is not null)
                    resp.Headers.TryAddWithoutValidation("ETag", etag);
                if (lastModified is not null)
                {
                    // v0.6.14: Last-Modified 在 .NET 里被归类为 content header
                    // (HttpContentHeaders),不是 response header — .NET 文档里
                    // 写在 "Entity Headers" 段。直接放 resp.Headers 会被 silently
                    // drop。CatalogFetcher 的 TryGetHeader 同时查两边来兼容。
                    if (!resp.Headers.TryAddWithoutValidation("Last-Modified", lastModified))
                        resp.Content!.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
                }
                return resp;
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAsync_NoEtag_SendsNoIfNoneMatchHeader()
    {
        HttpRequestMessage? captured = null;
        var http = MockedHttpClient(HttpStatusCode.OK, "[]", onRequest: (r, _) => captured = r);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        await fetcher.FetchAsync("https://example/c.json", etag: null, lastModified: null);

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("If-None-Match"));
        Assert.False(captured.Headers.Contains("If-Modified-Since"));
    }

    [Fact]
    public async Task FetchAsync_WithEtag_SendsIfNoneMatchHeader()
    {
        HttpRequestMessage? captured = null;
        var http = MockedHttpClient(HttpStatusCode.OK, "[]",
            onRequest: (r, _) => captured = r);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        await fetcher.FetchAsync("https://example/c.json", etag: "\"abc123\"", lastModified: null);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("If-None-Match"));
        Assert.Equal("\"abc123\"", captured.Headers.GetValues("If-None-Match").First());
    }

    [Fact]
    public async Task FetchAsync_ServerReturns304_ReturnsIs304TrueAndNewEtag()
    {
        var http = MockedHttpClient(HttpStatusCode.NotModified, body: null,
            etag: "\"new-etag\"", lastModified: null);
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        var result = await fetcher.FetchAsync("https://example/c.json",
            etag: "\"abc123\"", lastModified: null);

        Assert.True(result.Is304);
        Assert.Null(result.Entries);
        Assert.Equal("\"new-etag\"", result.NewEtag);
    }

    [Fact]
    public async Task FetchAsync_ServerReturns200_ReturnsEntriesAndNewEtag()
    {
        var json = @"{ ""custom_nodes"": [
            { ""id"": ""pkg-a"", ""title"": ""PkgA"" }
        ] }";
        var http = MockedHttpClient(HttpStatusCode.OK, body: json,
            etag: "\"v2\"", lastModified: "Wed, 21 Oct 2026 07:28:00 GMT");
        var fetcher = new CatalogFetcher(http, cacheTtlMinutes: 60);

        var result = await fetcher.FetchAsync("https://example/c.json", null, null);

        Assert.False(result.Is304);
        Assert.NotNull(result.Entries);
        Assert.Single(result.Entries!);
        Assert.Equal("pkg-a", result.Entries![0].Package);
        Assert.Equal("\"v2\"", result.NewEtag);
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", result.NewLastModified);
    }
}