using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

public sealed class SafetensorsMetadataMatcherTests : IDisposable
{
    private readonly string _tempDir;

    public SafetensorsMetadataMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"safe-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static (SafetensorsMetadataMatcher matcher, Mock<HttpMessageHandler> handler) CreateMatcher()
    {
        var handler = new Mock<HttpMessageHandler>();
        var http = new HttpClient(handler.Object);
        var service = new CivitAiLookupService(http, "https://civitai.com", "");
        return (new SafetensorsMetadataMatcher(service), handler);
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

    /// <summary>Write a synthetic .safetensors file with a JSON header containing the requested field.</summary>
    private static string WriteFakeSafetensors(string name, string headerField, string headerValue)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.safetensors");
        var headerJson = $"{{ \"__metadata__\": {{ \"{headerField}\": \"{headerValue}\" }} }}";
        var headerBytes = Encoding.UTF8.GetBytes(headerJson);
        var lengthBytes = BitConverter.GetBytes((ulong)headerBytes.Length);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(lengthBytes, 0, 8);
        fs.Write(headerBytes, 0, headerBytes.Length);
        return path;
    }

    [Fact]
    public async Task MatchAsync_HeaderHas_ss_sd_model_name_ReturnsMatchResult()
    {
        var filePath = WriteFakeSafetensors("a.safetensors", "ss_sd_model_name", "AnimateLCM");
        var (matcher, handler) = CreateMatcher();
        // Mock search returning 1 candidate with model id, then detail fetch
        var searchJson = """{"items":[{"id":99,"name":"AnimateLCM","creator":{"username":"u"}}]}""";
        var detailJson = """{"id":99,"name":"AnimateLCM","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(filePath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
        Assert.Equal("AnimateLCM", result.Detail.Title);
        File.Delete(filePath);
    }

    [Fact]
    public async Task MatchAsync_HeaderHas_modelspec_title_ReturnsMatchResult()
    {
        var filePath = WriteFakeSafetensors("a.safetensors", "modelspec.title", "MyModel");
        var (matcher, handler) = CreateMatcher();
        var searchJson = """{"items":[{"id":99,"name":"MyModel","creator":{"username":"u"}}]}""";
        var detailJson = """{"id":99,"name":"MyModel","creator":{"username":"u"},"modelVersions":[]}""";
        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson) });

        var result = await matcher.MatchAsync(MakeModel(filePath), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
        File.Delete(filePath);
    }

    [Fact]
    public async Task MatchAsync_NoHeaderOrNoMetadata_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "garbage.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }); // not safetensors
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(path), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_HeaderInvalidJson_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "broken.safetensors");
        var badHeader = Encoding.UTF8.GetBytes("{ not valid json");
        var lengthBytes = BitConverter.GetBytes((ulong)badHeader.Length);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.Write(lengthBytes, 0, 8);
            fs.Write(badHeader, 0, badHeader.Length);
        }
        var (matcher, _) = CreateMatcher();
        var result = await matcher.MatchAsync(MakeModel(path), CancellationToken.None);
        Assert.Null(result);
    }
}
