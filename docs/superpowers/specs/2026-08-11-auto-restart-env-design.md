---
date: 2026-08-11
topic: Node 安装完成后自动重启安装 ComfyUI 节点的环境
base_sha: c509534
spec_status: SHIP-READY
plan_status: PENDING
---

# Node 安装完成后自动重启环境 — 设计

## Scope

`NodeOperations.InstallAsync` 成功完成后,自动停止(若运行中)+ 启动**目标 env**,并自动切换到 env-list tab,让用户通过现有的 `EnvStartStatusViewModel` 面板看到 Stop+Start 进度。

## 锁定决策(用户已选)

- **触发源**: 仅 `NodeOperations.InstallAsync` 成功路径(env 内装节点)
- **目标 env**: 仅触发时传入的 env(不广播到所有装了同包的 env)
- **重启动作**: 若 env 当前运行 → Stop + Start;若已停止 → 只 Start
- **失败处理**: 重启失败时,**节点保留安装**,错误显示在 `EnvStartStatusViewModel` 面板(node 装一半回滚风险太高)
- **UI 切换**: 重启触发时自动切到 env-list tab,让用户看到 stop+start 进度
- **架构**: Direct chain in trigger VM —— 每个 install 触发点显式调用 `mvm.RestartEnvAsync(envId)`,不引入 event bus / orchestrator

## 架构

### §1 触发点(单一)

`NodeOperations.InstallAsync` 在整个 `src-wpf/` 内**只有 1 个生产调用点**:
`src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs:105`

`InstallDialog` 是 env-list 操作列「安装节点」按钮 → `CatalogEntryPickerDialog` → `InstallDialog` 的目标;也是 Catalog tab 装节点(若有入口)的 dialog 入口。

所以**自动重启**只需在 `InstallDialogViewModel` 一处挂回调,不必扫全 codebase。

### §2 `InstallDialogViewModel` 加回调

```csharp
public class InstallDialogViewModel : ViewModelBase
{
    // ...既有字段...

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
        // ...既有赋值...
        OnInstallSuccess = onInstallSuccess;
    }

    private async System.Threading.Tasks.Task InstallAsync()
    {
        // ...既有 try/catch...
        var result = await _ops.InstallAsync(...);
        if (result.Success)
        {
            Progress = $"OK, version={result.Version}";
            // v0.6.11+ SDD D1: 触发自动重启回调(不 await — dialog 立刻关,
            // 真正的 stop+start 在 background 跑,env-start panel 在 env-list tab 显示)。
            // 回调内部吞错 → AppLogger.Error + env-start 面板显示。
            if (OnInstallSuccess is not null)
            {
                _ = System.Threading.Tasks.Task.Run(
                    async () => await OnInstallSuccess(envId));
            }
            CloseRequested?.Invoke();
        }
        // ...失败分支不变...
    }
}
```

**关键决策**:
- 回调**不 await** —— dialog 立刻关,用户不用在 dialog 内等 10+ 秒的 stop+start
- 用 `Task.Run` 把回调挪到线程池线程,避免 dialog UI thread 在 `await` 时阻塞(dialog 关闭会触发 dispatcher 抛异常)
- 失败路径(`result.Success == false`)和异常路径(`catch`)不触发回调 —— 只在**真正装成功**时重启

### §3 `InstallDialog.Show` 透传回调

```csharp
// src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs
public static void Show(
    EnvironmentRepository envRepo,
    NodeOperations nodeOps,
    CatalogEntry entry,
    string? preselectedEnvId = null,
    string? preselectedTag = null,
    Func<string, Task>? onInstallSuccess = null)
{
    var vm = new InstallDialogViewModel(
        envRepo, nodeOps, entry,
        preselectedEnvId, preselectedTag,
        onInstallSuccess);
    var dlg = new InstallDialog(vm) { Owner = Application.Current.MainWindow };
    dlg.ShowDialog();
}
```

向后兼容 —— caller 不传 = 行为不变(无自动重启)。

### §4 `EnvironmentListViewModel.OpenInstallNodePicker` 注入回调

