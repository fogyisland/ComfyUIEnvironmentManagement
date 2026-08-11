# v0.6.11+ SDD D1: Auto-Restart Env After Node Install — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `NodeOperations.InstallAsync` 成功完成后,自动停止(若运行中)+ 启动**目标 env**,并自动切换到 env-list tab,让用户通过现有 `EnvStartStatusViewModel` 面板看到 Stop+Start 进度。

**Architecture:** Direct chain in trigger VM。`InstallDialog` 新增 `OnInstallSuccess` 回调 ctor 参 → 装成功路径 fire-and-forget 触发回调(`Task.Run` 挪后台线程)→ `EnvironmentListViewModel.OpenInstallNodePicker` 注入 `MainViewModel.RestartEnvAsync` 作为回调 → MVM 切到 env-list tab → MVM 委托给 `EnvListVM.RestartEnvInternalAsync` → 复用 per-env mutex + `EnvStartStatusViewModel` 显示进度。失败 → AppLogger + status panel,节点保留。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 现有 `AppLogger` / `EnvStartStatusViewModel` / `EnvironmentListViewModel` per-env mutex(v0.6.5.22) / `SetMainViewModel` setter 模式(v0.6.5 既有)

**base SHA:** `c509534`(v0.6.11+ SDD B MERGED,HEAD)
**target:** `c509534..HEAD`,3 commits on `auto-restart-env` branch
**baseline tests:** 779/1/1(2 pre-existing flaky `ProcessLauncherProgressTests` 时序敏感,非 D1 引入)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | 触发源 = **唯一**:`NodeOperations.InstallAsync` 成功路径(env 内装节点)。Catalog 装节点 / Download 节点不触发。 | spec §Scope + §1 |
| G2 | 目标 env = **唯一**:仅触发时传入的 env(不广播到所有装了同包的 env) | spec §Scope |
| G3 | 重启动作:`env.Status == "running"` → Stop + Start;否则只 Start | spec §Scope + §6 |
| G4 | 失败处理:重启失败时,**节点保留**,错误经 `EnvStartStatusViewModel.Fail(...)` 显示在 env-start 面板(node 装一半回滚风险太高) | spec §Scope + §6 |
| G5 | UI 切换:重启触发时自动切到 env-list tab,让用户看到 stop+start 进度 | spec §Scope + §5 |
| G6 | 架构:**Direct chain in trigger VM** — 每个 install 触发点显式调用 `MVM.RestartEnvAsync(envId)`,**不**引入 event bus / orchestrator | spec §Scope |
| G7 | 回调**不 await** —— dialog 立刻关,用户不用在 dialog 内等 10+ 秒的 stop+start | spec §2 |
| G8 | 用 `Task.Run` 把回调挪到线程池线程,避免 dialog UI thread 在 `await` 时阻塞 | spec §2 |
| G9 | 失败路径(`result.Success == false`)和异常路径(`catch`)不触发回调 —— 只在**真正装成功**时重启 | spec §2 |
| G10 | `Func<string, Task>? OnInstallSuccess` 默认 null = 行为不变(向后兼容) | spec §3 |
| G11 | `MainViewModel` ↔ `EnvironmentListViewModel` 循环依赖 → 用 `SetMainViewModel` setter + ctor 末尾赋值,跟 v0.6.5 既有 pattern 一致 | spec §4 |
| G12 | 复用 v0.6.5.22 per-env 互斥锁(`IsEnvBusy` / `MarkEnvBusy` / `UnmarkEnvBusy` + `BusyKind`),不引入新互斥机制 | spec §7 |
| G13 | `RestartEnvInternalAsync` busy 时 skip + AppLogger warn,不弹错误 | spec §6 |
| G14 | MVM 端无 AppLogger 时不抛(null-safe `?.`) | spec §5 |
| G15 | 测试 seam:`RestartEnvOverride` (`Func<string, Task>?`) 让 unit test 不真跑 stop+start | spec §5 |
| G16 | 不改 `NodeOperations.InstallAsync` 签名 | spec §非目标 |
| G17 | 不改 `EnvironmentListViewModel.StartEnvAsync` / `StopEnvAsync` 现有签名 / 行为 | spec §非目标 |
| G18 | 不改 `EnvStartStatusViewModel` —— 现有 start/stop 反馈机制已覆盖 | spec §非目标 |
| G19 | 不做 Settings toggle「自动重启」(默认开,无可配置) | spec §YAGNI |
| G20 | 不做「重启所有装了同包的 env」(单一目标 env) | spec §YAGNI |
| G21 | 不做 rollback node install(失败就失败,用户手动处理) | spec §YAGNI |
| G22 | 不做 concurrent install 协调(per-env mutex 已够) | spec §YAGNI |
| G23 | 命名:`RestartEnvInternalAsync(env, ct)` 在 EnvListVM;`RestartEnvAsync(envId)` 在 MVM | spec §5 + §6 |
| G24 | `Environment.Status == "running"` 是判断 env 运行的实际字段(`Models/Environment.cs:34`,string,默认 "stopped") | 实施期核实 |
| G25 | EnvListVM 已有 `StartEnvAsync(Environment?)` / `StopEnvAsync(Environment?)` 私有方法(`ViewModels/EnvironmentListViewModel.cs:434` / `:472`),不传 ct(内部 `default`);RestartEnvInternalAsync 内部直接调它们 | 实施期核实 |
| G26 | AppLogger API:`Info/Warn/Error` 方法(`Services/AppLogger.cs:97/100/103`);nullable ctor 模式跟 `BaseEnvInstaller` 一致 | 实施期核实 |

---

## File Structure

**改动**(本 SDD):

| 文件 | 职责 |
|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` | 加 `OnInstallSuccess` ctor 参 + 装成功路径 fire-and-forget 触发 |
| `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs` | `Show(...)` 加 `onInstallSuccess` 透传参数 |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | 加 `RestartEnvInternalAsync` + `SetMainViewModel` setter + `_mvm` 字段 + `AppLogger?` 字段;`OpenInstallNodePicker` 注入回调;`BusyKind` 加 `Restart` |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | 加 `RestartEnvAsync` + `RestartEnvOverride` seam + `AppLogger?` 字段;ctor 末尾 `_environmentsViewModel.SetMainViewModel(this)` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 把现有 `logger` 传给 MVM ctor |

**新建**(测试):

| 文件 | 测试数 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelRestartTests.cs` | 3 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelRestartTests.cs` | 4 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelRestartEnvTests.cs` | 5 |

**不动**:
- `Services/NodeOperations.cs` —— `InstallAsync` 签名不变
- `Models/Environment.cs` —— 不加字段
- `ViewModels/EnvStartStatusViewModel.cs` —— 现有 API 够用
- `Views/SettingsView.xaml` —— 不加 Settings toggle(YAGNI)
- `Views/CatalogView.xaml` —— 不接自动重启(Catalog 无 install 入口)

---

## Task Breakdown

