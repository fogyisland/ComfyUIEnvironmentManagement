# v0.6.5.9 Plan: Catalog 主页「下载」到本地节点目录

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Catalog 主页(`Views/CatalogView.xaml`)详情面板的「安装」按钮改成「下载」,下载目标 = Settings 新增的「本地节点目录」字段(默认 `<projectRoot>/local-nodes/`);纯 git clone,不写 ScannedNode,不绑 env。EnvList 行内「安装节点」流程(走 CatalogEntryPickerDialog → InstallDialog)完全不动。

**Architecture:**
- 新 `Settings.LocalNodeDirectory` 字段 + `SettingsDefaults.LocalNodesSubdir = "local-nodes"` 常量(template-style 默认子目录名)
- 新 `SettingsViewModel.LocalNodeDirectory` 属性 + SettingsView 「路径」section 新 UI 行 + Browse 按钮
- 新 `NodeOperations.DownloadAsync(localDir, nodeId, repoUrl, targetTag?)` 方法:git clone + 可选 checkout tag,不查 env、不写 ScannedNode
- 改写 `CatalogViewModel`:`InstallCommand` / `InstallButtonLabel` → `DownloadCommand` / `DownloadButtonLabel`,删 `EnvironmentRepository` 注入,目标 = `_settings.LocalNodeDirectory`
- 改 `CatalogView.xaml`:1 处 binding 改 `Download*`
- 改 `MainViewModel` ctor:删 `envRepo` 实参(CatalogViewModel 不再需要)

**Tech Stack:** WPF .NET 8 / C# 12 · `Microsoft.Win32.OpenFolderDialog` · xUnit

## Context

v0.6.5.8 P0-A (BED installing 状态写活 + 启动 reconciliation) T1 完成、T2/T3 deferred(等本 spec 完成后再回来收尾)。

本 spec 是用户 GUI smoke 阶段新提的问题:Catalog 主页「安装」按钮盲选 envs[0] + 名字误导(实际只 git clone)。修复路径:
1. 改名「安装」→「下载」
2. 删 envs[0] 盲选,改成 Settings.LocalNodeDirectory
3. 不写 ScannedNode(下载是纯文件,跟 env 解耦)

**base SHA:** `c6d890e`(v0.6.5.8 T1 fix 提交,on top of v0.6.5.8 plan `0f17333` / spec `a5b3361` / T1 `3dae3d6`)

