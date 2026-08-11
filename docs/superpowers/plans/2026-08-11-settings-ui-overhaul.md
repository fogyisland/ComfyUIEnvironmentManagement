# Settings UI Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-KILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把设置页从"改一处即写盘"改为"整页浮动 Save 按钮 + 行内 ⚠️ dirty 标记",并移除已废弃的 `SharedModelsDirectory` 字段(`DefaultModelsDirectory` 成为唯一 models 来源)。

**Architecture:**
- 新建 `DirtyLookup`(带索引器的 INPC 小对象)挂在 `SettingsViewModel.Dirty`,XAML 行内 ⚠️ 通过 `{Binding Dirty[PropertyName]}` 查 dirty;`Save` 写盘 + 清 dirty,`Discard` 用 `Settings.CopyInto` 就地回写到共享实例(不换引用 —— `Settings` 是 App 共享的,其它服务如 `ProcessLauncher` / `EnvCreatorService` 仍持有原对象)。
- `ThemeMode` setter 例外:dirty 模式但仍调 `_themeService?.Apply(...)` 即时预览;`Discard` 必须反向 Apply 才能回滚。
- 27 个属性 setter 把 `_repo.Save(_settings)` 换成 `MarkDirty(...)`(不写盘);集合 `ExtraPaths` / `QuerySources` / `DownloadSources` / `PythonInterpreters` / `CommonNodes` 的 `CollectionChanged` 仍即时写盘(点按钮的操作,非行内编辑)。
- 拦截点只在 `MainWindow.xaml.cs:OnClosing`(settings view 是 `UserControl` 没 `Closing` 事件);3 按钮 `MessageBox.Show(YesNoCancel)` + test seam `UnsavedPromptOverride`。
- 移除 `SharedModelsDirectory`:`Settings` model 删字段 + `ProcessLauncher` ctor 参数 `sharedModelsDirectory` → `modelsDirectory`(位置参数,不改调用语法)+ `EnvCreatorService` 步骤 5.5/5.6 合并为唯一 `DefaultModelsDirectory` 链接 + `App.xaml.cs` 传参改 + 4 个测试文件对应处理(2 删 2 改)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 手写 MVVM (`ViewModelBase` / `RelayCommand`) · `Microsoft.Win32.Open(Folder|File)Dialog` · `MessageBox.Show` · `InternalsVisibleTo("ComfyUI.Manager.Tests")`

**base SHA:** `79af0f3`(v0.6.11+ dashboard-splash-icon SHIP-READY,860 PASS / 3 FAIL(2 pre-existing flaky + 1 new real-network `ComfyUIManagerInstallerTests.InstallAsync_RealGit_ClonesRepo`) / 1 SKIP)
**spec SHIP-READY:** `6dba75f`(spec commit,自身)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | **所有 setter 改走 dirty,只有 Save 才写盘** —— `MarkDirty(name)` 标 dirty,`SaveCommand` 一次性 `_repo.Save` + `Dirty.Clear`;`DiscardCommand` 用 `Settings.CopyInto(onDisk)` 就地回写 + `RaiseAllPropertiesChanged` + 反向调 `ThemeService.Apply`。**禁止**部分 setter 仍走自动写盘(行为分裂)。 | spec §1-§4 |
| **G2** | **集合增删命令仍即时写盘** —— `ExtraPaths` / `QuerySources` / `DownloadSources` / `PythonInterpreters` / `CommonNodes` 这 5 个 `ObservableCollection` 的 `CollectionChanged` 订阅仍 `_repo.Save`;`ToggleCommonNodeEnabledCommand` 和 `RemovePythonInterpreterCommand` 的"清 active 名"小段也保留即时 Save。这些是"点按钮的操作",不是行内编辑,不参与 dirty 流程。 | spec §2 例外段 |
| G3 | **`ThemeMode` setter 必须仍调 `_themeService?.Apply(...)`** —— 即时预览(切暗/亮不需点 Save 才生效);但写盘走 `MarkDirty`。`Discard` 必须反向 Apply 才能回滚到磁盘值。`_themeService` 可空,既有测试 ctor 不传仍跑得动。 | spec §2 + spec §Carry-forward |
| G4 | **`Settings` 实例是 App 全局共享**(`App.xaml.cs:121` 传给 `ProcessLauncher` 等),`Discard` **不能** `_settings = _repo.Load()` 重新赋值 —— 必须 `CopyInto` 就地逐字段回写,否则别的服务仍持有被丢弃的那份。 | spec §4 |
| G5 | **所有 WPF `Setter` 引用 palette 资源必须 property-element + `DynamicResource`**;`MaterialButton` / `MaterialTextBox` 已知正确;新增 `WarningBrush` 在两个 Palette.xaml 加 `<SolidColorBrush>` 即可,Setter 写法参照 `SuccessBrush` / `ErrorBrush`。 | `feedback_wpf_style_setter_dynamic_resource.md` v0.6.9.2 |
| G6 | **拦截点只在主窗口关闭** —— `SettingsView` 是 `UserControl`,无 `Closing` 事件;切侧栏离开 settings 页**不**拦截(`_settingsViewModel` 在 `MainViewModel.cs:82` 缓存,dirty 状态跟着 VM 活着,切回时浮动 toolbar 仍显示)。`MainWindow.xaml.cs:134` 的 `OnClosing` 在最前面插一段 guard,UI 偏好写盘逻辑不动。 | spec §5 |
| G7 | **三按钮用 `MessageBoxButton.YesNoCancel`**(WPF 原生,不新建 dialog) + `MessageBoxImage.Warning`;test seam `internal Func<int, UnsavedChoice>? UnsavedPromptOverride`(沿用 `EnvironmentListViewModel.MessageBoxOverride` 同款 seam 模式)。 | spec §5 |
| G8 | **VM/服务做单元测试**;XAML 只做 STA-thread headless load test(已有 `SettingsViewLoadTests.cs` 模式);**不**为弹框写脆弱 UI 测试。 | 项目惯例 |
| G9 | **不做无关重构**;不改 `SettingsDefaults.Apply`;不改 `ThemeService.Apply`;不引入新依赖;不改其它 sidebar entry 的导航 pattern。 | 项目惯例 |
| G10 | **`Settings.CopyInto` 是 `static` 方法** —— 把 `onDisk` 的逐字段拷到 `target`(集合类字段做"清空 + AddRange"内容替换,不换 `List<>` 引用,保持 `CollectionChanged` 订阅稳定)。 | spec §4 + §G4 |
| G11 | **每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer**,严格匹配 progress.md ledger。 | SDD 流程 |
| G12 | **STA load test 走 `WpfTestResources.EnsureLoaded(PaletteVariant.Dark)` + `Measure(800,600)` + `Arrange + UpdateLayout`**;**不**走 `StaFact`(已有 helper 直接用)。 | `WpfTestResources.cs` |

---

## File Structure

**新建**
- `src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs` — 索引器 + INPC 小对象
- `tests-wpf/.../ViewModels/SettingsViewModelDirtyTests.cs` — 9 个 VM 测试
- `tests-wpf/.../ViewModels/MainViewModelUnsavedSettingsTests.cs` — 5 个 MVM 测试

**改**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — 删 `SharedModelsDirectory` + 加 `CopyInto`
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` — 27 setter 改 `MarkDirty` + 加 `Save/Discard` 命令 + `Dirty` 属性 + `MarkDirty/ClearDirty` 私有 + 删 `SharedModelsDirectory` 整块 + `RaiseAllPropertiesChanged` 删 `SharedModelsDirectory` 行
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — 顶部浮动 toolbar(标题 + 计数 + Save/Discard 按钮)+ 每行 label 后挂 ⚠️ + 删 SharedModelsDirectory UI 块
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` — 删 `BrowseSharedModelsDirectory`
- `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml` + `Palette.Light.xaml` — 加 `WarningBrush` + `WarningColor`
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` — `OnClosing` 顶部加 `ConfirmDiscardUnsavedSettings()` guard
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — 加 `ConfirmDiscardUnsavedSettings` + `UnsavedChoice` enum + `UnsavedPromptOverride` seam + :433 注释改 `DefaultModelsDirectory`
- `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` — ctor 参数 / 字段 / 注释 `sharedModelsDirectory` → `modelsDirectory`
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` — 步骤 5.5/5.6 合并成单一 `DefaultModelsDirectory` 链接
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — 传参改 `settings.DefaultModelsDirectory`

