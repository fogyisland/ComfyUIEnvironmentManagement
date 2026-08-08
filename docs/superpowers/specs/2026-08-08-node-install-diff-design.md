# v0.6.7.5 Node Install Diff Scan + Downgrade Warning — 设计 Spec

> **Status:** approved 2026-08-08
> **base:** `dacaf24` (v0.6.7.4 SHIP-READY)
> **Goal:** 节点安装前比对 catalog PipRequirements 跟 env 当前 pip,降级 / 冲突时弹 modal 警告,防止 ComfyUI 运行异常

---

## Context

用户桌面验 v0.6.7.4 时反馈:在 catalog 装一个节点,该节点的 pip 需求可能跟 env 当前装的版本冲突 / 降级,导致 ComfyUI 运行异常,需要预先评估。

**用户原话:**
> 在安装节点的时候评估下他与当前节点在各个组件的差异性,特别针对 comfyui 的 Requirments 的节点降级的组件进行提示:节点安装可能导致 comfyui 运行异常,这些是我们通过节点扫描需要完成的。

### 范围决策(脑暴产出)

| 问题 | 决定 |
|---|---|
| 对照谁 | env 当前 pip list(对比节点需求 vs env 现状) |
| 何时 scan | Pre-clone(用 v0.6.7.4 入库的 catalog `PipRequirements`,零 git 成本) |
| UX | Modal 警告 + [取消] [仍然安装](降级 / 冲突才弹) |
| 当前 pip 来源 | Live `pip list --format=json` on env.PythonExecutable(~0.5-1s) |
| 警告类别 | Downgrade + Conflict(New / Upgrade 静默) |

### 排他

- **不做** `pip install --dry-run`(post-clone)— 选了 pre-clone,不做
- **不做** cross-node collision(节点 vs 其它节点)— 选了 env pip 对比
- **不做** 非 catalog 装的 diff(custom repo URL 装没 PipRequirements,静默跳过)
- **不做** post-install diff 报告(选了 pre-install 警告)
- **不做** pip snapshot 缓存(选了 live pip list)

---

## Architecture

3 个新模块 + 2 个修改:

- **新** `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs` — `{ IReadOnlyList<DiffEntry> Entries; IReadOnlyList<DiffEntry> Warnings }`,Warnings = Entries 中 Category ∈ {Downgrade, Conflict} 的子集
- **新** `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs` — `{ string Name; DiffCategory Category; string? FromVersion; string? ToVersion }` + enum `DiffCategory { New, Upgrade, Downgrade, Conflict, NoChange }`
- **新** `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs` — `Task<NodeInstallDiffReport> CheckAsync(Models.Environment env, IReadOnlyList<PipRequirement> catalogReqs, CancellationToken ct)`
- **新** `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs` — `{ IReadOnlyList<DiffEntry> Warnings; string NodePackage; bool Proceed }` + Cancel/Proceed commands
- **新** `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml` + `.xaml.cs` — modal dialog
- **改** `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` — `InstallAsync` 加尾参 `IReadOnlyList<PipRequirement>? catalogPipReqs = null, CancellationToken ct = default`;clone 前调 DiffService;警告 → ShowDialog → 取消返 Fail
- **改** `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` — 调 `_ops.InstallAsync(envId, Entry.Package, repoUrl, Entry.PipRequirements, ct)`

依赖注入(在 App.cs DI container):`NodeInstallDiffService` 单例,需要 `Settings` 之外的 `ProcessRunner`(或复用既有 `GitRunner` 的同等抽象 — 跑任意可执行 + 拿 stdout)。

> **设计选择:`ProcessRunner` 抽象:** 看 `GitRunner` 已有跑 `git` 的代码,如果它已经是 `IProcessRunner` 接口就复用;否则抽 `IProcessRunner.RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken)`(返回 `ProcessResult { Ok, ExitCode, Stdout, Stderr }`)— `GitRunner` 实现 `git clone ...` / `git rev-parse HEAD ...` 等;`NodeInstallDiffService` 实现 `python.exe -m pip list --format=json`。**避免**在 `NodeInstallDiffService` 里 `Process.Start` 直接跑,统一走 DI 的 runner,便于测试 mock。

