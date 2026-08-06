# v0.6.5.22 Plan: 卸载按钮 + 互斥 + torch 2.4+ 默认

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** env-list 给每个 env 加"卸载基础环境 + 卸载依赖"两个按钮(同 env 互斥),并把 BED 默认 torch 版本升到 2.4+(消除 `comfy_kitchen` 用 `@torch.library.custom_op` 触发的 torch 2.1 不兼容启动异常)。

**Architecture:**
- 新 `Services/BaseEnvUninstaller.cs`(轻量):清 `env.BedStatus=null` / `BedProfileId=null` / `BedFailedReason=null` → env 可重新 BED(venv 文件不动)
- 新 `Services/RequirementsUninstaller.cs`:跑 `pip uninstall -y -r .requirements_filtered.txt` → 删 marker `.requirements_installed` + filtered(需先备份,因为新 install 会重新生成)
- 新 `ViewModels/BaseEnvUninstallStatusViewModel.cs`(参考 v0.6.5.15 `RequirementsStatusViewModel` 单阶段模式):IsVisible + LogLines + Error
- 改 `ViewModels/EnvironmentListViewModel.cs`:加 2 个 `RelayCommand`(UninstallBaseEnvCommand / UninstallRequirementsCommand)+ `_envBusy: Dictionary<string, BusyKind>` 互斥(StartCommand / InstallBaseEnvCommand / InstallRequirementsCommand / UninstallBaseEnvCommand / UninstallRequirementsCommand / DeleteCommand / OpenBaseEnvProgressCommand 同 env 互斥);`CanExecute` 互斥使用同一 helper `IsEnvBusy(env)`
- 改 `Views/EnvironmentListView.xaml`:操作列 5th-7th 按钮 → 7th-8th 按钮(加 2 个),Grid.Row 1 加 BaseEnvUninstallStatus Border(Requirements 卸载复用 v0.6.5.15 加的 RequirementsStatus Border)
- 改 `Services/PyTorchVersionCatalog.cs`:`BuildLiveDefaults` + `BuildFallback` 第一项 stable 升到 `torch==2.4.x+cu118`(`@torch.library.custom_op` 在 torch 2.4 引入);catalog dropdown 还显示 torch 2.1(向后兼容只标灰),但**默认不选中**;`MarkIncompatibleOlderVersions` 新方法标 < 2.4 不推荐
- 改 `Resources/Strings.resx` + `Strings.zh-CN.resx`:+4 keys

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · `System.Diagnostics.Process` · hand-rolled MVVM(`RelayCommand`)· v0.6.5.15 inline status panel pattern · v0.6.5.19 test seam pattern(`internal Action?`)

## Context

v0.6.5.21 staging 验完 OK。用户在跑 v0.6.5.21 staging 时遇到 2 个新问题:

1. **重新安装需求**:用户想测试不同 torch 版本或不同依赖,但 env 已 BED done / Req done,没"卸载"入口 → 用户桌面反馈原话 "**前面的基本OK了,现在的新的问题在于我们如果需要重新安装,就需要有卸载基础环境,卸载依赖按钮**"
2. **启动异常**(互斥 trigger):user 在 v0.6.5.21 staging 点 "启动" → ComfyUI 子进程 30 秒后挂,Python traceback 指 `@torch.library.custom_op` 是 comfy_kitchen 内部用的,要求 torch >= 2.4,但用户 env 是 BED 默认 `torch==2.1.0+cu118` 装的 → root cause 是 BED 默认 torch 版本太老

用户决策(AskUserQuestion 答复记录):
- **BED 卸载 scope** = 轻量(只删 marker + 重置状态,不删 venv 文件)
- **Req 卸载 scope** = 只清 requirements 装的包(跑 pip uninstall -y -r filtered)
- **互斥 granularity** = 按 env(同 env 互斥,跨 env 独立)

**base SHA:** `a5267e4`(v0.6.5.21 SHIP + 0.6.5.21 hotfix 2 commits 完成)