**测试改**
- `tests-wpf/.../ViewModels/SettingsViewModelTests.cs` — 既有依赖"setter 立即写盘"的断言改 `setter → SaveCommand.Execute → 断言`
- `tests-wpf/.../Views/SettingsViewLoadTests.cs` — +1 STA test `SettingsView_WithDirtyRows_RendersDirtyMarkers`

**删除测试**
- `tests-wpf/.../ViewModels/SettingsViewModelSharedModelsTests.cs`
- `tests-wpf/.../Services/EnvCreatorServiceSharedModelsTests.cs`

**改测试**
- `tests-wpf/.../Infrastructure/ProcessLauncherSharedModelsTests.cs` — 数据源换 `DefaultModelsDirectory`,断言不变
- `tests-wpf/.../Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs` — 删 `DefaultModelsDirectoryAndSharedModelsDirectory_BothSet_SharedModelsWins`(:106)+ 删 fixture 里的 `SharedModelsDirectory = ""`(:41)

---

## Task Breakdown

### Task 1: 浮动 Save 按钮 + 行内 dirty 标记 + Save/Discard 命令

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs`
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (加 `CopyInto` static 方法)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (`Dirty` 属性 + `MarkDirty`/`ClearDirty` + `SaveCommand`/`DiscardCommand` + 27 setter 改写 + `RaiseAllPropertiesChanged` 不变)
- Modify: `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml` + `Palette.Light.xaml` (加 `WarningColor` + `WarningBrush`)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (顶部浮动 toolbar + 每行 label 后挂 ⚠️)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs` (+1 STA test)

**Interfaces:**
- Consumes: `Models.Settings` 加 `public static void CopyInto(Settings target, Settings source)`
- Produces:
  - `ViewModels.DirtyLookup(string propertyName)` indexer / `Count` / `Any` / `Mark(...)` / `Clear()` / INPC `"Item[]"` notify
  - `SettingsViewModel.Dirty { get; }` 暴露 `DirtyLookup`
  - `SettingsViewModel.HasUnsavedChanges => Dirty.Any`
  - `SettingsViewModel.UnsavedCount => Dirty.Count`
  - `SettingsViewModel.SaveCommand { get; }` / `DiscardCommand { get; }`

**Step 1.1:** Write the failing `DirtyLookup` tests + 7 `SettingsViewModel` dirty tests (TDD)

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/DirtyLookupTests.cs`:

```csharp
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class DirtyLookupTests
{
    [Fact]
    public void Indexer_EmptyLookup_ReturnsFalse()
    {
        var d = new DirtyLookup();
        Assert.False(d["Xyz"]);
    }

    [Fact]
    public void Mark_NewProperty_SetsIndexerTrue_AndRaisesItemArrayNotify()
    {
        var d = new DirtyLookup();
        var notifies = new List<string>();
        d.PropertyChanged += (_, e) => notifies.Add(e.PropertyName ?? "");
        d.Mark("DefaultModelsDirectory");
        Assert.True(d["DefaultModelsDirectory"]);
        Assert.True(d.Any);
        Assert.Equal(1, d.Count);
        // WPF 重新评估所有索引器绑定的约定 key
        Assert.Contains("Item[]", notifies);
        Assert.Contains(nameof(DirtyLookup.Any), notifies);
        Assert.Contains(nameof(DirtyLookup.Count), notifies);
    }

    [Fact]
    public void Mark_SamePropertyTwice_NoDoubleCount_NoSpuriousNotify()
    {
        var d = new DirtyLookup();
        d.Mark("X");
        var notifiesAfterFirst = 0;
        d.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") notifiesAfterFirst++; };
        d.Mark("X");
        Assert.Equal(1, d.Count);
        Assert.Equal(0, notifiesAfterFirst);
    }

    [Fact]
    public void Clear_RemovesAll_AndRaisesNotify()
    {
        var d = new DirtyLookup();
        d.Mark("A"); d.Mark("B"); d.Mark("C");
        Assert.Equal(3, d.Count);
        var notifies = new List<string>();
        d.PropertyChanged += (_, e) => notifies.Add(e.PropertyName ?? "");
        d.Clear();
        Assert.Equal(0, d.Count);
        Assert.False(d.Any);
        Assert.False(d["A"]);
        Assert.Contains("Item[]", notifies);
    }

    [Fact]
    public void Clear_EmptyLookup_NoNotify()
    {
        var d = new DirtyLookup();
        var notifies = 0;
        d.PropertyChanged += (_, _) => notifies++;
        d.Clear();
        Assert.Equal(0, notifies);
    }
}
```

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.11+ SDD B T1:dirty tracking + Save / Discard + CopyInto 单元测试。
/// </summary>
public sealed class SettingsViewModelDirtyTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModelDirtyTests()
    {
        _path = Path.Combine(Path.GetTempPath(),
            "settings-vm-dirty-" + Path.GetRandomFileName() + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private SettingsViewModel NewVm() => new SettingsViewModel(
        new SettingsRepository(_path),
        GitProxyConfig.Disabled,
        new FakeValidator(isValid: true));

    [Fact]
    public void MarkDirty_SingleProperty_SetsDirtyAndHasUnsavedChanges()
    {
        var vm = NewVm();
        Assert.False(vm.HasUnsavedChanges);

        vm.DefaultModelsDirectory = @"D:\Models\shared";

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(1, vm.UnsavedCount);
        Assert.True(vm.Dirty["DefaultModelsDirectory"]);
    }

    [Fact]
    public void MarkDirty_MultipleProperties_AggregatesCount()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "a";
        vm.ComfyUiStartupTimeoutSeconds = 900;
        vm.FetchNodeVersionsOnRefresh = true;
        Assert.Equal(3, vm.UnsavedCount);
        Assert.True(vm.Dirty["DefaultModelsDirectory"]);
        Assert.True(vm.Dirty["ComfyUiStartupTimeoutSeconds"]);
        Assert.True(vm.Dirty["FetchNodeVersionsOnRefresh"]);
    }

    [Fact]
    public void MarkDirty_SameProperty_Twice_StaysAtOne()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "a";
        vm.DefaultModelsDirectory = "b";   // 同一 property,只算一行 dirty
        Assert.Equal(1, vm.UnsavedCount);
    }

    [Fact]
    public void Setter_DoesNotWriteToDisk_BeforeSave()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = @"D:\Models\dirty";

        var fresh = new SettingsRepository(_path).Load();
        Assert.Equal("", fresh.DefaultModelsDirectory);   // 还是默认值,未写盘
    }

    [Fact]
    public void SaveCommand_PersistsSettings_ClearsAllDirty()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = @"D:\Models\shared";
        vm.ComfyUiStartupTimeoutSeconds = 900;
        Assert.True(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);

        var fresh = new SettingsRepository(_path).Load();
        Assert.Equal(@"D:\Models\shared", fresh.DefaultModelsDirectory);
        Assert.Equal(900, fresh.ComfyUiStartupTimeoutSeconds);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.UnsavedCount);
    }

    [Fact]
    public void SaveCommand_CanExecute_FalseWhenClean()
    {
        var vm = NewVm();
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.DefaultModelsDirectory = "x";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void DiscardCommand_RevertsInPlace_KeepsSameSettingsInstance()
    {
        // 关键约束:_settings 是 App 共享实例,Discard 不能换引用(G4)
        var vm = NewVm();
        var beforeRef = vm.GetType()
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm);
        vm.DefaultModelsDirectory = "dirty";
        vm.SaveCommand.Execute(null);                 // 写到 disk
        vm.DefaultModelsDirectory = "another-dirty"; // 再改 dirty

        vm.DiscardCommand.Execute(null);

        var afterRef = vm.GetType()
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm);
        Assert.Same(beforeRef, afterRef);             // 同一对象,没被换掉
        Assert.Equal("dirty", vm.DefaultModelsDirectory); // 回到 disk 上的值
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void DiscardCommand_LeavesDiskUnchanged()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "committed";
        vm.SaveCommand.Execute(null);
        vm.ComfyUiStartupTimeoutSeconds = 999;       // dirty

        vm.DiscardCommand.Execute(null);

        var fresh = new SettingsRepository(_path).Load();
        Assert.Equal("committed", fresh.DefaultModelsDirectory);
        Assert.Equal(600, fresh.ComfyUiStartupTimeoutSeconds); // 默认值
    }

    [Fact]
    public void DiscardCommand_RevertsThemeMode_InMemory()
    {
        // G3:Discard 必须能回滚 ThemeMode(尽管它即时预览)
        var themeService = new RecordingThemeService();
        var vm = new SettingsViewModel(
            new SettingsRepository(_path),
            GitProxyConfig.Disabled,
            new FakeValidator(isValid: true),
            sharedSettings: null,
            themeService: themeService);

        vm.ThemeMode = "light";   // 触发 Apply(Light)
        Assert.Equal(Services.ThemeMode.Light, themeService.LastApplied);

        vm.ThemeMode = "dark";    // Apply(Dark)
        Assert.Equal(Services.ThemeMode.Dark, themeService.LastApplied);

        vm.DiscardCommand.Execute(null);   // disk 默认是 "dark",但 in-memory 已变 "dark"... 这条路径验它会重新 Apply(Dark)
        Assert.Equal(Services.ThemeMode.Dark, themeService.LastApplied);
    }

    // — helpers —
    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        private readonly bool _isValid;
        public FakeValidator(bool isValid) { _isValid = isValid; }
        public Task<PythonInterpreterValidationResult> ValidateAsync(
            string path, CancellationToken ct)
            => Task.FromResult(new PythonInterpreterValidationResult(_isValid, _isValid ? "ok" : "bad"));
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public Services.ThemeMode? LastApplied { get; private set; }
        public int ApplyCallCount { get; private set; }
        public void Apply(Services.ThemeMode mode)
        {
            LastApplied = mode;
            ApplyCallCount++;
        }
        public event EventHandler? ThemeChanging;
    }
}
```

