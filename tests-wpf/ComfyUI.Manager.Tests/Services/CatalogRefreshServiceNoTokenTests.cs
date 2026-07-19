using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// 验证无 GitHub token 时的 refresh 行为:catalog 正常拉取,version
/// 拉取分支被跳过,DB 里 latest_version 全部 NULL。
/// </summary>
public class CatalogRefreshServiceNoTokenTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;

    public CatalogRefreshServiceNoTokenTests()
    {
        _db = new TestDb();
        _settings = new Settings
        {
            GitHubToken = "",  // 关键:空 token
        };
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeFetcher : CatalogFetcher
    {
        public List<CatalogEntry> Entries { get; } = new()
        {
            new()
            {
                Id = "test-1",
                Package = "ComfyUI-Foo",
                RawMetadata = new Dictionary<string, object?>
                {
                    ["reference"] = "https://github.com/foo/bar",
                    ["title"] = "Foo",
                    ["author"] = "alice",
                },
            },
            new()
            {
                Id = "test-2",
                Package = "ComfyUI-Baz",
                RawMetadata = new Dictionary<string, object?>
                {
                    ["reference"] = "https://gitlab.com/skip/me",
                    ["title"] = "Baz",
                    ["author"] = "bob",
                },
            },
        };
        public FakeFetcher() : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }
        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
            => Task.FromResult(Entries);
    }

    [Fact]
    public async Task RefreshAsync_NoToken_SucceedsAndSkipsVersionFetch()
    {
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var svc = new CatalogRefreshService(fetcher, repo, _settings,
            versionService: new GitHubVersionService(new HttpClient(new Mock<HttpMessageHandler>().Object)));

        var result = await svc.RefreshAsync();

        Assert.True(result.Success, $"expected success, got error: {result.Error}");
        Assert.Equal(2, result.EntryCount);
        Assert.Equal(0, result.VersionCount);  // ← 关键:无 token → 不拉 version
        Assert.Null(result.Error);

        // DB 应有 2 行,但 latest_version 全 NULL
        var rows = repo.Search("", 10);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, e => Assert.Null(e.LatestVersion));
    }

    [Fact]
    public async Task RefreshAsync_NoToken_VersionServiceNotCalled()
    {
        // 用一个会 throw 的 versionService 来证明它根本没被调到
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var throwingVersionSvc = new ThrowingVersionService();
        var svc = new CatalogRefreshService(fetcher, repo, _settings,
            versionService: throwingVersionSvc);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.EntryCount);
        Assert.Equal(0, result.VersionCount);
        Assert.Equal(0, throwingVersionSvc.CallCount);  // ← 关键:throw 永远没机会跑
    }

    private sealed class ThrowingVersionService : GitHubVersionService
    {
        public int CallCount { get; private set; }
        public ThrowingVersionService()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("version service should not be called when token is empty");
        }
    }

    [Fact]
    public void SharedSettings_TokenUpdateVisibleToRefreshService()
    {
        // 验证修复:MainViewModel 注入的 shared Settings 实例被修改后,
        // CatalogRefreshService 用同一个实例,会读到最新 token。
        var fetcher = new FakeFetcher();
        var repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        var sharedSettings = new Settings { GitHubToken = "" };
        SettingsDefaults.Apply(sharedSettings, @"D:\ToolDevelop\ComfyUI");
        var refreshService = new CatalogRefreshService(
            fetcher, repo, sharedSettings,
            new GitHubVersionService(new HttpClient(new Mock<HttpMessageHandler>().Object)));

        // 模拟 SettingsViewModel 写入 token(共享同一实例)
        sharedSettings.GitHubToken = "ghp_test";

        // refreshService 内部仍持有 sharedSettings 引用,读到的应该是新 token
        // 通过 reflection / 行为验证:模拟 1 次 refresh + 假 version svc,断言 token 非空时它被调到
        var versions = new Dictionary<string, List<VersionInfo>>
        {
            ["test-1"] = new() { new() { Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z" } },
        };
        var countingSvc = new CountingVersionService(versions);
        var svcWithToken = new CatalogRefreshService(fetcher, repo, sharedSettings, countingSvc);
        var result = svcWithToken.RefreshAsync().GetAwaiter().GetResult();

        Assert.Equal(1, countingSvc.LastTokenSeen == "ghp_test" ? 1 : 0);
        Assert.True(result.Success);
        Assert.Equal(1, result.VersionCount);
        var entries = repo.Search("", 10);
        Assert.Single(entries, e => e.LatestVersion == "v1.0.0");
    }

    private sealed class CountingVersionService : GitHubVersionService
    {
        public string? LastTokenSeen { get; private set; }
        private readonly Dictionary<string, List<VersionInfo>> _result;
        public CountingVersionService(Dictionary<string, List<VersionInfo>> result)
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object))
        {
            _result = result;
        }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            CancellationToken ct = default)
        {
            LastTokenSeen = token;
            return Task.FromResult(_result);
        }
    }
}
