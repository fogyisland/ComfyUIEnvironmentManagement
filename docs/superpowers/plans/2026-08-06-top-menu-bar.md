# v0.6.5.21 Top Menu Bar + About Dialog 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `MainWindow` 顶部加传统 `Menu` 条(文件 / 设置 / 关于),含 Alt+字母助记键;文件菜单提供 UI 偏好(窗口尺寸/位置/最近选中 env)保存/加载(`<projectRoot>/config/ui-preferences.json`)+ 打开项目文件夹 + 打开日志目录 + 退出;设置菜单打开 Settings 页(复用现有命令);关于菜单弹模态 `AboutDialog`,含版本号/仓库链接/授权/微信赞助二维码(`<projectRoot>/assets/wechat-donate.png`)。侧栏 6 个导航按钮仍保留(并存,不替换)。

**Architecture:**
- 新 `Models/UiPreferences.cs`(DTO + JSON 序列化) + 新 `Services/UiPreferencesService.cs`(Save/Load round-trip + `DefaultPath` + `Loaded` 事件)
- `MainViewModel` 加 6 个 `RelayCommand`(`SaveUiPreferencesCommand` / `LoadUiPreferencesCommand` / `OpenProjectFolderCommand` / `OpenLogFolderCommand` / `ExitAppCommand` / `ShowAboutCommand`)+ 注入 `UiPreferencesService`
- 新 `ViewModels/AboutDialogViewModel.cs` + `Views/AboutDialog.xaml` + `.xaml.cs`(模态 `Show(Window owner)` 静态方法,堆叠布局 360×420,缺二维码时占位)
- `MainWindow.xaml` Grid 新 Row 0 = Auto 放 `<Menu>`,现有 Row 0/1 下移 1
- `App.OnStartup` 调 `UiPreferencesService.LoadFromFile(DefaultPath)` → 订阅 `Loaded` → 应用 Window 尺寸/位置/最大化;`MainWindow.Closing` 写回 preferences
- 测试 `UiPreferencesService`(round-trip + 缺文件 + JSON 损坏 + 多显示器位置裁剪)+ `AboutDialogViewModel`(默认版本号 + 资源缺位占位)+ `MainViewModel` 6 个新 command 测试 + 现有 469 个测试无回归

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite`(本 plan 不动 SQLite) · hand-rolled MVVM (`RelayCommand`) · `System.Text.Json`(UiPreferences 序列化) · `Microsoft.Win32.SaveFileDialog`/`OpenFileDialog` · `System.Diagnostics.Process`(explorer.exe) · `System.Windows.Media.Imaging.BitmapImage`(二维码运行时加载)

## Context

用户桌面验 v0.6.5.20 后,新需求:在 `MainWindow` 顶部加传统 Windows 风格的菜单条(文件 / 设置 / 关于 3 个顶级菜单),并明确指定要 Alt+单字母助记键 + 文件菜单含保存/加载"环境"(实际是 UI 偏好:窗口大小、位置、侧栏状态、最近选中 env 等)。设置菜单等价侧栏按钮。关于菜单弹模态对话框,末尾展示微信赞助二维码作为小额捐赠入口。

侧栏仍保留(用户原话:"侧栏+菜单并存"),不替换现有 6 个导航按钮 — 菜单是补充快捷入口,不是替代方案。

`config/ui-preferences.json` 路径在 spec 中确定;二维码 png 在 `<projectRoot>/assets/`,缺位时 AboutDialog 走占位文本(不崩)。

**base SHA:** `15e81d2`(v0.6.5.21 spec 落地 commit)

**相关已有代码:**
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` — 当前 Grid 2 行(侧栏 + 内容 / ErrorBanner)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` — 仅 `InitializeComponent`
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — 6 个现有 `ShowXxxCommand`,`CurrentView` 绑定到 ContentControl(已含 `EnvironmentsViewFactory` test seam,v0.6.5.20 加)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — `OnStartup` 组装服务,创建 `MainWindow`,设 `DataContext = _mainVm`
- `src-wpf/ComfyUI.Manager/Services/AppLogger.cs` — 启动写 INFO 行,本 plan 不动
- `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` — 现有 100+ 中文 string key;本 plan 加 6 个新 key
- `src-wpf/ComfyUI.Manager/Views/CatalogEntryPickerDialog.xaml.cs` — 现有对话框 `Show(...)` 静态方法,本 plan 参照同模式
- `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` — `<Version>0.6.5.6</Version>`(本 plan 不 bump,沿用 G12)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | 顶部 `<Menu>` 控件 + 3 个 `<MenuItem>`,放在 `MainWindow` Grid 新 Row 0,现有 Row 0/1 顺移到 Row 1/2(侧栏下移 1 Row,内容不变) | spec §Architecture 1 + G1 |
| G2 | 助记符:每个顶级菜单 Header 含 `_X` 下划线(`_文件(F)` / `_设置(S)` / `_关于(H)`);WPF 自动渲染下划线 + Alt+字母触发 | spec §G2 + user 选 Alt+单字母 |
| G3 | 文件菜单 5 项 + 2 `<Separator />`:`_保存环境...` / `_加载环境...` / `打开项目文件夹` / `查看日志目录` / `_退出` | spec §G3 |
| G4 | 设置菜单只 1 项:`设置...`,`Command="{Binding ShowSettingsCommand}"`(复用现有命令) | spec §G4 + G13 |
| G5 | 关于菜单只 1 项:`关于 ComfyUI Manager...`,`Command="{Binding ShowAboutCommand}"` | spec §G5 |
| G6 | UI 偏好文件路径 = `<projectRoot>/config/ui-preferences.json`,`projectRoot` = `MainViewModel._projectRoot`(复用 `App.OnStartup` 第 22-23 行的 `Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd('\\')`) | spec §G6 |
| G7 | UI 偏好 Save 走 `Microsoft.Win32.SaveFileDialog`(filter `*.json`,默认文件名 `ui-preferences.json`,初始目录 `<projectRoot>/config/`);Load 走 `OpenFileDialog`(filter `*.json`,初始目录同上) | spec §G7 |
| G8 | 启动时(`App.OnStartup` 在 `MainWindow.Show()` 之前)调 `UiPreferencesService.LoadFromFile(DefaultPath)`;订阅 `Loaded` 事件;`MainWindow.SourceInitialized` 把 prefs 应用到 Window(Width/Height/Left/Top/Maximized;位置越界 → 退到 (100,100));关闭时(`MainWindow.Closing`)写回 prefs | spec §G8 + Risk §"多显示器位置" |
| G9 | `AboutDialog.Show(Window owner)` 静态方法:`Owner = owner` + `ShowDialog()`(模态);`Esc` 关闭(Window `InputBindings` 设 `KeyBinding Key="Escape" Command="{Binding CloseCommand}"`) | spec §G9 |
| G10 | 微信赞助二维码 = `<projectRoot>/assets/wechat-donate.png`;`AboutDialogViewModel.HasDonateImage = File.Exists(path)`;缺位时 XAML 用 `Visibility` 切换:Image 隐藏 + 占位 TextBlock "二维码未配置,请联系作者" 显示;关闭按钮始终可见 | spec §G10 |
| G11 | 二维码加载走 `new BitmapImage(new Uri(absPath, UriKind.Absolute)) { CacheOption = BitmapCacheOption.OnLoad, CreateOptions = BitmapCreateOptions.None }`(同步 onLoad,不卡 UI;缺图 try/catch → 占位) | spec §G11 + Risk §"BitmapImage 异步加载" |
| G12 | 不 bump version(`csproj Version` 保持 `0.6.5.6`)/ 不发 release zip / 无 ledger commit | per v0.6.5.6 hotfix 偏好 |
| G13 | 侧栏 6 个按钮(环境 / 节点目录 / 基础环境 / 设置 / 批量更新 / 系统状态)保留;菜单的"设置"项等价侧栏按钮(同 `ShowSettingsCommand`) | spec §G13 + user 选 并存 |
| G14 | 退出命令 = `Application.Current.Shutdown()`;不弹确认(UI 偏好自动保存,无未保存业务数据) | spec §G14 |
| G15 | 资源字符串走 `Strings.zh-CN.resx` 6 个新 key:`Menu_File` / `Menu_Settings` / `Menu_About` / `About_Title` / `About_Description` / `About_DonatePlaceholder`;XAML 不写硬编码中文(本 plan 加的,老代码不动) | spec §G15 |
| G16 | `AboutDialogViewModel` 的 `RepositoryUrl = "https://github.com/fogyisland/ComfyUIEnvironmentManagement"`、`LicenseText = "MIT"`、`IssuesUrl = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/issues"` 硬编码在 ctor(不走 Settings) | spec §G16 + user 原话 |
| G17 | `UiPreferencesService.LoadFromFile(path)`:文件不存在 / JSON 损坏 / 字段缺失 / 字段为 null → 静默回退 `new UiPreferences()`,只 log `AppLogger.Error("ui-preferences", message)`(失败不阻塞启动) | spec §G17 + Risk §"UI 偏好 JSON 字段被改坏" |
| G18 | 测试不依赖 git/WPF STA;`UiPreferencesService` 构造接受任意路径(测试用 `Path.GetTempPath()`);`AboutDialogViewModel` 不实例化 `BitmapImage`(只持 `DonateImagePath` 字符串 + `HasDonateImage` bool);`MainViewModel` 测试用现有 `StubView` 模式(用 `EnvironmentsViewFactory` test seam,v0.6.5.20 加) | spec §G18 + 项目风格 |
| G19 | `BitmapImage` 必须在 UI 线程上 freeze(跨线程访问会抛);`AboutDialogViewModel` 暴露 `CreateDonateImage()` 方法在 `AboutDialog` code-behind 里 UI 线程调用,不放在 VM ctor 里 | WPF BitmapImage 限制 |
| G20 | `UiPreferencesService.SaveToFile(path, prefs)` 路径不存在父目录时 `Directory.CreateDirectory` 兜底(同 `SettingsRepository.Save`) | 同 settings 模式 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/UiPreferences.cs` | ~40 | DTO:WindowWidth/Height/Left/Top/Maximized + SidebarVisible + LastSelectedEnvId + LastViewName;`[JsonPropertyName]` 序列化 |
| `src-wpf/ComfyUI.Manager/Services/UiPreferencesService.cs` | ~180 | `LoadFromFile(path)` 静默回退 + `SaveToFile(path, prefs)` + `DefaultPath` 静态(走 ctor 注入 projectRoot)+ `Loaded` 事件 |
| `src-wpf/ComfyUI.Manager/ViewModels/AboutDialogViewModel.cs` | ~120 | `Version`(读 `Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0"`) + `Description` + `LicenseText` + `RepositoryUrl` + `IssuesUrl` + `DonateImagePath` + `HasDonateImage` + `CreateDonateImage()`(UI 线程同步)+ `CloseCommand` |
| `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml` | ~80 | Window 360×420;StackPanel 堆叠:标题 + 描述 + 授权 + 仓库 hyperlink + 问题 hyperlink + Separator + 赞助文字 + Image/占位 + 致谢 + 关闭按钮;Esc 触发 CloseCommand |
| `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml.cs` | ~50 | `Show(Window owner)` 静态:`Owner = owner`;`ShowDialog()`;DataContext = `new AboutDialogViewModel(projectRoot)`;ShowDialog 之后 `if (image != null) image.Freeze()` |
| `assets/wechat-donate.png` | 0 | 二维码 png(用户后续提供;commit 里建占位文件,加 `.gitkeep` 或 1x1 png) |
| `tests-wpf/.../Services/UiPreferencesServiceTests.cs` | ~220 | 6 测试:round-trip / 缺文件回退 / JSON 损坏回退 / 字段缺失回退 / Save 创建父目录 / DefaultPath 走 `<projectRoot>/config/ui-preferences.json` |
| `tests-wpf/.../ViewModels/AboutDialogViewModelTests.cs` | ~150 | 5 测试:默认版本号非空 / `RepositoryUrl` 正确 / 二维码文件存在时 `HasDonateImage=true` / 二维码缺位时 `HasDonateImage=false` / `CreateDonateImage` 缺位返 null |
| `tests-wpf/.../ViewModels/MainViewModelMenuTests.cs` | ~140 | 6 测试:每个新 command `CanExecute=true`;`ExitAppCommand.Execute` 调 `Application.Current.Shutdown`(用 `ExitAppCommand` test seam 注入 action 避免真退出) |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Grid 新 Row 0 = `Auto` 放 `<Menu>` + 3 `<MenuItem>`;现有 `Border Grid.Row="0"` / `ContentControl Grid.Row="0"` / `ErrorBanner Grid.Row="1"` 顺移到 Row 1 / Row 1 / Row 2 |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` | `SourceInitialized` 事件 → 应用 UiPreferences(Width/Height/Left/Top/WindowState;位置越界 → (100,100));`Closing` 事件 → 写回 preferences(用 `App.UiPreferencesService` 静态或 ctor 注入) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | 加 6 个 `RelayCommand` 属性;ctor 多接 `UiPreferencesService` + `string projectRoot` + `Action<string>? exitAction`(test seam);`OpenProjectFolder` / `OpenLogFolder` 命令体调 `Process.Start("explorer.exe", path)` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `OnStartup` 在 `MainWindow.Show()` 之前:`var uiPrefsService = new UiPreferencesService(projectRoot);` → `var uiPrefs = uiPrefsService.LoadFromFile(UiPreferencesService.DefaultPath);` → 把 `uiPrefs` 透传给 `MainWindow` + `MainViewModel`(构造函数 / 静态属性);`Application.Current.Shutdown` 由 `ExitAppCommand` 触发 |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | +6 key:`Menu_File` = "文件" / `Menu_Settings` = "设置" / `Menu_About` = "关于" / `About_Title` = "ComfyUI Manager" / `About_Description` = "一站式 ComfyUI 环境管理工具" / `About_DonatePlaceholder` = "二维码未配置,请联系作者" |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx`(default English fallback) | +6 同名 key:English 默认值(跟 zh-CN 同名时优先 zh-CN 走 fallback) |

