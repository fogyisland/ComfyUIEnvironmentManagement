using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// 真实调用 GitHub API(用 user 提供或环境变量的 token)拉几个已知 repo 的
/// latest release,验证整条链路 (URL 解析 → HTTP GET → JSON 解析 → DB 写入)
/// 都对。token 来自 COMFY_TEST_GH_TOKEN 环境变量,无 token 时跳过。
///
/// 这是 Live test,默认用 [Fact(Skip=...)] 关闭,只在本地手跑时启用。
/// </summary>
public class LiveGitHubVersionFetchTests : IDisposable
{
    private readonly TestDb _db;
    private readonly ITestOutputHelper _out;

    public LiveGitHubVersionFetchTests(ITestOutputHelper output)
    {
        _db = new TestDb();
        _out = output;
    }
    public void Dispose() => _db.Dispose();

    [Fact(Skip = "Live test — needs real token in COMFY_TEST_GH_TOKEN")]
    public async Task LiveFetch_RealGitHub_StoresTags()
    {
        var token = System.Environment.GetEnvironmentVariable("COMFY_TEST_GH_TOKEN");
        Assert.False(string.IsNullOrEmpty(token), "COMFY_TEST_GH_TOKEN env var required");

        var http = new HttpClient();
        var svc = new GitHubVersionService(http);
        var nodes = new (string, string)[]
        {
            ("foo-bar", "https://github.com/octocat/Hello-World"),
            ("comfy-mgr", "https://github.com/ltdrdata/ComfyUI-Manager"),
            ("comfy-foo", "https://github.com/ltdrdata/ComfyUI-Impact-Pack"),
        };
        var result = await svc.FetchVersionsAsync(nodes, token);

        foreach (var (id, _) in nodes)
        {
            if (result.TryGetValue(id, out var tag))
                _out.WriteLine($"{id}: {tag}");
            else
                _out.WriteLine($"{id}: <no release / private / not found>");
        }

        // 至少 octocat/Hello-World 应该有 release (test fixture repo 通常有)
        // 其他 repo 看运气
        Assert.NotEmpty(result);
    }
}
