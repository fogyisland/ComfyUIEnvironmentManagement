# Remove Base Environment Sidebar Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删 ComfyUI Manager 侧栏 "基础环境" 菜单项 + 全部 dead code,保留所有 per-env 基础环境安装/卸载功能。

**Architecture:** 3 task SDD — T1 MarkIncompat 迁移 (TDD 新测试) → T2 侧栏菜单 + dead refs 清理 (5 文件 edits + 4 测试文件更新) → T3 删 BaseEnvView/BaseEnvViewModel dead 文件。T4 opus final review + MEMORY + staging rebuild。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 既有 ViewModelBase / RelayCommand / App.xaml.cs DI pattern

**base SHA:** `9de9478` (v0.6.11+ Catalog UI polish SHIP-READY; 846/0/1 baseline)
**spec commit:** `f9ed8fe` (BaseEnv sidebar removal design)

---

## Global Constraints

(来自 spec G1-G9,精确引用)

| # | Constraint |
|---|---|
| **G1** | **保留所有 per-env BED 功能**:env-list 工具栏 "基础环境部署" 按钮 + per-env 行内 "卸载基础环境" + BED 徽章/状态/profile 列 + `BaseEnvInstaller`/`BaseEnvUninstaller`/`BaseEnvProgressDialog`/`BaseEnvProfilePickerDialog` 服务 |
| **G2** | **删侧栏 1 个入口 + dead code**:`MainWindow.xaml` RadioButton + `MainViewModel.ShowBaseEnv*` + `MainSection.BaseEnv` enum + `BaseEnvView.xaml/cs` + `BaseEnvViewModel.cs` + 关联 resx + Spotlight 命令 |
| **G3** | **VM 接口冻结**:`EnvironmentListViewModel` 接口不变(G2 不动 per-env);`MainViewModel` ctor 中仅 per-env 共享参数(`_baseEnvInstaller`/`_profileLoader`/`_pytorchVersionDirectory`/`_appDataDir`)保留 — EnvListVM 也用 |
| **G4** | **MarkIncompatibleOlderVersions 必须迁**:从 `BaseEnvViewModel` 构造调用 → `EnvironmentListViewModel.ShowEnvironments` 路径调用;否则 torch<2.4 profile "不推荐" 后缀失效 |
| **G5** | **不引入新依赖**;复用现有 resx / RelayCommand / DI pattern |
| **G6** | **测试覆盖**:MainViewModel / EnvListVM / MainSectionNameProvider / GlobalSearchService 单测更新;无新 XAML(无 STA load test) |
| **G7** | **resx 字符串严格删**:删 `SectionName_BaseEnv` 中英文双语;不删任何被其他 view 引用的 key |
| **G8** | **每 task 单独 commit + 单独 SDD subagent dispatch + task reviewer** |
| **G9** | **Settings 字段冻结**:不动 Settings.cs / appsettings.json / UI preferences |

---

## File Structure

| 文件 | 操作 |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Modify:删 sidebar RadioButton (line 96-103 area) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | Modify:删 5 member (BaseEnv enum + ShowBaseEnvCommand + ShowBaseEnv() + ResolveCurrentViewName arm + ctor init) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs` | Modify:删 BaseEnv arm (line 20) |
| `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs` | Modify:删 ("ShowBaseEnv", "基础环境") entry (line 145) |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` | Modify:删 SectionName_BaseEnv key (line 450 area) |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | Modify:删 SectionName_BaseEnv key (line 258 area) |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | Modify:ctor 加可选 `BaseEnvProfileLoader? profileLoader = null` + `ShowEnvironments()` 顶调 MarkIncompat |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | Modify:验证 EnvListVM DI 顺序(profileLoader 先于 EnvListVM 构造,EnvListVM 注入 `_profileLoader`) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs` | Modify:删 BaseEnv 测试 + 加新断言 `MainSection.BaseEnv` 不存在 + `ShowBaseEnvCommand` 不存在 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` | Modify:加 2 测试 `ShowEnvironments_CallsMarkIncompatOnce` + `ShowEnvironments_ProfileLoaderNull_NoThrow` |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainSectionNameProviderTests.cs` | Modify:删 BaseEnv arm 测试 + 加 fallback 测试(if exists) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs` | Modify:删 "ShowBaseEnv" 命令测试 + 加新断言该命令不存在(if exists) |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml` | Delete |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs` | Delete |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs` | Delete |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs` | Delete (if exists) |