### Delete

无。

### Keep (unchanged)

- `MainViewModel` 现有 6 个 `ShowXxxCommand` + `_environmentsViewModel` 缓存逻辑(v0.6.5.20)
- `AppLogger` + 所有 subsystem 接入
- `Settings` + `SettingsRepository`(本 plan 不动)
- `EnvironmentRepository` / `BedStatus` 列(本 plan 不动 SQLite)
- `MainWindow` 现有侧栏 Border + ContentControl + ErrorBanner(只 Grid.Row 顺移)

---

## Tasks

### Task 1: `UiPreferences` DTO + JSON 序列化

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/UiPreferences.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/UiPreferencesSerializationTests.cs`(round-trip 序列化 / 反序列化)

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public class UiPreferences
  {
      [JsonPropertyName("window_width")]    public double? WindowWidth    { get; set; }
      [JsonPropertyName("window_height")]   public double? WindowHeight   { get; set; }
      [JsonPropertyName("window_left")]     public double? WindowLeft     { get; set; }
      [JsonPropertyName("window_top")]      public double? WindowTop      { get; set; }
      [JsonPropertyName("window_maximized")] public bool  WindowMaximized { get; set; }
      [JsonPropertyName("sidebar_visible")]  public bool  SidebarVisible  { get; set; } = true;
      [JsonPropertyName("last_selected_env_id")] public string? LastSelectedEnvId { get; set; }
      [JsonPropertyName("last_view_name")]       public string? LastViewName      { get; set; }
  }
  ```

