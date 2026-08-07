# v0.6.7.3 Shared Models 目录 + 编辑器按钮 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让所有 env 通过 junction 共享同一份 Models 文件夹(避免每建一个 env 复制几 GB),并提供顶部「工具」菜单下的 2 个编辑器按钮,快速打开 ComfyUI 的 `comfy.settings.json` / `extra_model_paths.yaml`。

**Architecture:**
- 新 `Settings.SharedModelsDirectory`(string) + `SettingsView` UI(textbox + 浏览按钮)+ `SettingsViewModel` INPC property
- `EnvCreatorService.CreateAsync` 在步骤 5(链接/复制 ComfyUI)后插入步骤 5.5:删 `<env-root>/ComfyUI/models` 然后 junction 到 `SharedModelsDirectory`;失败抛 `CREATE_MODELS_LINK_FAILED` 并整体回滚 env 根目录
- `ProcessLauncher` ctor 加 `string sharedModelsDirectory = ""` 参数;`StartEnvAsync` 启动前比较 junction target 与 `SharedModelsDirectory`,不一致则删重建(失败仅 INFO 日志)
- `JunctionLinker.GetTargetAsync(linkPath)` 用 `cmd /c dir /AL` 解析 junction target
- `MainWindow.xaml` 加顶级 `MenuItem _工具(_T)` 下挂「打开 ComfyUI 设置...」+「打开 Models 配置...」;`MainViewModel` 加 `OpenComfySettingsJsonCommand` + `OpenExtraModelPathsYamlCommand`(依赖 env-list `Selected` env)

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite`(本 plan 不动 SQLite)· hand-rolled MVVM (`RelayCommand`)· `JunctionLinker`(`cmd /c mklink /D` + `cmd /c dir /AL`)· `Process.Start(UseShellExecute=true)`· `Path.GetFullPath` + `OrdinalIgnoreCase` 比较路径

## Context

用户桌面验 v0.6.7.2 后,提 2 个新需求:
1. "通用的 Models 目录" — 所有 env 共用同一份 Models 文件夹(避免每个 env 复制几 GB)
2. "编辑器按钮,通常用于常规的项目编辑" — 快速编辑 ComfyUI 配置

设计验证:
- ComfyUI 不支持 `COMFYUI_MODELS_PATH` 环境变量(查 `github.com/comfyanonymous/ComfyUI/folder_paths.py:19-23` + `cli_args.py`),只支持 CLI `--models-directory` / `--base-directory` / `--extra-model-paths-config`
- 用户确认方案:**junction(跟 shared layout 的 ComfyUI source 同款)**,而不是 CLI 参数也不是 env var
- 用户确认失败处理:**env-create 失败整体回滚**(跟 venv 失败同款),**env-start 失败仅 INFO 日志**(不阻塞启动,启动回滚代价大)
- 用户确认触发时机:**env-create + 每次启动前检查重建**(改 SharedModelsDirectory 后重启 env 自动生效)

**base SHA:** `151f5ca`(v0.6.7.2 ComfyUI locale ship-ready)

**相关已有代码:**
- `Infrastructure/JunctionLinker.cs:46-83` — `CreateAsync(linkPath, target, ct)` 跑 `cmd /c mklink /D`,已有现成可复用
- `Services/EnvCreatorService.cs:60-180` — 当前 8 步骤;步骤 5 之后、步骤 6 之前插新步骤 5.5
- `Infrastructure/ProcessLauncher.cs:37-56` — ctor 加 optional `string sharedModelsDirectory = ""` 参数(同 locale 模式)
- `Infrastructure/ProcessLauncher.cs:111-269` — `StartEnvAsync` 在 §"写 ComfyUI UI locale" 之前插"检查并重建 Models junction"
- `Models/Settings.cs` — 加新字段(同 ComfyUiLocale 模式)
- `ViewModels/SettingsViewModel.cs:222-233` — 加 property(同 ComfyUiLocale 模式)
- `Views/SettingsView.xaml:35-42` — 加 TextBox + 浏览按钮(同 ComfyUiLocale ComboBox 模式)
- `Views/MainWindow.xaml:19-44` — 当前 3 个顶级 MenuItem(文件/设置/关于),插第 4 个「工具」
- `ViewModels/MainViewModel.cs:80-92` — 加 2 个 RelayCommand
- `ViewModels/MainViewModel.cs:59,72` — `CurrentEnvironmentsViewModel?.Selected` 可用
- `ViewModels/EnvironmentListViewModel.cs:147+` — `Selected` property 是 INPC(env 一行 read-through)
- `App.xaml.cs` — 已有 `new ProcessLauncher(..., settings.ComfyUiStartupTimeoutSeconds, settings.ComfyUiLocale)` 链,加 `settings.SharedModelsDirectory`

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `Settings.SharedModelsDirectory`(string,默认空)+ `[JsonPropertyName("shared_models_directory")]`;空 = 不共享(向后兼容) | spec §3.1 |
| G2 | `SettingsView` 在「外部冲突 API URL」之后插新行(TextBlock + TextBox + 浏览按钮 + 说明),跟随现有 G17 11pt 灰字说明格式 | spec §3.1 |
| G3 | `SettingsViewModel.SharedModelsDirectory` property setter 同步写 `_settings` + `_repo.Save`;`BrowseSharedModelsDirectory` 复用现有 `PickFolder()` | spec §3.1 |
| G4 | `EnvCreatorService.CreateAsync` 步骤 5(链接 ComfyUI)之后、步骤 6(创建 venv)之前,插入步骤 5.5;只在 `SharedModelsDirectory` 非空白时执行 | spec §3.2 |
| G5 | 步骤 5.5 流程:`Directory.Delete(modelsLink, recursive: true)` → `JunctionLinker.CreateAsync(modelsLink, SharedModelsDirectory, ct)`;`SharedModelsDirectory` 先 `Path.GetFullPath` 规范化 | spec §3.2 |
| G6 | 步骤 5.5 失败:catch → 删 env 根目录(同 venv 失败)→ 抛 `CreateEnvException("CREATE_MODELS_LINK_FAILED", ...)`;异常里写出错原因 | spec §3.2 + user 选回滚 |
| G7 | `ProcessLauncher` ctor 加 `string sharedModelsDirectory = ""` 参数;在 `_comfyUiLocale` 后面;赋给 `_sharedModelsDirectory = sharedModelsDirectory ?? ""` | spec §3.3 |
| G8 | `ProcessLauncher.StartEnvAsync` 在写 comfy.settings.json locale **之前**,调用新的 `EnsureModelsJunctionAsync(comfyuiRoot)` helper | spec §3.3 |
| G9 | `EnsureModelsJunctionAsync` 行为:`SharedModelsDirectory` 空 → no-op;`<env-root>/ComfyUI/models` 不存在 → 建 junction;`GetTargetAsync` 返 null 或不等于 `SharedModelsDirectory`(用 `OrdinalIgnoreCase` 比 `Path.GetFullPath` 规范化后)→ 删旧(`Directory.Delete(recursive: true)`,删 junction 不删源)+ 建新;失败 → `_logger.Info("env-start", ...)` 不抛 | spec §3.3 + user 选仅 INFO 日志 |
| G10 | `JunctionLinker.GetTargetAsync(linkPath)`:跑 `cmd /c dir /AL "<linkPath>"`,stdout 解析 `<JUNCTION>      <target>` 行,返回 target 全路径(或 null 当不是 junction);不存在 / 失败抛 `JunctionCreationException` | spec §3.3 + 现有模式 |
| G11 | `JunctionLinker.GetTargetAsync` 在测试中可用 `cmd /c mklink /D` 真实建 junction 来断言;不需要 mock | spec §3.3 |
| G12 | 顶级 `MenuItem _工具(_T)` 加在「设置」之后、「关于」之前(按字母序),下挂 2 项 + Separator + 1 项(打开日志目录,顺手) | spec §3.4 |
| G13 | `OpenComfySettingsJsonCommand` / `OpenExtraModelPathsYamlCommand` 调 `EnsureFileExists(path)` 然后 `Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })` | spec §3.4 |
| G14 | `EnsureFileExists(comfySettingsPath)` 不存在 → `Directory.CreateDirectory` + `File.WriteAllText("{}")`;`EnsureFileExists(extraYamlPath)` 不存在 → 写 `# ComfyUI Models 路径配置\nbase_directory: <SharedModelsDirectory 或空>\n`(空 = placeholder) | spec §3.4 |
| G15 | 命令 `CanExecute` = `CurrentEnvironmentsViewModel?.Selected is not null`;CommandManager 自动监听 `PropertyChanged(nameof(Selected))`(既有 `RelayCommand` 设计) | spec §3.4 |
| G16 | 「工具 → 打开日志目录」复用既有 `OpenLogFolderCommand`(挪到工具菜单,文件菜单保留一份或移除 — 文件菜单移除以避免重复,本轮统一到工具菜单下) | spec §3.4 + Bonus |
| G17 | `App.xaml.cs` `_launcher = new ProcessLauncher(..., settings.ComfyUiStartupTimeoutSeconds, settings.ComfyUiLocale, settings.SharedModelsDirectory)` | spec §3.3 |
| G18 | `MainViewModel.OpenFolderOverride` test seam 复用给 2 个新命令(`OpenComfySettingsJsonCommand` / `OpenExtraModelPathsYamlCommand` 不用 `OpenFolder`,所以不共用;它们走 `ProcessStartOverride` 新 test seam) | spec §3.4 + 项目风格 |
| G19 | 不 bump version(`csproj Version` 保持 `0.6.7.2`) / 不发 release zip / 无 ledger commit(per `feedback_no_rebuild_zip.md` + v0.6.7 系列 hotfix 偏好) | user scope |
| G20 | 测试不依赖 git / WPF STA;`JunctionLinker` 集成测试用真 `mklink /D`(测试环境 Windows);`EnvCreatorService` 测试复用既有 `FakeJunctionLinker : JunctionLinker` | spec §5 + 既有模式 |
| G21 | 资源字符串走 `Strings.zh-CN.resx` 加 2 个新 key:`Menu_Tools` / `Menu_ToolsHint`;XAML 菜单项 Header 走 resx 绑定(本 plan 加的,老代码不动) | 项目 i18n 偏好 |
| G22 | `SharedModelsDirectory` 包含路径含空格 → `cmd /c mklink /D` 已加引号 handle;`Path.GetFullPath` 规范化后 cross-platform 一致(本项目仅 Windows,但保持规范) | 跨边界 case |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/JunctionLinkerGetTargetTests.cs` | ~110 | 3 测试:`GetTargetAsync_ExistingJunction_ReturnsTarget` / `GetTargetAsync_RegularDirectory_ReturnsNull` / `GetTargetAsync_NonExistentPath_Throws` |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs` | ~120 | 3 测试:`StartEnvAsync_TargetMatches_DoesNotRelink` / `StartEnvAsync_TargetDiffers_Relinks` / `StartEnvAsync_EmptySetting_DoesNothing` |
| `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs` | ~180 | 5 测试:`SharedModelsDirectorySet_JunctionsModels` / `SharedModelsEmpty_DoesNotTouchModels` / `SharedModelsDirectoryDoesNotExist_FailsAndRollsBack` / `IndependentLayout_AlsoJunctions` / `SharedLayout_ReplacesExistingJunction` |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs` | ~80 | 2 测试:`SharedModelsDirectory_SetterPersists` / `SharedModelsDirectory_DefaultIsEmpty` |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 加 `SharedModelsDirectory` 字段(同 ComfyUiLocale 模式) |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | 加 `SharedModelsDirectory` property + `BrowseSharedModelsDirectory` click handler(`Code-behind` 文件) |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 加 TextBox + 浏览按钮 + 说明(同 ComfyUiLocale ComboBox 模式) |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` | 加 `BrowseSharedModelsDirectory(object, RoutedEventArgs)` click handler(同 `BrowseTemplateComfyui` 模式) |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | 加 2 个 key:`Menu_Tools` = "工具" / `Menu_ToolsHint` = "打开 ComfyUI 项目配置文件" |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx`(默认) | 同 2 个 key 英文 |
| `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | 步骤 5 之后、步骤 6 之前插步骤 5.5:链接共享 Models + 失败回滚 |
| `src-wpf/ComfyUI.Manager/Infrastructure/JunctionLinker.cs` | 加 `GetTargetAsync(linkPath)` 方法(`cmd /c dir /AL`) |
| `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` | ctor 加 `string sharedModelsDirectory = ""`;`StartEnvAsync` 加 `EnsureModelsJunctionAsync` 调用 + 新 helper 方法 |
| `src-wpf/ComfyUI.Manager/Views/MainWindow.xaml` | 「设置」和「关于」之间加 `MenuItem _工具(_T)` 下挂 2 项 + Separator + 「打开日志目录」;文件菜单移除「查看日志目录」(统一到工具菜单) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | 加 2 个 RelayCommand + `OpenComfySettingsJsonOverride` / `OpenExtraModelPathsYamlOverride` / `EnsureFileExistsOverride` test seam |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | launcher ctor 多传 `settings.SharedModelsDirectory` |