### Task 1: InstallDialog OnInstallSuccess 回调

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs:36-53` (ctor + 加 `OnInstallSuccess` 属性)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs:110-114` (装成功路径 fire callback)
- Modify: `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs:24-34` (`Show` 加 `onInstallSuccess` 参数)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelRestartTests.cs`

**Interfaces:**
- Consumes: 无(底层任务)
- Produces:
  - `InstallDialogViewModel.OnInstallSuccess` property `Func<string, Task>?`
  - `InstallDialog.Show(envRepo, nodeOps, entry, preselectedEnvId, preselectedTag, onInstallSuccess)` 6 参
  - 装成功(`result.Success == true`)后 fire-and-forget 调用 `OnInstallSuccess(envId)`,**不 await**

**Step 1.1: 写失败测试 — 装成功触发回调**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelRestartTests.cs`:

```csharp
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class InstallDialogViewModelRestartTests
{
    [Fact]
    public async Task Install_Success_FiresOnInstallSuccess_WithEnvId()
    {
        var ops = new FakeNodeOperations { NextResult = new NodeInstallResult(true, "v1.0", null) };
        string? capturedEnvId = null;
        var tcs = new TaskCompletionSource();
        var vm = new InstallDialogViewModel(
            new EnvironmentRepositoryStub(),
            ops,
            new CatalogEntry { Package = "p", Title = "t" },
            preselectedEnvId: "env-1",
            onInstallSuccess: async envId => { capturedEnvId = envId; await Task.Yield(); tcs.SetResult(); });

        vm.InstallCommand.Execute(null);
        await tcs.Task;

        Assert.Equal("env-1", capturedEnvId);
    }

    [Fact]
    public async Task Install_Failure_DoesNotFireOnInstallSuccess()
    {
        var ops = new FakeNodeOperations { NextResult = new NodeInstallResult(false, null, "fail") };
        int callCount = 0;
        var vm = new InstallDialogViewModel(
            new EnvironmentRepositoryStub(),
            ops,
            new CatalogEntry { Package = "p" },
            preselectedEnvId: "env-1",
            onInstallSuccess: _ => { callCount++; return Task.CompletedTask; });

        vm.InstallCommand.Execute(null);
        // 等待 InstallAsync 完成(Busy 翻 false)
        for (var i = 0; i < 50 && vm.Busy; i++) await Task.Delay(20);

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Install_Exception_DoesNotFireOnInstallSuccess()
    {
        var ops = new FakeNodeOperations { ThrowOnInstall = new System.InvalidOperationException("boom") };
        int callCount = 0;
        var vm = new InstallDialogViewModel(
            new EnvironmentRepositoryStub(),
            ops,
            new CatalogEntry { Package = "p" },
            preselectedEnvId: "env-1",
            onInstallSuccess: _ => { callCount++; return Task.CompletedTask; });

        vm.InstallCommand.Execute(null);
        for (var i = 0; i < 50 && vm.Busy; i++) await Task.Delay(20);

        Assert.Equal(0, callCount);
    }

    // ---- fakes ----

    private class FakeNodeOperations : NodeOperations
    {
        public NodeInstallResult NextResult { get; set; }
        public System.Exception? ThrowOnInstall { get; set; }
        public FakeNodeOperations() : base(null!, null!, null!, null!) { }
        public new Task<NodeInstallResult> InstallAsync(string envId, string package, string repoUrl, string? targetTag = null, System.Collections.Generic.IReadOnlyList<PythonRequirement>? catalogPipReqs = null, System.Threading.CancellationToken ct = default)
        {
            if (ThrowOnInstall is not null) throw ThrowOnInstall;
            return Task.FromResult(NextResult);
        }
    }

    private class EnvironmentRepositoryStub : EnvironmentRepository
    {
        public EnvironmentRepositoryStub() : base(null!) { }
        public new System.Collections.Generic.List<Environment> ListAll() => new();
    }
}
```

**Step 1.2: 跑测试确认 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~InstallDialogViewModelRestart" -v minimal
```

期望:`error CS1739: 'InstallDialogViewModel' 不含 'OnInstallSuccess'` 或 `error CS1739: ... 不含带 6 个参数的构造函数` — 编译失败因为 ctor 还没加 `onInstallSuccess`。

**Step 1.3: 在 InstallDialogViewModel 加 OnInstallSuccess ctor 参 + 装成功触发**

`src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs`,把类签名替换为:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class InstallDialogViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly NodeOperations _ops;
    public CatalogEntry Entry { get; }
    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand InstallCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public string? PreselectedEnvId { get; }
    public string? PreselectedTag { get; }

    /// <summary>
    /// v0.6.11+ SDD D1: 安装成功回调(caller 注入,典型 = mvm.RestartEnvAsync)。
    /// null = 不触发自动重启(测试 / 离线场景)。
    /// </summary>
    public Func<string, Task>? OnInstallSuccess { get; }

    public InstallDialogViewModel(
        EnvironmentRepository repo,
        NodeOperations ops,
        CatalogEntry entry,
        string? preselectedEnvId = null,
        string? preselectedTag = null,
        Func<string, Task>? onInstallSuccess = null)
    {
        _repo = repo;
        _ops = ops;
        Entry = entry;
        PreselectedEnvId = preselectedEnvId;
        PreselectedTag = preselectedTag;
        OnInstallSuccess = onInstallSuccess;
        InstallCommand = new RelayCommand(
            async _ => await InstallAsync(),
            _ => SelectedEnv is not null && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        LoadEnvs();
    }

    private Environment? _selectedEnv;
    public Environment? SelectedEnv { get => _selectedEnv; set => SetField(ref _selectedEnv, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { if (SetField(ref _busy, value)) InstallCommand.RaiseCanExecuteChanged(); } }

    private string? _progress;
    public string? Progress { get => _progress; set => SetField(ref _progress, value); }

    private void LoadEnvs()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
        if (!string.IsNullOrEmpty(PreselectedEnvId))
        {
            var match = Environments.FirstOrDefault(e => e.Id == PreselectedEnvId);
            if (match is not null)
            {
                SelectedEnv = match;
                return;
            }
        }
        if (Environments.Count > 0) SelectedEnv = Environments[0];
    }

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
        try
        {
            var result = await _ops.InstallAsync(
                envId, Entry.Package, repoUrl,
                targetTag: PreselectedTag,
                catalogPipReqs: Entry.PipRequirements,
                ct: default);
            if (result.Success)
            {
                Progress = $"OK, version={result.Version}";
                // v0.6.11+ SDD D1: 触发自动重启回调(不 await — dialog 立刻关,
                // 真正的 stop+start 在 background 跑,env-start panel 在 env-list tab 显示)。
                // Task.Run 挪后台线程,避免 dialog UI thread 在 await 时阻塞
                // (dialog 关闭会触发 dispatcher 抛异常)。
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
        catch (Exception ex)
        {
            Progress = $"异常:{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }
}
```

注意:fakes 必须能编过。先 grep 现有测试看 `NodeOperations` ctor 长啥样;`FakeNodeOperations` 可能需要不同的 stub 模式(实际 `NodeOperations` ctor 可能是 internal,测试项目用 `InternalsVisibleTo` 已能访问)。如果基类 ctor 限制多,改用 `InstallAsync` 委托模式 + `NodeOperations` 接口/seam。

