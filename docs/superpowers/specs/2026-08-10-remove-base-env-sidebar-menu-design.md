# Remove Base Environment Sidebar Menu — Design

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` after plan is approved.

**Goal:** 删 ComfyUI Manager 侧栏 "基础环境" 菜单项 + 关联 dead code;**完全保留** per-env 基础环境安装/卸载功能(env-list 工具栏 + per-env 行内按钮),只少 1 个全局入口。

**Architecture:** View-only 菜单入口删 + dead code 清理 + 1 处副作用迁移(`MarkIncompatibleOlderVersions` 从 `BaseEnvViewModel` 构造时调用 → `EnvironmentListViewModel.ShowEnvironments` 调用)。VM 接口 / Settings / 服务 / 数据库 不变。3 task SDD 粒度(跟 v0.6.11++ 同款)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 既有 ViewModelBase / RelayCommand pattern

**base SHA:** `9de9478` (v0.6.11+ Catalog UI polish SHIP-READY;846/0/1 baseline)

---

## Context

v0.6.11+ Catalog UI polish SHIP-READY 后,用户桌面验 staging 时反馈 "去掉基础环境菜单"。调研发现侧栏 `BaseEnvView` 菜单是 1 个冗余入口 — 用户完全可以从 env-list 工具栏 "基础环境部署" 按钮或 per-env 行内按钮完成 BED 安装/卸载(这 2 个入口 v0.6.5.19/v0.6.5.19.1 已就位)。删侧栏菜单 + 专用 dead code,保留所有 per-env BED 功能。

## 用户原话
- "去掉基础环境菜单"
- "功能还是在的,只是删除侧边栏连接,因为我们依然需要安装基础环境"
- "完整删文件"(确认 dead code 不留)

## Global Constraints

| # | Constraint |
|---|---|
| **G1** | **保留所有 per-env BED 功能**:env-list 工具栏 "基础环境部署" 按钮 + per-env 行内 "卸载基础环境" 按钮 + BED 徽章/状态/profile 列 + `BaseEnvInstaller` / `BaseEnvUninstaller` / `BaseEnvProgressDialog` / `BaseEnvProfilePickerDialog` 服务 + `BedDisplay`/`BedStatus` 显示 |
| **G2** | **删侧栏 1 个入口 + 专用 dead code**:`MainWindow.xaml` 侧栏 RadioButton + `MainViewModel.ShowBaseEnv*` + `MainSection.BaseEnv` enum + `BaseEnvView.xaml/cs` + `BaseEnvViewModel.cs` + 关联 resx + Spotlight 命令 |
| **G3** | **VM 接口冻结**:`EnvironmentListViewModel` 接口不变;`BaseEnvViewModel` 删后,`MainViewModel` ctor 中仅 per-env 共享参数 (`_baseEnvInstaller`/`_profileLoader`/`_pytorchVersionDirectory`/`_appDataDir`) 保留 — 它们 `EnvironmentListViewModel` 也用 |
| **G4** | **MarkIncompatibleOlderVersions 必须迁**:从 `BaseEnvViewModel` 构造调用 → `EnvironmentListViewModel.ShowEnvironments` 路径调用;否则 torch<2.4 profile "不推荐" 后缀失效,UX regression |
| **G5** | **不引入新依赖**;所有现有 resx / Brush / Style / Command pattern 复用 |
| **G6** | **测试不写脆弱 UI 行为**:MainViewModel / EnvListVM / MainSectionNameProvider / GlobalSearchService 单测覆盖;STA-thread headless 不强制(本任务无新 XAML 改动) |
| **G7** | **resx 字符串严格删**:删 `SectionName_BaseEnv` 中英文双语;不删任何被其他 view 引用的 key |
| **G8** | **每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer,严格匹配 progress.md ledger** |
| **G9** | **Settings 字段冻结**:不动 Settings.cs / appsettings.json / 任何 UI preferences |

---

## Design

### 1. Removed sidebar entry point

**`src-wpf/ComfyUI.Manager/MainWindow.xaml:98-101`** — 删侧栏 `<RadioButton Content="基础环境" Command="{Binding ShowBaseEnvCommand}" ConverterParameter=BaseEnv>` 整段(含周围 margin)。侧栏剩 5 个 RadioButton。

### 2. Removed MainViewModel members

**`src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:**
- Line 23 — 删 `BaseEnv` from `MainSection` enum
- Line 136 — 删 `ShowBaseEnvCommand` property
- Line 228 — 删 `ShowBaseEnvCommand = new RelayCommand(_ => ShowBaseEnv())` 初始化
- Lines 307-315 — 删 `ShowBaseEnv()` 方法体
- Line 515 — 删 `ResolveCurrentViewName()` switch 中 `"BaseEnvView" => "基础环境"` arm