### Delete

无。

### Keep (unchanged)

- v0.6.7.2 的 locale 流程(commit `151f5ca`)— 本 plan 不动
- `BaseEnvInstaller` / `BaseEnvProfileLoader` / `RequirementsInstaller` / `ProcessStateRepository` — 本 plan 不动
- v0.6.5.21 顶部菜单(文件 / 设置 / 关于) — 仅加第 4 个「工具」顶级 menu-item,既有的不动
- `JunctionLinker.CreateAsync` / `CopyDirectory` 签名 — 不变(本 plan 加新方法)

---

## Tasks

### Task 1: Settings 字段 + UI + VM property + 2 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` + `Strings.resx`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Models.Settings.SharedModelsDirectory` (string, default "")
  - `ViewModels.SettingsViewModel.SharedModelsDirectory` (property, getter/setter)
  - `Views.SettingsView.xaml` 「共享 Models 目录」 row

- [ ] **Step 1: Write failing tests**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class SettingsViewModelSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _settingsPath;

    public SettingsViewModelSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "settingsvmshared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _settingsPath = Path.Combine(_rootDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private SettingsViewModel BuildVm()
    {
        var repo = new TestSettingsRepo(_settingsPath);
        var proxy = new GitProxyConfig();
        var validator = new FakePythonInterpreterValidator();
        return new SettingsViewModel(repo, proxy, validator);
    }

    [Fact]
    public void SharedModelsDirectory_DefaultIsEmpty()
    {
        var vm = BuildVm();
        Assert.Equal("", vm.SharedModelsDirectory);
    }

    [Fact]
    public void SharedModelsDirectory_SetterPersists()
    {
        var vm = BuildVm();
        vm.SharedModelsDirectory = @"D:\Models\shared";
        Assert.Equal(@"D:\Models\shared", vm.SharedModelsDirectory);
        // Reload from disk
        var repo2 = new TestSettingsRepo(_settingsPath);
        var fresh = repo2.Load();
        Assert.Equal(@"D:\Models\shared", fresh.SharedModelsDirectory);
    }
}