Run targeted tests (must FAIL — DirtyLookup / Dirty / Save / Discard 全不存在):

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~DirtyLookupTests|FullyQualifiedName~SettingsViewModelDirtyTests" -v minimal --no-build
```

Expected: build failure first (`DirtyLookup` / `Dirty` / `SaveCommand` / `DiscardCommand` don't exist). Once we add the implementation, they should all PASS.

**Step 1.2:** Implement `Models/Settings.cs::CopyInto` + `ViewModels/DirtyLookup.cs`

Append to `src-wpf/ComfyUI.Manager/Models/Settings.cs` (just before the closing brace of `Settings` class):

```csharp
    /// <summary>
    /// v0.6.11+ SDD B T1:把 <paramref name="source"/> 的逐字段拷到 <paramref name="target"/>。
    /// 集合类字段做"清空 + AddRange"内容替换,不换 List 引用 —— Settings 实例由
    /// App 全局共享,Discard 必须就地回写以免其它服务持有被丢弃的旧对象(G4)。
    /// </summary>
    public static void CopyInto(Settings target, Settings source)
    {
        // —— 基础 / 显示 ——
        target.Theme = source.Theme;
        target.ThemeMode = source.ThemeMode;
        target.Language = source.Language;
        target.CatalogAutoRefresh = source.CatalogAutoRefresh;
        target.CatalogCacheTtlMinutes = source.CatalogCacheTtlMinutes;
        target.CompatApiBaseUrl = source.CompatApiBaseUrl;
        // —— 路径 ——
        target.TemplatePythonDir = source.TemplatePythonDir;
        target.TemplateComfyuiDir = source.TemplateComfyuiDir;
        target.DefaultPythonVersion = source.DefaultPythonVersion;
        target.EnvsDir = source.EnvsDir;
        target.GlobalNodesDir = source.GlobalNodesDir;
        target.LocalNodeDirectory = source.LocalNodeDirectory;
        target.DefaultModelsDirectory = source.DefaultModelsDirectory;
        // target.SharedModelsDirectory —— T2 删除;这里也不写
        // —— 环境 / 工具 ——
        target.PythonVenvBaseline = source.PythonVenvBaseline;
        target.GitExe = source.GitExe;
        target.GitProxyUrl = source.GitProxyUrl;
        target.GitProxyPort = source.GitProxyPort;
        target.GitProxyEnabled = source.GitProxyEnabled;
        target.ComfyUiStartupTimeoutSeconds = source.ComfyUiStartupTimeoutSeconds;
        target.ComfyUiLocale = source.ComfyUiLocale;
        // —— Catalog 视图 ——
        target.CatalogViewMode = source.CatalogViewMode;
        target.CatalogPageSize = source.CatalogPageSize;
        // —— 节点源 ——
        target.ActiveQuerySourceName = source.ActiveQuerySourceName;
        target.ActiveDownloadSourceName = source.ActiveDownloadSourceName;
        // —— GitHub ——
        target.GitHubToken = source.GitHubToken;
        target.FetchNodeVersionsOnRefresh = source.FetchNodeVersionsOnRefresh;
        // —— Python ——
        target.ActivePythonInterpreterName = source.ActivePythonInterpreterName;
        // —— Pip mirror ——
        target.PipMirror = source.PipMirror;
        target.PipMirrorCustomUrl = source.PipMirrorCustomUrl;
        // —— 集合:不换 List 引用,清空 + AddRange ——
        target.ExtraPaths.Clear();
        target.ExtraPaths.AddRange(source.ExtraPaths);
        target.QuerySources.Clear();
        target.QuerySources.AddRange(source.QuerySources);
        target.DownloadSources.Clear();
        target.DownloadSources.AddRange(source.DownloadSources);
        target.PythonInterpreters.Clear();
        target.PythonInterpreters.AddRange(source.PythonInterpreters);
        target.CommonNodes.Clear();
        target.CommonNodes.AddRange(source.CommonNodes);
    }
```

Note: `source` is typed `Settings` (not nullable). `source` parameter might be the freshly-loaded JSON one which could have `SharedModelsDirectory` field on disk; the `CopyInto` ignores it (T2 will delete the field, so T1 implementation never assigns to it).

Create `src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs`:

```csharp
using System.Collections.Generic;
using System.ComponentModel;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.11+ SDD B T1:per-property dirty 标记集合,暴露索引器供 XAML 绑定
/// <c>{Binding Dirty[PropertyName]}</c>。WPF 索引器绑定约定 key 是 "Item[]",
/// 标 dirty 时 raise "Item[]" 让所有索引器绑定重算。
///
/// 线程模型:单 UI 线程访问,无锁。
/// </summary>
public sealed class DirtyLookup : INotifyPropertyChanged
{
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    /// <summary>
    /// 给定 property 名字面量是否 dirty。XAML 写 <c>Dirty[PropertyName]</c>。
    /// </summary>
    public bool this[string propertyName] =>
        !string.IsNullOrEmpty(propertyName) && _dirty.Contains(propertyName);

    public int Count => _dirty.Count;
    public bool Any => _dirty.Count > 0;

    public void Mark(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;
        if (!_dirty.Add(propertyName)) return;     // 已 dirty → no-op 不 notify
        RaiseAll();
    }

