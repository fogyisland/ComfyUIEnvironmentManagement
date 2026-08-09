# v0.6.9 UI Modernization 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ComfyUI.Manager` 主界面从"功能堆叠"升级为现代双主题 UI:Dark/Light 主题切换(默认 Dark)+ Dashboard Welcome 页(高密度信息:env/节点统计 + 最近操作 + GitHub release)+ Spotlight 全局搜索(env+节点+设置项+操作命令)+ 全套动效(Ripple / view fade / banner slide / dashboard stagger / check-update pulse)。保留现有 6 个侧栏入口和所有菜单命令。

**Architecture:**
- **双主题**:Light/Dark 两套独立 Palette ResourceDictionary,所有视图改用 `DynamicResource` 引用颜色/Brush;`ThemeService.Apply(ThemeMode)` 原子替换 Palette 字典 + 根层 300ms opacity cross-fade
- **Dashboard**:启动默认页;`DashboardService` 聚合 env 状态 + 节点数 + 最近日志 + GitHub release;`DashboardViewModel.RefreshAsync()` 进入时 lazy 加载 + 手动刷新按钮,无后台定时器;GitHub/日志失败部分降级
- **Spotlight Search**:`SearchIndex` 内存索引,打开时构建,键入仅走内存;score-based 排名(exact > prefix > substring > subsequence),无 Levenshtein;`Ctrl+K` 打开浮层,Enter 导航并选中目标
- **动效**:Ripple 用 attached Behavior(避免每按钮 ControlTemplate);view 切换 200ms opacity;ErrorBanner slide-in 250ms;Dashboard 卡片 100ms stagger;check-update pulse;统一 `MotionSettings.IsAnimationEnabled` 开关(尊重系统设置)

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · hand-rolled MVVM(ViewModelBase/RelayCommand)· `ResourceDictionary` 动态切换 · `DynamicResource` · `Storyboard` + `ColorAnimation` · attached Behaviors · 现有 AppLogger / GitHubVersionService / IEnvironmentRepository / NodeRepository

**base SHA:** `b0989c7` (v0.6.8 SHIP-READY, HEAD)

**相关已有代码:**
- `MainWindow.xaml`(99 行)· `MainWindow.xaml.cs`(74 行)· `MainViewModel.cs`(412 行,15 个 RelayCommand,line 80-95)
- `Resources/Theme.xaml`(124 行:8 颜色 + 8 brushes + MaterialButton style + 6 converters)
- `Resources/Strings.resx`(~108 keys)+ `Strings.zh-CN.resx`(~80 keys)
- `Models/Settings.cs`(25 字段,已有 `Theme` / `ThemeMode` / `Language`,**ThemeMode 当前无接线**)
- `Data/SettingsRepository.cs`(72 行,JSON 持久化到 `%APPDATA%\ComfyUI-Manager\settings.json`)
- `Services/AppLogger.cs`(`<projectRoot>\Logs\YYYY-MM-DD.log`,`ReadLines()` 只读今天,**需新增 `ReadRecentLines(daysBack=2)`**)
- `Services/GitHubVersionService.cs`(`GetLatestVersionAsync()` 已存在,返回 `string?`)
- `Data/IEnvironmentRepository.cs`(`ListAll()` 已存在,**无 `CountAllAsync()`**)
- `Data/NodeRepository.cs`(`ListByEnv(envId)`,**无全局 count**)
- `Views/EnvironmentListView.xaml:20`(`SelectedItem="{Binding Selected}"`)+ `EnvironmentListViewModel.Selected`(line 244)
- `Views/CatalogView.xaml:19`(Query TextBox)+ `CatalogViewModel.Query`(line 66-71,setter 触发 Search)
- 唯一现有动效:`Views/SplashWindow.xaml:11-17` FadeOutStoryboard

---

## Context

