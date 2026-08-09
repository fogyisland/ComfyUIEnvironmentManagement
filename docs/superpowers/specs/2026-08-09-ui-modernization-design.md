# v0.6.9 UI Modernization 设计 Spec

> 双主题 + Dashboard + Spotlight Search + 全量动效

**base SHA:** `b0989c7` (v0.6.8 SHIP-READY, HEAD)

**用户原话**:
> "主界面是否有可优化空间,我感觉现在有点单调"
> "深浅双主题切换 / Dashboard(信息密度高) / 搜 env+节点+设置项+操作命令 / 全量动效(Storyboard 重)"

---

## Context

`ComfyUI.Manager` v0.6.8 ship 后主界面功能完整,但视觉/交互层有 4 个问题:

1. **视觉层次单一** — 单一 Light 主题,所有 view 共用一套 `Surface #FFFBFE / Background #F6F1FB` Material 调色板,无深色模式,长时间使用刺眼。
2. **启动后无 Dashboard 概览** — Splash 关闭后直接进 Environment List,用户需要多次点 tab 才看得到节点总数、最近操作、版本状态。
3. **侧栏按钮无选中状态** — 6 个 sidebar 按钮点击后仅 `CurrentView` 切,无视觉高亮,用户不知道当前在哪个 tab。
4. **无跨功能搜索** — 想跳到 env-X 必须滚动列表找;想跳到 "设置" 的 "BED 部署" section 必须滚 Settings 页;想跑 "打开 extra_model_paths.yaml" 必须打开工具菜单。

外加动效缺乏:view 切换硬切,按钮点击无反馈,ErrorBanner 闪入闪出,Dashboard 卡片同时出现。

**目标**:在不引入新依赖(沿用 WPF/.NET 8 已有能力)的前提下,把主界面从"功能堆叠"升级为"现代双主题 UI"。

---

## 4 大方向(用户确认)

### 方向 1:深浅双主题切换
- **Dark 默认** + Light 备选(沿用现有 `#F6F1FB` 系列)
- 两套独立 `Palette.{Light,Dark}.xaml` 字典,同名键(`BackgroundBrush` / `SurfaceBrush` / `OnSurfaceBrush` / `OutlineBrush` / `ErrorBrush` / `PrimaryBrush` / `PrimaryVariantBrush` / `OnPrimaryBrush`)
- 所有 view XAML 改 `DynamicResource` 引用颜色/Brush
- `Settings.ThemeMode` 持久化(已有字段,无接线)+ 启动时 `App.OnStartup` 加载应用
- `ThemeService.Apply(ThemeMode)` 原子替换 merged dictionary 槽位 + 根 Window 300ms opacity cross-fade
- **不动画共享 Brush**(会被 freeze 失败);动画根 Window overlay opacity

### 方向 2:Dashboard Welcome 页
- 启动默认页(替换当前直接进 Environment List)
- 4 张卡片:环境统计(running / stopped / undeployed / 总数)+ 节点总数(跳 Catalog)+ 最近 5 条操作日志(从 `AppLogger.ReadRecentLines(daysBack=2)`)+ 版本/更新(当前版本 + 检查更新按钮)
- `DashboardService.GetSnapshotAsync(CancellationToken)` 并行聚合 4 类数据;GitHub 失败 → `LatestRelease = null` + 部分降级;env/node 失败 → throw
- 手动刷新按钮 + 首次进入 lazy 加载;失败保留 `LastSnapshot`
- 侧栏顶部新加 "主页" 入口(7 个入口,原 6 个保留)

### 方向 3:Spotlight 全局搜索
- `Ctrl+K` 打开浮层,300×400 compact SearchBox + 600×400 Popup overlay
- 索引覆盖 4 类条目:Environment / Node / SettingsSection / Command
- **打开时构建,键入仅走内存**(G7)— 不允许每次键入访问 SQLite/日志/网络
- Score-based 排名(exact 100 > prefix 80 > any-token-prefix 60 > substring 40 > subsequence 20),无 Levenshtein(G10 不引第三方)
- 大小写/空格/`_`/`-` 归一化;索引最大 1000 项,query 结果上限 20
- Enter 导航 + 选中:Environment → 切到 env tab + 选中;Node → 切到 env + 选 node;SettingsSection → 切到 Settings + 滚到 section;Command → 执行 command