---

## Data Flow

```
InstallDialogVM.InstallAsync(env, Entry, repoUrl)
  ↓ _ops.InstallAsync(envId, Entry.Package, repoUrl, Entry.PipRequirements, ct)
NodeOperations.InstallAsync(envId, nodeId, repoUrl, targetTag?, catalogPipReqs?, ct):
  if catalogPipReqs != null && catalogPipReqs.Count > 0:
    env = RequireEnv(envId)
    if !string.IsNullOrEmpty(env.PythonExecutable) && File.Exists(env.PythonExecutable):
      report = await _diffService.CheckAsync(env, catalogPipReqs, ct)
      if report.Warnings.Count > 0:
        proceed = ShowDiffWarningDialog(report, env, nodeId)  // 阻塞式 modal
        if !proceed:
          return NodeOperationResult.Fail("用户取消(diff warning)")
  (现有 clone + checkout + upsert ScannedNode 流程)
  return NodeOperationResult.Ok(headSha)

NodeInstallDiffService.CheckAsync(env, reqs, ct):
  result = await _processRunner.RunAsync(
    env.PythonExecutable,
    new[] { "-m", "pip", "list", "--format=json" },
    TimeSpan.FromSeconds(15), ct)
  if !result.Ok: return NodeInstallDiffReport.Empty  // 静默跳过
  installed = JsonSerializer.Deserialize<List<PipJsonRow>>(result.Stdout)
              ?? new List<PipJsonRow>()
  installedMap = installed.ToDictionary(p => p.name.ToLowerInvariant(), p => p.version)
  entries = new List<DiffEntry>()
  foreach req in reqs:
    if !installedMap.TryGetValue(req.Name.ToLowerInvariant(), out var installedVer):
      entries.Add(DiffEntry.New(req.Name, req.Specifier))  // New
      continue
    if req.IsSatisfiedBy(installedVer): continue  // NoChange,不入 entries
    reqMin = req.MinVersion  // 已 parse 出的最低版本, null 表示无下限
    installedV = Version.Parse(installedVer) if parsable else null
    reqMinV = Version.Parse(reqMin) if reqMin != null else null
    category = Classify(req, installedVer, installedV, reqMinV)
    entries.Add(new DiffEntry(req.Name, category, installedVer, req.Specifier))
  return new NodeInstallDiffReport(entries)

Classify 决策:
  reqMinV == null → 无下限,installed 不满足 → Conflict(spec 不含 installed)
  installedV == null → installed 无法 parse → Conflict
  installedV > reqMinV 且 req.MinVersion 满足 → Upgrade
  installedV > reqMinV 但 req.UpperBound != null 且 installedV > req.UpperBound → Conflict
  installedV < reqMinV → Upgrade(要升)
  req.UpperVersion != null && installedV > req.UpperVersion → Downgrade(要降到 <= upper)
  其他 → Upgrade / Downgrade 看具体区间
```

简化:`DiffService` 用 v0.6.7.4 `PipRequirement.IsSatisfiedBy(installedVer)` 判定是否满足 spec;不满足再细分 Upgrade / Downgrade / Conflict:

| 情形 | Category |
|---|---|
| 不在 installed | New |
| `IsSatisfiedBy(installedVer)` | NoChange(不进 Entries) |
| `installedVer` 可 parse 且 spec 有 `MinVersion`,`installedVer < spec.MinVersion` | Upgrade |
| `installedVer` 可 parse 且 spec 有 `UpperVersion`,`installedVer > spec.UpperVersion` | Downgrade |
| spec 同时约束上下(复合 spec)且 installed 在 spec 外 | Conflict |
| 无法 parse installed 或无法判定 | Conflict(防御性) |

---

## UI

### `NodeInstallDiffWarningDialog.xaml`

