# 「ComfyUI Manager 独立按键 (toggle 装/卸)」feature design

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** env-list 操作列 row 1 加 ComfyUI Manager toggle 按钮(已装=卸,未装=装),检测 `<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager`;「装依赖」末尾自动装(只装不卸);进度复用 v0.6.5.15 inline 状态面板。

**Architecture:** 新 `ComfyUIManagerInstaller` service(克隆 + pip install -r Manager/requirements.txt + 卸载/检测),新 `RequirementsFileInstaller` 公共 helper(从 `RequirementsInstaller` 抽出过滤+pip 逻辑两边复用);`EnvironmentListViewModel` 加 toggle command + computed button text + 子 mutex;`RequirementsInstaller.InstallAsync` 末尾调 Manager 装(失败 WARN 不阻断);XAML row 1 第 6 按钮 + 第 2 个 inline 状态面板。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 真 git + 真 pip 测试 · 现有 `GitRunner` / `NodeOperationResult` / `RequirementsStatusViewModel` 模式 · 手写 MVVM (ViewModelBase / RelayCommand)

---

## Context

v0.6.11 SDD (T3/T2/T8/T4 SHIP-READY 2026-08-10) 完成后,用户桌面验证时新提需求:
- env-list 操作列加 ComfyUI Manager toggle 独立按键(检测已装=卸,未装=装)
- 「装依赖」自动装 ComfyUI Manager(已装就跳过,不重复装)
- 任务范围修正(2026-08-10):原「装ComfyUI 独立按键」(装 ComfyUI codebase)feature 作废删除;新任务范围 = ComfyUI Manager 独立按键

用户原话(决定性):
- "我们在安装依赖的时候记得把ComfyUI manager 加进去"
- "我们可能需要安装ComfyUI manager 也可能需要卸载ComfyUI manager"
- "如果检测到已安装,我们的安装就变成卸载ComfyUI Manger 如果没有安装就变成安装ComfyUI Manager"
- "我需要安装的不是comfyui 独立按键,而是 ComfyUI Manager 的独立按键"(任务范围修正)
- "装的时候需要注意,我们复制目录之后检查comfyui manager 中的requirements.txt 也需要满足"(Manager 自己的 requirements.txt 要走 pip install)

