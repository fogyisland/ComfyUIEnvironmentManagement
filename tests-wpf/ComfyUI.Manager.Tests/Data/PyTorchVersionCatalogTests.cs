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
/// - <c>ParseCudaVariantsFromHtml(html)</c> extracts the CUDA-letter keys
///   (<c>cuda.x</c> / <c>cuda.y</c> / <c>cuda.z</c>) from
///   <c>pt_version_map.release</c> and maps them to <c>cuNNN</c> tags.
/// - <c>ParsePypiJson(json, cudaVariants)</c> reads the PyPI
///   <c>releases</c> dict, filters out pre-release / post / dev /
///   alpha / beta versions, applies the externally-supplied CUDA list
///   to every stable version, detects CPU wheels by filename
///   (<c>+cpu</c>), and uses the latest valid upload_time as the release
///   date. Returns <c>null</c> on malformed JSON or missing
///   <c>releases</c> key.
/// - <c>FetchAsync(ct)</c> requests both <see cref="PyTorchVersionCatalog.PyPiPageUrl"/>
///   and <see cref="PyTorchVersionCatalog.PytorchOrgPageUrl"/> in parallel,
///   merges the two sources, and returns <c>null</c> on any HTTP /
///   cancellation / parse failure.
/// </summary>
public sealed class PyTorchVersionCatalogTests
{
    /// <summary>
    /// Representative pytorch.org HTML snippet (mirrors real
    /// <c>https://pytorch.org/get-started/locally/</c> structure). The
    /// <c>pt_version_map</c> block is FLAT: <c>"cuda.x"</c> is a direct
    /// key inside <c>"release"</c>, NOT nested under a <c>"cuda"</c>
    /// sub-object. Nightly block is present to prove the regex scopes
    /// only to <c>release</c>.
    /// </summary>
    private const string SampleHtml = """
        <script>
        var pt_published_versions = {"latest_stable":"2.13.0"};
        var pt_version_map = {"nightly":{"accnone":["cpu",""],"cuda.x":["cuda","12.6"]},"release":{"accnone":["cpu",""],"cuda.x":["cuda","12.6"],"cuda.y":["cuda","13.0"],"cuda.z":["cuda","13.2"]}};
        </script>
        """;

    /// <summary>
    /// Compact hand-crafted JSON fixture shaped like the real PyPI
    /// <c>https://pypi.org/pypi/torch/json</c> payload (top-level
    /// <c>"releases"</c> dict; each version maps to a list of file objects
    /// with <c>filename</c> and <c>upload_time</c>).
    /// <para>PyPI's <c>torch</c> package no longer carries CUDA-tagged
    /// wheels — only CPU wheels — so the fixture reflects reality: no
    /// <c>+cuNNN</c> filenames, just plain <c>+cpu</c> wheels for some
    /// versions.</para>
    /// Includes:
    /// <list type="bullet">
    /// <item>stable <c>2.13.0</c> with a <c>+cpu</c> wheel and a plain
    ///   non-CUDA wheel</item>
    /// <item>stable <c>2.5.1</c> with a <c>+cpu</c> wheel</item>
    /// <item>stable <c>2.4.0</c> WITHOUT a CPU wheel</item>
    /// <item>pre-release <c>2.6.0rc1</c> that must be filtered out</item>
    /// <item>a missing version (<c>"2.0.0"</c> listed with an empty file list)</item>
    /// </list>
    /// </summary>
    private const string FixtureJson = """
        {
          "releases": {
            "2.13.0": [
              {"filename": "torch-2.13.0-cp311-cp311-manylinux_2_28_x86_64.whl", "upload_time": "2026-07-08T16:05:06"},
              {"filename": "torch-2.13.0+cpu-cp311-cp311-linux_x86_64.whl", "upload_time": "2026-07-08T16:11:00"}
            ],
            "2.5.1": [
              {"filename": "torch-2.5.1-cp310-cp310-manylinux1_x86_64.whl", "upload_time": "2024-10-29T17:33:38"},
              {"filename": "torch-2.5.1+cpu-cp310-cp310-linux_x86_64.whl", "upload_time": "2024-10-29T17:35:00"}
            ],
            "2.4.0": [
              {"filename": "torch-2.4.0-cp310-cp310-manylinux1_x86_64.whl", "upload_time": "2024-07-24T10:00:00"}
            ],
            "2.6.0rc1": [
              {"filename": "torch-2.6.0rc1-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-01-15T10:00:00"}
            ],
            "2.0.0": []
          }
        }
        """;

    private static readonly string[] AllCuda = new[] { "cu118", "cu121", "cu126" };

    // ----- ParseCudaVariantsFromHtml tests -----