**未触及文件**(G1 + G3 冻结):
- `Services/BaseEnvInstaller.cs` / `BaseEnvUninstaller.cs` / `BaseEnvProfileLoader.cs` / `BaseEnvProgressDialog.xaml/cs` / `BaseEnvProgressViewModel.cs` / `BaseEnvProfilePicker*.cs` / `BaseEnvUninstallStatusViewModel.cs`
- `Models/BaseEnvProfile.cs` / `BaseEnvUninstallStatus.cs`
- `Views/EnvironmentListView.xaml`(只 EnvListVM 改,view 不动)
- `Settings.cs` / SQLite schema

---

## Task 1: MarkIncompatibleOlderVersions 迁移 (TDD)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(ctor 加可选 `BaseEnvProfileLoader? profileLoader = null` + `ShowEnvironments()` 顶部调 `_profileLoader?.MarkIncompatibleOlderVersions()`)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(构造 EnvListVM 时传 `_profileLoader`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(加 2 测试)

**Interfaces:**
- Consumes: 既有 `BaseEnvProfileLoader.MarkIncompatibleOlderProfiles()` 方法 (instance method, void return, throws on file IO error)
- Consumes: `EnvironmentListViewModel.ShowEnvironments()` (async method, called when env-list tab opens)
- Produces: EnvListVM ctor 新签名 `EnvironmentListViewModel(..., BaseEnvProfileLoader? profileLoader = null, ...)` — 默认 null 向后兼容

---

- [ ] **Step 1: 读现有文件定位插入点**

读 `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`,找:
- ctor 签名(line ~50-80)
- `ShowEnvironments()` 方法体(line ~250-300)

读 `src-wpf/ComfyUI.Manager/App.xaml.cs`,找 EnvListVM 构造点(line ~180-200 区域),确认 `BaseEnvProfileLoader` 已在 EnvListVM 之前构造。

读 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` 末尾,找现有 ctor 调用 helper 方法(可能是 `BuildEnvListViewModel(...)` factory)。

- [ ] **Step 2: 写 failing 测试 — `ShowEnvironments_CallsMarkIncompatOnce`**

在 `EnvironmentListViewModelTests.cs` 末尾追加:

```csharp
[Fact]
public async Task ShowEnvironments_CallsMarkIncompatOnce()
{
    var profileLoader = new RecordingProfileLoader();
    var vm = BuildEnvListViewModel(profileLoader: profileLoader);
    await vm.ShowEnvironmentsAsync(); // 或 SyncShowEnvironments — 看现有接口

    Assert.Equal(1, profileLoader.MarkIncompatCallCount);
}

private sealed class RecordingProfileLoader : BaseEnvProfileLoader
{
    public int MarkIncompatCallCount { get; private set; }

    public RecordingProfileLoader() : base(/* 既有 ctor params */) { }

    public override void MarkIncompatibleOlderProfiles()
    {
        MarkIncompatCallCount++;
    }
}
```

**重要**:先 grep `BaseEnvProfileLoader` ctor 看实际签名(可能是 `(IEnvironmentRepository envRepo, string appDataDir)` 或类似),用真实参数;`MarkIncompatibleOlderProfiles` 实际方法名也以 grep 为准(可能是 `MarkIncompatibleOlderProfiles` 或 `MarkIncompatibleOlderVersions`)。**如果方法不是 virtual/sealed 不能 override,改用**:`RecordingProfileLoader` 通过 wrapper 实现接口(若有 `IBaseEnvProfileLoader`),或观察现有测试 fake pattern。

如果 `ShowEnvironments` 不是 async / 不是返回 Task,看实际签名同步调用即可。

- [ ] **Step 3: 跑测试确认 fail**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelTests.ShowEnvironments_CallsMarkIncompatOnce" -v minimal
```

Expected: **FAIL** (vm ctor 还没接受 profileLoader 参数 / ShowEnvironments 还没调 MarkIncompat)。

- [ ] **Step 4: 实现 EnvListVM ctor + ShowEnvironments 改动**

修改 `EnvironmentListViewModel.cs`:

1. ctor 末尾加可选参数 `BaseEnvProfileLoader? profileLoader = null`,存到 `private readonly BaseEnvProfileLoader? _profileLoader;` field(放 ctor 第一行,与其他 readonly field 同区域)
2. `ShowEnvironments()` 方法体顶部加:
   ```csharp
   try { _profileLoader?.MarkIncompatibleOlderProfiles(); }
   catch (Exception ex) { _logger?.Warn($"MarkIncompatible failed: {ex.Message}"); }
   ```
   (跟原 `BaseEnvViewModel` ctor 中同款 try/catch + log 行为等价;如果 EnvListVM 没有 `_logger` 字段,直接吞异常或加 `AppLogger?` 可选 ctor 参数 — 看既有 pattern)

- [ ] **Step 5: 跑测试确认 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelTests.ShowEnvironments_CallsMarkIncompatOnce" -v minimal
```

Expected: **PASS**。

- [ ] **Step 6: 写 failing 测试 — `ShowEnvironments_ProfileLoaderNull_NoThrow`**

```csharp
[Fact]
public async Task ShowEnvironments_ProfileLoaderNull_NoThrow()
{
    var vm = BuildEnvListViewModel(profileLoader: null);  // 默认 null
    await vm.ShowEnvironmentsAsync();
    // 不抛 = PASS
}
```

跑测试确认 PASS(profileLoader=null 时 `_profileLoader?.Method()` short-circuit 不抛)。

- [ ] **Step 7: 改 App.xaml.cs 传 profileLoader**

找 EnvListVM 构造点:
```csharp
_environmentListViewModel = new EnvironmentListViewModel(/* 既有 args */);
```
改成:
```csharp
_environmentListViewModel = new EnvironmentListViewModel(/* 既有 args */, profileLoader: _profileLoader);
```

(`_profileLoader` 已在 App.xaml.cs 顶部构造好,直接引用即可。)

- [ ] **Step 8: Build 验证**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 警告 0 错误。

- [ ] **Step 9: 全套测试无回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

Expected: 846 PASS / 0 FAIL / 1 SKIP baseline 持平 (或 +1/+2 如新测试覆盖其他路径,0 回归)。

- [ ] **Step 10: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs
git commit -m "feat(wpf): migrate MarkIncompatibleOlderProfiles from BaseEnvViewModel to EnvListVM"
```

---

## Task 2: 侧栏菜单 + dead refs 清理

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`(删 sidebar RadioButton)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(删 5 member)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs`(删 BaseEnv arm)
- Modify: `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs`(删 ("ShowBaseEnv", "基础环境"))
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx`(删 SectionName_BaseEnv)
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`(删 SectionName_BaseEnv)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs`(删 BaseEnv 测试 + 加 absence 断言)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainSectionNameProviderTests.cs`(如存在,删 BaseEnv arm 测试 + 加 fallback 测试)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs`(如存在,删 "ShowBaseEnv" 测试 + 加 absence 断言)

**Interfaces:**
- Consumes: T1 已经把 `MarkIncompatibleOlderProfiles` 迁到 EnvListVM;`BaseEnvViewModel` 现在**只剩** sidebar menu 专用功能(即将在 T3 删)
- Produces: 侧栏无 "基础环境" entry;`MainSection` enum 少 1 值;`MainSectionNameProvider` 少 1 arm;`GlobalSearchService` 少 1 Spotlight 命令;`Strings.resx` 双语少 1 key

---

- [ ] **Step 1: 删 MainWindow.xaml sidebar RadioButton**

打开 `src-wpf/ComfyUI.Manager/MainWindow.xaml`,找 line 96-103 的侧栏 `<RadioButton Content="基础环境" Command="{Binding ShowBaseEnvCommand}" ConverterParameter=BaseEnv>` 整段(包含 surrounding margin / container):

```xml
<RadioButton Content="基础环境"
             Command="{Binding ShowBaseEnvCommand}"
             ConverterParameter=BaseEnv
             Style="{StaticResource SidebarRadioButtonStyle}" />
