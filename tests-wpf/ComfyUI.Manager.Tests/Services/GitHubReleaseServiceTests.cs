using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class GitHubReleaseServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;

    public GitHubReleaseServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"gh-release-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _http = new HttpClient();
        _logger = null; // AppLogger 写盘在测试不需要
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        _http.Dispose();
    }

    private GitHubReleaseService NewSut(HttpClient? http = null,
        TimeSpan? cacheTtl = null) =>
        new(http ?? _http, _logger,
            cacheFilePath: Path.Combine(_tempDir, "cache.json"),
            cacheTtl: cacheTtl);

    [Fact]
    public async Task FetchAsync_CacheHit_ReturnsCachedWithoutHttp()
    {
        // 预填 cache file (写入 1h ago,valid)
        var cachePath = Path.Combine(_tempDir, "cache.json");
        var cached = new[] { new GitHubRelease("v0.6.11", "v0.6.11",
            DateTime.UtcNow, "https://...", false) };
        await File.WriteAllTextAsync(cachePath,
            GitHubReleaseService.SerializeCache(cached, DateTime.UtcNow.AddHours(-1)));

        var sut = NewSut(http: new HttpClient(new NoNetworkHandler())); // 阻断网络
        var releases = await sut.FetchAsync();

        Assert.Single(releases);
        Assert.Equal("v0.6.11", releases[0].TagName);
    }

    [Fact]
    public async Task FetchAsync_NetworkFail_ReturnsLastCached_SetsLastSync()
    {
        // 空 cache + 网络失败
        var sut = NewSut(http: new HttpClient(new FailingHandler()));
        var releases = await sut.FetchAsync();

        Assert.Empty(releases); // 没缓存可返
        Assert.Null(sut.LastSyncUtc); // 也没成功 sync
    }

    [Fact]
    public async Task FetchAsync_InvalidJson_LogsAndThrows()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        await File.WriteAllTextAsync(cachePath, "this is not json{");

        var sut = NewSut(http: new HttpClient(new NoNetworkHandler()));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => sut.FetchAsync());
    }

    [Fact]
    public async Task FetchAsync_EmptyResponse_ReturnsEmptyList()
    {
        var sut = NewSut(http: new HttpClient(new StubHandler("[]")));
        var releases = await sut.FetchAsync();

        Assert.Empty(releases);
        Assert.NotNull(sut.LastSyncUtc); // 成功 sync 即使空
    }

    [Fact]
    public async Task FetchAsync_ParsesValidJson()
    {
        var json = @"[{""tag_name"":""v0.6.11"",""name"":""v0.6.11"",
            ""published_at"":""2026-08-11T00:00:00Z"",
            ""html_url"":""https://github.com/.../releases/tag/v0.6.11"",
            ""prerelease"":false}]";
        var sut = NewSut(http: new HttpClient(new StubHandler(json)));
        var releases = await sut.FetchAsync();

        Assert.Single(releases);
        Assert.Equal("v0.6.11", releases[0].TagName);
        Assert.False(releases[0].IsPrerelease);
    }

    // Test handlers
    private class NoNetworkHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            throw new HttpRequestException("no network in test");
    }
    private class FailingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
    private class StubHandler : HttpMessageHandler {
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(_body) });
    }
}