    public void Clear()
    {
        if (_dirty.Count == 0) return;
        _dirty.Clear();
        RaiseAll();
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Any)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

**Step 1.3:** Modify `SettingsViewModel.cs` — add dirty plumbing + 27 setter conversions

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:

(a) Add `Dirty` property + `MarkDirty`/`ClearDirty` + commands just below `private Settings _settings;`:

```csharp
    // v0.6.11+ SDD B T1:dirty tracking。XAML 行内 ⚠️ 通过 {Binding Dirty[Xxx]} 查,
    // SaveCommand 一次性写盘 + 清 dirty,DiscardCommand 用 CopyInto 回滚。
    public DirtyLookup Dirty { get; } = new();

    public bool HasUnsavedChanges => Dirty.Any;
    public int UnsavedCount => Dirty.Count;

    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }

    private void MarkDirty(string propertyName)
    {
        Dirty.Mark(propertyName);
        RaisePropertyChanged(nameof(HasUnsavedChanges));
        RaisePropertyChanged(nameof(UnsavedCount));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }

    private void ClearDirty()
    {
        Dirty.Clear();
        RaisePropertyChanged(nameof(HasUnsavedChanges));
        RaisePropertyChanged(nameof(UnsavedCount));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }
```

(b) Wire up commands in ctor (just before `RaiseAllPropertiesChanged();` near line 261):

```csharp
        SaveCommand = new RelayCommand(
            _ => { _repo.Save(_settings); ClearDirty(); },
            _ => HasUnsavedChanges);
        DiscardCommand = new RelayCommand(
            _ =>
            {
                var onDisk = _repo.Load();
                Settings.CopyInto(_settings, onDisk);
                // G3:ThemeMode 即使即时预览,Discard 也得反向 Apply 才能回滚
                _themeService?.Apply(ParseThemeMode(_settings.ThemeMode));
                RaiseAllPropertiesChanged();
                ClearDirty();
            },
            _ => HasUnsavedChanges);
```

(d) Convert **27 setter sites** (line numbers from `79af0f3`). For each:
- Replace `_repo.Save(_settings);` with `MarkDirty(nameof(<ThisProperty>));`
- Keep all `RaisePropertyChanged(...)` and any side-effect calls (`_themeService?.Apply(...)` for `ThemeMode`)

Detailed mapping — apply each:

| Line | Property | Side-effect to preserve |
|------|----------|-----|
| 274 | `Language` | (none) |
| 277-283 | `ThemeMode` | keep `_themeService?.Apply(ParseThemeMode(value))` |
| 303-309 | `CacheTtlMinutes` | (none) |
| 311-315 | `ComfyUiStartupTimeoutSeconds` | (none) |
| 318-322 | `ComfyUiLocale` | (none — `value ?? ""` keep) |
| 329-334 | `CompatApiBaseUrl` | (none) |
| 340-345 | `GitHubToken` | (none — `value ?? ""` keep;no RaisePropertyChanged in current code, leave it) |
| 350-355 | `FetchNodeVersionsOnRefresh` | (none) |
| 365-369 | `PipMirror` | keep `RaisePropertyChanged(nameof(IsCustomPipMirrorSelected))` |
| 377-380 | `PipMirrorCustomUrl` | (none) |
| 388-391 | `TemplatePythonDir` | (none) |
| 393-396 | `TemplateComfyuiDir` | (none) |
| 398-401 | `EnvsDir` | (none) |
| 403-406 | `DefaultPythonVersion` | (none — `value ?? ""` keep) |
| 408-411 | `GlobalNodesDir` | (none) |
| 416-420 | `LocalNodeDirectory` | (none — `value ?? ""` keep) |
| 425-428 | `DefaultModelsDirectory` | (none — `value ?? ""` keep) |
| 441 | `PythonVenvBaseline` | (none) |
| 443-446 | `GitExe` | (none) |
| 452-457 | `GitProxyUrl` | (none — keep `_proxy.Url = value`) |
| 463-468 | `GitProxyPort` | (none — keep `_proxy.Port = value`) |
| 474-479 | `GitProxyEnabled` | (none — keep `_proxy.Enabled = value`) |
| 495-500 | `ActiveQuerySource` | (none) |
| 506-511 | `ActiveDownloadSource` | (none) |
| 587-592 | `ActivePythonInterpreter` | (none) |
| 598-603 | `ActivePythonInterpreterName` | keep `RaisePropertyChanged(nameof(ActivePythonInterpreter))` |

**Skip** (still write-to-disk):
- Line 61 ctor post-Apply save
- Lines 66/72/79/86/93 `CollectionChanged` (5 sites)
- Line 148 `ToggleCommonNodeEnabledCommand` end
- Line 176 `RemovePythonInterpreterCommand` wasActive reset

Run targeted tests now — must all PASS:

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~DirtyLookupTests|FullyQualifiedName~SettingsViewModelDirtyTests" -v minimal
```

Expected: 14 PASS / 0 FAIL.

**Step 1.4:** Add `WarningBrush` to both palette files

Append to `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml`(after `SuccessColor` line — keep alphabetical-ish):

```xml
    <Color x:Key="WarningColor">#FFB300</Color>
    <!-- ... existing OnPrimaryColor / OnSurfaceColor ... -->
    <SolidColorBrush x:Key="WarningBrush" Color="{StaticResource WarningColor}" />
```

Append to `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml`(after `SuccessColor`):

```xml
    <Color x:Key="WarningColor">#F57C00</Color>
    <SolidColorBrush x:Key="WarningBrush" Color="{StaticResource WarningColor}" />
```

(Use dark-mode amber + light-mode orange — same Material You palette as Error/Success.)

**Step 1.5:** Modify `SettingsView.xaml` — floating toolbar + per-row ⚠️

Replace the top-level wrapper. Find the existing `<ScrollViewer>` at line 12 (the immediate child of `<UserControl>`) and wrap it in a Grid with two rows:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.SettingsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:ComfyUI.Manager.Views"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:SettingsViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- v0.6.11+ SDD B T1:粘性 Save toolbar -->
        <Border Grid.Row="0" Padding="12,8"
                Background="{DynamicResource SurfaceBrush}"
                BorderBrush="{DynamicResource OutlineBrush}"
                BorderThickness="0,0,0,1">
            <DockPanel>
                <TextBlock DockPanel.Dock="Left" Text="设置"
                           FontSize="16" FontWeight="Bold"
                           VerticalAlignment="Center"/>
                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal"
                            HorizontalAlignment="Right">
                    <TextBlock VerticalAlignment="Center" Margin="0,0,12,0"
                               Foreground="{DynamicResource WarningBrush}"
                               Text="{Binding UnsavedCount, StringFormat='⚠ {0} 项未保存'}"
                               Visibility="{Binding HasUnsavedChanges, Converter={StaticResource BoolToVisibility}}"/>
                    <Button Content="↩ 放弃" Width="80" Margin="0,0,8,0"
                            Style="{StaticResource MaterialButton}"
                            Command="{Binding DiscardCommand}"/>
                    <Button Content="💾 保存" Width="80"
                            Style="{StaticResource MaterialButton}"
                            Command="{Binding SaveCommand}"/>
                </StackPanel>
            </DockPanel>
        </Border>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="16" MaxWidth="640" HorizontalAlignment="Left">
                <!-- … existing content (SettingsView body untouched below) … -->
```

Then add `</Grid>` before `</UserControl>`.

For per-row ⚠️: wrap each row's TextBlock + control into a StackPanel. Example for Catalog TTL row (currently lines 24-28):

Before:
```xml
            <TextBlock Text="Catalog 缓存 TTL(分钟)" Margin="0,8,0,4" />
            <TextBox Text="{Binding CacheTtlMinutes, UpdateSourceTrigger=PropertyChanged}"
                     Style="{StaticResource MaterialTextBox}" Width="240"
                     HorizontalAlignment="Left" />
```

After:
```xml
            <StackPanel Orientation="Horizontal" Margin="0,8,0,4">
                <TextBlock Text="Catalog 缓存 TTL(分钟)" VerticalAlignment="Center"/>
                <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
                           VerticalAlignment="Center"
                           Foreground="{DynamicResource WarningBrush}"
                           ToolTip="尚未保存"
                           Visibility="{Binding Dirty[CacheTtlMinutes], Converter={StaticResource BoolToVisibility}}"/>
            </StackPanel>
            <TextBox Text="{Binding CacheTtlMinutes, UpdateSourceTrigger=PropertyChanged}"
                     Style="{StaticResource MaterialTextBox}" Width="240"
                     HorizontalAlignment="Left" />
```

Apply this same pattern to **every** editable row in the existing body — 27 setters ↔ 27 rows (skip the readonly header TextBlocks at section markers). The exact rows to wrap:
- 基础:`Language` (ComboBox) / `CacheTtlMinutes` (above) / `ComfyUiStartupTimeoutSeconds` / `ComfyUiLocale` (ComboBox) / `CompatApiBaseUrl` / `DefaultModelsDirectory` / `GitHubToken` (PasswordBox — wrap label) / `FetchNodeVersionsOnRefresh` (CheckBox)
- 节点源:`ActiveQuerySource` (ComboBox) / `ActiveDownloadSource` (ComboBox)
- 环境与模板:`TemplateComfyuiDir` / `EnvsDir` / `DefaultPythonVersion` (ComboBox) / `PythonVenvBaseline` / `GitExe` / `LocalNodeDirectory` / `GlobalNodesDir`
- Git 代理:`GitProxyUrl` / `GitProxyPort` / `GitProxyEnabled` (CheckBox)
- Pip 镜像:`PipMirror` (ComboBox) / `PipMirrorCustomUrl`
- Python 解释器:`ActivePythonInterpreterName` (ComboBox)

(27 rows total; one-to-one with the 27 setters converted in Step 1.3d.)

**Step 1.6:** Add STA load test in `SettingsViewLoadTests.cs`

Append to `tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs`:

```csharp
    /// <summary>
    /// v0.6.11+ SDD B T1:dirty 索引器绑定不抛 XAML 解析异常 + toolbar 渲染。
    /// 验 WPF {Binding Dirty[PropertyName]} 索引器绑定求值不会因 INPC key 缺失
    /// 而 throw,toolbar 上 HasUnsavedChanges / UnsavedCount 渲染不出错。
    /// </summary>
    [Fact]
    public void SettingsView_WithDirtyRows_RendersDirtyMarkers()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var vm = new SettingsViewModel(
                    new Data.SettingsRepository(Path.Combine(Path.GetTempPath(),
                        $"settings-dirty-{Guid.NewGuid():N}.json")),
                    Infrastructure.GitProxyConfig.Disabled,
                    new FakeValidator(isValid: true));
                vm.DefaultModelsDirectory = "dirty";   // 标 dirty 一行
                Assert.True(vm.HasUnsavedChanges);     // 验 dirty plumbing
                var v = new SettingsView { DataContext = vm };
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"SettingsView dirty-rows load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        public Task<PythonInterpreterValidationResult> ValidateAsync(string path, CancellationToken ct)
            => Task.FromResult(new PythonInterpreterValidationResult(true, "ok"));
    }
```

Run full settings test suite (existing + 14 new + 1 STA = 32+ expected):

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~SettingsViewLoad|FullyQualifiedName~DirtyLookup" -v minimal
```

Expected: 14 (existing SettingsViewModelTests) + 9 (new SettingsViewModelDirtyTests) + 5 (new DirtyLookupTests) + 3 (existing SettingsViewLoadTests: 2 instantiation + 1 new) = **31 PASS / 0 FAIL**.

**Step 1.7:** Commit

```bash
cd "D:/ToolDevelop/ComfyUI"
git add src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs \
        src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/DirtyLookupTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): settings dirty tracking + floating Save button