**简化方案**(若 FakeNodeOperations 编译失败):
```csharp
// 改用 delegating 包装
private class FakeNodeOperations : NodeOperations
{
    public Func<string, string, string, Task<NodeInstallResult>> InstallFn { get; set; }
        = (_, _, _) => Task.FromResult(new NodeInstallResult(true, "v1.0", null));
    public FakeNodeOperations() : base(null!, null!, null!, null!) { }
    public override Task<NodeInstallResult> InstallAsync(
        string envId, string package, string repoUrl,
        string? targetTag = null,
        System.Collections.Generic.IReadOnlyList<PythonRequirement>? catalogPipReqs = null,
        System.Threading.CancellationToken ct = default)
        => InstallFn(envId, package, repoUrl);
}
```

如果 `NodeOperations` 不 virtual,把 `InstallCommand` 改直接调用 `_ops.InstallAsync` 难测。**plan deviation accepted**:实际签名以 grep 为准,改用可行方案;若 `NodeOperations` 是 sealed + non-virtual,改用 `INodeOperations` interface seam(若已存在)或在 InstallDialogViewModel 加 `NodeOperations` 子类可替换 seam(在测试项目新建 internal 子类,生产代码不变)。

**Step 1.4: `InstallDialog.Show` 加 `onInstallSuccess` 参数**

`src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs`:

```csharp
using System;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class InstallDialog : Window
{
    public InstallDialog(InstallDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }

    /// <summary>
    /// Show(envRepo, nodeOps, entry, preselectedEnvId, preselectedTag, onInstallSuccess):弹 InstallDialog,
    /// preselectedEnvId 非空时默认选中该 env,空时选第一个 env。
    /// preselectedTag(v0.6.11 T3):caller 显式选中的 GitHub tag,装完 git checkout 钉到该版本。
    /// onInstallSuccess(v0.6.11+ SDD D1):装成功时 fire-and-forget 回调(caller 典型传
    /// MainViewModel.RestartEnvAsync);null = 不触发(向后兼容)。
    /// 调用方提供 envRepo + nodeOps(由 App.xaml.cs 统一构造,跟其他 view 共享同一份)。
    /// </summary>
    public static void Show(
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogEntry entry,
        string? preselectedEnvId = null,
        string? preselectedTag = null,
        Func<string, Task>? onInstallSuccess = null)
    {
        var vm = new InstallDialogViewModel(
            envRepo, nodeOps, entry, preselectedEnvId, preselectedTag, onInstallSuccess);
        var dlg = new InstallDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }
}
```