### 方向 4:全量动效(Storyboard 重)
- **Ripple**:attached Behavior,Button 点击时 50ms 圆圈扩散 + 淡出
- **view 切换**:ContentControl fade 200ms,快速切换取消旧 Storyboard
- **ErrorBanner**:slide-in 250ms(`TranslateTransform.Y -40→0 + Opacity 0→1`,**不用 Margin 动画**)
- **Dashboard 卡片**:stagger fade-in 100ms/卡片
- **Check Update pulse**:检查中 opacity/scale 1.0↔1.05 循环
- **主题切换**:根 Window overlay opacity 1→0,swap dict,opacity 0→1(300ms)
- `MotionSettings.IsAnimationEnabled` 统一开关,默认 true,尊重 `SystemParameters.ClientAreaAnimation`

---

## Global Constraints

| # | 约束 | 来源 |
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
| G13 | Splash fade-out 跟 Dashboard fade-in 通过 cross-fade 衔接(已部分有 SplashWindow.xaml:11-17 FadeOutStoryboard) | 启动体验 |
| G14 | Spotlight 索引构建失败 → 浮层仍打开 + 显示 "搜索暂不可用" + 关闭 | 鲁棒性 |
| G15 | 不 bump version / 不发 release zip(per memory `feedback_no_zip.md`) | 跟 v0.6.7.x 惯例一致 |

---

## 设计决策(脑暴期 4 决策)

| 问题 | 决定 |
|---|---|
| 双主题持久化 | `Settings.ThemeMode` 字符串 enum (`Light` / `Dark` / `FollowSystem`),缺失回退 Dark |
| Dashboard 启动时机 | 启动 `MainWindow.Loaded` 后调 `ShowDashboardAsync()`,Splash 关闭后立即显示,无空白闪现 |
| Spotlight 索引粒度 | env / node name / Settings section 静态列表(从 `SettingsView.xaml` 抽 5-6 个 section header)/ MainViewModel 15 commands + SettingsViewModel 2 commands |
| 动效开关 | `MotionSettings.IsAnimationEnabled` 静态 bool,默认 true,读 `SystemParameters.ClientAreaAnimation`,可被未来 Settings 覆盖(本期不接 UI) |

**Why these choices:**
- `FollowSystem` 解析为当前 system theme,但本期不实装 UI 选择项(等 Settings 复用);spec 留口子
- Dashboard 启动 `Loaded` 触发是 WPF 习惯,`ContentRendered` 太晚;`Loaded` 一次足够
- 索引固定 commands 列表 17 项是 MainViewModel 实际有的(ShowXxx / OpenXxx / ExitApp / ShowAbout / ShowDonateQr),不需要 reflection
- `IsAnimationEnabled` 默认 true + 系统设置覆盖,99% 场景用户体验最佳,系统"减少动画"用户也能获一致体验

---

## 数据模型

### 新增 types

```csharp
// Models/DashboardSnapshot.cs
public sealed record DashboardSnapshot(
    EnvironmentCounts EnvironmentCounts,
    int NodeCount,
    IReadOnlyList<string> RecentOperations,  // 5 行原始 log lines
    string? LatestRelease,                   // null = fetch 失败
    bool GitHubFailed);

// Models/EnvironmentCounts.cs (nested 或同文件)
public sealed record EnvironmentCounts(int Running, int Stopped, int Undeployed)
{
    public int Total => Running + Stopped + Undeployed;
}

// Search/SearchTarget.cs
public enum TargetKind { Environment, Node, SettingsSection, Command }
public sealed record SearchTarget(
    TargetKind Kind,
    string? EnvironmentId = null,    // Environment / Node
    string? NodeId = null,            // Node
    string? SectionKey = null,        // SettingsSection
    string? CommandName = null);      // Command
```

### 修改 types

- `Models/Settings.cs`: `ThemeMode` 字段已存在,确认 string enum 命名 + 默认 "Dark"
- `Data/NodeRepository.cs`: `+ Task<int> CountAllAsync(CancellationToken)` 单 SQL `SELECT COUNT(*) FROM scanned_nodes`
- `Services/AppLogger.cs`: `+ IEnumerable<string> ReadRecentLines(int daysBack = 2)` 用 `yield return`,大文件友好

---

## 服务接口

