# ComfyUIManagement v1.0.0 Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship ComfyUIManagement v1.0.0 as a green-zip Windows release with first-run wizard, pre-filled catalog-cache.db, runtime resources (Python + Git + ComfyUI template), and greyed-out (disabled) Workflow + Model marketplace sidebar entries.

**Architecture:** Reuse existing `scripts/build_release.ps1` as the spine — extend it to: (a) generate `catalog-cache.db` from the live ComfyUI-Manager `custom-node-list.json`, (b) bundle first-run wizard + supporting files into the zip, (c) emit README + uninstall + Start Menu helper scripts. UI changes: (1) `MainWindow.xaml` adds `IsEnabled="False"` + ToolTip to Workflow/Model marketplace RadioButtons; (2) `App.xaml.cs` shows `FirstRunWizardWindow` instead of `MainWindow` when `%APPDATA%/ComfyUI-Manager/settings.json` is missing or empty, then writes a `.first-run-complete` sentinel so it never runs again. Bump `ComfyUI.Manager.csproj` `<Version>` to `1.0.0`.

**Tech Stack:** WPF .NET 8 / C# 12 / xUnit / PowerShell (build scripts) / Python (catalog pre-fill) / SQLite (`catalog-cache.db`).

**Spec:** This plan embeds the spec inline (Global Constraints + task descriptions). User-approved design captured 2026-08-22 in brainstorming.

## Global Constraints

- **Brand:** `ComfyUIManagement` (no space). Internal product namespace stays `ComfyUI.Manager` (no rename — would touch every file).
- **Version:** `1.0.0` (csproj `<Version>` → AssemblyVersion/FileVersion → AppVersionInfo splash).
- **Zip + folder name:** `ComfyUIManagement-v1.0.0-win-x64.zip` containing `ComfyUIManagement/` folder (no space). Exe inside still named `ComfyUI.Manager.exe` (csproj `<AssemblyName>` unchanged).
- **Grey-out scope:** ONLY "工作流库" (Workflows) and "模型市场" (Models) sidebar RadioButtons get `IsEnabled="False"` + `ToolTip="将在后续版本提供"`. All other 7 sidebar items unchanged.
- **API keys:** All token fields default to empty string. No tokens seeded into `settings.json`. `Settings.CivitAiApiToken`, `Settings.ModelScopeApiToken`, `Settings.HuggingFaceApiToken` (if exists) all empty in shipped settings.json.
- **Default directories:** All path fields already default to project-relative subdirs via `SettingsDefaults.Apply` (TemplatePythonDir="Python", TemplateComfyuiDir="ComfyUITemplate", EnvsDir="" empty-until-user-configures, GlobalNodesDir="" empty, LocalNodeDirectory="local-nodes", WorkflowsDirectory="workflows", DefaultModelsDirectory="models"). **Behavior unchanged** — they already do this. The wizard is what prompts the user when these are empty/invalid.
- **Runtime resources bundled:** `Python/` (portable Python), `Embeded/git-portable/` (git.exe + cmd/), `ComfyUITemplate/` (template source, v1.0.0+ 从 `ComfyUI/` 重命名) — already in `build_release.ps1`. **No change.**
- **Seeded data:**
  - `Settings.CommonNodes` (10 curated) — already seeded by `SeedCommonNodesIfEmpty` at first load. **No change.**
  - `catalog-cache.db` — **NEW**: pre-fill from live `custom-node-list.json` + per-node GitHub releases via existing `seed_versions.py` pattern. Baked into release zip at `ComfyUIManagement/data/catalog-cache.db` (relative to exe). App reads existing `catalog-cache.db` if shipped; otherwise creates fresh.
- **First-run wizard:** 3-step modal window. Step 1 "Welcome + choose install location" → Step 2 "Python interpreter" (Browse + verify) → Step 3 "Confirm + write settings". Triggered when `%APPDATA%/ComfyUI-Manager/settings.json` is missing or `0 bytes`. Writes a sentinel `%APPDATA%/ComfyUI-Manager/.first-run-complete` after completion so it never re-triggers. If user cancels, app exits gracefully (no half-config).
- **Extras in zip:** `README.md` (quick start + paths explained), `uninstall.bat` (removes app folder + sentinel, **NOT user data**), `install-start-menu.bat` (creates Start Menu shortcut to `ComfyUI.Manager.exe`).
- **No installer (green zip):** No MSI / Inno / NSIS. User unzips and runs `ComfyUI.Manager.exe` directly.

---

## File Structure

| Path | Role |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | + 2 `IsEnabled` + `ToolTip` on Workflows/Models RadioButtons |
| `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` | `<Version>1.0.0</Version>` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | + First-run wizard branch before `MainWindow` |
| `src-wpf/ComfyUI.Manager/Views/FirstRunWizard/FirstRunWizardWindow.xaml` (.cs) | NEW 3-step modal wizard |
| `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardViewModel.cs` | NEW VM: 3 steps + navigation + settings commit |
| `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardStep.cs` | NEW enum (Welcome / Python / Confirm) |
| `src-wpf/ComfyUI.Manager/Services/FirstRun/FirstRunDetector.cs` | NEW: `IsFirstRun(string appDataDir)` + `MarkComplete(string)` |
| `scripts/build_release.ps1` | +5 lines: catalog pre-fill step, extras copy step |
| `scripts/prefill_catalog_cache.py` | NEW: fetch custom-node-list.json + per-node releases → catalog-cache.db |
| `scripts/build_release_extras.ps1` | NEW: emit README + uninstall.bat + install-start-menu.bat into AppDir |
| `release/RELEASE-NOTES-v1.0.0.md` | NEW release notes |
| `release/ComfyUIManagement-v1.0.0-win-x64.zip` | FINAL OUTPUT (built by `build_release.ps1`) |
| `tests-wpf/.../Services/FirstRunDetectorTests.cs` | NEW tests |