**Step 1.5: 跑测试 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~InstallDialogViewModelRestart" -v minimal
```

期望:3 PASS / 0 FAIL。

**Step 1.6: 全套跑回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

期望:782+/1/1(baseline 779 + 3 新增,无回归)。

**Step 1.7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/InstallDialogViewModelRestartTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): InstallDialog OnInstallSuccess callback (D1 plumbing)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: EnvironmentListViewModel RestartEnvInternalAsync + SetMainViewModel + logger

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:50` (`BusyKind` 加 `Restart`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:170-206` (ctor 加 `AppLogger? logger = null` + `_mvm` 字段 + `SetMainViewModel` setter + `_logger` 字段)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:1118-1132` (`OpenInstallNodePicker` 注入回调)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (新增 `RestartEnvInternalAsync` 方法)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelRestartTests.cs`

**Interfaces:**
- Consumes:
  - `Environment.Status == "running"`(string,默认 "stopped")— 实际字段名
  - `StartEnvAsync(Environment?)` / `StopEnvAsync(Environment?)` 私有方法(不传 ct,内部 `default`)
  - v0.6.5.22 `IsEnvBusy` / `MarkEnvBusy` / `UnmarkEnvBusy` + `BusyKind` 枚举
  - `EnvStartStatusViewModel`(`Begin()` / `Fail(string)` / `Complete()` / `Hide()` / `AdvanceTo(string)`)
- Produces:
  - `internal void SetMainViewModel(MainViewModel mvm)` setter(打破循环依赖)
  - `internal async Task RestartEnvInternalAsync(Environment env, CancellationToken ct)` — Stop(if running)+ Start 串行,busy 跳过,失败 catch + log + status.Fail
  - `_mvm` 字段(`MainViewModel?`,nullable,setter 后非 null)
  - `_logger` 字段(`AppLogger?`,nullable)
  - `BusyKind.Restart` 新枚举值
  - `OpenInstallNodePicker(env)` 改:`Views.InstallDialog.Show(_repo, _nodeOps, entry, preselectedEnvId: env.Id, onInstallSuccess: ...)` — 当 `_mvm is not null` 时传 `_mvm.RestartEnvAsync`,否则传 null

**Step 2.1: 写失败测试 — RestartEnvInternalAsync 行为**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelRestartTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelRestartTests
{
    [Fact]
    public async Task RestartEnvInternal_NotBusy_StopsThenStarts()
    {
        var (vm, env, launcher) = CreateVm(envStatus: "running");
        var stopCalled = false;
        launcher.StopAsyncFn = _ => { stopCalled = true; env.Status = "stopped"; return Task.CompletedTask; };
        launcher.StartAsyncFn = (_, _, _, _) => { env.Status = "running"; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.True(stopCalled);
        Assert.Equal("running", env.Status);
    }

    [Fact]
    public async Task RestartEnvInternal_NotRunning_OnlyStarts()
    {
        var (vm, env, launcher) = CreateVm(envStatus: "stopped");
        var stopCalled = false;
        launcher.StopAsyncFn = _ => { stopCalled = true; return Task.CompletedTask; };
        launcher.StartAsyncFn = (_, _, _, _) => { env.Status = "running"; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.False(stopCalled);
        Assert.Equal("running", env.Status);
    }

    [Fact]
    public async Task RestartEnvInternal_EnvBusy_LogsWarn_NoStopNoStart()
    {
        var (vm, env, launcher) = CreateVm(envStatus: "running");
        vm.SetEnvBusyForTest(env);
        var stopCalled = false;
        var startCalled = false;
        launcher.StopAsyncFn = _ => { stopCalled = true; return Task.CompletedTask; };
        launcher.StartAsyncFn = (_, _, _, _) => { startCalled = true; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.False(stopCalled);
        Assert.False(startCalled);
    }

    [Fact]
    public async Task RestartEnvInternal_StartThrows_LogsError_UnmarksBusy()
    {
        var (vm, env, launcher) = CreateVm(envStatus: "stopped");
        launcher.StartAsyncFn = (_, _, _, _) => throw new InvalidOperationException("boom");

        // 不抛 — 异常被吞进 EnvStartStatusViewModel.Fail
        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        // 第二次再调,busy 应已清
        env.Status = "running";
        launcher.StartAsyncFn = (_, _, _, _) => Task.CompletedTask;
        await vm.RestartEnvInternalAsync(env, CancellationToken.None);
    }

    // ---- helpers ----

    private static (EnvironmentListViewModel vm, Environment env, FakeLauncher launcher)
        CreateVm(string envStatus)
    {
        var env = new Environment { Id = "env-1", Name = "env-1", RootPath = "C:/fake/env-1", Status = envStatus };
        var repo = new SingleEnvRepository(env);
        var launcher = new FakeLauncher();
        var vm = new EnvironmentListViewModel(
            repo, launcher,
            new FakeEnvCreatorService(), new FakeBaseEnvInstaller(),
            new Settings(), new FakeProfileLoader(), new FakeEnvDeleter(),
            new FakeNodeOps(), "C:/fake/root", new FakeReqInstaller());
        return (vm, env, launcher);
    }

    private class FakeLauncher : ProcessLauncher
    {
        public Func<Environment, Task> StopAsyncFn { get; set; } = _ => Task.CompletedTask;
        public Func<Environment, IProgress<string>, IProgress<string>, CancellationToken, Task> StartAsyncFn { get; set; }
            = (_, _, _, _) => Task.CompletedTask;
        public FakeLauncher() : base(null!, "", new NullLogger()) { }
        public new Task StopEnvAsync(Environment env) => StopAsyncFn(env);
        public new Task StartEnvAsync(Environment env, IProgress<string> stage, IProgress<string> log, CancellationToken ct)
            => StartAsyncFn(env, stage, log, ct);
    }

    private class SingleEnvRepository : EnvironmentRepository
    {
        private readonly Environment _env;
        public SingleEnvRepository(Environment env) : base(null!) { _env = env; }
        public new List<Environment> ListAll() => new() { _env };
        public new Environment? Get(string id) => id == _env.Id ? _env : null;
    }

    private class NullLogger : AppLogger
    {
        public NullLogger() : base(Path.Combine(Path.GetTempPath(), "null-logger-" + Guid.NewGuid())) { }
    }

    private class FakeEnvCreatorService : EnvCreatorService
    {
        public FakeEnvCreatorService() : base(null!, null!, null!, null!, null!, "", null!, null!) { }
    }
    private class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller() : base(null!, null) { }
    }
    private class FakeProfileLoader : BaseEnvProfileLoader
    {
        public FakeProfileLoader() : base(null!, "", "") { }
    }
    private class FakeEnvDeleter : EnvDeleterService
    {
        public FakeEnvDeleter() : base(null!, null!) { }
    }
    private class FakeNodeOps : NodeOperations
    {
        public FakeNodeOps() : base(null!, null!, null!, null!) { }
    }
    private class FakeReqInstaller : RequirementsInstaller
    {
        public FakeReqInstaller() : base(null!, null!, null!, "", null) { }
    }
}
```

**注意**:实际 `EnvironmentListViewModel` ctor 接受 16 个 positional 参数(详见 spec §G + `ViewModels/EnvironmentListViewModel.cs:170-185`)。**测试构造**用 16 positional + 新增 `AppLogger? logger = null`。Fake 子类需要匹配基类 ctor — 实施期用 grep 验证每个基类 ctor 真实签名。

`ProcessLauncher.StartEnvAsync` 实际签名(实施期 grep)— 可能是 `(env, stageProgress, logProgress, ct)` 或其他。Plan 假设标准 4 参,实际以代码为准。

**Step 2.2: 跑测试确认 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelRestart" -v minimal
```

期望:`error CS0117: 'EnvironmentListViewModel' 不含 'RestartEnvInternalAsync'` — 编译失败。

**Step 2.3: 加 `RestartEnvInternalAsync` + `SetMainViewModel` + logger 字段 + `BusyKind.Restart`**

`src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:

1. **`BusyKind` 枚举加 `Restart`**(行 50):

```csharp
private enum BusyKind { None, BEDInstall, BEDUninstall, ReqInstall, ReqUninstall, Start, Stop, Delete, ComfyUiManagerInstall, ComfyUiManagerUninstall, Restart }
```

2. **加字段**(放在已有字段下面):

```csharp
// v0.6.11+ SDD D1:MainViewModel 反向引用(打破构造期循环依赖),ctor 末尾由
// MainViewModel.SetMainViewModel(this) 注入。null = EnvListVM 早于 MVM 构造(测试),
// 此时 OpenInstallNodePicker 不传回调 → InstallDialog 装成功不触发重启。
private MainViewModel? _mvm;

// v0.6.11+ SDD D1:AppLogger — 自动重启失败 / env-not-found / busy 等诊断日志。
// 跟 BaseEnvInstaller 同 pattern:nullable ctor,生产 DI 在 App.xaml.cs 注入。
private readonly AppLogger? _logger;

/// <summary>
/// v0.6.11+ SDD D1:MainViewModel 注入反向引用。MainViewModel ctor 末尾调一次,
/// 把 _mvm 设上,这样 OpenInstallNodePicker 才能拿 _mvm.RestartEnvAsync 当回调。
/// </summary>
internal void SetMainViewModel(MainViewModel mvm) => _mvm = mvm;
```

3. **ctor 加 `AppLogger? logger = null`**(行 170-185):

```csharp
public EnvironmentListViewModel(
    EnvironmentRepository repo,
    ProcessLauncher launcher,
    EnvCreatorService envCreator,
    BaseEnvInstaller baseEnvInstaller,
    Settings settings,
    BaseEnvProfileLoader profileLoader,
    EnvDeleterService envDeleter,
    NodeOperations nodeOps,
    string projectRoot,
    RequirementsInstaller requirementsInstaller,
    BaseEnvUninstaller? baseEnvUninstaller = null,
    RequirementsUninstaller? requirementsUninstaller = null,
    IBrowserLauncher? browserLauncher = null,
    ErrorBannerViewModel? errorBanner = null,
    ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
    AppLogger? logger = null)
{
    _repo = repo;
    _launcher = launcher;
    _envCreator = envCreator;
    _baseEnvInstaller = baseEnvInstaller;
    _settings = settings;
    _profileLoader = profileLoader;
    _envDeleter = envDeleter;
    _nodeOps = nodeOps;
    _projectRoot = projectRoot;
    _requirementsInstaller = requirementsInstaller;
    _baseEnvUninstaller = baseEnvUninstaller ?? new BaseEnvUninstaller();
    _requirementsUninstaller = requirementsUninstaller ?? new RequirementsUninstaller();
    _browserLauncher = browserLauncher;
    _errorBanner = errorBanner;
    _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(new RequirementsFileInstaller());
    _logger = logger;   // v0.6.11+ SDD D1
    RecentBasePythonPath = null;
    // ... 既有 RelayCommand 构造不变 ...
}
```

4. **加 `RestartEnvInternalAsync` 方法**(放在 StartEnvAsync 之后,行 470 附近):

```csharp
/// <summary>
/// v0.6.11+ SDD D1:给 MainViewModel.RestartEnvAsync 调的内部入口。
/// Stop(若 env.Status == "running")+ Start,复用 per-env 互斥锁 + EnvStartStatusViewModel。
/// 失败 → AppLogger + env-start 面板 Fail(rethrow no,节点保留)。
/// 跳过条件:env 找不到 / env 已在 busy 状态(per-env 互斥锁,v0.6.5.22)。
/// </summary>
internal async Task RestartEnvInternalAsync(Environment env, CancellationToken ct)
{
    if (env is null)
    {
        _logger?.Warn("auto-restart-env", "env 为 null,跳过重启");
        return;
    }
    if (IsEnvBusy(env))
    {
        _logger?.Warn("auto-restart-env-busy",
            $"env {env.Name} 正忙,跳过自动重启");
        return;
    }

    // 跟 StartEnvAsync 一样构造 status panel,复用现有 EnvStartStatusViewModel 显示
    var status = new EnvStartStatusViewModel();
    StartStatus = status;
    RaisePropertyChanged(nameof(StartStatus));
    status.Begin();
    MarkEnvBusy(env, BusyKind.Restart);
    // v0.6.5.11 fix:把 status 包成 Progress<string> 捕获 UI SynchronizationContext,
    // 避免 AttachStdoutReader 后台线程改 LogLines ObservableCollection。
    var stageProgress = new Progress<string>(s => status.Report(s));
    var logProgress = new Progress<string>(line => status.Report(line));

    try
    {
        // 1) Stop if running
        if (string.Equals(env.Status, "running", StringComparison.Ordinal))
        {
            await StopEnvAsync(env);
        }

        // 2) Start
        await _launcher.StartEnvAsync(env, stageProgress, logProgress, default);
        status.Complete();
        await Task.Delay(TimeSpan.FromSeconds(2));
        status.Hide();
    }
    catch (Exception ex)
    {
        status.Fail($"自动重启失败:{ex.Message}");
        _logger?.Error("auto-restart-env-failed",
            $"env {env.Name} 自动重启失败(节点保留):{ex.Message}", ex);
        // 不抛 — InstallDialogViewModel 已经在 background 跑,异常会丢失
        // AppLogger 已记录,env-start 面板显示用户可见错
    }
    finally
    {
        UnmarkEnvBusy(env);
        Load();
        RaiseCommandsChanged();
    }
}
```

**注意**:
- `StopEnvAsync(env)` 已存在(行 472,private)— 直接调
- `StartEnvAsync(env, stageProgress, logProgress, default)` 是 `_launcher`(`ProcessLauncher`)的方法,不是 EnvListVM 自己的。EnvListVM 自己的 `StartEnvAsync(env)`(行 434)是包装,内部已包 Progress。**简化方案**:直接调 EnvListVM 自己的 `StartEnvAsync(env)`(它已有 status panel + Progress 包装)— 比 `_launcher.StartEnvAsync` 更对称。但因为 `RestartEnvInternalAsync` 已经在外面 new 了 status,不能让 `StartEnvAsync` 再 new 一次覆盖。
- **plan deviation accepted**:实际接口实施期 grep 决定。最简单:直接调 `StartEnvAsync(env)`(让它自己管 status)— 但会丢 RestartEnvInternalAsync 的 status。**推荐方案**:调 `_launcher.StartEnvAsync(env, stageProgress, logProgress, default)` 跟 status binding。

5. **`OpenInstallNodePicker` 注入回调**(行 1118-1132):

```csharp
private void OpenInstallNodePicker(Environment? env)
{
    if (env is null) return;

    if (OpenInstallPickerOverride is not null)
    {
        OpenInstallPickerOverride(env);
        return;
    }

    var entry = Views.CatalogEntryPickerDialog.Show();
    if (entry is null) return;

    // v0.6.11+ SDD D1: 注入自动重启回调 — 装成功时 fire-and-forget
    // 触发 MainViewModel.RestartEnvAsync(env.Id),切到 env-list tab + 重启 env。
    // _mvm null = EnvListVM 早于 MVM 构造(测试或极端 wiring)→ 不传回调,
    // InstallDialog 装成功不触发重启,行为跟 v0.6.11 既有兼容。
    Func<string, Task>? onSuccess = _mvm is not null ? _mvm.RestartEnvAsync : null;

    Views.InstallDialog.Show(
        _repo, _nodeOps, entry,
        preselectedEnvId: env.Id,
        onInstallSuccess: onSuccess);
}
```

**Step 2.4: 跑测试 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelRestart" -v minimal
```

期望:4 PASS / 0 FAIL(若 fake 编译失败,fix fakes + 重跑)。

**Step 2.5: 全套回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

期望:786+/1/1(782 + 4 新增)。

**Step 2.6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelRestartTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): EnvListVM RestartEnvInternalAsync + SetMainViewModel + logger (D1)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: MainViewModel RestartEnvAsync + AppLogger 接线

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (加 `AppLogger?` 字段 + `RestartEnvAsync` 方法 + `RestartEnvOverride` seam + ctor 末尾 `SetMainViewModel` 注入)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (把现有 `logger` 传给 MVM ctor)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelRestartEnvTests.cs`

**Interfaces:**
- Consumes:
  - `_environmentsViewModel.Environments`(`ObservableCollection<Environment>`)
  - `EnvironmentListViewModel.RestartEnvInternalAsync(env, ct)` (T2 新建)
  - `ShowEnvironmentsCommand.Execute(null)`(把 CurrentSection 切到 Environments)
  - `AppLogger?` (既有,App.xaml.cs:86 创建)
- Produces:
  - `public async Task RestartEnvAsync(string envId)` —— 找 env → ShowEnvironments → 委托给 `_environmentsViewModel.RestartEnvInternalAsync`
  - `internal Func<string, Task>? RestartEnvOverride` test seam
  - ctor 末尾:`if (_environmentsViewModel is not null) _environmentsViewModel.SetMainViewModel(this);`

**Step 3.1: 写失败测试 — RestartEnvAsync 行为**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelRestartEnvTests.cs`:

```csharp
using System;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainViewModelRestartEnvTests
{
    [Fact]
    public async Task RestartEnvAsync_EnvNotFound_LogsWarn_NoCrash()
    {
        var mvm = CreateMvm(out _, out _, envListVmFactory: _ => new FakeEnvListVm());
        // 不抛,也不调 EnvListVm
        await mvm.RestartEnvAsync("nonexistent");
    }

    [Fact]
    public async Task RestartEnvAsync_EnvFound_InvokesEnvListRestartInternal()
    {
        var env = new Environment { Id = "env-1", Name = "env-1", RootPath = "C:/fake/env-1", Status = "stopped" };
        var fakeEnvList = new FakeEnvListVm(env);
        var mvm = CreateMvm(out _, out _, envListVmFactory: _ => fakeEnvList);

        await mvm.RestartEnvAsync("env-1");

        Assert.Equal(1, fakeEnvList.RestartCount);
        Assert.Same(env, fakeEnvList.LastEnv);
    }

    [Fact]
    public void RestartEnvAsync_NavigatesToEnvironmentListTab()
    {
        var mvm = CreateMvm(out _, out _, envListVmFactory: _ => new FakeEnvListVm());
        // 先切走
        mvm.ShowCatalogCommand.Execute(null);
        Assert.Equal(MainSection.Catalog, mvm.CurrentSection);

        // restart 应切回 Environments
        _ = mvm.RestartEnvAsync("nonexistent");

        Assert.Equal(MainSection.Environments, mvm.CurrentSection);
    }

    [Fact]
    public async Task RestartEnvAsync_RestartEnvOverride_UsedInstead()
    {
        var env = new Environment { Id = "env-1", RootPath = "C:/fake", Status = "stopped" };
        var fakeEnvList = new FakeEnvListVm(env);
        var mvm = CreateMvm(out _, out _, envListVmFactory: _ => fakeEnvList);
        string? capturedEnvId = null;
        mvm.RestartEnvOverride = id => { capturedEnvId = id; return Task.CompletedTask; };

        await mvm.RestartEnvAsync("env-1");

        Assert.Equal("env-1", capturedEnvId);
        Assert.Equal(0, fakeEnvList.RestartCount);
    }

    [Fact]
    public async Task RestartEnvAsync_LogsError_PropagatesNothing()
    {
        var env = new Environment { Id = "env-1", RootPath = "C:/fake", Status = "stopped" };
        var fakeEnvList = new FakeEnvListVm(env) { ThrowOnRestart = new InvalidOperationException("boom") };
        var mvm = CreateMvm(out _, out _, envListVmFactory: _ => fakeEnvList);

        // 不抛 — RestartEnvInternalAsync 内部 catch
        await mvm.RestartEnvAsync("env-1");
    }

    // ---- helpers ----

    private static MainViewModel CreateMvm(
        out EnvironmentRepositoryStub envRepoStub,
        out FakeNodeOps nodeOps,
        Func<EnvironmentRepository, FakeEnvListVm> envListVmFactory)
    {
        // MainViewModel ctor 25+ 参数,走 named args
        envRepoStub = new EnvironmentRepositoryStub();
        nodeOps = new FakeNodeOps();
        var mvm = new MainViewModel(
            dbFactory: new SqliteConnectionFactoryStub(),
            launcher: new FakeLauncher(),
            orchestrator: new FakeOrchestrator(),
            nodeOps: nodeOps,
            envCreator: new FakeEnvCreator(),
            envDeleter: new FakeEnvDeleter(),
            settingsRepo: new FakeSettingsRepo(),
            gitProxy: new FakeGitProxy(),
            settings: new Settings(),
            catalogFetcher: new FakeCatalogFetcher(),
            catalogRefreshService: new FakeCatalogRefreshService(),
            catalogCacheStore: new FakeCatalogCacheStore(),
            baseEnvInstaller: new FakeBaseEnvInstaller(),
            profileLoader: new FakeProfileLoader(),
            pytorchVersionDirectory: new FakePytorchDir(),
            appDataDir: "C:/fake/appdata",
            projectRoot: "C:/fake/root",
            requirementsInstaller: new FakeReqInstaller(),
            systemInfoCollector: new FakeSystemInfoCollector(),
            uiPreferencesService: new FakeUiPrefsService(),
            browserLauncher: new FakeBrowserLauncher(),
            comfyUiManagerInstaller: new FakeComfyUiManagerInstaller(),
            logger: new NullLogger());
        // 模拟 ShowEnvironments 已建过 EnvListVM — 直接赋值
        var fakeEnvList = envListVmFactory(envRepoStub);
        SetEnvListVmForTest(mvm, fakeEnvList);
        return mvm;
    }

    // reflection helper — 内部访问 _environmentsViewModel
    private static void SetEnvListVmForTest(MainViewModel mvm, EnvironmentListViewModel envList)
    {
        var prop = typeof(MainViewModel).GetField("_environmentsViewModel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        prop!.SetValue(mvm, envList);
    }

    private class FakeEnvListVm : EnvironmentListViewModel
    {
        public int RestartCount { get; private set; }
        public Environment? LastEnv { get; private set; }
        public Exception? ThrowOnRestart { get; set; }
        public FakeEnvListVm(params Environment[] envs)
            : base(new SingleEnvRepo(envs), new FakeLauncher(),
                   new FakeEnvCreator(), new FakeBaseEnvInstaller(),
                   new Settings(), new FakeProfileLoader(), new FakeEnvDeleter(),
                   new FakeNodeOps(), "C:/fake/root", new FakeReqInstaller())
        {
            foreach (var e in envs) Environments.Add(e);
        }
        internal new Task RestartEnvInternalAsync(Environment env, CancellationToken ct)
        {
            RestartCount++;
            LastEnv = env;
            if (ThrowOnRestart is not null) throw ThrowOnRestart;
            return Task.CompletedTask;
        }
    }

    // ... (省略各类 Fake stub — 实施期按 MainViewModel ctor 真实签名构造)
    private class SingleEnvRepo : EnvironmentRepository { ... }
    private class FakeLauncher : ProcessLauncher { ... }
    private class FakeEnvCreator : EnvCreatorService { ... }
    private class FakeBaseEnvInstaller : BaseEnvInstaller { ... }
    private class FakeProfileLoader : BaseEnvProfileLoader { ... }
    private class FakeEnvDeleter : EnvDeleterService { ... }
    private class FakeNodeOps : NodeOperations { ... }
    private class FakeReqInstaller : RequirementsInstaller { ... }
    private class FakeOrchestrator : BulkUpdateOrchestrator { ... }
    private class FakeSettingsRepo : SettingsRepository { ... }
    private class FakeGitProxy : GitProxyConfig { ... }
    private class FakeCatalogFetcher : CatalogFetcher { ... }
    private class FakeCatalogRefreshService : CatalogRefreshService { ... }
    private class FakeCatalogCacheStore : CatalogCacheStore { ... }
    private class FakePytorchDir : PyTorchVersionDirectory { ... }
    private class FakeSystemInfoCollector : SystemInfoCollector { ... }
    private class FakeUiPrefsService : UiPreferencesService { ... }
    private class FakeBrowserLauncher : IBrowserLauncher { ... }
    private class FakeComfyUiManagerInstaller : ComfyUIManagerInstaller { ... }
    private class NullLogger : AppLogger { ... }
}
```

**注意**:MainViewModel ctor 22 个 positional 参数(详见 `MainViewModel.cs:170-197`),实际构造按 grep 结果为准。本 plan 给模式,实施期填空。Fake 子类太多时可改用 `MainViewModelEnvironmentViewCachingTests`(已存在)同 pattern 的 helper factory。

**Step 3.2: 跑测试确认 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelRestartEnv" -v minimal
```

期望:`error CS0117: 'MainViewModel' 不含 'RestartEnvAsync'`。

**Step 3.3: 加 `RestartEnvAsync` + `AppLogger?` + `RestartEnvOverride` + ctor 末尾 `SetMainViewModel`**

`src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:

1. **加 `AppLogger?` 字段**:

```csharp
// v0.6.11+ SDD D1:AppLogger — env-not-found 警告。跟 BaseEnvInstaller 同 pattern,
// nullable ctor,生产 DI 在 App.xaml.cs 注入(已有 var logger = new AppLogger(projectRoot);)。
private readonly AppLogger? _logger;
```

2. **ctor 末尾注入(在最后一行 `_comfyUiManagerInstaller = comfyUiManagerInstaller;` 之后)**:

```csharp
        _logger = logger;   // v0.6.11+ SDD D1

        // ...既有 RelayCommand 构造...

        // v0.6.11+ SDD D1:打破 MVM ↔ EnvListVM 构造期循环依赖。ctor 末尾一次性注入,
        // 让 EnvListVM.OpenInstallNodePicker 拿得到 _mvm.RestartEnvAsync 当 InstallDialog 回调。
        // 复用 v0.6.5 既有的 setter 模式(同 AppLogger 字段赋值顺序不冲突)。
        // _environmentsViewModel 此刻仍 null(ShowEnvironments 还没调过)— ShowEnvironments 内
        // 第一次构造 EnvListVM 后,需要补一行 SetMainViewModel 调用。如下面 ShowEnvironments 改。
    }
```

3. **`ShowEnvironments` 内构造 EnvListVM 后补 SetMainViewModel**(行 287-298):

```csharp
    private void ShowEnvironments()
    {
        CurrentSection = MainSection.Environments;
        if (_environmentsViewModel is null)
        {
            var envRepo = new EnvironmentRepository(_dbFactory);
            _environmentsViewModel = new EnvironmentListViewModel(
                envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader,
                _envDeleter, _nodeOps, _projectRoot, _requirementsInstaller,
                _baseEnvUninstaller, _requirementsUninstaller,
                _browserLauncher, ErrorBanner, _comfyUiManagerInstaller,
                logger: _logger);   // v0.6.11+ SDD D1
            // v0.6.11+ SDD D1:注入反向引用,让 EnvListVM.OpenInstallNodePicker 能拿 _mvm.RestartEnvAsync
            _environmentsViewModel.SetMainViewModel(this);
            _environmentsView = EnvironmentsViewFactory is null
                ? new EnvironmentListView { DataContext = _environmentsViewModel }
                : EnvironmentsViewFactory(_environmentsViewModel) as EnvironmentListView;
        }
        CurrentView = _environmentsView;
    }
```

4. **加 `RestartEnvAsync` + `RestartEnvOverride`**(放在类末尾,test seam 块附近):

```csharp
    /// <summary>
    /// v0.6.11+ SDD D1:节点安装完成后自动重启目标 env。
    /// 切到 env-list tab → 委托给 EnvListVM.RestartEnvInternalAsync(Stop if running + Start),
    /// 通过 EnvStartStatusViewModel 面板反馈进度。失败 → AppLogger + env-start 面板显示,节点保留。
    /// 跳过条件:env 找不到 / EnvListVM 未构造(_environmentsViewModel is null)/ env 已在 busy 状态。
    /// </summary>
    public async Task RestartEnvAsync(string envId)
    {
        if (RestartEnvOverride is not null)
        {
            await RestartEnvOverride(envId);
            return;
        }

        // 先切到 env-list —— 用户立刻看到进度面板(MVM 端 CurrentSection + 触发 ShowEnvironments
        // 让 EnvListVM 构造)。如果 EnvListVM 已存在,直接复用。
        ShowEnvironmentsCommand.Execute(null);

        var envListVm = _environmentsViewModel;
        if (envListVm is null)
        {
            _logger?.Warn("auto-restart-env",
                $"EnvListVM 未构造,跳过重启 env {envId}");
            return;
        }

        var env = envListVm.Environments.FirstOrDefault(e => e.Id == envId);
        if (env is null)
        {
            _logger?.Warn("auto-restart-env",
                $"env {envId} 不存在,跳过重启");
            return;
        }

        // EnvListVM 内部 per-env 互斥锁 + EnvStartStatusViewModel 反馈 + 失败 catch
        await envListVm.RestartEnvInternalAsync(env, CancellationToken.None);
    }

    /// <summary>
    /// v0.6.11+ SDD D1 test seam:替代默认的 envListVm.RestartEnvInternalAsync 调用。
    /// 单元测试可注入只记录不真跑 stop+start 的函数,避免 STA / 进程启动副作用。
    /// null = 走默认路径(envListVm.RestartEnvInternalAsync)。
    /// </summary>
    internal Func<string, Task>? RestartEnvOverride { get; set; }
```

5. **`App.xaml.cs` 把 logger 传给 MVM**(行 233):

```csharp
        _mainVm = new MainViewModel(
            ...
            comfyUiManagerInstaller: comfyUiManagerInstaller,
            logger: logger);  // v0.6.11+ SDD D1
```

**Step 3.4: 跑测试 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelRestartEnv" -v minimal
```

期望:5 PASS / 0 FAIL。

**Step 3.5: 全套回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

期望:791+/1/1(786 + 5 新增)。

**Step 3.6: Build**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

期望:0 错误 / 0 警告。

**Step 3.7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelRestartEnvTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): MainViewModel.RestartEnvAsync + logger plumbing (D1)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Final review + MEMORY + staging rebuild + GUI smoke

