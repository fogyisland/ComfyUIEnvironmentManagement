using System;
using System.Collections.Generic;
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

public class GitHubVersionServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public GitHubVersionServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gvsvc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
    [Theory]
    [InlineData("https://github.com/ltdrdata/ComfyUI-Manager", "ltdrdata", "ComfyUI-Manager")]
    [InlineData("https://github.com/ltdrdata/ComfyUI-Manager.git", "ltdrdata", "ComfyUI-Manager")]
    [InlineData("https://github.com/foo/bar/", "foo", "bar")]
    [InlineData("http://github.com/Owner/Repo", "Owner", "Repo")]
    public void ParseRepo_ValidUrl_ReturnsOwnerAndRepo(string url, string owner, string repo)
    {
        var (o, r) = GitHubVersionService.ParseRepo(url);
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://gitlab.com/foo/bar")]
    [InlineData("not a url")]
    [InlineData("https://github.com/")]
    [InlineData("https://example.com/foo/bar")]
    public void ParseRepo_NonGithubUrl_ReturnsNulls(string? url)
    {
        var (o, r) = GitHubVersionService.ParseRepo(url);
        Assert.Null(o);
        Assert.Null(r);
    }

    [Fact]
    public async Task FetchVersionsAsync_GitHubRepo_ReturnsTags()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    "[{\"tag_name\":\"v1.2.3\",\"published_at\":\"2025-06-01T00:00:00Z\",\"prerelease\":false,\"draft\":false}]"),
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        var nodes = new List<(string, string)>
        {
            ("id-1", "https://github.com/foo/bar"),
            ("id-2", "https://gitlab.com/skip/me"),
            ("id-3", "https://github.com/baz/qux"),
        };
        var result = await svc.FetchVersionsAsync(nodes, token: null);

        Assert.Equal(2, result.Count);
        Assert.Single(result["id-1"]);
        Assert.Equal("v1.2.3", result["id-1"][0].Tag);
        Assert.Equal("2025-06-01T00:00:00Z", result["id-1"][0].PublishedAt);
        Assert.False(result["id-1"][0].IsPrerelease);
        Assert.Single(result["id-3"]);
    }

    [Fact]
    public async Task FetchVersionsAsync_ApiError_SkipsThatNode()
    {
        var handler = new Mock<HttpMessageHandler>();
        int callCount = 0;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound }
                    : new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(
                            "[{\"tag_name\":\"v0.1.0\",\"published_at\":\"2025-01-01T00:00:00Z\",\"prerelease\":false,\"draft\":false}]"),
                    };
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        var nodes = new List<(string, string)>
        {
            ("fail", "https://github.com/foo/missing"),
            ("ok", "https://github.com/foo/bar"),
        };
        var result = await svc.FetchVersionsAsync(nodes, token: null);

        Assert.Single(result);
        Assert.Single(result["ok"]);
        Assert.Equal("v0.1.0", result["ok"][0].Tag);
        Assert.False(result.ContainsKey("fail"));
    }

    [Fact]
    public async Task FetchVersionsAsync_WithToken_AddsBearerAuth()
    {
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("[]"),
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        await svc.FetchVersionsAsync(
            new List<(string, string)> { ("id-1", "https://github.com/o/r") },
            token: "ghp_xxx");

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_xxx", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task FetchVersionsAsync_FiltersDrafts_AndSortsByPublishedAtDesc()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"[
                    {""tag_name"":""v1.0.0"",""published_at"":""2024-01-01T00:00:00Z"",""prerelease"":false,""draft"":false},
                    {""tag_name"":""v2.0.0-draft"",""published_at"":""2025-06-01T00:00:00Z"",""prerelease"":false,""draft"":true},
                    {""tag_name"":""v2.0.0-rc1"",""published_at"":""2025-03-01T00:00:00Z"",""prerelease"":true,""draft"":false},
                    {""tag_name"":""v2.0.0"",""published_at"":""2025-05-01T00:00:00Z"",""prerelease"":false,""draft"":false}
                ]"),
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        var result = await svc.FetchVersionsAsync(
            new List<(string, string)> { ("id-1", "https://github.com/o/r") }, null);

        Assert.Single(result);
        var list = result["id-1"];
        Assert.Equal(3, list.Count);  // draft 被过滤
        Assert.Equal("v2.0.0", list[0].Tag);   // 2025-05
        Assert.Equal("v2.0.0-rc1", list[1].Tag);  // 2025-03
        Assert.Equal("v1.0.0", list[2].Tag);   // 2024-01
        Assert.True(list[1].IsPrerelease);
    }

    [Fact]
    public async Task FetchVersionsAsync_CancellationRequested_StopsAndThrows()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async () =>
            {
                await Task.Delay(200);
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"tag_name\":\"x\"}"),
                };
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await svc.FetchVersionsAsync(
                new List<(string, string)> { ("id-1", "https://github.com/o/r") },
                token: null, progress: null, ct: cts.Token));
    }

    [Fact]
    public async Task FetchVersionsAsync_ReportsProgress()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"tag_name\":\"v1\"}"),
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        var reported = new List<VersionFetchProgress>();
        var progress = new Progress<VersionFetchProgress>(p => reported.Add(p));
        var nodes = new List<(string, string)>
        {
            ("a", "https://github.com/o/r1"),
            ("b", "https://github.com/o/r2"),
            ("c", "https://github.com/o/r3"),
        };
        await svc.FetchVersionsAsync(nodes, token: null, progress: progress);

        // Progress<T> 是异步回调,等待一点时间让它跑完
        await Task.Delay(100);

        Assert.Equal(3, reported.Count);
        Assert.Equal(3, reported[^1].Completed);
        Assert.Equal(3, reported[^1].Total);
    }

    /// <summary>
    /// v0.6.13-B.2 hotfix:403 + X-RateLimit-Remaining=0 → 旧行为 throw 让顶层
    /// catch fail-soft。但 v0.6.14 发现这会丢掉所有 partial results ——
    /// `Task.WhenAll` 立即 aggregate throw,前面已经 lock 写入 result 字典的
    /// 数据全部丢失,下次 refresh 还会再撞同样的 5000 entries,死循环。
    ///
    /// v0.6.14.1 hotfix:rate limit 时**不抛** RateLimitException,改回
    /// (empty, RateLimitHit=true) tuple;FetchVersionsAsync 写共享标志 +
    /// log Warn("version-rate-limit"),return 当前 partial result。partial
    /// data 落库后下次 refresh hash-diff 短路不再撞,自然恢复。
    /// </summary>
    [Fact]
    public async Task FetchVersionsAsync_RateLimited_ReturnsPartialAndLogsNoThrow()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                resp.Headers.Add("X-RateLimit-Reset", "1700000000");
                return resp;
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);
        using var logger = new AppLogger(_tempRoot);

        var nodes = new List<(string, string)>
        {
            ("a", "https://github.com/o/r1"),
            ("b", "https://github.com/o/r2"),
        };

        // 不抛,partial result(空)正常返回
        var result = await svc.FetchVersionsAsync(nodes, token: null, logger: logger);

        Assert.Empty(result);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[version-rate-limit]") && l.Contains("[WARN"));
    }

    /// <summary>
    /// v0.6.14.1 hotfix 主战场:并发跑 N 个 entry,前 K 个成功,K+1 个撞 rate
    /// limit。期望:K 个成功结果保留在 result 字典,撞 rate limit 的不抛。
    /// 旧实现:K 个结果被 Task.WhenAll aggregate exception 吞掉,result 是空。
    /// </summary>
    [Fact]
    public async Task FetchVersionsAsync_PartialSuccessBeforeRateLimit_RetainsSuccessfulResults()
    {
        var handler = new Mock<HttpMessageHandler>();
        int callCount = 0;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(
                            $"[{{\"tag_name\":\"v0.{callCount}.0\",\"published_at\":\"2025-0{callCount}-01T00:00:00Z\",\"prerelease\":false,\"draft\":false}}]"),
                    };
                }
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                return resp;
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);
        using var logger = new AppLogger(_tempRoot);

        var nodes = new List<(string, string)>
        {
            ("a", "https://github.com/o/r1"),
            ("b", "https://github.com/o/r2"),
            ("c", "https://github.com/o/r3"),
            ("d", "https://github.com/o/r4"),
            ("e", "https://github.com/o/r5"),
        };

        var result = await svc.FetchVersionsAsync(nodes, token: null, logger: logger);

        // 前 2 个成功保留,后面 3 个 rate limit 跳过
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("a"));
        Assert.True(result.ContainsKey("b"));
        Assert.Equal("v0.1.0", result["a"][0].Tag);
        Assert.Equal("v0.2.0", result["b"][0].Tag);
        Assert.False(result.ContainsKey("c"));
        Assert.False(result.ContainsKey("d"));
        Assert.False(result.ContainsKey("e"));
    }

    /// <summary>
    /// 403 但 X-RateLimit-Remaining > 0(暂时性封禁 / 二次验证 / 其他)→ 不抛,
    /// 按非 200 处理返回空 list(跟 NotFound / 5xx 同样的 fail-soft 路径)。
    /// </summary>
    [Fact]
    public async Task FetchVersionsAsync_ForbiddenButNotRateLimit_ReturnsEmpty()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Remaining", "42");
                return resp;
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        var nodes = new List<(string, string)>
        {
            ("a", "https://github.com/o/r1"),
        };
        var result = await svc.FetchVersionsAsync(nodes, token: null);

        Assert.Empty(result);
    }

    /// <summary>
    /// v0.6.14.1:单条 API GetLatestVersionAsync 撞 rate limit 仍 fail-fast
    /// 抛 RateLimitException(单条调用没 partial concerns,调用方通常
    /// catch 后展示错误)。批量 FetchVersionsAsync 不抛。
    /// </summary>
    [Fact]
    public async Task GetLatestVersionAsync_RateLimited_StillThrows()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                return resp;
            });
        var http = new HttpClient(handler.Object);
        var svc = new GitHubVersionService(http);

        await Assert.ThrowsAsync<RateLimitException>(async () =>
            await svc.GetLatestVersionAsync("https://github.com/o/r", token: null));
    }
}
