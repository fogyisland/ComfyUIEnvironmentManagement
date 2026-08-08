using System;
using System.IO;
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

/// <summary>
/// v0.6.7.4 T3 R1:直接跑真实 <see cref="CatalogFetcher.FetchAsync"/>(不用 FakeCatalogFetcher
/// 覆写),验证 fetch start / complete / failed 三条 <c>[catalog-fetch]</c> 日志真的写出来。
/// HTTP 层用 Moq.Protected 的 HttpMessageHandler(跟 CatalogFetcherTests 同款 pattern)。
/// </summary>
public class CatalogFetcherLoggingTests : IDisposable
{
    private const string TestUrl = "https://example.com/registry.json";

    private readonly string _tempRoot;

    public CatalogFetcherLoggingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"catalog-fetch-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static HttpClient RespondingHttpClient(string json)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
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
    public async Task FetchAsync_Success_Logs_StartAndComplete()
    {
        var json = @"[
            { ""id"": ""comfy-node-a"" },
            { ""id"": ""comfy-node-b"" }
        ]";
        using var logger = new AppLogger(_tempRoot);
        var fetcher = new CatalogFetcher(RespondingHttpClient(json), cacheTtlMinutes: 60, logger: logger);

        var entries = await fetcher.FetchAsync(TestUrl);

        Assert.Equal(2, entries.Count);
        var lines = logger.ReadLines();

        var start = Assert.Single(lines, l =>
            l.Contains("[catalog-fetch]")
            && l.Contains("[INFO ]")
            && l.Contains($"开始 fetch url={TestUrl}"));
        Assert.NotNull(start);

        var complete = Assert.Single(lines, l =>
            l.Contains("[catalog-fetch]")
            && l.Contains("[INFO ]")
            && l.Contains("完成 fetch")
            && l.Contains("count=2")
            && l.Contains("duration_ms=")
            && l.Contains($"url={TestUrl}"));
        Assert.NotNull(complete);

        // start 必须在 complete 之前
        Assert.True(Array.IndexOf(lines, start) < Array.IndexOf(lines, complete));
        // 成功路径不应该有 ERROR
        Assert.DoesNotContain(lines, l => l.Contains("[catalog-fetch]") && l.Contains("[ERROR]"));
    }

    [Fact]
    public async Task FetchAsync_Failure_Logs_Error_AndRethrows()
    {
        using var logger = new AppLogger(_tempRoot);
        var fetcher = new CatalogFetcher(
            ThrowingHttpClient(new HttpRequestException("dns fail")),
            cacheTtlMinutes: 60,
            logger: logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.FetchAsync(TestUrl));

        var lines = logger.ReadLines();

        // 即使失败,start 行也已经写了
        Assert.Contains(lines, l =>
            l.Contains("[catalog-fetch]")
            && l.Contains("[INFO ]")
            && l.Contains($"开始 fetch url={TestUrl}"));

        Assert.Single(lines, l =>
            l.Contains("[catalog-fetch]")
            && l.Contains("[ERROR]")
            && l.Contains($"fetch 失败 url={TestUrl}")
            && l.Contains("HttpRequestException")
            && l.Contains("dns fail"));

        // 失败路径不应该有 "完成 fetch"
        Assert.DoesNotContain(lines, l => l.Contains("完成 fetch"));
    }

    [Fact]
    public async Task FetchAsync_NoLogger_DoesNotThrow_BackwardsCompatible()
    {
        var json = @"[{ ""id"": ""pkg"" }]";
        var fetcher = new CatalogFetcher(RespondingHttpClient(json), cacheTtlMinutes: 60);

        var entries = await fetcher.FetchAsync(TestUrl);

        Assert.Single(entries);
        Assert.Equal("pkg", entries[0].Package);
        // 没传 logger 时不写任何日志文件
        Assert.False(Directory.EnumerateFiles(_tempRoot, "*.log", SearchOption.AllDirectories).Any());
    }
}
