using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// NodeOperations 集成测试:用真实 git / 真实 SQLite(同 BulkUpdateOrchestratorTests)。
/// </summary>
public sealed class NodeOperationsTests
{
    private static string FindGit()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "";
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0 ? "git" : "";
        }
        catch { return ""; }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static (string Remote, string Working) InitRepoPair(string root)
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var working = Path.Combine(root, "working");
        RunGit(root, "init", "--bare", "--initial-branch=main", remote);
        Directory.CreateDirectory(working);
        RunGit(working, "init", "-q", "--initial-branch=main");
        RunGit(working, "config", "user.email", "test@example.com");
        RunGit(working, "config", "user.name", "test");
        RunGit(working, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(working, "README.md"), "hello\n");
        RunGit(working, "add", "README.md");
        RunGit(working, "commit", "-q", "-m", "initial");
        RunGit(working, "remote", "add", "origin", remote);
        RunGit(working, "push", "-q", "-u", "origin", "main");
        return (remote, working);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        Assert.True(
            p.ExitCode == 0,
            $"git {string.Join(' ', args)} (cwd={cwd}) 退出码 {p.ExitCode} stderr={stderr}");
    }

    private static (EnvironmentRepository envRepo, NodeRepository nodeRepo, SqliteConnectionFactory factory)
        SeedEnv(TestDb db, string customNodesPath)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        envRepo.Upsert(new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = customNodesPath,
            ComfyuiLayout = "isolated",
            CustomNodesPath = customNodesPath,
            Port = FreePort(),
            Status = "stopped",
        });
        return (envRepo, nodeRepo, db.Factory);
    }

    [Fact]
    public async Task InstallAsync_ClonesAndRegistersNode()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-{Guid.NewGuid():N}");
        var (remote, _) = InitRepoPair(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings());

        var result = await ops.InstallAsync("env-1", "node-a", remote);
        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(string.IsNullOrWhiteSpace(result.Version));

        var targetDir = Path.Combine(customNodes, "node-a");
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));

        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.Equal("node-a", row!.Package);
        Assert.Equal("enabled", row.Status);
        Assert.Equal(targetDir, row.PackagePath);
    }

    [Fact]
    public async Task UpgradeAsync_PullsFastForward_UpdatesVersion()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-upgrade-{Guid.NewGuid():N}");
        var (remote, working) = InitRepoPair(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        var nodeDir = Path.Combine(customNodes, "node-a");
        Directory.CreateDirectory(customNodes);

        // 把 working 移到 customNodes/node-a + 改 origin
        Directory.Move(working, nodeDir);
        RunGit(nodeDir, "remote", "set-url", "origin", remote);

        // 第二次 commit 推到 origin,然后本地 fetch 后 ff-only pull 应当成功
        File.WriteAllText(Path.Combine(nodeDir, "second.md"), "second\n");
        RunGit(nodeDir, "add", "second.md");
        RunGit(nodeDir, "commit", "-q", "-m", "second");
        RunGit(nodeDir, "push", "-q", "origin", "main");
        // reset working back 一次 commit,模拟本地落后
        RunGit(nodeDir, "reset", "--hard", "HEAD~1");

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        nodeRepo.Upsert(new ScannedNode
        {
            Id = "node-a",
            EnvId = "env-1",
            Package = "node-a",
            PackagePath = nodeDir,
            Status = "enabled",
        });

        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings());
        var result = await ops.UpgradeAsync("env-1", "node-a");
        Assert.True(result.Success, $"reason={result.Reason}");

        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.False(string.IsNullOrWhiteSpace(row!.Version));
        Assert.True(File.Exists(Path.Combine(nodeDir, "second.md")));
    }

    [Fact]
    public async Task InstallAsync_TargetDirExists_Fails()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-exist-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(Path.Combine(customNodes, "node-a")); // 已存在

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings());

        var result = await ops.InstallAsync("env-1", "node-a", "https://example/repo");
        Assert.False(result.Success);
        Assert.Contains("目录已存在", result.Reason);
    }

    [Fact]
    public async Task RollbackAsync_ResetsToGivenSha()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-rollback-{Guid.NewGuid():N}");
        var (remote, working) = InitRepoPair(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        var nodeDir = Path.Combine(customNodes, "node-a");
        Directory.CreateDirectory(customNodes);
        Directory.Move(working, nodeDir);
        RunGit(nodeDir, "remote", "set-url", "origin", remote);

        // 第二次 commit
        File.WriteAllText(Path.Combine(nodeDir, "second.md"), "second\n");
        RunGit(nodeDir, "add", "second.md");
        RunGit(nodeDir, "commit", "-q", "-m", "second");

        // 拿第一次 commit 的 sha
        var firstSha = ReadHeadSha(nodeDir) + "~1";

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        nodeRepo.Upsert(new ScannedNode
        {
            Id = "node-a",
            EnvId = "env-1",
            Package = "node-a",
            PackagePath = nodeDir,
            Status = "enabled",
        });

        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings());
        // 直接用 HEAD~1(不解析短 sha,git 接受)
        var result = await ops.RollbackAsync("env-1", "node-a", "HEAD~1");
        Assert.True(result.Success, $"reason={result.Reason}");

        // 第二次 commit 的文件应当被 reset 掉
        Assert.False(File.Exists(Path.Combine(nodeDir, "second.md")));

        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.False(string.IsNullOrWhiteSpace(row!.Version));
    }

    [Fact]
    public void LockUnlock_PersistsFlag()
    {
        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, Path.GetTempPath());
        nodeRepo.Upsert(new ScannedNode
        {
            Id = "node-x",
            EnvId = "env-1",
            Package = "node-x",
            PackagePath = Path.GetTempPath(),
            Status = "enabled",
        });

        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings());
        ops.Lock("node-x");
        Assert.True(nodeRepo.Get("node-x")!.Locked);

        ops.Unlock("node-x");
        Assert.False(nodeRepo.Get("node-x")!.Locked);
    }

    [Fact]
    public async Task InstallAsync_EmptyRepoUrl_FallsBackToActiveDownloadSourceUrl()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-fallback-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        // 建一个 bare repo 在 <tempRoot>/repos/{node}.git,这样 {node}
        // 替换后正好是一个真实的 bare repo 路径。
        var reposRoot = Path.Combine(tempRoot, "repos");
        Directory.CreateDirectory(reposRoot);
        var remoteBare = Path.Combine(reposRoot, "node-a.git");
        RunGit(tempRoot, "init", "--bare", "--initial-branch=main", remoteBare);
        // 推一个 commit 进去(否则 clone 出空 repo 也不会报错,
        // 但我们仍然想要 git 真的拿到 refs)
        var seedWorking = Path.Combine(tempRoot, "seed");
        Directory.CreateDirectory(seedWorking);
        RunGit(seedWorking, "init", "-q", "--initial-branch=main");
        RunGit(seedWorking, "config", "user.email", "test@example.com");
        RunGit(seedWorking, "config", "user.name", "test");
        RunGit(seedWorking, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(seedWorking, "README.md"), "hello\n");
        RunGit(seedWorking, "add", "README.md");
        RunGit(seedWorking, "commit", "-q", "-m", "initial");
        RunGit(seedWorking, "remote", "add", "origin", remoteBare);
        RunGit(seedWorking, "push", "-q", "-u", "origin", "main");

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);

        // Settings 含一个 active download source 模板
        var settings = new ComfyUI.Manager.Models.Settings
        {
            DownloadSources = new List<NodeSource>
            {
                new() { Name = "test-source", Url = reposRoot + "/{node}.git" },  // 注意带 {node}
            },
            ActiveDownloadSourceName = "test-source",
        };
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings);

        // 传空 repoUrl → 应回落到 active download source,substitute {node}
        var result = await ops.InstallAsync("env-1", "node-a", "");
        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.True(Directory.Exists(Path.Combine(customNodes, "node-a")));
    }

    [Fact]
    public async Task InstallAsync_EmptyRepoUrl_NoActiveSource_Fails()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-nosrc-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);

        var settings = new ComfyUI.Manager.Models.Settings
        {
            DownloadSources = new(),  // 列表空
            ActiveDownloadSourceName = "nonexistent",
        };
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings);

        var result = await ops.InstallAsync("env-1", "node-a", "");
        Assert.False(result.Success);
        Assert.Contains("未配置下载源", result.Reason);
    }

    private static string ReadHeadSha(string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");
        using var p = Process.Start(psi)!;
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(3000);
        return sha;
    }
}