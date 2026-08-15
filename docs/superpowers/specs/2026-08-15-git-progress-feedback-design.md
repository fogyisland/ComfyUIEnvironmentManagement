# v0.6.15.5 Git Operation Progress Feedback (real-time stderr + percent bar)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 install / download / upgrade 三个 git 操作显示实时进度(百分比 progress bar + 实时 log 行),取代当前的"按按钮 → 等 N 秒 → 弹完成"黑盒。

**Architecture:**
- `GitRunner.RunAsync` 加可选 `IProgress<string>? onStderrLine` 参数 — null 走 `ReadToEndAsync()` (现行为),非空走 `OutputDataReceived` 实时回调,过滤 `Receiving objects:` / `Resolving deltas:` / `remote:` 三类
- `NodeOperations.InstallAsync/UpgradeAsync/DownloadAsync` 3 个方法签名末尾加 `IProgress<string>? progress = null`,默认 null 保后向兼容;内部 clone/fetch/pull 调用时透传给 `_git.RunAsync(..., onStderrLine: progress)`
- `NodeOperations` 析构 `progress` 行为:非 null 时在 fetch/clone 收尾 emit `progress.Report("done")` 一行 sentinel,让 UI 知道操作结束
- `InstallDialogViewModel` 接 `IProgress<string>` → 实时更新 `Progress` 文本 + 解析百分比到 `ProgressPercent`(Regex 抓 `(\d+)%`)
- `InstallDialog.xaml` 加 ProgressBar + ScrollViewer 包对 log 行
- `LocalNodeListViewModel.DownloadCommand` / `BulkUpdateOrchestrator` 同样接 progress,emit 到现有 status panel
- AppLogger 自动 fallback: `NodeOperations` 收到 `progress != null` 时,在 progress.Report 同步 append `_logger?.Info("node-download", line)` — 这样 Logs/2026-08-15.log 实时滚动,无需 UI 配合也能 staging 看到

**Tech Stack:** .NET 8 / WPF / System.Text.Json / Microsoft.Data.Sqlite (无变化) / 无新依赖

## Global Constraints

- .NET 8 + WPF + C# 12
- 后向兼容: `IProgress<string>? progress = null` 默认参数,旧 caller 不改不破
- UI 语言: 中文 (跟现有 dialog 一致)
- git 调用面: `NodeOperations.InstallAsync` / `NodeOperations.UpgradeAsync` / `NodeOperations.DownloadAsync` / `BulkUpdateOrchestrator` 共 4 个 entry point
- 不引入新 UI 框架 / 不引入 ReactiveUI
- 进度面板复用现有 `Progress<T>` pattern (跟 `CatalogViewModel:267` 和 `EnvStartStatusViewModel` 一致)
- 用户原话: "下载节点过程中没有进度,是否能够通过git获取进度" + "安装或者下载度需要查看进度"
- 用户已选决定: **(Q1) 百分比条 + log 双显示** / **(Q2) SDD 流程**

## Background

### 现状

- `GitRunner.RunAsync` (122 lines): 用 `process.StandardOutput.ReadToEndAsync()` + `ReadToEndAsync()` 等 stdout/stderr 读完才返回 `GitResult`,中途进度不暴露
- `NodeOperations.InstallAsync` (line 147): `git clone -- <repoUrl> <nodeId>` — 默认**无** `--progress` 标志
- `NodeOperations.UpgradeAsync` (line 407): `git fetch origin` + `git reset --hard origin/HEAD` — 默认**无** `--progress` 标志
- `NodeOperations.DownloadAsync` (line 283): `git clone -- <repoUrl> <nodeId>` — 默认**无** `--progress` 标志
- `BulkUpdateOrchestrator.RunAsync` (line 250): 自己调 `gitRun.RunAsync(workdir, ...)` 拉每个 env × node 状态 — 默认**无** `--progress` 标志
- `InstallDialog.xaml:14`: `<TextBlock Text="{Binding Progress}" Margin="0,8" />` — 只有一行字,装完写一次
- `InstallDialogViewModel.Progress` (line 82-83): 单 string property,赋值 `"Cloning..."` / `"OK, version=..."` / `"失败:..."` 三种状态
- `LoadEnvs` (line 85-101): dialog 启动时列出 env,选 SelectedEnv
- 无 CancellationToken 传递路径(InstallDialog 调 InstallAsync 传 `ct: default`)

### 现状日志样本(2026-08-15 staging)

```
[17:14:58.763] [node-download] dir='...' node=' ComfyUI-Light-N-Color' 开始下载
[17:15:08.756] [node-download] dir='...' node='0246' 开始下载
[17:15:18.025] [node-download] dir='...' node='0246' 下载成功 version=d09cb3fe
```

10 秒间隔,中间完全没进度。**用户报"没有进度"**就是这个。

## Design

### 1. GitRunner 加进度回调

**File:** `src-wpf/ComfyUI.Manager/Services/GitRunner.cs`

