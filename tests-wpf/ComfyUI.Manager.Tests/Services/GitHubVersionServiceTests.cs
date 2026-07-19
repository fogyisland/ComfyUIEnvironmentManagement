using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class GitHubVersionServiceTests
{
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
}
