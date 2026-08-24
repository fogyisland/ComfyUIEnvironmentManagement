using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0 T9a:CivitAiLookupService unit tests — 验证 fuzzy search + detail fetch +
/// error mapping。Mock&lt;HttpMessageHandler&gt; pattern(同 CatalogFetcherLoggingTests)。
/// T9a 是纯 service,zero UI 依赖,可 100% 单元测。
/// </summary>
public class CivitAiLookupServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public CivitAiLookupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"civitai-lookup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (CivitAiLookupService svc, Mock<HttpMessageHandler> mock)
        Build(string responseBody, HttpStatusCode status = HttpStatusCode.OK, string apiToken = "")
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(mock.Object)
        {
            BaseAddress = new Uri("https://civitai.com/"),
        };
        AppLogger? logger = string.IsNullOrEmpty(apiToken) ? null : new AppLogger(_tempRoot);
        var svc = new CivitAiLookupService(http, "https://civitai.com", apiToken, logger);
        return (svc, mock);
    }

    [Fact]
    public async Task Search_ByTitle_ReturnsCandidates()
    {
        var json = """
        {
          "items": [
            {
              "id": 12345,
              "name": "Anime Model",
              "creator": {"username": "alice"},
              "baseModel": "SD 1.5",
              "imageUrl": "https://cdn.example.com/thumb.jpg"
            }
          ]
        }
        """;
        var (svc, _) = Build(json);

        var candidates = await svc.SearchByTitleAsync("anime");

        Assert.Single(candidates);
        var c = candidates[0];
        Assert.Equal(12345, c.Id);
        Assert.Equal("Anime Model", c.Title);
        Assert.Equal("alice", c.Username);
        Assert.Equal("SD 1.5", c.BaseModel);
        Assert.Equal("https://cdn.example.com/thumb.jpg", c.ThumbnailUrl);
    }

    [Fact]
    public async Task Search_NoResults_ReturnsEmptyList()
    {
        var (svc, _) = Build("""{"items":[]}""");

        var candidates = await svc.SearchByTitleAsync("nothing");

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetDetail_ValidId_ReturnsFullDto()
    {
        var json = """
        {
          "id": 12345,
          "name": "Anime Model",
          "creator": {"username": "alice"},
          "baseModel": "SD 1.5",
          "description": "An awesome anime model",
          "tags": ["anime", "lora"],
          "modelVersions": [
            {"name": "v1", "baseModel": "SD 1.5", "createdAt": "2024-01-15T00:00:00Z"}
          ],
          "images": [
            {"url": "https://cdn.example.com/img1.jpg"}
          ]
        }
        """;
        var (svc, _) = Build(json);

        var detail = await svc.GetDetailAsync(12345);

        Assert.Equal(12345, detail.Id);
        Assert.Equal("Anime Model", detail.Title);
        Assert.Equal("alice", detail.Username);
        Assert.Equal("SD 1.5", detail.BaseModel);
        Assert.Equal("An awesome anime model", detail.Description);
        Assert.Equal(2, detail.Tags.Count);
        Assert.Contains("anime", detail.Tags);
        Assert.Single(detail.Versions);
        Assert.Equal("v1", detail.Versions[0].Name);
        Assert.Equal("SD 1.5", detail.Versions[0].BaseModel);
        Assert.Equal(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), detail.Versions[0].CreatedAt);
        Assert.Single(detail.ImageUrls);
        Assert.Equal("https://cdn.example.com/img1.jpg", detail.ImageUrls[0]);
    }

    [Fact]
    public async Task GetDetail_404_ThrowsNotFoundException()
    {
        var (svc, _) = Build("", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<CivitAiLookupNotFoundException>(
            () => svc.GetDetailAsync(99999));

        Assert.Equal(99999, ex.ModelId);
    }

    [Fact]
    public async Task GetDetail_500_PropagatesHttpRequestException()
    {
        var (svc, _) = Build("internal error", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.GetDetailAsync(12345));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Search_ApiTokenHeaderSet_WhenTokenProvided()
    {
        var (svc, mock) = Build("""{"items":[]}""", apiToken: "secret-token-xyz");

        await svc.SearchByTitleAsync("test");

        // DefaultRequestHeaders.Authorization 在每次 request 上镜像,verify 直接看 mock 收到的 req。
        mock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Headers.Authorization != null &&
                req.Headers.Authorization.Scheme == "Bearer" &&
                req.Headers.Authorization.Parameter == "secret-token-xyz"),
            ItExpr.IsAny<CancellationToken>());
    }
}