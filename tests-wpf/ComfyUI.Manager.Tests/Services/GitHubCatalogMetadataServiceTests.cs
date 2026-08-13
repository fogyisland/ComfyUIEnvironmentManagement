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

        var count = await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

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

        var count = await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

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
            await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None));
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
        var count = await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

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
        var count = await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

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
        await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

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
        await svc.EnrichAsync(new[] { entry }, null, ct: CancellationToken.None);

        Assert.Equal(new[] { "foo", "bar", "baz" }, entry.Tags);
    }

    /// <summary>
    /// v0.6.14: 7 字段(html_url/homepage/language/forks_count/open_issues_count/
    /// subscribers_count/created_at)从 /repos 响应提取。release_tag 从 /releases/latest。
    /// 零新 API call — 走既有 round 1 + round 2 路径。
    /// </summary>
    [Fact]
    public async Task EnrichOneAsync_Extracts8NewFields_FromExistingJsonResponses()
    {
        // 模拟 /repos + /releases/latest 的 GitHub API 响应
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/repos/o/r") && !url.Contains("/releases") && !url.Contains("/readme") && !url.Contains("/commits"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(@"{
                            ""html_url"": ""https://github.com/o/r"",
                            ""homepage"": ""https://example.com"",
                            ""language"": ""Python"",
                            ""forks_count"": 42,
                            ""open_issues_count"": 7,
                            ""subscribers_count"": 100,
                            ""created_at"": ""2025-01-01T00:00:00Z"",
                            ""license"": { ""spdx_id"": ""MIT"" },
                            ""stargazers_count"": 1000,
                            ""archived"": false,
                            ""topics"": [""img2img""],
                            ""pushed_at"": ""2026-08-10T12:00:00Z""
                        }", System.Text.Encoding.UTF8, "application/json"),
                    };
                }
                if (url.Contains("/releases/latest"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(@"{
                            ""tag_name"": ""v2.0.0"",
                            ""body"": ""## v2.0.0\n- new feature"",
                            ""assets"": []
                        }", System.Text.Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
        var http = new HttpClient(handler.Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new GitHubCatalogMetadataService(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        await InvokeEnrichOne(svc, entry);

        Assert.Equal("https://github.com/o/r", entry.HtmlUrl);
        Assert.Equal("https://example.com", entry.Homepage);
        Assert.Equal("Python", entry.Language);
        Assert.Equal(42, entry.ForksCount);
        Assert.Equal(7, entry.OpenIssuesCount);
        Assert.Equal(100, entry.SubscribersCount);
        Assert.Equal("2025-01-01T00:00:00Z", entry.CreatedAt);
        Assert.Equal("v2.0.0", entry.ReleaseTag);
        // v0.6.13-B 既有字段不破坏
        Assert.Equal("MIT", entry.License);
        Assert.Equal(1000, entry.Stars);
    }

    /// <summary>
    /// v0.6.14: /repos 响应缺字段时,8 新字段全部 null — 不抛异常(strict null-check)。
    /// </summary>
    [Fact]
    public async Task EnrichOneAsync_MissingFieldsInResponse_NewFieldsStayNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{
                    ""stargazers_count"": 100
                }", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler.Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new GitHubCatalogMetadataService(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        await InvokeEnrichOne(svc, entry);

        Assert.Null(entry.HtmlUrl);
        Assert.Null(entry.Homepage);
        Assert.Null(entry.Language);
        Assert.Equal(0, entry.ForksCount);
        Assert.Equal(0, entry.OpenIssuesCount);
        Assert.Null(entry.ReleaseTag);
        Assert.Equal(0, entry.SubscribersCount);
        Assert.Null(entry.CreatedAt);
        // v0.6.13-B 字段仍正常
        Assert.Equal(100, entry.Stars);
    }

    /// <summary>
    /// v0.6.14: 沿用 v0.6.13-B Fake subclass override pattern — 验证 class 不 sealed
    /// 让 subclass override <see cref="GitHubCatalogMetadataService.EnrichAsync"/> 直接
    /// 绕过 HTTP 测字段提取。
    /// </summary>
    [Fact]
    public async Task EnrichAsync_FakeSubclass_OverridesToSetHtmlUrl()
    {
        var http = new HttpClient(new Mock<HttpMessageHandler>().Object);
        var cache = new MetadataCache(Path.Combine(Path.GetTempPath(), $"meta-{Guid.NewGuid():N}.json"));
        var settings = new Settings { GitHubToken = "" };
        var svc = new FakeMetadataServiceWithHtmlOverride(http, cache, settings);

        var entry = new CatalogEntry
        {
            Id = "x1",
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/o/r",
            },
        };
        var done = await svc.EnrichAsync(new[] { entry });

        Assert.Equal(1, done);
        Assert.Equal("https://fake.override/x1", entry.HtmlUrl);
    }

    private sealed class FakeMetadataServiceWithHtmlOverride : GitHubCatalogMetadataService
    {
        public FakeMetadataServiceWithHtmlOverride(
            HttpClient http, MetadataCache cache, Settings settings)
            : base(http, cache, settings) { }

        // 整体 override EnrichAsync(它是 virtual)— 跳过真实 HTTP flow,
        // 直接把 entry.HtmlUrl 设成 magic string 让 test 验 Fake override 生效。
        // 这同时验证 class 不 sealed(v0.6.13-B.1 lesson)+ EnrichAsync 是 virtual。
        public override async Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            foreach (var e in entries)
            {
                e.HtmlUrl = $"https://fake.override/{e.Id}";
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            return entries.Count;
        }
    }

    /// <summary>
    /// v0.6.14: EnrichOneAsync 是 private,test 通过 reflection 调。
    /// </summary>
    private static async Task InvokeEnrichOne(
        GitHubCatalogMetadataService svc, CatalogEntry entry)
    {
        var method = typeof(GitHubCatalogMetadataService).GetMethod(
            "EnrichOneAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);  // 确保方法存在
        var task = (Task<bool>)method!.Invoke(svc, new object[] { entry, default(CancellationToken) })!;
        await task;
    }
}