# v0.6.15.5 Git Operation Progress Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 install / download / upgrade 三个 git 操作显示实时进度(百分比 progress bar + 实时 log 行),取代当前的"按按钮 → 等 N 秒 → 弹完成"黑盒。

**Architecture:**
- `GitRunner.RunAsync` 加可选 `IProgress<string>? onStderrLine` — null 走 `ReadToEndAsync()` (现行为),非空走 `OutputDataReceived` 实时回调,过滤 `Receiving objects:` / `Resolving deltas:` / `remote:` 三类
- `NodeOperations.InstallAsync/UpgradeAsync/DownloadAsync` 3 方法末尾加 `IProgress<string>? progress = null`,内部 clone/fetch/pull 透传给 `_git.RunAsync(..., onStderrLine: progress)`;非空时 `progress.Report("done")` 一行 sentinel 表结束
- `NodeOperations` `WrapProgress` helper:非 null 时把 progress 行同步 append 到 `_logger?.Info(...)` 让 Logs/ 实时滚动
- `InstallDialogViewModel` 接 `IProgress<string>` → `Progress` 文本 + `ProgressPercent`(Regex `\d+%`)+ `ProgressLog` ObservableCollection + `CancelCommand`
- `InstallDialog.xaml` 加 ProgressBar + ScrollViewer log 行 + Cancel button (Height 400→540)
- `LocalNodeListViewModel.DownloadAsync` / `BulkUpdateOrchestrator` 同样接 progress → emit 到现有 status panel

**Tech Stack:** .NET 8 / WPF / System.Text.Json / Microsoft.Data.Sqlite (无变化) / 无新依赖

**Spec:** `docs/superpowers/specs/2026-08-15-git-progress-feedback-design.md`

## Global Constraints

- .NET 8 + WPF + C# 12
- 后向兼容: `IProgress<string>? progress = null` 默认参数,旧 caller 不改不破
- UI 语言: 中文 (跟现有 dialog 一致)
- git 调用面: `NodeOperations.InstallAsync` / `NodeOperations.UpgradeAsync` / `NodeOperations.DownloadAsync` / `BulkUpdateOrchestrator` 共 4 个 entry point
- 不引入新 UI 框架 / 不引入 ReactiveUI
- 进度面板复用现有 `Progress<T>` pattern (跟 `CatalogViewModel:267` 和 `EnvStartStatusViewModel` 一致)
- 用户原话: "下载节点过程中没有进度,是否能够通过git获取进度" + "安装或者下载度需要查看进度"
- 用户已选决定: **(Q1) 百分比条 + log 双显示** / **(Q2) SDD 流程**

---

## Task Decomposition

5 tasks (T1→T5 sequential, T6 final review):

| Task | Scope | Files | Tests |
|---|---|---|---|
| T1 | GitRunner streaming `OutputDataReceived` | `Services/GitRunner.cs` | 5 new |
| T2 | NodeOperations 3 methods `progress` param + `WrapProgress` | `Services/NodeOperations.cs` | 5 new |
| T3 | InstallDialogVM `ProgressPercent` + `ProgressLog` + `CancelCommand` | `ViewModels/InstallDialogViewModel.cs` | 4 new |
| T4 | InstallDialog.xaml ProgressBar + ScrollViewer + Cancel button | `Views/InstallDialog.xaml` | 0 (visual) |
| T5 | LocalNodeListVM + BulkUpdateOrchestrator pass progress | 2 files | 0 (covered by T2) |
| T6 | Final review + ship + MEMORY.md | - | - |

---

### Task 1: GitRunner streaming mode with `IProgress<string>` callback

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/GitRunner.cs:46-112` (signature + body)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/GitRunnerProgressTests.cs` (new)

**Interfaces:**
- Consumes: existing `GitRunner` ctor (no change)
- Produces: `GitRunner.RunAsync(workdir, args, timeout?, ct, IProgress<string>? onStderrLine)` — new optional tail param