**相关已有代码:**
- `ViewModels/EnvironmentListViewModel.cs:1-50` — 既有 commands + ctor;缺 per-env 互斥,缺 Uninstall*Command
- `Services/BaseEnvInstaller.cs:1-80` — `BedStatus` SQLite 字段操作(opaque 给 VM,VM 直接 set env.BedStatus);`ReconcileStaleOnStartup` 翻 stale installing → failed
- `Services/RequirementsInstaller.cs:75-173` — `InstallAsync` 已实现 `pip install -r filtered` + 写 marker;卸载需反向:读 filtered(若存在)/ 否则 list installed;跑 `pip uninstall -y -r filtered`;删 marker + filtered
- `ViewModels/RequirementsStatusViewModel.cs`(v0.6.5.15) — 单阶段 inline 模式,IsBusy/LogLines/Error/CloseCommand
- `ViewModels/EnvironmentListViewModel.cs:StartAsync` / `InstallRequirementsAsync` / `OpenBaseEnvProgress` — 都是 `_launcher.StartEnvAsync(...)` / `_requirements.InstallAsync(...)` / `_baseEnv.OpenProgress(env, ...)` 派发,**没有** per-env mutex,目前彼此独立(可能并发跑会冲突)
- `Services/PyTorchVersionCatalog.cs:BuildLiveDefaults` — `BuildFallback` 当前选第一 stable(`pt_version_map.release[0]`,通常 pytorch.org stable 最新,如 `torch==2.9.0+cu126`);问题:不是最新而是默认,需要 hard-pin 一个已知 ≥ 2.4 版本,避免 pytorch.org 改 stable 顺序时不慎退回 2.1

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `BaseEnvUninstaller.Uninstall(env)` 必须:不删 venv 文件(用户重装 BED 用同一个 venv);若 `IsInstalled(env)==false` 立即返回 `AlreadyUninstalled=true`(短路,不动 sqlite) | 用户: "轻量(只删 marker + 重置状态)" |
| G2 | `RequirementsUninstaller.Uninstall(env)` 必须:跑 `pip uninstall -y -r <reconstructed-filtered>`;成功后删 `.requirements_installed`;若 `IsInstalled(env)==false` 立即 `AlreadyUninstalled=true` | 用户原话 + v0.6.5.19 IsInstalled short-circuit pattern |
| G3 | 互斥按 env:同 env 上一个操作跑时,其他 7 个操作(Start / Stop / InstallBaseEnv / InstallRequirements / UninstallBaseEnv / UninstallRequirements / Delete / OpenBaseEnvProgress)的 `CanExecute` 都返 false;跨 env 独立 | 用户: "按 env" |
| G4 | 卸载流程用 inline status panel(沿用 v0.6.5.15 RequirementsStatusViewModel 单阶段模式),不再开 dialog | v0.6.5.15 hotfix 改 dialog → inline |
| G5 | 默认 torch profile 由 `BuildLiveDefaults` 第一项改为 hard-pinned `torch==2.4.1+cu118`(向后兼容 + 已知兼容 comfy_kitchen),dropdown 还显示 pytorch.org 所有 stable + nightly + cpu(letter→tag 跟 v0.6.5.18 一致 7 个 stable + 1 nightly + 1 cpu),但 2.1.x / 2.2.x / 2.3.x 加 `(不推荐 — comfy_kitchen 不兼容)` 后缀 | root cause fix |
| G6 | `BaseEnvUninstaller.Uninstall` 跑之前必须先确认 `env` 没有正在启动的子进程(检查 `env.Status != "running"`),否则返回 `FailureReason="env 正在运行,请先停止"` | start pid 不一致会留 zombie |
| G7 | 测试不依赖 git / 不依赖实网络;RequirementsUninstallerTests 用 `FakePipRunner` 同 v0.6.5.12 模式 | v0.6.5.12 + 既有 pattern |
| G8 | 取消支持:`CancellationToken` 透传到 `pip uninstall`;取消时返回 `Cancelled=true`,status VM 显示 "已取消",不删 marker(半卸载留待下次) | 既有一致 |
| G9 | 不 bump version / 不发 release zip / 无 ledger commit(per v0.6.5.6 hotfix 偏好);新 commit 走普通 `feat(wpf):` / `fix(wpf):` | user scope |
| G10 | 状态面板超时自动收起:成功 2s 后自动收起,失败 / 取消不自动收起(用户看错误) | v0.6.5.10 pattern |
| G11 | resx 新增 +4 keys:EnvList_UninstallBaseEnv / EnvList_UninstallRequirements / UninstallBaseEnv_Title / UninstallRequirements_Title (zh-CN + en) | i18n 一致 |
| G12 | 测试 seam 模式:2 个 internal seam `MessageBoxOverride` for UninstallBaseEnv(若装中保护) + `ConfirmDialogOverride` for UninstallRequirements(防误删) | v0.6.5.19 + v0.6.5.19.1 pattern |
| G13 | `InstallRequirementsCommand` 已装后 IsInstalled 短路(v0.6.5.19);新 `UninstallRequirementsCommand` 必须 IsInstalled:true 才启用,IsInstalled:false 弹 "未安装,无需卸载" | 对称 |
| G14 | error/warn 文案保持中文(跟 v0.6.5.6+ 一致) | 既有 |
| G15 | mutex 持久化:dictionary 内存态即可(同进程内互斥;env 列表 reload 时清空 status) | 简单 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Services/BaseEnvUninstaller.cs` | ~90 | 轻量 uninstall:检查 status=running → 设 `BedStatus=null` + `BedProfileId=null` + `BedFailedReason=null`,返 Result;无 venv 删除 |
| `src-wpf/ComfyUI.Manager/Services/RequirementsUninstaller.cs` | ~110 | 卸载:FilterTorchLines 重建 filtered → pip uninstall -y -r filtered(传 IProgress + ct)→ 删 marker `.requirements_installed`,返 Result |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvUninstallStatusViewModel.cs` | ~75 | 单阶段 inline status VM:IsVisible/LogLines/Error/CloseCommand/Begin/Complete/Fail |
| `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvUninstallerTests.cs` | ~120 | 5 测试:null env / !IsInstalled 短路 / running 拒绝 / 全部字段 reset / 不删 venv |
| `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsUninstallerTests.cs` | ~150 | 5 测试:!IsInstalled 短路 / 重建 filtered / pip 失败返 Reason / 成功后删 marker / 取消返 Cancelled |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvUninstallStatusViewModelTests.cs` | ~80 | 4 测试:初始态/Begin/Complete/Fail + CloseCommand |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelUninstallTests.cs` | ~180 | 7 测试:mutex 互斥 7 路径 + CanExecute + close status |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | +130:2 `RelayCommand` + `_envBusy: Dictionary<string, BusyKind>` + `IsEnvBusy(env)` helper + `MarkEnvBusy/UnmarkEnvBusy` + status VM property `BaseEnvUninstallStatus` + 2 test seam `MessageBoxOverride / ConfirmDialogOverride` |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` | +60:操作列加 2 button + Grid.Row 1 加并列 2nd Border(BaseEnvUninstallStatus,XAML mode 复用 RequirementsStatus) |
| `src-wpf/ComfyUI.Manager/Services/PyTorchVersionCatalog.cs` | +25:`BuildLiveDefaults` 默认 stable 改为 `torch==2.4.1+cu118`;新方法 `MarkIncompatibleOlderVersions(profiles)`,给 `torch<2.4` label 加后缀 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | +8:注册 `BaseEnvUninstaller` + `RequirementsUninstaller` 到 DI |
| `tests-wpf/ComfyUI.Manager.Tests/Services/PyTorchVersionCatalogTests.cs` | +35:`BuildLiveDefaults_ReturnsTorch24AsFirstStable` / `MarkIncompatibleOlderVersions_AddsSuffixToTorch21` / `BuildLiveDefaults_FirstItemHasCudaTag`(3 新测试) |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx` | +4 keys × 2 locale |

