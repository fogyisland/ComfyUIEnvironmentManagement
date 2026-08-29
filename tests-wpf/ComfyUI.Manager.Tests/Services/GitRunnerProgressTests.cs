using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15.5: GitRunner 加 IProgress<string>? onStderrLine 参数,实时 emit Receiving objects 等行。
/// 不动 ctor + 现有行为:onStderrLine=null 走 ReadToEndAsync()(向后兼容)。
/// </summary>
public class GitRunnerProgressTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly GitRunner _runner;

    public GitRunnerProgressTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "GitRunnerProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        // 用 system git 跑真 git(TestFixture 保证 git 在 PATH 上)
        _runner = new GitRunner("git");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task RunAsync_WithProgress_EmitsReceivingObjectsLines()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));

        // 用真 git init 一个空 repo 然后 clone 一个公开小 repo
        var srcDir = Path.Combine(_tmpDir, "src");
        Directory.CreateDirectory(srcDir);
        await _runner.RunAsync(srcDir, new[] { "init", "-q" }, ct: default);

        // v1.0.0.x #724:timeout 拉到 180s,full-suite 跑时网络/CPU 繁忙下
        // 实测偶发 60+s 跑不完。
        var result = await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(180),
            onStderrLine: progress);

        Assert.True(result.Ok, "clone should succeed");
        await Task.Delay(200); // give Progress<string> async dispatch time
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Receiving objects:") || l.Contains("Cloning into"));
    }

    [Fact]
    public async Task RunAsync_WithProgress_FiltersOutNonProgressLines()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));

        var srcDir = Path.Combine(_tmpDir, "src");
        Directory.CreateDirectory(srcDir);
        await _runner.RunAsync(srcDir, new[] { "init", "-q" }, ct: default);

        await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(180),
            onStderrLine: progress);

        await Task.Delay(200);
        // filter: 必为 Receiving objects / Resolving deltas / remote: / Cloning into 之一
        // (spec §Design 1 加了 Cloning into: prefix,brief 没包含但 spec 覆盖 brief)
        foreach (var line in lines)
        {
            Assert.True(
                line.StartsWith("Receiving objects:") ||
                line.StartsWith("Resolving deltas:") ||
                line.StartsWith("remote:") ||
                line.StartsWith("Cloning into"),
                $"Unexpected line: {line}");
        }
    }

    [Fact]
    public async Task RunAsync_NoProgress_BehavesAsBefore()
    {
        var srcDir = Path.Combine(_tmpDir, "src");
        Directory.CreateDirectory(srcDir);
        await _runner.RunAsync(srcDir, new[] { "init", "-q" }, ct: default);

        // onStderrLine=null → 走 ReadToEndAsync() 现路径,stderr 全捕获在 result.Stderr
        var result = await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(180),
            onStderrLine: null);

        Assert.True(result.Ok);
        Assert.NotEmpty(result.Stderr); // stderr 仍捕获到 result.Stderr
    }

    [Fact]
    public async Task RunAsync_WithProgress_StderrStillReturnedInResult()
    {
        var srcDir = Path.Combine(_tmpDir, "src");
        Directory.CreateDirectory(srcDir);
        await _runner.RunAsync(srcDir, new[] { "init", "-q" }, ct: default);

        // v1.0.0.x #724 fix:full-suite 跑时网络/CPU 繁忙 + octocat/Hello-World
        // 是 github 公开小 repo,clone 实测 isolated 3s,full-suite 偶发 60+s 才
        // 完成或 timeout。把 timeout 拉到 180s 给足 buffer。
        var result = await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(180),
            onStderrLine: new Progress<string>(_ => { }));

        Assert.True(result.Ok);
        Assert.NotEmpty(result.Stderr); // 即使分流,GitResult.Stderr 仍 capture
    }

    [Fact]
    public async Task RunAsync_WithProgress_OnCanceled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var srcDir = Path.Combine(_tmpDir, "src");
        Directory.CreateDirectory(srcDir);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _runner.RunAsync(
                srcDir, new[] { "fetch", "origin" },
                timeout: TimeSpan.FromSeconds(30),
                ct: cts.Token,
                onStderrLine: new Progress<string>(_ => { })));
    }
}