**保留 ctor 参数**(`_baseEnvInstaller` / `_profileLoader` / `_pytorchVersionDirectory` / `_appDataDir`):`EnvironmentListViewModel.ShowEnvironments` 也用,不删。

### 3. Removed MainSectionNameProvider arm

**`src-wpf/ComfyUI.Manager/Services/MainSectionNameProvider.cs:20`** — 删 `MainSection.BaseEnv => "基础环境"` arm(若该文件实际行号不同,以 grep 结果为准)。fallback "未知 section" 已存在。

### 4. Removed GlobalSearchService Spotlight command

**`src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs:145`** — 删 `("ShowBaseEnv", "基础环境")` entry。

### 5. Removed resx strings

**`src-wpf/ComfyUI.Manager/Resources/Strings.resx`** + **`Strings.zh-CN.resx`** — 删 `SectionName_BaseEnv` key(双语)。先 grep 确认无其他 view 引用。

### 6. Migrated MarkIncompatibleOlderVersions

**From:** `ViewModels/BaseEnvViewModel.cs` ctor 调用
**To:** `ViewModels/EnvironmentListViewModel.cs` `ShowEnvironments` 调用

**具体:**
- `EnvironmentListViewModel` ctor 加 `BaseEnvProfileLoader?` 参数(可选,default null — 向后兼容)
- `App.xaml.cs` DI 顺序中,`EnvironmentListViewModel` ctor 在 `BaseEnvProfileLoader` 已构造后调(现已是这顺序,验证即可)
- `ShowEnvironments()` 方法体顶部加 `_profileLoader?.MarkIncompatibleOlderVersions();`(catch + log,不抛 — 跟原 BaseEnvViewModel 行为等价)
- 删 `BaseEnvViewModel` 后,这个调用就只剩 EnvListVM 一处

**行为等价:**
- 用户启动 app → env-list 自动加载 → mark 跑一次 → BED profile "不推荐" suffix 生效
- 之前是用户启动 → 点 sidebar 基础环境 → 切到 BaseEnvView → mark 跑一次
- 差异:用户不打开 env-list 就看不到 "不推荐" 后缀 — 但 BED install 时 picker dialog 仍会显示 "不推荐",所以不影响装机决策

### 7. Deleted dead files

完整删除(无 menu entry,只剩菜单专用):
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`

**Deleted test files**(若存在):
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs`

---

## File Structure