---

### Task 1: Sidebar grey-out (Workflows + Models)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml:113-123`

**Goal:** Disable "工作流库" and "模型市场" sidebar entries; explain via ToolTip.

- [ ] **Step 1: Edit the 2 RadioButtons**

Replace lines 113-123 of `MainWindow.xaml` so the "工作流库" and "模型市场" blocks each get `IsEnabled="False"` and `ToolTip="将在后续版本提供"` (one-line attrs). Other 7 RadioButtons untouched.

Final shape:
```xml
<RadioButton Content="工作流库" GroupName="SidebarNav"
             Command="{Binding ShowWorkflowsCommand}"
             IsEnabled="False"
             ToolTip="将在后续版本提供"
             IsChecked="{Binding CurrentSection, Converter={StaticResource SectionEquality}, ConverterParameter=Workflows, Mode=OneWay}"
             Style="{StaticResource SidebarRadioButtonStyle}" />
<RadioButton Content="模型市场" GroupName="SidebarNav"
             Command="{Binding ShowModelsCommand}"
             IsEnabled="False"
             ToolTip="将在后续版本提供"
             IsChecked="{Binding CurrentSection, Converter={StaticResource SectionEquality}, ConverterParameter=Models, Mode=OneWay}"
             Style="{StaticResource SidebarRadioButtonStyle}" />
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" -c Debug --nologo -clp:NoSummary -v:m`
Expected: `0 errors`. Pre-existing warnings unchanged.

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/MainWindow.xaml
git commit -m "feat(release): v1.0.0 grey out workflow + model marketplace sidebar"
```

---

### Task 2: Bump version to 1.0.0

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj:15`

- [ ] **Step 1: Change version**

Change `<Version>0.6.5.6</Version>` → `<Version>1.0.0</Version>`.

- [ ] **Step 2: Build to verify**

Run: `dotnet build "D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" -c Release --nologo -clp:NoSummary -v:m`
Expected: `0 errors`. `bin/Release/.../ComfyUI.Manager.dll` AssemblyVersion = `1.0.0.0`.

Verify:
```bash
powershell -NoProfile -Command "(Get-Item 'D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/ComfyUI.Manager.dll').VersionInfo.FileVersion"
```
Expected: `1.0.0.0`

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj
git commit -m "chore(release): bump to v1.0.0"
```

---

### Task 3: FirstRunDetector service + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/FirstRun/FirstRunDetector.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/FirstRunDetectorTests.cs`

**Interface (later tasks depend on this):**
```csharp
namespace ComfyUI.Manager.Services.FirstRun;
public static class FirstRunDetector
{
    public static bool IsFirstRun(string appDataDir);
    public static void MarkComplete(string appDataDir);
}
```