```csharp
// Services/ThemeService.cs
public enum ThemeMode { Light, Dark, FollowSystem }
public interface IThemeService
{
    void Apply(ThemeMode mode);            // 原子替换 merged dict
    ThemeMode Current { get; }
    event EventHandler<ThemeMode>? Applied;
}
public sealed class ThemeService : IThemeService
{
    public ThemeService(ResourceDictionary appResources);
    public void Apply(ThemeMode mode);
    public ThemeMode Current { get; private set; }
    public event EventHandler<ThemeMode>? Applied;
}

// Services/DashboardService.cs
public interface IDashboardService
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct);
}
public sealed class DashboardService : IDashboardService
{
    public DashboardService(
        IEnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        AppLogger logger,
        GitHubVersionService github);       // 4 类并行
}

// Search/SearchIndex.cs
public sealed class SearchIndex
{
    public void Build(IReadOnlyList<SearchEntry> entries);
    public IReadOnlyList<SearchResult> Query(string query, int max = 20);
}

// Services/GlobalSearchService.cs
public interface IGlobalSearchService
{
    Task<SearchIndex> BuildAsync(CancellationToken ct);
}
```

---

## UI 关键设计

### MainWindow.xaml 新布局

```
┌────────────────────────────────────────────────────────────┐
│ Menu: 文件  设置  工具  关于                            [─□×]│
├──────────────┬─────────────────────────────────────────────┤
│              │                                              │
│ 🏠  主页     │                                              │
│              │                                              │
│ 🔍 搜索...   │                                              │
│ ┌──────────┐ │                                              │
│ │ Ctrl+K   │ │                                              │
│ └──────────┘ │       ContentControl (CurrentView)           │
│              │       (ViewFadeTransitionBehavior 200ms)      │
│ 📦 环境      │                                              │
│ 📋 节点目录  │                                              │
│ ⚙  基础环境  │                                              │
│ 🛠  设置     │                                              │
│ 🔄 批量更新  │                                              │
│ 🖥  系统状态 │                                              │
│              │                                              │
│              │                                              │
├──────────────┴─────────────────────────────────────────────┤
│ ErrorBanner (slide-in 250ms)                                │
└────────────────────────────────────────────────────────────┘
```

### Dashboard 4 卡片

```
┌──────────────────────┬──────────────────────┐
│ 环境统计              │ 节点总数              │
│ ┌────┬────┬────┐    │       42              │
│ │ 3  │ 1  │ 2  │    │  → 查看               │
│ │运行 │停止 │未部署│    │                      │
│ └────┴────┴────┘    │                      │
│       6 总数         │                      │
├──────────────────────┼──────────────────────┤
│ 最近操作              │ 版本 / 更新           │
│ [14:32] env-prod 启动 │ 当前 v0.6.8          │
│ [14:28] env-dev 重启  │ GitHub: v0.6.9       │
│ [13:50] BED 部署      │ [检查更新]            │
│ ...                   │                      │
└──────────────────────┴──────────────────────┘
```

### Spotlight 浮层

```
┌─────────────────────────────────────┐
│ 🔍 搜索环境、节点、设置或命令...    │
├─────────────────────────────────────┤
│ ENVIRONMENTS                        │
│ > env-prod                          │
│   env-dev                           │
│ NODES                               │
│   ComfyUI-Manager                   │
│ SETTINGS                            │
│   设置 → 主题                       │
│ COMMANDS                            │
│   打开日志文件夹                    │
└─────────────────────────────────────┘
```

---

## 错误处理

- **ThemeService.Apply 失败** → 回退到 Current 模式 + AppLogger.Error
- **DashboardService 单项失败** → GitHub 失败用 null + GitHubFailed flag;env/node 失败 throw(本地数据不可用)
- **SearchIndex.Build 异常** → 浮层显示 "搜索暂不可用" + 关闭按钮;不阻塞主程序
- **Ripple 在 slow machine** → 限制并发 Ellipse 数 + 完成后 Unloaded 移除
- **Storyboard 快速切换泄漏** → 每次开始前 Stop() 旧 Storyboard;最终状态显式设置
- **键盘焦点丢失** → 主题切换时显式 Focus 恢复 MainContent

---