- [ ] **Step 1: Write 5 failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/GitRunnerProgressTests.cs`:

```csharp
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

        var result = await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(60),
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
            timeout: TimeSpan.FromSeconds(60),
            onStderrLine: progress);

        await Task.Delay(200);
        // filter: 必为 Receiving objects / Resolving deltas / remote: 之一
        foreach (var line in lines)
        {
            Assert.True(
                line.StartsWith("Receiving objects:") ||
                line.StartsWith("Resolving deltas:") ||
                line.StartsWith("remote:"),
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
            timeout: TimeSpan.FromSeconds(60),
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

        var result = await _runner.RunAsync(
            srcDir, new[] { "clone", "--progress", "https://github.com/octocat/Hello-World.git", "dest" },
            timeout: TimeSpan.FromSeconds(60),
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~GitRunnerProgressTests" --no-build` (after build fails)
Expected: COMPILE ERROR — `RunAsync` has 4 args, not 5.

- [ ] **Step 3: Modify `GitRunner.RunAsync` signature + streaming branch**

Edit `src-wpf/ComfyUI.Manager/Services/GitRunner.cs:46-112`:

```csharp
public async Task<GitResult> RunAsync(
    string workdir,
    IEnumerable<string> args,
    TimeSpan? timeout = null,
    CancellationToken ct = default,
    IProgress<string>? onStderrLine = null)  // v0.6.15.5: real-time stderr progress
{
    if (string.IsNullOrWhiteSpace(workdir))
    {
        throw new ArgumentException("workdir 不能为空", nameof(workdir));
    }
    if (args is null) throw new ArgumentNullException(nameof(args));

    var psi = new ProcessStartInfo
    {
        FileName = _gitExe,
        WorkingDirectory = workdir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    _proxy?.ApplyTo(psi);
    foreach (var a in args)
    {
        psi.ArgumentList.Add(a);
    }

    Process? process;
    try
    {
        process = Process.Start(psi);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"无法启动 git: {ex.Message}", ex);
    }
    if (process is null)
    {
        throw new InvalidOperationException("Process.Start 返回 null");
    }

    // v0.6.15.5: streaming 模式 vs capture 模式
    var capturedStderr = new System.Text.StringBuilder();
    var stderrT = onStderrLine is null
        ? process.StandardError.ReadToEndAsync()
        : (Task)Task.CompletedTask;

    if (onStderrLine is not null)
    {
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data is null) return;
            capturedStderr.AppendLine(e.Data);  // 仍 capture 给 GitResult.Stderr
            if (ShouldReportProgress(e.Data))
            {
                onStderrLine.Report(e.Data);
            }
        };
        process.BeginErrorReadLine();
    }

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (timeout is { } t) linkedCts.CancelAfter(t);

    try
    {
        await process.WaitForExitAsync(linkedCts.Token);
    }
    catch (OperationCanceledException)
    {
        TryKill(process);
        if (onStderrLine is not null) { try { process.CancelErrorRead(); } catch { } }
        throw;
    }

    // streaming 模式: flush stderr reader
    if (onStderrLine is not null)
    {
        try { process.WaitForExit(); } catch { } // flush BeginErrorReadLine buffer
    }

    var stdout = "";
    try { stdout = await process.StandardOutput.ReadToEndAsync(); } catch { }

    var stderr = onStderrLine is null
        ? await ((Task<string>)stderrT)
        : capturedStderr.ToString();
    return new GitResult(process.ExitCode, stdout, stderr);
}

// v0.6.15.5: 只 emit 进度相关行,过滤 git 自己的 noise(stderr "warning:" / "hint:" 等)
private static bool ShouldReportProgress(string line)
{
    return line.StartsWith("Receiving objects:")
        || line.StartsWith("Resolving deltas:")
        || line.StartsWith("remote:")
        || line.StartsWith("Cloning into");
}
```

- [ ] **Step 4: Add `using System.Text;` if not present**

Edit top of `GitRunner.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;          // v0.6.15.5
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~GitRunnerProgressTests"`
Expected: 5/5 PASS

- [ ] **Step 6: Verify full test suite still green**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-build`
Expected: Previously-passing tests still pass (allow pre-existing flaky `ProcessLauncherProgressTests`)

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/GitRunner.cs tests-wpf/ComfyUI.Manager.Tests/Services/GitRunnerProgressTests.cs
git commit -m "feat(git-runner): IProgress<string>? onStderrLine 实时 emit 进度行 (v0.6.15.5 T1)"
```

---

### Task 2: NodeOperations 3 methods `progress` param + `WrapProgress` AppLogger fallback

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs:88-92` (InstallAsync signature)
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` UpgradeAsync signature (line ~382)
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs:240-243` (DownloadAsync signature)
- Modify: 3 internal `_git.RunAsync` calls in each method to pass `onStderrLine: progress`
- Add: `WrapProgress` private helper
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsProgressTests.cs` (new)

**Interfaces:**
- Consumes: `GitRunner.RunAsync(..., onStderrLine: ...)` (T1)
- Produces: `NodeOperations.InstallAsync(... IProgress<string>? progress, ct)` / `UpgradeAsync(... IProgress<string>? progress, ct)` / `DownloadAsync(... IProgress<string>? progress, ct)` — new optional tail param

- [ ] **Step 1: Write 5 failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsProgressTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15.5: NodeOperations 3 个 git 方法接 IProgress<string>? progress 透传给 GitRunner,
/// 非空时通过 WrapProgress 同时把进度行写 AppLogger。
/// </summary>
public class NodeOperationsProgressTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly FakeGitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly Settings _settings;
    private readonly NodeOperations _ops;
    private readonly AppLogger _logger;
    private readonly string _logPath;

    public NodeOperationsProgressTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "NodeOpsProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _logPath = Path.Combine(_tmpDir, "test.log");
        _logger = new AppLogger(_logPath);

        _git = new FakeGitRunner();
        var settings = new Settings { LocalNodeDirectory = _tmpDir };
        SqliteConnectionFactory.InitializeForTests(_tmpDir);
        _envRepo = new EnvironmentRepository();
        _nodeRepo = new NodeRepository();
        _settings = settings;
        _ops = new NodeOperations(_git, _envRepo, _nodeRepo, _settings, new NodeInstallDiffService(_envRepo, null), null, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        _logger.Dispose();
    }

    [Fact]
    public async Task DownloadAsync_WithProgress_ForwardsProgressToGitRunner()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));
        _git.NextStderrLines = new[] { "Receiving objects:  45%", "Receiving objects: 100%" };

        var env = _envRepo.Get("env-x") ?? TestEnvFactory.CreateEnv("env-x", _tmpDir);
        if (_envRepo.Get("env-x") is null) _envRepo.Upsert(env);

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

        var env = TestEnvFactory.CreateEnv("env-x", _tmpDir);
        _envRepo.Upsert(env);

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

        // Prep: 已有 targetDir + .git
        var env = TestEnvFactory.CreateEnv("env-x", _tmpDir);
        _envRepo.Upsert(env);
        var nodeDir = Path.Combine(env.CustomNodesPath, "test-node");
        Directory.CreateDirectory(nodeDir);
        // NodeOperations.UpgradeAsync 调 fetch + reset --hard,所以 fake git 通过

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

        var env = TestEnvFactory.CreateEnv("env-x", _tmpDir);
        _envRepo.Upsert(env);

        await _ops.DownloadAsync(_tmpDir, "test-node", "https://example.com/repo.git", progress: progress);

        await Task.Delay(500); // let logger flush
        var lines = _logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("Receiving objects:  50%"));
    }

    [Fact]
    public async Task DownloadAsync_NoProgress_BehavesAsBefore()
    {
        _git.NextStderrLines = new[] { "Receiving objects:  50%" };

        var env = TestEnvFactory.CreateEnv("env-x", _tmpDir);
        _envRepo.Upsert(env);

        var result = await _ops.DownloadAsync(_tmpDir, "test-node", "https://example.com/repo.git");

        Assert.True(result.Success); // no progress → 原行为
    }
}