`ComfyUI.Manager` 当前主界面功能完整,但视觉层次单一、启动后无 Dashboard 概览、侧栏按钮无选中状态、无跨功能搜索能力、动效几乎为零。用户反馈"主界面有点单调",确认同时落地 4 方向改造:深浅双主题(默认 Dark)、Dashboard Welcome、Spotlight 全局搜索、全量动效。保留所有现有 6 侧栏入口 + 4 顶部菜单 + 所有 dialog 与数据流;不引入新依赖(沿用现有 WPF/.NET 8 能力)。预计 2-3 周,10 个 task 顺序提交。

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | 现有 6 个侧栏入口(环境/节点目录/基础环境/设置/批量更新/系统状态)+ 4 顶部菜单(文件/设置/工具/关于)全部保留,命令行为不变 | 用户确认 |
| G2 | Dashboard + 现有 6 view + 所有 dialog 全部适配 Light/Dark,**禁止新建硬编码颜色**(除 palette 字典本身) | 主题切换 |
| G3 | Dark 为默认主题;Light = 现有 `#F6F1FB` 系列;Dark = 新 `#1E1E1E` 系列(跟 Splash 一致) | 用户选 |
| G4 | 所有颜色/Brush 必须 `DynamicResource`;尺寸/时长等不变资源可用 `StaticResource` | 主题切换可工作 |
| G5 | `Settings.ThemeMode` 是持久化唯一来源;启动时加载应用,旧配置缺失 ThemeMode 时回退 Dark | 持久化约定 |
| G6 | VM/服务/排序/聚合做单元测试;**不为 WPF Window/Storyboard 写脆弱 UI 测试** | `feedback_wpf_dialog_close_requested.md` 教训 |
| G7 | 搜索**禁止**每次键入访问 SQLite/日志/网络;打开 Spotlight 时构建索引,键入仅走内存评分 | 性能 |
| G8 | GitHub/日志读取失败**不能**阻止 Dashboard 展示本地统计 | 降级 |
| G9 | 所有动效必须可关闭,且尊重系统"减少动画"设置 | 可访问性 |
| G10 | **不新增第三方依赖**(模糊搜索/动画);优先使用现有 WPF/.NET 8 能力 | 项目惯例 |
| G11 | UI 文案进入现有 i18n(`Strings.resx` + `Strings.zh-CN.resx`),不直接散落 XAML/VM | `feedback_workflow.md` i18n 推迟 |
| G12 | 每个 task 完成后跑相关测试 + 全套构建,用精确文件名暂存并单独 commit | SDD 流程 |
| G13 | Splash fade-out 跟 Dashboard fade-in 通过 cross-fade 衔接 | 启动体验 |
| G14 | Spotlight 索引构建失败 → 浮层仍打开 + 显示 "搜索暂不可用" + 关闭 | 鲁棒性 |
| G15 | 不 bump version / 不发 release zip(per memory `feedback_no_zip.md`) | 跟 v0.6.7.x 惯例一致 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml` | ~30 | 现有 Light 调色板 dict(同名键) |
| `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml` | ~30 | 新 Dark 调色板 dict(同名键) |
| `src-wpf/ComfyUI.Manager/Services/ThemeService.cs` | ~80 | IThemeService + ThemeService + ThemeMode enum |
| `src-wpf/ComfyUI.Manager/Services/DashboardService.cs` | ~150 | 4 类数据并行聚合 |
| `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs` | ~100 | BuildAsync 拉 env + nodes + 静态 list |
| `src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs` | ~40 | snapshot + EnvironmentCounts nested record |
| `src-wpf/ComfyUI.Manager/Data/INodeRepository.cs` | ~20 | 接口(跟 IEnvironmentRepository 同款) |
| `src-wpf/ComfyUI.Manager/Converters/SectionEqualityToBoolConverter.cs` | ~15 | IValueConverter |
| `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` + `.xaml.cs` | ~150 | 4 卡片 Grid + code-behind |
| `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs` | ~120 | Refresh/IsRefreshing/Snapshot/RefreshCommand |
| `src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml` + `.xaml.cs` | ~130 | compact SearchBox + Popup overlay + key handling |
| `src-wpf/ComfyUI.Manager/ViewModels/SpotlightSearchViewModel.cs` | ~100 | Query/Results/SelectedIndex/Open/Close/ExecuteSelected |
| `src-wpf/ComfyUI.Manager/Search/SearchIndex.cs` | ~120 | 内存索引 + Build + Query score-based |
| `src-wpf/ComfyUI.Manager/Search/SearchEntry.cs` | ~30 | Id/Kind/DisplayName/NormalizedTokens/Target |
| `src-wpf/ComfyUI.Manager/Search/SearchResult.cs` | ~20 | Entry/Score |
| `src-wpf/ComfyUI.Manager/Search/SearchTarget.cs` | ~15 | enum + payload record |
| `src-wpf/ComfyUI.Manager/Animations/MotionSettings.cs` | ~40 | IsAnimationEnabled + 5 时长常量 |
| `src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs` | ~120 | attached Behavior + 50ms Ellipse 扩散 |
| `src-wpf/ComfyUI.Manager/Behaviors/ViewFadeTransitionBehavior.cs` | ~80 | ContentControl Content 变化时 200ms opacity |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` + `.xaml.cs` | 侧栏 220px + 7 入口 + SearchBox 占位 + ContentControl + Ctrl+K |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | CurrentSection enum + ShowDashboardCommand + NavigateToTarget + 接 IThemeService + 接 Spotlight VM |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | + SelectEnvironment(string envId) 公开方法 |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | + SelectNode(string nodeId) 公开方法 |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | + ScrollToSection 公开方法 + ThemeMode 接 IThemeService |
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 确认 ThemeMode 字段 enum + 默认 Dark |
| `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs` | + CountAllAsync(CancellationToken) + implements INodeRepository |
| `src-wpf/ComfyUI.Manager/Services/AppLogger.cs` | + ReadRecentLines(int daysBack = 2) |
| `src-wpf/ComfyUI.Manager/App.xaml` | + palette dictionary 动态 merged dictionaries 槽位 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 启动 Load Settings → Apply Theme → Show MainWindow |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | 剥离 8 colors + 8 brushes,只留 styles/converters/font sizes/padding + 新增 SidebarButtonStyle |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx` | Dashboard 标题/卡片名/最近操作/检查更新 + ThemeMode 选项 |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` / `CatalogView.xaml` / `BaseEnvView.xaml` / `SettingsView.xaml` / `SystemStatusView.xaml` / `BulkUpdateDialog.xaml` / `ErrorBanner.xaml` + `.xaml.cs` | 硬编码颜色/StaticResource → DynamicResource + ErrorBanner slide-in |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` | 渐变协调 cross-fade |

### Delete

无。

### Keep(unchanged)
- `BaseEnvInstaller` / `BaseEnvProfileLoader` / `ProcessLauncher` / `EnvCreatorService`
- `Settings` JSON 模型结构(只确认 ThemeMode 字段,不动结构)
- 现有 6 个 view 逻辑(只动 XAML 颜色引用)
- `AppLogger.ReadLines()` 既有方法(ReadRecentLines 是新增,不动老方法)

---

## Tasks

### Task 1: 双主题 ResourceDictionary + ThemeService

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml`(~30 行)
- Create: `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml`(~30 行)
- Create: `src-wpf/ComfyUI.Manager/Services/ThemeService.cs`(~80 行)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(删 8 colors + 8 brushes)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml`(增加 palette dictionary 动态 merged dictionaries 槽位)
- Create test: `tests-wpf/.../Services/ThemeServiceTests.cs`(~5 测试)

**Step 1: 写 Palette.Light.xaml**(从 Theme.xaml 抽出 8 颜色 + 8 brushes,键名保持 `BackgroundBrush` 等)

**Step 2: 写 Palette.Dark.xaml**(新暗色系:#1E1E1E 背景,#2D2D2D surface,#E6E1E5 on-surface,#938F99 outline,#CF6679 error,#BB86FC primary,#9965E0 primary-variant,#1C1B1F on-primary)

**Step 3: 写 ThemeService 失败测试**(`Apply_Dark_ReplacesMergedDict` / `Apply_Light_ReplacesMergedDict` / `Apply_InvalidMode_FallsBackToDark` / `Current_TracksLastApplied` / `Applied_Event_Fires`)

**Step 4: 跑测试** — 失败 (ThemeService 不存在)

**Step 5: 写 ThemeService.cs**(IThemeService + ThemeService + ThemeMode enum + Apply 方法原子替换 + AppLogger 错误日志)

**Step 6: 跑测试** — PASS

**Step 7: 修改 Theme.xaml 剥离 8 colors + 8 brushes**(只留 styles/converters/font sizes/padding)

**Step 8: 修改 App.xaml 加 palette slot**(`<ResourceDictionary Source="Themes/Palette.Light.xaml"/>` 初始槽位)

**Step 9: 跑全套 build** — 0 errors / 0 warnings

**Step 10: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml \
        src-wpf/ComfyUI.Manager/Services/ThemeService.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        src-wpf/ComfyUI.Manager/App.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Services/ThemeServiceTests.cs
git commit -m "feat(wpf): add dynamic light/dark palette infrastructure"
```