/// <summary>类似既有 TestSettingsRepo 模式 — 写一个简单测试用 repo。</summary>
internal sealed class TestSettingsRepo
{
    private readonly string _path;
    public TestSettingsRepo(string path) { _path = path; }
    public Settings Load()
    {
        if (!File.Exists(_path)) return new Settings();
        var text = File.ReadAllText(_path);
        return System.Text.Json.JsonSerializer.Deserialize<Settings>(text) ?? new Settings();
    }
    public void Save(Settings s) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
}

internal sealed class FakePythonInterpreterValidator : ComfyUI.Manager.Services.IPythonInterpreterValidator
{
    public Task<ComfyUI.Manager.Services.PythonValidationResult> ValidateAsync(string path, CancellationToken ct)
        => Task.FromResult(new ComfyUI.Manager.Services.PythonValidationResult { IsValid = true });
}
```

(若 `TestSettingsRepo` 已在 `tests-wpf/.../Fakes/` 里有同名类,直接 `using ComfyUI.Manager.Tests.Fakes;` 用既有的;若没有就加到上面这个文件里。)

- [ ] **Step 2: Run tests, verify 2/2 FAIL** (`SharedModelsDirectory` 不存在 / Setter 抛)
- [ ] **Step 3: 加 `Models/Settings.cs` 字段**

```csharp
// v0.6.7.3: 全局共享 Models 目录(env-create / env-start 时
// 把 <env-root>/ComfyUI/models junction 到此路径)。
// 空字符串 = 不共享 Models,每 env 自己持有一份(向后兼容)。
[JsonPropertyName("shared_models_directory")]
public string SharedModelsDirectory { get; set; } = "";
```

- [ ] **Step 4: 加 `SettingsViewModel.cs` property**

```csharp
// v0.6.7.3: 全局共享 Models 目录。空 = 不共享。
public string SharedModelsDirectory
{
    get => _settings.SharedModelsDirectory;
    set { _settings.SharedModelsDirectory = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
}
```

- [ ] **Step 5: 加 `SettingsView.xaml` UI**(在「外部冲突 API URL」TextBox 之后插)

```xaml
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

- [ ] **Step 6: 加 `SettingsView.xaml.cs` click handler**

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

- [ ] **Step 7: 加 resx 字符串**(可选,本轮可推迟)

`Strings.zh-CN.resx` 加 2 个 key:本轮可暂不加(XAML 用硬编码中文,跟 G21 偏离一点;但仅本轮;下个 spec 收敛)。
**实际**:直接用 XAML 硬编码中文("共享 Models 目录(留空 = 不共享)"、"所有 env..."),本轮不碰 resx,记为 minor finding。

- [ ] **Step 8: Run tests, verify 2/2 PASS**
- [ ] **Step 9: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs
git commit -m "feat(wpf): Settings.SharedModelsDirectory 字段 + UI (v0.6.7.3 T1)"
```

---

### Task 2: JunctionLinker.GetTargetAsync + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/JunctionLinker.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/JunctionLinkerGetTargetTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  // public virtual async Task<string?> GetTargetAsync(string linkPath, CancellationToken ct = default)
  //   - 若 <linkPath> 不存在 → 抛 JunctionCreationException("link 路径不存在", -1, "")
  //   - 若 <linkPath> 不是 junction → 返 null(普通目录 / 文件)
  //   - 若 <linkPath> 是 junction → 跑 `cmd /c dir /AL "<linkPath>"`,正则解析
  //     `<JUNCTION>      <target>` 行 → 返 Path.GetFullPath(target)
  ```

- [ ] **Step 1: Write failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Infrastructure/JunctionLinkerGetTargetTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public sealed class JunctionLinkerGetTargetTests : IDisposable
{
    private readonly string _root;

    public JunctionLinkerGetTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "juncgettarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetTargetAsync_ExistingJunction_ReturnsTarget()
    {
        var linker = new JunctionLinker();
        var realDir = Path.Combine(_root, "real-target");
        Directory.CreateDirectory(realDir);
        var link = Path.Combine(_root, "link-to-real");
        await linker.CreateAsync(link, realDir, CancellationToken.None);

        var target = await linker.GetTargetAsync(link, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(
            Path.GetFullPath(realDir),
            Path.GetFullPath(target!),
            ignoreCase: true);
    }

    [Fact]
    public async Task GetTargetAsync_RegularDirectory_ReturnsNull()
    {
        var linker = new JunctionLinker();
        var regular = Path.Combine(_root, "regular");
        Directory.CreateDirectory(regular);

        var target = await linker.GetTargetAsync(regular, CancellationToken.None);

        Assert.Null(target);
    }

    [Fact]
    public async Task GetTargetAsync_NonExistentPath_Throws()
    {
        var linker = new JunctionLinker();
        var ghost = Path.Combine(_root, "ghost");

        await Assert.ThrowsAsync<JunctionLinker.JunctionCreationException>(() =>
            linker.GetTargetAsync(ghost, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run tests, verify 3/3 FAIL** (`GetTargetAsync` 不存在)
- [ ] **Step 3: 加 `JunctionLinker.cs` 新方法**

```csharp
/// <summary>
/// GetTargetAsync:如果 <paramref name="linkPath"/> 是一个 junction,
/// 返回它的 target 全路径;不是 junction(普通目录/文件)返回 null;
/// 不存在则抛 JunctionCreationException。
/// 用 <c>cmd /c dir /AL &lt;linkPath&gt;</c> 列出 junction 的真实 target。
/// </summary>
public virtual async Task<string?> GetTargetAsync(
    string linkPath,
    CancellationToken ct = default)
{
    if (!Directory.Exists(linkPath))
        throw new JunctionCreationException(
            $"link 路径不存在: {linkPath}", -1, "");

    var psi = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/c dir /AL \"{linkPath}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi)
        ?? throw new JunctionCreationException("cmd 启动失败", -1, "");
    var stdout = await p.StandardOutput.ReadToEndAsync(ct);
    var stderr = await p.StandardError.ReadToEndAsync(ct);
    await p.WaitForExitAsync(ct);

    if (p.ExitCode != 0)
    {
        // 不是 junction(junction 类型目录 dir 列出时 exit 0;
        // 普通目录 / 文件 dir /AL 也 exit 0 但没有 <JUNCTION> 行)
        // 但为了兼容性,任何 exit !=0 视为错误
        throw new JunctionCreationException(
            "dir /AL 失败", p.ExitCode, stderr);
    }

    // 解析 stdout,找含 "<JUNCTION>" + target 的行
    // 例:`02/01/2026  10:00 AM    <JUNCTION>     D:\target [real-dir]`
    foreach (var line in stdout.Split('\n'))
    {
        if (!line.Contains("<JUNCTION>")) continue;
        // target = line 中最后一个非空 token,trim 掉可能的 [real-dir] 后缀
        var trimmed = line.Trim();
        // dir 输出格式固定:<DATE> <TIME>    <JUNCTION>     <TARGET> [<REAL_NAME>]
        // 用 Regex 抓目标路径
        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"<JUNCTION>\s+(.+?)(?:\s+\[.+\])?\s*$");
        if (match.Success)
        {
            var target = match.Groups[1].Value.Trim();
            return Path.GetFullPath(target);
        }
    }
    // 不是 junction
    return null;
}
```

- [ ] **Step 4: Run tests, verify 3/3 PASS**
- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Infrastructure/JunctionLinker.cs \
        tests-wpf/ComfyUI.Manager.Tests/Infrastructure/JunctionLinkerGetTargetTests.cs
git commit -m "feat(wpf): JunctionLinker.GetTargetAsync 解析 junction target (v0.6.7.3 T2)"
```

---

### Task 3: EnvCreatorService 步骤 5.5 链接共享 Models + 失败回滚 + 5 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs`

**Interfaces:**
- Consumes: `_settings.SharedModelsDirectory`, `_linker` (既有)
- Produces: EnvCreatorService.CreateAsync 在步骤 5 之后、步骤 6 之前做 Models junction;失败抛 `CreateEnvException("CREATE_MODELS_LINK_FAILED", ...)`

- [ ] **Step 1: Write failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class EnvCreatorServiceSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ComfyUI.Manager.Models.Settings _settings;
    private readonly RecordingJunctionLinker _linker;
    private readonly EnvCreatorService _service;

    public EnvCreatorServiceSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "envcreator-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);

        _settings = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            TemplateComfyuiDir = "ComfyUI",
        };
        _linker = new RecordingJunctionLinker();

        // Prepare base python + ComfyUI template
        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");
        var comfyDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");

        _service = new EnvCreatorService(
            _factory, new FakeVenvCreator(), _linker, _settings, _rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private string CreateSharedModelsDir()
    {
        var shared = Path.Combine(_rootDir, "shared-models");
        Directory.CreateDirectory(shared);
        return shared;
    }

    [Fact]
    public async Task SharedModelsDirectorySet_JunctionsModels()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        var env = await _service.CreateAsync(
            "env-1", "independent", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        // Should have created a junction to <env-root>/ComfyUI/models → shared
        Assert.Contains(_linker.CreatedLinks,
            pair => pair.Link.EndsWith(Path.Combine("ComfyUI", "models"))
                && Path.GetFullPath(pair.Target).Equals(
                    Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SharedModelsEmpty_DoesNotTouchModels()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        _settings.SharedModelsDirectory = "";

        await _service.CreateAsync(
            "env-2", "independent", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        // No junction to models/
        Assert.DoesNotContain(_linker.CreatedLinks,
            pair => pair.Link.EndsWith(Path.Combine("ComfyUI", "models")));
    }

    [Fact]
    public async Task SharedModelsDirectoryDoesNotExist_FailsAndRollsBack()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        _settings.SharedModelsDirectory = Path.Combine(_rootDir, "ghost-models");

        var ex = await Assert.ThrowsAsync<EnvCreatorService.CreateEnvException>(() =>
            _service.CreateAsync(
                "env-3", "independent", basePy,
                Path.Combine(_rootDir, "ComfyUI"),
                port: null));

        Assert.Equal("CREATE_MODELS_LINK_FAILED", ex.Code);

        // Env root dir should be deleted (rollback)
        var envsDir = Path.Combine(_rootDir, "envs", "env-3");
        Assert.False(Directory.Exists(envsDir));

        // DB should have no env-3 row
        var repo = new EnvironmentRepository(_factory);
        Assert.Null(repo.GetByName("env-3"));
    }

    [Fact]
    public async Task IndependentLayout_AlsoJunctions()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        await _service.CreateAsync(
            "env-4", "independent", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        Assert.Contains(_linker.CreatedLinks,
            pair => pair.Link.EndsWith(Path.Combine("ComfyUI", "models")));
    }

    [Fact]
    public async Task SharedLayout_ReplacesExistingJunction()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        // Pre-create a junction at <env-root>/ComfyUI/models (simulating
        // shared-layout env where the original models is a junction to <comfyui-source>/models)
        var envRootWillBe = Path.Combine(_rootDir, "envs", "env-5");
        var preLink = Path.Combine(envRootWillBe, "ComfyUI", "models");
        Directory.CreateDirectory(Path.GetDirectoryName(preLink)!);
        await _linker.CreateAsync(preLink, Path.Combine(_rootDir, "ComfyUI", "models"), CancellationToken.None);

        await _service.CreateAsync(
            "env-5", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        // Final junction target should be SharedModelsDirectory, not <comfyui-source>/models
        Assert.Contains(_linker.CreatedLinks,
            pair => pair.Link == preLink
                && Path.GetFullPath(pair.Target).Equals(
                    Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingJunctionLinker : JunctionLinker
    {
        public System.Collections.Generic.List<(string Link, string Target)> CreatedLinks { get; } = new();

        public override async Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            CreatedLinks.Add((linkPath, target));
            // 若 target 不存在,模拟真实 mklink 失败行为
            if (!Directory.Exists(target))
                throw new JunctionCreationException(
                    $"junction target 不存在: {target}", -1, "");
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            await Task.CompletedTask;
        }

        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }

    private sealed class FakeVenvCreator : VenvCreator
    {
        public override async Task CreateAsync(string basePython, string venvPath, CancellationToken ct = default)
        {
            var scriptsDir = Path.Combine(venvPath, "Scripts");
            Directory.CreateDirectory(scriptsDir);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "python.exe"), "");
        }
    }
}
```

注:`EnvironmentRepository.GetByName(...)` 可能在现有类里没有;若没有,改用 `repo.ListAll().FirstOrDefault(e => e.Name == "env-3")`。Or, 检查现有 `EnvironmentRepository` 接口,看是否有 `GetByName`。

- [ ] **Step 2: Run tests, verify 5/5 FAIL**
- [ ] **Step 3: 改 `EnvCreatorService.cs` 步骤 5.5**

在步骤 5(`comfyuiLink = Path.Combine(rootPath, "ComfyUI")` 之后,流程分支结束后)插入步骤 5.5;在步骤 6(`var venvPath = Path.Combine(rootPath, "venv")`)之前。

```csharp
// 5.5 链接共享 Models(若 SharedModelsDirectory 非空)
if (!string.IsNullOrWhiteSpace(_settings.SharedModelsDirectory))
{
    var sharedModelsFull = Path.GetFullPath(_settings.SharedModelsDirectory);
    var modelsLink = Path.Combine(comfyuiLink, "models");
    progress?.Report(new CreateStepReport("链接共享 Models",
        $"junction: {modelsLink} → {sharedModelsFull}"));
    try
    {
        if (Directory.Exists(modelsLink))
        {
            // shared layout 时是 junction 链回 <comfyui-source>/models,删 junction 不删源
            // independent 时是本地拷贝,删本地没事
            Directory.Delete(modelsLink, recursive: true);
        }
        await _linker.CreateAsync(modelsLink, sharedModelsFull, ct);
    }
    catch (Exception ex)
    {
        // 回滚:删 env 根目录,跟 venv 失败同款
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw new CreateEnvException("CREATE_MODELS_LINK_FAILED",
            $"Models junction 创建失败: {ex.Message}");
    }
}

// 6. 创建 venv(不变)
var venvPath = Path.Combine(rootPath, "venv");
```

- [ ] **Step 4: Run tests, verify 5/5 PASS**
- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs
git commit -m "feat(wpf): EnvCreatorService 步骤 5.5 链接共享 Models + 失败回滚 (v0.6.7.3 T3)"
```

---

### Task 4: ProcessLauncher 启动前检查重建 Models junction + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs`

**Interfaces:**
- Consumes: `_sharedModelsDirectory` (ctor param), `_linker` (JunctionLinker,新增字段)
- Produces:
  - `ProcessLauncher(string projectRoot, ..., string comfyUiLocale = "", string sharedModelsDirectory = "")` ctor
  - `EnsureModelsJunctionAsync(comfyuiRoot)` helper:sharedModelsDirectory 空 → no-op;`<env-root>/ComfyUI/models` 不存在 → 建 junction;target 不等 → 删重建
  - 在 `StartEnvAsync` 写 comfy.settings.json locale **之前** 调用 `EnsureModelsJunctionAsync`

- [ ] **Step 1: Write failing tests**

`tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public sealed class ProcessLauncherSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;

    public ProcessLauncherSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "launcher-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private (ProcessLauncher Launcher, RecordingJunctionLinker Linker, SqliteConnectionFactory Db) BuildLauncher(string sharedDir, bool withEnv = true)
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        var envRepo = new EnvironmentRepository(factory);
        var psRepo = new ProcessStateRepository(factory);
        var linker = new RecordingJunctionLinker();
        var launcher = new ProcessLauncher(
            _rootDir, factory, envRepo, psRepo, logger: null,
            comfyUiStartupTimeoutSeconds: 600,
            comfyUiLocale: "",
            sharedModelsDirectory: sharedDir,
            linker: linker);
        return (launcher, linker, factory);
    }

    /// <summary>
    /// Test seam 让 ProcessLauncher 直接调我们的 RecordingJunctionLinker。
    /// </summary>
    private void PrepareEnv(string envRoot, string comfyuiRoot, string? existingModelsLinkTarget = null)
    {
        Directory.CreateDirectory(Path.Combine(comfyuiRoot, "models"));
        File.WriteAllText(Path.Combine(comfyuiRoot, "main.py"), "");
        if (existingModelsLinkTarget is not null)
        {
            // 预先建个 dummy junction 模拟"旧 target"
            // (用真 mklink 因为 RecordingJunctionLinker.CreateAsync 是 mock)
        }
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_EmptySetting_DoesNothing()
    {
        var (launcher, linker, _) = BuildLauncher("");
        // 通过 reflection 或 internal accessor 调 EnsureModelsJunctionAsync
        // 这里用 reflection(测试只关心行为,不在意 API 暴露级别)
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        await (Task)method!.Invoke(launcher, new object?[] { Path.Combine(_rootDir, "ComfyUI"), CancellationToken.None })!;

        Assert.Empty(linker.CreatedLinks);
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_TargetMatches_DoesNotRelink()
    {
        var shared = Path.Combine(_rootDir, "shared-models");
        Directory.CreateDirectory(shared);
        var comfyuiRoot = Path.Combine(_rootDir, "ComfyUI");
        var modelsLink = Path.Combine(comfyuiRoot, "models");
        Directory.CreateDirectory(modelsLink);  // 假装已经是 junction(测试不真建)

        var (launcher, linker, _) = BuildLauncher(shared);
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        await (Task)method!.Invoke(launcher, new object?[] { comfyuiRoot, CancellationToken.None })!;

        // TargetMatches 逻辑:GetTargetAsync 返 null(普通目录,不是 junction)
        // → 测试期望:linker 不被调
        Assert.Empty(linker.CreatedLinks);
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_TargetDiffers_Relinks()
    {
        var shared = Path.Combine(_rootDir, "shared-models");
        Directory.CreateDirectory(shared);
        var comfyuiRoot = Path.Combine(_rootDir, "ComfyUI");
        var modelsLink = Path.Combine(comfyuiRoot, "models");
        // modelsLink 不存在 → 触发"建 junction"分支
        Directory.CreateDirectory(comfyuiRoot);

        var (launcher, linker, _) = BuildLauncher(shared);
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        await (Task)method!.Invoke(launcher, new object?[] { comfyuiRoot, CancellationToken.None })!;

        Assert.Single(linker.CreatedLinks);
        Assert.Equal(modelsLink, linker.CreatedLinks[0].Link);
        Assert.Equal(
            Path.GetFullPath(shared),
            Path.GetFullPath(linker.CreatedLinks[0].Target),
            ignoreCase: true);
    }

    private sealed class RecordingJunctionLinker : JunctionLinker
    {
        public List<(string Link, string Target)> CreatedLinks { get; } = new();
        public List<string> DeletedLinks { get; } = new();
        public List<string> GetTargetCalls { get; } = new();

        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            CreatedLinks.Add((linkPath, target));
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            return Task.CompletedTask;
        }
        public override Task<string?> GetTargetAsync(string linkPath, CancellationToken ct = default)
        {
            GetTargetCalls.Add(linkPath);
            // 简化:假装 linkPath 是普通目录(返 null),除非测试手动塞真 junction
            return Task.FromResult<string?>(null);
        }
        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }
}
```

- [ ] **Step 2: Run tests, verify 3/3 FAIL**
- [ ] **Step 3: 改 `ProcessLauncher.cs`**

3.1 ctor 加 `linker` + `sharedModelsDirectory` 参数:

```csharp
private readonly string _projectRoot;
private readonly SqliteConnectionFactory _dbFactory;
private readonly EnvironmentRepository _envRepo;
private readonly ProcessStateRepository _processStateRepo;
private readonly AppLogger? _logger;
private readonly int _startupTimeoutSeconds;
private readonly string _comfyUiLocale;
private readonly string _sharedModelsDirectory;
private readonly JunctionLinker _linker;  // 新增
// ... existing fields

public ProcessLauncher(
    string projectRoot,
    SqliteConnectionFactory dbFactory,
    EnvironmentRepository envRepo,
    ProcessStateRepository processStateRepo,
    AppLogger? logger = null,
    int startupTimeoutSeconds = 600,
    string comfyUiLocale = "",
    string sharedModelsDirectory = "",
    JunctionLinker? linker = null)  // 新增
{
    _projectRoot = projectRoot;
    _dbFactory = dbFactory;
    _envRepo = envRepo;
    _processStateRepo = processStateRepo;
    _logger = logger;
    _startupTimeoutSeconds = startupTimeoutSeconds > 0 ? startupTimeoutSeconds : 600;
    _comfyUiLocale = comfyUiLocale ?? "";
    _sharedModelsDirectory = sharedModelsDirectory ?? "";
    _linker = linker ?? new JunctionLinker();  // 默认 real,App 端不传也跑得动
}
```

3.2 在 `StartEnvAsync` 写 comfy.settings.json locale **之前**,加:

```csharp
// v0.6.7.3: 启动前检查并重建 Models junction(改 SharedModelsDirectory 后自动生效)
try
{
    var comfyUiRootForModels = Path.GetDirectoryName(mainPy)!;
    await EnsureModelsJunctionAsync(comfyUiRootForModels, ct);
}
catch (Exception ex)
{
    _logger?.Info("env-start", $"Models junction 检查失败(继续启动): {ex.Message}");
}

// v0.6.7.2: 写 ComfyUI UI locale (现有代码,不动)
if (!string.IsNullOrWhiteSpace(_comfyUiLocale))
{ ... }
```

3.3 加 `EnsureModelsJunctionAsync` 方法:

```csharp
/// <summary>
/// v0.6.7.3: 启动前检查 <paramref name="comfyuiRoot"/>/models 是否指向
/// _sharedModelsDirectory,不一致则删重建。失败仅 INFO 日志,不阻塞启动。
/// </summary>
internal virtual async Task EnsureModelsJunctionAsync(
    string comfyuiRoot,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(_sharedModelsDirectory)) return;

    var sharedFull = Path.GetFullPath(_sharedModelsDirectory);
    var modelsLink = Path.Combine(comfyuiRoot, "models");

    bool needsRelink;
    if (!Directory.Exists(modelsLink))
    {
        needsRelink = true;
    }
    else
    {
        string? existingTarget = null;
        try { existingTarget = await _linker.GetTargetAsync(modelsLink, ct); }
        catch { existingTarget = null; }

        needsRelink = existingTarget is null
            || !string.Equals(
                Path.GetFullPath(existingTarget),
                sharedFull,
                StringComparison.OrdinalIgnoreCase);
    }

    if (!needsRelink) return;

    if (Directory.Exists(modelsLink))
    {
        Directory.Delete(modelsLink, recursive: true);
    }
    await _linker.CreateAsync(modelsLink, sharedFull, ct);
    _logger?.Info("env-start", $"重新链接 Models: {modelsLink} → {sharedFull}");
}
```

- [ ] **Step 4: 改 `App.xaml.cs` 传 sharedModelsDirectory**

在 `new ProcessLauncher(...)` 调用多加一个参数:

```csharp
_launcher = new ProcessLauncher(
    projectRoot, dbFactory, envRepo, processStateRepo, logger,
    settings.ComfyUiStartupTimeoutSeconds,
    settings.ComfyUiLocale,
    settings.SharedModelsDirectory);   // 新增
```

- [ ] **Step 5: Run tests, verify 3/3 PASS**
- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs
git commit -m "feat(wpf): ProcessLauncher 启动前检查重建 Models junction (v0.6.7.3 T4)"
```

---

### Task 5: 编辑器按钮(「工具」菜单 + 2 commands)+ 0 新测试(view-model wiring 由手工 GUI smoke 验;command 行为由既有 RelayCommand 测试覆盖)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/MainWindow.xaml`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `CurrentEnvironmentsViewModel?.Selected` (env-list 当前选中 env)
- Produces:
  - `MainViewModel.OpenComfySettingsJsonCommand` (RelayCommand)
  - `MainViewModel.OpenExtraModelPathsYamlCommand` (RelayCommand)
  - `MainWindow.xaml` 顶级 `MenuItem _工具(_T)` 下挂 2 项 + Separator + 「打开日志目录」

- [ ] **Step 1: 改 `MainWindow.xaml` 加「工具」菜单**

在「设置」MenuItem 之后、「关于」MenuItem 之前插:

```xaml
<MenuItem Header="_工具(_T)">
    <MenuItem Header="_打开 ComfyUI 设置..."
              Command="{Binding OpenComfySettingsJsonCommand}"
              InputGestureText="comfy.settings.json" />
    <MenuItem Header="_打开 Models 配置..."
              Command="{Binding OpenExtraModelPathsYamlCommand}"
              InputGestureText="extra_model_paths.yaml" />
    <Separator />
    <MenuItem Header="打开日志目录"
              Command="{Binding OpenLogFolderCommand}" />
</MenuItem>
```

从「文件」菜单移除「查看日志目录」(避免重复):找到并删 `<MenuItem Header="查看日志目录" Command="{Binding OpenLogFolderCommand}" />`。

- [ ] **Step 2: 改 `MainViewModel.cs` 加 2 个 command**

2.1 加 fields + test seams:

```csharp
public RelayCommand OpenComfySettingsJsonCommand { get; }
public RelayCommand OpenExtraModelPathsYamlCommand { get; }

// test seams
internal Action<string>? ProcessStartOverride { get; set; }
internal Action<string>? EnsureFileExistsOverride { get; set; }
```

2.2 在 ctor 注册:

```csharp
OpenComfySettingsJsonCommand = new RelayCommand(
    _ => OpenComfyConfigFile("comfy.settings.json"),
    _ => CurrentEnvironmentsViewModel?.Selected is not null);
OpenExtraModelPathsYamlCommand = new RelayCommand(
    _ => OpenComfyConfigFile("extra_model_paths.yaml"),
    _ => CurrentEnvironmentsViewModel?.Selected is not null);
```

2.3 加 helper 方法:

```csharp
/// <summary>
/// 打开 ComfyUI 配置文件。filename: "comfy.settings.json" 或 "extra_model_paths.yaml"。
/// 路径 = &lt;env-root&gt;/ComfyUI/{user/default/comfy.settings.json 或 extra_model_paths.yaml}。
/// </summary>
private void OpenComfyConfigFile(string filename)
{
    var env = CurrentEnvironmentsViewModel?.Selected;
    if (env is null) return;
    var comfyuiRoot = env.ComfyuiLayout == "shared" && env.ComfyuiSource is not null
        ? env.ComfyuiSource
        : Path.Combine(env.RootPath, "ComfyUI");
    string path = filename == "comfy.settings.json"
        ? Path.Combine(comfyuiRoot, "user", "default", "comfy.settings.json")
        : Path.Combine(comfyuiRoot, filename);

    if (EnsureFileExistsOverride is not null)
    {
        EnsureFileExistsOverride(path);
    }
    else
    {
        EnsureFileExists(path);
    }

    if (ProcessStartOverride is not null)
    {
        ProcessStartOverride(path);
    }
    else
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}

/// <summary>
/// 确保文件存在。comfy.settings.json 不存在 → 写 "{}"。
/// extra_model_paths.yaml 不存在 → 写 placeholder。
/// </summary>
private void EnsureFileExists(string path)
{
    if (File.Exists(path)) return;
    var dir = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(dir);
    if (path.EndsWith("comfy.settings.json", StringComparison.OrdinalIgnoreCase))
    {
        File.WriteAllText(path, "{}");
    }
    else
    {
        File.WriteAllText(path, "# ComfyUI Models 路径配置\n# 编辑 base_directory 指向共享 Models 目录(配合 Settings.SharedModelsDirectory)\n");
    }
}
```

2.4 监听 Selected 变化(refresh CanExecute):
`RelayCommand` 已有 `CommandManager.InvalidateRequerySuggested` 自动监听 UI 焦点;若不工作,在 `CurrentEnvironmentsViewModel` `Selected` setter 设 `CommandManager.InvalidateRequerySuggested()`。最简:`CurrentEnvironmentsViewModel` 已有 INPC(`Selected` 是 property),WPF CommandManager 自动监听 UI 上的 focus,但 Selected 是 VM property 不一定 trigger。最稳的:`CurrentEnvironmentsViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EnvironmentListViewModel.Selected)) CommandManager.InvalidateRequerySuggested(); };`,订阅在 ctor 里。

实际上,本 plan 简化:**让命令 CanExecute 始终 true**,GUI 选中与否只是「有没有 env」。Selected==null 时 helper 直接 return。CanExecute=true 让按钮总可点;空点 = no-op。

最终简化:

```csharp
OpenComfySettingsJsonCommand = new RelayCommand(_ => OpenComfyConfigFile("comfy.settings.json"));
OpenExtraModelPathsYamlCommand = new RelayCommand(_ => OpenComfyConfigFile("extra_model_paths.yaml"));
```

(移除 CanExecute predicate)

- [ ] **Step 3: Build 编译验证**

`dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings

- [ ] **Step 4: Run full suite, verify no regression**

`dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → ~620 PASS / 0 FAIL / 1 SKIP(607 + 13)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/MainWindow.xaml \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs
git commit -m "feat(wpf): 顶部「工具」菜单 + 打开 ComfyUI 设置/Models 配置 (v0.6.7.3 T5)"
```

---

### Task 6: 全量 verify + 重建 staging + memory

**Files:** none (memory update only)

- [ ] **Step 1:** `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
- [ ] **Step 2:** `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → ~620 PASS / 0 FAIL / 1 SKIP
- [ ] **Step 3:** 重建 staging per `feedback_staging_self_contained.md`:

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

- [ ] **Step 4:** `git status --short` → working tree clean(staging exe 时间戳变动 gitignored)
- [ ] **Step 5:** 无 v-bump / 无 zip(G19)
- [ ] **Step 6:** 更新 memory:
  - 创建 `memory/project_v0_6_7_3_shared_models_editor.md`(commit 链 + 关键改动 + GUI smoke 状态)
  - 更新 `MEMORY.md` 一行指向新文件 + 短描述

---

## Verification

### 单元测试

`dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
预期 ~620 PASS / 0 FAIL / 1 SKIP(607 + 13:2 SettingsViewModel + 3 JunctionLinker + 5 EnvCreator + 3 ProcessLauncher)

### 端到端手动测试(staging exe)

1. **Settings UI**:打开 Settings,看到新增「共享 Models 目录」行;textbox + 浏览;留空 + 保存 → `settings.json` 包含 `"shared_models_directory": ""`
2. **env-create 无共享**:留空 SharedModelsDirectory,新建 env → `<env-root>/ComfyUI/models/` 是本地拷贝(几百 MB)
3. **env-create 共享**:SharedModelsDirectory 填一个已存在的空目录 → 新建 env → `<env-root>/ComfyUI/models` 是 junction(`dir /AL` 显示 `<JUNCTION>`)
4. **shared layout env 共享**:同步骤 3,但 layout = shared → junction 链回 `<comfyui-source>/models` 先被删(junction,删的是链),然后新建 junction 到 SharedModelsDirectory
5. **env-start 重建**:SharedModelsDirectory 已改,启动已建 env → 旧 junction target 不一致,被删重建;日志记录 `重新链接 Models: ...`
6. **env-start target 一致**:SharedModelsDirectory 未改 → junction 不重建(perf:无 redundant work)
7. **失败回滚**:SharedModelsDirectory 指向不存在路径 → env-create 失败,env 根目录被整体删;SQLite 无残留行
8. **编辑器按钮(ComfyUI 设置)**:env-list 选中 env → 点「工具 → 打开 ComfyUI 设置...」 → 系统默认关联程序(记事本/vscode)打开 `<env-root>/ComfyUI/user/default/comfy.settings.json`(locale 内容保留);没选 env → no-op
9. **编辑器按钮(Models 配置)**:点「工具 → 打开 Models 配置...」 → 打开 `<env-root>/ComfyUI/extra_model_paths.yaml`(`# TODO: M1 填充` 占位);shared layout env → 打开 `<comfyui-source>/extra_model_paths.yaml`
10. **日志目录去重**:验证文件菜单不再有「查看日志目录」;工具菜单下「打开日志目录」保留并可用

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| shared layout + 删 `<env-root>/ComfyUI/models` 误删 `<comfyui-source>/models` | `Directory.Delete` 对 junction 是删 junction 不删源(Windows 行为);本 plan 仅针对该 env 的 `comfyuiLink` 路径 |
| SharedModelsDirectory 改路径后已建 env 旧 junction target 还在 | env-start 启动前检查重建;失败仅 INFO 日志 |
| junction 重建失败(env-start 阶段) | 仅 INFO 日志,不阻塞启动 — 用户已确认;用户在 ComfyUI 端可能看不到 Models,后续可手动补救 |
| 编辑器按钮 在 env-list 未选中时无法工作 | CanExecute 简化:始终可点;Selected==null 时 helper return no-op(用户点没反应) |
| `ExtraModelPathsYaml` 字段现状(`# TODO: M1 填充` 占位) | 不动;编辑器按钮让用户手动编辑 |
| `Settings.SharedModelsDirectory` 是相对路径 | `EnvCreatorService` / `ProcessLauncher` 内部 `Path.GetFullPath` 规范化 |
| `Settings.SharedModelsDirectory` 路径含空格 | `cmd /c mklink /D` 加引号 handle;`Path.GetFullPath` 跨平台一致 |
| `JunctionLinker.GetTargetAsync` 在测试中是真 cmd | 测试跑在 Windows 即可;若 CI 是 macOS / Linux 测试会失败 — 但本项目仅 Windows,接受 |
| `ProcessLauncher` ctor 加 `JunctionLinker? linker = null` 参数破坏既有 4 处调用 | 现有调用不传新参数 → 默认 `new JunctionLinker()`,行为不变 |
| 菜单项数量增加导致拥挤 | 「工具」是新增顶级菜单,跟文件/设置/关于同级;每菜单下 2-4 项,可接受 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/Settings.cs`(1 field)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(1 property)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(1 row)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(1 click handler)
- `src-wpf/ComfyUI.Manager/Infrastructure/JunctionLinker.cs`(1 method)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(1 step + rollback)
- `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`(ctor + 1 method + 1 hook in StartEnvAsync)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(1 ctor arg)
- `src-wpf/ComfyUI.Manager/Views/MainWindow.xaml`(1 MenuItem block)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(2 commands + 2 helpers)
- 4 new test files

---

## Self-Review

**1. Spec coverage:**

| spec 需求 | task |
|---|---|
| §3.1 Settings.SharedModelsDirectory + UI + VM | T1 |
| §3.2 env-create 步骤 5.5 链接共享 Models + 失败回滚 | T3 |
| §3.3 env-start 启动前检查重建 Models junction | T4 |
| §3.3 JunctionLinker.GetTargetAsync | T2 |
| §3.4 编辑器按钮(2 个)+ 工具菜单 | T5 |
| §3.4 「打开日志目录」顺带挪到工具菜单 | T5 (file menu 移除 + tools menu 加) |
| §5.1 测试 +13 | T1+T2+T3+T4 |
| §5.2 手动 GUI smoke 10 步 | T6 verify 清单 |

**2. Placeholder scan:** 无 "TBD" / "TODO" / "implement later"。每个 step 含完整代码。

**3. Type consistency:**
- `Settings.SharedModelsDirectory` (string) 在 T1 定义,T3 T4 复用 ✓
- `JunctionLinker.GetTargetAsync(string, CancellationToken)` 在 T2 定义,T4 复用 ✓
- `ProcessLauncher.EnsureModelsJunctionAsync(string, CancellationToken)` 在 T4 定义,T4 测试通过 reflection 调 ✓
- `MainViewModel.OpenComfySettingsJsonCommand` / `OpenExtraModelPathsYamlCommand` 在 T5 定义,T5 XAML 引用 ✓

**4. Ambiguity check:**
- "删除旧 junction" 在 §3.3 明确 `Directory.Delete(recursive: true)`;T4 step 3.3 实现 ✓
- "Selected==null 时" 按钮行为:T5 step 2.4 明确"始终可点,no-op" ✓
- "SharedModelsDirectory 不存在时" T3 step 3 测试覆盖 ✓
- 「查看日志目录」从文件菜单移除,T5 step 1 明确 ✓