### Delete

无。

### Keep (unchanged)

- `BaseEnvInstaller` / `BaseEnvProgressViewModel` / `BaseEnvInstaller.G6` running-guard 检查(我们的 G6 copy same)
- v0.6.5.15 `RequirementsStatusViewModel`(Requirements 卸载直接复用,只新加 BaseEnvUninstallVersionViewModel)
- v0.6.5.19 IsInstalled short-circuit(Requirements 卸载复用同 check)
- v0.6.5.21 9 commits + 2 hotfix(本 plan 在它们之上叠加,不动它们的代码)
- `ProcessLauncher.StartEnvAsync`(本 plan 不动 env-start)
- `NodeOperations.DownloadAsync / InstallAsync`(本 plan 不动 install flow)

---

## Tasks

### Task 1: `BaseEnvUninstaller` 轻量 reset + 5 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/BaseEnvUninstaller.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvUninstallerTests.cs`

**Interfaces:**
```csharp
public sealed class BaseEnvUninstaller
{
    public BaseEnvUninstaller(AppLogger? logger = null);
    public BaseEnvUninstallResult Uninstall(Environment env);
}

public record BaseEnvUninstallResult(
    bool Success,
    bool AlreadyUninstalled,
    bool EnvWasRunning,    // true 表示拒绝卸载,env.Status="running"
    string? Reason);

public static bool IsInstalled(Environment env)
    => env.BedStatus is "done" or "failed" or "installing";
```

**Behavior:**
- `Uninstall(env)`:
  1. 若 `env is null` → 返 `Success=false, Reason="env 为空"`
  2. 若 `IsInstalled(env)==false` → 返 `AlreadyUninstalled=true, Success=true`
  3. 若 `env.Status=="running"` → 返 `EnvWasRunning=true, Success=false, Reason="env 正在运行,请先停止"`
  4. 写 `_logger?.Info("bed-uninstall", $"env='{env.Name}' 开始重置 BedStatus")`
  5. `env.BedStatus=null; env.BedProfileId=null; env.BedFailedReason=null;`
  6. 写 `_logger?.Info("bed-uninstall", $"env='{env.Name}' 重置完成")` → 返 `Success=true`

**关键设计点:** 不调 `IEnvironmentRepository.Save(env)`,**写操作是 VM 自己 commit**(因为 sqlite 在 App 层 wire,VM 见的是 `IEnvironmentRepository` 接口)。`Uninstall` 返回后 caller(VM)负责 Save。理由:Service 是 stateless 的,持久化职责归 caller。

- [ ] **Step 1: Write failing tests** — 5 个:
  - `Uninstall_NullEnv_ReturnsFailureReason`
  - `Uninstall_EnvNotInstalled_ReturnsAlreadyUninstalledTrue`
  - `Uninstall_EnvRunning_ReturnsEnvWasRunningTrue`
  - `Uninstall_EnvInstalled_ResetsAllBedFields`
  - `Uninstall_EnvInstalled_DoesNotDeleteVenvFiles`(用 `tempDir` + fake env.RootPath + Verify file still exists)
- [ ] **Step 2:** Run,verify 5 FAIL(类型不存在)
- [ ] **Step 3:** 实现 verbatim 上述签名 + behavior
- [ ] **Step 4:** Run,verify 5 PASS
- [ ] **Step 5:** Commit `feat(wpf): BaseEnvUninstaller 轻量 reset (不动 venv)`

---

### Task 2: `RequirementsUninstaller` 跑 pip uninstall -y -r filtered + 删 marker + 5 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/RequirementsUninstaller.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsUninstallerTests.cs`

**Interfaces:**
```csharp
public sealed class RequirementsUninstaller
{
    public const string MarkerFileName = ".requirements_installed";  // re-export 跟 RequirementsInstaller 同步

    public RequirementsUninstaller(AppLogger? logger = null);
    public Task<RequirementsUninstallResult> UninstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default);

    public static bool IsInstalled(Environment env)
        => RequirementsInstaller.IsInstalled(env);  // delegate
}

public record RequirementsUninstallResult(
    bool Success,
    bool AlreadyUninstalled,
    bool Cancelled,
    string? Reason,
    int UninstalledCount);
```

**Behavior:**
- `UninstallAsync(env, logProgress, ct)`:
  1. 若 `env is null` → 返 `Success=false, Reason="env 为空"`
  2. `RequirementsInstaller.IsInstalled(env)==false` → 返 `AlreadyUninstalled=true, Success=true, UninstalledCount=0`
  3. 找 `requirements.txt` candidates(委托 `RequirementsInstaller.ResolveRequirementsCandidates(env)`)+ 跑 `FilterTorchLines` 重建 filtered(写到 `env.RootPath/.requirements_uninstall_temp.txt`,避免污染 `.requirements_filtered.txt` 后续 install flow)
  4. 找 venv python(`RequirementsInstaller` 已有但 internal → 改 internal→public 或新加 public helper `ResolveVenvPython(env)`)
  5. 跑 `pip uninstall -y -r <filtered> --disable-pip-version-check`,同 `RunPipAsync` 模式(`RunPipAsync` 改 `protected virtual` 让 test seam override 跟 RequirementsInstaller 同样的 pattern)
  6. 清理 `<filtered>` temp 文件
  7. 若取消 → 返 `Cancelled=true, Success=false`,**不删 marker**
  8. 若 pip 退出码 != 0 → 返 `Reason="pip 退出码 {code}"`,**不删 marker**
  9. 删 `.requirements_installed` + 返 `Success=true, UninstalledCount={filtered.Count}`