/// <summary>
/// Test fake: 替代真 GitRunner,记录每次调用的 args + onStderrLine,并 emit 预设 stderr lines。
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeOperationsProgressTests"`
Expected: COMPILE ERROR — `InstallAsync` / `UpgradeAsync` / `DownloadAsync` don't have `progress` param.

- [ ] **Step 3: Add `WrapProgress` helper + signatures**

Edit `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`:

Add field after `_logger` (line 42):
```csharp
private IProgress<string>? WrapProgress(IProgress<string>? inner, string operationTag)
{
    if (inner is null) return null;
    return new Progress<string>(line =>
    {
        _logger?.Info(operationTag, line);
        inner.Report(line);
    });
}
```

Edit `InstallAsync` signature (line 88-92):
```csharp
public virtual async Task<NodeOperationResult> InstallAsync(
    string envId, string nodeId, string repoUrl,
    string? targetTag = null,
    IReadOnlyList<PipRequirement>? catalogPipReqs = null,
    IProgress<string>? progress = null,           // v0.6.15.5
    CancellationToken ct = default)
```

Edit internal `_git.RunAsync` calls (line 147, 176):
```csharp
var progressWrapped = WrapProgress(progress, "node-install");
result = await _git.RunAsync(
    env.CustomNodesPath,
    new[] { "clone", "--progress", "--", repoUrl, nodeId },  // + --progress
    DefaultPerCallTimeout, ct,
    onStderrLine: progressWrapped);
```