**spec:** `docs/superpowers/specs/2026-08-05-catalog-local-download-design.md`(本 plan 的 source of truth)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `Settings.LocalNodeDirectory` 字段名固定,`[JsonPropertyName("local_node_directory")]` | spec §3 |
| G2 | 默认值 = `SettingsDefaults.LocalNodesSubdir = "local-nodes"`(template-style,跟 TemplatePythonDir 同类,空字段自动填子目录名),运行时 `Path.Combine(projectRoot, settings.LocalNodeDirectory)` 解析 | spec §4.1 |
| G3 | `SettingsDefaults.Apply` 已存在的 `MigrateOnly` 逻辑适用:绝对路径若在 projectRoot 下 → 转相对,否则保留 | spec §4.1 + 现有 `SettingsDefaults.MigrateOnly` |
| G4 | `NodeOperations.DownloadAsync` 失败语义跟 `InstallAsync` 完全一致:用户取消 → "用户取消",git 退出非零 → stderr 首行,启动失败 → 异常消息 | spec §4.3 |
| G5 | `NodeOperations.DownloadAsync` **不写** `ScannedNode` row(纯文件下载,跟 env 解耦) | spec §1 G3 + G4 |
| G6 | `CatalogViewModel` 删 `EnvironmentRepository` 注入;`MainViewModel` 同步删实参 | spec §4.4 |
| G7 | `InstallCommand` / `InstallButtonLabel` → `DownloadCommand` / `DownloadButtonLabel`(全文 rename,不留 alias) | spec §4.4 |
| G8 | 老 `CatalogEntryPickerDialog` / `InstallDialog` / `InstallNodeCommand` / `EnvListVM` / `NodeOperations.InstallAsync` 完全不动 —— 「下载」跟「安装到 env」是两条正交路径 | spec §1 G6 + §2 |
| G9 | `SettingsView` 新 UI 行放在 `GlobalNodesDir` 行**之后**(「路径」section 内),TextBox + 浏览按钮 layout 跟 `EnvsDir`/`GlobalNodesDir` 完全一致 | spec §4.2 |
| G10 | 无 version bump,无 release zip,无 ledger commit(per `feedback_no_zip.md` + v0.6.5.8 P0-A hotfix 偏好一致) | spec §8 + memory |
| G11 | `App.xaml.cs:44` `SettingsDefaults.Apply(settings, projectRoot)` 后可选加 `Directory.CreateDirectory(Path.Combine(projectRoot, settings.LocalNodeDirectory))`,确保首次启动预创建 | spec §4.1 + 现有 `App.xaml.cs` |
| G12 | 测试沿用 `tests-wpf/.../Services/NodeOperationsTests.cs` 的真 git + 本地 bare repo pattern(`InitRepoPair` / `FindGit()` helper,无 git 环境降级),不引入 `FakeGitRunner`(plan 初稿误述 `FakeGitRunner` 已有,实则 `GitRunner` 是 `sealed` 且无接口;抽出 `IGitRunner` 会波及 12 处生产代码,不在本 spec 范围) | spec §6.2 + NodeOperationsTests 现有 pattern |
| G13 | `NodeOperations.DownloadAsync` 是 `public virtual`(跟 `InstallAsync` 一致,允许测试 subclass override `GitRunner` 行为) | spec §4.3 + 现有 `NodeOperations.cs` 风格 |
| G14 | `NodeOperations.DownloadAsync` 入口对 `localDir` 空做 early-return Fail,跟 `InstallAsync` 对 `envId` 的 `RequireEnv` 抛异常风格不同(后者是抛,前者是返 Fail,因为本地目录配置错误对用户更友好应该 InfoMessage 提示而不是 throw) | spec §4.3 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsDownloadTests.cs` | ~200 | `DownloadAsync_*` 系列(7 tests),用现有 `FakeGitRunner` pattern |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsLocalNodeDirectoryTests.cs` | ~80 | `LocalNodeDirectory_*` 系列(4 tests:default / persists / absolute-under-root migrates / absolute-outside-root keeps) |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 「路径」section 加 `[JsonPropertyName("local_node_directory")] public string LocalNodeDirectory { get; set; } = "";` |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | `public const string LocalNodesSubdir = "local-nodes";` + `Apply` 末尾加 `s.LocalNodeDirectory = Resolve(s.LocalNodeDirectory, LocalNodesSubdir, projectRoot);` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `Apply` + `Save` 之后加一行 `Directory.CreateDirectory(Path.Combine(projectRoot, settings.LocalNodeDirectory));`(try/catch,失败静默) |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | `+ LocalNodeDirectory` 属性(读 `_settings.LocalNodeDirectory`,setter 写并 Save);`RaiseAllPropertiesChanged` 加一行 |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 「路径」section 在 `GlobalNodesDir` 行后加 `<TextBlock Text="本地节点目录..."/>` + `<DockPanel>`(Browse 按钮 + TextBox) |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` | `+ BrowseLocalNodeDir` handler,跟 `BrowseGlobalNodesDir` 同 pattern |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | `+ DownloadAsync(localDir, nodeId, repoUrl, targetTag?, ct?)` 方法(在 `InstallAsync` 之后) |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | 删 `EnvironmentRepository envRepo` ctor 参 + `_envRepo` 字段;`InstallCommand` / `InstallButtonLabel` 重命名为 `DownloadCommand` / `DownloadButtonLabel`;`InstallAsync` 方法体重写为 `DownloadAsync`(调 `_nodeOps.DownloadAsync(Path.Combine(projectRoot, _settings.LocalNodeDirectory), ...)`) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | `CatalogViewModel` 构造调用处删 `envRepo` 实参;若 ctor 注释提到 catalog 用 envRepo,删掉 |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 详情面板按钮:`Content="{Binding InstallButtonLabel}"` → `"{Binding DownloadButtonLabel}"`,`Command="{Binding InstallCommand}"` → `"{Binding DownloadCommand}"` |

### Delete

无。

### Keep (unchanged)

- `NodeOperations.InstallAsync` + `UpgradeAsync` + `RollbackAsync` + `ScanAsync` + `Lock/Unlock/Enable/Disable`
- `NodeRepository` / `ScannedNode`(DownloadAsync 不写)
- `CatalogEntryPickerDialog` + `InstallDialog` + `InstallNodeCommand`(env 流程)
- `EnvironmentListViewModel` 操作列 + EnvList 行内安装节点按钮
- `SettingsRepository` / `SettingsViewModel` 其它字段 + UI
- `App.xaml.cs` 启动 reconciliation(v0.6.5.8 范围,本 spec 不动)

---

## Tasks

### Task 1: Settings.LocalNodeDirectory 字段 + 默认值 + UI + 4 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:23-28`(在「路径」section 加一行)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs`(常量 + Apply 一行)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:44`(Apply 后加 CreateDirectory,try/catch 静默)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(+ 属性 + RaiseAllPropertiesChanged 一行)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(在 GlobalNodesDir 行后加 UI)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(+ handler)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsLocalNodeDirectoryTests.cs`(4 tests)

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Settings.LocalNodeDirectory` (string, default `""`)
  - `SettingsDefaults.LocalNodesSubdir = "local-nodes"`
  - `SettingsViewModel.LocalNodeDirectory` 属性
  - SettingsView.xaml 「本地节点目录」UI 行