**Logic:**
- `IsFirstRun`: returns `true` when `%APPDATA%/ComfyUI-Manager/settings.json` is missing OR `0 bytes`, AND `%APPDATA%/ComfyUI-Manager/.first-run-complete` sentinel is missing.
- `MarkComplete`: writes the sentinel file (empty `.first-run-complete`) at `%APPDATA%/ComfyUI-Manager/.first-run-complete`. Creates the dir if missing.

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/FirstRunDetectorTests.cs`:
```csharp
using System.IO;
using ComfyUI.Manager.Services.FirstRun;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class FirstRunDetectorTests : IDisposable
{
    private readonly string _dir;
    public FirstRunDetectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"firstrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void IsFirstRun_True_WhenSettingsMissing()
    {
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_True_WhenSettingsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "");
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_False_WhenSentinelExists()
    {
        File.WriteAllText(Path.Combine(_dir, ".first-run-complete"), "");
        Assert.False(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_True_WhenSettingsPresent_NoSentinel()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        // user has settings but never completed wizard → still first run
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void MarkComplete_WritesSentinel()
    {
        FirstRunDetector.MarkComplete(_dir);
        Assert.True(File.Exists(Path.Combine(_dir, ".first-run-complete")));
        Assert.False(FirstRunDetector.IsFirstRun(_dir));
    }
}
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test "D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj" --filter "FullyQualifiedName~FirstRunDetectorTests" --nologo -clp:NoSummary -v:m`
Expected: compilation error `FirstRunDetector` not found.

- [ ] **Step 3: Implement FirstRunDetector**

Create `src-wpf/ComfyUI.Manager/Services/FirstRun/FirstRunDetector.cs`:
```csharp
using System.IO;

namespace ComfyUI.Manager.Services.FirstRun;

public static class FirstRunDetector
{
    public const string SentinelFileName = ".first-run-complete";
    public const string SettingsFileName = "settings.json";

    public static bool IsFirstRun(string appDataDir)
    {
        var sentinel = Path.Combine(appDataDir, SentinelFileName);
        if (File.Exists(sentinel)) return false;
        var settings = Path.Combine(appDataDir, SettingsFileName);
        if (!File.Exists(settings)) return true;
        var len = new FileInfo(settings).Length;
        return len == 0;
    }

    public static void MarkComplete(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        File.WriteAllText(Path.Combine(appDataDir, SentinelFileName), "");
    }
}
```

- [ ] **Step 4: Run tests — expect PASS**

Run: `dotnet test "D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj" --filter "FullyQualifiedName~FirstRunDetectorTests" --nologo -clp:NoSummary -v:m`
Expected: `5/5 PASS`.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/FirstRun/FirstRunDetector.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/FirstRunDetectorTests.cs
git commit -m "feat(release): v1.0.0 FirstRunDetector + tests"
```

---

### Task 4: FirstRunWizardViewModel + step enum

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardStep.cs`
- Create: `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/FirstRunWizardViewModelTests.cs`

**Interface (later tasks consume):**
```csharp
namespace ComfyUI.Manager.ViewModels.FirstRunWizard;
public enum FirstRunWizardStep { Welcome, Python, Confirm }
public class FirstRunWizardViewModel : INotifyPropertyChanged
{
    public FirstRunWizardStep CurrentStep { get; }
    public string InstallPath { get; set; }   // user-chosen install root
    public string PythonPath { get; set; }
    public bool IsPythonValid { get; }
    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? Completed;     // fired on Finish → window closes
    public event Action? Cancelled;     // fired on Cancel → window closes + app exits
}
```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/FirstRunWizardViewModelTests.cs`:
```csharp
using System;
using System.IO;
using ComfyUI.Manager.ViewModels.FirstRunWizard;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class FirstRunWizardViewModelTests
{
    [Fact]
    public void InitialStep_IsWelcome()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        Assert.Equal(FirstRunWizardStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void Next_FromWelcome_GoesToPython()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test" };
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Python, vm.CurrentStep);
    }

    [Fact]
    public void Next_FromWelcome_Disabled_WhenInstallPathEmpty()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Back_FromPython_GoesToWelcome()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test" };
        vm.NextCommand.Execute(null);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void Finish_FiresCompleted()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath())
        { InstallPath = "C:/test", PythonPath = "" };
        var fired = false;
        vm.Completed += () => fired = true;
        vm.NextCommand.Execute(null);  // to Python
        vm.NextCommand.Execute(null);  // to Confirm
        vm.FinishCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void Cancel_FiresCancelled()
    {
        var vm = new FirstRunWizardViewModel(appDataDir: Path.GetTempPath());
        var fired = false;
        vm.Cancelled += () => fired = true;
        vm.CancelCommand.Execute(null);
        Assert.True(fired);
    }
}
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test "D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj" --filter "FullyQualifiedName~FirstRunWizardViewModelTests" --nologo -clp:NoSummary -v:m`
Expected: compilation error.

- [ ] **Step 3: Implement enum**

Create `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardStep.cs`:
```csharp
namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

public enum FirstRunWizardStep
{
    Welcome,
    Python,
    Confirm,
}
```

- [ ] **Step 4: Implement VM**

Create `src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/FirstRunWizardViewModel.cs`:
```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

public class FirstRunWizardViewModel : INotifyPropertyChanged
{
    private readonly string _appDataDir;
    private FirstRunWizardStep _currentStep = FirstRunWizardStep.Welcome;
    private string _installPath = "";
    private string _pythonPath = "";

    public FirstRunWizardStep CurrentStep
    {
        get => _currentStep;
        private set { _currentStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsWelcome)); OnPropertyChanged(nameof(IsPython)); OnPropertyChanged(nameof(IsConfirm)); }
    }
    public bool IsWelcome => CurrentStep == FirstRunWizardStep.Welcome;
    public bool IsPython => CurrentStep == FirstRunWizardStep.Python;
    public bool IsConfirm => CurrentStep == FirstRunWizardStep.Confirm;

    public string InstallPath
    {
        get => _installPath;
        set { _installPath = value ?? ""; OnPropertyChanged(); NextCommandCanExecuteChanged(); }
    }
    public string PythonPath
    {
        get => _pythonPath;
        set { _pythonPath = value ?? ""; OnPropertyChanged(); NextCommandCanExecuteChanged(); }
    }
    public bool IsPythonValid => !string.IsNullOrWhiteSpace(_pythonPath) && System.IO.File.Exists(_pythonPath);

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action? Completed;
    public event Action? Cancelled;
    public event PropertyChangedEventHandler? PropertyChanged;

    public FirstRunWizardViewModel(string appDataDir)
    {
        _appDataDir = appDataDir;
        NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext());
        BackCommand = new RelayCommand(_ => GoBack(), _ => CurrentStep != FirstRunWizardStep.Welcome);
        FinishCommand = new RelayCommand(_ => Finish(), _ => CurrentStep == FirstRunWizardStep.Confirm);
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
    }

    private bool CanGoNext() => CurrentStep switch
    {
        FirstRunWizardStep.Welcome => !string.IsNullOrWhiteSpace(_installPath),
        FirstRunWizardStep.Python => IsPythonValid,
        FirstRunWizardStep.Confirm => false,
        _ => false,
    };

    private void GoNext()
    {
        if (CurrentStep == FirstRunWizardStep.Welcome) CurrentStep = FirstRunWizardStep.Python;
        else if (CurrentStep == FirstRunWizardStep.Python) CurrentStep = FirstRunWizardStep.Confirm;
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void GoBack()
    {
        if (CurrentStep == FirstRunWizardStep.Python) CurrentStep = FirstRunWizardStep.Welcome;
        else if (CurrentStep == FirstRunWizardStep.Confirm) CurrentStep = FirstRunWizardStep.Python;
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void Finish()
    {
        // Write settings + sentinel via detector
        var settingsPath = System.IO.Path.Combine(_appDataDir, FirstRunDetector.SettingsFileName);
        var s = System.IO.File.Exists(settingsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Models.Settings>(System.IO.File.ReadAllText(settingsPath))
            : new Models.Settings();
        if (s is null) s = new Models.Settings();
        // wizard 强制写入 user-confirmed Python 路径
        s.TemplatePythonDir = System.IO.Path.GetDirectoryName(_pythonPath) ?? "";
        s.DefaultPythonVersion = "";  // 已通过 PythonInterpreters 多解释器管理,清掉 legacy 字段
        // 把至少一条 PythonInterpreters 加进去,让 SettingsViewModel.PythonInterpreters 有内容
        if (s.PythonInterpreters.Count == 0)
        {
            s.PythonInterpreters.Add(new Models.PythonInterpreter
            {
                Name = "wizard-python",
                Path = _pythonPath,
            });
            s.ActivePythonInterpreterName = "wizard-python";
        }
        Directory.CreateDirectory(_appDataDir);
        System.IO.File.WriteAllText(settingsPath,
            System.Text.Json.JsonSerializer.Serialize(s,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        FirstRunDetector.MarkComplete(_appDataDir);
        Completed?.Invoke();
    }

    private void NextCommandCanExecuteChanged()
    {
        NextCommand.RaiseCanExecuteChanged();
        FinishCommand.RaiseCanExecuteChanged();
    }
    private void BackCommandCanExecuteChanged() => BackCommand.RaiseCanExecuteChanged();

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: Run tests — expect PASS**

Run: `dotnet test "D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj" --filter "FullyQualifiedName~FirstRunWizardViewModelTests" --nologo -clp:NoSummary -v:m`
Expected: `6/6 PASS`.

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/FirstRunWizard/ \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/FirstRunWizardViewModelTests.cs
git commit -m "feat(release): v1.0.0 FirstRunWizardViewModel + step enum + tests"
```

---

### Task 5: FirstRunWizardWindow XAML

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/FirstRunWizard/FirstRunWizardWindow.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/FirstRunWizard/FirstRunWizardWindow.xaml.cs`

**Goal:** 3-step modal window with step indicator + content panels + Back/Next/Finish/Cancel buttons. Shows one panel at a time based on `CurrentStep`.

- [ ] **Step 1: Create XAML**

Create `src-wpf/ComfyUI.Manager/Views/FirstRunWizard/FirstRunWizardWindow.xaml`:
```xml
<Window x:Class="ComfyUI.Manager.Views.FirstRunWizard.FirstRunWizardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ComfyUIManagement 首次配置"
        Width="640" Height="440"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize"
        ShowInTaskbar="False">
    <Window.Resources>
        <!-- Step indicator bold via DataTrigger(用 SemiBold 兜底避免编译期 converter 缺失)-->
        <Style x:Key="StepLabelStyle" TargetType="TextBlock">
            <Setter Property="FontWeight" Value="Normal"/>
            <Setter Property="Margin" Value="0,0,12,0"/>
        </Style>
    </Window.Resources>
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Step indicator -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,16">
            <TextBlock Text="① 欢迎" Style="{StaticResource StepLabelStyle}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource StepLabelStyle}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsWelcome}" Value="True">
                                <Setter Property="FontWeight" Value="Bold"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
            <TextBlock Text="→  ② Python 解释器" Style="{StaticResource StepLabelStyle}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource StepLabelStyle}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsPython}" Value="True">
                                <Setter Property="FontWeight" Value="Bold"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
            <TextBlock Text="→  ③ 完成">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource StepLabelStyle}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsConfirm}" Value="True">
                                <Setter Property="FontWeight" Value="Bold"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </StackPanel>

        <!-- Step content -->
        <Grid Grid.Row="1">
            <!-- Step 1: Welcome -->
            <StackPanel Visibility="{Binding IsWelcome, Converter={StaticResource BoolToVisibility}}">
                <TextBlock Text="欢迎使用 ComfyUIManagement"
                           FontSize="18" FontWeight="SemiBold" Margin="0,0,0,12"/>
                <TextBlock TextWrapping="Wrap" Margin="0,0,0,12">
                    这是首次启动,请设置安装根目录(ComfyUIManagement 把环境、模型、节点数据放在这里)。
                </TextBlock>
                <TextBlock Text="安装根目录:" Margin="0,8,0,4"/>
                <DockPanel>
                    <Button DockPanel.Dock="Right" Content="浏览..."
                            Margin="6,0,0,0" Padding="10,4"
                            Click="OnBrowseInstallPath"/>
                    <TextBox Text="{Binding InstallPath, UpdateSourceTrigger=PropertyChanged}"/>
                </DockPanel>
            </StackPanel>

            <!-- Step 2: Python -->
            <StackPanel Visibility="{Binding IsPython, Converter={StaticResource BoolToVisibility}}">
                <TextBlock Text="配置 Python 解释器"
                           FontSize="18" FontWeight="SemiBold" Margin="0,0,0,12"/>
                <TextBlock TextWrapping="Wrap" Margin="0,0,0,12">
                    请选择 Python 解释器(3.10 或更高)。ComfyUIManagement 会用它创建环境。
                </TextBlock>
                <TextBlock Text="python.exe 路径:" Margin="0,8,0,4"/>
                <DockPanel>
                    <Button DockPanel.Dock="Right" Content="浏览..."
                            Margin="6,0,0,0" Padding="10,4"
                            Click="OnBrowsePythonPath"/>
                    <TextBox Text="{Binding PythonPath, UpdateSourceTrigger=PropertyChanged}"/>
                </DockPanel>
                <TextBlock Margin="0,6,0,0">
                    <Run Text="状态: "/>
                    <Run Text="{Binding IsPythonValid, Mode=OneWay}" FontWeight="SemiBold"/>
                </TextBlock>
            </StackPanel>

            <!-- Step 3: Confirm -->
            <StackPanel Visibility="{Binding IsConfirm, Converter={StaticResource BoolToVisibility}}">
                <TextBlock Text="即将完成配置"
                           FontSize="18" FontWeight="SemiBold" Margin="0,0,0,12"/>
                <TextBlock TextWrapping="Wrap" Margin="0,0,0,8">
                    请确认以下设置,完成后 ComfyUIManagement 将启动主界面。
                </TextBlock>
                <TextBlock Margin="0,4,0,0">
                    <Run Text="安装根目录: "/><Run Text="{Binding InstallPath, Mode=OneWay}" FontWeight="SemiBold"/>
                </TextBlock>
                <TextBlock Margin="0,4,0,0">
                    <Run Text="Python 解释器: "/><Run Text="{Binding PythonPath, Mode=OneWay}" FontWeight="SemiBold"/>
                </TextBlock>
            </StackPanel>
        </Grid>

        <!-- Nav buttons -->
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="取消" Width="80" Margin="0,0,8,0"
                    Command="{Binding CancelCommand}"/>
            <Button Content="上一步" Width="80" Margin="0,0,8,0"
                    Command="{Binding BackCommand}"/>
            <Button Content="下一步" Width="80" Margin="0,0,8,0"
                    Command="{Binding NextCommand}"/>
            <Button Content="完成" Width="80"
                    Command="{Binding FinishCommand}"/>
        </StackPanel>
    </Grid>