```xaml
<Window Title="依赖变更警告" Height="380" Width="560"
        Background="{StaticResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize">
  <Grid Margin="16">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto" />   <!-- 标题 + 正文 -->
      <RowDefinition Height="*" />      <!-- DataGrid -->
      <RowDefinition Height="Auto" />   <!-- 按钮 -->
    </Grid.RowDefinitions>

    <StackPanel Grid.Row="0">
      <TextBlock Text="依赖变更警告" FontSize="16" FontWeight="Bold"
                 Foreground="#FFC62828" Margin="0,0,0,8" />
      <TextBlock TextWrapping="Wrap">
        即将安装节点 <Run Text="{Binding NodePackage}" FontWeight="Bold" />
        会对 env `<Run Text="{Binding EnvName}" />` 的 pip 依赖产生以下降级或冲突。
        安装可能导致 ComfyUI 运行异常,请确认是否继续。
      </TextBlock>
    </StackPanel>

    <DataGrid Grid.Row="1" ItemsSource="{Binding Warnings}"
              AutoGenerateColumns="False" IsReadOnly="True"
              Margin="0,12,0,12" HeadersVisibility="Column">
      <DataGrid.Columns>
        <DataGridTextColumn Header="包名" Binding="{Binding Name}" Width="*" />
        <DataGridTextColumn Header="类别" Binding="{Binding CategoryLabel}" Width="100" />
        <DataGridTextColumn Header="当前版本" Binding="{Binding FromVersionDisplay}" Width="120" />
        <DataGridTextColumn Header="将变为" Binding="{Binding ToVersionDisplay}" Width="120" />
      </DataGrid.Columns>
    </DataGrid>

    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Content="取消" Command="{Binding CancelCommand}"
              Style="{StaticResource MaterialButton}" Width="80" />
      <Button Content="仍然安装" Command="{Binding ProceedCommand}"
              Style="{StaticResource MaterialButton}" Margin="8,0,0,0" Width="100" />
    </StackPanel>
  </Grid>
</Window>
```

- `NodeInstallDiffWarningViewModel`:
  - ctor(`NodeInstallDiffReport report, string nodePackage, string envName`)
  - `Warnings` (ObservableCollection<DiffEntry>) — 分类颜色 Downgrade 红 / Conflict 橙
  - `CancelCommand` / `ProceedCommand` → 设 `Result = Proceed` / `false` → `CloseRequested?.Invoke()`
  - `CloseRequested` event — caller 拿 Proceed 走
  - `CategoryLabel` / `FromVersionDisplay` / `ToVersionDisplay` — computed props 给 DataGrid

### 调用点

`NodeOperations.InstallAsync` 内 modal 调用(伪代码,实际用 `Window.ShowDialog()` 同步阻塞):

```csharp
var dlg = new NodeInstallDiffWarningDialog(
    new NodeInstallDiffWarningViewModel(report, nodeId, env.Name));
dlg.ShowDialog();
if (!dlg.ViewModel.Proceed)
{
    _logger?.Info("node-install", $"env='{envId}' node='{nodeId}' 用户取消 diff warning");
    return NodeOperationResult.Fail("用户取消(diff warning)");
}
```