| 文件 | 操作 |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Modify:删 sidebar RadioButton |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | Modify:删 5 个 member(BaseEnv enum / ShowBaseEnvCommand / ShowBaseEnv() / ResolveCurrentViewName arm / ctor init) |
| `src-wpf/ComfyUI.Manager/Services/MainSectionNameProvider.cs` | Modify:删 BaseEnv arm |
| `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs` | Modify:删 "ShowBaseEnv" Spotlight entry |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` | Modify:删 SectionName_BaseEnv key |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | Modify:删 SectionName_BaseEnv key |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Modify:ctor 加可选 BaseEnvProfileLoader? + ShowEnvironments 调 MarkIncompat |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | Modify:验证 EnvListVM DI 顺序(profileLoader 已先于 EnvListVM 构造) |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml` | Delete |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs` | Delete |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs` | Delete |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs` | Delete (if exists) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs` | Modify:删 BaseEnv 相关测试 + 加断言 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/MainSectionNameProviderTests.cs` | Modify:删 BaseEnv arm 测试 + 加 fallback 测试(if exists) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs` | Modify:删 "ShowBaseEnv" 测试 + 加断言(if exists) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` | Modify:加 MarkIncompatOnLoad 测试 |

**未触及文件**(G1 + G3 冻结):
- `Services/BaseEnvInstaller.cs` / `BaseEnvUninstaller.cs` / `BaseEnvProfileLoader.cs` / `BaseEnvProgressDialog.xaml/cs` / `BaseEnvProgressViewModel.cs` / `BaseEnvProfilePicker*.cs` / `BaseEnvUninstallStatusViewModel.cs`
- `Models/BaseEnvProfile.cs` / `BaseEnvUninstallStatus.cs`
- `Views/EnvironmentListView.xaml`(只 EnvListVM 改,view 不动)
- `Settings.cs` / 所有 SQLite schema / 所有 dialogs

---

## Risks

| 风险 | 缓解 |
|---|---|
| 用户原习惯点侧栏菜单 → 找不到 → UX regression | GUI smoke 第一步就验;env-list tab 加 tooltip 提示 "环境 → 基础环境部署 按钮";观察一轮用户反馈 |
| MarkIncompat 不打开 env-list 不跑 → torch<2.4 后缀丢失 | 装机入口(picker dialog)仍显 "不推荐";不影响装机决策 |
| resx 删 key → 运行时 XAML 引用 missing resource 抛异常 | Step 1 grep 全 codebase 找 `SectionName_BaseEnv` 引用(应该只在 MainSectionNameProvider);若有遗漏,XAML load 即抛,test 会 catch |
| `BaseEnvProfileLoader` DI 顺序变更影响其他 consumer | 验证 App.xaml.cs 现有顺序;DI 已正确 |
| `EnvironmentListViewModel` ctor 签名改 → 现有测试构造适配 | ctor 加可选参数 default null(向后兼容);既有测试不传也跑 |
| 删 BaseEnvViewModel 后,`MainViewModel` 中 `_baseEnvInstaller` 等参数实际只有 EnvListVM 用 | 保留(G3 冻结,共享 ctor 参数)— 即使 MainViewModel 自己不用,也不删(避免重写 EnvListVM ctor 注入逻辑) |

---

## Testing Strategy

### Unit tests(必须)

1. **MainViewModelTests**:删 `ShowBaseEnvCommand` 存在性测试 + 加新断言 `MainSection.BaseEnv` 不存在(用 `Assert.DoesNotContain(MainSection.BaseEnv, ...)`);删 `ShowBaseEnv` 方法不存在断言
2. **EnvironmentListViewModelTests**:
   - 加 `ShowEnvironments_CallsMarkIncompatOnce`:env-list 加载触发 MarkIncompat 1 次(用 fake profileLoader + callCount assertion)
   - 加 `ShowEnvironments_ProfileLoaderNull_NoThrow`:profileLoader=null 时 ShowEnvironments 不抛(向后兼容)
3. **MainSectionNameProviderTests**:删 `BaseEnv` arm 测试;加 fallback "未知 section" 测试(传入未知 enum value → 返回 fallback string)
4. **GlobalSearchServiceTests**:删 "ShowBaseEnv" 命令存在测试;加断言该命令不存在(用 `Assert.DoesNotContain`)

### Manual GUI smoke(用户桌面,SHIPPED 后)

1. 启动 staging → 侧栏确认只剩 5 个按钮,无 "基础环境"
2. 点 env-list tab → 加载 → BED profile 列有 "不推荐" 后缀(torch<2.4 profile)
3. 选中 env → 工具栏 "基础环境部署" 按钮仍可用 → 弹出 picker dialog
4. per-env 行内 "卸载基础环境" 按钮仍可用
5. BED 徽章/状态显示正常(Success/Secondary/Error/Outline 4 色)
6. Spotlight (Ctrl+K) → 搜 "基础环境" → 不再弹出 ShowBaseEnv 命令
7. 状态栏 section label 在 BaseEnv 已删后,fallback 正常

---

## Verification (end-to-end)

按顺序验证 3 task commit 全 PASS:

```bash
# T1: MainViewModel + MainWindow + MainSectionNameProvider + GlobalSearchService + resx
git status --short   # 仅 5 文件 modified
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# T2: MarkIncompat 迁移 + EnvListVM 测试
git status --short   # EnvListVM.cs + App.xaml.cs + EnvListVM tests
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal  # 全 PASS

# T3: 删 BaseEnvView/BaseEnvViewModel + 测试更新 + 删 BaseEnvVM tests
git status --short   # 3 文件 deleted + 测试 updated
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 846/0/1 baseline 持平(可能 847/0/1 如加 2 新测试)
```

最终 (opus review + MEMORY + staging rebuild):
```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal   # 0/0
```

---

## Carry-forward(预期)

- 用户桌面验后,若 UX regression 反馈频繁,可考虑:
  - env-list 工具栏 "基础环境部署" 按钮改名为 "安装基础环境" 更显眼
  - BED picker dialog 增加快捷入口(顶部菜单 "工具" 项)
- `BaseEnvProfileLoader.MarkIncompatibleOlderVersions` 当前在 EnvListVM load 调;若用户报告 "重启后 '不推荐' 后缀没出现",考虑加到 `BaseEnvInstaller.Install` 调,或 `App.OnStartup`(权衡)

---

## Scope Check

**Focused:** 单菜单删除 + dead code 清理 + 1 处副作用迁移。无新功能,无架构变更,无 VM 接口扩张,无 DB 变更,无 Settings 字段。**单一实施 plan 覆盖完整**。

**Decompose?** 不需要。3 task 自然顺序(MainViewModel 清理 → MarkIncompat 迁移 → 删 dead files),相互依赖但 scope 独立。