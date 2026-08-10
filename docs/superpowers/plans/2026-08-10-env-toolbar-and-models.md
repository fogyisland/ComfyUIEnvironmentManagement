# env-list 两行按钮 + 组件报告 Chrome Fallback + 设置-全局默认 Models 路径

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** env-list 操作列拆两行按钮,组件报告 Chrome 优先 fallback 默认浏览器,设置新增"全局默认 Models 目录"字段(env-create 用作 junction 目标)。

**Architecture:**
- **两行按钮**:`EnvironmentListView.xaml` 操作列 DataGridTemplateColumn 改 `<StackPanel Vertical>` 含两个 `<WrapPanel>`,每行 5 个按钮(安装/卸载链路 vs 调试/删除链路);列宽 560→580
- **Chrome fallback**:抽 `BrowserLauncher.OpenWithChromeFallback(string path)`,复用 `EnvironmentListViewModel` 现有 3 个 Chrome 候选路径;`DefaultOpenReportFile` + `OpenBrowser` 都走它;失败回退默认浏览器;两者都失败 → `ErrorBanner.Add` Warn
- **全局默认 Models 目录**:`Settings.DefaultModelsDirectory` 字段,SettingsView 加浏览按钮 + TextBox;env-create 看到非空 → junction `<env>/ComfyUI/models` 到该路径(跟 SharedModelsDirectory 同样逻辑,但语义不同:Default = 新 env 默认,Shared = 跨 env 共享);空 → 不动 models 目录

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · hand-rolled MVVM · existing `EnvCreatorService` / `JunctionLinker` / `SettingsRepository` / `IEnvironmentRepository`

**base SHA:** `1c423dc`(spec 已 commit)

---

## Context

v0.6.9.3 (status bar + gear + theme toggle) 已 SHIP,HEAD `434b7e1`。spec commit `1c423dc` 描述 3 个用户桌面反馈需求。本 plan 实现 spec。

**沿用既有契约:**
- `EnvironmentListViewModel.ResolveChromePath()` 已在 v0.6.7.2 实现,3 个候选路径,static method
- `EnvironmentListViewModel.DefaultOpenReportFile(string path)` 现有 impl 直接走默认浏览器 — T2 替换
- `EnvironmentListViewModel.OpenBrowser(Environment env)` 现有 impl 已 Chrome 优先 — T2 抽公共 helper 复用
- `Settings.SharedModelsDirectory` 字段 + SettingsView UI 已存在(v0.6.7.3)— T1 在它"之上"加 DefaultModelsDirectory 字段
- `EnvCreatorService.CreateAsync` 现有 step 5.5 处理 SharedModelsDirectory junction,T3 在 step 5.5 之前加 DefaultModelsDirectory 分支
- v0.6.9.2 教训:任何 Setter Value 的 `{StaticResource}` 跨 merged-dict 失败;新加 XAML 全用 `{DynamicResource}`

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | env-list 现有 6 视图 + 10 命令保留;操作列按钮顺序按用户确认(启动/停止/装依赖/卸依赖/卸基础环境 在行 1;查日志/打开浏览器/装节点/组件报告/删除 在行 2);按钮功能不变 | 用户原话 |
| G2 | 组件报告按钮行为跟"打开浏览器"完全一致:Chrome 优先 → 默认浏览器 → ErrorBanner Warn(不弹 MessageBox) | 用户原话 |
| G3 | `Settings.DefaultModelsDirectory` 新字段,空字符串 = 不动 env-create 的 models 目录(每 env 自己拷贝);非空 = env-create 时 junction `<env>/ComfyUI/models` 到该路径 | 用户选"仅控制 junction 目标" |
| G4 | 保留 `Settings.SharedModelsDirectory` 字段 + 现有 junction 逻辑(v0.6.7.3);两字段语义独立 — Default = 新 env 默认,Shared = 跨 env 共享 | 用户确认 |
| G5 | 所有颜色/Brush 必须 `DynamicResource`,v0.6.9.2 教训;Setter Value 不能直接 `{StaticResource}` | 主题切换可工作 |
| G6 | 单元测试覆盖 VM/Service/纯逻辑;不为 WPF Window/DataGrid 写脆弱 STA UI 测试;XAML 解析错误的回归防御走现有 STA test seam (WpfTestResources helper) | `feedback_wpf_dialog_close_requested.md` 教训 |
| G7 | 测试命名:`BrowserLauncherTests` / `SettingsDefaultModelsDirectoryTests` / `EnvCreatorServiceDefaultModelsDirectoryTests` / `EnvironmentListViewLoadTests`;分支筛选后必须全 PASS | SDD 流程 |
| G8 | 每个 task 完成后跑相关测试 + 全套 `dotnet test`;精确文件名暂存单独 commit;不走 v-bump / 不走 release zip(项目惯例 UI hotfix 级) | 项目惯例 |
| G9 | `BrowserLauncher` 抽 `IBrowserLauncher` 接口 + 测试 seam(同 SettingsDefaults 模式);`ResolveChromePath` 改成 `internal static` + InternalsVisibleTo "ComfyUI.Manager.Tests" | spec §风险 |