settings 页从"改一处即写盘"改为整页浮动 Save + 行内 ⚠️ dirty 标记;
新建 DirtyLookup(INPC + 索引器)挂 VM,Save 写盘 + 清 dirty,
Discard 用 Settings.CopyInto 就地回写到共享实例(不换引用,
避免 ProcessLauncher / EnvCreatorService 仍持有被丢弃的对象)。

- 新建 ViewModels/DirtyLookup.cs(索引器 + Item[] notify)
- 27 个 setter 改走 MarkDirty;ThemeMode 仍即时 Apply(Discard 反向 Apply 回滚)
- 集合增删命令保留即时写盘
- SettingsView.xaml 顶部浮动 toolbar(标题 + ⚠ N 项未保存 + 放弃/保存)
- 每行 label 后挂 ⚠️;绑 {Binding Dirty[Xxx]} 索引器
- 两套 Palette 加 WarningBrush
- 9 + 5 + 1 个新单元测试

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: SharedModelsDirectory 移除

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:52` (删 `SharedModelsDirectory` 属性)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (删 `SharedModelsDirectory` 整块 property + setter + `RaiseAllPropertiesChanged` 里那一行)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (删「共享 Models 目录」整块 UI)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` (删 `BrowseSharedModelsDirectory` handler)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` (字段/ctor 参数/注释 `sharedModelsDirectory` → `modelsDirectory`)
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` (步骤 5.5/5.6 合并)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:121` (传参改 `settings.DefaultModelsDirectory`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:433` (extra_model_paths.yaml 模板注释改 `DefaultModelsDirectory`)
- Delete: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs`
- Delete: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs` (数据源换 `DefaultModelsDirectory`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs` (删 :106 测试 + 删 fixture :41)

**Step 2.1:** Write failing test: `Settings.SharedModelsDirectory` 不再存在

Create `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsNoSharedModelsTests.cs`:

```csharp
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v0.6.11+ SDD B T2:SharedModelsDirectory 字段从 model 移除。模型上根本没有这个属性;
/// 启动时 _repo.Load() 静默忽略 disk 上遗留的 shared_models_directory JSON 字段
/// (原生 JsonSerializer 默认)。
/// </summary>
public sealed class SettingsNoSharedModelsTests
{
    [Fact]
    public void Settings_DoesNotExposeSharedModelsDirectory()
    {
        var s = new Settings();
        // 编译期就不该有这个 property;这条 assertion 主要是为了 reviewer grep
        // 一眼能验。如果有人误加回 SharedModelsDirectory property,这条会编译失败
        // —— 删它。
        Assert.False(typeof(Settings).GetProperty("SharedModelsDirectory") is not null,
            "SharedModelsDirectory property 应已从 Settings model 删除");
    }

    [Fact]
    public void JsonLoad_IgnoresLegacySharedModelsDirectoryField()
    {
        // 模拟老 disk:写一份含 shared_models_directory 的 JSON
        var legacyJson = """{"shared_models_directory":"D:\\legacy","default_models_directory":"D:\\default"}""";
        var s = JsonSerializer.Deserialize<Settings>(legacyJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(s);
        // legacy 字段被忽略,不影响 default 字段读取
        Assert.Equal("D:\\default", s!.DefaultModelsDirectory);
    }
}
```

Run (must compile-fail because `Settings.SharedModelsDirectory` exists at line 52):

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsNoSharedModels" -v minimal
```

Expected: compile failure.

**Step 2.2:** Delete `Settings.SharedModelsDirectory` + remove all references

(a) In `src-wpf/ComfyUI.Manager/Models/Settings.cs`, delete the entire `SharedModelsDirectory` block (the property + the `[JsonPropertyName(...)]` attribute on line 52):

```csharp
    [JsonPropertyName("default_models_directory")]
    public string DefaultModelsDirectory { get; set; } = "";
    // ↓↓↓ 删 ↓↓↓
    // [JsonPropertyName("shared_models_directory")]
    // public string SharedModelsDirectory { get; set; } = "";
    // ↑↑↑ 删 ↑↑↑
    // —— 环境 / 工具 ——
```

(b) In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:
- Delete the entire `SharedModelsDirectory` property + setter block (lines 431-435 area).
- In `RaiseAllPropertiesChanged()` near line 747, delete the `RaisePropertyChanged(nameof(SharedModelsDirectory));` line.

(c) In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`, delete the entire "共享 Models 目录" block (the comment + TextBlock label + DockPanel containing the Browse button and TextBox — currently around lines 62-71). The whole block to delete:

```xml
            <!-- v0.6.7.3: 共享 Models 目录 -->
            <TextBlock Text="共享 Models 目录(留空 = 不共享)" Margin="0,8,0,4" />
            <DockPanel Margin="0,2,0,0">
                <Button DockPanel.Dock="Right" Content="浏览..."
                        Click="BrowseSharedModelsDirectory"
                        Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
                <TextBox Text="{Binding SharedModelsDirectory, UpdateSourceTrigger=PropertyChanged}"
                         Style="{StaticResource MaterialTextBox}" />
            </DockPanel>
            <TextBlock Text="所有 env 的 ComfyUI/models 都 junction 到此目录;新建 env 自动生效,已建 env 启动时检查并重建。"
                       Foreground="Gray" FontSize="11" Margin="0,2,0,0" TextWrapping="Wrap" MaxWidth="480"
                       HorizontalAlignment="Left" />
```

(d) In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`, delete the entire `BrowseSharedModelsDirectory` method (currently lines 116-123):

```csharp
    private void BrowseSharedModelsDirectory(object sender, RoutedEventArgs e)
    {
        var picked = DataContext is SettingsViewModel vm ? vm.PickFolder() : null;
        if (!string.IsNullOrEmpty(picked) && DataContext is SettingsViewModel vm2)
        {
            vm2.SharedModelsDirectory = picked;
        }
    }
```

(e) In `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`, do 4 replacements:

- Line 33: `private readonly string _sharedModelsDirectory;` → `private readonly string _modelsDirectory;`
- Line 47 ctor param: `string sharedModelsDirectory = "",` → `string modelsDirectory = "",`
- Lines 60-62 (comment + assignment):
  ```csharp
          // v0.6.7.3: SharedModelsDirectory 空 = 不动 models 目录(走独立布局);
          // 非空则启动前 EnsureModelsJunctionAsync 检查/重建 junction。
          _sharedModelsDirectory = sharedModelsDirectory ?? "";
  ```
  → ```csharp
          // v0.6.7.3 + v0.6.11+ T2:用户配置的全局 models 目录(env-create 时 junction,
          // env-start 时检查并重建)。空 = 不动 models 目录(走独立布局)。
          _modelsDirectory = modelsDirectory ?? "";"""
- Line 161 comment: `// v0.6.7.3: 启动前检查并重建 Models junction(改 SharedModelsDirectory 后自动生效)。` → `// v0.6.7.3 + v0.6.11+ T2:启动前检查并重建 Models junction(改 DefaultModelsDirectory 后自动生效)。`
- Line 432 (in `EnsureModelsJunctionAsync`): `if (string.IsNullOrWhiteSpace(_sharedModelsDirectory)) return;` → `if (string.IsNullOrWhiteSpace(_modelsDirectory)) return;`
- Line 438: `var sharedFull = Path.GetFullPath(_sharedModelsDirectory);` → `var sharedFull = Path.GetFullPath(_modelsDirectory);`
- (Note: `modelsLink` / `sharedFull` local variable names are fine — they're about the target path, not the field semantic. Keep them.)

(f) In `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`:

- Line 23 (step comment block):
  ```csharp
  ///   5.5 链接共享 Models(Settings.SharedModelsDirectory 非空时,models → 共享目录)
  ```
  → ```csharp
  ///   5.5 链接默认 Models 目录(Settings.DefaultModelsDirectory 非空时,models → 该目录)
  ```
- Lines 145-164 (entire step 5.5 block) — replace with:
  ```csharp
          // 5.5 链接默认 Models 目录(v0.6.11+ T2 合并:Shared 字段删除,只此一条)。
          if (!string.IsNullOrWhiteSpace(_settings.DefaultModelsDirectory))
          {
              var modelsDirFull = Path.GetFullPath(_settings.DefaultModelsDirectory);
              var modelsLink = Path.Combine(comfyuiLink, "models");
              progress?.Report(new CreateStepReport("链接 Models 目录",
                  $"junction: {modelsLink} → {modelsDirFull}"));
              try
              {
                  if (Directory.Exists(modelsLink))
                  {
                      Directory.Delete(modelsLink, recursive: true);
                  }
                  await _linker.CreateAsync(modelsLink, modelsDirFull, ct);
              }
              catch (Exception ex)
              {
                  try { Directory.Delete(rootPath, recursive: true); } catch { }
                  throw new CreateEnvException("MODELS_LINK_FAILED",
                      $"Models junction 创建失败: {ex.Message}");
              }
          }
  ```
- Delete entire step 5.6 block (current lines 171-196):
  ```csharp
          // 5.6 链接默认 Models 目录(若 DefaultModelsDirectory 非空且 SharedModelsDirectory 未配置)
          if (!string.IsNullOrWhiteSpace(_settings.DefaultModelsDirectory)
              && string.IsNullOrWhiteSpace(_settings.SharedModelsDirectory))
          { ... }
  ```

(g) In `src-wpf/ComfyUI.Manager/App.xaml.cs:121`, change:
```csharp
            settings.SharedModelsDirectory);
```
to:
```csharp
            settings.DefaultModelsDirectory);
```

(h) In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:433`, change:
```csharp
            "# 编辑 base_directory 指向共享 Models 目录(配合 Settings.SharedModelsDirectory)\n"
```
to:
```csharp
            "# 编辑 base_directory 指向全局默认 Models 目录(配合 Settings.DefaultModelsDirectory)\n"
```

**Step 2.3:** Delete the two test files

```bash
cd "D:/ToolDevelop/ComfyUI"
git rm tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs
git rm tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs
```

**Step 2.4:** Modify the two surviving test files

(a) In `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs`, do find/replace:

- `SharedModelsDirectory` → `ModelsDirectory`(出现在 `_settings.SharedModelsDirectory = ...` 这种行)
- `shared` → `models`(局部变量名)
- 文件注释里的 v0.6.7.3 加上 `+ v0.6.11+ T2(DefaultModelsDirectory)`
- 测试方法名 `SharedModelsDirectorySet_JunctionsModels` → `ModelsDirectorySet_JunctionsModels`

(b) In `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs`:
- Line 41:删 `SharedModelsDirectory = "",`(字段已删)
- Lines 106-124:删 `DefaultModelsDirectoryAndSharedModelsDirectory_BothSet_SharedModelsWins` 测试方法
- 文件注释里的 "SharedModelsDirectory 非空时 5.6 不执行(Shared 优先,Default 兜底)" 改 "DefaultModelsDirectory 非空时 5.5 建一条 models 链接"

**Step 2.5:** Run the no-shared-models tests, must PASS now

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsNoSharedModels" -v minimal
```

Expected: 2 PASS.

**Step 2.6:** Verify full build + targeted test suites

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
# 必须 0 编译错误,0 警告污染(已有 warning 保留)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~EnvCreator|FullyQualifiedName~ProcessLauncher" -v minimal
```

Expected: 既有 + 改后测试全 PASS。注意 `EnvCreatorServiceDefaultModelsDirectoryTests` 改完少了 1 个 case,`ProcessLauncherSharedModelsTests` 改名后总数不变,`SettingsViewModelSharedModelsTests` 整个文件没了。

**Step 2.7:** Dead-reference check

```bash
cd "D:/ToolDevelop/ComfyUI"
grep -rn "SharedModelsDirectory\|sharedModelsDirectory\|_sharedModelsDirectory" src-wpf/ tests-wpf/ --include="*.cs" --include="*.xaml"
```

Expected: empty output (除了 obj/bin 二进制)。

**Step 2.8:** Commit

```bash
cd "D:/ToolDevelop/ComfyUI"
git add -u src-wpf/ tests-wpf/
git commit -m "$(cat <<'EOF'
refactor(wpf): remove SharedModelsDirectory; DefaultModelsDirectory is sole models source

用户原话"保留全局默认 Models 目录,去掉共享 Models 目录"。Settings
SharedModelsDirectory 字段从 model 删除,启动时 disk 上的旧
shared_models_directory JSON 字段被 JsonSerializer 静默忽略。
ProcessLauncher ctor 参数 sharedModelsDirectory → modelsDirectory
(位置参数不改调用语法);EnvCreatorService 步骤 5.5/5.6 合并为
唯一 DefaultModelsDirectory 链接;App.xaml.cs 传参同步改。

- Settings.cs: 删 SharedModelsDirectory property
- SettingsViewModel.cs: 删 property + setter + RaiseAll 行
- SettingsView.xaml/.xaml.cs: 删整块「共享 Models 目录」UI + handler
- ProcessLauncher.cs: 字段/ctor 参数/注释重命名
- EnvCreatorService.cs: 5.5/5.6 合并成单一 DefaultModelsDirectory 链接
- 2 个 test 文件整体删除(SettingsViewModelSharedModels + EnvCreatorServiceSharedModels)
- 2 个 test 文件改写(ProcessLauncherSharedModels → ModelsDirectory,
  EnvCreatorServiceDefaultModelsDirectory 删 1 case)
- 2 个新增测试钉住"property 不存在 + 旧 JSON 字段被忽略"

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 主窗口关闭 guard + ConfirmDiscardUnsavedSettings

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (加 `ConfirmDiscardUnsavedSettings` + `UnsavedChoice` enum + `UnsavedPromptOverride` seam + 私有 `PromptUnsaved`)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` (`OnClosing` 顶部插 guard)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs`

**Step 3.1:** Write failing tests (TDD)

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.11+ SDD B T3:主窗口关闭 guard。Settings 有未保存改动时弹 3 按钮
/// MessageBox(是=保存退出/否=丢弃退出/取消=留下),test seam UnsavedPromptOverride
/// 防 STA 死锁。
/// </summary>
public sealed class MainViewModelUnsavedSettingsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dbPath;
    private readonly string _rootDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;

    public MainViewModelUnsavedSettingsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "mvm-unsaved-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _settingsPath = Path.Combine(_rootDir, "settings.json");
        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _settingsRepo = new SettingsRepository(_settingsPath);
        _settings = _settingsRepo.Load();
        // 注入默认 Settings,不让 ctor 从 disk 加载
        SettingsDefaults.Apply(_settings, _rootDir);
        _settingsRepo.Save(_settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private MainViewModel NewMvm() => new MainViewModel(
        _factory, _settingsRepo, _settings,
        projectRoot: _rootDir,
        baseEnvInstaller: null,
        envCreator: null,
        profileLoader: null,
        envDeleter: null,
        nodeOps: null,
        requirementsInstaller: null,
        baseEnvUninstaller: null,
        requirementsUninstaller: null,
        comfyUiManagerInstaller: null,
        gitProxy: GitProxyConfig.Disabled,
        themeService: null,
        processLauncher: null,
        orchestrator: null,
        catalogRefreshService: null,
        catalogCacheStore: null,
        uiPreferencesService: null,
        browserLauncher: null,
        settings: _settings,
        logger: null);

    [Fact]
    public void ConfirmDiscard_NoSettingsVm_ReturnsTrue()
    {
        var mvm = NewMvm();
        // CurrentView 是 Dashboard,CurrentSettingsViewModel 是 null
        var result = mvm.ConfirmDiscardUnsavedSettings();
        Assert.True(result);
    }

    [Fact]
    public void ConfirmDiscard_Clean_ReturnsTrue_NoPrompt()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);    // 缓存 + 切到 Settings tab
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());    // 没 dirty → 直接允许关闭
    }

    [Fact]
    public void ConfirmDiscard_Dirty_SaveChoice_PersistsAndReturnsTrue()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "save-me";   // 标 dirty

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Save;
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());

        var fresh = _settingsRepo.Load();
        Assert.Equal("save-me", fresh.DefaultModelsDirectory);
        Assert.False(svm.HasUnsavedChanges);
    }

    [Fact]
    public void ConfirmDiscard_Dirty_DiscardChoice_RevertsAndReturnsTrue()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "discarded-value";

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Discard;
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());

        Assert.False(svm.HasUnsavedChanges);
        Assert.Equal("", svm.DefaultModelsDirectory);   // disk 默认值
        // disk 没动
        var fresh = _settingsRepo.Load();
        Assert.Equal("", fresh.DefaultModelsDirectory);
    }

    [Fact]
    public void ConfirmDiscard_Dirty_CancelChoice_ReturnsFalse_KeepsDirty()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "keep-dirty";

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Cancel;
        Assert.False(mvm.ConfirmDiscardUnsavedSettings());

        Assert.True(svm.HasUnsavedChanges);
        Assert.Equal("keep-dirty", svm.DefaultModelsDirectory);
        var fresh = _settingsRepo.Load();
        Assert.Equal("", fresh.DefaultModelsDirectory);   // 没写盘
    }
}
```

(The MainViewModel ctor signature varies — implementer MUST inspect `MainViewModel.cs` constructor and pass the correct positional / named args to fit the existing one; many args can be null. If the signature has changed, the implementer updates the `NewMvm()` helper accordingly, keeping test intent intact.)

Run (must compile-fail — `ConfirmDiscardUnsavedSettings` / `UnsavedChoice` / `UnsavedPromptOverride` not exist):

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelUnsavedSettings" -v minimal
```