> **测试可达性:** `InstallDialog` 风格 — `NodeOperations` 持有 `Func<NodeInstallDiffReport, Environment, string, bool>` 的 DI seam(默认实现 = `ShowDiffWarningDialog`),测试可注入 mock。**不**直接把 modal 调用写进 InstallAsync。

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs` | ~25 | DTO + DiffCategory enum |
| `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs` | ~20 | DTO + Warnings computed prop + Empty factory |
| `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs` | ~120 | `CheckAsync(env, reqs, ct)` + `Classify` helper + private `PipJsonRow` DTO |
| `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs` | ~60 | VM + commands + computed props |
| `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml` + `.xaml.cs` | ~80 | modal |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs` | ~150 | 6 测试 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs` | ~80 | 3 测试 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs` | ~120 | 3 集成测试 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | ctor 加 `NodeInstallDiffService _diffService` + `Func<NodeInstallDiffReport, Models.Environment, string, bool>? _showDiffDialog`;`InstallAsync` 尾随参数加 `IReadOnlyList<PipRequirement>? catalogPipReqs = null, CancellationToken ct = default`(已有 ct 是默认)— **注意:** 现有签名是 `(envId, nodeId, repoUrl, string? targetTag = null, CancellationToken ct = default)`,加新尾参 `IReadOnlyList<PipRequirement>? catalogPipReqs = null` 必须在 ct 之前避免歧义 → 把 ct 移到 `catalogPipReqs` 之后或同时加两个尾参。**选择:** 加 `IReadOnlyList<PipRequirement>? catalogPipReqs = null` 在 `targetTag` 之后、`ct` 之前(保持 `ct` 在最尾,跟 WPF 约定一致)。`InstallAsync` 内逻辑:1) require env 2) **如果 catalogPipReqs != null && count > 0 且 env.PythonExecutable 存在:** 调 `_diffService.CheckAsync(env, catalogPipReqs, ct)`;如果有 warnings → 调 `_showDiffDialog?.Invoke(report, env, nodeId) ?? false`(默认是 null → 跳过 modal,只 log)— 实际默认实现 `_showDiffDialog = ShowDiffWarningDialogImpl` 在 ctor 里赋值 |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | 同文件:加 `ShowDiffWarningDialogImpl(report, env, nodeId)` private static 真正创建 + ShowDialog |
| `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` | `_ops.InstallAsync(envId, Entry.Package, repoUrl)` → `_ops.InstallAsync(envId, Entry.Package, repoUrl, targetTag: null, catalogPipReqs: Entry.PipRequirements, ct: default)` |

### Keep (unchanged)

- `CatalogViewModel.DownloadAsync`(catalog 是 download-only,不动)— Feature B 也不动 catalog
- `NodeOperations.DownloadAsync`(本地下载,跟 env 解耦,不做 diff 检查)
- v0.6.7.4 T1-T4 + final R1 commit(不动 CatalogRepository 等)
- `EnvCreatorService` 端口分配(Feature B 单独 SDD)

---

## Constraints

| # | Constraint | Reason |
|---|---|---|
| G1 | `InstallAsync` 加新尾参 `catalogPipReqs` 在 `targetTag` 之后、`ct` 之前;`ct` 永远在最尾 | 现有调用点 4 处不传 `targetTag` / `ct`,加新尾参不破坏 — 但需要核对 4 处 caller 的命名实参 |
| G2 | `NodeInstallDiffService` 不抛(失败 → Empty report) | pip list 失败 / 超时不应该阻塞 install,静默跳过 |
| G3 | Modal 只在 Downgrade + Conflict ≥ 1 时弹 | New / Upgrade 不弹,符合用户"针对降级"的明确意图 |
| G4 | Modal 调用走 DI seam(`Func<...>`),默认实现是真 dialog | 测试可注入 mock 返回 true/false |
| G5 | 既有 `InstallAsync` 测试(无 `catalogPipReqs`)0 改动 | 默认 `null` → 跳过 diff,行为与现状完全一致 |
| G6 | 不 bump version / 不发 release zip / 无 ledger 提交 | per `feedback_no_rebuild_zip.md` |
| G7 | 中文 UI 文案(标题 + 正文 + 按钮),跟现有 dialog 一致 | i18n 不变 |
| G8 | 不改 `NodeOperations.DownloadAsync`(本地下载不需要 diff) | 隔离 |
| G9 | `pip list` 命令 timeout = 15s | env venv 慢启动不要阻塞 install 太久 |
| G10 | `pip list --format=json` 输出大小 < 几 KB,parse 用 `System.Text.Json`(项目已用) | 不引新依赖 |
| G11 | DiffEntry 的 `FromVersion`/`ToVersion` 来自 raw catalog specifier 字符串,不做归一化(用户看原始 spec)| 透明 |
| G12 | `InstallAsync` 改签名后 grep 全代码库确认 4 处 caller 仍编译 | 防漏 |

