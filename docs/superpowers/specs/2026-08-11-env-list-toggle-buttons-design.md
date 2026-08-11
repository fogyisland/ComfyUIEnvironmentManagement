# Env-List Toggle Buttons — Design

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` after plan is approved.

**Goal:** env-list 操作列 4 个按钮(`装依赖`/`卸依赖`/`安装基础环境`/`卸载基础环境`)合并成 2 个 toggle 按钮;toolbar "基础环境部署" 按钮合并到 per-env 行 toggle(因 install + uninstall = 1 按钮)。完全复用 v0.6.11+ T4 已落地的 ComfyUI-Manager toggle pattern(`ToggleComfyUiManagerCommand` + `Environment.ComfyUiManagerButtonText`)。

**Architecture:** 新增 2 个 `RelayCommand`(`ToggleRequirementsCommand` / `ToggleBaseEnvCommand`),每个内部根据 `RequirementsInstaller.IsInstalled(env)` / `BaseEnvUninstaller.IsInstalled(env)` 判断调 install 还是 uninstall 子命令。`Environment` 模型加 `RequirementsButtonText` / `BaseEnvButtonText` 字符串属性(同 ComfyUiManagerButtonText pattern)。CanExecute 走现有 `IsEnvBusy(env)` mutex;busy 时按钮自动禁用,PropertyChanged 在 install state 变更时触发,label 同步刷新。复用现有 `RequirementsStatus` / `BaseEnvUninstallStatus` inline 状态面板做进度反馈。4 task SDD(T1 VM + T2 XAML + T3 resx/STA test + T4 final review + MEMORY + staging rebuild)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 既有 ViewModelBase / RelayCommand / EnvironmentListViewModel 模式

**base SHA:** `a565f9c` (v0.6.11+ Remove BaseEnv sidebar SHIP-READY,818/0/1 baseline)

---

## Context

v0.6.11+ Remove BaseEnv sidebar SHIP-READY 后,用户桌面验 staging 时反馈 env-list 操作列按钮过多。现有 per-env row 6 + toolbar 3,装卸链路密集。希望合并对称的 install/uninstall pair → 单 toggle 按钮(动态 label + 动态 action),跟 v0.6.11+ T4 已落地的 ComfyUI-Manager toggle 同款 UX。

## 用户原话
- "装依赖和卸载依赖缩成一个按钮,不再需要两个按钮"
- "安装基础环境和卸载基础环境也是一个[按钮]"
- (后续澄清)Toggle 按钮 label 动态变化(`装依赖` ↔ `卸依赖` / `安装基础环境` ↔ `卸载基础环境`)
- (后续澄清)Busy 时按钮禁用 + label 显示进度文案(`装依赖中...` / `卸依赖中...`),跟现有 inline 状态面板互补

## Global Constraints

| # | Constraint |
|---|---|
| **G1** | **复用 ComfyUI-Manager toggle pattern**(v0.6.11+ T4 已落地):`Environment` 模型加 `ButtonText` 字符串属性 + `EnvironmentListViewModel` 加 `ToggleCommand`(`RelayCommand` + `IsEnvBusy` gate);进度走 inline 状态面板(`RequirementsStatus` / `BaseEnvUninstallStatus` / `ComfyUiManagerStatus`)。不要新设计 toggle UI pattern |
| **G2** | **保留所有现有 install / uninstall 子命令接口不变**:`InstallRequirementsCommand` / `UninstallRequirementsCommand` / `OpenBaseEnvProgress` / `UninstallBaseEnvCommand` 4 个 RelayCommand + 4 个 private async 方法(供 v0.6.5.19 / v0.6.5.19.1 / v0.6.5.22 现有测试 + future caller 复用);`MessageBoxOverride` / `ConfirmDialogOverride` test seam 不动 |
| **G3** | **toolbar `BaseEnvCommand` 删除**:env-list 顶部 "基础环境部署" 按钮被 per-env BED toggle 取代(Selected env 时 redundant)。`BaseEnvCommand` RelayCommand property + `OpenBaseEnvProgressAsync` 方法在 EnvListVM 中保留(若 ToggleBaseEnvCommand 内部调,作为 helper;若不调,删之 —— T1 implementer 决定)。**关联 `BaseEnvCommand` 测试(若有)**:若 `BaseEnvCommand` 删了,删测试;若保留,测试不动 |
| **G4** | **VM 接口冻结(扩展)**:不删 `OpenBaseEnvProgress` 任何 caller;`BaseEnvProfilePickerDialog` / `BaseEnvProfileLoader` / `BaseEnvInstaller` / `BaseEnvUninstaller` / `RequirementsInstaller` / `RequirementsUninstaller` 服务类不动;Settings.cs / SQLite schema 不动 |
| **G5** | **不引入新依赖**;所有现有 resx / Brush / Style / Button style / 命令 pattern 复用 |
| **G6** | **按钮 label 跟 ComfyUI-Manager toggle 一致用硬编码中文**:在 `Environment` 模型默认值 + inline 更新处硬编码 `"装依赖"` / `"卸依赖"` / `"装依赖中..."` / `"卸依赖中..."` / `"安装基础环境"` / `"卸载基础环境"` / `"安装基础环境中..."` / `"卸载基础环境中..."`。**不进 resx**(跟 ComfyUI-Manager toggle pattern 一致 — `ComfyUiManagerButtonText` 也是硬编码)。但 `Strings.resx` / `Strings.zh-CN.resx` 已存在的 `EnvList_UninstallBaseEnv` / `EnvList_UninstallRequirements` / `UninstallBaseEnv_Title` / `UninstallRequirements_Title` 保留(其他 view 可能引用) |
| **G7** | **测试不写脆弱 UI 行为**:VM 单测覆盖 toggle 命令路由 + busy 门控 + label 变更;XAML STA-thread load test 验证操作列渲染不抛;T1 implementer 必须用 fake `_installer` / `_uninstaller` 隔离(避免真的跑 pip install) |
| **G8** | **每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer,严格匹配 progress.md ledger** |
| **G9** | **Settings 字段冻结**:不动 Settings.cs / appsettings.json / 任何 UI preferences |
| **G10** | **失败 retry 走完整 install 流程**:BED 失败时 button label 回 `"安装基础环境"`(不是 `"重试"`),点击走 picker dialog 同首次安装;Requirements 失败时 button label 回 `"装依赖"`,点击重跑 pip install |

---

## Design

### 1. New VM commands

**`src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:**

