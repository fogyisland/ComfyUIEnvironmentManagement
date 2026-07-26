using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// Tests for <see cref="PyTorchVersionCatalog"/>:
/// - <c>Parse(json)</c> reads the PyPI JSON <c>releases</c> dict, filters out
///   pre-release / post / development versions (e.g. <c>2.6.0rc1</c>,
///   <c>2.6.0.post1</c>, <c>2.7.0.dev0</c>), extracts CUDA / CPU variants
///   from wheel filenames (<c>+cu118</c>, <c>+cu126</c>, <c>+cpu</c>),
///   deduplicates tags, and uses the latest valid upload_time as the release
///   date. Returns <c>null</c> on malformed JSON.
/// - <c>FetchAsync</c> requests <see cref="PyTorchVersionCatalog.PageUrl"/>,
///   parses a successful response, and returns <c>null</c> for 404 or
///   <see cref="HttpRequestException"/>.
/// </summary>
public sealed class PyTorchVersionCatalogTests
{
    /// <summary>
    /// Compact hand-crafted JSON fixture shaped like the real PyPI
    /// <c>https://pypi.org/pypi/torch/json</c> payload (top-level
    /// <c>"releases"</c> dict; each version maps to a list of file objects
    /// with <c>filename</c> and <c>upload_time</c>).
    /// Includes:
    /// <list type="bullet">
    /// <item>stable <c>2.13.0</c> with <c>+cu126</c>, <c>+cpu</c> wheels and an
    /// old non-CUDA wheel (must NOT contribute a CUDA tag)</item>
    /// <item>stable <c>2.5.1</c> with <c>+cu121</c> and <c>+cpu</c> wheels</item>
    /// <item>pre-release <c>2.6.0rc1</c> that must be filtered out</item>
    /// <item>a missing version (<c>"2.0.0"</c> listed with an empty file list)</item>
    /// </list>
    /// </summary>
    private const string FixtureJson = """
        {
          "releases": {
            "2.13.0": [
              {"filename": "torch-2.13.0-cp311-cp311-manylinux_2_28_x86_64.whl", "upload_time": "2026-07-08T16:05:06"},
              {"filename": "torch-2.13.0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2026-07-08T16:10:00"},
              {"filename": "torch-2.13.0+cpu-cp311-cp311-linux_x86_64.whl", "upload_time": "2026-07-08T16:11:00"},
              {"filename": "torch-2.13.0+cu126-cp312-cp312-linux_x86_64.whl", "upload_time": "2026-07-08T16:12:00"}
            ],
            "2.5.1": [
              {"filename": "torch-2.5.1-cp310-cp310-manylinux1_x86_64.whl", "upload_time": "2024-10-29T17:33:38"},
              {"filename": "torch-2.5.1+cu121-cp310-cp310-linux_x86_64.whl", "upload_time": "2024-10-29T17:34:00"},
              {"filename": "torch-2.5.1+cpu-cp310-cp310-linux_x86_64.whl", "upload_time": "2024-10-29T17:35:00"}
            ],
            "2.6.0rc1": [
              {"filename": "torch-2.6.0rc1+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-01-15T10:00:00"}
            ],
            "2.0.0": []
          }
        }
        """;

    // ----- Parse tests (pure, no HTTP) -----

    [Fact]
    public void Parse_FiltersPrereleaseAndExtractsWheelVariants()
    {
        var result = PyTorchVersionCatalog.Parse(FixtureJson);

        Assert.NotNull(result);
        Assert.NotEmpty(result!);

        var latest = Assert.Single(result!, x => x.Version == "2.13.0");
        Assert.Equal(new[] { "cu126" }, latest.CudaVariants);
        Assert.True(latest.HasCpu);

        // Pre-release must be filtered out.
        Assert.DoesNotContain(result!, x => x.Version == "2.6.0rc1");
    }

