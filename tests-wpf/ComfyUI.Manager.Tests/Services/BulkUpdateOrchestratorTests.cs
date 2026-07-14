using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
/// BulkUpdateOrchestrator 集成测试:启动真实 git 进程,跑真实 SQLite,
/// 不 mock git 本身(测试代码里只 mock git exe 路径,文件 IO 与 SQLite 走真)。
///
/// git 仓库在 %TEMP% 下临时初始化,完成后清理。
/// </summary>
public sealed class BulkUpdateOrchestratorTests
{
    private static string FindGit()
    {
        // 与 ProcessLauncherTests 类似,从 PATH 拿 git。
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
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 在 temp 下建一个 bare 远程 + 一个 working 仓库,都做 1 次提交
    /// (同分支名)。这样 working 仓库 git pull --ff-only 能干净跑完。
    /// </summary>
    private static (string Remote, string Working) InitRepoPair(string root)
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var working = Path.Combine(root, "working");

        // bare 远程 —— 必须把 cwd 设到 root(已存在的目录),
        // 然后用绝对路径作为 init 的目标参数,否则 Windows 在
        // 不存在的目录上启动 git 会爆 "目录名称无效"。
        RunGit(root, "init", "--bare", "--initial-branch=main", remote);
        // working 仓库
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
            $"git {string.Join(' ', args)} (cwd={cwd}) 退出码 {p.ExitCode} stderr={stderr} stdout={stdout}");
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

    private static void SeedEnv(
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        string envId,
        string customNodesPath,
        params (string NodeId, string PackagePath)[] nodes)
    {
        envRepo.Upsert(new Environment
        {
            Id = envId,
            Name = envId,
            RootPath = customNodesPath,
            ComfyuiLayout = "isolated",
            CustomNodesPath = customNodesPath,
            Port = FreePort(),
            Status = "stopped",
        });

        foreach (var (nodeId, packagePath) in nodes)
        {
            nodeRepo.Upsert(new ScannedNode
            {
                Id = nodeId,
                EnvId = envId,
                Package = nodeId,
                PackagePath = packagePath,
                Status = "enabled",
            });
        }
    }

    [Fact]
    public async Task StartAsync_PrePopulatesAllRows_ThenMarksSucceeded()
    {
        if (string.IsNullOrEmpty(FindGit())) return; // git 缺失 → 跳过

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        var logsRoot = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(tempRoot);

        // 实际目录布局:customNodesPath = <tempRoot>/nodes,真实 git 仓库在
        // <tempRoot>/nodes/node-a (orchestrator 用 customNodesPath + nodeId
        // 推导目录)。bare 远程放到 <tempRoot>/remote.git 与 nodes/ 平级。
        var (_, _) = InitRepoPair(tempRoot);
        // 把 working 仓库搬到 nodes/node-a 下做主目录
        var nodesRoot = Path.Combine(tempRoot, "nodes");
        var working = Path.Combine(nodesRoot, "node-a");
        Directory.CreateDirectory(nodesRoot);
        var srcWorking = Path.Combine(tempRoot, "working");
        if (Directory.Exists(srcWorking))
        {
            Directory.Move(srcWorking, working);
            // remote 引用是绝对/相对路径,改 origin 到新位置
            RunGit(working, "remote", "set-url", "origin", Path.Combine(tempRoot, "remote.git"));
        }

        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        SeedEnv(envRepo, nodeRepo, "env-1", nodesRoot, ("node-a", working));

        var orch = new BulkUpdateOrchestrator(
            tempRoot, "git", envRepo, nodeRepo);

        var progress = new List<BulkUpdateRow>();
        var completed = (BulkUpdateSummary?)null;
        orch.Progress += r => progress.Add(r);
        orch.Completed += s => completed = s;

        var summary = await orch.StartAsync(
            new[] { "env-1" }, new[] { "node-a" }, CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Failed);

        // 至少一次 running 一次 succeeded
        Assert.Contains(progress, r => r.Status == "running");
        Assert.Contains(progress, r => r.Status == "succeeded" && r.EnvId == "env-1" && r.NodeId == "node-a");
        // log 文件存在
        Assert.True(Directory.EnumerateFiles(logsRoot, "bulk-update-*.log").Any());
    }

    [Fact]
    public async Task StartAsync_MissingNodeDir_EmitsSkipped()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        // customNodesPath 指向 tempRoot,节点目录不存在
        SeedEnv(envRepo, nodeRepo, "env-1", tempRoot);

        var orch = new BulkUpdateOrchestrator(tempRoot, "git", envRepo, nodeRepo);

        var progress = new List<BulkUpdateRow>();
        orch.Progress += r => progress.Add(r);