**关键改动:**
- `RequirementsInstaller.ResolveVenvPython(env)` 改 `internal` → `public`(G14 cross-task 接口)
- `RequirementsInstaller.ResolveRequirementsCandidates(env)` 已经是 internal,但测试已在同 assembly 调用 → 保持 internal

- [ ] **Step 1:** 改 `RequirementsInstaller.ResolveVenvPython` 为 `public`(目前是 `private static`,改为 `public static` 让 `RequirementsUninstaller` 跨类调用)
- [ ] **Step 2:** Write failing tests — 5 个:
  - `UninstallAsync_NullEnv_ReturnsFailureReason`
  - `UninstallAsync_NotInstalled_ReturnsAlreadyUninstalledTrue`(marker 不存在 → 调用 0 次 pip)
  - `UninstallAsync_Installed_RunsPipUninstallWithFiltered`(用 FakePipRunner 验 captured args `["uninstall","-y","-r",<path>,"--disable-pip-version-check"]`)
  - `UninstallAsync_PipFails_KeepsMarker`(FakePipRunner 返 exit 1 → marker 仍在 disk)
  - `UninstallAsync_Success_DeletesMarker`(FakePipRunner 返 exit 0 → marker 删)
- [ ] **Step 3:** Run,verify 5 FAIL
- [ ] **Step 4:** 实现 verbatim 上述签名 + behavior
- [ ] **Step 5:** Run,verify 5 PASS
- [ ] **Step 6:** Run full suite,verify no regression(496 + 5 = ~501)
- [ ] **Step 7:** Commit `feat(wpf): RequirementsUninstaller 跑 pip uninstall -y -r + 删 marker`

---

### Task 3: `BaseEnvUninstallStatusViewModel` 单阶段 + 4 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvUninstallStatusViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvUninstallStatusViewModelTests.cs`

**Interfaces(单阶段版,跟 v0.6.5.15 RequirementsStatusViewModel 同款):**
```csharp
public sealed class BaseEnvUninstallStatusViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public RelayCommand CloseCommand { get; }

    public void Begin();              // IsVisible=true;Error=null;LogLines.Clear;LogLines.Add("开始卸载基础环境...");
    public void AppendLog(string line);
    public void Complete();           // AppendLog("卸载完成 — env 可重新部署基础环境");Task.Delay(2s)(外部触发 Hide)
    public void Fail(string reason);  // Error=reason;IsVisible 仍 true
    public void Hide();               // IsVisible=false
}
```

- [ ] **Step 1:** Write failing tests — 4 个:
  - `InitialState_IsVisibleFalseErrorNullLogEmpty`
  - `Begin_SetsIsVisibleTrueAndAddsStartLog`
  - `Complete_AppendsCompletionLog`
  - `Fail_SetsErrorButStaysVisible`
- [ ] **Step 2:** Run,verify 4 FAIL
- [ ] **Step 3:** 实现 verbatim(参考 `RequirementsStatusViewModel:1-75` 同样结构)
- [ ] **Step 4:** Run,verify 4 PASS
- [ ] **Step 5:** Commit `feat(wpf): BaseEnvUninstallStatusViewModel 单阶段 inline`

---

### Task 4: `EnvironmentListViewModel` mutex + 2 Uninstall 命令 + 7 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(+ ~130 LOC)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelUninstallTests.cs`

**修改清单:**
1. ctor 新增 `_baseEnvUninstaller: BaseEnvUninstaller` + `_requirementsUninstaller: RequirementsUninstaller` 参数(同时加 optional default `null` 走既有 19 参 ctor,保持向后兼容既有 `MainViewModelEnvironmentViewCachingTests`)
2. 新 enum `private enum BusyKind { None, BEDInstall, BEDUninstall, ReqInstall, ReqUninstall, Start, Stop, Delete }`
3. 新 `_envBusy: Dictionary<string, BusyKind>`(string key = env.RootPath)
4. 新 helper:
   ```csharp
   private bool IsEnvBusy(Environment env)
       => _envBusy.ContainsKey(env.RootPath);  // 任何非 None 视为 busy
   private void MarkEnvBusy(Environment env, BusyKind kind)
       => _envBusy[env.RootPath] = kind;
   private void UnmarkEnvBusy(Environment env)
       => _envBusy.Remove(env.RootPath);
   ```
5. **既有 commands `CanExecute` 加 mutex 检查**(共 7 个):
   - `StartCommand.CanExecute(env)`:既有 `env.Status=="stopped" && env.BedStatus is not null && env.BedStatus!="installing"` → 改成 `&& !IsEnvBusy(env)`
   - `StopCommand.CanExecute(env)`:既有 `env.Status=="running"` → 加 `&& !IsEnvBusy(env)`
   - `BaseEnvCommand.CanExecute(env)`:`!IsInstalled` 或 `==installing` → 加 `&& !IsEnvBusy(env)`
   - `InstallRequirementsCommand.CanExecute(env)`:`!IsInstalled` 或 fakemarker 缺失 → 加 `&& !IsEnvBusy(env)`
   - `DeleteCommand.CanExecute(env)`:既有 → 加 `&& !IsEnvBusy(env)`
   - **新 `UninstallBaseEnvCommand.CanExecute(env)`**:`BaseEnvUninstaller.IsInstalled(env) && env.Status!="running" && !IsEnvBusy(env)`
   - **新 `UninstallRequirementsCommand.CanExecute(env)`**:`RequirementsInstaller.IsInstalled(env) && !IsEnvBusy(env)`