---

### Task 2: Settings 持久化 + 全视图主题迁移

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs`(确认 ThemeMode 字段)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(接 IThemeService)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(构造接 IThemeService)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(启动 Load Settings → Apply Theme)
- Modify: 6 个 view XAML(硬编码颜色 → DynamicResource)
- Modify: `Resources/Strings.resx` + `Strings.zh-CN.resx`
- Create test: `tests-wpf/.../Services/SettingsThemeIntegrationTests.cs`(~4 测试)

**Step 1: 确认 Settings.ThemeMode 字段**(已有 `ThemeMode` 字段,改成 string enum `Light` / `Dark` / `FollowSystem`,默认 `Dark`)

**Step 2: 写 SettingsThemeIntegrationTests 失败测试**(`Write_ThenRead_PreservesThemeMode` / `MissingFile_FallsBackToDark` / `InvalidValue_FallsBackToDark` / `FollowSystem_ResolvesToSystemTheme`)

**Step 3: 跑测试** — 失败

**Step 4: 实现 Settings.ThemeMode 字段 + SettingsRepository 兼容 + fallback 逻辑**

**Step 5: 跑测试** — PASS

**Step 6: 6 view XAML 颜色迁移**(`EnvironmentListView.xaml` / `CatalogView.xaml` / `BaseEnvView.xaml` / `SettingsView.xaml` / `SystemStatusView.xaml` / `BulkUpdateDialog.xaml` — 硬编码 `#FFFBFE` 等 → `DynamicResource BackgroundBrush`)

**Step 7: SettingsViewModel 接 IThemeService + SettingsView.xaml 接 ThemeMode ComboBox 双向绑定**

**Step 8: App.xaml.cs 启动顺序:Load Settings → Apply Theme → Show MainWindow**

**Step 9: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 10: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml \
        src-wpf/ComfyUI.Manager/Views/CatalogView.xaml \
        src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Views/SystemStatusView.xaml \
        src-wpf/ComfyUI.Manager/Views/BulkUpdateDialog.xaml \
        src-wpf/ComfyUI.Manager/Resources/Strings.resx \
        src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx \
        tests-wpf/ComfyUI.Manager.Tests/Services/SettingsThemeIntegrationTests.cs