**Files:**
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_auto_restart_env.md`
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (加 1 行 index)
- Stage: `release/staging/ComfyUI Manager/` rebuild via `dotnet publish`

**Step 4.1: Final whole-branch review dispatch**

用 `superpowers:requesting-code-review` 派 opus 整体 review `c509534..HEAD`:
- spec compliance(覆盖 G1-G26)
- code quality(YAGNI / DRY / 测试 seam)
- 集成风险(MVM ↔ EnvListVM 接线 / Test seam / Fake 编译)

若有 Critical/Important findings,fix round;若 clean,继续。

**Step 4.2: 全套测试 + build**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

期望:0/0 build,791+/1/1 tests(2 pre-existing flaky 不算回归)。

**Step 4.3: MEMORY 项目记忆**

新建 `memory/project_v0_6_11_plus_auto_restart_env.md`:

```markdown
---
name: v0.6.11+ 自动重启 env D1 SDD
description: Node 安装完成后自动停止(若运行)+ 启动目标 env,EnvStartStatusViewModel 面板显示进度
type: project
---

# v0.6.11+ SDD D1:Auto-restart env after node install — SHIP

## Status

✓ **SHIP-READY**,HEAD `<commit-hash>`(base `c509534` + 3 commits),791+/1/1 tests。

## Scope

