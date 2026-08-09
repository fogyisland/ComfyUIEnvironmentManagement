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
/// v0.6.7.5 T3: NodeOperations.InstallAsync 在 clone 前跑 diff check + 警告 modal。
///
/// 设计说明:由于 GitRunner 是 sealed + RunAsync 非 virtual,我们无法 fake 它,
/// 仍然用真实 git + 真实 bare repo(同 <see cref="NodeOperationsTests"/> 模式)。
/// FakeDiffService / FakeShowDialog 替换 diff 检查和 modal 入口。
/// </summary>
public sealed class NodeOperationsInstallDiffTests
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
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        Assert.True(
            p.ExitCode == 0,
            $"git {string.Join(' ', args)} (cwd={cwd}) 退出码 {p.ExitCode} stderr={stderr}");
    }

    private static string InitBareRemote(string root)
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var seed = Path.Combine(root, "seed");
        RunGit(root, "init", "--bare", "--initial-branch=main", remote);
        Directory.CreateDirectory(seed);
        RunGit(seed, "init", "-q", "--initial-branch=main");
        RunGit(seed, "config", "user.email", "test@example.com");
        RunGit(seed, "config", "user.name", "test");
        RunGit(seed, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        RunGit(seed, "add", "README.md");
        RunGit(seed, "commit", "-q", "-m", "initial");
        RunGit(seed, "remote", "add", "origin", remote);
        RunGit(seed, "push", "-q", "-u", "origin", "main");
        return remote;
    }

    private static void SeedEnv(TestDb db, string customNodesPath)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        // 写一个真实存在的 PythonExecutable(File.Exists 必须通过才会跑 diff check)。
        // FakeDiffService 不真跑 pip list,所以不关心 exe 是不是 python —— 给个存在的占位。
        var dummyPython = Path.Combine(Path.GetTempPath(), "dummy-python.exe");
        if (!File.Exists(dummyPython))
        {
            File.WriteAllText(dummyPython, "");  // 0 字节占位
        }
        envRepo.Upsert(new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = customNodesPath,
            ComfyuiLayout = "isolated",
            CustomNodesPath = customNodesPath,
            Port = FreePort(),
            Status = "stopped",
            PythonExecutable = dummyPython,
        });
    }

    [Fact]
    public async Task InstallAsync_WithDiffWarnings_UserCancels_DoesNotClone_ReturnsFail()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-diff-cancel-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        SeedEnv(db, customNodes);
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);

        var diffService = new FakeDiffService(MakeReportWithDowngrade());
        var showDialog = new FakeShowDialog(returnValue: false);
        var ops = new NodeOperations(
            new GitRunner("git"), envRepo, nodeRepo, new Settings(),
            diffService, showDialog.Invoke);

        var result = await ops.InstallAsync(
            "env-1", "node-a", "https://example/repo",
            targetTag: null,
            catalogPipReqs: new[] { new PipRequirement("torch", "<=1.5") });

        Assert.False(result.Success);
        Assert.Equal("用户取消(diff warning)", result.Reason);
        Assert.Equal(1, diffService.CheckCallCount);
        Assert.Equal(1, showDialog.CallCount);

        // clone 没发生 — 没有 node-a 目录
        Assert.False(Directory.Exists(Path.Combine(customNodes, "node-a")));
        Assert.Null(nodeRepo.Get("node-a"));
    }

    [Fact]
    public async Task InstallAsync_WithDiffWarnings_UserProceeds_ClonesNormally()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-diff-proceed-{Guid.NewGuid():N}");
        var remote = InitBareRemote(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        SeedEnv(db, customNodes);
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);

        var diffService = new FakeDiffService(MakeReportWithDowngrade());
        var showDialog = new FakeShowDialog(returnValue: true);  // 用户接受
        var ops = new NodeOperations(
            new GitRunner("git"), envRepo, nodeRepo, new Settings(),
            diffService, showDialog.Invoke);

        var result = await ops.InstallAsync(
            "env-1", "node-a", remote,
            targetTag: null,
            catalogPipReqs: new[] { new PipRequirement("torch", "<=1.5") });

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.Equal(1, diffService.CheckCallCount);
        Assert.Equal(1, showDialog.CallCount);

        // clone 应当真发生
        var targetDir = Path.Combine(customNodes, "node-a");
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));

        var row = nodeRepo.Get("node-a");
        Assert.NotNull(row);
        Assert.Equal("enabled", row!.Status);
    }

    [Fact]
    public async Task InstallAsync_NoCatalogPipReqs_SkipsDiffCheck_BehavesLikeOriginal()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-diff-skip-{Guid.NewGuid():N}");
        var remote = InitBareRemote(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        SeedEnv(db, customNodes);
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);

        var diffService = new FakeDiffService(NodeInstallDiffReport.Empty);
        var showDialog = new FakeShowDialog(returnValue: true);
        var ops = new NodeOperations(
            new GitRunner("git"), envRepo, nodeRepo, new Settings(),
            diffService, showDialog.Invoke);

        // catalogPipReqs = null → diff 不调,showDialog 不弹
        var result = await ops.InstallAsync("env-1", "node-a", remote);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.Equal(0, diffService.CheckCallCount);
        Assert.Equal(0, showDialog.CallCount);

        // clone 还是应当真发生(没 catalogPipReqs 等于走原本路径)
        var targetDir = Path.Combine(customNodes, "node-a");
        Assert.True(Directory.Exists(targetDir));
    }

    private static NodeInstallDiffReport MakeReportWithDowngrade() => new(new[]
    {
        new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
    });

    /// <summary>
    /// FakeDiffService:不真跑 pip list,直接返固定 report。
    /// ctor 传一个 noop runProcess(只是为了让 base ctor 不抛)。
    /// </summary>
    private sealed class FakeDiffService : NodeInstallDiffService
    {
        private readonly NodeInstallDiffReport _report;
        public int CheckCallCount { get; private set; }

        public FakeDiffService(NodeInstallDiffReport report)
            : base((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")))
        {
            _report = report;
        }

        public override Task<NodeInstallDiffReport> CheckAsync(
            Environment env, IReadOnlyList<PipRequirement> reqs, CancellationToken ct)
        {
            CheckCallCount++;
            return Task.FromResult(_report);
        }
    }

    /// <summary>
    /// FakeShowDialog:记录调用次数,固定返一个 bool(模拟用户在 modal 上的选择)。
    /// </summary>
    private sealed class FakeShowDialog
    {
        private readonly bool _returnValue;
        public int CallCount { get; private set; }

        public FakeShowDialog(bool returnValue) => _returnValue = returnValue;

        public bool Invoke(NodeInstallDiffReport report, Environment env, string nodeId)
        {
            CallCount++;
            return _returnValue;
        }
    }
}