git commit -m "feat(wpf): connect theme settings and migrate all views"
```

---

### Task 3: 主壳层重构 + 侧栏选中状态

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`(侧栏 200→220px + 7 入口 + SearchBox 占位 + ContentControl)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`(Ctrl+K hook)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(新增 `CurrentSection` enum)
- Create: `src-wpf/ComfyUI.Manager/Converters/SectionEqualityToBoolConverter.cs`(~15 行)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(新增 `SidebarButtonStyle`)
- Create test: `tests-wpf/.../ViewModels/MainViewModelNavigationTests.cs`(~6 测试)

**Step 1: 写 MainViewModelNavigationTests 失败测试**(`ShowEnvironments_UpdatesCurrentSectionAndView` / `ShowCatalog_UpdatesCurrentSectionAndView` / `ShowBaseEnv_UpdatesCurrentSectionAndView` / `ShowSettings_UpdatesCurrentSectionAndView` / `ShowDashboard_UpdatesCurrentSectionAndView` / `CurrentSection_StaysConsistent_WhenCachingPage`)

**Step 2: 跑测试** — 失败

**Step 3: MainViewModel 加 `enum MainSection { Dashboard, Environments, Catalog, BaseEnv, Settings, BulkUpdate, SystemStatus }` + `CurrentSection` property + 所有 ShowXxx 同步更新**

**Step 4: 跑测试** — PASS

**Step 5: 写 `SectionEqualityToBoolConverter`**(IValueConverter,`Equals(section, parameter) → Visibility.Visible/Collapsed`)

**Step 6: 写 `SidebarButtonStyle`**(Theme.xaml 加 Style 含 `IsChecked` Trigger + Selected 模板,绑定 `Background` 到 `PrimaryBrush` 当 `IsChecked`)

**Step 7: MainWindow.xaml 侧栏改 220px + 7 入口(主页/环境/节点目录/基础环境/设置/批量更新/系统状态)+ SearchBox 占位 + ContentControl 绑 CurrentView**

**Step 8: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 9: Commit**
```bash
git add src-wpf/ComfyUI.Manager/MainWindow.xaml \
        src-wpf/ComfyUI.Manager/MainWindow.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/Converters/SectionEqualityToBoolConverter.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelNavigationTests.cs
git commit -m "refactor(wpf): modernize shell with section navigation state"
```

---

### Task 4: Dashboard 数据聚合服务

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/DashboardService.cs`(~150 行)
- Create: `src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs`(~40 行)
- Modify: `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs`(+ `CountAllAsync()`)
- Create: `src-wpf/ComfyUI.Manager/Data/INodeRepository.cs`(~20 行)
- Modify: `src-wpf/ComfyUI.Manager/Services/AppLogger.cs`(+ `ReadRecentLines`)
- Create test: 3 文件 ~13 测试

**Step 1: 写 NodeRepositoryCountTests 失败测试**(`CountAllAsync_EmptyDb_ReturnsZero` / `CountAllAsync_NonEmptyDb_ReturnsCount`)

**Step 2: 跑测试** — 失败

**Step 3: 抽 INodeRepository.cs**(跟 IEnvironmentRepository 同款;声明 `CountAllAsync` + `ListByEnv`)

**Step 4: NodeRepository 实现 INodeRepository + 加 `CountAllAsync` 单 SQL `SELECT COUNT(*) FROM scanned_nodes`**

**Step 5: 跑测试** — PASS

**Step 6: 写 AppLoggerReadRecentLinesTests 失败测试**(`ReadRecentLines_OneDay_ReturnsTodayOnly` / `ReadRecentLines_TwoDays_MergesTodayAndYesterday` / `ReadRecentLines_MissingFile_SkipsAndReturnsRest`)

**Step 7: 跑测试** — 失败

**Step 8: AppLogger 加 `ReadRecentLines(int daysBack = 2)` 用 yield return**

**Step 9: 跑测试** — PASS

**Step 10: 写 DashboardSnapshot.cs**(record + EnvironmentCounts nested record)

**Step 11: 写 DashboardServiceTests 失败测试**(`GetSnapshotAsync_EmptyEnvList_ReturnsZeroes` / `GetSnapshotAsync_MixedEnvs_CountsByStatus` / `GetSnapshotAsync_GitHubFailure_StillReturnsSnapshotWithNullRelease` / `GetSnapshotAsync_NodeFailure_Throws` / `GetSnapshotAsync_RecentOps_ReturnsFiveLatest` / `GetSnapshotAsync_LogReadFailure_ReturnsEmptyOps` / `GetSnapshotAsync_ParallelExecution_FasterThanSequential` / `GetSnapshotAsync_CancellationRequested_StopsCleanly`)

**Step 12: 跑测试** — 失败

**Step 13: 实现 DashboardService**(`Task.WhenAll(env counts, node count, recent ops, GitHub)` + 各自 try/catch + GitHub 失败 partial result + 其它 throw)

**Step 14: 跑测试** — PASS

**Step 15: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 16: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Services/DashboardService.cs \
        src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs \
        src-wpf/ComfyUI.Manager/Data/NodeRepository.cs \
        src-wpf/ComfyUI.Manager/Data/INodeRepository.cs \
        src-wpf/ComfyUI.Manager/Services/AppLogger.cs \
        tests-wpf/.../Services/DashboardServiceTests.cs \
        tests-wpf/.../Data/NodeRepositoryCountTests.cs \
        tests-wpf/.../Services/AppLoggerReadRecentLinesTests.cs