</Window>
```

> Note: This XAML uses ONLY standard WPF + the existing `BoolToVisibility` from `Resources/Theme.xaml`. The Python validity status displays `True`/`False` (true/false strings from `Boolean.ToString()`) — good enough for v1.0; users won't read this often.

- [ ] **Step 2: Create code-behind**

Create `src-wpf/ComfyUI.Manager/Views/FirstRunWizard/FirstRunWizardWindow.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Forms;  // OpenFileDialog via WinForms
using ComfyUI.Manager.ViewModels.FirstRunWizard;
using Microsoft.Win32;       // WPF OpenFolderDialog (NET 8)

namespace ComfyUI.Manager.Views.FirstRunWizard;

public partial class FirstRunWizardWindow : Window
{
    private readonly FirstRunWizardViewModel _vm;

    public FirstRunWizardWindow(FirstRunWizardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
        vm.Cancelled += () => { DialogResult = false; Close(); };
    }

    private void OnBrowseInstallPath(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择安装根目录",
            InitialDirectory = _vm.InstallPath is { Length: > 0 } ? _vm.InstallPath : null,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.InstallPath = dlg.FolderName;
        }
    }

    private void OnBrowsePythonPath(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 python.exe",
            Filter = "Python 解释器|python.exe;python3.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.PythonPath = dlg.FileName;
        }
    }
}
```

> Note: `OpenFolderDialog` is .NET 8+. If your project targets net8.0-windows, this is available.

- [ ] **Step 3: Build + verify**

Run: `dotnet build "D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" -c Debug --nologo -clp:NoSummary -v:m`
Expected: `0 errors`. Existing converters `BoolToBold` / `BoolToChinese` / `BoolToVisibility` must exist in `Converters/` — if not, replace bindings with equivalents or add the converters.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/FirstRunWizard/
git commit -m "feat(release): v1.0.0 FirstRunWizardWindow XAML + code-behind"
```