```csharp
// src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:1131
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

    // v0.6.11+ SDD D1: 注入自动重启回调 —— MainViewModel 缓存引用通过 DI/字段注入。
    // 详 InjectMainViewModel: EnvironmentListViewModel ctor 新增
    // `MainViewModel? mvm = null`(默认 null 让测试不传)。
    Views.InstallDialog.Show(
        _repo, _nodeOps, entry,
        preselectedEnvId: env.Id,
        onInstallSuccess: env.Id is { } eid && _mvm is not null
            ? _mvm.RestartEnvAsync
            : null);
}
```

**新依赖**:`EnvironmentListViewModel` ctor 加 `MainViewModel? mvm = null`(nullable,测试不传)。

**`MainViewModel` ↔ `EnvironmentListViewModel` 现存关系**:
- 当前 `MainViewModel` ctor 已经注入 `EnvironmentListViewModel`(_envListVm)
- 反向引用(`EnvironmentListViewModel` 持有 `MainViewModel`)会引起循环依赖 → **用 setter / lazy property 注入**:
  ```csharp
  // EnvironmentListViewModel.cs
  private MainViewModel? _mvm;
  /// <summary>由 MainViewModel ctor 内赋值,打破构造期循环依赖。</summary>
  internal void SetMainViewModel(MainViewModel mvm) => _mvm = mvm;
  ```
  - `MainViewModel` ctor 末尾:`_envListVm.SetMainViewModel(this);`
  - 这是 v0.6.5 既有 pattern 的复用 —— 一些 vm 用 setter / 事件反向通信

### §5 `MainViewModel.RestartEnvAsync` (新方法)

```csharp
// src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs

/// <summary>
/// v0.6.11+ SDD D1: 节点安装完成后自动重启目标 env。
/// 切到 env-list tab → Stop (if running) → Start,通过 EnvStartStatusViewModel
/// 面板反馈进度。失败 → AppLogger + env-start 面板显示,节点保留。
/// 跳过条件:env 找不到 / env 已在 busy 状态(per-env 互斥锁,v0.6.5.22)。
/// </summary>
public async Task RestartEnvAsync(string envId)
{
    var env = _envListVm.Envs.FirstOrDefault(e => e.Id == envId);
    if (env is null)
    {
        _logger?.Warn("auto-restart-env", $"env {envId} 不存在,跳过重启");
        return;
    }

    // 先切到 env-list —— 用户立刻看到进度面板
    ShowEnvironmentListCommand.Execute(null);

    // 把 Stop+Start 串到 env-list vm 的现有入口
    // —— 它内部用 per-env 互斥锁 + EnvStartStatusViewModel 面板
    await _envListVm.RestartEnvInternalAsync(env, CancellationToken.None);
}
```

**测试 seam**:`internal Func<string, Task>? RestartEnvOverride` 允许 unit test 不真跑 stop+start。

### §6 `EnvironmentListViewModel.RestartEnvInternalAsync` (新内部方法)

```csharp
// src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs

/// <summary>
/// v0.6.11+ SDD D1: 给 MainViewModel.RestartEnvAsync 调的内部入口。
/// Stop(若 running)+ Start,复用 per-env 互斥锁 + EnvStartStatusViewModel。
/// </summary>
internal async Task RestartEnvInternalAsync(Environment env, CancellationToken ct)
{
    if (IsEnvBusy(env))
    {
        _logger?.Warn("auto-restart-env-busy",
            $"env {env.Name} 正忙,跳过自动重启");
        return;
    }

    try
    {
        MarkEnvBusy(env, BusyKind.Restart);

        // 1) Stop if running
        if (env.IsRunning)  // 字段名以实际为准 — grep 验证
        {
            await StopAsync(env, ct);
        }

        // 2) Start
        await StartAsync(env, ct);
    }
    catch (Exception ex)
    {
        _logger?.Error("auto-restart-env-failed",
            $"env {env.Name} 自动重启失败(节点保留):{ex.Message}");
        // 不抛 —— InstallDialogViewModel 已经在 background 跑,异常会丢失
        // AppLogger 已记录,env-start 面板(由 StartAsync 自己管理)显示用户可见错
    }
    finally
    {
        UnmarkEnvBusy(env, BusyKind.Restart);
    }
}
```