git commit -m "feat(wpf): add Dashboard aggregation service"
```

---

### Task 5: DashboardView + 设为启动默认页

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml`(~120 行)
- Create: `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml.cs`(~30 行)
- Create: `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs`(~120 行)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(+ `ShowDashboardCommand` + `ShowDashboardAsync()`)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`(启动 `Loaded` 调 `ShowDashboardAsync()`)
- Modify: `Resources/Strings.resx` + `Strings.zh-CN.resx`
- Create test: `tests-wpf/.../ViewModels/DashboardViewModelTests.cs`(~6 测试)

**Step 1: 写 DashboardViewModelTests 失败测试**(`Ctor_InitialState_IsRefreshingFalse_NullSnapshot` / `RefreshAsync_LoadsSnapshot_SetsIsRefreshingFalse` / `RefreshAsync_GitHubFailed_SnapshotHasNullRelease` / `RefreshAsync_PartialFailure_RetainsLastSnapshot` / `RefreshCommand_TriggersRefresh` / `RefreshAsync_ConcurrentCalls_Deduplicates`)

**Step 2: 跑测试** — 失败

**Step 3: 写 DashboardViewModel**(IDashboardService 注入 + Snapshot + IsRefreshing + LastSnapshot + RefreshAsync + RefreshCommand + SemaphoreSlim 并发去重)

**Step 4: 跑测试** — PASS

**Step 5: 写 DashboardView.xaml**(Grid 2x2 4 卡片:env 统计 + 节点数 + 最近操作 + 版本/更新)

**Step 6: 写 DashboardView.xaml.cs**(DataContext 绑 VM + 手动刷新按钮绑 RefreshCommand)

**Step 7: 改 Strings.resx + Strings.zh-CN.resx**(`Dashboard_Title` / `Dashboard_EnvironmentStats` / `Dashboard_Running` / `Dashboard_Stopped` / `Dashboard_Undeployed` / `Dashboard_Total` / `Dashboard_NodeCount` / `Dashboard_RecentOps` / `Dashboard_CheckUpdate` / `Dashboard_Refresh` / `Dashboard_LatestVersion` / `Dashboard_GitHubFailed` / `Dashboard_Loading`)

**Step 8: MainViewModel 加 `ShowDashboardCommand` + `ShowDashboardAsync()`**(缓存 DashboardView,设 CurrentSection = Dashboard,CurrentView = DashboardView,首次调 RefreshAsync)

**Step 9: MainWindow.xaml.cs 启动 `Loaded` 调 `ShowDashboardAsync()`**(用 dispatcher 确保不阻塞主线程)

**Step 10: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 11: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Views/DashboardView.xaml \
        src-wpf/ComfyUI.Manager/Views/DashboardView.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/MainWindow.xaml.cs \
        src-wpf/ComfyUI.Manager/Resources/Strings.resx \
        src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx \
        tests-wpf/.../ViewModels/DashboardViewModelTests.cs
git commit -m "feat(wpf): add Dashboard Welcome as startup view"
```

---

### Task 6: SearchIndex + score-based 排名

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Search/SearchIndex.cs`(~120 行)
- Create: `src-wpf/ComfyUI.Manager/Search/SearchEntry.cs`(~30 行)
- Create: `src-wpf/ComfyUI.Manager/Search/SearchResult.cs`(~20 行)
- Create: `src-wpf/ComfyUI.Manager/Search/SearchTarget.cs`(~15 行)
- Create: `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs`(~100 行)
- Create test: `tests-wpf/.../Search/SearchIndexTests.cs`(~10 测试)

**Step 1: 写 SearchIndexTests 失败测试**(`Query_Empty_ReturnsEmpty` / `Query_ExactMatch_Scores100` / `Query_PrefixMatch_Scores80` / `Query_TokenPrefix_Scores60` / `Query_Substring_Scores40` / `Query_Subsequence_Scores20` / `Query_ChineseText_Matches` / `Query_CaseInsensitive_Normalizes` / `Query_RespectsMaxLimit` / `Query_TieBreak_KindPriorityAndShortText`)

**Step 2: 跑测试** — 失败

**Step 3: 写 SearchEntry / SearchResult / SearchTarget.cs**

**Step 4: 写 SearchIndex.Build + Query**(归一化 token + 5 档评分 + tie-break by Kind + text length)

**Step 5: 跑测试** — PASS

**Step 6: 写 GlobalSearchService.BuildAsync**(env list + node names + Settings 静态 list + commands 静态 list → SearchEntry list → SearchIndex.Build)

**Step 7: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 8: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Search/SearchIndex.cs \
        src-wpf/ComfyUI.Manager/Search/SearchEntry.cs \
        src-wpf/ComfyUI.Manager/Search/SearchResult.cs \
        src-wpf/ComfyUI.Manager/Search/SearchTarget.cs \
        src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs \
        tests-wpf/.../Search/SearchIndexTests.cs
git commit -m "feat(wpf): add ranked global search index"
```

---