6. **新 2 命令:**
   ```csharp
   public RelayCommand UninstallBaseEnvCommand { get; }
   public RelayCommand UninstallRequirementsCommand { get; }

   public BaseEnvUninstallStatusViewModel? BaseEnvUninstallStatus { get; private set; }
   // (RequirementsStatus 已存在,v0.6.5.15 在 EnvironmentListViewModel 加的 — 直接复用)
   ```
7. **新 2 方法 `UninstallBaseEnvAsync(env)` / `UninstallRequirementsAsync(env)`** — 完整流程:
   ```csharp
   private async Task UninstallBaseEnvAsync(Environment? env)
   {
       if (env is null || IsEnvBusy(env)) return;
       var status = new BaseEnvUninstallStatusViewModel();
       BaseEnvUninstallStatus = status;
       status.Begin();
       MarkEnvBusy(env, BusyKind.BEDUninstall);
       try
       {
           // 确认 dialog(v0.6.5.19 pattern)
           if (!ShowConfirmDialogOverride?.Invoke($"确定要卸载基础环境吗?\nenv: {env.Name}\nvenv 文件会保留,可重新部署。") ?? true)
           {
               status.Fail("用户取消");
               return;
           }
           var result = _baseEnvUninstaller.Uninstall(env);
           if (result.EnvWasRunning)
           {
               ShowMessageBoxOverride?.Invoke("env 正在运行,请先停止", "无法卸载");
               status.Fail("env 正在运行,请先停止");
               return;
           }
           if (result.AlreadyUninstalled)
           {
               status.Fail("env 未安装基础环境,无需卸载");
               return;
           }
           await _envRepo.SaveAsync(env);  // 持久化 reset
           status.Complete();
           await Task.Delay(TimeSpan.FromSeconds(2));
           status.Hide();
       }
       catch (Exception ex)
       {
           status.Fail($"卸载失败:{ex.Message}");
       }
       finally
       {
           UnmarkEnvBusy(env);
           Load();  // reload env 列表(BedStatus 变了)
           RaiseCommandsChanged();
       }
   }
   ```
   Requirements 卸载同款流程(v0.6.5.15 panel mode + `Progress<string>` wrap + `_requirementsUninstaller.UninstallAsync`),用 v0.6.5.15 既有 `RequirementsStatus` property:
   ```csharp
   private async Task UninstallRequirementsAsync(Environment? env)
   {
       if (env is null || IsEnvBusy(env)) return;
       // 复用 v0.6.5.15 既有 RequirementsStatus property(单 VM,跨 invocation reuse;Hide 在 Begin 里清空)
       var status = RequirementsStatus ??= new RequirementsStatusViewModel();
       status.Begin();
       MarkEnvBusy(env, BusyKind.ReqUninstall);
       try
       {
           if (!ShowConfirmDialogOverride?.Invoke(
                   $"确定要卸载依赖吗?\nenv: {env.Name}\n会跑 pip uninstall -y -r ComfyUI/requirements.txt 的非 torch 包。",
                   "卸载依赖") ?? true)
           {
               status.Fail("用户取消");
               return;
           }
           // v0.6.5.11:Progress<T> 包装捕获 SynchronizationContext,后台线程自动 marshal 回 UI
           var progress = new Progress<string>(line => status.AppendLog(line));
           var result = await _requirementsUninstaller.UninstallAsync(env, progress, default);
           if (result.AlreadyUninstalled)
           {
               status.Fail("env 未装依赖,无需卸载");
               return;
           }
           if (!result.Success) { status.Fail(result.Reason ?? "卸载失败"); return; }
           status.Complete();
           await Task.Delay(TimeSpan.FromSeconds(2));
           status.Hide();
       }
       catch (Exception ex)
       {
           status.Fail($"卸载失败:{ex.Message}");
       }
       finally
       {
           UnmarkEnvBusy(env);
           Load();
           RaiseCommandsChanged();
       }
   }
   ```
8. 新 `internal Func<string, string, bool>? ConfirmDialogOverride { get; set; }`(message, title → bool)+ `internal Action<string, string>? MessageBoxOverride { get; set; }`

**关键设计点:**
- mutex `Dictionary<RootPath, BusyKind>` — RootPath 作为 stable key(env.Name 可能重名)
- `UnmarkEnvBusy` 在 finally,保证即使 status.Fail 也会清除 busy
- `ShowConfirmDialogOverride?.Invoke(...) ?? true` — 测试不注入时默认 true(assume yes for prod;prod 真要 confirm dialog 再加 `MainWindow.confirm(...)` 调用)— **简化 prod**:G9 不发 release,GUI smoke 验证按钮直接走,**留下 hook 给后续版本 dialog 完整接**。本 plan 的 `ShowConfirmDialogOverride` 在 prod 路径**直接弹 `MainWindow.confirm`**(具体 dialog 由后续 commit 接,但 VM 留 hook)— 简化:VM 留 hook,prod 路径 fallthrough 到 `MessageBox.Show` 走默认,继续 GUI smoke