现有相关:
- `BulkUpdateOrchestrator`(v0.6.11 T8 SHIP-READY `6cf62d8`)已能 git pull `<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager` — 本任务不动 BulkUpdate
- `RequirementsInstaller`(v0.6.5.12 SHIP-READY `9b3c300` + v0.6.5.15 inline 状态面板 `5195398`)跑 `pip install -r requirements.txt`,过滤 torch 行
- `EnvironmentListViewModel`(v0.6.10.2 SHIP-READY `55c41f3`)row 1 操作列 5 按钮(装卸链);v0.6.5.22 hotfix `64189fc` IsEnvBusy mutex
- `EnvironmentListView.xaml` row 1 + row 2 双行布局(560→660 需扩)
- `BaseEnvUninstaller` / `RequirementsUninstaller`(v0.6.5.22 SHIP-READY `64189fc`)删 BED/requirements 状态(参考模式)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | ComfyUI Manager git URL 写死为 `https://github.com/ltdrdata/ComfyUI-Manager`(官方 ltdrdata),不加 Settings 字段 | 用户决策(本 spec §1) |
| **G2** | 检测方式 = `Directory.Exists(<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager)`,per check,**不**加 SQLite 列 | 用户决策(本 spec §1) |
| **G3** | 卸载 = `rm -rf` 整个目录,**不**走 git reset/clean,**不**备份 zip,**不**删 venv 中已装的 Manager Python 依赖 | 用户决策(本 spec §1) |
| **G4** | 按钮形式 = 单按钮 + content binding("安装 ComfyUI Manager" / "卸载 ComfyUI Manager"),不用两个独立按钮 | 用户决策(本 spec §1) |
| **G5** | 进度反馈 = 复用 v0.6.5.15 inline 状态面板模式(RequirementsStatusViewModel 同款),独立 `ComfyUIManagerStatusViewModel`,**不**抽公共基类(YAGNI) | 用户决策 + 现有模式 |
| **G6** | 按钮位置 = row 1 装卸链,「卸载基础环境」之后(row 1 = 6 按钮) | 用户决策(本 spec §1) |
| **G7** | 自动装触发 = `RequirementsInstaller.InstallAsync` 末尾(pip install 成功后),Manager 装失败**不阻断** requirements(只 WARN 日志 + 状态面板提示,可手动 toggle 重试) | 用户决策(本 spec §1) |
| **G8** | busy mutex = 复用 v0.6.5.22 `IsEnvBusy` + 新增 `IsComfyUiManagerBusy` 子 mutex(防止 toggle 装卸期间跟装依赖子步骤 / 同一 env 多次 toggle race) | 项目惯例 v0.6.5.22 |
| **G9** | ComfyUI Manager 自己的 `requirements.txt` 必须 `pip install -r`(过滤 torch 行同 v0.6.5.12),pip 失败回滚 rm -rf 整个 Manager 目录 | 用户原话 "复制目录之后检查comfyui manager 中的requirements.txt 也需要满足" |
| **G10** | 抽 `RequirementsFileInstaller` 公共 helper,给 `RequirementsInstaller` 和 `ComfyUIManagerInstaller` 两边复用(YAGNI 镜像相反 — 避免 30 行过滤逻辑复制) | 本 spec 决策 |
| **G11** | WPF `Setter` 引用 palette brush 必须 property-element + `DynamicResource`(v0.6.9.2 教训 `feedback_wpf_style_setter_dynamic_resource.md`) | 项目惯例 |
| **G12** | 新文件放置:`Services/ComfyUIManagerInstaller.cs`、`Services/RequirementsFileInstaller.cs`、`ViewModels/ComfyUIManagerStatusViewModel.cs`(镜像 `RequirementsStatusViewModel.cs`) | 现有代码结构 |
| **G13** | DI 接线:`App.xaml.cs` 注册 `RequirementsFileInstaller` + `ComfyUIManagerInstaller` 单例;`RequirementsInstaller` ctor 加 `ComfyUIManagerInstaller` 参数 | 现有模式 |
| **G14** | 测试覆盖:真 git + 真 venv python(`NodeOperationsDownloadTests` / `BulkUpdateOrchestratorTests` 同款)+ Fake helper(`CapturingNodeOps` 风格)+ `if (FindGit() is null) return` skip 缺失 | 项目惯例 |
| **G15** | AppLogger INFO 日志 `comfyui-manager-install` / `comfyui-manager-uninstall` 每个 install/uninstall 写一行;失败 WARN/ERROR;Manager 自动装失败 WARN 不阻断 requirements | v0.6.5.13 惯例 |
| **G16** | Service 不抛异常出,所有失败返 `NodeOperationResult` 沿用现有模式 | 项目惯例 |
| **G17** | 不改 `BulkUpdateOrchestrator`(它已能 pull ComfyUI-Manager,scope 独立);不改 `Environment` model / `ScannedNode` / `NodeOperations`;不引入新依赖;不做无关重构 | YAGNI + 项目惯例 |

---

## Task Breakdown

### Task 1: `RequirementsFileInstaller` 公共 helper + 适配 `RequirementsInstaller`

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`(~80 行)
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`(ctor 加参数 + `InstallAsync` 内部改为调 helper)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs`(~80 行,5 测试)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`(ctor 适配)

**Interfaces:**
- Produces: `public sealed class RequirementsFileInstaller { public Task<ProcessResult> InstallAsync(string requirementsFilePath, string venvPythonPath, IProgress<string>? progress, CancellationToken ct); private static string FilterTorchLines(string content); }`

**Verification:**
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~RequirementsInstaller|FullyQualifiedName~RequirementsFileInstaller"   # 全 PASS
```

**Commit:** `refactor(wpf): extract RequirementsFileInstaller helper`

---

### Task 2: `ComfyUIManagerInstaller` service(克隆 + pip + 卸载 + 检测)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs`(~120 行)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs`(~150 行,8-10 测试)

**Interfaces:**
- Consumes: `RequirementsFileInstaller`(T1 产出),`GitRunner` (sealed),` `AppLogger?`
- Produces:
  ```csharp
  public sealed class ComfyUIManagerInstaller {
      public const string DefaultRepoUrl = "https://github.com/ltdrdata/ComfyUI-Manager";
      public const string DirName = "ComfyUI-Manager";
      public bool IsInstalled(Environment env);
      public string? ResolveTargetDirectory(Environment env);
      public Task<NodeOperationResult> InstallAsync(Environment env, IProgress<string>? progress, CancellationToken ct);
      public NodeOperationResult Uninstall(Environment env);
  }
  ```