### Task 7: Spotlight UI + 快捷键 + 导航

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml`(~50 行)
- Create: `src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml.cs`(~80 行)
- Create: `src-wpf/ComfyUI.Manager/ViewModels/SpotlightSearchViewModel.cs`(~100 行)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(+ `OpenSpotlightCommand` + `NavigateToTarget(SearchTarget)`)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`(侧栏顶部加 SpotlightSearchBox)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`(`Ctrl+K` hotkey)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(+ `SelectEnvironment(string envId)`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(+ `SelectNode(string nodeId)`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(+ `ScrollToSection(string sectionKey)`)
- Create test: `tests-wpf/.../ViewModels/SpotlightSearchViewModelTests.cs`(~6 测试)
- Create test: `tests-wpf/.../ViewModels/MainViewModelSearchNavigationTests.cs`(~5 测试)

**Step 1: 写 SpotlightSearchViewModelTests 失败测试**(`OpenCommand_TriggersBuild` / `Query_UpdatesResults` / `Enter_ExecutesSelected` / `Esc_ClosesPopup` / `UpDown_ChangesSelectedIndex` / `BuildAsync_Failure_ShowsUnavailableMessage`)

**Step 2: 跑测试** — 失败

**Step 3: 写 SpotlightSearchViewModel**(IGlobalSearchService + OpenCommand + CloseCommand + UpCommand + DownCommand + EnterCommand + Query + Results + SelectedIndex + IsUnavailable)

**Step 4: 跑测试** — PASS

**Step 5: 写 MainViewModelSearchNavigationTests**(`NavigateToTarget_Environment_ShowsEnvListAndSelects` / `NavigateToTarget_Node_ShowsEnvListAndSelectsNode` / `NavigateToTarget_Settings_ShowsSettingsAndScrolls` / `NavigateToTarget_Command_ExecutesRelayCommand` / `OpenSpotlightCommand_OpensPopup`)

**Step 6: 跑测试** — 失败

**Step 7: MainViewModel 加 `OpenSpotlightCommand` + `NavigateToTarget(SearchTarget)` + 4 分发 switch**

**Step 8: EnvironmentListVM 加 `SelectEnvironment(string envId)`**(scroll + select)
CatalogVM 加 `SelectNode(string nodeId)`
SettingsVM 加 `ScrollToSection(string sectionKey)`

**Step 9: 跑测试** — PASS

**Step 10: 写 SpotlightSearchBox.xaml**(compact TextBox 300px + Popup overlay 600×400 + ListBox 显示 top 20 + 分组标题)

**Step 11: 写 SpotlightSearchBox.xaml.cs**(code-behind + key handling Up/Down/Enter/Esc + DataContext 绑 VM)

**Step 12: MainWindow.xaml 侧栏顶部加 SpotlightSearchBox 引用**

**Step 13: MainWindow.xaml.cs 加 `Ctrl+K` hotkey**(KeyBinding 绑 OpenSpotlightCommand + InputBindings)

**Step 14: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 15: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml \
        src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SpotlightSearchViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/MainWindow.xaml \
        src-wpf/ComfyUI.Manager/MainWindow.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        tests-wpf/.../ViewModels/SpotlightSearchViewModelTests.cs \
        tests-wpf/.../ViewModels/MainViewModelSearchNavigationTests.cs
git commit -m "feat(wpf): add Spotlight search UI and navigation"
```

---

### Task 8: 动效基础 + Ripple Behavior

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Animations/MotionSettings.cs`(~40 行)
- Create: `src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs`(~120 行)
- Create: `src-wpf/ComfyUI.Manager/Behaviors/ViewFadeTransitionBehavior.cs`(~80 行)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(+ `Motion` 资源 + `MaterialButton` 接入 RippleBehavior)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`(ContentControl 加 ViewFadeTransitionBehavior)
- Create test: `tests-wpf/.../Animations/MotionSettingsTests.cs`(~3 测试)

**Step 1: 写 MotionSettingsTests 失败测试**(`IsAnimationEnabled_DefaultIsTrue` / `RippleDuration_50ms` / `StaggerDuration_100ms`)

**Step 2: 跑测试** — 失败

**Step 3: 写 MotionSettings.cs**(静态 `IsAnimationEnabled = SystemParameters.ClientAreaAnimation` + 5 时长常量 `DurationRipple=50` / `FadeView=200` / `SlideBanner=250` / `StaggerDashboard=100` / `ThemeCrossfade=300`)

**Step 4: 跑测试** — PASS

**Step 5: 写 RippleBehavior.cs**(attached Behavior,Button.OnApplyTemplate hook overlay Canvas + Ellipse + 50ms Storyboard + `IsAnimationEnabled=false` 时 0ms 跳到最终状态)

**Step 6: 写 ViewFadeTransitionBehavior.cs**(Behavior<ContentControl>,Content 变化时 200ms opacity fade,开始前 Stop() 旧 Storyboard,最终状态显式设置)

**Step 7: Theme.xaml 延长 `MaterialButton` Style 接入 `behaviors:RippleBehavior.IsEnabled="True"` + `Motion` 资源**

**Step 8: MainWindow.xaml ContentControl 加 `behaviors:ViewFadeTransitionBehavior.IsEnabled="True"`**

**Step 9: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 10: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Animations/MotionSettings.cs \
        src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs \
        src-wpf/ComfyUI.Manager/Behaviors/ViewFadeTransitionBehavior.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        src-wpf/ComfyUI.Manager/MainWindow.xaml \
        tests-wpf/.../Animations/MotionSettingsTests.cs