- [ ] **Step 1: Write failing test**

```csharp
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class UiPreferencesSerializationTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var orig = new UiPreferences
        {
            WindowWidth = 1024,
            WindowHeight = 768,
            WindowLeft = 100,
            WindowTop = 50,
            WindowMaximized = true,
            SidebarVisible = false,
            LastSelectedEnvId = "env-abc",
            LastViewName = "Catalog",
        };
        var json = JsonSerializer.Serialize(orig, Opts);
        var back = JsonSerializer.Deserialize<UiPreferences>(json, Opts)!;
        Assert.Equal(1024, back.WindowWidth);
        Assert.Equal(768, back.WindowHeight);
        Assert.Equal(100, back.WindowLeft);
        Assert.Equal(50, back.WindowTop);
        Assert.True(back.WindowMaximized);
        Assert.False(back.SidebarVisible);
        Assert.Equal("env-abc", back.LastSelectedEnvId);
        Assert.Equal("Catalog", back.LastViewName);
    }

    [Fact]
    public void Deserialize_AllFieldsNull_ReturnsDefaults()
    {
        var back = JsonSerializer.Deserialize<UiPreferences>("{}", Opts)!;
        Assert.Null(back.WindowWidth);
        Assert.Null(back.WindowLeft);
        Assert.False(back.WindowMaximized);
        Assert.True(back.SidebarVisible);  // default true
        Assert.Null(back.LastSelectedEnvId);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~UiPreferencesSerializationTests" -v minimal`
Expected: FAIL — `'UiPreferences' does not exist`(CS0246)

- [ ] **Step 3: Implement `Models/UiPreferences.cs`**

```csharp
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// UI 偏好(窗口尺寸/位置/侧栏状态/最近选中 env/最近视图)— v0.6.5.21。
/// 走 <c>&lt;projectRoot&gt;/config/ui-preferences.json</c>(G6),失败静默回退默认值。
/// </summary>
public class UiPreferences
{
    [JsonPropertyName("window_width")]     public double? WindowWidth    { get; set; }
    [JsonPropertyName("window_height")]    public double? WindowHeight   { get; set; }
    [JsonPropertyName("window_left")]      public double? WindowLeft     { get; set; }
    [JsonPropertyName("window_top")]       public double? WindowTop      { get; set; }
    [JsonPropertyName("window_maximized")] public bool    WindowMaximized { get; set; }
    [JsonPropertyName("sidebar_visible")]  public bool    SidebarVisible { get; set; } = true;
    [JsonPropertyName("last_selected_env_id")] public string? LastSelectedEnvId { get; set; }
    [JsonPropertyName("last_view_name")]       public string? LastViewName     { get; set; }
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~UiPreferencesSerializationTests" -v minimal`
Expected: PASS(2/2)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/UiPreferences.cs tests-wpf/ComfyUI.Manager.Tests/Models/UiPreferencesSerializationTests.cs
git commit -m "feat(wpf): UiPreferences DTO + JSON 序列化 (v0.6.5.21 part 1)"
```

---

### Task 2: `UiPreferencesService`(Save/Load/DefaultPath/Loaded 事件) + 6 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/UiPreferencesService.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/UiPreferencesServiceTests.cs`(6 测试)

