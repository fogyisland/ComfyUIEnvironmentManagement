using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>v0.6.13-B: GitHubCatalogMetadataService 2 轮轮询 + retry + rate limit + skip non-GitHub。</summary>
public class GitHubCatalogMetadataServiceTests : IDisposable
{
    private readonly string _cacheFile;
    private readonly Mock<HttpMessageHandler> _handler;
    private readonly HttpClient _http;

    public GitHubCatalogMetadataServiceTests()
    {
        _cacheFile = Path.Combine(Path.GetTempPath(), $"svc-cache-{Guid.NewGuid():N}.json");
        _handler = new Mock<HttpMessageHandler>();
        _http = new HttpClient(_handler.Object);
    }

    public void Dispose()
    {
        if (File.Exists(_cacheFile)) File.Delete(_cachePath());
    }

    private string _cachePath() => _cacheFile;

    private GitHubCatalogMetadataService CreateService()
        => new(_http, new MetadataCache(_cachePath()), new Settings());

    private static CatalogEntry MakeEntry(string id, string pkg, string reference)
        => new()
        {
            Id = id, Package = pkg,
            SourceUrl = "https://example.com/catalog.json",
            RawMetadata = new Dictionary<string, object?> { ["reference"] = reference },
            CachedAt = "2026-08-12T00:00:00", ExpiresAt = "2026-08-13T00:00:00",
        };

    private void MockJsonResponse(string url, HttpStatusCode status, string body,
        IDictionary<string, string>? headers = null)
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().EndsWith(url)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                _handler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body),
                    })
                    .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                    {
                        // can't easily add headers here; for rate limit test we'll use a different mock
                    });
            }
        }
    }

    [Fact]
    public async Task EnrichAsync_GitHubRef_FetchesAllFields()
    {
        // Round 1: /repos/foo/bar
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""license"": { ""spdx_id"": ""MIT"" },
                    ""stargazers_count"": 1234,
                    ""archived"": false,
                    ""topics"": [""img2img"", ""controlnet""],
                    ""pushed_at"": ""2026-08-10T12:34:56Z""
                }"),
            });
        // Round 2a: /repos/foo/bar/readme (base64 of "# hi")
        var readmeContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("# hi\n\nworld"));
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/readme"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($@"{{ ""content"": ""{readmeContent}"" }}"),
            });
        // Round 2b: /repos/foo/bar/commits/latest
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/commits/latest"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""sha"": ""abc123"",
                    ""commit"": { ""author"": { ""date"": ""2026-08-10T12:00:00Z"" } }
                }"),
            });
        // Round 2c: /repos/foo/bar/releases/latest
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/releases/latest"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""body"": ""## v1.2.3\n- fix bug"",
                    ""assets"": [
                        { ""download_count"": 100 },
                        { ""download_count"": 200 }
                    ]
                }"),
            });

        var entry = MakeEntry("foo-bar", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();

        var count = await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal("MIT", entry.License);
        Assert.Equal(1234, entry.Stars);
        Assert.False(entry.Deprecated);
        Assert.Equal(new[] { "img2img", "controlnet" }, entry.Tags);
        Assert.Equal("# hi\n\nworld", entry.ReadmeMarkdown);
        Assert.Equal("## v1.2.3\n- fix bug", entry.LatestChangelog);
        Assert.Equal(300, entry.Downloads);  // 100 + 200
        Assert.Equal("2026-08-10T12:00:00Z", entry.LastCommit);
        Assert.NotNull(entry.MetadataFetchedAt);
    }

    [Fact]
    public async Task EnrichAsync_NonGithubRef_Skipped()
    {
        var entry = MakeEntry("g", "Pkg", "https://gitlab.com/foo/bar");
        var svc = CreateService();

        var count = await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Null(entry.License);
        _handler.Protected().Verify(
            "SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task EnrichAsync_RateLimit_Throws()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(@"{""message"":""API rate limit exceeded""}"),
        };
        resp.Headers.Add("X-RateLimit-Remaining", "0");
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(resp);

        var entry = MakeEntry("rl", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();

        await Assert.ThrowsAsync<RateLimitException>(async () =>
            await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None));
    }

    [Fact]
    public async Task EnrichAsync_RetryOn503()
    {
        var callCount = 0;
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                // 2nd call: round 1 success
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                        ""license"": { ""spdx_id"": ""MIT"" },
                        ""stargazers_count"": 5,
                        ""archived"": false,
                        ""topics"": [],
                        ""pushed_at"": ""2026-08-10T12:00:00Z""
                    }"),
                };
            });

        var entry = MakeEntry("retry", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();
        var count = await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal("MIT", entry.License);
        Assert.True(callCount >= 2);  // at least 2 HTTP calls (1 fail + 1 success)
    }

    [Fact]
    public async Task EnrichAsync_ReadmeNotFound_LeavesFieldNull()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""license"": { ""spdx_id"": ""GPL-3.0"" },
                    ""stargazers_count"": 10,
                    ""archived"": true,
                    ""topics"": [],
                    ""pushed_at"": ""2026-08-01T00:00:00Z""
                }"),
            });
        // readme 404
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/readme"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        // commits 404
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/commits/latest"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        // releases 404
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/releases/latest"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var entry = MakeEntry("nf", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();
        var count = await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, count);  // entry still counted as enriched (Round 1 succeeded)
        Assert.Equal("GPL-3.0", entry.License);
        Assert.Null(entry.ReadmeMarkdown);
        Assert.Null(entry.LatestChangelog);
        Assert.True(entry.Deprecated);
    }

    [Fact]
    public async Task EnrichAsync_DownloadsSummed()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""license"": null,
                    ""stargazers_count"": 0,
                    ""archived"": false,
                    ""topics"": [],
                    ""pushed_at"": ""2026-08-01T00:00:00Z""
                }"),
            });
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar/releases/latest"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""body"": """",
                    ""assets"": [
                        { ""download_count"": 1 },
                        { ""download_count"": 2 },
                        { ""download_count"": 3 },
                        { ""download_count"": 4 }
                    ]
                }"),
            });
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/readme")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/commits/latest")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var entry = MakeEntry("dl", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();
        await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(10, entry.Downloads);  // 1+2+3+4
    }

    [Fact]
    public async Task EnrichAsync_TagsFlattened()
    {
        // Moq uses last-matching-setup-wins; register catchall first, specific last
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath == "/repos/foo/bar"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""license"": null,
                    ""stargazers_count"": 0,
                    ""archived"": false,
                    ""topics"": [""foo"", ""bar"", ""baz""],
                    ""pushed_at"": ""2026-08-01T00:00:00Z""
                }"),
            });

        var entry = MakeEntry("tg", "Pkg", "https://github.com/foo/bar");
        var svc = CreateService();
        await svc.EnrichAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(new[] { "foo", "bar", "baz" }, entry.Tags);
    }
}