git commit -m "feat(wpf): add shared motion system and Ripple"
```

---

### Task 9: 页面级 + 状态级动效集成

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/ErrorBanner.xaml` + `.xaml.cs`(接入 250ms `TranslateTransform.Y + Opacity` slide-in/out)
- Modify: `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml`(卡片加 stagger fade-in)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(Check Update 按钮加 pulse)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` + `MainWindow.xaml`(主题切换 300ms root overlay cross-fade 跟 Task 2 接入)
- Modify: `src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs`(`IsAnimationEnabled=false` 时 0ms)
- Create test: `tests-wpf/.../Animations/AnimationDisabledBehaviorTests.cs`(~2 测试)

**Step 1: 写 AnimationDisabledBehaviorTests 失败测试**(`MotionSettings_IsAnimationEnabledFalse_AllAnimationsSkipped` / `MotionSettings_IsAnimationEnabledTrue_AnimationsRun`)

**Step 2: 跑测试** — 失败

**Step 3: ErrorBanner.xaml + .xaml.cs 加 slide-in**(Visibility=Visible 触发 Storyboard `TranslateTransform.Y -40→0 + Opacity 0→1` 250ms,Visibility=Hidden 反向)

**Step 4: DashboardView.xaml 卡片 stagger**(首次 Loaded 后按视觉顺序每隔 100ms 启动 fade+轻微 translate;refresh 后整组重播)

**Step 5: SettingsView.xaml Check Update 按钮 pulse**(`IsChecking` 为 true 时循环 opacity/scale 1.0↔1.05,完成/失败停止)

**Step 6: App.xaml.cs + MainWindow.xaml 主题切换 cross-fade**(根 Window overlay Grid opacity 1→0,swap palette dict,opacity 0→1,300ms)

**Step 7: RippleBehavior 完善 `IsAnimationEnabled=false` 分支**(0ms 跳到最终状态,不创建 Storyboard)

**Step 8: 跑测试** — PASS

**Step 9: 跑全套 build + test** — 0 errors / 0 warnings / 全套 PASS

**Step 10: Commit**
```bash
git add src-wpf/ComfyUI.Manager/Views/ErrorBanner.xaml \
        src-wpf/ComfyUI.Manager/Views/ErrorBanner.xaml.cs \
        src-wpf/ComfyUI.Manager/Views/DashboardView.xaml \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        src-wpf/ComfyUI.Manager/MainWindow.xaml \
        src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs \
        tests-wpf/.../Animations/AnimationDisabledBehaviorTests.cs