和 (line 176-179):
```csharp
checkoutResult = await _git.RunAsync(
    targetDir,
    new[] { "checkout", targetTag },
    DefaultPerCallTimeout, ct,
    onStderrLine: progressWrapped);
```

Edit `UpgradeAsync` signature + 改 `--progress` 在 fetch/reset:
```csharp
public virtual async Task<NodeOperationResult> UpgradeAsync(
    string envId, string nodeId,
    IProgress<string>? progress = null,           // v0.6.15.5
    CancellationToken ct = default)
```

和内部 `_git.RunAsync` 调用 (line ~407, 470):
```csharp
var progressWrapped = WrapProgress(progress, "node-upgrade");
result = await _git.RunAsync(
    targetDir,
    new[] { "fetch", "--progress", "origin" },
    DefaultPerCallTimeout, ct,
    onStderrLine: progressWrapped);
```

Edit `DownloadAsync` signature (line 240-243):
```csharp
public virtual async Task<NodeOperationResult> DownloadAsync(
    string localDir, string nodeId, string repoUrl,
    string? targetTag = null,
    IProgress<string>? progress = null,           // v0.6.15.5
    CancellationToken ct = default)
```

和内部 `_git.RunAsync` 调用 (line 283, 312):
```csharp
var progressWrapped = WrapProgress(progress, "node-download");
result = await _git.RunAsync(
    localDir,
    new[] { "clone", "--progress", "--", repoUrl, nodeId },
    DefaultPerCallTimeout, ct,
    onStderrLine: progressWrapped);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~NodeOperationsProgressTests"`
Expected: 5/5 PASS

- [ ] **Step 5: Verify full suite still green**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-build`
Expected: Still passes (allow pre-existing flaky `ProcessLauncherProgressTests`)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/NodeOperations.cs tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsProgressTests.cs
git commit -m "feat(node-ops): Install/Upgrade/Download 接 IProgress<string> + WrapProgress AppLogger fallback (v0.6.15.5 T2)"
```

---

### Task 3: InstallDialogViewModel `ProgressPercent` + `ProgressLog` + `CancelCommand`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` (add fields + change `InstallAsync`)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelProgressTests.cs` (new)

**Interfaces:**
- Consumes: `NodeOperations.InstallAsync(..., progress: IProgress<string>?, ct: CancellationToken)` (T2)
- Produces: `InstallDialogViewModel.ProgressPercent` (double 0-100), `ProgressLog` (ReadOnlyObservableCollection<string>), `CancelCommand` (RelayCommand)