- [ ] **Step 1:** Write failing tests — 7 个:
  - `Mutex_StartCommand_DisabledWhenEnvBusy`(BED uninstall 跑时 Start 不可点)
  - `Mutex_UninstallBaseEnv_DisabledWhenStartInProgress`(start 跑时 uninstall 不可点)
  - `Mutex_DifferentEnvs_NotBlocked`(env A uninstall 时 env B start 可点)
  - `UninstallBaseEnvCommand_Execute_ResetsBedFieldsAndReloads`(FakeUninstaller 验证调用 + status.Complete 触发)
  - `UninstallBaseEnvCommand_ConfirmCancel_ShowsFailStatus`(ShowConfirmDialogOverride 返 false → status.Fail("用户取消"),env 不动)
  - `UninstallRequirementsCommand_Execute_CallsUninstallerAndReloads`
  - `UninstallRequirementsCommand_NotInstalled_ShowsFailStatus`(marker 不存在 → status.Fail)
- [ ] **Step 2:** Run,verify 7 FAIL
- [ ] **Step 3:** 实现 mutex + 2 commands + status property + 2 seam + 修改既有 5 commands CanExecute
- [ ] **Step 4:** Run,verify 7 PASS
- [ ] **Step 5:** Run full suite,verify no regression(~501 + 7 = ~508 + 既有 MainViewModelEnvironmentViewCachingTests 仍 19 参过)
- [ ] **Step 6:** Commit `feat(wpf): EnvironmentListViewModel per-env mutex + 2 uninstall commands`

---

### Task 5: `EnvironmentListView.xaml` 操作列加 2 button + Grid.Row 1 加 BaseEnvUninstallStatus Border

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`

**修改清单(0 新测试 — XAML/wiring GUI smoke):**

1. 操作列 DataGridTemplateColumn 内 `<StackPanel Orientation="Horizontal">`,在 "装依赖" 之后加 2 个 button:
   ```xaml
   <Button Content="卸载基础环境"
           Command="{Binding DataContext.UninstallBaseEnvCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}"
           CommandParameter="{Binding}"
           ToolTip="重置 BedStatus,保留 venv 文件,可重新部署基础环境" />
   <Button Content="卸载依赖"
           Command="{Binding DataContext.UninstallRequirementsCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}"
           CommandParameter="{Binding}"
           ToolTip="卸载 ComfyUI requirements.txt 已装的包(SQLAlchemy/einops/transformers 等,不动 torch 系列)" />
   ```
2. Grid.Row 1 已有 RequirementsStatus Border(v0.6.5.15)。改 Grid 为 2 行(RequirementsStatus row + BaseEnvUninstallStatus row 用并列 2 个 Border 横向布局)— **简化**:Grid.Row 1 改成 ItemsControl 模式 — 实际上 v0.6.5.15 加 Requirements panel 时是 1 个 Border 跟 DataGrid 并列在 2 行 Grid,**现在需要 2 个 Border 同时显示可能 1 个或 2 个 status panel**。两种方案:
   - **(A) 每次只显示一个 panel**:Requirements 卸载时 Requirements panel;BaseEnv 卸载时 BaseEnv panel;互斥显示 — **简单**,但与 per-env mutex 重复
   - **(B) 同时显示两个 panel**,竖排或横排
   - **采纳 (A)**:显示逻辑跟 mutex 联动 — mutex 同一时刻只有一个 env 在跑,所以最多一个 BaseEnv panel + 一个 Requirements panel;但 mutex 按 env 区分,跨 env 不冲突 — 实测可能 2 个 panel 同时显示(BaseEnv 卸载 env A + Requirements 卸载 env B)
   - **最终方案(B')**:Grid.Row 1 用 `ItemsControl` 绑到 `EnvListVM.StatusPanels`(新增 ObservableCollection<StatusPanel> Base),动态增删 panel。但模板改太大。
   - **简化最终方案**:Grid.Row 1 改 Grid 2×2,RequirementsStatus row + BaseEnvUninstallStatus row 上下排列,各自 visibility 绑 StatusVM.IsVisible — **采纳**
3. DataGrid.Row 行高 / Grid 分层不动,只 Row 1 加新行
4. 新 Border XAML:
   ```xaml
   <Border Grid.Row="2" Margin="0,8,0,0" Padding="8" Background="#2A1A1A" CornerRadius="4"
           Visibility="{Binding BaseEnvUninstallStatus.IsVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}"
           DataContext="{Binding BaseEnvUninstallStatus}">
       <StackPanel>
           <Grid>
               <Grid.ColumnDefinitions>
                   <ColumnDefinition Width="*" />
                   <ColumnDefinition Width="Auto" />
               </Grid.ColumnDefinitions>
               <TextBlock Grid.Column="0" Text="卸载基础环境" FontWeight="Bold" FontSize="14" Foreground="#FFB8B8" />
               <Button Grid.Column="1" Content="✕" Width="20" Height="20"
                       Command="{Binding CloseCommand}" />
           </Grid>
           <ScrollViewer Height="100" Margin="0,4" VerticalScrollBarVisibility="Auto">
               <ItemsControl ItemsSource="{Binding LogLines}">
                   <ItemsControl.ItemTemplate>
                       <DataTemplate>
                           <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11" Foreground="#FFB8B8" />
                       </DataTemplate>
                   </DataTemplate>
               </ItemsControl>
           </ScrollViewer>
           <TextBlock Text="{Binding Error}" Foreground="#FF6B6B" Margin="0,4,0,0" TextWrapping="Wrap"
                      Visibility="{Binding Error, Converter={StaticResource NullToVisibility}, FallbackValue=Collapsed}" />
       </StackPanel>
   </Border>
   ```
5. Grid 行定义改成 3 行:`<RowDefinition Height="*" />` (DataGrid) + `<RowDefinition Height="Auto" MinHeight="0" />` (Requirements status panel) + `<RowDefinition Height="Auto" MinHeight="0" />` (BaseEnv uninstall status panel)

- [ ] **Step 1:** Read 现有 `EnvironmentListView.xaml` 主 Grid 结构,确认 Row 1 是不是 RequirementsStatus(找 `RequirementsStatus` 或 `EqStartStatus`)
- [ ] **Step 2:** Edit Grid 行定义 → 3 行;改 Row 1 / Row 2 索引
- [ ] **Step 3:** 操作列加 2 个 Button
- [ ] **Step 4:** 加 BaseEnvUninstallStatus Border(verbatim 上面 XAML)
- [ ] **Step 5:** `dotnet build src-wpf/ComfyUI.Manager/ -v minimal` → 0 errors / 0 warnings(检查 Row 索引没串、StaticResource 都在 Theme.xaml 注册)
- [ ] **Step 6:** Commit `feat(wpf): EnvironmentListView 操作列加卸载按钮 + BaseEnvUninstallStatus panel`

---

### Task 6: `PyTorchVersionCatalog` 默认 torch 2.4+ + `MarkIncompatibleOlderVersions` + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/PyTorchVersionCatalog.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/PyTorchVersionCatalogTests.cs`

