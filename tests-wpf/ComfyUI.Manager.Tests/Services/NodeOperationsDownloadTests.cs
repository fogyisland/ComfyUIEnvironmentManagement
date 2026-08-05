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
/// NodeOperations.DownloadAsync 测试:纯 git clone 到本地节点目录,
/// 不查 env、不写 ScannedNode。
///
/// 用真实 git + 本地 bare repo(同 <see cref="NodeOperationsTests"/> 的既有 pattern),
/// 不走网络 —— 仓库里没有 FakeGitRunner,且 GitRunner 是 sealed / RunAsync 非 virtual,
/// 无法在不改生产代码的前提下注入 fake。
/// </summary>
public sealed class NodeOperationsDownloadTests
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
        p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        Assert.True(
            p.ExitCode == 0,
            $"git {string.Join(' ', args)} (cwd={cwd}) 退出码 {p.ExitCode} stderr={stderr}");
    }

    /// <summary>建一个 bare remote,里面有一个 commit(README.md)。</summary>
    private static string InitRemote(string root)
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var working = Path.Combine(root, "seed");
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
        return remote;
    }

    /// <summary>
    /// DownloadAsync 不该碰 env / node 表,但 ctor 仍需要 repo 实例。
    /// 这里给一个空库 —— 空到连 env row 都没有,正好证明 DownloadAsync 不查 env。
    /// </summary>
    private static (NodeOperations Ops, NodeRepository NodeRepo) NewOps(
        TestDb db, Settings? settings = null)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var ops = new NodeOperations(
            new GitRunner("git"), envRepo, nodeRepo, settings ?? new Settings());
        return (ops, nodeRepo);
    }

    private static string NewTempRoot(string tag) =>
        Path.Combine(Path.GetTempPath(), $"comfy-download-{tag}-{Guid.NewGuid():N}");

    [Fact]
    public async Task DownloadAsync_ClonesRepoIntoLocalDir()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = NewTempRoot("clone");
        var remote = InitRemote(tempRoot);
        var localDir = Path.Combine(tempRoot, "local-nodes");

        using var db = new TestDb();
        var (ops, _) = NewOps(db);

        var result = await ops.DownloadAsync(localDir, "node-x", remote);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(string.IsNullOrWhiteSpace(result.Version));

        // clone 落在 <localDir>/<nodeId> —— 等价于验 cwd=localDir + args 末尾是 nodeId
        var targetDir = Path.Combine(localDir, "node-x");
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".git")));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotWriteScannedNode()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = NewTempRoot("noscan");
        var remote = InitRemote(tempRoot);
        var localDir = Path.Combine(tempRoot, "local-nodes");

        using var db = new TestDb();
        var (ops, nodeRepo) = NewOps(db);

        var result = await ops.DownloadAsync(localDir, "node-x", remote);
        Assert.True(result.Success, $"reason={result.Reason}");

        // G5:纯文件下载,不注册到任何 env
        Assert.Null(nodeRepo.Get("node-x"));
        Assert.Empty(nodeRepo.ListByEnv("env-1"));
    }

    [Fact]
    public async Task DownloadAsync_TargetTag_ChecksOutAfterClone()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = NewTempRoot("tag");
        Directory.CreateDirectory(tempRoot);
        var remote = Path.Combine(tempRoot, "remote.git");
        var working = Path.Combine(tempRoot, "seed");
        RunGit(tempRoot, "init", "--bare", "--initial-branch=main", remote);
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

        // tagged commit
        File.WriteAllText(Path.Combine(working, "second.md"), "second\n");
        RunGit(working, "add", "second.md");
        RunGit(working, "commit", "-q", "-m", "second");
        RunGit(working, "tag", "v1.0.0");
        RunGit(working, "push", "-q", "origin", "main");
        RunGit(working, "push", "-q", "origin", "v1.0.0");

        // tag 之后再走一个 commit,让 main HEAD 领先 tag
        File.WriteAllText(Path.Combine(working, "third.md"), "third\n");
        RunGit(working, "add", "third.md");
        RunGit(working, "commit", "-q", "-m", "third");
        RunGit(working, "push", "-q", "origin", "main");

        var localDir = Path.Combine(tempRoot, "local-nodes");

        using var db = new TestDb();
        var (ops, _) = NewOps(db);

        var result = await ops.DownloadAsync(localDir, "node-x", remote, targetTag: "v1.0.0");
        Assert.True(result.Success, $"reason={result.Reason}");

        var targetDir = Path.Combine(localDir, "node-x");
        // clone 先发生(README 在),checkout 后发生(停在 tag,不含 third)
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));
        Assert.True(File.Exists(Path.Combine(targetDir, "second.md")));
        Assert.False(File.Exists(Path.Combine(targetDir, "third.md")));
    }

    [Fact]
    public async Task DownloadAsync_DirAlreadyExists_ReturnsFail()
    {
        var tempRoot = NewTempRoot("exists");
        var localDir = Path.Combine(tempRoot, "local-nodes");
        Directory.CreateDirectory(Path.Combine(localDir, "node-x"));  // 预创建

        using var db = new TestDb();
        var (ops, _) = NewOps(db);

        var result = await ops.DownloadAsync(localDir, "node-x", "https://example.com/node-x");

        Assert.False(result.Success);
        Assert.Contains("目录已存在", result.Reason);
    }

    [Fact]
    public async Task DownloadAsync_LocalDirEmpty_ReturnsFail()
    {
        using var db = new TestDb();
        var (ops, _) = NewOps(db);

        // G14:空目录是 Fail 不是 throw(让 VM 弹 InfoMessage)
        var result = await ops.DownloadAsync("", "node-x", "https://example.com/node-x");

        Assert.False(result.Success);
        Assert.Contains("本地节点目录为空", result.Reason);
    }

    [Fact]
    public async Task DownloadAsync_GitFails_CleansUpEmptyDirAndReturnsFail()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = NewTempRoot("gitfail");
        var localDir = Path.Combine(tempRoot, "local-nodes");
        // 指向一个不存在的本地仓库 → git clone 退出码非零
        var missingRemote = Path.Combine(tempRoot, "no-such-repo.git");

        using var db = new TestDb();
        var (ops, nodeRepo) = NewOps(db);

        var result = await ops.DownloadAsync(localDir, "node-x", missingRemote);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        // 失败后不该留半截目录
        Assert.False(Directory.Exists(Path.Combine(localDir, "node-x")));
        Assert.Null(nodeRepo.Get("node-x"));
    }

    [Fact]
    public async Task DownloadAsync_UserCancels_ReturnsCancelReason()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = NewTempRoot("cancel");
        var remote = InitRemote(tempRoot);
        var localDir = Path.Combine(tempRoot, "local-nodes");

        using var db = new TestDb();
        var (ops, _) = NewOps(db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();  // 已取消的 token → GitRunner 抛 OperationCanceledException

        var result = await ops.DownloadAsync(localDir, "node-x", remote, null, cts.Token);

        Assert.False(result.Success);
        Assert.Equal("用户取消", result.Reason);
    }
}