    [Fact]
    public void ParseCudaVariantsFromHtml_ExtractsCudaXYZ()
    {
        var result = PyTorchVersionCatalog.ParseCudaVariantsFromHtml(SampleHtml);

        Assert.Equal(new[] { "cu118", "cu121", "cu126" }, result);
    }

    [Fact]
    public void ParseCudaVariantsFromHtml_ReturnsEmptyOnNoReleaseKey()
    {
        // No "release" block at all.
        const string html = """
            <script>
            var pt_version_map = {"nightly":{"cuda.x":["cuda","12.6"]}};
            </script>
            """;

        var result = PyTorchVersionCatalog.ParseCudaVariantsFromHtml(html);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseCudaVariantsFromHtml_OnlyNightly_ReturnsEmpty()
    {
        // pt_version_map has nightly.cuda.x but no "release" block.
        const string html = """
            <script>
            var pt_version_map = {"nightly":{"accnone":["cpu",""],"cuda.x":["cuda","12.6"]}};
            </script>
            """;

        var result = PyTorchVersionCatalog.ParseCudaVariantsFromHtml(html);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseCudaVariantsFromHtml_EmptyHtml_ReturnsEmpty()
    {
        var result = PyTorchVersionCatalog.ParseCudaVariantsFromHtml("");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseCudaVariantsFromHtml_PartialRelease_ReturnsPresentLetters()
    {
        // Only cuda.x in release block → only cu118 returned.
        const string html = """
            var pt_version_map = {"release":{"cuda.x":["cuda","12.6"]},"nightly":{"cuda.x":["cuda","12.6"],"cuda.y":["cuda","13.0"]}};
            """;

        var result = PyTorchVersionCatalog.ParseCudaVariantsFromHtml(html);

        Assert.Equal(new[] { "cu118" }, result);
    }

    // ----- ParsePypiJson tests -----

    [Fact]
    public void ParsePypiJson_StableVersionGetsAllCudaAndCpu()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, AllCuda);

        Assert.NotNull(result);
        var latest = Assert.Single(result!, x => x.Version == "2.13.0");
        Assert.Equal(new[] { "cu118", "cu121", "cu126" }, latest.CudaVariants);
        Assert.True(latest.HasCpu);
    }

    [Fact]
    public void ParsePypiJson_FiltersPrereleaseAndPostAndDev()
    {
        // 2.6.0rc1 (pre-release), 2.6.0.post1 (post), 2.7.0.dev0 (dev), 2.7.0a1 (alpha).
        const string json = """
            {
              "releases": {
                "2.6.0": [
                  {"filename": "torch-2.6.0+cpu-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-01-29T16:25:15"}
                ],
                "2.6.0rc1": [
                  {"filename": "torch-2.6.0rc1-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-01-15T10:00:00"}
                ],
                "2.6.0.post1": [
                  {"filename": "torch-2.6.0.post1-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-02-15T10:00:00"}
                ],
                "2.7.0": [
                  {"filename": "torch-2.7.0+cpu-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-04-10T10:00:00"}
                ],
                "2.7.0.dev0": [
                  {"filename": "torch-2.7.0.dev0-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-03-15T10:00:00"}
                ],
                "2.7.0a1": [
                  {"filename": "torch-2.7.0a1-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-03-01T10:00:00"}
                ],
                "2.7.0b2": [
                  {"filename": "torch-2.7.0b2-cp311-cp311-linux_x86_64.whl", "upload_time": "2025-03-20T10:00:00"}
                ]
              }
            }
            """;

        var result = PyTorchVersionCatalog.ParsePypiJson(json, AllCuda);

        Assert.NotNull(result);
        var versions = result!.Select(v => v.Version).ToList();
        Assert.Contains("2.6.0", versions);
        Assert.Contains("2.7.0", versions);
        Assert.DoesNotContain("2.6.0rc1", versions);
        Assert.DoesNotContain("2.6.0.post1", versions);
        Assert.DoesNotContain("2.7.0.dev0", versions);
        Assert.DoesNotContain("2.7.0a1", versions);
        Assert.DoesNotContain("2.7.0b2", versions);
    }

    [Fact]
    public void ParsePypiJson_EmptyReleases_ReturnsEmptyList()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson("""{"releases": {}}""", AllCuda);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void ParsePypiJson_CorruptJson_ReturnsNull()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson("{not valid json at all][", AllCuda);

        Assert.Null(result);
    }