`NodeOperations.InstallAsync` 成功路径 → 切到 env-list tab → 委托给
`EnvListVM.RestartEnvInternalAsync` → Stop(若 `env.Status == "running"`)+ Start →
`EnvStartStatusViewModel` 显示进度 → 失败 → AppLogger + 面板,节点保留。

## Architecture

- Direct chain in trigger VM(`InstallDialog.OnInstallSuccess` 回调 → `_mvm.RestartEnvAsync`)
- `SetMainViewModel` setter 打破 MVM ↔ EnvListVM 构造期循环依赖(v0.6.5 既 pattern)
- `Task.Run` fire-and-forget:InstallDialog 立刻关,dialog UI thread 不在 await 阻塞
- 复用 v0.6.5.22 per-env mutex(`IsEnvBusy` / `MarkEnvBusy` / `UnmarkEnvBusy`) + `BusyKind.Restart`
- AppLogger wiring:`MainViewModel` ctor 加 `AppLogger? logger = null`,传给 EnvListVM

## Locked decisions

- 触发源唯一 = `NodeOperations.InstallAsync` 成功路径
- 目标 env 唯一 = 触发时传入的 env(不广播)
- 重启动作 = Stop if running + Start
- 失败 = 节点保留,错误显示在 env-start 面板
- UI = 切到 env-list tab
- 架构 = direct chain,无 event bus / orchestrator