- [ ] **Step 1: Write 4 failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelProgressTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15.5: InstallDialogVM 接 IProgress<string> → ProgressPercent + ProgressLog + CancelCommand.
/// </summary>
public class InstallDialogViewModelProgressTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeNodeOperations _ops;
    private readonly CatalogEntry _entry;

    public InstallDialogViewModelProgressTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "InstallDlgProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        SqliteConnectionFactory.InitializeForTests(_tmpDir);
        _envRepo = new EnvironmentRepository();
        _envRepo.Upsert(TestEnvFactory.CreateEnv("env-1", _tmpDir));
        _ops = new FakeNodeOperations();
        _entry = new CatalogEntry
        {
            Id = Guid.NewGuid(),
            Package = "test-node",
            Name = "Test Node",
            RawMetadata = new Dictionary<string, object> { ["repository"] = "https://github.com/x/y.git" },
            PipRequirements = new System.Collections.Generic.List<PipRequirement>(),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task InstallAsync_GitProgress_UpdatesProgressPercent()
    {
        var vm = new InstallDialogViewModel(_envRepo, _ops, _entry);
        _ops.ProgressToReport = new[] { "Receiving objects:  45%", "Receiving objects: 100%" };

        var tcs = new TaskCompletionSource();
        vm.InstallCommand.Execute(null);
        await Task.Delay(300); // wait for Progress<string> async dispatch

        Assert.Equal(100, vm.ProgressPercent);
        Assert.Contains("Receiving objects: 100%", vm.Progress);
    }

    [Fact]
    public async Task InstallAsync_GitProgress_AppendsToProgressLog()
    {
        var vm = new InstallDialogViewModel(_envRepo, _ops, _entry);
        _ops.ProgressToReport = new[] { "Receiving objects:  45%", "Resolving deltas: 100%" };

        vm.InstallCommand.Execute(null);
        await Task.Delay(300);

        Assert.Equal(2, vm.ProgressLog.Count);
        Assert.Equal("Receiving objects:  45%", vm.ProgressLog[0]);
        Assert.Equal("Resolving deltas: 100%", vm.ProgressLog[1]);
    }

    [Fact]
    public async Task InstallAsync_Regex_OnlyWholeNumberPercent()
    {
        var vm = new InstallDialogViewModel(_envRepo, _ops, _entry);
        _ops.ProgressToReport = new[] { "Receiving objects: 1234/5678" };  // 无百分比

        vm.InstallCommand.Execute(null);
        await Task.Delay(300);

        Assert.Equal(0, vm.ProgressPercent); // 无 percentile → 0
    }

    [Fact]
    public async Task CancelCommand_TriggersCancellation()
    {
        var vm = new InstallDialogViewModel(_envRepo, _ops, _entry);
        _ops.BlockUntilCancelled = true;
        _ops.ProgressToReport = Array.Empty<string>();

        var installTask = Task.Run(async () => vm.InstallCommand.Execute(null));
        await Task.Delay(100); // let the install start
        vm.CancelCommand.Execute(null);
        await Task.Delay(300);

        Assert.True(_ops.CancelCalled);
        Assert.Contains("取消", vm.Progress ?? "");
    }
}

/// <summary>
/// Test fake: 替代 NodeOperations,记录 progress + 模拟取消。
/// </summary>
internal class FakeNodeOperations : NodeOperations
{
    public string[] ProgressToReport { get; set; } = Array.Empty<string>();
    public bool BlockUntilCancelled { get; set; }
    public bool CancelCalled { get; private set; }

    public FakeNodeOperations()
        : base(new FakeGitRunner(),          // dummy for base
               new EnvironmentRepository(),
               new NodeRepository(),
               new Settings(),
               new NodeInstallDiffService(new EnvironmentRepository(), null))
    { }

