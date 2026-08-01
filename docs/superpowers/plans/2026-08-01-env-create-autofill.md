# 新建环境 Auto-Fill from Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `CreateEnvDialog` 打开时从 `Settings` 自动拉 Python 解释器 + ComfyUI 模板路径填进 VM 的两个字段,加 "应用模板" 按钮让用户手动重新拉,并在模板缺失时 dialog 顶部黄色提示。

**Architecture:** 加一个 `Settings.DefaultPythonVersion` 字段(默认 `"3.10"`)用于从 `TemplatePythonDir` 模板根解析出具体 `<version>/python.exe` 子目录。`CreateEnvDialogViewModel` ctor 多接 `(Settings settings, string projectRoot)`,新增 `ApplyTemplate()` 公开方法 + `ApplyTemplateCommand` 命令 + `TemplateWarningMessage` 顶部黄色提示字段;`Show()` 静态方法签名同步扩展。`EnvironmentListViewModel` / `MainViewModel` / `App.xaml.cs` 配套把 `projectRoot` 串到 dialog 调用点。`Layout` ComboBox 切换**不**触发重新填充(只 dialog open + Apply 按钮)。

**Tech Stack:** WPF .NET 8 / C# 12 · hand-rolled MVVM (ViewModelBase + RelayCommand) · xUnit · System.Text.Json · csproj `InternalsVisibleTo("ComfyUI.Manager.Tests")`

## Context

`specs/2026-08-01-env-create-autofill-design.md` 是 source of truth。本 plan 完全 follow spec §0–§8。decisions(2026-08-01 brainstorm):

1. shared + independent 两个布局都 auto-fill
2. dialog 初次打开填充一次;`Layout` ComboBox 切换**不**重新填充
3. "应用模板" 按钮让用户手动重新从 settings 拉
4. 新增 `Settings.DefaultPythonVersion` 字段(默认 `"3.10"`),用于解析
   `<TemplatePythonDir>/<DefaultPythonVersion>/python.exe`