```csharp
public async Task<GitResult> RunAsync(
    string workdir,
    IEnumerable<string> args,
    TimeSpan? timeout = null,
    CancellationToken ct = default,
    IProgress<string>? onStderrLine = null)  // NEW
```

- `onStderrLine == null`: 走现 `ReadToEndAsync()` 路径 (完全向后兼容)
- `onStderrLine != null`: 改 `process.ErrorDataReceived += (s, e) => { if (e.Data != null && ShouldReport(e.Data)) onStderrLine.Report(e.Data); }`
- `ShouldReport(line)`: line 包含 `Receiving objects:` / `Resolving deltas:` / `remote:` 任一前缀 → return true
- `OutputDataReceived` 不订阅(进度在 stderr,stdout 通常空)
- `Process.Exited` 时 `WaitForExitAsync` 后调 `BeginErrorReadLine()` 之前 `WaitForExitAsync` 等,接 `WaitForExitAsync` 后再 `process.WaitForExit()` (no-param) flush stdio buffer

### 2. Git 操作参数透传

**File:** `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`

3 个方法签名末尾加 `IProgress<string>? progress = null`:

```csharp
public virtual async Task<NodeOperationResult> InstallAsync(
    string envId, string nodeId, string repoUrl,
    string? targetTag = null,
    IReadOnlyList<PipRequirement>? catalogPipReqs = null,
    IProgress<string>? progress = null,           // NEW
    CancellationToken ct = default)

public virtual async Task<NodeOperationResult> UpgradeAsync(
    string envId, string nodeId,
    IProgress<string>? progress = null,           // NEW
    CancellationToken ct = default)

public virtual async Task<NodeOperationResult> DownloadAsync(
    string localDir, string nodeId, string repoUrl,
    string? targetTag = null,
    IProgress<string>? progress = null,           // NEW
    CancellationToken ct = default)
```

`progress != null` 时:
- 内部 `_git.RunAsync(..., onStderrLine: progress)` 透传
- `progress.Report("done")` 在 clone/checkout/fetch/reset 完成后 emit,让 UI 知道 terminal

`progress == null` 时: 走 `_git.RunAsync(..., onStderrLine: null)` 完全原行为

### 3. AppLogger 自动 fallback

`NodeOperations` 收到 `progress != null` 时,内部 wrap 一层把进度行 append 到 `_logger`:

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

调用例: `WrapProgress(progress, "node-download")` 然后透传给 `_git.RunAsync`。这样 Logs/2026-08-15.log 实时滚动,无需 UI 配合。

### 4. InstallDialogViewModel

**File:** `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs`

新字段:

```csharp
private double _progressPercent;
public double ProgressPercent { get => _progressPercent; set => SetField(ref _progressPercent, value); }

private readonly System.Collections.ObjectModel.ObservableCollection<string> _progressLog = new();
public System.Collections.ObjectModel.ReadOnlyObservableCollection<string> ProgressLog { get; }
```

`InstallAsync` 改:

```csharp
Busy = true;
Progress = "Cloning...";
ProgressPercent = 0;
ProgressLog.Clear();
var progress = new Progress<string>(line =>
{
    Progress = line;
    ProgressLog.Add(line);
    var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
    if (m.Success && double.TryParse(m.Groups[1].Value, out var p))
    {
        ProgressPercent = p;
    }
});
try
{
    var result = await _ops.InstallAsync(
        envId, Entry.Package, repoUrl,
        targetTag: PreselectedTag,
        catalogPipReqs: Entry.PipRequirements,
        progress: progress,
        ct: default);
    // ...
}
```

加 cancellation token 路径: ctor 接受 `CancellationTokenSource? cts = null`; `InstallCommand` 加 XAML 取消按钮,`cts.Cancel()` 触发,`InstallAsync` 抛 `OperationCanceledException` → `Progress = "用户取消"`。

### 5. InstallDialog.xaml

**File:** `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml`

加 ProgressBar + ScrollViewer:

```xml
<TextBlock Text="{Binding Progress}" Margin="0,8" />
<ProgressBar Value="{Binding ProgressPercent}" Maximum="100" Height="6" Margin="0,4" />
<Border BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Margin="0,8" Height="120">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <ItemsControl ItemsSource="{Binding ProgressLog}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11" TextWrapping="NoWrap" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </ScrollViewer>
</Border>
<StackPanel Orientation="Horizontal" Margin="0,16,0,0">
    <Button Content="安装" Command="{Binding InstallCommand}" Style="{StaticResource MaterialButton}" />
    <Button Content="取消" Command="{Binding CancelCommand}" Margin="8,0,0,0" Style="{StaticResource MaterialButton}" />
</StackPanel>
```

加 `CancelCommand` (RelayCommand `_ => _cts?.Cancel()`)。窗口高度 400 → 540。

### 6. LocalNodeListViewModel.DownloadAsync

**File:** `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs`

`DownloadAsync` 接受 `IProgress<string>? progress = null` 参数 (传透给 `_nodeOps.DownloadAsync`),`progress != null` 时 emit 到 `DownloadStatus` property。

### 7. BulkUpdateOrchestrator

**File:** `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs`