新增 2 个 RelayCommand property(沿 line 88 现有 `ToggleComfyUiManagerCommand` pattern):

```csharp
public RelayCommand ToggleRequirementsCommand { get; }
public RelayCommand ToggleBaseEnvCommand { get; }
```

ctor 初始化(沿 line 280 现有 `ToggleComfyUiManagerCommand` ctor):

```csharp
ToggleRequirementsCommand = new RelayCommand(
    async p => await ToggleRequirementsAsync(p as Environment ?? Selected),
    p =>
    {
        var env = p as Environment ?? Selected;
        if (env is null) return false;
        if (IsEnvBusy(env)) return false;
        return true;
    });

ToggleBaseEnvCommand = new RelayCommand(
    async p => await ToggleBaseEnvAsync(p as Environment ?? Selected),
    p =>
    {
        var env = p as Environment ?? Selected;
        if (env is null) return false;
        if (IsEnvBusy(env)) return false;
        return true;
    });
```

新增 2 个 private async 方法(沿 line 871 现有 `ToggleComfyUiManagerAsync`):

```csharp
internal async Task ToggleRequirementsAsync(Environment? env)
{
    if (env is null) return;
    if (IsEnvBusy(env)) return;

    if (RequirementsInstaller.IsInstalled(env))
        await UninstallRequirementsAsync(env);
    else
        await InstallRequirementsAsync(env);
}

internal async Task ToggleBaseEnvAsync(Environment? env)
{
    if (env is null) return;
    if (IsEnvBusy(env)) return;

    if (BaseEnvUninstaller.IsInstalled(env))
        await UninstallBaseEnvAsync(env);
    else
        await OpenBaseEnvProgressAsync(env);   // 走 picker dialog(签名扩展)
}
```