## Files

- `ViewModels/InstallDialogViewModel.cs` — `OnInstallSuccess` ctor 参 + 装成功 fire callback
- `Views/InstallDialog.xaml.cs` — `Show(...)` 加 `onInstallSuccess` 参数
- `ViewModels/EnvironmentListViewModel.cs` — `RestartEnvInternalAsync` + `SetMainViewModel` + `_logger` + `BusyKind.Restart` + `OpenInstallNodePicker` 注入回调
- `ViewModels/MainViewModel.cs` — `RestartEnvAsync` + `RestartEnvOverride` seam + `_logger` + `ShowEnvironments` 末尾 SetMainViewModel
- `App.xaml.cs` — MVM ctor 加 logger
- 3 测试文件(12 测试全 PASS)

## Carry-forward

- 不做 Settings toggle「自动重启」(默认开,无可配置)
- 不做「重启所有装了同包的 env」(单一目标 env)
- 不做 toast / 系统通知(走 env-start panel)
- 不做 `RequirementsInstaller` / `BaseEnvInstaller` / `BulkUpdateOrchestrator` 的自动重启

## 用户原话

"完成以上内容追加如下节点功能：节点安装安装完成后自动重启安装ComfyUI节点的环境"
```

**Step 4.4: 更新 MEMORY.md index**

在 `<details>` 块附近加一行:
```markdown
- [v0.6.11+ 自动重启 env D1 SDD](project_v0_6_11_plus_auto_restart_env.md) — SHIP 3 commits;InstallDialog OnInstallSuccess + EnvListVM.RestartEnvInternalAsync + MainViewModel.RestartEnvAsync + Task.Run fire-forget + SetMainViewModel 打破循环依赖 + 复用 per-env mutex + 12 测试全 PASS
```

**Step 4.5: Staging rebuild**

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

期望:0 errors,产物 `<projectRoot>/release/staging/ComfyUI Manager/ComfyUI Manager.exe`。

**Step 4.6: GUI smoke(用户桌面验证)**

1. 启动 staging → env-list 工具栏 → 创建 env-A(若没)
2. env-A 启动 ComfyUI
3. env-A 操作列 → "安装节点" → 选 catalog entry → dialog 关 → 自动切到 env-list tab
4. env-start 面板显示 stop+start 进度(同手动点 stop+start)
5. 进度结束后 env-A 正常运行,新装节点已加载
6. 反向:env-A 停止 → 装节点 → 不应 stop(已停止),只 start
7. 失败路径:启动 env-A → 装节点 → 中途 env-A 启动失败 → 节点仍在 env-A,env-start 面板显示错误
8. 互斥路径:env-A 正装 requirements 时再装 node → 自动重启 skip + AppLogger warn

**Step 4.7: Merge to main**

```bash
git checkout main
git merge --no-ff auto-restart-env -m "Merge SDD D1: auto-restart env after node install"
```

如用户要 push / PR,按 `superpowers:finishing-a-development-branch` 走标准流程。

**Step 4.8: Commit MEMORY + ledger**

```bash
git add memory/project_v0_6_11_plus_auto_restart_env.md memory/MEMORY.md
git commit -m "docs(memory): SDD D1 auto-restart-env ship notes"
```

---

## Verification (end-to-end)

按顺序 4 task commit 全 PASS:

```bash
# T1 验证
git log --oneline -3
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~InstallDialogViewModelRestart" -v minimal  # 3 PASS