**前置 grep 验证**(实施期做,不写死):
- `Environment` 模型上的「running」字段名(`IsRunning` / `Running` / `Status` ?)
- `StartAsync(env, ct)` / `StopAsync(env, ct)` 实际签名(可能跟 `OpenInstallNodePicker` 调的不同)
- `BusyKind` 现有枚举值(新增 `Restart` 还是复用 `Install`)

### §7 per-env 互斥锁(v0.6.5.22 既有)

`EnvironmentListViewModel` 已有:
- `Dictionary<RootPath, BusyKind> _busy` 跟踪 busy 状态
- `IsEnvBusy(env)` / `MarkEnvBusy(env, kind)` / `UnmarkEnvBusy(env, kind)`
- 5 个 CanExecute gate 全 `!IsEnvBusy(env)`

`RestartEnvInternalAsync` 复用这套 —— 不引入新互斥机制。

## Tests

### 单元测试

`tests-wpf/.../ViewModels/MainViewModelRestartEnvTests.cs`(新建)—— ~5 测试:
- `RestartEnvAsync_EnvNotFound_LogsWarn_NoCrash`
- `RestartEnvAsync_EnvFound_InvokesEnvListRestartInternal` (mock env list vm, verify 1 调用 + envId 正确)
- `RestartEnvAsync_NavigatesToEnvironmentListTab` (mock ShowEnvironmentListCommand, verify 调用)
- `RestartEnvAsync_RestartEnvOverride_UsedInstead` (test seam 路径)
- `RestartEnvAsync_LogsError_PropagatesNothing` (env-list restart 抛异常,MVM 不 rethrow)

`tests-wpf/.../ViewModels/InstallDialogViewModelRestartTests.cs`(新建)—— ~3 测试:
- `Install_Success_FiresOnInstallSuccess_WithEnvId` (用 mock `NodeOperations` 返 Success=true)
- `Install_Failure_DoesNotFireOnInstallSuccess` (Success=false)
- `Install_Exception_DoesNotFireOnInstallSuccess` (NodeOperations 抛异常)

`tests-wpf/.../ViewModels/EnvironmentListViewModelRestartTests.cs`(新建)—— ~4 测试:
- `RestartEnvInternal_NotBusy_StopsThenStarts`
- `RestartEnvInternal_NotRunning_OnlyStarts`
- `RestartEnvInternal_EnvBusy_LogsWarn_NoStopNoStart`
- `RestartEnvInternal_StartThrows_LogsError_UnmarksBusy` (finally 路径)

### STA load test

不需要 —— `InstallDialogViewModel` 是 VM,无新 XAML;env-start panel XAML 已有 STA 路径。

## 改动文件汇总