    [Fact]
    public void ParsePypiJson_OrdersByReleaseDateDesc()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, AllCuda);

        Assert.NotNull(result);
        var orderedVersions = result!.Select(x => x.Version).ToList();
        var idx213 = orderedVersions.IndexOf("2.13.0");
        var idx251 = orderedVersions.IndexOf("2.5.1");
        var idx240 = orderedVersions.IndexOf("2.4.0");
        // 2.13.0 (2026-07) > 2.5.1 (2024-10) > 2.4.0 (2024-07).
        Assert.True(idx213 < idx251, $"Expected 2.13.0 before 2.5.1, got [{string.Join(", ", orderedVersions)}]");
        Assert.True(idx251 < idx240, $"Expected 2.5.1 before 2.4.0, got [{string.Join(", ", orderedVersions)}]");
    }

    [Fact]
    public void ParsePypiJson_CudaVariantsPropagatedFromCaller()
    {
        // Caller passes empty list → every version gets empty CudaVariants.
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, Array.Empty<string>());

        Assert.NotNull(result);
        Assert.All(result!, v => Assert.Empty(v.CudaVariants));
    }

    [Fact]
    public void ParsePypiJson_VersionWithoutCpuHasHasCpuFalse()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, AllCuda);

        var v240 = result!.Single(x => x.Version == "2.4.0");
        Assert.False(v240.HasCpu);
    }

    [Fact]
    public void ParsePypiJson_ReturnsNullOnMissingReleasesKey()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson("""{"info":{"version":"2.13.0"}}""", AllCuda);

        Assert.Null(result);
    }

    [Fact]
    public void ParsePypiJson_SkipsVersionsWithEmptyFileList()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, AllCuda);

        Assert.DoesNotContain(result!, x => x.Version == "2.0.0");
    }

    [Fact]
    public void ParsePypiJson_PicksLatestUploadTimeAsReleaseDate()
    {
        var result = PyTorchVersionCatalog.ParsePypiJson(FixtureJson, AllCuda);

        var latest = result!.Single(x => x.Version == "2.13.0");
        // Latest of {16:05:06, 16:11:00} = 16:11:00.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 8, 16, 11, 0, TimeSpan.Zero),
            latest.ReleaseDate);
    }

    // ----- FetchAsync tests (HttpMessageHandler fakes) -----

    /// <summary>
    /// Fake handler that returns a different body per URL — used to
    /// distinguish PyPI GET vs pytorch.org GET.
    /// </summary>
    private static HttpClient MultiUrlHttpClient(
        string pypiBody,
        string pytorchOrgBody,
        HttpStatusCode pypiStatus = HttpStatusCode.OK,
        HttpStatusCode pytorchOrgStatus = HttpStatusCode.OK,
        Action? onSend = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                onSend?.Invoke();
                var url = req.RequestUri?.ToString() ?? "";
                var (body, status, contentType) = url == PyTorchVersionCatalog.PyPiPageUrl
                    ? (pypiBody, pypiStatus, "application/json")
                    : (pytorchOrgBody, pytorchOrgStatus, "text/html");
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = status,
                    Content = new StringContent(body, Encoding.UTF8, contentType),
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
    public async Task FetchAsync_ReturnsParsedOnSuccess()
    {
        var http = MultiUrlHttpClient(FixtureJson, SampleHtml);
        var catalog = new PyTorchVersionCatalog(http);

        var result = await catalog.FetchAsync();

        Assert.NotNull(result);
        Assert.NotEmpty(result!);
        // Stable 2.13.0 should appear with CUDA list from pytorch.org HTML.
        var v213 = result!.Single(x => x.Version == "2.13.0");
        Assert.Equal(new[] { "cu118", "cu121", "cu126" }, v213.CudaVariants);
        Assert.True(v213.HasCpu);
        // Pre-release must be filtered out.
        Assert.DoesNotContain(result!, x => x.Version == "2.6.0rc1");
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnHttp404()
    {
        // PyPI 404 → entire FetchAsync fails.
        var http = MultiUrlHttpClient(FixtureJson, SampleHtml, pypiStatus: HttpStatusCode.NotFound);
        var catalog = new PyTorchVersionCatalog(http);

        var result = await catalog.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnNetworkError()
    {
        var catalog = new PyTorchVersionCatalog(
            ThrowingHttpClient(new HttpRequestException("network down")));

        var result = await catalog.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullWhenPytorchOrgFails()
    {
        // pytorch.org 404 → entire FetchAsync fails (parallel GETs
        // means either failure aborts).
        var http = MultiUrlHttpClient(FixtureJson, "not found", pytorchOrgStatus: HttpStatusCode.NotFound);
        var catalog = new PyTorchVersionCatalog(http);

        var result = await catalog.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsEmptyCudaWhenPytorchOrgOmitsCudaKeys()
    {
        // pytorch.org HTML with no release block → empty CUDA list →
        // result is non-null but every CudaVariants is empty.
        var emptyCudaHtml = """<script>var pt_version_map = {};</script>""";
        var http = MultiUrlHttpClient(FixtureJson, emptyCudaHtml);
        var catalog = new PyTorchVersionCatalog(http);

        var result = await catalog.FetchAsync();

        Assert.NotNull(result);
        Assert.All(result!, v => Assert.Empty(v.CudaVariants));
    }
}