```

**完整删** 整段 + 周围 margin StackPanel slot。注意保留其他 5 个 sidebar RadioButton 不动。

- [ ] **Step 2: 删 MainViewModel.cs 的 5 member**

打开 `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`,依次删:

1. **Line 23 区域** `MainSection.BaseEnv`:
   ```csharp
   public enum MainSection
   {
       Dashboard,
       Catalog,
       BaseEnv,        // ← 删这行
       Environments,
       Settings,
       SystemStatus
   }
   ```

2. **Line 136** `ShowBaseEnvCommand` property:
   ```csharp
   public RelayCommand ShowBaseEnvCommand { get; }    // ← 删整行
   ```

3. **Line 228** ctor init:
   ```csharp
   ShowBaseEnvCommand = new RelayCommand(_ => ShowBaseEnv());    // ← 删整行
   ```

4. **Lines 307-315** `ShowBaseEnv()` 方法体 — 删整个方法块(包括 `private void ShowBaseEnv() { ... }` 13 行)

5. **Line 515 区域** `ResolveCurrentViewName()` switch arm — 找:
   ```csharp
   "BaseEnvView" => "基础环境",    // ← 删这行
   ```
   删整行 (保留其他 arm)。

- [ ] **Step 3: 删 MainSectionNameProvider.cs BaseEnv arm**

打开 `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs`,line 20:
```csharp
MainSection.BaseEnv => Get("SectionName_BaseEnv", "基础环境"),    // ← 删整行
```

如果 switch 表达式其他 case 用 `=>` 形式,删这行后其他 case 仍编译;如果有逗号 trailing,检查下一个 case 前是否需要补 `,`。

- [ ] **Step 4: 删 GlobalSearchService.cs Spotlight entry**

打开 `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs`,line 145:
```csharp
("ShowBaseEnv",                      "基础环境"),    // ← 删整行
```

注意 tuple 列表中的 comma — 删行后上一个 tuple 的 `,` trailing 保持不变。

- [ ] **Step 5: 删 Strings.resx 双语 SectionName_BaseEnv**

打开 `src-wpf/ComfyUI.Manager/Resources/Strings.resx`,line 450 区域:

```xml
<data name="SectionName_BaseEnv" xml:space="preserve">
  <value>基础环境</value>
</data>
```

**完整删** 这 3 行(含前后空行 — 看实际格式)。

同样删 `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` line 258 区域同款 3 行。

**验证 step 5 没漏引用**:
```bash
grep -rn "SectionName_BaseEnv" src-wpf tests-wpf
```
Expected: 0 命中(只剩 T2-Step-2-5 已删的 0 处)。

- [ ] **Step 6: 更新 MainViewModelTests**

打开 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs`,搜 `BaseEnv` / `ShowBaseEnv`:

**删除** 任何引用 `ShowBaseEnvCommand` / `ShowBaseEnv()` / `MainSection.BaseEnv` 的测试方法。

**追加** absence 断言测试:

```csharp
[Fact]
public void MainViewModel_DoesNotExposeBaseEnvSection()
{
    // G2:侧栏菜单删除后,VM 不暴露 ShowBaseEnvCommand / BaseEnv section
    var vm = BuildMainViewModel();
    Assert.Null(vm.ShowBaseEnvCommand); // property 已删 → 编译期保证;运行期 null check
    
    // MainSection.BaseEnv enum value 已删 → 编译期保证;运行时反射扫描防回归
    var sectionType = typeof(MainSection);
    Assert.DoesNotContain(sectionType.GetEnumValues(), v => v?.ToString() == "BaseEnv");
}
```

如果 `MainViewModel` 没有 `BuildMainViewModel` factory helper,看测试类顶部既有 pattern 构造 vm。

- [ ] **Step 7: 更新 MainSectionNameProviderTests(若存在)**