`internal` 修饰让测试能直接 await(避免绕 RelayCommand fire-and-forget),跟 `ToggleComfyUiManagerAsync` 一致。

**签名扩展注意**:line 474 现有 `OpenBaseEnvProgressAsync()` 无参(用 `Selected` 隐式);T1 需扩展为 `OpenBaseEnvProgressAsync(Environment? env)`,toolbar `BaseEnvCommand` handler 调用处临时设 `Selected = env` 后调,或保留 helper method `OpenBaseEnvProgressAsync(Environment? env)` + overload `OpenBaseEnvProgressAsync()` 设 Selected 后调。**T1 implementer 自选**,G3 默认 G3a 保留 `BaseEnvCommand` toolbar + `OpenBaseEnvProgressAsync()` toolbar helper,新增 `OpenBaseEnvProgressAsync(Environment env)` per-env helper。

### 2. New model properties

**`src-wpf/ComfyUI.Manager/Models/Environment.cs`:**

在 line 66 现有 `ComfyUiManagerButtonText` 旁加:

```csharp
public string RequirementsButtonText { get; set; } = "装依赖";
public string BaseEnvButtonText { get; set; } = "安装基础环境";
```

### 3. Dynamic label updates

**`src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:**

`ShowEnvironments` (or `Load` —— 现有 line 348-350 模式):
```csharp
// 现有 ComfyUiManager:
var installed = _comfyUiManagerInstaller.IsInstalled(env);
env.IsComfyUiManagerInstalled = installed;
env.ComfyUiManagerButtonText = installed ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";