## 风险与缓解(17 条)

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
| Dashboard 默认页 + Splash 改动组合 → Splash 关后到 Dashboard 之间的视觉过渡 | Splash fade-out 跟 Dashboard fade-in 通过 Crossfade 衔接 |
| Spotlight 浮层在边角截断 | 测试 4 角位置;Popup 居中策略 fallback |

---

## Verification

### 单元测试(基线 672 + ~45 新 = ~717 PASS)

| 测试 | 验证 |
|---|---|
| `ThemeServiceTests` (5) | Apply Light/Dark/FollowSystem 替换 dict;无效值回退 Dark;Current 同步 |
| `SettingsThemeIntegrationTests` (4) | 写/读/缺失默认 Dark/FollowSystem 解析 |
| `MainViewModelNavigationTests` (6) | 每个 Show 命令同步 CurrentSection + CurrentView,缓存页保高亮 |
| `DashboardServiceTests` (8) | env 统计/空 DB/跨日日志/取 5 条/GitHub 失败/并发去重 |
| `NodeRepositoryCountTests` (2) | CountAllAsync 单 SQL + 0 行 DB |
| `AppLoggerReadRecentLinesTests` (3) | 跨日合并/坏行跳过/不存在日期 |
| `DashboardViewModelTests` (6) | 初始态/刷新中/部分失败/手动刷新/重复进入保留数据 |
| `SearchIndexTests` (10) | 精确/前缀/substring/subsequence/中英文/空查询/结果上限/tie-break |
| `SpotlightSearchViewModelTests` (6) | 打开/键入/Enter/Esc/Up-Down/导航分发 |
| `MainViewModelSearchNavigationTests` (5) | 每种 TargetKind 正确导航 |
| `MotionSettingsTests` (3) | 静态读取 + 系统开关 |

### 端到端 GUI smoke(16 步)

1. 启动无闪烁:迁移旧设置后启动 → Splash 后进入 Dark Dashboard,无 light 闪屏
2. Dashboard 显示:env 三类统计 + 节点总数 + 最近 5 条操作 + GitHub 最新版本
3. 降级:断网重启或刷新 → 版本查询失败只显示局部提示,本地统计仍可用
4. 主题切换:Settings 切 Light → Dark → FollowSystem;逐一打开 Dashboard + 6 view + 主要 dialog;背景/文字/边框/选中/禁用/错误状态全正确
5. 持久化:重启应用确认主题恢复;旧配置缺 ThemeMode 时保持 Dark 默认
6. 侧栏选中:依次点 6 入口 → 高亮唯一 + 页面 200ms fade + 原命令仍可执行
7. Spotlight 打开:点 SearchBox 或 `Ctrl+K` → 浮层显示,文本框聚焦
8. Spotlight 搜索:分别搜 env 名 / 节点名 / Settings 标题 / 操作命令 → 结果实时显示
9. Spotlight 排名:精确 > 前缀 > substring > subsequence;Up/Down / Enter / Esc 行为正确
10. Spotlight 导航:搜 Catalog 项 → Query 过滤 + 选中目标;搜环境 → DataGrid 选中并滚动可见;搜 Settings → 跳到对应 section
11. Ripple:点普通按钮 → 50ms 圆圈扩散,响应快不挡点击;快速连点无残留
12. ErrorBanner:触发 → 250ms slide-in;消失 → 反向 slide-out
13. Dashboard stagger:刷新 Dashboard → 卡片 100ms stagger fade-in
14. Check Update pulse:点检查更新 → 按钮 pulse;完成后停止
15. 快速切换:连续切页面 + 切主题 → 无闪烁/冻结/透明页面/输入失焦
16. 缩放 + 大量数据:Windows 125%/150%/200% 缩放下 Dashboard 卡片换行 + Spotlight 边界 + 侧栏文字截断;50+ env 数据下 Spotlight 首次建立索引有加载态,键入无卡顿;系统动画关闭时所有交互直接落最终状态

### 自动化验证

- `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → ~717 PASS / 2 known FAIL / 1 SKIP
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal` → 成功
- `git status --short` → 只 pre-existing `?? full-suite.log` + `?? tools/`
- 走 SDD G15(无 v-bump / 无 zip — UI 改造是 hotfix 级,不打 release)

---

## Task Breakdown

### Task 1: 双主题 ResourceDictionary + ThemeService
T1 立基座(2 palette dict + ThemeService + 5 tests)

