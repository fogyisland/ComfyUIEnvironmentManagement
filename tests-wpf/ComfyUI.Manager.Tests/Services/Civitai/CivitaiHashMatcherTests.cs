using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class CivitaiHashMatcherTests
{
    private static (CivitaiHashMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new CivitaiHashMatcher(service), handler);
    }

    // DownloadedModel is a class with init-only properties (not a positional record),
    // so the brief's `new DownloadedModel(Title:, ...)` positional form won't compile.
    // Use object initializer syntax consistent with ModelFilesystemScanner + existing tests.
    private static DownloadedModel MakeModel(string? hash) => new()
    {
        Title = "test",
        SubfolderName = "checkpoints",
        FullPath = "C:\\models\\test.safetensors",
        Kind = ModelKind.Checkpoint,
        Source = "Local",
        SourceId = "local:test",
        SourceVersionId = "",
        DownloadedAt = DateTime.UtcNow,
        PreviewImagePath = null,
        Hash = hash,
    };

    [Fact]
    public async Task MatchAsync_HashHit_ReturnsMatchResult()
    {
        var (matcher, handler) = CreateMatcher();
        var json = """{"id":12345,"name":"Test Model","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains("/api/v1/model-versions/by-hash/ABC")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.Hash, result!.Source);
        Assert.Equal("Test Model", result.Detail.Title);
    }

    [Fact]
    public async Task MatchAsync_Hash404_ReturnsNull()
    {
        var (matcher, _) = CreateMatcher();
        // handler already returns 404 in default setup
        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_5xx_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        var matcher = new CivitaiHashMatcher(service);

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_NetworkError_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        var matcher = new CivitaiHashMatcher(service);

        var result = await matcher.MatchAsync(MakeModel("ABC"), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_NullHash_ReturnsNullImmediately()
    {
        var (matcher, handler) = CreateMatcher();
        var model = MakeModel(null);
        var result = await matcher.MatchAsync(model, CancellationToken.None);
        Assert.Null(result);
        handler.Protected().Verify(
            "SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
