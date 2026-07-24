using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using ComfyUI.Manager.Data;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// Tests for <see cref="PyTorchVersionFetcher"/>:
/// - <c>Parse(html)</c> extracts <c>Stable</c> version from
///   <c>pt_published_versions</c> literal and detects
///   <c>pt_version_map.nightly.cuda.x</c> existence via <c>HasNightlyCu126</c>.
/// - <c>FetchAsync</c> swallows every expected failure mode
///   (HTTP 404, timeout, network error) and returns <c>null</c>.
/// </summary>
public sealed class PyTorchVersionFetcherTests
{
    /// <summary>
    /// Minimal but representative HTML pulled from pytorch.org/get-started/locally/.
    /// Includes both <c>pt_published_versions</c> literal (so the stable regex
    /// can fire on the "stable,pip,linux,cuda.x,python" key) and the
    /// <c>pt_version_map</c> literal (so the nightly-cuda-x regex can fire).
    /// </summary>
    private const string SampleHtml = """
        <script>
        var pt_published_versions = {"stable,pip,linux,cuda.x,python":"pip3 install torch==2.13.0 torchvision==0.22.0 torchaudio==2.6.0 --index-url https://download.pytorch.org/whl/cu126"};
        var pt_version_map = {"stable":{"cpu":["cpu"]},"nightly":{"cpu":["cpu"],"cuda":{"x":["12.6"]}}};
        </script>
        """;

    /// <summary>
    /// Build a mocked HttpClient whose single SendAsync call returns the given
    /// HTML body (or the given status, e.g. for 404).
    /// </summary>
    private static HttpClient MockedHttpClient(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
        return new HttpClient(handler.Object);
    }

    /// <summary>
    /// Build a mocked HttpClient whose SendAsync throws the given exception
    /// (used for network-error simulation).
    /// </summary>
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

    /// <summary>
    /// Build a mocked HttpClient whose SendAsync delays longer than the test
    /// timeout. Used to verify FetchAsync returns null on timeout (via the
    /// CancellationToken + HttpClient timeout interaction).
    /// </summary>
    private static HttpClient SlowHttpClient(TimeSpan delay)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(SampleHtml, System.Text.Encoding.UTF8, "text/html"),
                };
            });
        return new HttpClient(handler.Object) { Timeout = TimeSpan.FromMilliseconds(200) };
    }

    // ----- Parse tests (pure, no HTTP) -----

    [Fact]
    public void Parse_ExtractsStableFromPytorchOrgHtml()
    {
        var result = PyTorchVersionFetcher.Parse(SampleHtml);

        Assert.NotNull(result);
        Assert.Equal("2.13.0", result!.Stable);
        Assert.True(result.HasNightlyCu126);
    }

    [Fact]
    public void Parse_ReturnsNullWhenLatestStableMissing()
    {
        // No "stable,pip,linux,cuda.x,python" key in pt_published_versions
        var html = """
            <script>
            var pt_published_versions = {"stable,pip,linux,accnone,python":"pip3 install torch==2.13.0"};
            var pt_version_map = {"nightly":{"cpu":["cpu"],"cuda":{"x":["12.6"]}}};
            </script>
            """;

        var result = PyTorchVersionFetcher.Parse(html);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsNullWhenNightlyCudaXMissing()
    {
        // pt_version_map exists but nightly has no cuda.x (e.g. cu126 was retired)
        var html = """
            <script>
            var pt_published_versions = {"stable,pip,linux,cuda.x,python":"pip3 install torch==2.13.0"};
            var pt_version_map = {"nightly":{"cpu":["cpu"]}};
            </script>
            """;

        var result = PyTorchVersionFetcher.Parse(html);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsNullOnCorruptHtml()
    {
        // Empty / unrelated HTML — neither regex matches
        var result = PyTorchVersionFetcher.Parse("<html></html>");

        Assert.Null(result);
    }

    // ----- FetchAsync tests (HttpMessageHandler fakes) -----

    [Fact]
    public async Task FetchAsync_ReturnsNullOnHttp404()
    {
        var fetcher = new PyTorchVersionFetcher(MockedHttpClient("not found", HttpStatusCode.NotFound));

        var result = await fetcher.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnTimeout()
    {
        // Handler delays 5s, but client timeout = 200ms → TaskCanceledException
        // must be swallowed and FetchAsync returns null.
        var fetcher = new PyTorchVersionFetcher(SlowHttpClient(TimeSpan.FromSeconds(5)));

        var result = await fetcher.FetchAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnNetworkError()
    {
        var fetcher = new PyTorchVersionFetcher(
            ThrowingHttpClient(new HttpRequestException("network down")));

        var result = await fetcher.FetchAsync();

        Assert.Null(result);
    }
}