### `IProcessRunner` 抽取(决定)

- 如果 `GitRunner` 已经实现 `IProcessRunner` 接口 → 直接复用
- 否则**不**在 v0.6.7.5 抽接口,改在 `NodeInstallDiffService` ctor 注入 `Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>>`(process runner func),`App.cs` 注入 lambda 包 `Process.Start` — 这样不污染 `GitRunner`,defer 抽接口到下次有需要
- 最终方案见 plan T1 决定

---

## Open questions

无(脑暴已全清)。

---

## Tasks plan

### Task 1: `NodeInstallDiffService` + DiffEntry + DiffReport + 6 tests

- 创建 `DiffEntry.cs` + `DiffCategory` enum
- 创建 `NodeInstallDiffReport.cs`(含 `Warnings` computed prop + `Empty` factory)
- 创建 `NodeInstallDiffService.cs` + 注入 process runner func
- 创建 `tests/.../NodeInstallDiffServiceTests.cs`(6 测试)

### Task 2: `NodeInstallDiffWarningDialog` XAML + VM + 3 tests

- 创建 `NodeInstallDiffWarningViewModel.cs`
- 创建 `NodeInstallDiffWarningDialog.xaml` + `.xaml.cs`
- 创建 `tests/.../NodeInstallDiffWarningDialogTests.cs`(3 测试 — VM only,无 WPF)

### Task 3: `NodeOperations.InstallAsync` 接 diff + modal 弹窗 + 3 集成 tests

- `NodeOperations.cs`:ctor 加 `_diffService` + `_showDiffDialog` seam;`InstallAsync` 签名 + 逻辑(尾参 + clone 前 diff check)
- 创建 `tests/.../NodeOperationsInstallDiffTests.cs`(3 测试,注入 fake diffService + fake showDialog)

### Task 4: `InstallDialogViewModel` 传 `Entry.PipRequirements` + close-out + 全量 suite + staging rebuild

- `InstallDialogViewModel.cs`:`InstallAsync` 调 `_ops.InstallAsync` 时传 `Entry.PipRequirements`
- `App.cs`:DI container 注册 `NodeInstallDiffService` 单例
- `dotnet build` 0/0
- `dotnet test` 649 + 12 = 661 / 0 / 1
- 重建 staging per `feedback_staging_self_contained.md`
- 无 v-bump / 无 zip

---

## Risks

| 风险 | 缓解 |
|---|---|
| `pip list` 失败被静默 → 用户期望警告但没看到 | AppLogger INFO 记 `_diffService.CheckAsync` start / skip / done,符合 v0.6.5.13 模式 — 用户查 Logs/ 可发现 |
| pip list 输出格式变化(未来 pip 升级) | `--format=json` 自 pip 9.0 (2017) 起 stable,预期不变 |
| Modal 在 4 个 `InstallAsync` 调用点都被触发,UX 重复 | 1 个 UI entry(InstallDialog)— `InstallDialogViewModel.InstallAsync` 是唯一 caller;`NodeOperations.InstallAsync` 内 modal 触发由调用方控制(传 `catalogPipReqs != null`) |
| `PipRequirement.MinVersion` / `UpperVersion` 当前可能为 null(简化 PEP 440) | Classify 决策表里 null 全部走 Conflict(防御性) |
| `InstallAsync` 签名变更漏 grep caller → 编译失败 | T4 编译 0/0 + full suite + grep 全代码库 |
| DiffService 跑 `python.exe` 而不是 venv python → 拿到 base python 的 pip list(env 隔离失败) | `env.PythonExecutable` 是 venv 路径(`EnvCreatorService` 写 `python.exe` 到 venv 内)— T1 测试要明确传 venv 路径,验证拿到 venv 自己的 pip(用 FakeProcessRunner) |

---

## Verification

### 单元测试