**Step 3.2:** Implement in `MainViewModel.cs`

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`, add (anywhere; recommend near the bottom in a new region):

```csharp
    // ============ v0.6.11+ SDD B T3:主窗口关闭 guard ============

    internal enum UnsavedChoice { Save, Discard, Cancel }

    /// <summary>
    /// 测试 seam:STA 测试环境外不能弹真 MessageBox。生产路径为 null,走 <see cref="PromptUnsaved"/>。
    /// </summary>
    internal Func<int, UnsavedChoice>? UnsavedPromptOverride { get; set; }

    /// <summary>
    /// 检查当前缓存的 SettingsViewModel 是否有未保存改动,有则弹三按钮框。
    /// 返回 true = 可以继续关闭,false = 用户选了"取消"。
    /// </summary>
    internal bool ConfirmDiscardUnsavedSettings()
    {
        var vm = CurrentSettingsViewModel;
        if (vm is null || !vm.HasUnsavedChanges) return true;

        var choice = (UnsavedPromptOverride ?? PromptUnsaved)(vm.UnsavedCount);
        switch (choice)
        {
            case UnsavedChoice.Save:
                vm.SaveCommand.Execute(null);
                return true;
            case UnsavedChoice.Discard:
                vm.DiscardCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private static UnsavedChoice PromptUnsaved(int count)
    {
        var r = MessageBox.Show(
            $"您有 {count} 项设置尚未保存。\n\n是 = 保存并退出\n否 = 丢弃并退出\n取消 = 返回",
            "未保存的设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return r switch
        {
            MessageBoxResult.Yes => UnsavedChoice.Save,
            MessageBoxResult.No  => UnsavedChoice.Discard,
            _                    => UnsavedChoice.Cancel,
        };
    }
```

(Add `using System.Windows;` if not already imported.)

**Step 3.3:** Wire `MainWindow.xaml.cs` `OnClosing`

In `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`, modify `OnClosing` (currently at line 134). Prepend the guard at the very top of the method body:

```csharp
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // v0.6.11+ SDD B T3:settings 未保存改动拦截
        if (DataContext is MainViewModel mvm && !mvm.ConfirmDiscardUnsavedSettings())
        {
            e.Cancel = true;
            return;
        }

        // 写回 prefs(G8)— 只读当前 Window 状态(简化版,完整版本由
        // LastSelectedEnvId 在 MainViewModel 维护)
        // ... 现有代码保持不变 ...
```

**Step 3.4:** Run targeted tests — must PASS

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelUnsavedSettings" -v minimal
```

Expected: 5 PASS.

**Step 3.5:** Verify full build clean

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors.

**Step 3.6:** Commit

```bash
cd "D:/ToolDevelop/ComfyUI"
git add src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/MainWindow.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): main-window OnClosing guard for unsaved settings

切侧栏离开 settings tab 不拦截(VM 缓存,dirty 活着),
主窗口关闭时一次性拦截:MessageBox 三按钮 YesNoCancel,
Yes = Save 写盘退出,No = Discard 回滚退出,Cancel = 留下。
test seam UnsavedPromptOverride 让 STA 测试不弹真框。
切 tab 不弹避免噪音。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 最终 review + MEMORY + staging rebuild + GUI smoke

**Files:**
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (加索引条目)
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_settings_dirty_save.md`
- Create (optional): `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\feedback_wpf_dirty_save_pattern.md`(如果本次出现 reusable 教训)

**Step 4.1:** Full test suite

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

Expected: 860 baseline + ~15 new = ~875 PASS / 3 pre-existing FAIL (`ProcessLauncherProgressTests` ×2 flaky + `ComfyUIManagerInstallerTests.InstallAsync_RealGit_ClonesRepo` ×1) / 1 SKIP. Document the 3 FAILs as pre-existing in the task report.

**Step 4.2:** Dead-reference check (final)

```bash
cd "D:/ToolDevelop/ComfyUI"
grep -rn "SharedModelsDirectory\|BulkUpdateDialog\|OpenBulkUpdateCommand" src-wpf/ tests-wpf/ --include="*.cs" --include="*.xaml"
```

Expected: empty.

**Step 4.3:** Staging rebuild (self-contained win-x64)

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: 0 errors. Staging folder under `release/staging/ComfyUI Manager/` includes the rebuilt `ComfyUI.Manager.exe`.

**Step 4.4:** GUI smoke checklist (manual user verification — describe in commit message / SDD report)

启动 staging → 主页 → 设置 tab:

1. 改任意 field(例 Catalog TTL)→ 该行 label 后出现 ⚠️,toolbar 显示「⚠ N 项未保存」+ Save/Discard enabled
2. 继续改第二个 field → 计数变 2
3. 切到「主页」tab → 切回设置 → ⚠️ 与计数仍在(VM 缓存,不弹框,不丢失)
4. 切到「环境」tab → 切回设置 → 同上(任意切来切去都不弹框)
5. 点 Save → 所有 ⚠️ 消失,toolbar 计数归零;关闭 staging,重启 → 改动仍在
6. 改 field → 关主窗口 → 弹 3 按钮框
   - 选「是」(保存) → 写盘后退出,重启验证改动在
   - 改 field → 再关 → 选「否」(丢弃) → 改动回滚,重启验证旧值
   - 改 field → 再关 → 选「取消」 → 留在主窗口,⚠️ 仍在
7. 设置页不再有「共享 Models 目录」行;「全局默认 Models 目录」仍在且 env-create 链接生效(env-create 新 env → 验证 `<env>/ComfyUI/models` 是 junction 指向配置的目录)

**Step 4.5:** Final commit — MEMORY update

Add to `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`(preserve one-line entry style):

```markdown
- [Settings dirty-save + 移除 SharedModelsDirectory v0.6.11+ SDD](project_v0_6_11_plus_settings_dirty_save.md) — SDD B SHIP-READY,T1 dirty plumbing + T2 SharedModels 移除 + T3 OnClosing guard;HEAD `<TBD>`,baseline 860/3/1 → +N PASS;DeleteModelsDirectory 字段从 model 删除,JsonSerializer 静默忽略 disk 上遗留的 shared_models_directory;EnvCreatorService 5.5/5.6 合并成唯一 DefaultModelsDirectory 链接;ProcessLauncher ctor 参数 sharedModelsDirectory → modelsDirectory;27 setter 改走 MarkDirty,集合增删命令保留即时写盘;ThemeMode 仍即时 Apply(Discard 反向 Apply);切 tab 不拦截(VM 缓存),仅主窗口关闭时 3 按钮拦截
```

Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_settings_dirty_save.md` with full detail (similar to existing `project_*.md` files — commit SHAs,test counts,lessons).

(Optional but recommended) Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\feedback_wpf_dirty_save_pattern.md`:

```markdown
---
name: WPF dirty-save pattern — 浮动 Save + 索引器 binding
description: WPF Settings-style 整页 Save 按钮用 DirtyLookup(带 INPC 索引器的小对象),XAML {Binding Dirty[PropertyName]} 走 WPF "Item[]" notify 约定重新求值;Save 写盘,Discard 用 CopyInto 就地回写到共享实例(不换引用,避免其它服务持旧对象);Theme 这种"改即预览"字段 Save 不写盘但 Discard 必须反向 Apply 才能回滚
type: feedback
---
**Rule**: ...
```

(filled by implementer based on what they actually learned during execution)

```bash
cd "D:/ToolDevelop/ComfyUI"
# No automated commit — MEMORY updates are repo-claude-private, NOT in main git repo
```

---

## Critical Files (full list)

**新建**
- `src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs`
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/DirtyLookupTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsNoSharedModelsTests.cs`
- `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_settings_dirty_save.md`
- (optional)`C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\feedback_wpf_dirty_save_pattern.md`

**修改**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`
- `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml`
- `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml`
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`
- `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`
- `src-wpf/ComfyUI.Manager/App.xaml.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs`
- `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`

**删除**
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs`

---

## Verification (end-to-end)

```bash
# T1 验证
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~SettingsViewLoad|FullyQualifiedName~DirtyLookup" -v minimal   # 31 PASS

# T2 验证
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsNoSharedModels|FullyQualifiedName~EnvCreator|FullyQualifiedName~ProcessLauncher" -v minimal   # 全 PASS
grep -rn "SharedModelsDirectory" src-wpf/ tests-wpf/ --include="*.cs" --include="*.xaml"   # empty

# T3 验证
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelUnsavedSettings" -v minimal   # 5 PASS

# T4 验证(所有 commit 合并后)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 860 baseline + 15 new = ~875 PASS / 3 pre-existing FAIL / 1 SKIP
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal   # 0/0
```

**GUI smoke(桌面验证,user)**:见 Task 4 Step 4.4 七步。

---

## Risks

| 风险 | 缓解 |
|------|------|
| 27 个 setter 改写漏一个 → 行为分裂(部分自动写盘,部分走 dirty) | 实施前逐行 grep 输出 → 实施后 `grep -c "_repo.Save(_settings);" src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` 应只剩 ctor :61 + 5 CollectionChanged + 2 collection-command = 8 处 |
| `Settings.CopyInto` 漏字段 → Discard 不完整 | 实施后跑既有 `SettingsViewModelTests` + 改后的 dirty 测试,断言 `DiscardCommand_LeavesDiskUnchanged` |
| `_themeService?.Apply` 在 ctor 路径之外的调用点漏 Apply(Discard 必须反向) | `DiscardCommand_RevertsThemeMode_InMemory` 测试钉死 |
| `UnsavedPromptOverride` seam 类型签名漏 `internal` → 跨程序集访问失败 | 测试文件同 csproj(`InternalsVisibleTo("ComfyUI.Manager.Tests")` 已有,`internal` 自动可见) |
| `MainViewModel` ctor 签名每次 PR 微调 → 测试 fixture 写错参数顺序 | `NewMvm()` 全部用**命名参数**(Step 3.1 已用 named args),编译失败时由 implementer 修正 |
| `Palette.Light.xaml` / `Palette.Dark.xaml` 颜色定义顺序敏感 | 跟随文件末尾追加(`<Color x:Key="WarningColor">`);不挪动既有 color 定义 |
| `SettingsView.xaml` 27 行的 ⚠️ 手工加错(漏写 / 错 property name) | 实施后 `grep -c 'Dirty\[' src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` 应 = 27 |
| `MainWindow.xaml.cs:OnClosing` 在 `SaveUiPreferences` 之前拦截 → 关闭时永远写不回 prefs | Step 3.3 是 **prepend**(顶部插入 guard,返回 `true` 后 fall-through 到现有逻辑);现有逻辑一行不动 |
| 既有 `SettingsViewModelTests` 依赖"setter 立即写盘"断言会 fail | Step 1.6 后再修:`LanguageSet_PersistsToFile` 等改成 `setter → SaveCommand.Execute → 断言` |

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例)**:
- 4 task × (implementer + reviewer) ≈ 8 dispatch
- 4 commit on main,最后 staging rebuild + GUI smoke + MEMORY update

If this plan is relevant to the current work and not already complete, continue working on it.