    [Fact]
    public void Parse_ReturnsMultipleCudaVariants()
    {
        // Multiple distinct CUDA tags for one version.
        const string json = """
            {
              "releases": {
                "2.7.0": [
                  {"filename": "torch-2.7.0+cu118-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T10:00:00"},
                  {"filename": "torch-2.7.0+cu121-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T11:00:00"},
                  {"filename": "torch-2.7.0+cu124-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T12:00:00"},
                  {"filename": "torch-2.7.0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T13:00:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.Parse(json);

        var v = Assert.Single(result!);
        Assert.Equal(new[] { "cu118", "cu121", "cu124", "cu126" }, v.CudaVariants);
        Assert.False(v.HasCpu);
    }

    [Fact]
    public void Parse_DeduplicatesCudaVariants()
    {
        // Same tag appearing on multiple wheel files must dedupe.
        const string json = """
            {
              "releases": {
                "2.8.0": [
                  {"filename": "torch-2.8.0+cu126-cp310-cp310-linux_x86_64.whl", "upload_time": "2025-05-10T10:00:00"},
                  {"filename": "torch-2.8.0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-05-10T11:00:00"},
                  {"filename": "torch-2.8.0+cu126-cp312-cp312-linux_x86_64.whl", "upload_time": "2025-05-10T12:00:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.Parse(json);

        var v = Assert.Single(result!);
        Assert.Equal(new[] { "cu126" }, v.CudaVariants);
    }

    [Fact]
    public void Parse_DetectsCpuWithoutCuda()
    {
        const string json = """
            {
              "releases": {
                "2.4.0": [
                  {"filename": "torch-2.4.0-cp310-cp310-manylinux1_x86_64.whl", "upload_time": "2024-07-24T10:00:00"},
                  {"filename": "torch-2.4.0+cpu-cp310-cp310-linux_x86_64.whl", "upload_time": "2024-07-24T10:01:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.Parse(json);

        var v = Assert.Single(result!);
        Assert.Empty(v.CudaVariants);
        Assert.True(v.HasCpu);
    }

    [Fact]
    public void Parse_PicksLatestUploadTimeAsReleaseDate()
    {
        var result = PyTorchVersionCatalog.Parse(FixtureJson);

        var latest = result!.Single(x => x.Version == "2.13.0");
        // Latest of {16:05:06, 16:10:00, 16:11:00, 16:12:00} = 16:12:00.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 8, 16, 12, 0, TimeSpan.Zero),
            latest.ReleaseDate);
    }

    [Fact]
    public void Parse_SkipsVersionsWithEmptyFileList()
    {
        var result = PyTorchVersionCatalog.Parse(FixtureJson);

        Assert.DoesNotContain(result!, x => x.Version == "2.0.0");
    }

    [Fact]
    public void Parse_FiltersPostVersions()
    {
        // Post-release versions (PEP 440 .postN) must be excluded.
        const string json = """
            {
              "releases": {
                "2.6.0": [
                  {"filename": "torch-2.6.0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-01-29T16:25:15"}
                ],
                "2.6.0.post1": [
                  {"filename": "torch-2.6.0.post1+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-02-15T10:00:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.Parse(json);

        var v = Assert.Single(result!);
        Assert.Equal("2.6.0", v.Version);
    }

    [Fact]
    public void Parse_FiltersDevVersions()
    {
        // Development versions (PEP 440 .devN) must be excluded.
        const string json = """
            {
              "releases": {
                "2.7.0": [
                  {"filename": "torch-2.7.0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T10:00:00"}
                ],
                "2.7.0.dev0": [
                  {"filename": "torch-2.7.0.dev0+cu126-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-03-15T10:00:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.Parse(json);

        var v = Assert.Single(result!);
        Assert.Equal("2.7.0", v.Version);
    }

    [Fact]
    public void Parse_ReturnsNullOnInvalidJson()
    {
        var result = PyTorchVersionCatalog.Parse("{not valid json at all][");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsNullOnMissingReleasesKey()
    {
        // Top-level object without "releases" → null (malformed).
        var result = PyTorchVersionCatalog.Parse("""{"info":{"version":"2.13.0"}}""");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsEmptyListWhenReleasesEmpty()
    {
        var result = PyTorchVersionCatalog.Parse("""{"releases": {}}""");

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_OrdersByReleaseDateDescending()
    {
        var result = PyTorchVersionCatalog.Parse(FixtureJson);

        Assert.NotNull(result);
        // 2.13.0 (2026-07) must precede 2.5.1 (2024-10) when sorted desc.
        var orderedVersions = result!.Select(x => x.Version).ToList();
        var idx213 = orderedVersions.IndexOf("2.13.0");
        var idx251 = orderedVersions.IndexOf("2.5.1");
        Assert.True(idx213 < idx251, $"Expected 2.13.0 before 2.5.1, got [{string.Join(", ", orderedVersions)}]");
    }

    // ----- FetchAsync tests (HttpMessageHandler fakes) -----

    private static HttpClient MockedHttpClient(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        Action? onSend = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                onSend?.Invoke();
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = status,
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            });
        return new HttpClient(handler.Object);
    }

    private static HttpClient ThrowingHttpClient(Exception ex)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(ex);
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAsync_RequestsPyPiPageUrl()
    {
        Uri? requestedUri = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(FixtureJson, Encoding.UTF8, "application/json"),
            }))
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => requestedUri = req.RequestUri);

        var catalog = new PyTorchVersionCatalog(new HttpClient(handler.Object));

        await catalog.FetchAsync();

        Assert.NotNull(requestedUri);
        Assert.Equal(PyTorchVersionCatalog.PageUrl, requestedUri!.ToString());
    }

    [Fact]
    public async Task FetchAsync_ReturnsParsedVersionsOnSuccess()
    {
        var catalog = new PyTorchVersionCatalog(MockedHttpClient(FixtureJson));

        var result = await catalog.FetchAsync();

        Assert.NotNull(result);
        Assert.Contains(result!, x => x.Version == "2.13.0");
        Assert.DoesNotContain(result!, x => x.Version == "2.6.0rc1");
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnHttp404()
    {
        var catalog = new PyTorchVersionCatalog(MockedHttpClient("not found", HttpStatusCode.NotFound));

        var result = await catalog.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnHttpRequestException()
    {
        var catalog = new PyTorchVersionCatalog(
            ThrowingHttpClient(new HttpRequestException("network down")));

        var result = await catalog.FetchAsync();

        Assert.Null(result);
    }
}