// 新增:
env.IsRequirementsInstalled = RequirementsInstaller.IsInstalled(env);
env.RequirementsButtonText = env.IsRequirementsInstalled ? "卸依赖" : "装依赖";
env.IsBaseEnvInstalled = BaseEnvUninstaller.IsInstalled(env);
env.BaseEnvButtonText = env.IsBaseEnvInstalled ? "卸载基础环境" : "安装基础环境";
```

`RequirementsButtonText` 更新点(沿 line 904 `ComfyUiManagerButtonText` 更新模式):
- `InstallRequirementsAsync` 末尾(line 580+):成功时 `env.RequirementsButtonText = "卸依赖"; env.IsRequirementsInstalled = true;`
- `UninstallRequirementsAsync` 末尾(line 774+):成功时 `env.RequirementsButtonText = "装依赖"; env.IsRequirementsInstalled = false;`
- 失败时不更新 label(G10:失败时 label 回原状态,下次点 retry 走完整 install 流程)

`BaseEnvButtonText` 更新点:
- `OpenBaseEnvProgressAsync` 成功路径(用户 picker 选 profile 装完后):`env.BaseEnvButtonText = "卸载基础环境"; env.IsBaseEnvInstalled = true;`
- `UninstallBaseEnvAsync` 末尾:成功时 `env.BaseEnvButtonText = "安装基础环境"; env.IsBaseEnvInstalled = false;`

**注意**:`OpenBaseEnvProgressAsync` 是 toolbar `BaseEnvCommand` handler(toggle 删后,G3 决定是保留 as helper 还是删);若保留,需在成功后更新 `env.BaseEnvButtonText`。

**Busy 状态 label 变更**(用户原话 "Busy 时禁用 + 进度文案"):
- 进入 `InstallRequirementsAsync` 顶部(在 IsEnvBusy check 之后):`env.RequirementsButtonText = "装依赖中...";`
- 进入 `UninstallRequirementsAsync` 顶部:`env.RequirementsButtonText = "卸依赖中...";`
- 进入 `OpenBaseEnvProgressAsync` 顶部:`env.BaseEnvButtonText = "安装基础环境中...";`
- 进入 `UninstallBaseEnvAsync` 顶部:`env.BaseEnvButtonText = "卸载基础环境中...";`
- 结束(成功/失败)时 label 回 install/uninstall 二选一(G10 决定失败回原状态)

### 4. IsInstalled state properties

**`src-wpf/ComfyUI.Manager/Models/Environment.cs`:** 加(类似现有 line 65 `IsComfyUiManagerInstalled`):

```csharp
public bool IsRequirementsInstalled { get; set; }
public bool IsBaseEnvInstalled { get; set; }
```

**注意**:这些字段不进 SQLite(只是 in-memory cached state,启动时从 marker / BedStatus 重新算)。跟 `IsComfyUiManagerInstalled` 一致(后者也不持久化)。

### 5. XAML changes

**`src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`:**

- 删 toolbar line 25-26 `<Button Content="基础环境部署" ...>`(G3:`BaseEnvCommand` 删/保留待 T1 implementer 决定,但 toolbar 按钮一定删)
- Per-env row 0 line 351-368:4 个 button(col 2 + col 3 + col 4) → 2 个 toggle button(col 2 + col 3):
  - col 2:`Content="{Binding RequirementsButtonText}"` `Command="{Binding DataContext.ToggleRequirementsCommand, ...}"` `Style="{StaticResource MaterialButton}"`(`MaterialButton` 因 uninstall 也走同按钮,不能固定 DangerButton — 沿用 ComfyUI-Manager toggle 模式)
  - col 3:`Content="{Binding BaseEnvButtonText}"` `Command="{Binding DataContext.ToggleBaseEnvCommand, ...}"` `Style="{StaticResource MaterialButton}"`
- 现有 col 5 ComfyUI-Manager toggle 移到 col 4(因 row 0 从 6 列变 5 列)
- Grid 列定义 line 331-336:`<ColumnDefinition Width="*" />` 5 个(从 6 个)
- Per-env row 1(line 378-404)5 列布局不变(col 0-4,5 列)

**关键 UX 一致性**(沿用 ComfyUI-Manager toggle):
- Toggle 按钮用 `MaterialButton` style(不是 `DangerButton`)— 因为同一个按钮处理 install 和 uninstall,不能固定危险色
- MinWidth=0(沿现有 per-env row 按钮)Margin=2(沿现有)
- ToolTip 描述该按钮的当前动作(动态文本? 或固定 "装/卸 ComfyUI requirements.txt" 描述)— T1 implementer 决定;固定描述简单,沿 ComfyUI-Manager toggle 的 ToolTip 模式

### 6. Resx impact

`Strings.resx` / `Strings.zh-CN.resx`:
- **保留**:`EnvList_UninstallBaseEnv` / `EnvList_UninstallRequirements` / `UninstallBaseEnv_Title` / `UninstallRequirements_Title`(其他 view 可能引用;G6 严格保留)
- **不新增**(G6 决定 toggle label 走硬编码中文,跟 ComfyUiManagerButtonText 一致)

---

## File Structure

| 文件 | 操作 |
|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Modify:加 `ToggleRequirementsCommand` / `ToggleBaseEnvCommand` RelayCommand + ctor 初始化 + `ToggleRequirementsAsync` / `ToggleBaseEnvAsync` private async + `RequirementsButtonText` / `BaseEnvButtonText` 更新点(5 处:Load + InstallRequirementsAsync 末尾 + UninstallRequirementsAsync 末尾 + OpenBaseEnvProgressAsync 末尾 + UninstallBaseEnvAsync 末尾)+ busy 顶部 label 切换(4 处)+ `RaiseCommandsChanged()` 加 2 个新命令名 |
| `src-wpf/ComfyUI.Manager/Models/Environment.cs` | Modify:加 `RequirementsButtonText` / `BaseEnvButtonText` 字符串属性 + `IsRequirementsInstalled` / `IsBaseEnvInstalled` bool 属性 |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` | Modify:删 toolbar "基础环境部署" button(1 个)+ per-env row 0 col 2-4 三按钮改两 toggle(2 个) + col 5 ComfyUI-Manager 移到 col 4 + Grid 列定义 6→5 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` | Modify:加 6-8 测试(toggle command routing + busy 门控 + label 变更)|
| `tests-wpf/.../EnvironmentListViewModelOpenBaseEnvTests.cs` | Modify(若 G3 删 toolbar BaseEnvCommand):删 toolbar 测试;若 BaseEnvCommand 保留 as helper,测试不动 |
| `tests-wpf/.../Views/EnvironmentListViewLoadTests.cs` | Modify:加 1 STA-thread load test,验证操作列渲染不抛(行 5 toggle button 布局) |

**未触及文件**(G4 冻结):
- `Services/BaseEnvInstaller.cs` / `BaseEnvUninstaller.cs` / `BaseEnvProfileLoader.cs` / `BaseEnvProfilePickerDialog.xaml/cs` / `BaseEnvProfilePickerViewModel.cs` / `RequirementsInstaller.cs` / `RequirementsUninstaller.cs`
- `Models/BaseEnvProfile.cs` / `BaseEnvUninstallStatus.cs` / `RequirementsStatus.cs` / `RequirementsStatusViewModel.cs` / `BaseEnvUninstallStatusViewModel.cs` / `ComfyUIManagerStatusViewModel.cs`
- `Settings.cs` / 所有 SQLite schema / 所有 dialogs(除了上面列的 EnvListVM + Environment + EnvironmentListView)
- `Resources/Strings.resx` / `Strings.zh-CN.resx`(G6 决定不新增 toggle label key)

---

## State Machine (per-env per-row)

| state | RequirementsButtonText | BaseEnvButtonText | ToggleReq CanExecute | ToggleBED CanExecute |
|---|---|---|---|---|
| 未装 + 未 busy | `装依赖` | `安装基础环境` | true | true |
| 装中 (busy) | `装依赖中...` | (不变) | false | false |
| 已装 + 未 busy | `卸依赖` | `卸载基础环境` | true | true |
| 卸中 (busy) | `卸依赖中...` | (不变) | false | false |
| 失败 + 未 busy | `装依赖` (retry) | `安装基础环境` (retry) | true | true |

Busy mutex 跨所有 4 个 install/uninstall + ComfyUiManager 共享(沿现有 `IsEnvBusy` 实现)。

---

## Risks

| 风险 | 缓解 |
|---|---|
| Toolbar `BaseEnvCommand` 删除后,某些 caller 找不到 BED install 入口(per-env 才有 toggle,没 Selected 时 toolbar 不再有按钮) | per-env 行内 toggle 直接可见,无需 Selected;GUI smoke 验证:启动 staging → 不选 env → 操作列每行都能看到 toggle → 不需要 toolbar |
| Toggle 按钮 label "装依赖中..." 等 busy 文案干扰 WPF binding 刷新(PropertyChanged 时机不对) | 沿用 ComfyUiManager toggle 模式:busy 顶部直接 set model property + `RaisePropertyChanged` (同 line 904 模式);STA load test + dotnet test 全 PASS 即可 |
| `OpenBaseEnvProgressAsync` 在 toggle 内部被调,但 G3 要求 BaseEnvCommand 可能删 — helper 引用空 | T1 implementer 必须选:G3a) 保留 `BaseEnvCommand` RelayCommand as public(Selected env 路径),`OpenBaseEnvProgressAsync` as helper;**或** G3b) 删 `BaseEnvCommand` RelayCommand + `BaseEnvCommand` property,只保留 `OpenBaseEnvProgressAsync(env)` private method 作为 `ToggleBaseEnvAsync` 的 helper。G3a 风险最低(toolbar 已有按钮,改 1 行);**默认 G3a**,T1 implementer 解释 deviation |
| Busy label 改 model property 触发 `RaisePropertyChanged` 风暴 — 1 次 install 设 4 次 (顶部 / 末尾成功 / 末尾失败 / unlock) | T1 implementer 只在进入 busy 时设 1 次 busy label,结束时设 1 次 final label;不重复设 |
| `IsRequirementsInstalled` / `IsBaseEnvInstalled` 不存 SQLite → 重启后从 marker / BedStatus 重新算,首次 `Load` 时机竞争 | 沿用 `IsComfyUiManagerInstalled` 模式(同样不存 SQLite,Load 时调 `_installer.IsInstalled()`);T1 测试覆盖重启场景 |
| 测试用 fake installer / uninstaller,busy mutex 需精确模拟(MarkEnvBusy / UnmarkEnvBusy) | T1 测试沿用现有 `FakeInstaller` / `FakeUninstaller` + `MarkEnvBusy` 手动调;env-list 测试已有此模式(`OpenBaseEnvProgress_AfterDialogCloses_TriggersReload` 之类)|
| 操作列 6 列 → 5 列,布局宽度重新分配;5 个 `*` 自动均分但 ComfyUI-Manager toggle 文字变长可能 overflow | GUI smoke 验证:启动 staging → env-list → 5 列按钮宽度自适应;若 overflow 调 MinWidth / 字数(`"卸载基础环境"` 6 字 最长)|
| 失败重试走 picker dialog(同首次),但 picker dialog 可能耗时 — 用户不耐烦 | 沿用现有首次 install 行为;GUI smoke 观察;若反馈再优化 |
| `ToggleRequirementsCommand` uninstall 路径调 `UninstallRequirementsAsync(env)`,但后者 `CanExecute` 也有 `IsInstalled` check — toggle 内 install 路径调 `InstallRequirementsAsync`,后者有 `IsInstalled` check | T1 implementer 注意:若 IsInstalled == true 时点 toggle → 调 UninstallRequirementsAsync(对);IsInstalled == false 时点 toggle → 调 InstallRequirementsAsync(对);CanExecute 不重复 gate `IsInstalled` |

---

## Testing Strategy

### Unit tests(必须)

1. **`ToggleRequirementsCommand_Uninstalled_InvokesInstall`**:构造 env,IsRequirementsInstalled=false,fire command → fake installer.InstallAsync 被调 1 次,uninstaller 0 次
2. **`ToggleRequirementsCommand_Installed_InvokesUninstall`**:同上,但 IsRequirementsInstalled=true → uninstaller.InstallAsync 被调 1 次,installer 0 次
3. **`ToggleRequirementsCommand_Busy_IsDisabled`**:env MarkEnvBusy 后,command.CanExecute(null) == false;即使 env = Selected
4. **`ToggleRequirementsCommand_NullEnv_NoOp`**:Selected = null 时,CanExecute false,Execute 不抛
5. **`ToggleBaseEnvCommand_Installed_InvokesUninstall`**:IsBaseEnvInstalled=true → 调用 UninstallBaseEnvAsync(用 fake 验)
6. **`ToggleBaseEnvCommand_Uninstalled_InvokesInstallViaPicker`**:IsBaseEnvInstalled=false → 调用 OpenBaseEnvProgressAsync(返回 fake PickerResult)
7. **`ToggleBaseEnvCommand_Busy_IsDisabled`**:同 #3
8. **`RequirementsButtonText_TransitionsToBusy_WhenInstallStarts`**:调 ToggleRequirementsAsync(IsInstalled=false) → 等到进入 InstallRequirementsAsync 后(顶部 label 设完),验 RequirementsButtonText == "装依赖中..."
9. **`BaseEnvButtonText_TransitionsToInstalledLabel_WhenInstallSucceeds`**:同上,install 成功后 BaseEnvButtonText == "卸载基础环境"
10. **`RequirementsButtonText_StaysAtInstall_OnFailure`(G10)**:调 ToggleRequirementsAsync,强制 fake installer throw → 末尾 RequirementsButtonText 应保持进入前的 label(失败不更新)

### Manual GUI smoke(用户桌面,SHIPPED 后)

1. 启动 staging → env-list 操作列每行:启动/停止/[Requirements toggle]/[BED toggle]/[ComfyUI-Manager toggle](5 列)+ 调试删除链路(5 列)
2. 启动 staging → toolbar 只有 刷新 + 新建环境(2 个,无"基础环境部署")
3. 未装 env → 点 Requirements toggle → 按钮变灰 + label "装依赖中..." + inline `RequirementsStatus` 面板出现
4. 装完 → 按钮 enabled + label "卸依赖"
5. 点 toggle → 卸中 → 按钮变灰 + label "卸依赖中..." + inline 状态
6. 卸完 → label 回 "装依赖"
7. 同 3-6 BED 流程(label "安装基础环境" → "安装基础环境中..." → "卸载基础环境" → "卸载基础环境中..." → "安装基础环境")
8. ComfyUI-Manager toggle 行为不变(原有 v0.6.11+ T4 测试覆盖)
9. Busy mutex 验证:同时点 Requirements toggle + BED toggle + ComfyUI-Manager toggle → 第一个点中后其他 2 个 IsEnabled=false(灰)
10. 失败重试:fake installer throw(测试覆盖)→ GUI 验 staging 时可用 dialog "强制失败" 测试按钮

### STA-thread load test(必须)

`tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs` 加 1 测试:
- `EnvListView_RendersWith5ColumnOperationRow_NotThrows`:headless STA load,创建 fake EnvListVM with 3 envs,Instantiate EnvironmentListView,验不抛;验每行 toggle button 渲染(用 VisualTreeHelper.FindDescendant 找 3 个 TextBlock,内容分别是 RequirementsButtonText / BaseEnvButtonText / ComfyUiManagerButtonText)

---

## Verification (end-to-end)

按顺序验证 4 task commit 全 PASS:

```bash
# T1: VM + Model
git status --short   # EnvListVM.cs + Environment.cs modified
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal   # 全 PASS