        var summary = await orch.StartAsync(
            new[] { "env-1" }, new[] { "node-missing" }, CancellationToken.None);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains(progress, r =>
            r.Status == "skipped" && r.Reason == "目录不存在");
    }

    [Fact]
    public async Task StartAsync_EnvMissingCustomNodesPath_EmitsSkipped()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        envRepo.Upsert(new Environment
        {
            Id = "env-no-path",
            Name = "env-no-path",
            RootPath = tempRoot,
            ComfyuiLayout = "isolated",
            CustomNodesPath = null, // <-- 关键:null
            Port = FreePort(),
            Status = "stopped",
        });

        var orch = new BulkUpdateOrchestrator(tempRoot, "git", envRepo, nodeRepo);

        var progress = new List<BulkUpdateRow>();
        orch.Progress += r => progress.Add(r);

        var summary = await orch.StartAsync(
            new[] { "env-no-path" }, new[] { "node-a" }, CancellationToken.None);

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains(progress, r =>
            r.Status == "skipped" && r.Reason == "env 缺 custom_nodes_path");
    }

    [Fact]
    public async Task StartAsync_Cancel_EmitsCancelled()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // 取消语义测试:传入已 cancel 的 token,确保 Completed + Cancelled 都触发,
        // 且 Total=0 —— 因为 ct 在任何 (env,node) 进入之前就 cancel 了。
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        // cancel 前置 → 不会跑到 git,目录存在与否无所谓;给个有效路径。
        SeedEnv(envRepo, nodeRepo, "env-1", tempRoot, ("node-a", tempRoot));

        var orch = new BulkUpdateOrchestrator(tempRoot, "git", envRepo, nodeRepo);

        var cancelledFired = false;
        var completedFired = false;
        orch.Cancelled += () => cancelledFired = true;
        orch.Completed += _ => completedFired = true;

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 取消在 start 之前

        var summary = await orch.StartAsync(
            new[] { "env-1" }, new[] { "node-a" }, cts.Token);

        Assert.True(completedFired, "Completed 必须触发");
        Assert.Equal(0, summary.Total); // 全部都没跑
        Assert.True(cancelledFired, "Cancelled 必须触发");
    }

    [Fact]
    public async Task StartAsync_MidRunCancel_StopsBeforeAllRowsComplete()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // 慢 fake-git:每个 git pull 阻塞 ~5s,这样 orchestrator 在跑第二个 (env,node)
        // 之前给我们 cancel 的窗口。
        var fakeScript = Path.Combine(tempRoot, "slow-git.cmd");
        File.WriteAllText(fakeScript,
            "@echo off\r\nping 127.0.0.1 -n 6 > nul\r\nexit /b 0\r\n");

        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);

        // 4 个 (env, node):2 env × 2 node。总耗时 ~20s(没 cancel)。我们 cancel
        // 在第一个 running emit 之后,期望 Total < 4。
        var nodesRoot = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(nodesRoot);
        Directory.CreateDirectory(Path.Combine(nodesRoot, "node-a"));
        Directory.CreateDirectory(Path.Combine(nodesRoot, "node-b"));
        SeedEnv(envRepo, nodeRepo, "env-1", nodesRoot,
            ("node-a", Path.Combine(nodesRoot, "node-a")),
            ("node-b", Path.Combine(nodesRoot, "node-b")));
        SeedEnv(envRepo, nodeRepo, "env-2", nodesRoot,
            ("node-a", Path.Combine(nodesRoot, "node-a")),
            ("node-b", Path.Combine(nodesRoot, "node-b")));

        var orch = new BulkUpdateOrchestrator(
            tempRoot, fakeScript, envRepo, nodeRepo);

        var seenRunning = 0;
        orch.Progress += r =>
        {
            if (r.Status == "running")
            {
                Interlocked.Increment(ref seenRunning);
            }
        };

        var cancelledFired = new TaskCompletionSource();
        orch.Cancelled += () => cancelledFired.TrySetResult();

        using var cts = new CancellationTokenSource();

        // 在背景启动 orchestrator,等见到第一个 running 就 cancel。
        var runTask = orch.StartAsync(
            new[] { "env-1", "env-2" }, new[] { "node-a", "node-b" }, cts.Token);

        // 轮询等到 at least 1 个 running emit(慢 git 让我们有时间)。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Volatile.Read(ref seenRunning) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        cts.Cancel();

        // Orchestrator 应当半路停下 —— summary.Total < 4(完整 2x2)。
        var summary = await runTask;
        Assert.True(summary.Total < 4,
            $"mid-run cancel 应该中止,Total={summary.Total},期望 < 4");
        Assert.True(cancelledFired.Task.Wait(TimeSpan.FromSeconds(5)),
            "Cancelled 事件应触发");
    }

    [Fact]
    public async Task StartAsync_Timeout_FailsGracefully()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // 用一个不会超时的假 git 脚本 ——
        // Windows 上写一个 .cmd 包装 ping -n 60(约 60s),保证 > 30s 的
        // orchestrator 内部 timeout。
        var fakeScript = Path.Combine(tempRoot, "fake-git.cmd");
        File.WriteAllText(fakeScript,
            "@echo off\r\nping 127.0.0.1 -n 60 > nul\r\nexit /b 0\r\n");
        var fakeGitExe = fakeScript;

        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);

        // 即使 fake-git 不真做 git pull,orchestrator 也会先检查节点目录
        // 是否存在。我们建一个目录让它跑。
        var nodeDir = Path.Combine(tempRoot, "node-a");
        Directory.CreateDirectory(nodeDir);
        SeedEnv(envRepo, nodeRepo, "env-1", tempRoot, ("node-a", nodeDir));

        var orch = new BulkUpdateOrchestrator(
            tempRoot, fakeGitExe, envRepo, nodeRepo);

        var progress = new List<BulkUpdateRow>();
        orch.Progress += r => progress.Add(r);

        // orchestrator 内部 30s 超时,所以这个测试本身耗时 ≤ 31s。
        var summary = await orch.StartAsync(
            new[] { "env-1" }, new[] { "node-a" }, CancellationToken.None);

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        var failed = progress.FirstOrDefault(r => r.Status == "failed");
        Assert.NotNull(failed);
        Assert.Equal("timeout", failed!.Reason);
    }
}
