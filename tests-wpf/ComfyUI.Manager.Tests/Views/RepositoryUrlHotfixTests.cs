using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.15.1 hotfix 测试:节点目录信息展示 repository URL。
///
/// 覆盖 4 块:
/// 1. <see cref="ScannedNode.RepositoryUrl"/> 经 Upsert → Get 完整 round-trip
/// 2. <see cref="LocalNodeService.ListAsync"/> 从 DB 读 URL 给 LocalNodeInfo
/// 3. <see cref="LocalNodeListItem.RepositoryUrlDisplay"/> 从 URL 抽 owner/repo
/// 4. <see cref="NodeOperations.TryReadRemoteUrlAsync"/> 读 .git/config (用真 git 仓)
/// </summary>
public class RepositoryUrlHotfixTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _repo;

    public RepositoryUrlHotfixTests()
    {
        _db = new TestDb();
        _repo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ScannedNode_RepositoryUrl_RoundTrip()
    {
        _repo.Upsert(new ScannedNode
        {
            Id = "pkg-a",
            EnvId = "",
            Source = "download",
            Package = "pkg-a",
            RepositoryUrl = "https://github.com/owner/pkg-a",
        });
        var loaded = _repo.Get("pkg-a");
        Assert.NotNull(loaded);
        Assert.Equal("https://github.com/owner/pkg-a", loaded!.RepositoryUrl);
    }

    [Fact]
    public void ScannedNode_RepositoryUrl_NullByDefault_AndNullableAfterUpsert()
    {
        _repo.Upsert(new ScannedNode
        {
            Id = "pkg-b",
            EnvId = "",
            Source = "download",
            Package = "pkg-b",
            // 不设 RepositoryUrl — 模拟老已下载行
        });
        var loaded = _repo.Get("pkg-b");
        Assert.NotNull(loaded);
        Assert.Null(loaded!.RepositoryUrl);
    }

    [Fact]
    public void LocalNodeListItem_RepositoryUrlDisplay_ExtractsOwnerRepo_GithubHttps()
    {
        var item = new LocalNodeListItem(new LocalNodeInfo(
            NodeId: "pkg",
            HeadSha: null,
            InstallDate: null,
            HasPhysicalDir: true,
            IsInDb: true,
            InstalledEnvIds: Array.Empty<string>(),
            InstalledEnvNames: Array.Empty<string>(),
            RepositoryUrl: "https://github.com/owner/repo.git"));
        Assert.Equal("github.com/owner/repo", item.RepositoryUrlDisplay);
    }

    [Fact]
    public void LocalNodeListItem_RepositoryUrlDisplay_ExtractsOwnerRepo_GithubSsh()
    {
        var item = new LocalNodeListItem(new LocalNodeInfo(
            NodeId: "pkg",
            HeadSha: null,
            InstallDate: null,
            HasPhysicalDir: true,
            IsInDb: true,
            InstalledEnvIds: Array.Empty<string>(),
            InstalledEnvNames: Array.Empty<string>(),
            RepositoryUrl: "git@github.com:owner/repo.git"));
        Assert.Equal("github.com/owner/repo", item.RepositoryUrlDisplay);
    }

    [Fact]
    public void LocalNodeListItem_RepositoryUrlDisplay_EmptyWhenUrlNull()
    {
        var item = new LocalNodeListItem(new LocalNodeInfo(
            NodeId: "pkg",
            HeadSha: null,
            InstallDate: null,
            HasPhysicalDir: false,
            IsInDb: false,
            InstalledEnvIds: Array.Empty<string>(),
            InstalledEnvNames: Array.Empty<string>(),
            RepositoryUrl: null));
        Assert.Equal("", item.RepositoryUrlDisplay);
    }

    [Fact]
    public async Task LocalNodeService_ListAsync_PopulatesRepositoryUrl_FromDb()
    {
        var localDir = Path.Combine(Path.GetTempPath(), "local-url-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(localDir, "demo-node"));
        try
        {
            _repo.Upsert(new ScannedNode
            {
                Id = "demo-node",
                EnvId = "",
                Source = "download",
                Package = "demo-node",
                RepositoryUrl = "https://github.com/comfyanonymous/demo-node.git",
            });

            var settings = new Settings { LocalNodeDirectory = localDir };
            var envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
            var git = new GitRunner("git");
            var diffService = new NodeInstallDiffService(
                (_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));
            var nodeOps = new NodeOperations(git, envRepo, _repo, settings, diffService, logger: null);
            var svc = new LocalNodeService(settings, _repo, envRepo, nodeOps, logger: null);

            var list = await svc.ListAsync(CancellationToken.None);
            var demo = list.FirstOrDefault(x => x.NodeId == "demo-node");
            Assert.NotNull(demo);
            Assert.Equal("https://github.com/comfyanonymous/demo-node.git", demo!.RepositoryUrl);
        }
        finally
        {
            try { Directory.Delete(localDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NodeOperations_TryReadRemoteUrlAsync_NonGitDir_ReturnsNull()
    {
        var envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        var settings = new Settings();
        var diffService = new NodeInstallDiffService(
            (_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));
        var nodeOps = new NodeOperations(new GitRunner("git"), envRepo, _repo, settings, diffService, logger: null);

        var nonGitDir = Path.Combine(Path.GetTempPath(), "non-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nonGitDir);
        try
        {
            var url = await nodeOps.TryReadRemoteUrlAsync(nonGitDir, CancellationToken.None);
            Assert.Null(url);
        }
        finally
        {
            try { Directory.Delete(nonGitDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NodeOperations_TryReadRemoteUrlAsync_GitRepoWithOrigin_ReturnsUrl()
    {
        // 用真 git 初始化一个 repo + 加 origin + 验证 TryReadRemoteUrlAsync 读出来
        var workdir = Path.Combine(Path.GetTempPath(), "git-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        try
        {
            RunGit(workdir, "init", "--initial-branch=main");
            RunGit(workdir, "remote", "add", "origin", "https://github.com/owner/repo.git");

            var envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
            var settings = new Settings();
            var diffService = new NodeInstallDiffService(
                (_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));
            var nodeOps = new NodeOperations(new GitRunner("git"), envRepo, _repo, settings, diffService, logger: null);

            var url = await nodeOps.TryReadRemoteUrlAsync(workdir, CancellationToken.None);
            Assert.Equal("https://github.com/owner/repo.git", url);
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); } catch { }
        }
    }

    private static void RunGit(string workdir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(30_000);
    }
}