---

## Task Breakdown

### Task 1: Settings.DefaultModelsDirectory 字段 + VM + UI + 浏览按钮

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs`(+1 字段,line 36 之前插入)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(+ `DefaultModelsDirectory` property after `SharedModelsDirectory`)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(line 46 之前插入"全局默认 Models 目录" TextBlock + DockPanel[TextBox + 浏览按钮])
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(+ `BrowseDefaultModelsDirectory` Click handler,模仿 `BrowseSharedModelsDirectory` line 107-115)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx`(+ 2 keys: `SettingsPage_全局默认Models目录` / `SettingsPage_默认Models目录提示`)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`(同上)
- Create test: `tests-wpf/.../Models/SettingsDefaultModelsDirectoryTests.cs`(~3 测试:字段存在 / 默认空字符串 / JSON round-trip)

**Key decisions:**
- 字段 JSON key = `"default_models_directory"`,跟 `shared_models_directory` 同命名风格
- Property impl 跟 `SharedModelsDirectory`(line 320-324)同款:`get => _settings.DefaultModelsDirectory; set { _settings.DefaultModelsDirectory = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }`
- 浏览按钮 handler:`var picked = DataContext is SettingsViewModel vm ? vm.PickFolder() : null; if (picked is not null && DataContext is SettingsViewModel vm2) { vm2.DefaultModelsDirectory = picked; }`
- TextBlock 文案:"全局默认 Models 目录(留空 = 不动 env 的 models 目录)"
- 提示文案:"新建 env 时 junction <env>/ComfyUI/models 到此路径;留空则 env 自己保留本地 models。"
- 不重排 SettingsPage 字段顺序;新字段就插在 "共享 Models 目录"之上(用户原话"全局默认"更常用)
- 不动 `SettingsDefaults.Apply` — DefaultModelsDirectory 默认空,SettingsDefaults 不主动填

**Commit:** `feat(wpf): add Settings.DefaultModelsDirectory field + UI`

---

### Task 2: BrowserLauncher 抽象 + EnvironmentListViewModel 接入

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/BrowserLauncher.cs`(~60 行,`IBrowserLauncher` 接口 + `BrowserLauncher` 默认 impl + `ResolveChromePath` internal static)
- Create: `src-wpf/ComfyUI.Manager/Services/IBrowserLauncher.cs`(~15 行,接口)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:
  - `DefaultOpenReportFile` (line 876) 改调 `BrowserLauncher.OpenWithChromeFallback(path)`,catch Exception → `_errorBanner?.Add(...)` 或者 existing fallback
  - `OpenBrowser` (line 905) 也改调 `BrowserLauncher.OpenWithChromeFallback(url)`
  - 删除 `ResolveChromePath` (line 941) — 移到 BrowserLauncher;或者保留 internal static wrapper 让 EnvListVM 旧测试不 break
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI 注入 `IBrowserLauncher`)
- Create test: `tests-wpf/.../Services/BrowserLauncherTests.cs`(~4 测试:Chrome 优先 / Chrome 失败回退 / 都失败 ErrorBanner / 无 path no-op)

**Key decisions:**
- `IBrowserLauncher.OpenWithChromeFallback(string path)` — path 是文件路径(报告)或 URL(浏览器);实现走 Chrome 优先 → 默认浏览器 → catch ErrorBanner
- `BrowserLauncher.OpenWithChromeFallback` 签名接 `Action<ErrorSeverity, string, string>? errorReporter` 作为可选注入;不接 ErrorBanner 静态引用,方便测试
- `BrowserLauncher.ResolveChromePath()` internal static,3 候选路径复用现有 line 941-948
- EnvListVM 的 `ResolveChromePath` 删除(迁移到 BrowserLauncher),但保留 `OpenReportFileOverride` test seam(给 v0.6.7 T2 test 继续用) — 用 `IBrowserLauncher` 默认 + test override `BrowserLauncherOverride`
- `OpenBrowser` 当前 line 905-940 的 Chrome try/catch 逻辑替换为单行 `BrowserLauncher.OpenWithChromeFallback(url, _errorBanner.Add)` 调用
- 不动 `OpenBrowserCommand.CanExecute`(env.Status=="running" gate 保持)
- 不动 `ReportComponentsCommand.CanExecute`