# T2: XAML + 删 toolbar button
git status --short   # EnvironmentListView.xaml modified
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# T3: STA load test + 全套验证
git status --short   # EnvironmentListViewLoadTests.cs + 既有 EnvListVM tests
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 818+ baseline + 7-9 新测试

# T4: final review + MEMORY + staging rebuild
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal   # 0/0
```

---

## Carry-forward(预期)

- 用户桌面验后若 toggle label "装依赖中..." 觉得突兀,可改 WPF template + Animation(Spinner)
- 用户若想要 toggle 按钮 color 区分(uninstall 时 DangerButton 色)— 沿 DangerBrush 在 PropertyChanged 切换,需要 Style Trigger,工作量 +1 task
- v0.6.5.19 + v0.6.5.19.1 IsInstalled guards(toggle 内调子命令,guards 失效)— 但 toggle 替代 guards 更直观,guards 可在后续 cleanup 删

---

## Scope Check

**Focused:** 单 view-only 按钮合并 + VM 加 2 toggle 命令 + 2 model 字段。复用 ComfyUI-Manager toggle pattern。无新功能,无架构变更,无 DB 变更,无 Settings 字段,无 dialog 改动。**单一实施 plan 覆盖完整**。

**Decompose?** 不需要。3-4 task 自然顺序(VM/Model → XAML → 测试 → review),scope 独立。