打开 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainSectionNameProviderTests.cs`:

**删除** 任何引用 `MainSection.BaseEnv` 的测试。

**追加** fallback 测试:

```csharp
[Fact]
public void GetName_UnknownSection_ReturnsFallback()
{
    var provider = new MainSectionNameProvider(/* 既有 deps */);
    // 强转一个不在 enum 内的值 — 用 default 或越界 int
    var unknown = (MainSection)999;
    var name = provider.GetName(unknown);
    Assert.False(string.IsNullOrEmpty(name)); // fallback 不为空
}
```

如该测试文件不存在,skip 这一步。

- [ ] **Step 8: 更新 GlobalSearchServiceTests(若存在)**

打开 `tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs`:

**删除** 任何引用 "ShowBaseEnv" 的测试。

**追加** absence 断言:

```csharp
[Fact]
public async Task BuildAsync_DoesNotIncludeShowBaseEnvCommand()
{
    var service = new GlobalSearchService(/* 既有 deps */);
    var index = await service.BuildAsync();
    Assert.DoesNotContain(index.Entries, e => e.CommandId == "ShowBaseEnv");
}
```

如该测试文件不存在,skip 这一步。

- [ ] **Step 9: Build 验证**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 警告 0 错误。

如果编译失败:
- `MainSection.BaseEnv` 未删干净 → grep 全 codebase
- `ShowBaseEnvCommand` 仍引用 → grep MainViewModel + MainWindow.xaml
- resx `SectionName_BaseEnv` 已删但代码仍引用 → grep 全 codebase

- [ ] **Step 10: 全套测试无回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

Expected: 846 PASS / 0 FAIL / 1 SKIP baseline 持平(T1 +2 / T2 +1~+3 测试 net,0 回归)。

- [ ] **Step 11: Commit**

```bash
git add src-wpf/ComfyUI.Manager/MainWindow.xaml \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs \
        src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs \
        src-wpf/ComfyUI.Manager/Resources/Strings.resx \
        src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainSectionNameProviderTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs
git commit -m "refactor(wpf): remove Base Environment sidebar menu + dead refs"
```

---

## Task 3: 删 BaseEnvView dead 文件

**Files:**
- Delete: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`
- Delete: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs`
- Delete: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`
- Delete: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs`(若存在)

**Interfaces:**
- Consumes: T2 已删所有 sidebar entry + VM references;`BaseEnvView` / `BaseEnvViewModel` 现在 0 caller
- Produces: 3-4 dead 文件从仓库消失

---

- [ ] **Step 1: grep 确认 0 caller**

```bash
grep -rn "BaseEnvView\b\|new BaseEnvViewModel\b" src-wpf tests-wpf
```

Expected: **0 命中**(T2 应该已删干净 MainViewModel.cs:313 的 `new BaseEnvViewModel(...)` 构造)。

如果有命中:
- 大概率是 MainViewModel.cs 还残留构造调用(虽然 T2 已删 ShowBaseEnv 方法)— 检查并清
- 可能是 XAML resource 引用(如 `<DataTemplate DataType="{x:Type vm:BaseEnvViewModel}">`)— grep XAML,删

- [ ] **Step 2: 删 BaseEnvView.xaml + .xaml.cs + BaseEnvViewModel.cs**

```bash
git rm src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml \
       src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs \
       src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs
```

**重要**:BaseEnvViewModel 删后,`MainViewModel.cs:313` 那行已经 T2 删过(ShowBaseEnv 方法已删);T2 也已删 `MainViewModel.cs:23` `MainSection.BaseEnv` enum — 不会有 dangling reference。

- [ ] **Step 3: 删 BaseEnvViewModelTests.cs(若存在)**

```bash
ls tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs 2>/dev/null
```

If exists:
```bash
git rm tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs
```

If not, skip。

- [ ] **Step 4: Build 验证**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 警告 0 错误。

- [ ] **Step 5: 全套测试无回归**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

Expected: 846 PASS / 0 FAIL / 1 SKIP baseline 持平(T3 删测试可能 -1~3,baseline 净 +1/+2)。

- [ ] **Step 6: Commit**

```bash
git commit -m "refactor(wpf): delete BaseEnvView + BaseEnvViewModel dead files"
```

---

## Task 4: final review + MEMORY + staging rebuild

**Files:**
- Modify: `D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\ComfyUI.Manager.exe`(rebuilt)
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_remove_base_env_sidebar.md`(MEMORY topic)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`(追加一行)

---

- [ ] **Step 1: final whole-branch review (opus)**

Dispatch opus model reviewer with:
- 范围:`9de9478..HEAD` (3 commits: T1 + T2 + T3)
- review package path:`D:\ToolDevelop\ComfyUI\.superpowers\sdd\2026-08-10-remove-base-env-sidebar-menu\review-9de9478..HEAD.diff`
- spec path:`D:\ToolDevelop\ComfyUI\docs\superpowers\specs\2026-08-10-remove-base-env-sidebar-menu-design.md`
- 3 task reports + 3 task review packages

Expected: APPROVED 0 Critical/Important;可能 1 Minor (e.g. fall back "未知 section" 字符串应该用 resx key 不硬编码)。

如不 APPROVED → 走 fix loop(opus reviewer 通常 1 round fix 即过,跟 v0.6.11+ 同款)。

- [ ] **Step 2: staging rebuild**

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: 0 警告 0 错误;`ComfyUI.Manager.exe` 时间戳更新。

- [ ] **Step 3: 创建 MEMORY topic file**

新建 `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_remove_base_env_sidebar.md`:

```markdown
---
name: Remove Base Environment sidebar menu v0.6.11+
description: 3 task SDD SHIP-READY — 删侧栏 "基础环境" 菜单项 + 全部 dead code;保留所有 per-env BED 功能
type: project
---