**Commit:** `feat(wpf): add BrowserLauncher with Chrome fallback`

---

### Task 3: EnvCreatorService 用 DefaultModelsDirectory 作 junction 目标

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(step 5.5 之后插入 step 5.6:DefaultModelsDirectory junction)
- Create test: `tests-wpf/.../Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs`(~3 测试:非空时 junction 到 DefaultModelsDirectory / 空时不建 models junction / SharedModelsDirectory 优先 vs Default 顺序)

**Key decisions:**
- 现有 step 5.5(line 141-163)处理 SharedModelsDirectory junction;新增 step 5.6 在 5.5 之后:
  ```csharp
  // 5.6 默认 Models 目录(若 DefaultModelsDirectory 非空且 step 5.5 未生效)
  if (!string.IsNullOrWhiteSpace(_settings.DefaultModelsDirectory) 
      && string.IsNullOrWhiteSpace(_settings.SharedModelsDirectory))
  {
      var defaultModelsFull = Path.GetFullPath(_settings.DefaultModelsDirectory);
      var modelsLink = Path.Combine(comfyuiLink, "models");
      // 同 5.5 模式:删已存在 → junction
      if (Directory.Exists(modelsLink))
          Directory.Delete(modelsLink, recursive: true);
      await _linker.CreateAsync(modelsLink, defaultModelsFull, ct);
      progress?.Report(new CreateStepReport("链接默认 Models",
          $"junction: {modelsLink} → {defaultModelsFull}"));
  }
  ```
- 5.5 vs 5.6 优先级:`SharedModelsDirectory` 非空时 5.6 不执行(避免覆盖);SharedModelsDirectory 空时 5.6 兜底
- 回滚:5.6 失败同样抛 `CreateEnvException("DEFAULT_MODELS_LINK_FAILED", ex.Message)` 让外面 catch
- 测试用现有 `FakeLinker` + `FakeVenvCreator`,不动 EnvCreatorService ctor 签名
- `Environment.ModelsDirectory` 字段**不新增** — 用户选"仅控制 junction 目标"

**Commit:** `feat(wpf): EnvCreatorService uses DefaultModelsDirectory for junction`

---

### Task 4: EnvironmentListView.xaml 两行按钮布局

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(line 31-90 操作列 DataGridTemplateColumn 改双 WrapPanel)
- Create test: `tests-wpf/.../Views/EnvironmentListViewLoadTests.cs`(~1 STA load test 走 WpfTestResources helper)

**Key decisions:**
- 列 Width 560 → 580
- 原 `<StackPanel Orientation="Horizontal">` 改 `<StackPanel Orientation="Vertical">` 含 2 个 `<WrapPanel Orientation="Horizontal">`
- Row1 WrapPanel:启动 / 停止 / 装依赖 / 卸载依赖 / 卸载基础环境
- Row2 WrapPanel:查看日志 / 打开浏览器 / 安装节点 / 组件报告 / 删除
- 顺序保持现状(每个按钮的 Command binding 不变)
- 每个 WrapPanel `ItemHeight` 让按钮对齐;`Margin="2"` 沿用原风格
- DataGrid 行高自动 grow(原没显式固定,保持)
- STA load test:instantiate EnvironmentListView + Measure/Arrange/UpdateLayout,验证不抛 XamlParseException(走 WpfTestResources helper)

**Commit:** `feat(wpf): env-list operation column split into two rows`

---

### Task 5: Final review + GUI 烟测 + MEMORY 写入