- [ ] **Step 1: Write failing tests** (verbatim from spec §6.1)
- [ ] **Step 2: Run tests, verify FAIL**
- [ ] **Step 3: Add `LocalNodeDirectory` to Settings.cs**
- [ ] **Step 4: Add `LocalNodesSubdir` constant + Apply line to SettingsDefaults.cs**
- [ ] **Step 5: Add `LocalNodeDirectory` property to SettingsViewModel.cs**
- [ ] **Step 6: Add UI row to SettingsView.xaml + handler to SettingsView.xaml.cs**
- [ ] **Step 7: Add `Directory.CreateDirectory` to App.xaml.cs (try/catch 静默)**
- [ ] **Step 8: Run tests, verify PASS**(4/4)
- [ ] **Step 9: Run full suite, verify no regressions**(基线 362 + 4 = ~366)
- [ ] **Step 10: Commit** `feat(wpf): Settings.LocalNodeDirectory 字段 + UI + 默认子目录`

---

### Task 2: `NodeOperations.DownloadAsync` 方法 + 7 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`(在 `InstallAsync` 之后加 `DownloadAsync`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsDownloadTests.cs`(7 tests)

**Interfaces:**
- Consumes: 现有 `NodeOperations` 字段(`_git` / `_settings`)
- Produces:
  ```csharp
  public virtual async Task<NodeOperationResult> DownloadAsync(
      string localDir, string nodeId, string repoUrl,
      string? targetTag = null,
      CancellationToken ct = default);
  ```
  语义:纯 git clone(可选 checkout tag),不查 env,不写 ScannedNode,失败语义跟 InstallAsync 一致。

- [ ] **Step 1: Write failing tests** (verbatim from spec §6.2 — 7 tests,用真 git + 本地 bare repo pattern):
  - `DownloadAsync_ClonesRepoIntoLocalDir`
  - `DownloadAsync_DoesNotWriteScannedNode`
  - `DownloadAsync_TargetTag_ChecksOutAfterClone`
  - `DownloadAsync_DirAlreadyExists_ReturnsFail`
  - `DownloadAsync_LocalDirEmpty_ReturnsFail`
  - `DownloadAsync_GitFails_CleansUpEmptyDirAndReturnsFail`
  - `DownloadAsync_UserCancels_ReturnsCancelReason`
- [ ] **Step 2: Run tests, verify FAIL**
- [ ] **Step 3: Implement `DownloadAsync`**(verbatim from spec §4.3 伪代码,跟 `InstallAsync` 同结构)
  - 复用 `TryDelete` / `FirstLine` / `TryReadHeadShaAsync` 现有 helper
  - 入口对 `localDir` 空做 early-return `Fail("本地节点目录为空,请先在 Settings 配置")`
- [ ] **Step 4: Run tests, verify PASS**(7/7)
- [ ] **Step 5: Run full suite, verify no regressions**(基线 ~366 + 7 = ~373)
- [ ] **Step 6: Commit** `feat(wpf): NodeOperations.DownloadAsync 纯 git clone 无 env 无 ScannedNode`

---

### Task 3: `CatalogViewModel` 改写 + `CatalogView.xaml` binding 改 + 1 测试 + verify + staging rebuild

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(删 envRepo、Install→Download rename、目标 = Settings.LocalNodeDirectory)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(删 envRepo 实参)
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(1 处 binding 改 Download)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs`(若有,把 `InstallCommand` 引用同步改 `DownloadCommand` —— 1 test)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelDownloadTests.cs`(1+ test:`DownloadCommand_TargetsLocalDir`,fake `NodeOperations` 抓 DownloadAsync 调用参数)

**Interfaces:**
- Consumes:
  - T1 的 `Settings.LocalNodeDirectory`
  - T2 的 `NodeOperations.DownloadAsync`
  - `App.xaml.cs` 已传 `projectRoot` 到 `_mainVm`(`MainViewModel` ctor 已有 `projectRoot` 参数)
- Produces:
  - `CatalogViewModel.DownloadCommand` + `DownloadButtonLabel` + `DownloadAsync`(VM 私有方法)