**Interfaces:**
- Consumes: `UiPreferences`(Task 1)
- Produces:
  ```csharp
  public class UiPreferencesService
  {
      public UiPreferencesService(string projectRoot, AppLogger? logger = null);
      public string DefaultPath { get; }   // <projectRoot>/config/ui-preferences.json
      public event EventHandler<UiPreferences>? Loaded;
      public UiPreferences LoadFromFile(string path);   // 失败静默回退 + 触发 Loaded
      public void SaveToFile(string path, UiPreferences prefs);
  }
  ```

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class UiPreferencesServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _configDir;
    private readonly UiPreferencesService _svc;

    public UiPreferencesServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "ui-prefs-tests-" + Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_projectRoot, "config");
        _svc = new UiPreferencesService(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultPath_IsUnderConfigUnderProjectRoot()
    {
        Assert.Equal(Path.Combine(_projectRoot, "config", "ui-preferences.json"), _svc.DefaultPath);
    }

    [Fact]
    public void LoadFromFile_NoFile_ReturnsDefaults_AndFiresLoaded()
    {
        var loaded = (UiPreferences?)null;
        _svc.Loaded += (_, p) => loaded = p;

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);

        Assert.Null(prefs.WindowWidth);
        Assert.True(prefs.SidebarVisible);
        Assert.NotNull(loaded);
        Assert.Equal(prefs.WindowWidth, loaded!.WindowWidth);
    }

    [Fact]
    public void SaveToFile_ThenLoadFromFile_RoundTripsAllFields()
    {
        var orig = new UiPreferences
        {
            WindowWidth = 1200,
            WindowHeight = 800,
            WindowLeft = 50,
            WindowTop = 50,
            WindowMaximized = true,
            SidebarVisible = false,
            LastSelectedEnvId = "env-x",
            LastViewName = "Environments",
        };
        _svc.SaveToFile(_svc.DefaultPath, orig);

        Assert.True(File.Exists(_svc.DefaultPath));

        var back = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1200, back.WindowWidth);
        Assert.Equal(800, back.WindowHeight);
        Assert.True(back.WindowMaximized);
        Assert.False(back.SidebarVisible);
        Assert.Equal("env-x", back.LastSelectedEnvId);
        Assert.Equal("Environments", back.LastViewName);
    }

    [Fact]
    public void LoadFromFile_MissingFields_ReturnsDefaults()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_svc.DefaultPath, "{\"window_width\": 1100}");  // 只写一个字段

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Equal(1100, prefs.WindowWidth);
        Assert.Null(prefs.WindowHeight);
        Assert.False(prefs.WindowMaximized);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void LoadFromFile_CorruptJson_ReturnsDefaults_DoesNotThrow()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_svc.DefaultPath, "{ this is not valid JSON :::");

        var prefs = _svc.LoadFromFile(_svc.DefaultPath);
        Assert.Null(prefs.WindowWidth);
        Assert.True(prefs.SidebarVisible);
    }

    [Fact]
    public void SaveToFile_CreatesParentDirIfMissing()
    {
        // _configDir 不存在,SaveToFile 应该自动建
        Assert.False(Directory.Exists(_configDir));
        _svc.SaveToFile(_svc.DefaultPath, new UiPreferences { WindowWidth = 999 });
        Assert.True(Directory.Exists(_configDir));
        Assert.True(File.Exists(_svc.DefaultPath));
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~UiPreferencesServiceTests" -v minimal`
Expected: FAIL — `'UiPreferencesService' does not exist`(CS0246)

- [ ] **Step 3: Implement `Services/UiPreferencesService.cs`**

```csharp
using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// UI 偏好持久化:<c>&lt;projectRoot&gt;/config/ui-preferences.json</c>(G6/G20)。
/// 加载失败静默回退 <see cref="UiPreferences"/> 默认值,只走 <see cref="AppLogger"/> 记 ERROR(G17);
/// 加载成功触发 <see cref="Loaded"/> 事件(订阅者:MainWindow 应用 Window 尺寸 / MainViewModel 切 LastViewName)。
/// </summary>
public class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly AppLogger? _logger;

    public string DefaultPath { get; }

    public UiPreferencesService(string projectRoot, AppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));
        _logger = logger;
        DefaultPath = Path.Combine(projectRoot, "config", "ui-preferences.json");
    }

    /// <summary>加载完成后触发(订阅者从 <see cref="UiPreferences"/> 读字段应用)。</summary>
    public event EventHandler<UiPreferences>? Loaded;

    /// <summary>
    /// 从 <paramref name="path"/> 加载。失败(文件不存在 / JSON 损坏 / 字段缺失)→ 静默
    /// 回退 <c>new UiPreferences()</c>,触发 <see cref="Loaded"/>(让订阅者照常启动)。
    /// </summary>
    public UiPreferences LoadFromFile(string path)
    {
        UiPreferences prefs;
        try
        {
            if (!File.Exists(path))
            {
                prefs = new UiPreferences();
            }
            else
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    prefs = new UiPreferences();
                }
                else
                {
                    prefs = JsonSerializer.Deserialize<UiPreferences>(json, JsonOpts)
                        ?? new UiPreferences();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("ui-preferences", $"加载失败 path={path}", ex);
            prefs = new UiPreferences();
        }
        Loaded?.Invoke(this, prefs);
        return prefs;
    }

    /// <summary>写 prefs 到 <paramref name="path"/>(父目录不存在则创建,G20)。失败只 log。</summary>
    public void SaveToFile(string path, UiPreferences prefs)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(prefs, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.Error("ui-preferences", $"保存失败 path={path}", ex);
        }
    }
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~UiPreferencesServiceTests" -v minimal`
Expected: PASS(6/6)

- [ ] **Step 5: Run full suite, no regression**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 471 PASS / 0 FAIL / 1 SKIP(469 + 2 new UiPreferencesSerializationTests)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/UiPreferencesService.cs tests-wpf/ComfyUI.Manager.Tests/Services/UiPreferencesServiceTests.cs
git commit -m "feat(wpf): UiPreferencesService Save/Load + DefaultPath + Loaded 事件 (v0.6.5.21 part 2)"
```

---

### Task 3: `AboutDialogViewModel` + 5 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/AboutDialogViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/AboutDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `string projectRoot`
- Produces:
  ```csharp
  public sealed class AboutDialogViewModel : ViewModelBase
  {
      public AboutDialogViewModel(string projectRoot);
      public string Version { get; }                          // assembly version
      public string Description { get; }                     // "一站式 ComfyUI 环境管理工具"(resx)
      public string LicenseText { get; }                     // "MIT"(G16 硬编码)
      public string RepositoryUrl { get; }                   // "https://github.com/fogyisland/ComfyUIEnvironmentManagement"
      public string IssuesUrl { get; }                       // "https://github.com/fogyisland/ComfyUIEnvironmentManagement/issues"
      public string DonateImagePath { get; }                 // <projectRoot>/assets/wechat-donate.png
      public bool HasDonateImage { get; }                    // File.Exists(DonateImagePath)
      public string DonatePlaceholder { get; }                // resx "二维码未配置,请联系作者"
      public BitmapSource? CreateDonateImage();              // UI 线程同步;缺图 → null
      public RelayCommand CloseCommand { get; }              // Esc 触发
      public void Close();                                   // 触发 RequestClose
      public event EventHandler? RequestClose;               // View code-behind 订阅 → Close()
  }
  ```

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class AboutDialogViewModelTests : IDisposable
{
    private readonly string _projectRoot;

    public AboutDialogViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "about-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Version_IsNonEmpty()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.False(string.IsNullOrEmpty(vm.Version));
    }

    [Fact]
    public void RepositoryUrl_PointsToFogyislandRepo()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.Equal("https://github.com/fogyisland/ComfyUIEnvironmentManagement", vm.RepositoryUrl);
    }

    [Fact]
    public void HasDonateImage_TrueWhenPngExists()
    {
        var assetsDir = Path.Combine(_projectRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllBytes(Path.Combine(assetsDir, "wechat-donate.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.True(vm.HasDonateImage);
    }

    [Fact]
    public void HasDonateImage_FalseWhenPngMissing()
    {
        // assets/ 不存在或文件缺位
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.False(vm.HasDonateImage);
    }

    [Fact]
    public void CreateDonateImage_ReturnsNullWhenPngMissing()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.Null(vm.CreateDonateImage());
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~AboutDialogViewModelTests" -v minimal`
Expected: FAIL — `'AboutDialogViewModel' does not exist`

- [ ] **Step 3: Implement `ViewModels/AboutDialogViewModel.cs`**

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using ComfyUI.Manager.Properties;  // 来自 Strings.resx auto-generated

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// About 对话框的 VM — v0.6.5.21。堆叠顶~下:标题 + 版本 + 描述 + 授权 + 仓库 / 问题
/// 链接 + 二维码(<c>&lt;projectRoot&gt;/assets/wechat-donate.png</c>)。
///
/// 二维码加载策略(G10/G11/G19):
/// - <see cref="HasDonateImage"/> 是同步 bool(<c>File.Exists</c>),XAML 用它切换 Image vs 占位;
/// - <see cref="CreateDonateImage"/> 在 UI 线程同步创建 <see cref="BitmapImage"/>(不是异步),
///   View code-behind 在 <c>Loaded</c> 事件里调一次,缺位返 null。
/// </summary>
public sealed class AboutDialogViewModel : ViewModelBase
{
    public const string RepositoryUrlValue = "https://github.com/fogyisland/ComfyUIEnvironmentManagement";
    public const string IssuesUrlValue = RepositoryUrlValue + "/issues";
    public const string LicenseTextValue = "MIT";
    public const string DonateImageFileName = "wechat-donate.png";

    private readonly string _projectRoot;

    public AboutDialogViewModel(string projectRoot)
    {
        _projectRoot = projectRoot;
        Version = (Assembly.GetExecutingAssembly().GetName().Version?.ToString()) ?? "0.0.0";
        Description = Strings.About_Description;
        LicenseText = LicenseTextValue;
        RepositoryUrl = RepositoryUrlValue;
        IssuesUrl = IssuesUrlValue;
        DonateImagePath = Path.Combine(projectRoot, "assets", DonateImageFileName);
        HasDonateImage = File.Exists(DonateImagePath);
        DonatePlaceholder = Strings.About_DonatePlaceholder;
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Version { get; }
    public string Description { get; }
    public string LicenseText { get; }
    public string RepositoryUrl { get; }
    public string IssuesUrl { get; }
    public string DonateImagePath { get; }
    public bool HasDonateImage { get; }
    public string DonatePlaceholder { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>UI 线程同步创建 <see cref="BitmapSource"/>;缺位返 null。View code-behind 调。</summary>
    public BitmapSource? CreateDonateImage()
    {
        if (!HasDonateImage) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;     // 同步加载到内存
            img.CreateOptions = BitmapCreateOptions.None;
            img.UriSource = new Uri(DonateImagePath, UriKind.Absolute);
            img.EndInit();
            img.Freeze();   // 跨线程安全
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>View code-behind 订阅 → 调 <c>Close()</c>。</summary>
    public event EventHandler? RequestClose;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~AboutDialogViewModelTests" -v minimal`
Expected: PASS(5/5)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/AboutDialogViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/AboutDialogViewModelTests.cs
git commit -m "feat(wpf): AboutDialogViewModel 版本/链接/二维码 (v0.6.5.21 part 3)"
```

---

### Task 4: `AboutDialog.xaml` + code-behind + `Show(Window owner)` 静态方法

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml.cs`

**Step 1: Implement `AboutDialog.xaml`**

```xml
<Window x:Class="ComfyUI.Manager.Views.AboutDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="关于 ComfyUI Manager" Width="360" Height="420"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize" ShowInTaskbar="False">
    <Window.InputBindings>
        <KeyBinding Key="Escape" Command="{Binding CloseCommand}" />
    </Window.InputBindings>
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- 标题 + 版本 -->
        <DockPanel Grid.Row="0" Margin="0,0,0,12">
            <TextBlock DockPanel.Dock="Right" Text="{Binding Version}"
                       FontSize="14" Foreground="Gray" VerticalAlignment="Bottom" />
            <TextBlock Text="{x:Static properties:Strings.About_Title}"
                       FontSize="24" FontWeight="Bold" />
        </DockPanel>

        <!-- 描述 -->
        <TextBlock Grid.Row="1" Text="{Binding Description}"
                   FontSize="12" Foreground="Gray" TextWrapping="Wrap"
                   Margin="0,0,0,16" />

        <!-- 授权 -->
        <TextBlock Grid.Row="2" Margin="0,0,0,8">
            <Run Text="授权:" FontWeight="Bold" />
            <Run Text="{Binding LicenseText, Mode=OneWay}" />
        </TextBlock>

        <!-- 仓库 -->
        <TextBlock Grid.Row="3" Margin="0,0,0,4">
            <Run Text="仓库:" FontWeight="Bold" />
            <Hyperlink NavigateUri="{Binding RepositoryUrl}" RequestNavigate="OnHyperlinkRequestNavigate">
                <Run Text="{Binding RepositoryUrl, Mode=OneWay}" />
            </Hyperlink>
        </TextBlock>

        <!-- 问题反馈 -->
        <TextBlock Grid.Row="4" Margin="0,0,0,16">
            <Run Text="问题反馈:" FontWeight="Bold" />
            <Hyperlink NavigateUri="{Binding IssuesUrl}" RequestNavigate="OnHyperlinkRequestNavigate">
                <Run Text="{Binding IssuesUrl, Mode=OneWay}" />
            </Hyperlink>
        </TextBlock>

        <Separator Grid.Row="5" Margin="0,0,0,12" />

        <!-- 赞助 -->
        <TextBlock Grid.Row="6" Text="扫码赞助(微信)"
                   FontSize="12" Foreground="Gray" Margin="0,0,0,8" />

        <Grid Grid.Row="7" Margin="0,0,0,8">
            <Image x:Name="DonateImage" Width="180" Height="180"
                   Source="{Binding ., Converter={StaticResource NullToVisibility}}"
                   Visibility="{Binding HasDonateImage, Converter={StaticResource BoolToVisibility}}" />
            <!-- ↑ Source binding 在 code-behind Loaded 事件里手动设 CreateDonateImage() -->
            <TextBlock Text="{Binding DonatePlaceholder}" TextWrapping="Wrap"
                       FontSize="11" Foreground="Gray" HorizontalAlignment="Center"
                       VerticalAlignment="Center" MaxWidth="180"
                       Visibility="{Binding HasDonateImage, Converter={StaticResource InverseBoolToVisibility}}" />
        </Grid>

        <TextBlock Grid.Row="8" Text="感谢你的支持 ❤"
                   FontSize="11" Foreground="Gray" HorizontalAlignment="Center"
                   Margin="0,0,0,16" />

        <!-- 关闭 -->
        <Button Grid.Row="11" Content="关闭" Command="{Binding CloseCommand}"
                Style="{StaticResource MaterialButton}" HorizontalAlignment="Right"
                MinWidth="80" />
    </Grid>
</Window>
```

> 注:`xmlns:properties="clr-namespace:ComfyUI.Manager.Properties"` 加在 Window 根(Strings resx auto-generated)。
> Source binding 因为 <c>CreateDonateImage</c> 需要 UI 线程 + BitmapImage 不能从 DataContext 走 XAML 简洁绑定(它不是 POCO 属性),
> 由 code-behind 在 Loaded 事件里手动设:<c>DonateImage.Source = vm.CreateDonateImage()</c>(Task 4 Step 2)。

- [ ] **Step 2: Implement `AboutDialog.xaml.cs`**

```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹模态 About 对话框。Owner 通常 <c>Application.Current.MainWindow</c>。
    /// v0.6.5.21 spec G9。projectRoot 用于定位 <c>assets/wechat-donate.png</c>。
    /// </summary>
    public static void Show(Window owner, string projectRoot)
    {
        var vm = new AboutDialogViewModel(projectRoot);
        var dlg = new AboutDialog
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        dlg.Loaded += (_, _) =>
        {
            // UI 线程同步创建 BitmapImage,缺位 null → Image 隐藏(Visibility 已经按 HasDonateImage 走)
            if (vm.HasDonateImage)
            {
                dlg.DonateImage.Source = vm.CreateDonateImage();
            }
        };
        dlg.ShowDialog();
    }

    /// <summary>Hyperlink 点击 → 用默认浏览器开 URL。</summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
```

- [ ] **Step 3: Build verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings(XAML compile 触发 resx codegen)

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml.cs
git commit -m "feat(wpf): AboutDialog XAML + Show 静态 (v0.6.5.21 part 4)"
```

---

### Task 5: `Strings.zh-CN.resx` + `Strings.resx` 加 6 个 key

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx`

- [ ] **Step 1: Add 6 keys to `Strings.zh-CN.resx`**

在 `</root>` 前插入:
```xml
  <data name="Menu_File" xml:space="preserve">
    <value>文件</value>
  </data>
  <data name="Menu_Settings" xml:space="preserve">
    <value>设置</value>
  </data>
  <data name="Menu_About" xml:space="preserve">
    <value>关于</value>
  </data>
  <data name="About_Title" xml:space="preserve">
    <value>ComfyUI Manager</value>
  </data>
  <data name="About_Description" xml:space="preserve">
    <value>一站式 ComfyUI 环境管理工具</value>
  </data>
  <data name="About_DonatePlaceholder" xml:space="preserve">
    <value>二维码未配置,请联系作者</value>
  </data>
```

- [ ] **Step 2: Add 6 keys (English fallback) to `Strings.resx`**

在 `</root>` 前插入(同名 key,英文 fallback):
```xml
  <data name="Menu_File" xml:space="preserve">
    <value>File</value>
  </data>
  <data name="Menu_Settings" xml:space="preserve">
    <value>Settings</value>
  </data>
  <data name="Menu_About" xml:space="preserve">
    <value>Help</value>
  </data>
  <data name="About_Title" xml:space="preserve">
    <value>ComfyUI Manager</value>
  </data>
  <data name="About_Description" xml:space="preserve">
    <value>One-stop ComfyUI environment manager</value>
  </data>
  <data name="About_DonatePlaceholder" xml:space="preserve">
    <value>Donate QR not configured. Contact author.</value>
  </data>
```

- [ ] **Step 3: Build verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors(resx 触发 auto-gen `Strings.Designer.cs`,新增 6 个 strong-typed 属性)

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx src-wpf/ComfyUI.Manager/Resources/Strings.resx
git commit -m "feat(wpf): Strings.resx +6 menu/about i18n keys (v0.6.5.21 part 5)"
```

---

### Task 6: `MainViewModel` 加 6 个 menu command + ctor 注入 + 6 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelMenuTests.cs`

**Interfaces:**
- Consumes: `UiPreferencesService`(Task 2)+ `AboutDialog`(Task 4)+ `projectRoot` 路径
- Produces:
  ```csharp
  public class MainViewModel : ViewModelBase
  {
      public RelayCommand SaveUiPreferencesCommand   { get; }   // 弹 SaveFileDialog → 写 prefs
      public RelayCommand LoadUiPreferencesCommand   { get; }   // 弹 OpenFileDialog → LoadFromFile → 触发 Loaded
      public RelayCommand OpenProjectFolderCommand  { get; }   // Process.Start("explorer.exe", projectRoot)
      public RelayCommand OpenLogFolderCommand      { get; }   // Process.Start("explorer.exe", Logs/)
      public RelayCommand ExitAppCommand            { get; }   // _exitAction() → 默认 Application.Current.Shutdown
      public RelayCommand ShowAboutCommand          { get; }   // AboutDialog.Show(MainWindow, projectRoot)

      internal Action<string>? OpenFolderOverride  { get; set; }  // test seam:替 Process.Start
      internal Action? ExitAppOverride            { get; set; }  // test seam:替 Shutdown
  }
  ```

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainViewModelMenuTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelMenuTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "main-vm-menu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewMainVm(out Action<string>? capturedFolder, out Action? capturedExit)
    {
        capturedFolder = null;
        capturedExit = null;
        var svc = new UiPreferencesService(_projectRoot);
        var main = new MainViewModel(
            _db.Factory, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!,
            null!, "", "", svc, null!);
        main.EnvironmentsViewFactory = vm => new object();  // 避 STA,跟 v0.6.5.20 同款
        main.OpenFolderOverride = p => capturedFolder = p;
        main.ExitAppOverride = () => capturedExit = () => { };
        return main;
    }

    [Fact]
    public void AllSixMenuCommands_CanExecuteIsTrue()
    {
        var main = NewMainVm(out _, out _);
        Assert.True(main.SaveUiPreferencesCommand.CanExecute(null));
        Assert.True(main.LoadUiPreferencesCommand.CanExecute(null));
        Assert.True(main.OpenProjectFolderCommand.CanExecute(null));
        Assert.True(main.OpenLogFolderCommand.CanExecute(null));
        Assert.True(main.ExitAppCommand.CanExecute(null));
        Assert.True(main.ShowAboutCommand.CanExecute(null));
    }

    [Fact]
    public void OpenProjectFolderCommand_DelegatesToOpenFolderOverride()
    {
        var main = NewMainVm(out var folder, out _);
        main.OpenProjectFolderCommand.Execute(null);
        Assert.NotNull(folder);
        Assert.Equal(_projectRoot, folder!);
    }

    [Fact]
    public void OpenLogFolderCommand_DelegatesToOpenFolderOverride()
    {
        var main = NewMainVm(out var folder, out _);
        main.OpenLogFolderCommand.Execute(null);
        Assert.NotNull(folder);
        Assert.Equal(Path.Combine(_projectRoot, "Logs"), folder!);
    }

    [Fact]
    public void ExitAppCommand_DelegatesToExitAppOverride()
    {
        var main = NewMainVm(out _, out var exit);
        main.ExitAppCommand.Execute(null);
        Assert.NotNull(exit);
    }

    [Fact]
    public void SaveUiPreferencesCommand_PopSaveDialogOverride_DelegatesToOverride()
    {
        var main = NewMainVm(out _, out _);
        var capturedPath = (string?)null;
        var capturedPrefs = (UiPreferences?)null;
        main.SaveUiPreferencesDialogOverride = (path, prefs) =>
        {
            capturedPath = path;
            capturedPrefs = prefs;
            return true;
        };
        // 关窗时调 SaveToFile——这里模拟关窗:直接 Execute + 注入一个能调到的 prefs 来源
        // 因为命令体需要当前 prefs,需要一个简单回调:在命令体里通过 _uiPreferencesService 读一次
        main.SaveUiPreferencesCommand.Execute(null);
        Assert.NotNull(capturedPath);
    }

    [Fact]
    public void LoadUiPreferencesCommand_PopOpenDialogOverride_DelegatesToOverride()
    {
        var main = NewMainVm(out _, out _);
        var capturedPath = (string?)null;
        main.LoadUiPreferencesDialogOverride = path => { capturedPath = path; return true; };
        main.LoadUiPreferencesCommand.Execute(null);
        Assert.NotNull(capturedPath);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelMenuTests" -v minimal`
Expected: FAIL — 多项:`SaveUiPreferencesDialogOverride` / `OpenFolderOverride` / `ExitAppOverride` 不存在

- [ ] **Step 3: Modify `MainViewModel.cs`**

加 `using ComfyUI.Manager.Views;` / `using System.Diagnostics;` / `using Microsoft.Win32;`(顶部 using 区域)。

在现有 `ShowEnvironmentsCommand { get; }` 之后追加:

```csharp
public RelayCommand SaveUiPreferencesCommand { get; }
public RelayCommand LoadUiPreferencesCommand { get; }
public RelayCommand OpenProjectFolderCommand { get; }
public RelayCommand OpenLogFolderCommand { get; }
public RelayCommand ExitAppCommand { get; }
public RelayCommand ShowAboutCommand { get; }

internal Action<string>? OpenFolderOverride { get; set; }  // test seam
internal Action? ExitAppOverride { get; set; }            // test seam
internal Func<string, UiPreferences, bool>? SaveUiPreferencesDialogOverride { get; set; }
internal Func<string, bool>? LoadUiPreferencesDialogOverride { get; set; }
```

ctor 末尾 + `SaveUiPreferencesCommand = ...` 等 6 个(挨着现有 `ShowSystemStatusCommand = ...` 之后):

```csharp
var uiPrefsService = uiPreferencesService
    ?? throw new ArgumentNullException(nameof(uiPreferencesService));
_projectRoot = projectRoot;  // 把现有 ctor 的 _projectRoot 用进来(已有字段)

SaveUiPreferencesCommand = new RelayCommand(_ => SaveUiPreferences(uiPrefsService));
LoadUiPreferencesCommand = new RelayCommand(_ => LoadUiPreferences(uiPrefsService));
OpenProjectFolderCommand = new RelayCommand(_ => OpenFolder(_projectRoot));
OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(Path.Combine(_projectRoot, "Logs")));
ExitAppCommand = new RelayCommand(_ => DoExit());
ShowAboutCommand = new RelayCommand(_ =>
{
    var owner = Application.Current?.MainWindow;
    if (owner is null) return;
    AboutDialog.Show(owner, _projectRoot);
});
```

> ctor 加 `UiPreferencesService uiPreferencesService` 参数(位置:`systemInfoCollector` 之后),
> 存到 `_uiPreferencesService` 私有字段;若 null → 抛 `ArgumentNullException`。

新增私有方法:

```csharp
private void OpenFolder(string path)
{
    try
    {
        Directory.CreateDirectory(path);  // 目录不存在先建(OpenLogFolder 适用)
        if (OpenFolderOverride is not null) { OpenFolderOverride(path); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        // log 到 ErrorBanner 不抛(用户原话没要弹窗)
        ErrorBanner.Push($"打开文件夹失败:{ex.Message}");
    }
}

private void DoExit()
{
    if (ExitAppOverride is not null) { ExitAppOverride(); return; }
    Application.Current?.Shutdown();
}

private void SaveUiPreferences(UiPreferencesService svc)
{
    // 收集当前 prefs(Window 尺寸 / LastSelectedEnvId 在 MainWindow code-behind 维护 —
    // 这里简化为只写 prefs.WindowMaximized/LastViewName,MainWindow.Closing 覆盖完整版)
    var prefs = new UiPreferences { LastViewName = ResolveCurrentViewName() };
    string path;
    if (SaveUiPreferencesDialogOverride is not null)
    {
        path = svc.DefaultPath;
        if (!SaveUiPreferencesDialogOverride(path, prefs)) return;
    }
    else
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "ui-preferences.json",
            InitialDirectory = Path.GetDirectoryName(svc.DefaultPath),
        };
        if (dlg.ShowDialog() != true) return;
        path = dlg.FileName;
    }
    svc.SaveToFile(path, prefs);
}

private void LoadUiPreferences(UiPreferencesService svc)
{
    string path;
    if (LoadUiPreferencesDialogOverride is not null)
    {
        path = svc.DefaultPath;
        if (!LoadUiPreferencesDialogOverride(path)) return;
    }
    else
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = Path.GetDirectoryName(svc.DefaultPath),
        };
        if (dlg.ShowDialog() != true) return;
        path = dlg.FileName;
    }
    svc.LoadFromFile(path);  // 触发 Loaded 事件,订阅者应用
}

private string? ResolveCurrentViewName()
{
    if (CurrentView is null) return null;
    var t = CurrentView.GetType().Name;
    return t switch
    {
        "EnvironmentListView" => "Environments",
        "CatalogView"         => "Catalog",
        "BaseEnvView"         => "BaseEnv",
        "SettingsView"        => "Settings",
        "SystemStatusView"    => "SystemStatus",
        _                     => t,
    };
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModelMenuTests" -v minimal`
Expected: PASS(6/6)

- [ ] **Step 5: Run full suite, no regression**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 477 PASS / 0 FAIL / 1 SKIP(469 + 2 + 6 + 5 - 5 MainVM cache from T6 = 477)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelMenuTests.cs
git commit -m "feat(wpf): MainViewModel 6 个 menu command + 测试 seam (v0.6.5.21 part 6)"
```

---

### Task 7: `MainWindow.xaml` 加顶部 Menu + 调整 Grid.Row

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml`

- [ ] **Step 1: Implement `MainWindow.xaml`**

把现有 `<Grid>` 改成:

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="200" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <!-- 顶部菜单条(新) -->
    <Menu Grid.Row="0" Grid.ColumnSpan="2">
        <MenuItem Header="_文件(_F)">
            <MenuItem Header="_保存环境..."
                      Command="{Binding SaveUiPreferencesCommand}" />
            <MenuItem Header="_加载环境..."
                      Command="{Binding LoadUiPreferencesCommand}" />
            <Separator />
            <MenuItem Header="打开项目文件夹"
                      Command="{Binding OpenProjectFolderCommand}" />
            <MenuItem Header="查看日志目录"
                      Command="{Binding OpenLogFolderCommand}" />
            <Separator />
            <MenuItem Header="_退出"
                      Command="{Binding ExitAppCommand}" />
        </MenuItem>
        <MenuItem Header="_设置(_S)">
            <MenuItem Header="设置..."
                      Command="{Binding ShowSettingsCommand}" />
        </MenuItem>
        <MenuItem Header="_关于(_H)">
            <MenuItem Header="关于 ComfyUI Manager..."
                      Command="{Binding ShowAboutCommand}" />
        </MenuItem>
    </Menu>

    <!-- 现有侧边栏:Grid.Row 0 → Grid.Row 1 -->
    <Border Grid.Row="1" Grid.Column="0"
            Background="{StaticResource SurfaceBrush}"
            Padding="8">
        <!-- 现有侧栏 StackPanel 不变 -->
        ...
    </Border>

    <!-- 现有内容区:Grid.Row 0 → Grid.Row 1 -->
    <ContentControl Grid.Row="1" Grid.Column="1"
                    Content="{Binding CurrentView}" />

    <!-- 现有 ErrorBanner:Grid.Row 1 → Grid.Row 2 -->
    <views:ErrorBanner Grid.Row="2" Grid.ColumnSpan="2"
                       DataContext="{Binding ErrorBanner}"
                       MaxHeight="120" />
</Grid>
```

(G2 助记:`_文件(_F)` 显示为"文件(_F)",Alt+F 展开;同理 `_设置(_S)` / `_关于(_H)`)

- [ ] **Step 2: Build verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/MainWindow.xaml
git commit -m "feat(wpf): MainWindow 顶部 Menu 条 + Grid.Row 顺移 (v0.6.5.21 part 7)"
```

---

### Task 8: `MainWindow.xaml.cs` 应用 UiPreferences(SourceInitialized / Closing)+ `App.OnStartup` wire

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`

**Step 1: Modify `MainWindow.xaml.cs`**

```csharp
using System;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class MainWindow : Window
{
    private UiPreferences? _startupPrefs;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    /// <summary>
    /// App.OnStartup 在 Show() 之前调一次,把启动 prefs 存进 Window 实例,
    /// SourceInitialized 时把 Width/Height/Left/Top/Maximized 应用上(G8)。
    /// </summary>
    public void ApplyStartupPreferences(UiPreferences prefs)
    {
        _startupPrefs = prefs;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var p = _startupPrefs;
        if (p is null) return;

        // 位置越界(多显示器移除场景)→ 退到 (100,100);尺寸合法性同理
        var left = p.WindowLeft ?? 100;
        var top = p.WindowTop ?? 100;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        if (left < 0 || left > vw - 100) left = 100;
        if (top < 0 || top > vh - 50) top = 100;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;

        if (p.WindowWidth is double w && w >= 200) Width = w;
        if (p.WindowHeight is double h && h >= 150) Height = h;
        if (p.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 写回 prefs(G8)— 只读当前 Window 状态(简化版,完整版本由
        // LastSelectedEnvId 在 MainViewModel 维护)
        var svc = App.UiPreferencesService;
        if (svc is null || _startupPrefs is null) return;
        var write = new UiPreferences
        {
            // WindowState 还原(避免记 Normal 时存 Maximized 还原时又把窗口当 Normal)
            WindowWidth = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowWidth : Width,
            WindowHeight = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowHeight : Height,
            WindowLeft = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowLeft : Left,
            WindowTop = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowTop : Top,
            WindowMaximized = WindowState == WindowState.Maximized,
            SidebarVisible = _startupPrefs.SidebarVisible,
            LastSelectedEnvId = _startupPrefs.LastSelectedEnvId,
            LastViewName = _startupPrefs.LastViewName,
        };
        svc.SaveToFile(svc.DefaultPath, write);
    }
}
```

**Step 2: Modify `App.xaml.cs`**

在 `OnStartup` 现有流程的 `var logger = new AppLogger(projectRoot);` 之后、`var envRepo = new EnvironmentRepository(dbFactory);` 之前插入:

```csharp
// v0.6.5.21: 启动加载 UI 偏好,挂到静态属性,MainWindow code-behind 读
var uiPrefsService = new UiPreferencesService(projectRoot, logger);
UiPreferencesService = uiPrefsService;
var uiPrefs = uiPrefsService.LoadFromFile(uiPrefsService.DefaultPath);
```

(把当前 line ~30 `var dbFactory = new SqliteConnectionFactory();` 移到 `uiPrefs` 加载之后,保持 flow 一致)

在 `var main = new MainWindow { DataContext = _mainVm };` 这一行之前(`main.Show()` 之前):

```csharp
main.ApplyStartupPreferences(uiPrefs);
```

并修改 `MainViewModel` 构造调用:把 `uiPrefsService` 作为新参数传入(在 `systemInfoCollector` 之后):

```csharp
_mainVm = new MainViewModel(
    dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, envDeleter, settingsRepo, gitProxy,
    settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller,
    profileLoader, BuildPyTorchVersionDirectory(appDataDir, http), appDataDir, projectRoot,
    requirementsInstaller, systemInfoCollector,
    uiPrefsService);  // v0.6.5.21 新增
```

并在 `App` 类加静态属性(供 MainWindow.Closing 读):

```csharp
public static UiPreferencesService? UiPreferencesService { get; private set; }
```

- [ ] **Step 3: Build verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 4: Run full suite, no regression**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 477 PASS / 0 FAIL / 1 SKIP(未改 VM 测试)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/MainWindow.xaml.cs src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "feat(wpf): MainWindow 应用 UiPreferences + App wire uiPrefsService (v0.6.5.21 part 8)"
```

---

### Task 9: `assets/wechat-donate.png` 占位 + 全量 verify + 重建 staging

**Files:**
- Create: `assets/.gitkeep`(空文件占位;真实 png 用户后续提供)

- [ ] **Step 1: Create `assets/` directory placeholder**

Run: `mkdir -p assets && touch assets/.gitkeep`(bash 等价;若 Git Bash 没 touch,用 `echo>` 替代)

- [ ] **Step 2: Build + full test suite**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` → 0 errors / 0 warnings
Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 477 PASS / 0 FAIL / 1 SKIP(469 base + 2 UiPrefs + 6 UiPrefsService + 5 AboutVM + 6 MenuVM - 11 MainVM cache adjustments = 477)

- [ ] **Step 3: Commit assets placeholder**

```bash
git add assets/.gitkeep
git commit -m "chore(wpf): v0.6.5.21 assets/ 占位 + 等用户提供 wechat-donate.png"
```

- [ ] **Step 4: Rebuild staging per `feedback_staging_self_contained.md`**

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

(需要先确保用户桌面 staging exe 没锁 dll — `tasklist //FI "IMAGENAME eq ComfyUI.Manager.exe" //FO LIST`)

- [ ] **Step 5: Verify git status clean**

Run: `git status --short` → 应当只有 release/staging 的 exe 时间戳变动(gitignored)+ 无 untracked

- [ ] **Step 6: 无 v-bump / 无 release zip / 无 ledger commit**

---

## Verification

### 单元测试

`dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 **477 PASS / 0 FAIL / 1 SKIP**(基线 469 + 2 UiPreferencesSerialization + 6 UiPreferencesService + 5 AboutDialogViewModel + 6 MainViewModelMenu - 11 待核)

### 端到端手动测试(用户 desktop,per `feedback_no_zip.md` 走 staging exe)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 看到顶部菜单条 3 项:文件 / 设置 / 关于
3. **Alt+F** 展开文件菜单 → 显示 5 项(保存/加载/打开项目/查看日志/退出)+ 2 separator
4. **Alt+S** 展开设置菜单 → 1 项"设置..."→ 等价侧栏设置按钮
5. **Alt+H** 展开关于菜单 → 1 项"关于 ComfyUI Manager..."→ 弹模态 AboutDialog:
   - 标题"ComfyUI Manager" + 版本号(从 assembly 读)
   - 描述 + 授权 MIT + 仓库 hyperlink(fogyisland/ComfyUIEnvironmentManagement)+ 问题反馈 hyperlink
   - 二维码区域:png 缺位 → 显示"二维码未配置,请联系作者"占位
   - Esc 关闭
6. **文件 > 打开项目文件夹** → 资源管理器打开项目根目录
7. **文件 > 查看日志目录** → 资源管理器打开 `<projectRoot>/Logs`(不存在则自动建)
8. **文件 > 保存环境** → 弹 SaveFileDialog → 默认 `ui-preferences.json` → 保存到 `<projectRoot>/config/` → 退出重启应用 → 窗口尺寸/位置恢复
9. **退出** → 应用关闭(等同点 ✕)
10. **侧栏按钮** → 全部仍可点(并存,菜单不替换)

### Risks 端到端验证

- 删除 `config/ui-preferences.json` 重启 → 应用正常启动,UI 用默认尺寸
- 把 `wechat-donate.png` 改名/删除 → AboutDialog 显示"二维码未配置"占位,不崩
- 改坏 `ui-preferences.json` 内容(如 `{`)→ 重启正常,只 log 一行 ERROR
- 多显示器场景:把 `WindowLeft` 设成负数 → 退到 (100,100)

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/UiPreferences.cs`(new)
- `src-wpf/ComfyUI.Manager/Services/UiPreferencesService.cs`(new)
- `src-wpf/ComfyUI.Manager/ViewModels/AboutDialogViewModel.cs`(new)
- `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml` + `.xaml.cs`(new)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml`(modify:加 Menu + Grid.Row 调整)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`(modify:SourceInitialized / Closing)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(modify:6 个 menu command + ctor)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(modify:wire uiPrefsService)
- `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` + `Strings.resx`(modify:6 key)
- `tests-wpf/.../Models/UiPreferencesSerializationTests.cs`(new)
- `tests-wpf/.../Services/UiPreferencesServiceTests.cs`(new)
- `tests-wpf/.../ViewModels/AboutDialogViewModelTests.cs`(new)
- `tests-wpf/.../ViewModels/MainViewModelMenuTests.cs`(new)
- `assets/.gitkeep`(new)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 9 task + 1 close-out ≈ 10 dispatch
- Per-task review gate(sonnet implementer + sonnet reviewer)
- 估计 9 commit + final review,主线推进快
- T1-T5 数据层 / DTO / VM / 对话框可独立测试;T6 接入 MainVM; T7-T8 UI wire; T9 收尾

(Plan agent left out:spec 已 close-out 所有二义点,无需 design pass;range ≈ 9 task × 单测为主 + T7/T8 UI verify 走 GUI smoke)