**Files:**
- 无新增 source file
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`(+ v0.6.10 入口一行)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_10_env_toolbar.md`(NEW,完整 SDD 概要)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\feedback_browser_chrome_fallback.md`(NEW,小条目:组件报告/OpenBrowser Chrome 优先级统一走 BrowserLauncher)

**Verification(End-to-end GUI 烟测):**
1. 启动 → env-list 操作列变两行(行 1 = 5 个装卸按钮,行 2 = 5 个调试删除按钮)
3. 点"组件报告" → Chrome 打开 reports/env-{name}-{ts}.html
4. 卸载 Chrome(临时改 path 模拟)→ 再点 → 默认浏览器打开
5. 设置 → 填"全局默认 Models 目录" → 保存 → 新建 env → junction 到该路径
6. 设置 → "共享 Models 目录"仍可填,行为不变
7. 旧 settings.json 缺 default_models_directory 字段 → 反序列化给 "" → env-create 不动 models 目录

**Commit:** `docs(wpf): v0.6.10 env toolbar + chrome fallback + models path SDD record`

---

## Critical files (full list)

**新增:**
- `src-wpf/ComfyUI.Manager/Services/BrowserLauncher.cs`
- `src-wpf/ComfyUI.Manager/Services/IBrowserLauncher.cs`

**修改:**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`(+1 字段)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(+1 property)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(+1 行 UI)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(+1 Click handler)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(操作列改双 WrapPanel)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`DefaultOpenReportFile` + `OpenBrowser` 改调 `BrowserLauncher`)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(step 5.6 DefaultModelsDirectory junction)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI 注入 `IBrowserLauncher`)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx`(+ 2 keys)
- `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`(+ 2 keys)

**测试新增(~11 测试):**
- `tests-wpf/.../Models/SettingsDefaultModelsDirectoryTests.cs`(3)
- `tests-wpf/.../Services/BrowserLauncherTests.cs`(4)
- `tests-wpf/.../Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs`(3)
- `tests-wpf/.../Views/EnvironmentListViewLoadTests.cs`(1 STA)

---

## Verification (end-to-end GUI smoke)

按 7 步测试覆盖(同 Task 5 §Verification)。

**自动化测试:**
- `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → 基线 764 + ~11 新 = ~ 775 PASS / 2 known FAIL / 1 SKIP
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal` → 成功
- `git status --short` → 只 pre-existing `?? full-suite.log` + `?? tools/` + 本任务新文件
- 走 SDD G8(无 v-bump / 无 zip — UI 改造是 hotfix 级,不打 release)

---

## Risks

| 风险 | 缓解 |
|---|---|
| BrowserLauncher 抽接口 + 测试 seam,OpenBrowser 现有 5+ 测试可能 break(因为 mock 方式变了) | OpenBrowser 内 `ResolveChromePath` 改 internal static 后,既有测试用 `InternalsVisibleTo("ComfyUI.Manager.Tests")` 访问,无需改 mock;只改 DefaultOpenReportFile 的代码路径 |
| `IBrowserLauncher` 静态化后,测试注入 `BrowserLauncherOverride` 时跟 `OpenReportFileOverride` 冲突 | 两个 seam 共存:OpenReportFileOverride 走 path(string)→BrowserLauncher 兜底;BrowserLauncherOverride 完全替换 BrowserLauncher(用于将来禁用 Chrome 测试) |
| EnvCreatorService step 5.6 加 junction 操作,junction 失败抛 CreateEnvException 会让整个 env-create 回滚 | 跟 step 5.5 同模式,失败 throw 后外面 catch 删 env 根目录,沿用现有回滚链 |
| DataGrid 行高自适应让 viewport 可见行数变少 | 单行 30px → 双行 70px,影响 1-2 行可见性,可接受;若有用户反馈再加 MaxHeight 限制 |
| Settings UI 新字段位置在 "共享 Models 目录" 之上,可能让用户困惑"为什么有两个" | Tooltip 文字清楚说明两个语义不同;后续如需要可加 Comment 标识 |
| WPF `WrapPanel` 不带 row break — 当窗口很窄时按钮可能挤到 4-5 个换行,跟单 StackPanel 没区别 | WrapPanel 默认按可用宽度换行,不影响功能;视觉上比挤在一行好看 |

---

## Critical Files for Implementation (priority order)

1. `src-wpf/ComfyUI.Manager/Services/BrowserLauncher.cs` — Chrome fallback 抽象基座
2. `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` — 接入 BrowserLauncher
3. `src-wpf/ComfyUI.Manager/Models/Settings.cs` — DefaultModelsDirectory 字段
4. `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — 设置 UI 新行
5. `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` — env-create step 5.6
6. `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` — 操作列两 WrapPanel

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 5 task × (implementer + reviewer) ≈ 10 dispatch + 1 final whole-branch review
- Per-task review gate(haiku implementer + haiku reviewer,Task 4 XAML 整合用 sonnet)
- 预计 5 commits on main(T1-T5)+ 1 final fix wave(若 review 发现问题)
- T1+T2+T3 单元测试覆盖核心逻辑(Settings 字段 / BrowserLauncher / EnvCreatorService)
- T4 XAML 整合编译 verify + STA load test
- T5 MEMORY 写入 + GUI smoke

(Plan agent left out: 用户已确认 3 个核心决策(5+5 分组 / Chrome fallback / 全局默认 junction),spec 完整,无需额外 design pass。下一步进入 SDD dispatch。)