5. 模板缺失时静默留空 + dialog 顶部黄色提示
6. 架构:VM ctor 加 `ApplyTemplate()` 公开方法(不抽 service,YAGNI)

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | 默认 `DefaultPythonVersion` = `"3.10"` | spec §1 + 用户决策 |
| G2 | 路径拼接用 `Path.Combine(projectRoot, settings.TemplatePythonDir, settings.DefaultPythonVersion, "python.exe")`(相对路径模式,沿用 `SettingsDefaults` 现存惯例) | spec §2.3 |
| G3 | `CreateEnvDialogViewModel` ctor 接受 `(EnvCreatorService creator, Settings settings, string projectRoot, Action<Models.Environment?>? onResult = null)` — `onResult` 保留向后兼容(原 ctor 第 3 参) | spec §2.3 + 现有代码 line 13-15 |
| G4 | `CreateEnvDialog.Show(creator, settings, projectRoot)` 静态方法签名扩展 | spec §2.5 |
| G5 | `MainViewModel` ctor 多接 `string projectRoot` 一参;`EnvironmentListViewModel` ctor 多接 `string projectRoot` 一参 | spec §2.6 + 现有代码 `MainViewModel.cs:43-58` + `EnvironmentListViewModel.cs:32-45` |
| G6 | `App.xaml.cs:21` 已有 `projectRoot` 局部变量,直接串到 `MainViewModel` ctor | spec §2.6 + 现有 `App.xaml.cs:21-22` |
| G7 | `Layout` ComboBox `SelectionChanged` 不接 auto-fill(决策 2),只 dialog open + Apply 按钮触发 | spec §0 + §2.3 |
| G8 | `EnvCreatorService.CreateAsync` 签名 / 行为 / validation 一字不动 | spec §6 |
| G9 | 现有 `CreateEnvDialog.xaml` 5 字段布局不改顺序,只在顶部插入提示 + 在 Python 字段下方加按钮 | spec §6 + §2.4 |
| G10 | 测试不依赖真实文件 IO,用 `FakeSettings` 或直接 new `Settings { ... }` + temp dir(`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())`) | spec §5 |
| G11 | 增量 bump 版本号 v0.6.5.3 → v0.6.5.4(5 处字面量) | 项目惯例(详见 T8) |
| G12 | 不抽 `EnvTemplateAutoFillService`(决策 6,YAGNI) | spec §8 |
| G13 | 项目已结题(M5 终版),新版本是 hotfix;release notes 文件名 `release/RELEASE-NOTES-v0.6.5.4.md` | 项目惯例 |
| G14 | `feedback_no_zip.md`:不打 zip / 不主动压缩;smoke 走 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` | 已有 feedback memory |

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs` | ~50 | JSON round-trip test for `DefaultPythonVersion`(同文件用 xUnit `Fact`) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs` | ~200 | 9 个 `ApplyTemplate_*` / `ApplyTemplateCommand_*` / `Constructor_AppliesTemplateOnInit` 测试 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 加 `[JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";`(line 24-25 后) |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 在 `TemplateComfyuiDir` row 后(line 158)插 `DefaultPythonVersion` ComboBox row |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | 加 `DefaultPythonVersion` passthrough 属性 + `RaiseAllPropertiesChanged` 列表加一项 |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | ctor 多接 settings+projectRoot;加 `TemplateWarningMessage` 字段 + `ApplyTemplateCommand`;加 `ApplyTemplate()` 方法 |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` | Name 字段下方插 `TemplateWarningMessage` TextBlock(用 `NullToVisibilityConverter`);Python + ComfyUI 两个 DockPanel 各加一个"应用模板"按钮(绑 `ApplyTemplateCommand`) |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs` | `Show(creator)` → `Show(creator, settings, projectRoot)` |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | ctor 多接 `string projectRoot` 一参(line 32-38 后);`_settings` 已存,新增 `_projectRoot` 字段;`CreateEnv()` 调 `Show(_envCreator, _settings, _projectRoot)` |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | ctor 多接 `string projectRoot` 一参(line 43-58);`ShowEnvironments()` 把 projectRoot 传给 `EnvironmentListViewModel`(line 88) |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `MainViewModel` ctor 多传 `projectRoot` 一参(line 81 附近) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` | ctor 多接 `null!` 或 temp dir(string)一参(line 38-45) |
| `pyproject.toml` + `src/comfy_mgr/__init__.py` + `shared/errors.json` + `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` + `tests/test_version_consistency.py` | 5 处版本字面量 `0.6.5.3` → `0.6.5.4` |
| `release/RELEASE-NOTES-v0.6.5.4.md` (new) | release notes |
| `tests-wpf/ComfyUI.Manager.Tests/AppWiringTests.cs` | 不动 |

### Delete

无。

### Keep (unchanged)

- `EnvCreatorService` / `VenvCreator` / `JunctionLinker`
- `BaseEnvProfileLoader` / `BaseEnvProfile`
- `BaseEnvViewModel` / `BaseEnvView` / `BaseEnvProgressDialog` / `BaseEnvProgressViewModel`
- `CatalogView` / `EnvironmentListView`(只 ViewModel ctor 改,View 不动)
- `SettingsDefaults`(不动 — `DefaultPythonVersion` 已有内联默认值,不需要再 Apply 时填)
- `SettingsRepository` / `GitProxyConfig`

---

## Tasks

### Task 1: `Settings.DefaultPythonVersion` POCO + JSON round-trip test

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:23-28`(在 `template_comfyui_dir` 后加 `default_python_version`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs`(新文件,`namespace ComfyUI.Manager.Tests.Models`)

**Interfaces:**
- Consumes: nothing
- Produces: `Settings.DefaultPythonVersion` (`string`, default `"3.10"`)

- [ ] **Step 1: Write failing JSON round-trip test**

Create `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs`:

```csharp
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsTests
{
    [Fact]
    public void DefaultPythonVersion_DefaultsTo310()
    {
        var s = new Settings();
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_RoundTripsViaJson()
    {
        var s = new Settings { DefaultPythonVersion = "3.11" };
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"default_python_version\":\"3.11\"", json);
        var restored = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(restored);
        Assert.Equal("3.11", restored!.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_DefaultsWhenJsonMissing()
    {
        // 旧 settings.json 没有 default_python_version 字段 → 反序列化后仍是 "3.10"
        var oldJson = "{\"template_python_dir\":\"python\",\"template_comfyui_dir\":\"ComfyUI\"}";
        var restored = JsonSerializer.Deserialize<Settings>(oldJson);
        Assert.NotNull(restored);
        Assert.Equal("3.10", restored!.DefaultPythonVersion);
    }
}
```

- [ ] **Step 2: Run test, verify FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsTests" -v minimal
```

Expected: `DefaultPythonVersion_DefaultsTo310` fails with `DefaultPythonVersion` not found (编译失败 or 运行时 NRE);其他 2 个测试因编译失败也被 skip。

- [ ] **Step 3: Add `DefaultPythonVersion` field to `Settings`**

Edit `src-wpf/ComfyUI.Manager/Models/Settings.cs` — 在 line 25 `[JsonPropertyName("template_comfyui_dir")]` 之后插入一行(line 26 后):

```csharp
[JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";
```

完整结果(line 23-28):

```csharp
// —— 路径 ——
[JsonPropertyName("template_python_dir")] public string TemplatePythonDir { get; set; } = "";
[JsonPropertyName("template_comfyui_dir")] public string TemplateComfyuiDir { get; set; } = "";
[JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";
[JsonPropertyName("envs_dir")] public string EnvsDir { get; set; } = "";
[JsonPropertyName("global_nodes_dir")] public string GlobalNodesDir { get; set; } = "";
```

- [ ] **Step 4: Run test, verify PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsTests" -v minimal
```

Expected: 3 PASS / 0 FAIL.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs
git commit -m "feat(wpf): Settings.DefaultPythonVersion + JSON round-trip tests"
```

---

### Task 2: `SettingsView` + `SettingsViewModel` 加 `DefaultPythonVersion` UI / passthrough

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:151-158`(在 `TemplateComfyuiDir` row 后插 `DefaultPythonVersion` row)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:180-189`(加 `DefaultPythonVersion` passthrough 属性)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:342-359`(`RaiseAllPropertiesChanged` 加一项)

**Interfaces:**
- Consumes: Task 1 的 `Settings.DefaultPythonVersion`
- Produces: `SettingsViewModel.DefaultPythonVersion` (`string` 属性,passthrough 到 `_settings.DefaultPythonVersion` + `_repo.Save`)

- [ ] **Step 1: Add `DefaultPythonVersion` ComboBox row to `SettingsView.xaml`**

Edit `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — 在 line 158 (`</DockPanel>` 结束 TemplateComfyuiDir row) 后插入新 row:

```xml
            <TextBlock Text="默认 Python 版本(auto-fill 时使用)" Margin="0,8,0,4" />
            <DockPanel Margin="0,2,0,0">
                <ComboBox DockPanel.Dock="Right" Width="120"
                          IsEditable="True"
                          Text="{Binding DefaultPythonVersion, UpdateSourceTrigger=PropertyChanged}" />
                <TextBlock VerticalAlignment="Center" Foreground="Gray" FontSize="11"
                           Text="(auto-fill 时选 TemplatePythonDir 下的哪个版本子目录,如 3.10/3.11/3.12)" />
            </DockPanel>
```

ComboBox 是 `IsEditable=true`,用户可以选预填的 `DefaultPythonVersions` 列表(由 VM 暴露),也可以手填任意版本号。

- [ ] **Step 2: Add `DefaultPythonVersion` passthrough to `SettingsViewModel`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:

(a) 在 line 189 (`public string EnvsDir { get; ... }`) 之后(line 200 之前,`// —— 环境 / 工具 ——` 之前)插入新属性:

```csharp
    public string DefaultPythonVersion
    {
        get => _settings.DefaultPythonVersion;
        set { _settings.DefaultPythonVersion = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
    }
```

(b) 在 line 143 (`public List<string> Languages { get; } = new() { "zh_CN", "en_US" };`) 后加一个:

```csharp
    public List<string> DefaultPythonVersions { get; } = new() { "3.10", "3.11", "3.12", "3.13" };
```

(c) 在 line 348 (`RaisePropertyChanged(nameof(TemplateComfyuiDir));`) 后插入:

```csharp
        RaisePropertyChanged(nameof(DefaultPythonVersion));
```

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings。

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/SettingsView.xaml src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs
git commit -m "feat(wpf): SettingsView adds DefaultPythonVersion picker"
```

---

### Task 3: `CreateEnvDialogViewModel` 加 `ApplyTemplate()` 方法 + `TemplateWarningMessage` + `ApplyTemplateCommand`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(整文件改造)

**Interfaces:**
- Consumes: Task 1 的 `Settings.DefaultPythonVersion` + 已有 `Settings.TemplatePythonDir` / `TemplateComfyuiDir`
- Produces:
  - 新 ctor:`CreateEnvDialogViewModel(EnvCreatorService creator, Settings settings, string projectRoot, Action<Models.Environment?>? onResult = null)`
  - 新属性:`string? TemplateWarningMessage { get; private set; }`
  - 新命令:`RelayCommand ApplyTemplateCommand { get; }`
  - 新方法:`void ApplyTemplate()`

- [ ] **Step 1: Write failing test scaffold**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogViewModelTests
{
    private static Settings MakeSettings(string pythonVersion = "3.10")
    {
        return new Settings
        {
            TemplatePythonDir = "python",
            TemplateComfyuiDir = "ComfyUI",
            DefaultPythonVersion = pythonVersion,
        };
    }

    private static (string projectRoot, string pythonExe, string comfyuiDir) CreateTemplateTree(string version)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var pythonExe = Path.Combine(projectRoot, "python", version, "python.exe");
        var comfyuiDir = Path.Combine(projectRoot, "ComfyUI");
        Directory.CreateDirectory(Path.GetDirectoryName(pythonExe)!);
        Directory.CreateDirectory(comfyuiDir);
        File.WriteAllText(pythonExe, "");
        File.WriteAllText(Path.Combine(comfyuiDir, "main.py"), "");
        return (projectRoot, pythonExe, comfyuiDir);
    }

    [Fact]
    public void Constructor_AppliesTemplateOnInit_WhenBothTemplatesPresent()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(
                creator: null!,
                settings: MakeSettings("3.10"),
                projectRoot: root);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_LeavesPythonExeBlank_WhenPythonTemplateMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var cm = Path.Combine(root, "ComfyUI");
        Directory.CreateDirectory(cm);
        File.WriteAllText(Path.Combine(cm, "main.py"), "");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal("", vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("3.10", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_LeavesComfyuiSourceBlank_WhenComfyuiTemplateMissing()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal("", vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("ComfyUI", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_CombinesWarnings_WhenBothTemplatesMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal("", vm.PythonExe);
            Assert.Equal("", vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("3.10", vm.TemplateWarningMessage);
            Assert.Contains("ComfyUI", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_RespectsDefaultPythonVersion()
    {
        var (root, py, cm) = CreateTemplateTree("3.11");
        try
        {
            var vm = new CreateEnvDialogViewModel(
                null!,
                MakeSettings("3.11"),
                root);
            Assert.Equal(py, vm.PythonExe);   // 解析到 3.11 子目录
            Assert.Equal(cm, vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_ClearsWarning_WhenBothTemplatesPresent()
    {
        var (root, _, _) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplyTemplate_PopulatesPythonExe_WhenTemplateExists()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            vm.PythonExe = "";  // 模拟用户清空
            vm.ApplyTemplate();
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplyTemplateCommand_ReappliesTemplate()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            vm.PythonExe = "C:\\user-overridden";
            vm.ApplyTemplateCommand.Execute(null);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Layout_DoesNotRefillOnChange()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            vm.PythonExe = "C:\\user-overridden";
            vm.Layout = "independent";   // 切 layout
            Assert.Equal("C:\\user-overridden", vm.PythonExe);  // 不应被覆盖
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test, verify FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 编译失败 — `CreateEnvDialogViewModel` ctor 当前不接受 `settings` + `projectRoot`,也没有 `TemplateWarningMessage` / `ApplyTemplate` / `ApplyTemplateCommand`。

- [ ] **Step 3: Rewrite `CreateEnvDialogViewModel`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` 全文为以下版本(120 行内):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CreateEnvDialogViewModel : ViewModelBase
{
    private readonly EnvCreatorService _creator;
    private readonly Settings _settings;
    private readonly string _projectRoot;
    private readonly Action<Models.Environment?>? _onResult;

    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Settings settings,
        string projectRoot,
        Action<Models.Environment?>? onResult = null)
    {
        _creator = creator;
        _settings = settings;
        _projectRoot = projectRoot;
        _onResult = onResult;
        CreateCommand = new RelayCommand(
            async _ => await CreateAsync(),
            _ => CanCreate());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        ApplyTemplateCommand = new RelayCommand(_ => ApplyTemplate());
        ApplyTemplate();   // 初次填充
    }

    public event Action<Models.Environment?>? Closed;

    public System.Collections.Generic.List<string> LayoutOptions { get; } =
        new() { "shared", "independent" };

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _layout = "shared";
    public string Layout
    {
        get => _layout;
        // 决策 2:layout 切换不重新 auto-fill,只 RaisePropertyChanged + RaiseCommandsChanged
        set { _layout = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _pythonExe = "";
    public string PythonExe
    {
        get => _pythonExe;
        set { _pythonExe = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _comfyuiSource = "";
    public string ComfyuiSource
    {
        get => _comfyuiSource;
        set { _comfyuiSource = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _port = "";
    public string Port
    {
        get => _port;
        set { _port = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; RaisePropertyChanged(); }
    }

    private string? _templateWarningMessage;
    public string? TemplateWarningMessage
    {
        get => _templateWarningMessage;
        private set { _templateWarningMessage = value; RaisePropertyChanged(); }
    }

    public RelayCommand CreateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyTemplateCommand { get; }

    public bool CanCreate()
    {
        if (IsBusy) return false;
        if (string.IsNullOrWhiteSpace(Name)) return false;
        if (string.IsNullOrWhiteSpace(PythonExe)) return false;
        if (Layout == "shared" && string.IsNullOrWhiteSpace(ComfyuiSource)) return false;
        return true;
    }

    /// <summary>
    /// 从 settings 读 TemplatePythonDir + DefaultPythonVersion + TemplateComfyuiDir +
    /// projectRoot 拼接,填回 PythonExe + ComfyuiSource。模板缺失时静默留空 +
    /// TemplateWarningMessage 设警告。
    /// </summary>
    public void ApplyTemplate()
    {
        var pythonExe = Path.Combine(
            _projectRoot,
            _settings.TemplatePythonDir,
            _settings.DefaultPythonVersion,
            "python.exe");
        var comfyuiSource = Path.Combine(
            _projectRoot,
            _settings.TemplateComfyuiDir);

        var warnings = new List<string>();

        if (File.Exists(pythonExe))
        {
            PythonExe = pythonExe;
        }
        else
        {
            warnings.Add($"Python 模板 {_settings.DefaultPythonVersion} 未安装,请先在设置页下载");
            PythonExe = "";
        }

        if (Directory.Exists(comfyuiSource))
        {
            ComfyuiSource = comfyuiSource;
        }
        else
        {
            warnings.Add("ComfyUI 模板目录未安装,请先在设置页下载");
            ComfyuiSource = "";
        }

        TemplateWarningMessage = warnings.Count == 0
            ? null
            : string.Join("\n", warnings);
    }

    private async Task CreateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            int? port = null;
            if (int.TryParse(Port, out var p) && p > 0) port = p;

            var env = await _creator.CreateAsync(
                Name, Layout, PythonExe,
                string.IsNullOrWhiteSpace(ComfyuiSource) ? null : ComfyuiSource,
                port,
                CancellationToken.None);
            Closed?.Invoke(env);
        }
        catch (EnvCreatorService.CreateEnvException ex)
        {
            ErrorMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandsChanged()
    {
        CreateCommand.RaiseCanExecuteChanged();
    }
}
```

- [ ] **Step 4: Run test, verify PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 9 PASS / 0 FAIL(test 文件预期 9 个 `Fact`,实际本 task 加了 9 个 + T3 加 0 = 9 个,加 T3 一起跑后总数 9)。

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs
git commit -m "feat(wpf): CreateEnvDialogViewModel ApplyTemplate + warnings"
```

---

### Task 4: `CreateEnvDialog.xaml` 加顶部黄色 `TemplateWarningMessage` TextBlock + "应用模板" 按钮

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`

**Interfaces:**
- Consumes: Task 3 的 `CreateEnvDialogViewModel.TemplateWarningMessage` + `ApplyTemplateCommand`
- Produces: 一个绑 `TemplateWarningMessage` 的 `TextBlock`(null 时 Collapsed,非空时 Visible + 黄色 Foreground);Python 字段 + ComfyUI 字段各一个"应用模板"按钮绑 `ApplyTemplateCommand`

- [ ] **Step 1: Build to verify pre-change state**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings(Task 3 已让 VM 编译通过,XAML 还没绑新属性所以 WPF 运行时会因 binding 缺失报 trace 但**编译 0 错**)。

- [ ] **Step 2: Add `TemplateWarningMessage` TextBlock above Name field**

Edit `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` — 在 line 9 (`<TextBlock Text="名称" />`) 之前(line 8-9 之间)插入:

```xml
        <TextBlock Text="{Binding TemplateWarningMessage}"
                   Foreground="Orange" FontWeight="SemiBold"
                   TextWrapping="Wrap" Margin="0,0,0,8"
                   Visibility="{Binding TemplateWarningMessage, Converter={x:Static views:NullToVisibilityConverter.Instance}}" />
```

converter 用现有 `views:NullToVisibilityConverter.Instance`(line 44 已有 `ErrorMessage` 同样的 converter)。

- [ ] **Step 3: Add "应用模板" button to Python field DockPanel**

Edit `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` line 21-27(`<DockPanel>` 含 Python TextBox + 浏览按钮),在 "浏览..." 按钮**之前**(line 22 之前)插入新按钮:

```xml
            <Button DockPanel.Dock="Right" Content="应用模板"
                    Command="{Binding ApplyTemplateCommand}"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
```

完整 Python DockPanel 结果(line 21-29):

```xml
        <DockPanel Margin="0,2,0,8">
            <Button DockPanel.Dock="Right" Content="应用模板"
                    Command="{Binding ApplyTemplateCommand}"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
            <Button DockPanel.Dock="Right" Content="浏览..."
                    Click="BrowsePython"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
            <TextBox Text="{Binding PythonExe, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>
```

- [ ] **Step 4: Add "应用模板" button to ComfyUI field DockPanel**

Edit `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` line 31-37(`<DockPanel>` 含 ComfyUI TextBox + 浏览按钮),在 "浏览..." 按钮**之前**(line 32 之前)插入新按钮:

```xml
            <Button DockPanel.Dock="Right" Content="应用模板"
                    Command="{Binding ApplyTemplateCommand}"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
```

完整 ComfyUI DockPanel 结果(line 31-39):

```xml
        <DockPanel Margin="0,2,0,8">
            <Button DockPanel.Dock="Right" Content="应用模板"
                    Command="{Binding ApplyTemplateCommand}"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
            <Button DockPanel.Dock="Right" Content="浏览..."
                    Click="BrowseComfyui"
                    IsEnabled="{Binding IsBusy, Converter={x:Static views:NotBoolConverter.Instance}}"
                    Margin="4,0,0,0" />
            <TextBox Text="{Binding ComfyuiSource, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>
```

- [ ] **Step 5: Build to verify WPF compilation**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings。

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml
git commit -m "feat(wpf): CreateEnvDialog top warning + apply template buttons"
```

---

### Task 5: `CreateEnvDialog.xaml.cs` `Show()` 签名扩展 + `EnvironmentListViewModel` 串 `projectRoot`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs:24-30`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:32-45, 132-136`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs:38-45`(配合 ctor 改)

**Interfaces:**
- Consumes: Task 3 的新 `CreateEnvDialogViewModel` ctor + `Settings` / `string projectRoot`
- Produces:
  - `CreateEnvDialog.Show(EnvCreatorService creator, Settings settings, string projectRoot)`
  - `EnvironmentListViewModel(..., string projectRoot)` 末尾多接 `projectRoot`
  - `EnvironmentListViewModel.CreateEnv()` 调 `Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot)`

- [ ] **Step 1: Update `CreateEnvDialog.Show()` signature**

Edit `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs` line 24-30:

```csharp
    public static Models.Environment? Show(EnvCreatorService creator, Models.Settings settings, string projectRoot)
    {
        var vm = new CreateEnvDialogViewModel(creator, settings, projectRoot);
        var dlg = new CreateEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
```

(`using ComfyUI.Manager.Models;` 已经在文件顶部 line 1 区域,不需要新加 — line 10 已有 `Models.Environment` 用法。)

- [ ] **Step 2: Run build to find call sites that need updating**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal 2>&1 | grep -E "error CS|error MSB" | head -20
```

Expected: `CreateEnvDialog.Show(_envCreator)` 在 `EnvironmentListViewModel.cs:134` 调用 — 旧签名缺 2 参,会编译报错:

```
src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs(134,38): error CS1503: Argument 1: cannot convert from 'EnvCreatorService' to '...'
```

(确切报错信息可能略有不同,但会是 CS1503 类的参数不匹配。)

- [ ] **Step 3: Update `EnvironmentListViewModel` ctor + CreateEnv**

Edit `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:

(a) line 22 后(在 `_profileLoader` 字段后)添加新字段:

```csharp
    private readonly string _projectRoot;
```

(b) line 32-45 ctor 改 — 在 `BaseEnvProfileLoader profileLoader` 参数后加 `string projectRoot`:

```csharp
    public EnvironmentListViewModel(
        EnvironmentRepository repo,
        ProcessLauncher launcher,
        EnvCreatorService envCreator,
        BaseEnvInstaller baseEnvInstaller,
        Settings settings,
        BaseEnvProfileLoader profileLoader,
        string projectRoot)
    {
        _repo = repo;
        _launcher = launcher;
        _envCreator = envCreator;
        _baseEnvInstaller = baseEnvInstaller;
        _settings = settings;
        _profileLoader = profileLoader;
        _projectRoot = projectRoot;
```

(c) line 134 改 `CreateEnv()`:

```csharp
    private void CreateEnv()
    {
        var created = Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot);
        if (created is not null) Load();
    }
```

- [ ] **Step 4: Update existing test to match new ctor**

Edit `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` — 找到现有 `Load_PopulatesEnvironmentsFromRepository` 测试(line 31-45 附近),在 ctor 末尾加一个 `null!` 参数(保持现有"传 null! 跳过 launcher / creator"的风格)。具体修改在 line 38:

```csharp
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);    // ← projectRoot,测试用不到
```

如果文件里有更多 `new EnvironmentListViewModel(...)` 调用(其他测试),都同样加一个 `null!` 末尾。

- [ ] **Step 5: Build to verify all callers compile**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings(`MainViewModel.cs:88` 的 `EnvironmentListViewModel` 构造还差一个 `projectRoot`,这一步**还会报错** — 这是 Task 6 的修复点。本 task 允许到这里还 build 不过,但 `CreateEnvDialogViewModel` 自身和 `EnvironmentListViewModel` 自身编译必须通过)。

- [ ] **Step 6: Run `CreateEnvDialogViewModelTests` to verify**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 9 PASS / 0 FAIL(Task 3 的测试不受本 task 影响,因为 `Show()` 静态方法和 VM ctor 测试独立)。

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs
git commit -m "feat(wpf): thread projectRoot into CreateEnvDialog.Show + EnvListVM"
```

---

### Task 6: `MainViewModel` ctor 多接 `projectRoot` 一参 + 串到 `EnvironmentListViewModel`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:43-58`(ctor 多接一参)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:83-90`(`ShowEnvironments` 把 projectRoot 传给 EnvironmentListViewModel)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:78-82`(MainViewModel ctor 多传 `projectRoot`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(加 `_projectRoot` 字段)

**Interfaces:**
- Consumes: Task 5 的 `EnvironmentListViewModel` 多接的 `projectRoot`
- Produces: `MainViewModel(..., string projectRoot)`,`ShowEnvironments()` 把 `_projectRoot` 传下去

- [ ] **Step 1: Add `_projectRoot` field to MainViewModel**

Edit `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — 在 line 26 (`private readonly string _appDataDir;`) 之后插入:

```csharp
    private readonly string _projectRoot;
```

- [ ] **Step 2: Update MainViewModel ctor signature**

Edit `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` line 57-58 — 在 `string appDataDir` 参数后(line 58 之后)插入新参数:

```csharp
        PyTorchVersionDirectory pytorchVersionDirectory,
        string appDataDir,
        string projectRoot)
```

完整 line 43-59 ctor:

```csharp
    public MainViewModel(
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator,
        SettingsRepository settingsRepo,
        GitProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogRefreshService catalogRefreshService,
        CatalogCacheStore catalogCacheStore,
        BaseEnvInstaller baseEnvInstaller,
        BaseEnvProfileLoader profileLoader,
        PyTorchVersionDirectory pytorchVersionDirectory,
        string appDataDir,
        string projectRoot)
```

- [ ] **Step 3: Assign `_projectRoot` in ctor body**

Edit `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` line 73-74(`_appDataDir = appDataDir;`) 之后插入:

```csharp
        _projectRoot = projectRoot;
```

- [ ] **Step 4: Pass `_projectRoot` to `EnvironmentListViewModel`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` line 88:

```csharp
            DataContext = new EnvironmentListViewModel(envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader, _projectRoot),
```

- [ ] **Step 5: Update `App.xaml.cs` MainViewModel construction**

Edit `src-wpf/ComfyUI.Manager/App.xaml.cs` line 78-82:

```csharp
        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller,
            profileLoader, BuildPyTorchVersionDirectory(appDataDir, http), appDataDir, projectRoot);
```

(`projectRoot` 已在 line 21-22 局部变量。)

- [ ] **Step 6: Build to verify full compilation**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings。

- [ ] **Step 7: Run full WPF test suite**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 282 PASS / 1 SKIP / 0 FAIL(基线 273 + Task 1 SettingsTests 3 + Task 3 CreateEnvDialogViewModelTests 9 - `EnvironmentListViewModelTests` 中如果原本不止 1 个测试可能略多,但净 +9 总)。

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "feat(wpf): thread projectRoot from App through MainViewModel to EnvList"
```

---

### Task 7: 全量 verify + bump v0.6.5.4 + release notes + ledger

**Files:**
- Modify: `pyproject.toml`: `version = "0.6.5.3"` → `"0.6.5.4"`
- Modify: `src/comfy_mgr/__init__.py`: `__version__ = "0.6.5.3"` → `"0.6.5.4"`
- Modify: `shared/errors.json`: `"_version": "0.6.5.3"` → `"0.6.5.4"`
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`: `<Version>0.6.5.3</Version>` → `0.6.5.4`
- Modify: `tests/test_version_consistency.py`: 3 处字面量 `0.6.5.3` → `0.6.5.4`
- Create: `release/RELEASE-NOTES-v0.6.5.4.md`

**Interfaces:**
- Consumes: Task 1-6 全部完成 + 测试基线 282/1/0 + Release build 0 errors
- Produces: verified v0.6.5.4 release-ready;**未**自动 push / tag / gh release

- [ ] **Step 1: Bump 5 处版本字面量 `0.6.5.3` → `0.6.5.4`**

(a) `pyproject.toml` line 3:`version = "0.6.5.4"`

(b) `src/comfy_mgr/__init__.py` line 1:`__version__ = "0.6.5.4"`

(c) `shared/errors.json` line 2:`"_version": "0.6.5.4",`

(d) `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` line 11:`<Version>0.6.5.4</Version>`

(e) `tests/test_version_consistency.py` 3 处(`comfy_mgr.__version__` / `data["_version"]` / `m.group(1)`):

```python
assert comfy_mgr.__version__ == "0.6.5.4"
...
assert data["_version"] == "0.6.5.4"
...
assert m.group(1) == "0.6.5.4"
```

- [ ] **Step 2: Run pytest version consistency**

```bash
cd "D:/ToolDevelop/ComfyUI" && PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q
```

Expected: 3 PASS。

- [ ] **Step 3: Run full WPF test suite**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 282 PASS / 1 SKIP / 0 FAIL。Record 实际数字到 ledger。

- [ ] **Step 4: Run WPF Release build**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: 0 warnings / 0 errors。

- [ ] **Step 5: Write release notes**

Create `release/RELEASE-NOTES-v0.6.5.4.md`(中文,follow v0.6.5.3 模板风格):

```markdown
## v0.6.5.4 — 新建环境:自动从设置带出 Python 解释器 + ComfyUI 模板

新建环境 dialog 之前 Python 解释器 / ComfyUI 源两个字段总是空的,即使 settings
里 `TemplatePythonDir` / `TemplateComfyuiDir` 早就配好了。v0.6.5.4 让 dialog
自动从 settings 拉常用模板路径,避免每次手填。

---

### 1) 新增功能

- **dialog 初次打开 auto-fill**:`PythonExe` / `ComfyuiSource` 自动从 settings
  拉,shared + independent 两个布局都生效。
- **"应用模板" 按钮**:Python / ComfyUI 两个字段行各加一个,用户改了
  settings 后可手动重新拉,不会自动覆盖用户已改的字段。
- **顶部黄色提示**:模板缺失(`PythonExe` 或 `ComfyuiSource` 不存在)时,dialog
  顶部显示"Python 模板 X.Y 未安装,请先在设置页下载"等提示。
- **`Settings.DefaultPythonVersion` 新字段**(默认 `"3.10"`):Settings 页
  加一行 ComboBox(可手填),auto-fill 时用来定位
  `<TemplatePythonDir>/<DefaultPythonVersion>/python.exe` 具体子目录。
- **`Layout` ComboBox 切换不重新 auto-fill**:只 dialog open + Apply 按钮
  触发,避免覆盖用户已手填的字段(决策 2)。

### 2) 数据流

```
User clicks "新建环境" in EnvListView
  ↓
MainViewModel.ShowEnvironmentsCommand
  ↓
CreateEnvDialog.Show(creator, settings, projectRoot)
  ↓
CreateEnvDialogViewModel ctor → ApplyTemplate()
  ↓
Fills PythonExe + ComfyuiSource + (optional) TemplateWarningMessage
  ↓
Dialog shown — user can edit or click "应用模板" to refetch
  ↓
User clicks "创建" → EnvCreatorService.CreateAsync (unchanged)
```

### 3) 升级注意

- **直接覆盖 v0.6.5.3 文件即可**。
- 老 `settings.json` 没 `default_python_version` 字段也兼容 — 反序列化时
  fallback 到 `"3.10"`。
- 不破坏现有手填 UX(用户改了字段不会被自动覆盖)。

### 4) Verification

- **dotnet test:** 282 PASS + 1 SKIP / 0 FAIL(基线 v0.6.5.3 = 273 +
  SettingsTests 3 + CreateEnvDialogViewModelTests 9 - 全量替换的旧测试)
- **pytest version consistency:** 3 PASS(v0.6.5.3 → v0.6.5.4)
- **dotnet build Release:** 0 warnings / 0 errors
- **手动 GUI smoke (用户桌面):** 启动 → 环境 → 新建 → 验证 PythonExe +
  ComfyuiSource 已 auto-fill;改 DefaultPythonVersion 到有子目录的版本,
  点"应用模板" 验证刷新;删 Python 模板子目录,重启,验证顶部黄色提示

---

### 5) Commits since v0.6.5.3(`82cd854`)

```
(将由本 task 自动生成 7 个 commit — 见 git log)
```

---

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.4` push + `gh release create` —
  等用户明确授权(沿用 v0.6.5.3 同模式)。
- **手动 GUI smoke (TBD):** 用户桌面验证(详见 §4)。

---

### Lessons learned(SDD)

- **YAGNI > 抽 service**:决策 6,本 feature 只 1 个 caller(VM.ApplyTemplate),
  不需要抽 `EnvTemplateAutoFillService`,直接放 VM 里测试覆盖更直接。
- **决策记录防 YAGNI drift**:第 6 条"YAGNI"明确写在 spec §8 + plan G12,
  防止后续 PR 评审时把 service 抽回来。
```

- [ ] **Step 6: Manual GUI smoke (per `feedback_no_zip.md` 走 staging)**

```bash
ls "release/staging/ComfyUI Manager/ComfyUI.Manager.exe" 2>&1 | head -1
```

确认 staging exe 存在(应是 v0.6.5.3 的旧版本 — **本 plan 不 rebuild release zip**,
仅手动跑现存的 exe 验证 dialog 流程,**新功能只在 exe 重建后生效**)。

注:per `feedback_no_zip.md`,rebuild zip 在 release commit 之后,用户授权后才跑。
本 task **不**包含 zip rebuild / push / tag / gh release。

- [ ] **Step 7: Update SDD ledger**

Update `.superpowers/sdd/progress.md`(或新开 `2026-08-01-env-create-autofill/progress.md`):

```
Task 1 (Settings.DefaultPythonVersion): complete (commit <sha>, 3 new tests)
Task 2 (SettingsView + VM passthrough): complete (commit <sha>)
Task 3 (CreateEnvDialogViewModel.ApplyTemplate + warnings): complete (commit <sha>, 9 new tests)
Task 4 (CreateEnvDialog.xaml top warning + apply buttons): complete (commit <sha>)
Task 5 (Show() signature + EnvListVM ctor): complete (commit <sha>)
Task 6 (MainViewModel + App.xaml.cs threading): complete (commit <sha>)
Task 7 (close-out + version bump + release notes): complete (commit <sha>)
```

(ledger 文件在 `.superpowers/sdd/` 是 gitignored scratch,不需要 commit。)

- [ ] **Step 8: Commit release notes + version bumps**

```bash
git add release/RELEASE-NOTES-v0.6.5.4.md pyproject.toml src/comfy_mgr/__init__.py shared/errors.json src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj tests/test_version_consistency.py
git commit -m "chore(release): bump to v0.6.5.4 + release notes"
```

Expected: 6 files changed, 1 new file (release notes).

- [ ] **Step 9: Verify full state**

```bash
git log --oneline -10
git status --short
```

Expected: 8 commits on top of v0.6.5.3 (`82cd854`); working tree clean (除了可能 `.superpowers/sdd/` gitignored 改动)。

- [ ] **Step 10: Report release boundary**

向用户报告:
- 所有 T1-T7 commits + 测试 + build 数字
- 手动 GUI smoke 状态(staging exe 是 v0.6.5.3,**新功能需要 rebuild zip 后生效**)
- 询问单独授权是否:
  - `git push origin main`
  - rebuild `release/ComfyUI-Manager-v0.6.5.4-win-x64.zip`(265 MB 量级)
  - `git tag v0.6.5.4 && git push origin v0.6.5.4`
  - `gh release create v0.6.5.4 <zip> --notes-file release/RELEASE-NOTES-v0.6.5.4.md`
  - 验证 `gh release list` v0.6.5.4 是 Latest

(以上每项都影响外部状态,默认**不**自动执行,等用户明确授权 — 沿用 v0.6.5.3 模式。)

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 282 PASS + 1 SKIP / 0 FAIL(基线 273 + SettingsTests 3 + CreateEnvDialogViewModelTests 9 - 替换的旧测试 0 = 净 +12)
- Python: `PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q` → 3 PASS

### 端到端手动测试(用户 desktop)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`(注意:**新功能需要先 rebuild zip 才有 v0.6.5.4 的 exe**,rebuild 后再跑)
2. 侧边栏点 **环境** → **+ 新建环境**
3. 验证 dialog 顶部无黄色提示 + `PythonExe` / `ComfyuiSource` 已 auto-fill
4. 改 `PythonExe` → 点 **应用模板** → 验证被覆盖回 settings 路径
5. 切 `Layout` 到 `independent` → 验证 `PythonExe` / `ComfyuiSource` **不**变(决策 2)
6. 去 Settings 页改 `DefaultPythonVersion` 到 `3.11`(假设有 `3.11` 子目录) → 回新建 dialog 点 **应用模板** → 验证 `PythonExe` 跟到 `3.11/python.exe`
7. 删 `python/3.10/` 子目录 → 重启 WPF → 新建 dialog 顶部应有黄色提示"Python 模板 3.10 未安装"
8. 选 `independent` 布局 → 验证 `PythonExe` / `ComfyuiSource` 仍 auto-fill(决策 1)
9. 不真跑 pip 安装,只验 UI 流

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| 老 `settings.json` 没 `default_python_version` 字段 | JSON 反序列化时 C# 字符串属性 fallback 到 `= "3.10"` 默认值(SettingsTests 已覆盖) |
| 用户改了 `DefaultPythonVersion` 到不存在的版本号(如 `"3.99"`) | `File.Exists(pythonExe)` 返回 false → `PythonExe = ""` + 黄色提示(决策 5) |
| `projectRoot` 含空格 / 中文 | `Path.Combine` 正确处理(WPF 已在 `App.xaml.cs:21` 用 `TrimEnd('\\')`) |
| 现有 `EnvironmentListViewModelTests` 多处 ctor 调用 | Task 5 Step 4 已 patch 加 `null!` 参数 |
| staging exe 是 v0.6.5.3 旧版,新功能看不到 | T7 Step 6 已注明:**新功能 rebuild zip 后生效**,smoke 需要重建后跑 |
| YAGNI 风险:有人想抽 `EnvTemplateAutoFillService` | spec §8 + plan G12 明确"YAGNI",防止 PR 评审时过度抽象 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/Settings.cs`(加 `DefaultPythonVersion`)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(加一行 ComboBox)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(passthrough)
- `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(ctor + ApplyTemplate)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`(顶部警告 + 2 个按钮)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`(`Show()` 签名)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(多接 projectRoot)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(多接 projectRoot)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(传 projectRoot 到 MainViewModel)
- 5 处版本字面量 + `release/RELEASE-NOTES-v0.6.5.4.md`(new)
- 2 个 test 文件(`SettingsTests.cs` new + `CreateEnvDialogViewModelTests.cs` new)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 7 task + 1 close-out = 8 dispatches(实际 T1-T7 + 不需要单独 review by opus at end,因为 patch 很窄 — 每个 task 自带 reviewer per SDD 模式)
- Per-task review gate(sonnet implementer + sonnet reviewer)
- Mechanical task(T1 Settings POCO,T2 VM passthrough,T4 XAML,T5 串签名,T6 串 ctor,T7 close-out)→ Haiku
- Integration / design task(T3 ApplyTemplate 实现,因为是 spec §2.3 核心代码)— Sonnet

Estimated 7-8 commits on main on top of v0.6.5.3 `82cd854`.