每个 env × node 调用 `gitRun.RunAsync(workdir, ..., onStderrLine: sharedProgress)` 透传,sharedProgress 收集所有行的 max percent。

### 8. 测试

**File:** `tests-wpf/ComfyUI.Manager.Tests/Services/GitRunnerProgressTests.cs` (new)

- `RunAsync_WithProgress_EmitsReceivingObjectsLines`
- `RunAsync_WithProgress_FiltersOutNonProgressLines`
- `RunAsync_WithProgress_HighestPercentWins` (解析 45% 后 70% → report 70)
- `RunAsync_NoProgress_BehavesAsBefore` (stderr 仍捕获)
- `RunAsync_StderrLineOrder_IsPreserved` (Progress<string> 同步 sequential)

**File:** `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsProgressTests.cs` (new)

- `InstallAsync_WithProgress_ForwardsProgressToGitRunner`
- `DownloadAsync_WithProgress_ForwardsProgressToGitRunner`
- `UpgradeAsync_WithProgress_ForwardsProgressToGitRunner`
- `InstallAsync_AppLoggerFallback_LogsProgressLines`
- `InstallAsync_NoProgress_BehavesAsBefore`

**File:** `tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelProgressTests.cs` (new)

- `InstallAsync_GitProgress_UpdatesProgressPercent`
- `InstallAsync_GitProgress_AppendsToProgressLog`
- `InstallAsync_Regex_OnlyWholeNumberPercent` (45% 解析, "1234 objects" 不解析)
- `CancelCommand_TriggersCancellation`

### 9. File changes summary

| File | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/GitRunner.cs` | +1 param `IProgress<string>? onStderrLine`;branch 上 OutputDataReceived |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | 3 methods +1 param `IProgress<string>? progress`;`WrapProgress` helper;`progress.Report("done")` sentinel |
| `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` | +`ProgressPercent` + `ProgressLog` + `CancelCommand`;改 `InstallAsync` 接 progress |
| `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml` | +ProgressBar + ScrollViewer + Cancel button;Height 400→540 |
| `src-wpf/ComfyUI.Manager/ViewModels/LocalNodeListViewModel.cs` | `DownloadAsync` +`IProgress<string>?` 参数 |
| `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs` | 调 `gitRun.RunAsync` 传 `onStderrLine: sharedProgress` |
| `tests-wpf/.../GitRunnerProgressTests.cs` | new, 5 测试 |
| `tests-wpf/.../NodeOperationsProgressTests.cs` | new, 5 测试 |
| `tests-wpf/.../InstallDialogViewModelProgressTests.cs` | new, 4 测试 |

### 10. Out of scope

- `BaseEnvInstaller` (BED) — 跑 pip install,不是 git,不需进度
- `RequirementsInstaller` — 跑 pip,不是 git
- `ProcessLauncher` — 启动 ComfyUI Python,不是 git,已有 3-stage 状态面板
- `LocalNodeCopyInstaller` — 本地 FS copy,不是 git 拉取,不需要进度
- Auto-rollback on cancellation — 用户取消时**不**做事务回滚,留 partial state (跟现行为一致)
- Multi-progress for concurrent installs — 串行 (NodeOperations 串行声明),无需并发 UI 协调

## Verification

1. `dotnet build` 0 errors
2. `dotnet test tests-wpf/ComfyUI.Manager.Tests` 全套 1160+ PASS / 0 FAIL (除 pre-existing flaky `ProcessLauncherProgressTests`)
3. 13 new tests pass (5 GitRunner + 5 NodeOperations + 4 InstallDialogVM)
4. Staging rebuild: `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` 跑通
5. GUI smoke:
   - 启动 app → Catalog tab → 选节点 → 点 Install → 弹 InstallDialog
   - 选 env → 点"安装" → ProgressBar 实时填 (0% → 100%) + ScrollViewer 实时滚 "Receiving objects: 25%, 50%, 75%, 100%" + Logs/2026-08-15.log 同步有 `[node-install]` 进度行
   - 中途点"取消" → OperationCanceledException → `Progress = "用户取消"`
   - LocalNodeList → 选节点 → 点 Download → 同样 UI 反馈
   - 卸 system git 后跑 staging → 不报 "git not found" (继承 v0.6.15.4)
6. 终端日志样本(预期):
   ```
   [20:01:23.001] [node-install] env='env-xxx' node=' ComfyUI-Manager' 开始安装
   [20:01:23.045] [node-install] Cloning into 'ComfyUI-Manager'...
   [20:01:25.123] [node-install] remote: Counting objects: 1234, done.
   [20:01:26.456] [node-install] Receiving objects:  45% (555/1234)
   [20:01:27.789] [node-install] Receiving objects: 100% (1234/1234), 1.23 MiB | 5.67 MiB/s, done.
   [20:01:28.012] [node-install] Resolving deltas: 100% (567/567), done.
   [20:01:33.456] [node-install] env='env-xxx' node=' ComfyUI-Manager' 安装成功 sha=abc12345 tag=v2.0.0
   ```