v0.6.11+ Remove BaseEnv sidebar SHIP-READY 2026-08-10, HEAD `<commit-sha>`, 3 commits (T1 MarkIncompat 迁移 / T2 侧栏菜单 + dead refs / T3 删 BaseEnvView dead files), **846 PASS / 0 FAIL / 1 SKIP** baseline 持平 (+1~3 新测试).

## 用户原话
- "去掉基础环境菜单" — 桌面验 v0.6.11+ Catalog UI polish 后觉得侧栏 BaseEnv 冗余
- "功能还是在的,只是删除侧边栏连接,因为我们依然需要安装基础环境" — 确认 per-env BED 功能保留
- "完整删文件" — 确认 dead code 全删不留

## T1 MarkIncompat 迁移
- `EnvironmentListViewModel` ctor 加可选 `BaseEnvProfileLoader? profileLoader = null` (向后兼容,default null)
- `ShowEnvironments()` 顶部 try/catch 调 `_profileLoader?.MarkIncompatibleOlderProfiles()` (catch + log,跟原 BaseEnvViewModel 行为等价)
- `App.xaml.cs` 构造 EnvListVM 时传 `_profileLoader` (DI 顺序已正确,profileLoader 先于 EnvListVM)
- 加 2 测试:`ShowEnvironments_CallsMarkIncompatOnce` (用 RecordingProfileLoader 计数) + `ShowEnvironments_ProfileLoaderNull_NoThrow` (向后兼容验证)

## T2 侧栏菜单 + dead refs 清理
- `MainWindow.xaml` 删侧栏 `<RadioButton Content="基础环境">` 整段 (剩 5 个 sidebar button)
- `MainViewModel.cs` 删 5 member:`MainSection.BaseEnv` enum / `ShowBaseEnvCommand` property / ctor init line / `ShowBaseEnv()` 方法 (307-315) / `ResolveCurrentViewName` "BaseEnvView" arm
- `MainSectionNameProvider.cs` 删 `MainSection.BaseEnv => Get("SectionName_BaseEnv", ...)` arm (line 20)
- `GlobalSearchService.cs` 删 `("ShowBaseEnv", "基础环境")` Spotlight entry (line 145)
- `Strings.resx` + `Strings.zh-CN.resx` 双语删 `SectionName_BaseEnv` key (line 450 / 258)
- 加 MainViewModel absence 断言 (property 删 + reflection 扫 enum);MainSectionNameProvider fallback 测试 (传入越界 enum);GlobalSearchService "ShowBaseEnv" absence 断言

## T3 删 BaseEnvView dead 文件
- `git rm` 3 文件:`Views/BaseEnvView.xaml` / `.xaml.cs` / `ViewModels/BaseEnvViewModel.cs`
- 如 `BaseEnvViewModelTests.cs` 存在则 `git rm`

## G-Constraints 落地
- **G1 保留 per-env BED**:env-list 工具栏 "基础环境部署" + per-env 行内 "卸载基础环境" + BED 徽章/状态/profile 列 + 所有 `BaseEnvInstaller`/`BaseEnvUninstaller`/`BaseEnvProgressDialog`/`BaseEnvProfilePickerDialog` 服务 全保留
- **G3 VM 接口冻结**:`EnvironmentListViewModel` 接口不变,新增可选 ctor 参数 default null;`MainViewModel` ctor 共享参数 `_baseEnvInstaller`/`_profileLoader`/`_pytorchVersionDirectory`/`_appDataDir` 保留 (EnvListVM 也用)
- **G4 MarkIncompat 迁成功**:torch<2.4 profile "不推荐" 后缀继续生效

## Verification (final consolidated)
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → 846 PASS / 0 FAIL / 1 SKIP baseline 持平
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager"` → 0/0
- 无 v-bump / 无 release zip (项目惯例,纯清理类改动)