- [ ] **Step 1: Write failing test** (verbatim — `DownloadCommand_TargetsLocalDir`)
- [ ] **Step 2: Run test, verify FAIL**
- [ ] **Step 3: Refactor `CatalogViewModel`**:
  - 删 ctor `EnvironmentRepository envRepo` 参数 + `_envRepo` 字段
  - `InstallCommand` / `InstallButtonLabel` / `InstallAsync` 重命名为 `DownloadCommand` / `DownloadButtonLabel` / `DownloadAsync`
  - `DownloadButtonLabel` getter:`_selectedVersion is null ? "下载" : $"下载 {_selectedVersion.Tag}"`
  - `DownloadAsync`(VM 方法)行为:
    1. `ExtractRepoUrl(entry)` 拿 repoUrl(已有)
    2. 空 → ErrorMessage + return
    3. `localDir = Path.Combine(projectRoot, _settings.LocalNodeDirectory)`,空 → ErrorMessage「请先在 Settings 配置」+ return
    4. 调 `_nodeOps.DownloadAsync(localDir, entry.Package, repoUrl, SelectedVersion?.Tag)`
    5. 成功 → InfoMessage `已下载 {entry.Package} → version={result.Version}`
    6. 失败 → ErrorMessage `下载失败:{result.Reason}`
  - `LoadVersionsForSelected` + `Selected` setter 里 `RaisePropertyChanged(nameof(InstallButtonLabel))` → `DownloadButtonLabel`
- [ ] **Step 4: Run test, verify PASS**
- [ ] **Step 5: Update `MainViewModel.cs` ctor**(删 `envRepo` 实参;若 ctor 注释提到 catalog 用 envRepo,删掉)
- [ ] **Step 6: Update `CatalogView.xaml`**(1 处 binding 改 Download)
- [ ] **Step 7: Update `CatalogViewModelTests.cs` if exists**(InstallCommand → DownloadCommand rename)
- [ ] **Step 8: Run full suite, verify ~373+ PASS / 0 FAIL / 1 SKIP**
- [ ] **Step 9: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`** → 0 errors, 0 warnings
- [ ] **Step 10: Rebuild staging** per `feedback_staging_self_contained.md`:
  ```bash
  dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
      -c Release -r win-x64 --self-contained true \
      -o "release/staging/ComfyUI Manager"
  ```
- [ ] **Step 11: Commit** `refactor(wpf): Catalog 主页 InstallCommand → DownloadCommand 目标 Settings.LocalNodeDirectory`

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 ~373 PASS / 0 FAIL / 1 SKIP(基线 362 + 11 新)
  - 增量:`SettingsDefaultsLocalNodeDirectoryTests` 4 + `NodeOperationsDownloadTests` 7 + `CatalogViewModelDownloadTests` 1 - 1 老 test rename ≈ 11 新

### 端到端手动测试(用户 desktop,per `feedback_no_zip.md` 走 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`)

1. 双击启动 → Settings → 「路径」section → 看到「本地节点目录」,TextBox 预填 `<exe-dir>/local-nodes`
2. 侧栏 Catalog → 刷新 → 选一个节点 → 详情面板按钮文字 = `下载 {version}` 或 `下载`
3. 点「下载」→ 几秒后 InfoMessage `已下载 {pkg} → version={sha}`
4. 打开 `<exe-dir>/local-nodes/{pkg}/` 文件夹 → 看到节点代码
5. 再次点同一个节点「下载」→ ErrorMessage `已存在:<...>`
6. Settings 清空本地节点目录 → 回到 Catalog 点「下载」→ 提示「本地节点目录为空,请先在 Settings 配置」
7. 选不同节点试不同 version(下拉切换)→ 都下到同一个 local-nodes 目录

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| `CatalogViewModel` 删 envRepo 注入 → MainViewModel ctor 编译断 | MainViewModel 同步删实参即可 |
| 老 `CatalogViewModelTests` 若有 `InstallCommand` 引用 → 编译断 | Step 7 同步 rename(应该只有 1-2 处引用) |
| `NodeOperations.DownloadAsync` 不写 ScannedNode → 用户重启 app 看不到下载历史 | 设计如此:本 spec 不在范围追踪已下载节点;用户去本地文件夹看 |
| `LocalNodeDirectory` 默认 `./local-nodes/`(相对路径)→ 跨机器迁移需 settings.json 不带绝对路径 | 跟 `TemplatePythonDir` / `TemplateComfyuiDir` 同语义,`MigrateOnly` 自动处理 |
| `App.xaml.cs` 启动时 `Directory.CreateDirectory` 失败(权限/盘满)→ 用户首次点下载还会再 CreateDirectory 兜底,不影响功能 | 启动 CreateDirectory 失败静默,运行时再 CreateDirectory 兜底 |
| v0.6.5.8 P0-A T2/T3 未完成 → 多个未完成 PR 叠加 | 本 spec 完全独立文件集,跟 P0-A 不冲突;P0-A T2/T3 后续恢复 |

### Critical files to modify

(汇总在 §Critical files to modify,见 spec §10)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 3 task + 1 close-out = ~4 dispatch
- Per-task review gate(sonnet implementer + sonnet reviewer)
- 最后 T3 自带 verify + staging rebuild

(Plan agent left out: design constraints 已经由用户的"本地节点目录"明确决定 + spec 已经写了。Skipping redundant design pass.)

If this plan is relevant to the current work and not already complete, continue working on it.