# T2 验证
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelRestart" -v minimal  # 4 PASS

# T3 验证
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelRestartEnv" -v minimal  # 5 PASS

# T4 验证 (合并后)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 791+/1/1
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke**(桌面验证,user):
1. 启动 staging → env-list → 创建 env-A
2. env-A 启动 ComfyUI
3. env-A 行 → "安装节点" → 选 entry → dialog 关 → 自动切回 env-list tab
4. env-start 面板显示 stop+start 进度
5. 完成后 env-A 正常运行,新装节点已加载
6. 失败:启动失败 → 节点保留,env-start 面板显示错
7. 互斥:env 正忙 → 自动重启 skip + AppLogger warn

---

## Risks

| 风险 | 缓解 |
|---|---|
| `EnvironmentListViewModel` ctor 加 `AppLogger?` 参数破坏现有测试 fixture | 14+ 测试 ctor 调用,加 = null 默认值;新加 `_logger` field 默认 null,测试可不传 |
| `MainViewModel` ctor 加 `AppLogger?` 参数破坏现有 fixture | 25+ positional arg,加 `logger: null` 默认值;`App.xaml.cs` 生产注入 |
| Fake 子类(`FakeNodeOperations` / `FakeLauncher` 等)基类 ctor 复杂 | 实施期 grep;若不可行,改用 internal 测试 seam(测试项目已在 `InternalsVisibleTo` 内) |
| `ProcessLauncher.StartEnvAsync` 真实签名 4 参不一定对 | 实施期 grep 验证;若不同,plan deviation 调 |
| `MainViewModel` 22 参数 fixture 难构造 | 复用既有 `MainViewModelEnvironmentViewCachingTests` 的 named-arg helper factory |
| `Task.Run` 把回调挪后台线程,dispatcher 抛异常 | InstallDialog 关闭后才 fire,UI dispatcher 不活跃 → Task.Run 避开 |
| 自动重启与 `StopEnvAsync` / `StartEnvAsync` 现有 per-env 互斥锁冲突 | 复用 `IsEnvBusy` 检查,busy 时 skip + log |
| `RestartEnvOverride` 在测试外被人 set | internal 属性,生产代码无 setter 调用 |
| 用户在 Catalog tab 装节点 → 重启期间切到别 tab → env-start 进度看不到 | 用户主动选择,接受 |
| `_mvm` setter 在 ctor 末尾调用,但 `_environmentsViewModel` 此刻 null | `ShowEnvironments` 内构造 EnvListVM 后立即 `SetMainViewModel(this)` |

---

## Execution Choice

**Subagent-Driven Development**(沿用项目惯例):
- 3 实施 task × (implementer + reviewer) ≈ 6 dispatch
- T1/T2/T3 各 1 implementer + 1 reviewer
- T4 final review (opus) + MEMORY + staging + merge
- 4 commit on main