| 测试 | 验证 |
|---|---|
| `NodeInstallDiffServiceTests.CheckAsync_NewPackage_NotInWarnings` | reqs=[{name:foo, spec:null}] + installed={} → Warnings.Count == 0 |
| `CheckAsync_Upgrade_NotInWarnings` | reqs=[{name:foo, spec:">=2"}] + installed={foo:1.0} → Warnings.Count == 0(Upgrade 静默) |
| `CheckAsync_Downgrade_AddedToWarnings` | reqs=[{name:foo, spec:"<=1.5"}] + installed={foo:2.5} → Warnings=[{name:foo, Category:Downgrade, From:2.5, To:<=1.5}] |
| `CheckAsync_Conflict_AddedToWarnings` | reqs=[{name:foo, spec:"<1"}] + installed={foo:2.0} → Warnings=[{name:foo, Category:Conflict, From:2.0, To:<1}] |
| `CheckAsync_EmptyCatalogReqs_EmptyReport` | reqs=[] → Entries.Count == 0 && Warnings.Count == 0 |
| `CheckAsync_PipListFails_EmptyReport_NoThrow` | FakeProcessRunner 返 exit != 0 → Report.Empty,无异常 |

| 测试 | 验证 |
|---|---|
| `NodeInstallDiffWarningDialogTests.Vm_Ctor_PopulatesWarnings` | ctor 传 report + nodePackage + envName → Warnings 数 == report.Warnings.Count |
| `Vm_CancelCommand_SetsProceedFalse_TriggersCloseRequested` | 触发 Cancel → Proceed == false + CloseRequested fired |
| `Vm_ProceedCommand_SetsProceedTrue_TriggersCloseRequested` | 触发 Proceed → Proceed == true + CloseRequested fired |

| 测试 | 验证 |
|---|---|
| `NodeOperationsInstallDiffTests.InstallAsync_WithDiffWarnings_UserCancels_DoesNotClone_ReturnsFail` | catalogPipReqs=[{name:torch,spec:"<=1"}] + installed={torch:2.0} → showDialog 返 false → NodeOperationResult.Fail("用户取消(diff warning)") + ScannedNode 0 行 |
| `InstallAsync_WithDiffWarnings_UserProceeds_ClonesNormally` | 同上 + showDialog 返 true → git clone 跑 + ScannedNode 1 行 |
| `InstallAsync_NoCatalogPipReqs_SkipsDiffCheck_BehavesLikeOriginal` | catalogPipReqs=null → diffService 不被调 + showDialog 不弹 + 走完 clone |

### 全量

- `dotnet build` 0 errors / 0 warnings
- `dotnet test` 661 PASS / 0 FAIL / 1 SKIP(649 + 12,SKIP = LiveFetch real GitHub)
- 既有 `InstallAsync` 测试 0 改动通过(默认 `catalogPipReqs=null`)

### 端到端桌面(用户测)

1. 启动 staging exe
2. 侧栏"环境" → 选一个 env(stopped + BED done)→ 行内"安装节点"按钮
3. CatalogEntryPicker → 选一个有 `pip` 需求的 catalog entry
4. InstallDialog 开 → 选 env → 点 Install
5. **若** env 装了高于 spec 要求的包:弹 modal 警告 + 列冲突 / 降级
   - 点 [取消] → 回到 dialog,无 git clone
   - 点 [仍然安装] → git clone 跑 → ScannedNode 写入 → dialog 关
6. **若** 无冲突 / 降级:无 modal,直接 clone
7. 侧栏 Logs/ 看 `[node-install]` INFO 行(start / 用户取消 / 完成)

---

## Carry forward(不做)

- `pip install --dry-run` post-clone 真实 pip resolver 预测
- 跨节点 collision 检测(节点 vs 其他已装节点的需求)
- pip snapshot 缓存(env-start 时落库)
- 用户配置:关闭 diff 检查的 settings 开关(本期不暴露)
- 非 catalog 装(custom URL)的 diff 检查(本期跳)