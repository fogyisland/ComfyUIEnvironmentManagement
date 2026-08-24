using System;
using System.IO;
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

public sealed class CompanionJsonMatcherTests : IDisposable
{
    private readonly string _tempDir;

    public CompanionJsonMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"comp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // DownloadedModel is a class with init-only properties (not a positional record),
    // so the brief's `new DownloadedModel(Title:, ...)` positional form won't compile.
    // Use object initializer syntax consistent with ModelFilesystemScanner + existing tests.
    private static DownloadedModel MakeModel(string fullPath) => new()
    {
        Title = "test",
        SubfolderName = "checkpoints",
        FullPath = fullPath,
        Kind = ModelKind.Checkpoint,
        Source = "Local",
        SourceId = "local:test",
        SourceVersionId = "",
        DownloadedAt = DateTime.UtcNow,
        PreviewImagePath = null,
        Hash = null,
        MatchedDetail = null,
        MatchSource = null,
    };

    private static (CompanionJsonMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new CompanionJsonMatcher(service), handler);
    }

    [Fact]
    public async Task MatchAsync_ValidSidecarWithModelId_ReturnsMatchResult()
    {
        var modelPath = Path.Combine(_tempDir, "MyModel.safetensors");
        File.WriteAllText(Path.Combine(_tempDir, "MyModel.civitai.info"),
            """{"modelId":99,"modelName":"MyModel"}""");
        var (matcher, handler) = CreateMatcher();
        var detailJson = """{"id":99,"name":"MyModel","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().EndsWith("/api/v1/models/99")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.CompanionJson, result!.Source);
        Assert.Equal(99, result.Detail.Id);
    }

    [Fact]
    public async Task MatchAsync_NoSidecar_ReturnsNull()
    {
        var modelPath = Path.Combine(_tempDir, "NoSidecar.safetensors");
        // don't write sidecar
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_SidecarModelIdReturns404_ReturnsNull()
    {
        var modelPath = Path.Combine(_tempDir, "BadId.safetensors");
        File.WriteAllText(Path.Combine(_tempDir, "BadId.civitai.info"), """{"modelId":404}""");
        var (matcher, handler) = CreateMatcher();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await matcher.MatchAsync(MakeModel(modelPath), CancellationToken.None);
        Assert.Null(result);
    }
}