**修改清单:**
1. `BuildLiveDefaults` / `BuildFallback` 之前 `var first = profiles.FirstOrDefault();` 用首项 stable。现在改成 filter 出 `torch>=2.4` stable 取首项;若全空 → fall through 到 fallback。
2. 新方法 `MarkIncompatibleOlderVersions(profiles)`:
   ```csharp
   public static IReadOnlyList<BaseEnvProfile> MarkIncompatibleOlderVersions(
       IReadOnlyList<BaseEnvProfile> profiles)
   {
       var result = new List<BaseEnvProfile>(profiles.Count);
       foreach (var p in profiles)
       {
           if (p.TorchVersion is null) { result.Add(p); continue; }
           var versionMatch = System.Text.RegularExpressions.Regex.Match(
               p.TorchVersion, @"(\d+)\.(\d+)");
           if (!versionMatch.Success) { result.Add(p); continue; }
           var major = int.Parse(versionMatch.Groups[1].Value);
           var minor = int.Parse(versionMatch.Groups[2].Value);
           if (major < 2 || (major == 2 && minor < 4))
           {
               result.Add(new BaseEnvProfile(
                   Id: p.Id + " (不推荐 — comfy_kitchen 不兼容)",
                   TorchVersion: p.TorchVersion,
                   CudaVariant: p.CudaVariant,
                   PythonVersion: p.PythonVersion,
                   PipExtraArgs: p.PipExtraArgs,
                   IsUserOverrideActive: p.IsUserOverrideActive));
           }
           else
           {
               result.Add(p);
           }
       }
       return result;
   }
   ```

**关键设计点:**
- `BaseEnvProfile` 是 record,新 record instance vs 同 Id 不同 — IsUserOverrideActive 透传
- 修改只在 `IsUserOverrideActive=false`(默认 chain 渲染)时调;user override JSON 文件一律不标(silent override 是用户知情选择)
- `BuildLiveDefaults` pipeline:live fetch → stable → `MarkIncompatibleOlderVersions` → `BuildFallback` first(同样 mark)→ 返
- 既有 dropdown UI 文本 = `profile.Id`(XAML DisplayMemberPath="Id")— 直接显示后缀

- [ ] **Step 1:** Write 3 failing tests:
  - `MarkIncompatibleOlderVersions_Torch21_AppendsIncompatibleSuffix`
  - `MarkIncompatibleOlderVersions_Torch24_LeavesIdUnchanged`
  - `MarkIncompatibleOlderVersions_Torch15_AppendsIncompatibleSuffix`
- [ ] **Step 2:** Run,verify 3 FAIL(方法不存在)
- [ ] **Step 3:** 加 `MarkIncompatibleOlderVersions` + 接入 `BuildLiveDefaults` pipeline
- [ ] **Step 4:** Run,verify 3 PASS
- [ ] **Step 5:** Run full suite,verify no regression(~508 + 3 = ~511)
- [ ] **Step 6:** Commit `fix(wpf): PyTorchVersionCatalog 默认 torch 2.4+ compatible + drop torch<2.4 推荐`

---

### Task 7: resx +4 keys + App DI wire + final verify + staging rebuild

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI register 2 new services)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(ctor 调 EnvListVM 加 2 new param)

**修改清单:**
1. resx +4 keys:
   - `EnvList_UninstallBaseEnv` : zh-CN=`"卸载基础环境"` / en=`"Uninstall Base Env"`
   - `EnvList_UninstallRequirements` : zh-CN=`"卸载依赖"` / en=`"Uninstall Requirements"`
   - `UninstallBaseEnv_Title` : zh-CN=`"正在卸载基础环境"` / en=`"Uninstalling Base Env"`
   - `UninstallRequirements_Title` : zh-CN=`"正在卸载依赖"` / en=`"Uninstalling Requirements"`
2. App.xaml.cs 已有 `EnvDeleterService(_envRepo, _logger)` 类似 pattern — 加:
   ```csharp
   _baseEnvUninstaller = new Services.BaseEnvUninstaller(_logger);
   _requirementsUninstaller = new Services.RequirementsUninstaller(_logger);
   ```
3. MainViewModel.cs 既有 ctor 调 `_envListVM = new EnvironmentListViewModel(_envRepo, _nodeOps, ...)` 加 2 末参数。
4. csproj 不动(asset/ 规则已配,v0.6.5.21 hotfix)。