### Task 2: Settings 持久化 + 全视图主题迁移
T2 接线 Settings + 6 view XAML DynamicResource 迁移(4 tests)

### Task 3: 主壳层重构 + 侧栏选中状态
T3 MainWindow 220px 侧栏 + SearchBox 占位 + CurrentSection 枚举 + SidebarButtonStyle(6 tests)

### Task 4: Dashboard 数据聚合服务
T4 DashboardService + DashboardSnapshot + NodeRepository.CountAllAsync + INodeRepository + AppLogger.ReadRecentLines(13 tests)

### Task 5: DashboardView + 设为启动默认页
T5 DashboardView XAML/VM/cs + ShowDashboardCommand + i18n(6 tests)

### Task 6: SearchIndex + score-based 排名
T6 SearchIndex + SearchEntry/Result/Target + GlobalSearchService(10 tests)

### Task 7: Spotlight UI + 快捷键 + 导航
T7 SpotlightSearchBox XAML/cs + SpotlightSearchViewModel + Ctrl+K + NavigateToTarget(11 tests)

### Task 8: 动效基础 + Ripple Behavior
T8 MotionSettings + RippleBehavior + ViewFadeTransitionBehavior(3 tests)

### Task 9: 页面级 + 状态级动效集成
T9 ErrorBanner slide + Dashboard stagger + Check Update pulse + 主题切换 cross-fade(2 tests)

### Task 10: 最终整合 + 性能 + close-out
T10 全量 XAML 审 + 缩放 + 大量数据 + final review + memory commit

---

## Critical files (full list)

**新增(19 文件):**
- `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml` + `Palette.Dark.xaml`
- `src-wpf/ComfyUI.Manager/Services/ThemeService.cs` + `DashboardService.cs` + `GlobalSearchService.cs`
- `src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs`
- `src-wpf/ComfyUI.Manager/Data/INodeRepository.cs`
- `src-wpf/ComfyUI.Manager/Converters/SectionEqualityToBoolConverter.cs`
- `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` + `.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs`
- `src-wpf/ComfyUI.Manager/Controls/SpotlightSearchBox.xaml` + `.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/SpotlightSearchViewModel.cs`
- `src-wpf/ComfyUI.Manager/Search/SearchIndex.cs` + `SearchEntry.cs` + `SearchResult.cs` + `SearchTarget.cs`
- `src-wpf/ComfyUI.Manager/Animations/MotionSettings.cs`
- `src-wpf/ComfyUI.Manager/Behaviors/RippleBehavior.cs` + `ViewFadeTransitionBehavior.cs`

**修改(15+ 文件):**
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` + `.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(+ `SelectEnvironment`)
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(+ `SelectNode`)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- `src-wpf/ComfyUI.Manager/Data/NodeRepository.cs`(+ `CountAllAsync`)
- `src-wpf/ComfyUI.Manager/Services/AppLogger.cs`(+ `ReadRecentLines`)
- `src-wpf/ComfyUI.Manager/App.xaml` + `.xaml.cs`
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(剥离 colors/brushes,只留 styles)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` / `CatalogView.xaml` / `BaseEnvView.xaml` / `SettingsView.xaml` / `SystemStatusView.xaml` / `BulkUpdateDialog.xaml` / `ErrorBanner.xaml` + `.xaml.cs` / `SplashWindow.xaml`

**测试新增(~45 测试):**
- `tests-wpf/.../Services/ThemeServiceTests.cs` (5)
- `tests-wpf/.../Services/SettingsThemeIntegrationTests.cs` (4)
- `tests-wpf/.../ViewModels/MainViewModelNavigationTests.cs` (6)
- `tests-wpf/.../Services/DashboardServiceTests.cs` (8)
- `tests-wpf/.../Data/NodeRepositoryCountTests.cs` (2)
- `tests-wpf/.../Services/AppLoggerReadRecentLinesTests.cs` (3)
- `tests-wpf/.../ViewModels/DashboardViewModelTests.cs` (6)
- `tests-wpf/.../Search/SearchIndexTests.cs` (10)
- `tests-wpf/.../ViewModels/SpotlightSearchViewModelTests.cs` (6)
- `tests-wpf/.../ViewModels/MainViewModelSearchNavigationTests.cs` (5)
- `tests-wpf/.../Animations/MotionSettingsTests.cs` (3)
