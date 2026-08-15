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
/// v0.6.15.5 T2: NodeOperations 3 个 git 方法接 IProgress<string>? progress 透传给 GitRunner,
/// 非空时通过 WrapProgress 同时把进度行写 AppLogger。
///
/// 适配 codebase (跟 brief 有偏离):
/// - AppLogger ctor = (projectRoot, baseDir?) — 不是 (logPath)
/// - EnvironmentRepository / NodeRepository 都要 SqliteConnectionFactory — 用 TestDb
/// - NodeInstallDiffService ctor = (runProcess, logger?) — 跟 v0.6.7.5 FakeDiffService 同款
/// - 没有 TestEnvFactory — inline Environment 构造(跟 BulkUpdateOrchestratorTests 同款)
/// </summary>
public class NodeOperationsProgressTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly TestDb _db;
    private readonly FakeGitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _ops;
    private readonly AppLogger _logger;

    public NodeOperationsProgressTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "NodeOpsProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _logger = new AppLogger(_tmpDir);

        _git = new FakeGitRunner();
        var settings = new Settings { LocalNodeDirectory = _tmpDir };
        _db = new TestDb();
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeRepo = new NodeRepository(_db.Factory);
        _ops = new NodeOperations(
            _git, _envRepo, _nodeRepo, settings,
            NoopDiffService(), null, _logger);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { }
        _logger.Dispose();
    }

    /// <summary>既有 InstallAsync/UpgradeAsync/DownloadAsync 测试不关心 diff — noop service。</summary>
    private static NodeInstallDiffService NoopDiffService() =>
        new((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void SeedEnv(string envId, string rootPath)
    {
        _envRepo.Upsert(new Environment
        {
            Id = envId,
            Name = envId,
            RootPath = rootPath,
            ComfyuiLayout = "isolated",
            CustomNodesPath = Path.Combine(rootPath, "custom_nodes"),
            Port = FreePort(),
            Status = "stopped",
        });
    }

    [Fact]
    public async Task DownloadAsync_WithProgress_ForwardsProgressToGitRunner()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));
        _git.NextStderrLines = new[] { "Receiving objects:  45%", "Receiving objects: 100%" };

        SeedEnv("env-x", _tmpDir);

        var result = await _ops.DownloadAsync(_tmpDir, "test-node", "https://example.com/repo.git", progress: progress);

        Assert.True(result.Success);
        await Task.Delay(200);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Receiving objects:  45%", lines[0]);
    }

    [Fact]
    public async Task InstallAsync_WithProgress_ForwardsProgressToGitRunner()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));
        _git.NextStderrLines = new[] { "Receiving objects:  25%", "Resolving deltas: 100%" };

        SeedEnv("env-x", _tmpDir);

        var result = await _ops.InstallAsync("env-x", "test-node", "https://example.com/repo.git", progress: progress);

        await Task.Delay(200);
        Assert.True(result.Success);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task UpgradeAsync_WithProgress_ForwardsProgressToGitRunner()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));
        _git.NextStderrLines = new[] { "remote: Counting objects: 100", "Receiving objects:  60%" };

        // Prep: 已有 env + node row + targetDir
        SeedEnv("env-x", _tmpDir);
        var nodeDir = Path.Combine(_tmpDir, "custom_nodes", "test-node");
        Directory.CreateDirectory(nodeDir);
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = "test-node",
            EnvId = "env-x",
            Package = "test-node",
            PackagePath = nodeDir,
            Status = "enabled",
        });

        var result = await _ops.UpgradeAsync("env-x", "test-node", progress: progress);

        await Task.Delay(200);
        Assert.True(result.Success);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task DownloadAsync_WithProgress_LogsProgressLinesToAppLogger()
    {
        _git.NextStderrLines = new[] { "Receiving objects:  50%" };
        var progress = new Progress<string>(_ => { });

        SeedEnv("env-x", _tmpDir);

        await _ops.DownloadAsync(_tmpDir, "test-node", "https://example.com/repo.git", progress: progress);

        await Task.Delay(500); // let logger flush
        var lines = _logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("Receiving objects:  50%"));
    }

    [Fact]
    public async Task DownloadAsync_NoProgress_BehavesAsBefore()
    {
        _git.NextStderrLines = new[] { "Receiving objects:  50%" };

        SeedEnv("env-x", _tmpDir);

        var result = await _ops.DownloadAsync(_tmpDir, "test-node", "https://example.com/repo.git");

        Assert.True(result.Success); // no progress → 原行为
    }
}

/// <summary>
/// Test fake: 替代真 GitRunner,记录每次调用的 args + onStderrLine,并 emit 预设 stderr lines。
///
/// v0.6.15.5 T1 把 GitRunner 改成 non-sealed + RunAsync virtual,所以这里可以 override。
/// </summary>
internal class FakeGitRunner : GitRunner
{
    public string[] NextStderrLines { get; set; } = Array.Empty<string>();
    public List<(string Workdir, string[] Args, IProgress<string>? OnStderrLine)> Calls { get; } = new();

    public FakeGitRunner() : base("git") { }

    public override Task<GitResult> RunAsync(
        string workdir, IEnumerable<string> args,
        TimeSpan? timeout = null, CancellationToken ct = default,
        IProgress<string>? onStderrLine = null)
    {
        var argsArr = args as string[] ?? new List<string>(args).ToArray();
        Calls.Add((workdir, argsArr, onStderrLine));
        if (onStderrLine is not null)
        {
            foreach (var line in NextStderrLines)
            {
                onStderrLine.Report(line);
            }
        }
        return Task.FromResult(new GitResult(0, "", string.Join("\n", NextStderrLines)));
    }
}