**final verify:**
- [ ] **Step 1:** `dotnet build src-wpf/ComfyUI.Manager/ -v minimal` → 0 errors / 0 warnings
- [ ] **Step 2:** `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → **~511 PASS / 0 FAIL / 1 SKIP**(基线 496 + 5 uninstaller + 5 req-uninstaller + 4 statusVM + 7 mutex + 3 PyTorch catalog = 520;实际跑出来为准)
- [ ] **Step 3:** 重建 staging per `feedback_staging_self_contained.md`:`dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal`
- [ ] **Step 4:** `git status --short` → working tree clean(staging exe 时间戳变动 gitignored)
- [ ] **Step 5:** 无 v-bump / 无 zip / 无 ledger commit(G9)

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 **~520 PASS / 0 FAIL / 1 SKIP**(基线 496 + T1 5 + T2 5 + T3 4 + T4 7 + T6 3 = 520)

### 端到端手动测试(用户 desktop,走 staging exe)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧栏"环境" → 选中一个 BedStatus="done" + Requirements done 的 env
3. 操作列新增"卸载基础环境" + "卸载依赖"两个按钮可见
4. **点"卸载基础环境"**:
   - DataGrid 下方面板出"卸载基础环境"标题(粉色背景)
   - Confirm dialog "确定要卸载基础环境吗?vnev 文件会保留,可重新部署" 弹 → 取消 → 状态面板显示 "用户取消",env 不动
   - 再点 → 这次确认 → 状态面板 Complete 后 2s 自动收起
   - env 行 BED 列变 "✗ 未装"(BedStatus=null)
5. **点"卸载依赖"**:
   - 数据流同上,Logs/Logs/yyyy-MM-dd.log 出 "env='xxx' requirements-uninstall started" → "pip uninstall -y -r ..." → 退出码 0 → "marker 删除" → succeeded
   - env 行 `<env-root>/.requirements_installed` 文件消失
6. **互斥**:env A 跑卸基础环境时(env A 行所有按钮变灰);env B 操作列同时可点 → 验证 per-env 隔离
7. **新装兼容性**:重新走 BED → 默认应该选 `torch==2.4.1+cu118`(dropdown 第一项),不是 `torch==2.1.0+cu118`
8. **ComfyUI 启动** → 不再 `@torch.library.custom_op` 抛异常,正常 listen 端口

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| `pip uninstall` 不一定真卸干净(`comfy_kitchen` 包同名冲突 / 包被其它依赖 retain) | 用户原话"只清 requirements 装的包",pip 自己处理;失败返 Reason,marker 不删,允许重试 |
| `BaseEnvUninstaller` 不删 venv → 用户重装 BED 时用旧 venv 但 venv 里有旧 torch 残留 | BED Install 流程本身用 `pip install --force-reinstall torch=={profile}` pin,会覆盖旧版本(G1 + v0.6.5 既有 — 落实验证需 GUI smoke) |
| mutex dictionary 在 env list reload 时不清 → 旧 env id stuck | Env list reload 后旧 env 不会进 busy 字典(从来没新增过),不需要清;Load() 后 RaiseCommandsChanged 触发 CanExecute recheck |
| 2 个 status panel 同时显示一个 Grid.Row 1 时行高争夺 | Grid.Row 1 MinHeight=0,空闲时 0;有内容时 Auto,可观察 |
| `MarkIncompatibleOlderVersions` 改 Id 后,`profile.Id` 在 XAML 显示改变(user-dropdown 选过 2.1.x 后 UI 文本加 "(不推荐 — comfy_kitchen 不兼容)")| 设计如此:user override JSON 不动,user 知情选择保留 |
| `BuildLiveDefaults` 默认 torch 2.4.1 后,**老用户重装 BED(已在 2.1)再点 BED**:dropdown 默认 2.4.1 但 sqlite BedProfileId 仍是 2.1.0,UI 显示不一致 | 既不一致就 reinstall — 是用户主动操作 |
| `PyTorchVersionCatalog.MarkIncompatibleOlderVersions` 用 `BaseEnvProfile` 全字段重建 record,`==` 比较可能 false(equatable record 默认 reference) | records 默认有 value equality(G3 record syntax)— 没事 |
| `app.ico` 在 csproj 不动;asset/ 已拷 (v0.6.5.21 hotfix)— 不重复加 | 已有 |
| 状态面板在 env list 卸载流程跑成功后 2s 自动收起,太快 | 跟 v0.6.5.10 失败不收起 + 成功 2s 一致 — user 已知 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Services/BaseEnvUninstaller.cs`(new)
- `src-wpf/ComfyUI.Manager/Services/RequirementsUninstaller.cs`(new)
- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvUninstallStatusViewModel.cs`(new)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(mutex + 2 commands + property)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(2 buttons + BaseEnvUninstallStatus Border)
- `src-wpf/ComfyUI.Manager/Services/PyTorchVersionCatalog.cs`(默认 2.4+ + MarkIncompatibleOlderVersions)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI wire)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(ctor 加 2 参数)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`(+4 keys)
- `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`(ResolveVenvPython → public)
- `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvUninstallerTests.cs`(new)
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsUninstallerTests.cs`(new)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvUninstallStatusViewModelTests.cs`(new)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelUninstallTests.cs`(new)
- `tests-wpf/ComfyUI.Manager.Tests/Services/PyTorchVersionCatalogTests.cs`(+3 tests)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 7 task(3 service/VM + 1 EnvListVM 重构 + 1 XAML + 1 catalog + 1 final verify)= ~7 dispatch
- Per-task review gate(sonnet implementer + sonnet reviewer)
- T1-T4 单元测试覆盖核心逻辑;T5 XAML/wiring + T6 catalog 边界手工 GUI smoke 验
- Estimated 7 commits on main

(Plan agent left out: 互斥复杂度已由 G3 用户决策明确(mutex=per-env),服务行为已由 G1/G2 用户决策明确(轻量/标准);spec 完整,无需额外 design pass。)

If this plan is relevant to the current work and not already complete, continue working on it.