**Verification:**
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~ComfyUIManagerInstaller"
```

**Commit:** `feat(wpf): add ComfyUIManagerInstaller (clone + pip + uninstall + detect)`

---

### Task 3: `ComfyUIManagerStatusViewModel` + `EnvironmentListViewModel` toggle command

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs`(~50 行,镜像 `RequirementsStatusViewModel`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:
  - EnvRow inner class 加 `IsComfyUiManagerInstalled` / `ComfyUiManagerButtonText` / `ComfyUiManagerStatus` / `ToggleComfyUiManagerCommand`
  - `_comfyUiManagerBusyByEnv` 字典 + `IsComfyUiManagerBusy/MarkComfyUiManagerBusy` 子 mutex
  - `LoadEnvsAsync` 末尾对每 row 计算 `IsComfyUiManagerInstalled` + button text
  - `ToggleComfyUiManagerAsync` 方法(切换逻辑)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs`(~120 行,5-6 测试)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(ctor 适配)

**Interfaces:**
- `public sealed class ComfyUIManagerStatusViewModel : ViewModelBase { public string StatusText; public bool IsVisible; public ObservableCollection<string> LogLines; public event Action? CloseRequested; public void ReportStatus(string); public void ReportComplete(string); public void ReportFailed(string); }`

**Verification:**
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentListViewModel"
```

**Commit:** `feat(wpf): add ComfyUI Manager toggle command + inline status panel`

---

### Task 4: XAML row 1 第 6 按钮 + 第 2 个 inline 状态面板

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`:
  - row 1 WrapPanel 现有 5 按钮后加 `<Button Content="{Binding ComfyUiManagerButtonText}" Command="{Binding ToggleComfyUiManagerCommand}" Style="{StaticResource MaterialButton}" />`
  - row 1 Width 560→660(或 grid 列宽调整)
  - 在 Requirements inline 状态面板 XAML 后追加同款 Border + DataContext 绑 `ComfyUiManagerStatus` + `✕` 关闭按钮 + `LogLines` ItemsControl + `OnComfyUiManagerStatusCloseClicked` 回调
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs`:
  - 加 `OnComfyUiManagerStatusCloseClicked(object, RoutedEventArgs)` 方法
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`:
  - 加 `EnvList_InstallComfyUiManager` / `EnvList_UninstallComfyUiManager` 2 个 key(XAML 直接 hardcode 中文,不接 resx;按 v0.6.10.2 项目惯例)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs`(若存在)STA-thread headless load test 验证 XAML 不崩
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` DI:注册 `RequirementsFileInstaller` + `ComfyUIManagerInstaller` 单例,`RequirementsInstaller` ctor 注入 `ComfyUIManagerInstaller`,`EnvironmentListViewModel` ctor 注入 `ComfyUIManagerInstaller`

**Verification:**
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentList"   # 全 PASS
```

**Commit:** `feat(wpf): ComfyUI Manager toggle button + inline status panel in env-list`

---

### Task 5: `RequirementsInstaller` 末尾自动装 ComfyUI Manager

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`:
  - ctor 加 `ComfyUIManagerInstaller _comfyUiManager` 参数
  - `InstallAsync` 末尾(pip install -r 成功后):
    ```csharp
    progress?.Report("stage:自动装 ComfyUI Manager");
    var cmResult = await _comfyUiManager.InstallAsync(env, progress, ct);
    if (!cmResult.Ok) {
        _logger?.Warn("requirements-install",
            $"env='{env.Id}' ComfyUI Manager 自动装失败(reason={cmResult.Reason}),requirements 已成功");
    }
    ```
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`:
  - ctor 适配 + 新增 2 测试:装依赖成功末尾调 ComfyUIManagerInstaller.InstallAsync / Manager 失败不阻断 requirements

**Verification:**
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~RequirementsInstaller"
```

**Commit:** `feat(wpf): auto-install ComfyUI Manager after 装依赖`

---

## Critical Files (full list)

**Modified (5):**
- `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`(T1 ctor + 内部用 helper;T5 末尾加自动装 Manager)
- `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`(不动 — 仅参考其 git clone + TryDelete 模式)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(T3 加 EnvRow 属性 + 子 mutex + ToggleCommand)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(T4 row 1 第 6 按钮 + inline 状态面板)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs`(T4 OnComfyUiManagerStatusCloseClicked)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`(T4 中文文案 — 实际可能 hardcode)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(T4 DI 接线)

**Created (3 source + 3 test):**
- `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`(T1,~80 行)
- `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs`(T2,~120 行)
- `src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs`(T3,~50 行)
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs`(T1,~80 行)
- `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs`(T2,~150 行)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs`(T3,~120 行)

**Test files modified (3):**
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`(T1 + T5)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(T3)
- `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs`(T4 若存在)

---

## Verification (end-to-end)

按顺序验证 5 task commit 全 PASS:

```bash
# T1 验证
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~RequirementsInstaller|FullyQualifiedName~RequirementsFileInstaller"

# T2 验证(基于 T1)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~ComfyUIManagerInstaller"

# T3 验证(基于 T2)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentListViewModel"

# T4 验证(基于 T3)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentList"

# T5 验证(基于 T4)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~RequirementsInstaller"

# 全套验证
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke(桌面验证,user):**
1. 启动 staging → env-list row 1 有 6 按钮(最后是 "安装 ComfyUI Manager",默认新建 env 未装)
2. 点 "安装 ComfyUI Manager" → inline 面板开,显示 "stage:克隆 ComfyUI Manager" → 几秒后 "stage:安装 ComfyUI Manager 依赖"(若有 requirements.txt)→ "安装成功"
3. 按钮文字变 "卸载 ComfyUI Manager";File Explorer 看 `<env>/ComfyUI/custom_nodes/ComfyUI-Manager/` 有 `.git/` + `requirements.txt` 等文件
4. 点 "卸载 ComfyUI Manager" → 面板显示 "卸载中..." → "卸载成功" → 按钮文字回 "安装 ComfyUI Manager" → 目录已删
5. 点「装依赖」→ requirements 装完后面板最后追加 "stage:自动装 ComfyUI Manager" + 日志行(已装则 "info: 已装,跳过")
6. 暗/亮主题切换 → 按钮 + inline 状态面板颜色跟随(v0.6.9.2 教训 + v0.6.10.2 DynamicResource 沿用)
7. 跑 staging 测装依赖时,toggle 按钮 disabled(busy mutex 生效)

---

## Risks

| 风险 | 缓解 |
|---|---|
| ComfyUI Manager 的 requirements.txt 含非 torch 大依赖(数十 MB)→ 装依赖时间变长 | 用户明确要求,G9 接受;状态面板透明展示给用户 |
| pip install 中途断网 → 部分装 → 回滚全删 → 用户需手动重试 | TryDelete 收尾 + 状态面板提示;重试是 toggle 按钮兜底 |
| junction 损坏 + TryDelete 失败 | 不 throw,允许 Manager dir 留半成品 + ERROR 日志,等下次手动清理 |
| RequirementsInstaller 末尾自动装 Manager 失败但 requirements 成功 → 用户困惑 | WARN 日志 + 状态面板最后一行提示 + toggle 按钮可手动重试 |
| `IsEnvBusy` mutex 已 busy 时 RequirementsInstaller 子步骤 Manager 装也 busy → 死锁 | RequirementsInstaller 跟 EnvironmentListViewModel 共享 `IsEnvBusy` 字典,装依赖时 toggle 自然 disabled;不构成死锁(IsEnvBusy 是按 envId 串行 gate,RequirementsInstaller 不查 IsEnvBusy,直接跑) |
| Manager 跟其他 custom node 同名冲突(罕见)| 路径是固定 `custom_nodes/ComfyUI-Manager`,不冲突 |
| v0.6.5.22 IsEnvBusy 字典加新 ctor 字段后,既有测试构造参数对不上 | T1/T3 既有测试适配 ctor,加 FakeComfyUIManagerInstaller 占位 |
| bulk_update 的 ComfyUiManager 路径解析跟本任务 ResolveTargetDirectory 重复 | 暂不抽公共方法(YAGNI),新代码直接写,日后真重复再 refactor |
| Toggle 按钮在 Manager 装完后短暂 stale 状态(IsComfyUIManagerInstalled 还没 RaisePropertyChanged)| 装完回调里立刻重算 + 赋值,无 stale |
| T4 XAML 触发 v0.6.9.2 Setter + StaticResource 崩溃 | G11 + 所有 Setter 强制 property-element + DynamicResource;新增 Setter 前 grep |

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例)**:
- 5 task × (implementer + reviewer) ≈ 10 dispatch
- T1 先做(`RequirementsFileInstaller` 是 T2/T3/T5 的依赖)
- T1→T2→T3→T4→T5 串行,每 task commit 后立即 task-review
- 5 commit on main,最后 staging rebuild + GUI smoke + MEMORY update