## Final review (opus)
APPROVED SHIP-READY. 0 Critical / 0 Important / 1 Minor style-only(<具体>).

## Carry-forward(均不阻塞)
- 用户若报告 "找不到基础环境入口" → 可考虑 env-list 工具栏 "基础环境部署" 按钮改名为 "安装基础环境" 更显眼
- 用户若报告 "重启后 '不推荐' 后缀消失" → 考虑加到 `BaseEnvInstaller.Install` 调,或 `App.OnStartup` (但权衡启动慢)

## 跟版本入库 SDD 关系
本 SDD 完成 (BASE = `9de9478` 后面),下次"将版本写入到数据库中"独立 SDD 从本 SDD 完成后的 HEAD 接着做。
```

(把 `<commit-sha>` 替换成 T3 实际 commit SHA;commit 后再做这步确保 SHA 准确)

- [ ] **Step 4: 追加 MEMORY 索引行**

在 `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` 的 v0.6.11+ Catalog UI polish 行**之后**追加一行:

```markdown
- [v0.6.11+ Remove BaseEnv sidebar menu SDD](project_remove_base_env_sidebar.md) — ✓ SHIP-READY 2026-08-10,HEAD `<commit-sha>`(base `9de9478` + 3 commits:T1 MarkIncompat 迁移 EnvListVM / T2 侧栏菜单 + dead refs / T3 删 BaseEnvView dead 文件),846/0/1 持平;删侧栏 "基础环境" RadioButton + `MainSection.BaseEnv` enum + `ShowBaseEnvCommand` + `MainSectionNameProvider` arm + `GlobalSearchService` Spotlight entry + 2 resx `SectionName_BaseEnv` + 3 dead 文件 (BaseEnvView/ViewModel);**G1 保留** env-list 工具栏 + per-env 行内 BED 按钮 + BED 徽章/状态 + 所有 BED 服务;**G3 冻结**:`EnvListVM` 加可选 ctor 参数 default null + `MainViewModel` ctor 共享参数保留;**G4 MarkIncompat 迁移**:`BaseEnvViewModel` ctor → `EnvListVM.ShowEnvironments` 顶调 try/catch + log;无 v-bump / 无 release zip;staging rebuilt;GUI smoke 7 步待桌面验证(侧栏剩 5 / env-list 加载 BED 不推荐 suffix / per-env 按钮可用 / Spotlight 不再 ShowBaseEnv)
```

(把 `<commit-sha>` 替换成 T3 实际 commit SHA)

---

## Critical Files (full list)

**Modified (8 files):**
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` (-sidebar RadioButton)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (-5 member)
- `src-wpf/ComfyUI.Manager/ViewModels/MainSectionNameProvider.cs` (-BaseEnv arm)
- `src-wpf/ComfyUI.Manager/Services/GlobalSearchService.cs` (-Spotlight entry)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` (-1 key)
- `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` (-1 key)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (+ctor param + ShowEnvironments MarkIncompat)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` (EnvListVM DI 注入 profileLoader)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelTests.cs` (-BaseEnv 测试 + absence 断言)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` (+2 测试)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainSectionNameProviderTests.cs` (-BaseEnv + fallback 测试,若存在)
- `tests-wpf/ComfyUI.Manager.Tests/Services/GlobalSearchServiceTests.cs` (-ShowBaseEnv + absence,若存在)

**Deleted (3-4 files):**
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs` (若存在)

**Unchanged (G1 + G3 冻结):**
- 全部 per-env BED 服务 / Models / dialogs
- `Views/EnvironmentListView.xaml`
- `Settings.cs` / SQLite schema

---

## Verification (end-to-end)

按顺序验证 3 task commit 全 PASS:

```bash
# T1
git status --short   # EnvListVM.cs + App.xaml.cs + tests
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal   # 全 PASS (+2 新测试)

# T2
git status --short   # MainWindow.xaml + MainViewModel.cs + MainSectionNameProvider.cs + GlobalSearchService.cs + 2 resx + tests
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# T3
git status --short   # 3-4 文件 deleted
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# 合并后全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 846 PASS / 0 FAIL / 1 SKIP baseline 持平
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal   # 0/0
```