---

### Task 6: App.xaml.cs first-run branch

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` — add first-run branch before `MainWindow` is shown.

- [ ] **Step 1: Inspect current `OnStartup` / `MainWindow` creation**

Read `App.xaml.cs` `OnStartup` method to find the exact insertion point. Likely around line 200-300 where `MainWindow = new MainWindow(...)` is set.

- [ ] **Step 2: Add first-run branch**

Before the `MainWindow = new MainWindow(...)` line, add:
```csharp
// v1.0.0:首启动 wizard
var appDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "ComfyUI-Manager");
if (FirstRun.FirstRunDetector.IsFirstRun(appDataDir))
{
    var wizardVm = new ViewModels.FirstRunWizard.FirstRunWizardViewModel(appDataDir);
    var wizard = new Views.FirstRunWizard.FirstRunWizardWindow(wizardVm);
    if (wizard.ShowDialog() != true)
    {
        // user cancelled → exit cleanly (no half-config state)
        Shutdown();
        return;
    }
    // wizard completed → settings.json + sentinel already written by VM Finish()
    // continue to MainWindow construction as usual
}
```

Add `using System.IO;` and `using System;` if not present.

- [ ] **Step 3: Build to verify**

Run: `dotnet build "D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" -c Debug --nologo -clp:NoSummary -v:m`
Expected: `0 errors`.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "feat(release): v1.0.0 first-run wizard branch in App.xaml.cs"
```

---

### Task 7: Catalog pre-fill script

**Files:**
- Create: `scripts/prefill_catalog_cache.py`

**Goal:** Generate `catalog-cache.db` (SQLite) populated with the full ComfyUI-Manager custom-node-list and per-node GitHub releases. Output goes to `bin/Release/.../publish/data/catalog-cache.db` for `build_release.ps1` to copy.

- [ ] **Step 1: Write script**

