// v1.0.0.x (2026-09-16) user「云端其实是有这个功能,只是没有写入」+ B 方案:
// 测 DetailRow.RefreshFromRawJson() 从 raw_json 懒解析 branches/recentReleases/
// releaseCount/latestRelease 4 字段。
//
// 不开 SQLite 新列,启动时一次 parse,详情面板直接显示。
// null/空 raw_json / 非 JSON / 无 repository 嵌套 → 全部空值。

using System.Linq;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class DetailRowRawJsonTests
{
    // 用 NIMnodes 真实 raw_json(3 个 release,有 branches/releaseCount/latestRelease)
    private const string NimnodesRawJson = """
    {
      "fetch_status": "ok",
      "fetched_at": "2026-09-15T20:00:00Z",
      "stale": false,
      "warning": null,
      "repository": {
        "name": "NIMnodes",
        "description": "test",
        "stars": 100,
        "forks": 5,
        "watchers": 3,
        "language": "Python",
        "license": "MIT",
        "defaultBranch": "main",
        "pushedAt": "2025-08-14T01:16:03Z",
        "updatedAt": "2025-08-14T01:16:03Z",
        "createdAt": "2025-06-06T18:00:00Z",
        "homepage": null,
        "archived": false,
        "disabled": false,
        "private": false,
        "topics": ["ai", "comfy"],
        "branches": [
          {"name": "main", "protected": false, "commit_sha": "abc123"}
        ],
        "releaseCount": 3,
        "recentReleases": [
          {"tag_name": "1.1.0", "name": "Release 1.1.0", "published_at": "2025-08-14T01:16:03Z", "prerelease": false},
          {"tag_name": "1.0.1", "name": "Release 1.0.1", "published_at": "2025-06-06T19:08:42Z", "prerelease": false},
          {"tag_name": "1.0.0", "name": "Release 1.0.0", "published_at": "2025-06-06T18:57:09Z", "prerelease": false}
        ],
        "latestRelease": null
      }
    }
    """;

    [Fact]
    public void RefreshFromRawJson_NimnodesShape_ParsesBranchesAndReleases()
    {
        var row = new NodelistViewModel.DetailRow { RawJson = NimnodesRawJson };
        row.RefreshFromRawJson();

        // branches:["main"]
        Assert.Single(row.BranchNames);
        Assert.Equal("main", row.BranchNames[0]);

        // recentReleases:[{tag} ({YYYY-MM-DD})] x 3
        Assert.Equal(3, row.RecentReleases.Count);
        Assert.Equal("1.1.0 (2025-08-14)", row.RecentReleases[0]);
        Assert.Equal("1.0.0 (2025-06-06)", row.RecentReleases[2]);

        // releaseCount=3
        Assert.Equal(3, row.ReleaseCount);

        // latestRelease=null 但 recentReleases[0].tag_name="1.1.0" → fallback
        Assert.Equal("1.1.0", row.LatestReleaseTag);
    }

    [Fact]
    public void RefreshFromRawJson_NullRawJson_LeavesAllEmpty()
    {
        var row = new NodelistViewModel.DetailRow { RawJson = null };
        row.RefreshFromRawJson();

        Assert.Empty(row.BranchNames);
        Assert.Empty(row.RecentReleases);
        Assert.Null(row.ReleaseCount);
        Assert.Null(row.LatestReleaseTag);
    }

    [Fact]
    public void RefreshFromRawJson_EmptyRawJson_LeavesAllEmpty()
    {
        var row = new NodelistViewModel.DetailRow { RawJson = "" };
        row.RefreshFromRawJson();

        Assert.Empty(row.BranchNames);
        Assert.Empty(row.RecentReleases);
        Assert.Null(row.ReleaseCount);
        Assert.Null(row.LatestReleaseTag);
    }

    [Fact]
    public void RefreshFromRawJson_InvalidJson_DoesNotThrow_LeavesAllEmpty()
    {
        var row = new NodelistViewModel.DetailRow { RawJson = "{not valid json" };
        row.RefreshFromRawJson();

        Assert.Empty(row.BranchNames);
        Assert.Empty(row.RecentReleases);
        Assert.Null(row.ReleaseCount);
        Assert.Null(row.LatestReleaseTag);
    }

    [Fact]
    public void RefreshFromRawJson_NoRepositoryWrapper_FallsBackToTopLevel()
    {
        // GitHub 原生顶层格式(没 repository.*)→ 应该走 root 分支
        var githubShape = """
        {
          "name": "test",
          "stargazers_count": 42,
          "forks_count": 5,
          "branches": [{"name": "master"}],
          "releaseCount": 0
        }
        """;
        var row = new NodelistViewModel.DetailRow { RawJson = githubShape };
        row.RefreshFromRawJson();

        // branches 顶层能取到
        Assert.Single(row.BranchNames);
        Assert.Equal("master", row.BranchNames[0]);
        Assert.Equal(0, row.ReleaseCount);
    }

    [Fact]
    public void RefreshFromRawJson_LatestReleaseTag_PreferredOverRecentReleases()
    {
        // latestRelease.tag_name 存在 → 用它,不用 recentReleases[0]
        var json = """
        {
          "repository": {
            "branches": [{"name": "main"}],
            "releaseCount": 5,
            "recentReleases": [
              {"tag_name": "0.9.0", "published_at": "2025-09-01T00:00:00Z"}
            ],
            "latestRelease": {"tag_name": "1.0.0", "published_at": "2025-10-01T00:00:00Z"}
          }
        }
        """;
        var row = new NodelistViewModel.DetailRow { RawJson = json };
        row.RefreshFromRawJson();

        Assert.Equal("1.0.0", row.LatestReleaseTag);
        Assert.Equal(5, row.ReleaseCount);
    }

    [Fact]
    public void RefreshFromRawJson_ReleaseCountFallback_FromRecentReleasesLength()
    {
        // releaseCount 缺失 → 兜底 recentReleases.Count
        var json = """
        {
          "repository": {
            "branches": [{"name": "main"}],
            "recentReleases": [
              {"tag_name": "1.0.0", "published_at": "2025-09-01T00:00:00Z"},
              {"tag_name": "0.9.0", "published_at": "2025-08-01T00:00:00Z"}
            ]
          }
        }
        """;
        var row = new NodelistViewModel.DetailRow { RawJson = json };
        row.RefreshFromRawJson();

        Assert.Equal(2, row.ReleaseCount);
        // latest 从 recentReleases[0] fallback
        Assert.Equal("1.0.0", row.LatestReleaseTag);
    }
}