using System;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-16) T43i.2-fix+user「怎么这么多 null 的」:Parse 函数兼容两种 API 格式的单元测试。
///
/// 用户 host (github.pudafo.com) 返回嵌套 + camelCase:
///   { "fetch_status": "ok", "repository": { "stars": N, "pushedAt": "...", "language": "...", ... } }
///
/// GitHub 原生 API 返回顶层 + snake_case:
///   { "stargazers_count": N, "pushed_at": "...", "language": "...", ... }
///
/// 两种都要正确解析,不能返回 null。
/// </summary>
public class NodeRepoQueryServiceParseTests
{
    [Fact]
    public void Parse_CustomApiNestedFormat_ReadsAllFields()
    {
        // 用户自定义 API 的真实返回格式(curl 验证过)
        var raw = """
            {
              "fetch_status": "ok",
              "fetched_at": "2026-09-15T00:00:00Z",
              "stale": false,
              "warning": null,
              "repository": {
                "name": "ComfyUI-Manager",
                "forks": 2474,
                "stars": 16144,
                "topics": ["management", "core"],
                "license": "GPL-3.0",
                "private": false,
                "archived": false,
                "branches": [],
                "disabled": false,
                "homepage": "",
                "language": "Python",
                "pushedAt": "2026-09-13T11:01:28Z",
                "watchers": 81,
                "createdAt": "2023-04-23T14:36:09Z",
                "updatedAt": "2026-09-15T03:00:56Z",
                "description": "ComfyUI-Manager is an extension designed to enhance the usability of ComfyUI.",
                "releaseCount": 0,
                "defaultBranch": "main",
                "latestRelease": null,
                "recentReleases": []
              }
            }
            """;

        var meta = NodeRepoQueryService.Parse("ltdrdata", "ComfyUI-Manager", "https://github.pudafo.com", raw);

        Assert.NotNull(meta);
        Assert.Equal("ltdrdata", meta!.Owner);
        Assert.Equal("ComfyUI-Manager", meta.Repo);
        // 自定义 API 字段读取验证
        Assert.Equal(16144, meta.Stars);
        Assert.Equal(2474, meta.Forks);
        Assert.Equal(81, meta.Watchers);
        Assert.Equal("GPL-3.0", meta.License);
        Assert.Equal("Python", meta.Language);
        Assert.Equal("main", meta.DefaultBranch);
        Assert.NotNull(meta.PushedAt);
        Assert.Equal(2026, meta.PushedAt!.Value.Year);
        Assert.NotNull(meta.UpdatedAt);
        Assert.Equal("management,core", meta.Topics);
        Assert.NotNull(meta.Description);
        Assert.Contains("enhance", meta.Description);
        // htmlUrl 兜底:自定义 API 没这字段 → 应生成 github.com 兜底 URL
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager", meta.HtmlUrl);
        // openIssues 自定义 API 没这字段 → null
        Assert.Null(meta.OpenIssues);
    }

    [Fact]
    public void Parse_GitHubNativeFormat_StillWorks()
    {
        // GitHub 原生 API /repos/{owner}/{repo} 的格式(snake_case 顶层)
        var raw = """
            {
              "name": "ComfyUI-Manager",
              "stargazers_count": 16144,
              "watchers_count": 81,
              "subscribers_count": 75,
              "forks_count": 2474,
              "language": "Python",
              "license": { "spdx_id": "GPL-3.0", "name": "GNU General Public License v3.0" },
              "default_branch": "main",
              "updated_at": "2026-09-15T03:00:56Z",
              "pushed_at": "2026-09-13T11:01:28Z",
              "html_url": "https://github.com/ltdrdata/ComfyUI-Manager",
              "description": "ComfyUI-Manager description",
              "topics": ["management"],
              "open_issues_count": 42
            }
            """;

        var meta = NodeRepoQueryService.Parse("ltdrdata", "ComfyUI-Manager", "https://api.github.com", raw);

        Assert.NotNull(meta);
        Assert.Equal(16144, meta!.Stars);
        Assert.Equal(2474, meta.Forks);
        Assert.Equal(75, meta.Watchers);  // subscribers_count 优先
        Assert.Equal("GPL-3.0", meta.License);
        Assert.Equal("Python", meta.Language);
        Assert.Equal("main", meta.DefaultBranch);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager", meta.HtmlUrl);
        Assert.Equal(42, meta.OpenIssues);
        Assert.Equal("management", meta.Topics);
    }

    [Fact]
    public void Parse_CustomApiMissingTopics_ReturnsNullTopics()
    {
        // 一些 repo 没有 topics 字段
        var raw = """
            {
              "fetch_status": "ok",
              "repository": {
                "name": "minimal-repo",
                "stars": 5,
                "language": null,
                "pushedAt": "2026-01-01T00:00:00Z"
              }
            }
            """;

        var meta = NodeRepoQueryService.Parse("foo", "minimal-repo", "https://github.pudafo.com", raw);

        Assert.NotNull(meta);
        Assert.Equal(5, meta!.Stars);
        Assert.Null(meta.Topics);
        Assert.Null(meta.Language);
        Assert.Null(meta.License);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        // 防御性:解析失败应返回 null(不抛异常)
        var raw = "{ this is not valid json";
        var meta = NodeRepoQueryService.Parse("foo", "bar", "https://github.pudafo.com", raw);
        Assert.Null(meta);
    }
}