Create `scripts/prefill_catalog_cache.py`:
```python
#!/usr/bin/env python3
"""v1.0.0 release: 预填 catalog-cache.db。
    - 拉 https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json
    - 对 GitHub 仓库节点并发拉最近 10 个 release(用 GITHUB_TOKEN 环境变量加速,无 token 走匿名)
    - 写入 catalog_cache.db 的 nodes + node_versions 表(C# CatalogCacheStore schema 兼容)
"""
import json
import os
import re
import sqlite3
import sys
import time
import asyncio
import aiohttp

CATALOG_URL = "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json"
RE_GH = re.compile(r'^https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$', re.I)
MAX_VERSIONS = 10
CONCURRENCY = 8

def ensure_schema(conn):
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS nodes (
            id TEXT PRIMARY KEY,
            title TEXT,
            author TEXT,
            description TEXT,
            repository TEXT,
            html_url TEXT,
            fetched_at TEXT
        );
        CREATE TABLE IF NOT EXISTS node_versions (
            node_id TEXT NOT NULL,
            tag_name TEXT NOT NULL,
            published_at TEXT NOT NULL,
            is_prerelease INTEGER NOT NULL DEFAULT 0,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY(node_id, tag_name)
        );
        CREATE INDEX IF NOT EXISTS idx_node_versions_node
            ON node_versions(node_id, published_at DESC);
    """)
    conn.commit()

async def fetch_releases(session, repo, token):
    url = f"https://api.github.com/repos/{repo}/releases?per_page={MAX_VERSIONS}"
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "ComfyUIManagement-prefill"}
    if token: headers["Authorization"] = f"Bearer {token}"
    try:
        async with session.get(url, headers=headers, timeout=aiohttp.ClientTimeout(total=15)) as r:
            if r.status != 200: return []
            data = await r.json()
            return [(r["tag_name"], r["published_at"], int(r.get("prerelease", False)))
                    for r in data if r.get("tag_name")]
    except Exception as e:
        print(f"  [warn] {repo}: {e}", file=sys.stderr)
        return []

async def main_async():
    token = os.environ.get("GITHUB_TOKEN", "")
    print(f"fetching {CATALOG_URL} ...")
    async with aiohttp.ClientSession() as session:
        async with session.get(CATALOG_URL, timeout=aiohttp.ClientTimeout(total=30)) as r:
            catalog = await r.json()
        custom_nodes = catalog.get("custom_nodes", [])
        print(f"got {len(custom_nodes)} entries")

        repo_set = []
        for entry in custom_nodes:
            repo = entry.get("repository", "")
            if RE_GH.match(repo):
                m = RE_GH.match(repo)
                repo_set.append((entry["id"], f"{m.group(1)}/{m.group(2)}"))

        print(f"resolving releases for {len(repo_set)} GitHub repos (concurrency={CONCURRENCY})")
        sem = asyncio.Semaphore(CONCURRENCY)
        async def bounded(item):
            async with sem:
                await asyncio.sleep(0.1)  # rate-limit safety
                return item[0], await fetch_releases(session, item[1], token)

        tasks = [bounded(item) for item in repo_set]
        results = await asyncio.gather(*tasks)

    return custom_nodes, results

def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else "catalog-cache.db"
    out_dir = os.path.dirname(out_path)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    custom_nodes, release_results = asyncio.run(main_async())
    now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    conn = sqlite3.connect(out_path)
    ensure_schema(conn)
    cur = conn.cursor()
    for entry in custom_nodes:
        cur.execute(
            "INSERT OR REPLACE INTO nodes (id, title, author, description, repository, html_url, fetched_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (entry.get("id"), entry.get("title"), entry.get("author"),
             entry.get("description"), entry.get("repository"),
             entry.get("html_url"), now))
    versions_written = 0
    for node_id, releases in release_results:
        for tag, published_at, is_pre in releases:
            cur.execute(
                "INSERT OR REPLACE INTO node_versions (node_id, tag_name, published_at, is_prerelease, fetched_at) "
                "VALUES (?, ?, ?, ?, ?)",
                (node_id, tag, published_at, is_pre, now))
            versions_written += 1
    conn.commit()
    conn.close()
    print(f"wrote {len(custom_nodes)} nodes, {versions_written} versions → {out_path}")

if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Test dry-run against local test DB**

Run: `python "D:/ToolDevelop/ComfyUI/scripts/prefill_catalog_cache.py" "D:/ToolDevelop/ComfyUI/scripts/test-catalog.db"`
Expected: script runs, prints node count, version count. Takes ~5-10 min for full catalog (anonymous GitHub API rate limit).
If `aiohttp` missing: `pip install aiohttp`.

- [ ] **Step 3: Commit**

```bash
git add scripts/prefill_catalog_cache.py
git commit -m "feat(release): v1.0.0 prefill_catalog_cache.py"
```

---

### Task 8: build_release_extras.ps1 (README + uninstall + Start Menu)

**Files:**
- Create: `scripts/build_release_extras.ps1`

**Goal:** Emit 3 helper files into `AppDir/`:
- `README.md` (quick start + paths)
- `uninstall.bat` (removes app folder + sentinel, NOT user data)
- `install-start-menu.bat` (creates Start Menu shortcut via PowerShell `New-Item` + `.lnk` COM)

- [ ] **Step 1: Write script**

Create `scripts/build_release_extras.ps1`:
```powershell
# scripts/build_release_extras.ps1
# v1.0.0 release: emit helper files into AppDir.
param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = "Stop"

Write-Host "[extras] writing README.md..." -ForegroundColor Yellow
@"
# ComfyUIManagement v$Version

绿色版 WPF 应用,免安装。

## 快速开始

