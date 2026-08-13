using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// T1 picker redesign:
/// 1) UninstallAsync:删目录 + 删 row(失败语义见 brief)
/// 2) InstallAsync 写 ScanMeta["installed_tag"]
///
/// GitRunner 是 sealed / 非虚 → 用真实 git + bare repo,不 mock。
/// 复用 NodeOperationsTests 的 helpers(find git / init pair / seed env)。
/// </summary>
public sealed class NodeOperationsUninstallTests
{
    // -------- helpers (复制自 NodeOperationsTests.cs verbatim) --------

    private static NodeInstallDiffService NoopDiffService() =>
        new((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));

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

    // -------- tests --------

    [Fact]
    public async Task UninstallAsync_HappyPath_RemovesDirAndRow()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-uninstall-happy-{Guid.NewGuid():N}");
        var (remote, working) = InitRepoPair(tempRoot);

        // 打 tag v1.0.0 + push tag
        File.WriteAllText(Path.Combine(working, "second.md"), "second\n");
        RunGit(working, "add", "second.md");
        RunGit(working, "commit", "-q", "-m", "second");
        RunGit(working, "tag", "v1.0.0");
        RunGit(working, "push", "-q", "origin", "main");
        RunGit(working, "push", "-q", "origin", "v1.0.0");

        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings(), NoopDiffService());

        // install with targetTag=v1.0.0
        var installResult = await ops.InstallAsync("env-1", "node-a", remote, targetTag: "v1.0.0");
        Assert.True(installResult.Success, $"reason={installResult.Reason}");

        var targetDir = Path.Combine(customNodes, "node-a");
        Assert.True(Directory.Exists(targetDir));
        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.True(row!.ScanMeta.ContainsKey("installed_tag"));
        Assert.Equal("v1.0.0", row.ScanMeta["installed_tag"]);

        // uninstall
        var uninstallResult = await ops.UninstallAsync("env-1", "node-a");
        Assert.True(uninstallResult.Success, $"reason={uninstallResult.Reason}");
        Assert.False(string.IsNullOrWhiteSpace(uninstallResult.Version));  // 返原 sha

        // row + dir 都应消失
        Assert.Null(nodeRepo.Get("node-a"));
        Assert.False(Directory.Exists(targetDir));
    }

    [Fact]
    public async Task UninstallAsync_RowMissing_ReturnsFail()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-uninstall-norow-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings(), NoopDiffService());

        var result = await ops.UninstallAsync("env-1", "ghost-node");
        Assert.False(result.Success);
        Assert.Contains("未注册", result.Reason);
    }

    [Fact]
    public async Task UninstallAsync_DirMissing_StillRemovesRow()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-uninstall-dirmissing-{Guid.NewGuid():N}");
        var (remote, working) = InitRepoPair(tempRoot);

        // 打 tag
        File.WriteAllText(Path.Combine(working, "second.md"), "second\n");
        RunGit(working, "add", "second.md");
        RunGit(working, "commit", "-q", "-m", "second");
        RunGit(working, "tag", "v1.0.0");
        RunGit(working, "push", "-q", "origin", "main");
        RunGit(working, "push", "-q", "origin", "v1.0.0");

        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings(), NoopDiffService());

        // install
        var installResult = await ops.InstallAsync("env-1", "node-a", remote, targetTag: "v1.0.0");
        Assert.True(installResult.Success, $"reason={installResult.Reason}");

        var targetDir = Path.Combine(customNodes, "node-a");
        Assert.True(Directory.Exists(targetDir));

        // 手动删目录(模拟"用户手动删了目录但 DB 还在"场景)。
        // git pack/idx 文件是 readonly,先清 attribute 再删,跟 NodeOperations.TryDelete 同款。
        if (Directory.Exists(targetDir))
        {
            foreach (var f in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(targetDir, recursive: true);
        }
        Assert.False(Directory.Exists(targetDir));

        // uninstall 仍应成功(目录不存在就跳过删目录,只删 row)
        var result = await ops.UninstallAsync("env-1", "node-a");
        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.Null(nodeRepo.Get("node-a"));
    }

    [Fact]
    public async Task UninstallAsync_NonExistentEnv_ReturnsFail()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        using var db = new TestDb();
        // 不 seed env,直接 uninstall
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings(), NoopDiffService());

        var result = await ops.UninstallAsync("nonexistent-env", "node-a");
        Assert.False(result.Success);
        Assert.Contains("env 不存在", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_CapturesInstalledTag_InScanMeta()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-installtag-{Guid.NewGuid():N}");
        var (remote, working) = InitRepoPair(tempRoot);

        // 加第二次 commit + 打 v1.0.0 tag + push
        File.WriteAllText(Path.Combine(working, "second.md"), "second\n");
        RunGit(working, "add", "second.md");
        RunGit(working, "commit", "-q", "-m", "second");
        RunGit(working, "tag", "v1.0.0");
        RunGit(working, "push", "-q", "origin", "main");
        RunGit(working, "push", "-q", "origin", "v1.0.0");

        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings(), NoopDiffService());

        var result = await ops.InstallAsync("env-1", "node-a", remote, targetTag: "v1.0.0");
        Assert.True(result.Success, $"reason={result.Reason}");

        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.True(row!.ScanMeta.ContainsKey("installed_tag"));
        Assert.Equal("v1.0.0", row.ScanMeta["installed_tag"]);
    }
}