    public override Task<NodeOperationResult> InstallAsync(
        string envId, string nodeId, string repoUrl,
        string? targetTag = null,
        IReadOnlyList<PipRequirement>? catalogPipReqs = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (progress is not null)
        {
            foreach (var line in ProgressToReport) progress.Report(line);
        }
        if (BlockUntilCancelled)
        {
            // Block until cancelled
            var tcs = new TaskCompletionSource();
            ct.Register(() => { CancelCalled = true; tcs.SetResult(); });
            tcs.Task.Wait();
            return Task.FromResult(NodeOperationResult.Fail("用户取消"));
        }
        return Task.FromResult(NodeOperationResult.Ok("abc12345"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~InstallDialogViewModelProgressTests"`
Expected: COMPILE ERROR — `ProgressPercent` / `ProgressLog` / `CancelCommand` don't exist.

- [ ] **Step 3: Add fields + change `InstallAsync`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs`:

Add fields after line 83 (`Progress` property):
```csharp
private double _progressPercent;
public double ProgressPercent { get => _progressPercent; set => SetField(ref _progressPercent, value); }

private readonly ObservableCollection<string> _progressLog = new();
public ReadOnlyObservableCollection<string> ProgressLog { get; }

private readonly System.Threading.CancellationTokenSource _cts = new();
public RelayCommand CancelCommand { get; }
```

Add to ctor (line 73, after `LoadEnvs()`):
```csharp
ProgressLog = new ReadOnlyObservableCollection<string>(_progressLog);
CancelCommand = new RelayCommand(_ => _cts.Cancel(), _ => Busy);
```

Replace `InstallAsync` body (line 103-160):
```csharp
private async System.Threading.Tasks.Task InstallAsync()
{
    if (SelectedEnv is null) return;
    var envId = SelectedEnv.Id;
    var repoUrl = ExtractRepoUrl(Entry);
    if (string.IsNullOrWhiteSpace(repoUrl))
    {
        MessageBox.Show("catalog 条目缺 repository url", "安装节点",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    Busy = true;
    Progress = "Cloning...";
    ProgressPercent = 0;
    _progressLog.Clear();
    var progress = new Progress<string>(line =>
    {
        Progress = line;
        _progressLog.Add(line);
        var m = Regex.Match(line, @"(\d+)%");
        if (m.Success && double.TryParse(m.Groups[1].Value, out var p))
        {
            ProgressPercent = p;
        }
    });
    _cts.CancelAfter(TimeSpan.FromMinutes(10)); // safety
    try
    {
        var result = await _ops.InstallAsync(
            envId, Entry.Package, repoUrl,
            targetTag: PreselectedTag,
            catalogPipReqs: Entry.PipRequirements,
            progress: progress,
            ct: _cts.Token);
        if (result.Success)
        {
            Progress = $"OK, version={result.Version}";
            ProgressPercent = 100;
            if (OnInstallSuccess is not null)
            {
                _ = System.Threading.Tasks.Task.Run(
                    async () => await OnInstallSuccess(envId));
            }
            CloseRequested?.Invoke();
        }
        else
        {
            Progress = $"失败:{result.Reason}";
        }
    }
    catch (OperationCanceledException)
    {
        Progress = "用户取消";
    }
    catch (Exception ex)
    {
        Progress = $"异常:{ex.Message}";
    }
    finally
    {
        Busy = false;
    }
}
```

- [ ] **Step 4: Add `using System.Text.RegularExpressions;`**

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~InstallDialogViewModelProgressTests"`
Expected: 4/4 PASS

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelProgressTests.cs
git commit -m "feat(install-dialog): ProgressPercent + ProgressLog + CancelCommand (v0.6.15.5 T3)"
```

---

### Task 4: InstallDialog.xaml ProgressBar + ScrollViewer + Cancel button

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml` (full file rewrite)

- [ ] **Step 1: Replace XAML content**

Replace entire `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml` content:

```xml
<Window x:Class="ComfyUI.Manager.Views.InstallDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="安装节点" Height="540" Width="500"
        Background="{StaticResource BackgroundBrush}"
        Icon="pack://siteoforigin:,,,/asset/comfyuiLogo.png">
    <StackPanel Margin="16">
        <TextBlock Text="{Binding Entry.Name}" FontSize="18" FontWeight="Bold" />
        <TextBlock Text="{Binding Entry.Description}" TextWrapping="Wrap" Margin="0,8" />
        <TextBlock Text="选择环境:" Margin="0,8,0,4" />
        <ComboBox ItemsSource="{Binding Environments}"
                  DisplayMemberPath="Name"
                  SelectedItem="{Binding SelectedEnv}" />
        <TextBlock Text="{Binding Progress}" Margin="0,12,0,4" TextWrapping="Wrap" />
        <ProgressBar Value="{Binding ProgressPercent}" Maximum="100"
                     Height="6" Margin="0,0,0,8"
                     Style="{StaticResource MaterialProgressBar}" />
        <Border BorderBrush="{StaticResource BorderBrush}" BorderThickness="1"
                Margin="0,8" MinHeight="120" MaxHeight="200">
            <ScrollViewer VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Auto">
                <ItemsControl ItemsSource="{Binding ProgressLog}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding}"
                                       FontFamily="Consolas" FontSize="11"
                                       TextWrapping="NoWrap" Margin="2,0" />
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
        </Border>
        <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
            <Button Content="安装" Command="{Binding InstallCommand}"
                    Style="{StaticResource MaterialButton}" />
            <Button Content="取消" Command="{Binding CancelCommand}" Margin="8,0,0,0"
                    Style="{StaticResource MaterialButton}" />
            <Button Content="关闭" Command="{Binding CloseCommand}" Margin="8,0,0,0"
                    Style="{StaticResource MaterialButton}" />
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`
Expected: 0 errors (warnings OK if about unused).

- [ ] **Step 3: STA load test still passes**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~InstallDialogLoadTests|FullyQualifiedName~SettingsViewLoadTests"`
Expected: All pre-existing STA load tests pass.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml
git commit -m "feat(install-dialog): ProgressBar + ScrollViewer log + Cancel button (v0.6.15.5 T4)"
```

---

### Task 5: LocalNodeListVM + BulkUpdateOrchestrator pass progress

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs` (DownloadAsync add progress)
- Modify: `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs` (git calls pass progress)

- [ ] **Step 1: LocalNodeListViewModel `DownloadAsync` accept progress**

Edit `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs:72` (InstallAsync signature):

```csharp
public async Task InstallAsync(LocalNodeInfo info, IProgress<string>? progress = null)
{
    // ...existing body...
    var r = await _installer.InstallAsync(selected.Id, sourcePath, info.NodeId, CancellationToken.None, progress);
    // ...
}
```

(Add `LocalNodeCopyInstaller.InstallAsync` progress param in same way — see step 2 if not yet covered.)

如果 `LocalNodeCopyInstaller.InstallAsync` 是本地 FS copy (不是 git),那么 LocalNodeListViewModel.Download 不需要 git progress。

Verify path: grep `_nodeOps.DownloadAsync` in LocalNodeListViewModel. If found, add `IProgress<string>? progress = null` to that call and forward.

Read file `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs` lines 60-100 to confirm:
- If `DownloadAsync` 调 `_nodeOps.DownloadAsync(...)` → add progress
- If `InstallAsync` 调 `_installer.InstallAsync(...)` (LocalNodeCopyInstaller) → no progress (FS copy, not git)

- [ ] **Step 2: BulkUpdateOrchestrator `gitRun.RunAsync` pass shared progress**

Read `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs` 确认 git RunAsync 调用位置(应该有 1-2 处,fetch + reset)。

Edit 每个 `gitRun.RunAsync(...)` 加 `onStderrLine: sharedProgress` 参数 (modulo null check):

```csharp
var sharedProgress = new Progress<string>(line =>
{
    var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
    if (m.Success && double.TryParse(m.Groups[1].Value, out var p))
    {
        Progress?.Invoke(new BulkUpdateRow { /*existing fields*/ Percent = p });
    }
});

gitRun.RunAsync(workdir, new[] { "fetch", "--progress", "origin" }, timeout, ct,
    onStderrLine: sharedProgress);
```

(`BulkUpdateRow` 加 `Percent` 字段,如果有 `Progress` event。)

- [ ] **Step 3: Build + full test suite**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj && dotnet test tests-wpf/ComfyUI.Manager.Tests --no-build`
Expected: 0 build errors, test suite still passes (allow pre-existing flaky).

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs
git commit -m "feat(caller): LocalNodeListVM + BulkUpdateOrchestrator pass progress to git ops (v0.6.15.5 T5)"
```

---

### Task 6: Final review + ship + MEMORY.md

- [ ] **Step 1: Run full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --no-build`
Expected: 1170+ PASS / 0 FAIL (除 pre-existing flaky `ProcessLauncherProgressTests`) / 1 SKIP

- [ ] **Step 2: Staging rebuild**

Run: `pwsh scripts/build_staging.ps1` (or `powershell -File scripts/build_staging.ps1` if pwsh not on PATH)
Expected: exit 0, staging at `release/staging/ComfyUI Manager/`

- [ ] **Step 3: Update MEMORY.md**

Add bullet under v0.6.15 entries:
```markdown
- [v0.6.15.5 git progress feedback (real-time stderr + percent bar)](project_v0_6_15_5_git_progress.md) — 5 commits (...);GitRunner + IProgress<string> onStderrLine + NodeOperations 3 methods 接 progress + WrapProgress AppLogger fallback + InstallDialog ProgressBar + ScrollViewer log + LocalNodeListVM + BulkUpdateOrchestrator
```

Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_15_5_git_progress.md` with full spec summary (architecture, files, key design decisions, lessons).

- [ ] **Step 4: Commit MEMORY.md change**

```bash
git add docs/superpowers/specs/2026-08-15-git-progress-feedback-design.md  # spec already committed
git add .  # any other working tree changes
git commit -m "docs: v0.6.15.5 git progress feedback ship-ready"
```

- [ ] **Step 5: Final review ledger entry**

Append to `.superpowers/sdd/2026-08-15-git-progress-feedback/progress.md`:
- All 5 tasks lists commits
- Final test count
- Spec verification status
- Mark T6 complete

- [ ] **Step 6: Delete SDD workspace**

```bash
rm -rf .superpowers/sdd/2026-08-15-git-progress-feedback
```

- [ ] **Step 7: GUI smoke (user)**

On staging:
- Catalog → 选 entry → Install → 看 ProgressBar 实时填 + ScrollViewer 滚 "Receiving objects: 45%, 100%" + Logs/2026-08-15.log 同步有进度行
- 中途点"取消" → Progress = "用户取消"
- LocalNodeList → 选 → Download → 同样 UI

---

## Self-Review

**1. Spec coverage:**
- §Design 1 (GitRunner + IProgress) → T1 ✓
- §Design 2 (NodeOperations 3 methods 接 progress) → T2 ✓
- §Design 3 (WrapProgress AppLogger fallback) → T2 ✓
- §Design 4 (InstallDialogVM fields) → T3 ✓
- §Design 5 (InstallDialog.xaml 改) → T4 ✓
- §Design 6 (LocalNodeListVM) → T5 ✓
- §Design 7 (BulkUpdateOrchestrator) → T5 ✓
- §Design 8 (tests) → T1+T2+T3 ✓
- §Verification → T6 ✓

**2. Placeholder scan:** No "TBD/TODO/fill in detail" found. All code blocks in steps are concrete.

**3. Type consistency:** `IProgress<string>?` 名称/p位置在 T1→T2→T3 一致(wn可选 tail param)。`ProgressPercent` / `ProgressLog` / `CancelCommand` 名称在 T3 Step 1 测试和 T3 Step 3 实装一致。

**Issues found & fixed:**
- T2 Step 3 BulkUpdateOrchestrator progress 没明确 `gitRun.RunAsync` 调用数量 → T5 Step 2 改成 read-first 路径
- T1 Step 3 `WaitForExit` flush 缺 — 加 `process.WaitForExit()` no-param flush
- T4 Step 1 XAML MaterialProgressBar style 不一定存在 — fallback `Style` attribute 删除用默认