1. 解压到任意目录(如 `D:\Tools\ComfyUIManagement\`)
2. 双击 `ComfyUI.Manager.exe`
3. 首次启动会弹出配置向导(选择安装根目录 + Python 解释器)
4. 配置完成后进入主界面

## 目录说明

| 目录 | 说明 |
|---|---|
| `python/` | 内置 portable Python(用于创建环境)|
| `bin/git-portable/` | 内置 git(用于拉取节点仓库)|
| `ComfyUITemplate/` | ComfyUI 源模板 |
| `data/` | 节点详情缓存(catalog-cache.db) |
| `logs/` | 运行日志 |

用户配置后会在所选安装根目录下创建:`envs\`(环境)、`local-nodes\`(本地节点)、
`workflows\`(工作流)、`models\`(模型)等子目录。

## 工作流 / 模型市场

v1.0.0 暂不提供,将在后续版本发布。侧栏对应按钮为灰色不可用。

## 卸载

双击运行 `uninstall.bat`(只删除应用目录 + 配置 sentinel,**不删除**用户数据)。
如需同时清理用户数据,请手动删除 `%APPDATA%\ComfyUI-Manager\`。

## 创建开始菜单快捷方式

双击 `install-start-menu.bat`。
"@ | Set-Content (Join-Path $AppDir "README.md") -Encoding UTF8

Write-Host "[extras] writing uninstall.bat..." -ForegroundColor Yellow
@"
@echo off
setlocal
echo 即将卸载 ComfyUIManagement ...
echo.
echo 该脚本会删除:
echo   - 当前应用目录(包含 exe + python + git-portable)
echo   - %%APPDATA%%\ComfyUI-Manager\.first-run-complete
echo.
echo 不会删除:
echo   - %%APPDATA%%\ComfyUI-Manager\settings.json(用户配置)
echo   - 安装根目录下创建的 envs\workflows\models 等用户数据
echo.
set /p CONFIRM=确认卸载?(Y/N)
if /i not "%CONFIRM%"=="Y" goto :end
cd /d "%~dp0"
rd /s /q "%~dp0"
del /q "%APPDATA%\ComfyUI-Manager\.first-run-complete" 2>nul
echo.
echo 卸载完成。
:end
pause
"@ | Set-Content (Join-Path $AppDir "uninstall.bat") -Encoding ASCII

Write-Host "[extras] writing install-start-menu.bat..." -ForegroundColor Yellow
@"
@echo off
setlocal
set EXEDIR=%~dp0
set SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\ComfyUIManagement.lnk
set TARGET=%EXEDIR%ComfyUI.Manager.exe
powershell -NoProfile -Command "\$ws = New-Object -ComObject WScript.Shell; \$s = \$ws.CreateShortcut('%SHORTCUT%'); \$s.TargetPath = '%TARGET%'; \$s.WorkingDirectory = '%EXEDIR%'; \$s.Description = 'ComfyUIManagement v$Version'; \$s.Save()"
echo 开始菜单快捷方式已创建:%SHORTCUT%
pause
"@ | Set-Content (Join-Path $AppDir "install-start-menu.bat") -Encoding ASCII

Write-Host "[extras] done" -ForegroundColor Green
```

- [ ] **Step 2: Smoke-test on staging dir**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File "D:/ToolDevelop/ComfyUI/scripts/build_release_extras.ps1" -AppDir "D:/ToolDevelop/ComfyUI/release/staging/ComfyUI Manager" -Version "1.0.0"`
Expected: 3 files written; `cat` shows correct content.

- [ ] **Step 3: Commit**

```bash
git add scripts/build_release_extras.ps1
git commit -m "feat(release): v1.0.0 build_release_extras.ps1 README+uninstall+startmenu"
```

---

### Task 9: Update build_release.ps1 (version, zip name, AppDir, catalog pre-fill hook, extras hook)

**Files:**
- Modify: `scripts/build_release.ps1`

- [ ] **Step 1: Change default Version to 1.0.0**

Line 4: `[string]$Version = "0.6.0"` → `[string]$Version = "1.0.0"`.

- [ ] **Step 2: Change zip + AppDir naming to ComfyUIManagement**

Line 6: `ComfyUI-Manager-v$Version-win-x64.zip` → `ComfyUIManagement-v$Version-win-x64.zip`.
Line 14: `$AppDir = Join-Path $StageDir "ComfyUI Manager"` → `$AppDir = Join-Path $StageDir "ComfyUIManagement"`.

- [ ] **Step 3: Add catalog pre-fill step**

After step [5.5/7] (ComfyUI template), insert:
```powershell
# v1.0.0:预填 catalog-cache.db
Write-Host "[6/7] Pre-filling catalog-cache.db..." -ForegroundColor Yellow
$PublishDir = "$Root/src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
$DataDir = Join-Path $PublishDir "data"
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
$CatalogDb = Join-Path $DataDir "catalog-cache.db"
if (-not (Test-Path $CatalogDb) -or $env:REBUILD_CATALOG -eq "1") {
    python "$Root/scripts/prefill_catalog_cache.py" $CatalogDb
    if ($LASTEXITCODE -ne 0) { throw "prefill_catalog_cache.py failed" }
} else {
    Write-Host "  catalog-cache.db exists, skipping (set REBUILD_CATALOG=1 to force)" -ForegroundColor DarkGray
}
```

- [ ] **Step 4: Add extras step**

After the catalog pre-fill, before zip:
```powershell
Write-Host "[6.5/7] Emitting extras..." -ForegroundColor Yellow
& "$Root/scripts/build_release_extras.ps1" -AppDir $AppDir -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build_release_extras.ps1 failed" }
```

- [ ] **Step 5: Smoke-test build_release.ps1 locally**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File "D:/ToolDevelop/ComfyUI/scripts/build_release.ps1" -Version "1.0.0"`
Expected: zip generated at `release/ComfyUIManagement-v1.0.0-win-x64.zip`. May take 10+ min for catalog pre-fill.
If `python` not on PATH: install Python 3 or add `PYTHON_EXE` env var override.

- [ ] **Step 6: Commit**

```bash
git add scripts/build_release.ps1
git commit -m "feat(release): v1.0.0 build_release.ps1 catalog pre-fill + extras + brand rename"
```

---

### Task 10: Release notes + final smoke

**Files:**
- Create: `release/RELEASE-NOTES-v1.0.0.md`

- [ ] **Step 1: Write release notes**

Create `release/RELEASE-NOTES-v1.0.0.md`:
```markdown
# ComfyUIManagement v1.0.0 — 首个正式发布

从 v0.6.22.x 演进而来,首个以 **ComfyUIManagement** 品牌命名的 1.0 版本。

## 重大变化

- **品牌:** `ComfyUI Manager` → **`ComfyUIManagement`**(zip 与目录命名变化,内部 product namespace 保持 `ComfyUI.Manager` 不变)
- **侧栏调整:** "工作流库" 与 "模型市场" 在 v1.0.0 暂时禁用(灰色 + ToolTip "将在后续版本提供"),其余 7 项保留
- **首次启动向导:** 全新 3 步配置向导(安装根目录 → Python 解释器 → 确认),仅在首次启动(`%APPDATA%\ComfyUI-Manager\settings.json` 缺失/为空)触发,完成后写 sentinel 不再重复
- **打包形式:** 绿色版 zip(无安装器),解压即用,自带 `uninstall.bat` + `install-start-menu.bat` 辅助脚本

## 运行时资源(已包含)

- 内置 portable Python(`python/`)
- 内置 git(`bin/git-portable/`)
- 内置 ComfyUI 源模板(`ComfyUITemplate/`,v1.0.0+ 从 `ComfyUI/` 重命名)
- 预填充节点详情缓存(`Data/catalog-cache.db`,约 5000+ 节点 + GitHub releases),首启即用

## 默认配置

- 所有目录默认指向所选安装根目录的子目录(envs\local-nodes\workflows\models\python 等)
- 所有 API Key 字段为空字符串(CivitAI / ModelScope / HuggingFace),用户按需在 Settings 填写
- 10 个 curated 常用节点已 seed(`Settings.CommonNodes`),首启即可勾选安装

## 已知限制

- 工作流市场 / 模型市场:UI 入口灰显,功能将在后续版本提供

## 下载

- `release/ComfyUIManagement-v1.0.0-win-x64.zip`
```

- [ ] **Step 2: Verify zip content**

```bash
powershell -NoProfile -Command "Expand-Archive 'D:\ToolDevelop\ComfyUI\release\ComfyUIManagement-v1.0.0-win-x64.zip' -DestinationPath 'D:\Temp\verify-cm' -Force; Get-ChildItem 'D:\Temp\verify-cm\ComfyUIManagement' | Format-Table Name,Length -AutoSize"
```
Expected: `ComfyUI.Manager.exe`, `Python/`, `Embeded/git-portable/`, `ComfyUITemplate/`, `Data/catalog-cache.db`, `Logs/`, `README.md`, `uninstall.bat`, `install-start-menu.bat`.

- [ ] **Step 3: Run unzipped exe to verify wizard triggers**

```bash
powershell -NoProfile -Command "Start-Process -FilePath 'D:\Temp\verify-cm\ComfyUIManagement\ComfyUI.Manager.exe'; Start-Sleep -Seconds 3; Get-Process -Name 'ComfyUI.Manager' | Format-Table Id,StartTime"
```
Expected: process running. Close manually after visual check.

- [ ] **Step 4: Commit**

```bash
git add release/RELEASE-NOTES-v1.0.0.md
git commit -m "docs(release): v1.0.0 release notes"
```

---

### Task 11: Final smoke + verify

- [ ] **Step 1: Run full test suite**

Run: `dotnet test "D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj" --nologo -clp:NoSummary`
Expected: all PASS (or pre-existing flaky only — see memory).

- [ ] **Step 2: Verify staging matches release semantics**

Run staging from `release/staging/ComfyUIManagement/ComfyUI.Manager.exe` (rebuild staging first via `build_staging.ps1` if needed).
Expected: launches. First-run wizard triggers when `%APPDATA%\ComfyUI-Manager\settings.json` missing.

- [ ] **Step 3: Commit any final fixes**

If steps 1-2 revealed regressions, fix them and commit.

---

## Notes

- **Catalog pre-fill takes time** (~10 min for full ComfyUI-Manager catalog with anonymous GitHub API). Use `GITHUB_TOKEN` env var to halve the time. The `REBUILD_CATALOG=1` env var forces re-generation even when DB exists.
- **First-run wizard `OpenFolderDialog`** requires .NET 8 — already required by csproj (`net8.0-windows`).
- **API keys empty** — already the default. No code change needed.
- **Default directories = project folder** — already done in `SettingsDefaults.Apply`. No code change needed.
- **The first 3 sidebar items still navigate to existing views** — only Workflow/Models are blocked. If a user manually toggles `IsEnabled` via Snoop or by editing XAML, the click still does nothing useful because the source code is bundled but UI is disabled.