**源码**
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — 加 `RestartEnvAsync` + `RestartEnvOverride` seam
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` — 加 `RestartEnvInternalAsync` + `SetMainViewModel` setter + `_mvm` 字段
- `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` — 加 `OnInstallSuccess` ctor 参 + `Task.Run` 触发
- `src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml.cs` — `Show(...)` 加 `onInstallSuccess` 参
- `src-wpf/ComfyUI.Manager/MainViewModel.cs`(已有) — ctor 末尾 `_envListVm.SetMainViewModel(this);`

**测试**
- `tests-wpf/.../ViewModels/MainViewModelRestartEnvTests.cs` — 新建
- `tests-wpf/.../ViewModels/InstallDialogViewModelRestartTests.cs` — 新建
- `tests-wpf/.../ViewModels/EnvironmentListViewModelRestartTests.cs` — 新建

**不动**
- `Services/NodeOperations.cs` —— InstallAsync 签名不变
- `Models/Environment.cs` —— 不加字段(env 的 running 状态走现有字段)
- `ViewModels/EnvStartStatusViewModel.cs` —— 现有 start/stop 反馈机制已够用
- `Views/SettingsView.xaml` —— 不加 Settings toggle(YAGNI)

## YAGNI 划线

- 不做 Settings toggle「自动重启」(默认开,无可配置)
- 不做「重启所有装了同包的 env」(单一目标 env)
- 不做 toast / 系统通知(走 env-start panel)
- 不做 `RequirementsInstaller` / `BaseEnvInstaller` / `BulkUpdateOrchestrator` 的自动重启
- 不做 rollback node install(失败就失败,用户手动处理)
- 不做 concurrent install 协调(per-env mutex 已够)
- 不做 Dialog 内 await restart(用户立刻进 env-list tab,不要在 install dialog 等)

## 非目标

- 不改 `NodeOperations.InstallAsync` 签名
- 不改 `EnvironmentListViewModel.StartAsync` / `StopAsync` 现有签名 / 行为
- 不改 `EnvStartStatusViewModel` —— 现有 start/stop 反馈机制已覆盖

## 风险

| 风险 | 缓解 |
|------|------|
| `Environment` running 字段名猜错 | 实施前 grep `env.IsRunning` / `env.Running` / `env.Status` 找准字段名 |
| `MainViewModel` ↔ `EnvironmentListViewModel` 循环依赖 | 用 `SetMainViewModel` setter + ctor 末尾赋值,跟 v0.6.5 既有 pattern 一致 |
| `Task.Run` 把回调挪后台线程, dispatcher 抛异常 | InstallDialogViewModel 在 dialog 关闭后回调,UI dispatcher 已 shutdown —— Task.Run 避开 |
| 自动重启与 `StopAsync`/`StartAsync` 现有 per-env 互斥锁冲突 | 复用 `IsEnvBusy` 检查,busy 时 skip + log |
| 用户在 Catalog tab 装节点 → 重启期间切到别 tab → env-start 进度看不到 | 这是用户主动选择,接受(用户也可以切回 env-list) |
| `Func<string, Task>? OnInstallSuccess` 在 `InstallDialogViewModel` 测试里没设 | 默认 null = 不触发,行为兼容现有 |
| `InstallDialogViewModel.InstallCommand` 已 `CanExecute = !Busy` —— 重启期间 `Busy=false`,用户能再点 Install 触发第二次重启 | per-env mutex + `_envListVm.IsEnvBusy(env)` 检查在 `RestartEnvInternalAsync` 顶部拦,busy 时 skip |

## 验证

```bash
# 1. 单元测试
dotnet test tests-wpf/ComfyUI.Manager.Tests/ \
  --filter "FullyQualifiedName~MainViewModelRestartEnv|FullyQualifiedName~InstallDialogViewModelRestart|FullyQualifiedName~EnvironmentListViewModelRestart" \
  -v minimal
# 期望: 12 PASS / 0 FAIL

# 2. 全套(基线 ~875 / 2 / 1 + 12 新 = ~887 / 2 / 1)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build

# 3. Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
# 期望: 0/0

# 4. GUI smoke (桌面验证)
# 1) 启动 staging → 创建 env-A(若还没有)→ env-A 启动 ComfyUI
# 2) Catalog tab → 选个 node → 装到 env-A → dialog 关 → 自动切到 env-list tab
# 3) env-start 面板显示 stop+start 进度(同手动点 stop+start)
# 4) 进度结束后 env-A 正常运行,新装节点已加载
# 5) 反向:env-A 停止 → 装节点 → 不应 stop(已停止),只 start
# 6) 失败路径:启动 env-A → 装节点 → 中途 env-A 启动失败 → 节点仍在 env-A,env-start 面板显示错误
# 7) 互斥路径:env-A 正装 requirements 时再装 node → 自动重启 skip + AppLogger warn
```

## Carry-forward

- 未来如需「所有装了同包的 env 都重启」或「配置化开关」,独立 SDD 重新设计
- 未来如需 `RequirementsInstaller` / `BulkUpdateOrchestrator` 也触发自动重启,独立 SDD
- 未来如需 rollback node install 配合自动重启失败,独立 SDD(目前 YAGNI)

## 用户原话

> "完成以上内容追加如下节点功能：节点安装安装完成后自动重启安装ComfyUI节点的环境"