**GUI smoke (用户桌面):**
1. 启动 staging → 侧栏确认只剩 5 个按钮,无 "基础环境"
2. 点 env-list tab → 加载 → BED profile 列有 "不推荐" 后缀 (torch<2.4 profile)
3. 选中 env → 工具栏 "基础环境部署" 按钮可用 → 弹出 picker dialog
4. per-env 行内 "卸载基础环境" 按钮可用
5. BED 徽章/状态显示正常 (Success/Secondary/Error/Outline 4 色)
6. Spotlight (Ctrl+K) → 搜 "基础环境" → 不再弹出 ShowBaseEnv 命令
7. 状态栏 section label fallback 正常 (无 BaseEnv 后无错位)

---

## Risks

| 风险 | 缓解 |
|---|---|
| 用户原习惯点侧栏 → 找不到 → UX regression | GUI smoke 第一步就验;观察一轮用户反馈 |
| MarkIncompat 不打开 env-list 不跑 → torch<2.4 后缀丢失 | 装机入口 picker dialog 仍显 "不推荐";不影响装机决策 |
| resx 删 key → 运行时 XAML 引用 missing resource 抛异常 | T2 Step 5 grep 0 引用 + Step 9 build 0/0 兜底 |
| `BaseEnvProfileLoader` DI 顺序变更影响其他 consumer | T1 Step 1 验证 App.xaml.cs 现有顺序,DI 已正确 |
| `EnvironmentListViewModel` ctor 签名改 → 现有测试构造适配 | ctor 加可选参数 default null(向后兼容) |
| 删 BaseEnvViewModel 后,`MainViewModel` 中 `_baseEnvInstaller` 等参数实际只有 EnvListVM 用 | 保留 (G3 冻结,共享 ctor 参数)— 即使 MainViewModel 自己不用,也不删 |

---

## Self-Review

1. **Spec coverage:**
   - spec §1 侧栏 RadioButton 删除 → T2 Step 1 ✓
   - spec §2 MainViewModel 5 member 删 → T2 Step 2 ✓
   - spec §3 MainSectionNameProvider arm 删 → T2 Step 3 ✓
   - spec §4 GlobalSearchService Spotlight entry 删 → T2 Step 4 ✓
   - spec §5 resx 双语删 → T2 Step 5 ✓
   - spec §6 MarkIncompat 迁移 → T1 Steps 1-7 ✓
   - spec §7 BaseEnvView 3 文件删 → T3 Steps 1-3 ✓
   - spec Public API:VM 接口冻结 → T1 加可选 ctor param (default null 向后兼容) ✓
   - spec 测试策略 → T1 2 测试 + T2 absence 断言 + fallback 测试 ✓
   - spec 4 Task 分解 → 本 plan 4 task ✓

2. **Placeholder scan:** 0 处 TBD/TODO/未填

3. **Type consistency:**
   - `BaseEnvProfileLoader.MarkIncompatibleOlderProfiles` 方法名 ↔ T1 测试 + EnvListVM 调用 ↔ grep 实际为准 ✓
   - `MainSection.BaseEnv` enum 值 ↔ MainViewModel / MainSectionNameProvider 删 ↔ 编译期保证 ✓
   - `ShowBaseEnvCommand` property 名 ↔ MainViewModel + MainWindow.xaml 删 ↔ 编译期保证 ✓
   - `SectionName_BaseEnv` resx key ↔ resx 双语删 ↔ grep 验证 0 引用 ✓
   - `RecordingProfileLoader` 测试 helper ↔ EnvListVMTests 局部 sealed class ↔ 既有 fake pattern ✓

4. **Ambiguity check:**
   - T1 Step 4 "看既有 pattern" 提示 implementer 灵活适配 try/catch / `_logger` 可选参数 ✓
   - T2 Step 7/8 "如该测试文件不存在,skip" 明确 ✓
   - T3 Step 3 "若存在则删" 明确 ✓

---

## Execution Choice

**Subagent-Driven Development (沿用项目惯例):**
- 3 task × (implementer + reviewer) ≈ 6 dispatch
- T1 implementer (haiku) + T1 reviewer (sonnet)
- T2 implementer (haiku) + T2 reviewer (sonnet)
- T3 implementer (haiku) + T3 reviewer (sonnet)
- T4 final whole-branch review (opus) + MEMORY + staging rebuild
- 3 commits on main, 1 final review + MEMORY commit