git commit -m "feat(wpf): integrate page and status animations"
```

---

### Task 10: 最终整合 + 性能 + close-out

**Files:** 无新增 source file;只调整 i18n + 修 GUI smoke 发现的 bug

**Step 1: 全视图 XAML 重审**(`git diff --stat` 列出所有改过的 XAML,grep `StaticResource` 找硬编码颜色;只 `Motion` / `FontSize` / `Padding` 允许 `StaticResource`,其它颜色/Brush 必须 `DynamicResource`)

**Step 2: 焦点顺序/键盘导航/Tab 顺序测试**(逐 view Tab 一次,确认焦点环在视觉顺序)

**Step 3: Windows 125% / 150% / 200% 缩放下检查**:
- Dashboard 4 卡片换行(Grid 2x2 → 1x4 on narrow)
- Spotlight 浮层 600×400 不超出屏幕
- 侧栏文字截断(220px 宽度,长 label 走 TextTrimming="CharacterEllipsis")

**Step 4: 大量 env/node 数据性能测试**(造 50+ env + 100+ node,测 Spotlight 首次 BuildAsync < 1s,键入 100ms 内响应)

**Step 5: 全套自动化**:
- `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → ~717 PASS / 2 known FAIL / 1 SKIP
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal` → 成功
- `git status --short` → 只 pre-existing `?? full-suite.log` + `?? tools/`

**Step 6: Final review**(opus model,扫所有 10 task commits 整体跨文件质量)

**Step 7: Memory commit**(写 `project_v0_6_9_ui_modernization.md` + MEMORY.md 加 entry)

**Step 8: 收尾**:
```bash
git add docs/superpowers/plans/2026-08-09-ui-modernization.md
git commit -m "docs(plan): v0.6.9 UI Modernization close-out"
```

---

## Verification (end-to-end GUI smoke)

按 16 步测试覆盖:

1. **启动无闪烁**:迁移旧设置后启动 → Splash 后进入 Dark Dashboard,无 light 闪屏
2. **Dashboard 显示**:env 三类统计(running/stopped/undeployed)+ 节点总数 + 最近 5 条操作 + GitHub 最新版本
3. **降级**:断网重启或刷新 → 版本查询失败只显示局部提示,本地统计仍可用
4. **主题切换**:Settings 切 Light → Dark → FollowSystem;逐一打开 Dashboard + 6 view + 主要 dialog;背景/文字/边框/选中/禁用/错误状态全正确
5. **持久化**:重启应用确认主题恢复;旧配置缺 ThemeMode 时保持 Dark 默认
6. **侧栏选中**:依次点 6 入口 → 高亮唯一 + 页面 200ms fade + 原命令仍可执行
7. **Spotlight 打开**:点 SearchBox 或 `Ctrl+K` → 浮层显示,文本框聚焦
8. **Spotlight 搜索**:分别搜 env 名 / 节点名 / Settings 标题 / 操作命令 → 结果实时显示
9. **Spotlight 排名**:精确 > 前缀 > substring > subsequence;Up/Down / Enter / Esc 行为正确
10. **Spotlight 导航**:搜 Catalog 项 → Query 过滤 + 选中目标;搜环境 → DataGrid 选中并滚动可见;搜 Settings → 跳到对应 section
11. **Ripple**:点普通按钮 → 50ms 圆圈扩散,响应快不挡点击;快速连点无残留
12. **ErrorBanner**:触发 → 250ms slide-in;消失 → 反向 slide-out
13. **Dashboard stagger**:刷新 Dashboard → 卡片 100ms stagger fade-in
14. **Check Update pulse**:点检查更新 → 按钮 pulse;完成后停止
15. **快速切换**:连续切页面 + 切主题 → 无闪烁/冻结/透明页面/输入失焦
16. **缩放 + 大量数据**:Windows 125%/150%/200% 缩放下 Dashboard 卡片换行 + Spotlight 边界 + 侧栏文字截断;50+ env 数据下 Spotlight 首次建立索引有加载态,键入无卡顿;系统动画关闭时所有交互直接落最终状态

**自动化测试**:
- `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → 基线 672 + ~45 新 = ~717 PASS / 2 known FAIL / 1 SKIP
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal` → 成功
- `git status --short` → 只 pre-existing `?? full-suite.log` + `?? tools/`
- 走 SDD G15(无 v-bump / 无 zip — UI 改造是 hotfix 级,不打 release)

---

## Risks

| 风险 | 缓解 |
|---|---|
| 某些 view 仍用 StaticResource 或硬编码颜色 → 切主题后局部不刷新 | 同名 palette + 全量 XAML 审计;仅颜色/Brush 强制 DynamicResource;GUI 逐页检查 |
| 逐项替换 App.Resources 产生瞬态不一致(控件显示混合主题) | 原子替换单个 Palette ResourceDictionary;根层 300ms overlay fade |
| Shared Brush 被冻结,`ColorAnimation` 失败 | **不动画共享 Brush**;动画 overlay/root opacity 后交换资源 |
| 搜索每次键入访问 DB | 打开 Spotlight 时构建快照,输入仅走内存评分;结果上限 20 |
| Levenshtein 成本 + 排序难解释 | score-based 加权(exact/prefix/substring/subsequence),不计算编辑距离 |
| 节点全量统计 N+1(逐 env `ListByEnv` 再 sum) | `NodeRepository.CountAllAsync()` 单 SQL |
| GitHub 请求拖慢 Dashboard 首屏 | 本地统计 + 远端版本并行;局部失败;保留 last successful |
| 快速导航启动多个 Storyboard → 页面透明 + 时钟泄漏 | 开始新动画前停止旧;Unloaded 清理;最终状态显式设置 |
| Ripple 每次点击创建视觉对象 → 慢机器掉帧或泄漏 | 轻量 Ellipse + 限制并发 + 完成后移除;`IsAnimationEnabled=false` 时禁用 |
| 主题/搜索/刷新竞争 UI 线程 | I/O + 索引构建放后台;集合发布回 Dispatcher;cancellation token |
| `CurrentView.GetType()` 推断高亮 → 缓存页 + 新建页状态不一致 | `CurrentSection` 单一来源,所有入口统一走导航方法 |
| 系统"减少动画"未尊重 → 可访问性问题 | `MotionSettings.IsAnimationEnabled` 统一开关,所有 Storyboard 检查 |
| 启动 Splash 后立即 Show Dashboard,但 Dashboard 异步加载数据 → 空白闪现 | DashboardViewModel 保留 last snapshot(失败不清空);首次进入显示 skeleton + 加载态 |
| Spotlight 索引构建慢 → 浮层打开有延迟感 | 异步构建 + 浮层显示 loading indicator;用户在输入时结果已就绪 |
| Spotlight 搜 node 结果跨多个 env → 选中无法精确定位 | `SearchTarget` 包含 envId,导航先切 env 再 SelectNode |
| Dashboard 默认页 + Splash 改动组合 → Splash 关后到 Dashboard 之间的视觉过渡 | Splash fade-out 跟 Dashboard fade-in 通过 Crossfade 衔接(已部分有 FadeOutStoryboard) |
| Spotlight 浮层在边角截断 | 测试 4 角位置;Popup 居中策略 fallback |

---

## Critical Files for Implementation (priority order)

1. `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml` + `Palette.Light.xaml` — 双主题基座
2. `src-wpf/ComfyUI.Manager/Services/ThemeService.cs` — 主题切换 atomicity
3. `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — `CurrentSection` 导航契约
4. `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` — Welcome 页布局
5. `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs` — 数据聚合
6. `src-wpf/ComfyUI.Manager/Search/SearchIndex.cs` — 评分核心
7. `src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml` — UI 入口
8. `src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs` — 动效基础设施

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 10 task × (implementer + reviewer) ≈ 20 dispatch + 1 final whole-branch review
- Per-task review gate(sonnet implementer + sonnet reviewer,Task 1/4/6 用 mid-tier 因涉及架构)
- 预计 10 commits on main(T1-T10)+ 1 close-out commit(T10 整合)
- T1+T2 单元测试覆盖核心逻辑(ThemeService / SettingsTheme)
- T3+T5+T7 view 改动编译 verify + 既有测试 PASS
- T4+T6 服务 / 索引逻辑重点测试
- T8+T9 动效只能 GUI smoke 验证(per G6 不写 UI 单元测试)
- T10 全量 + staging rebuild + close-out
