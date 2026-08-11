# Env-List Toggle Buttons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 env-list 操作列 4 个 install/uninstall 按钮(`装依赖`/`卸依赖`/`安装基础环境`/`卸载基础环境`)合并成 2 个 toggle 按钮,根据当前状态动态切换 label + action。toolbar "基础环境部署" 按钮删除(per-env toggle 取代)。完全复用 v0.6.11+ T4 已落地的 ComfyUI-Manager toggle 模式。

**Architecture:** 新增 2 个 `RelayCommand`(`ToggleRequirementsCommand` / `ToggleBaseEnvCommand`),内部根据 `RequirementsInstaller.IsInstalled(env)` / `BaseEnvUninstaller.IsInstalled(env)` 判断调 install 还是 uninstall 子命令。`Environment` 模型加 `RequirementsButtonText` / `BaseEnvButtonText` 字符串属性 + `IsRequirementsInstalled` / `IsBaseEnvInstalled` bool(同 ComfyUiManagerButtonText pattern,`[JsonIgnore]` 不持久化)。CanExecute 走现有 `IsEnvBusy(env)` mutex;busy 时按钮自动禁用,PropertyChanged 在 install state 变更时触发,label 同步刷新。复用现有 `RequirementsStatus` / `BaseEnvUninstallStatus` inline 状态面板做进度反馈。失败 → label 回 install/uninstall 二选一(G10:retry 走完整 install 流程)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 既有 ViewModelBase / RelayCommand / EnvironmentListViewModel 模式

**base SHA:** `a565f9c` (v0.6.11+ Remove BaseEnv sidebar SHIP-READY,818/0/1 baseline)
**spec:** `docs/superpowers/specs/2026-08-11-env-list-toggle-buttons-design.md` (`f27d25e`)

---

## Global Constraints

| # | Constraint |
|---|---|
| **G1** | 复用 ComfyUI-Manager toggle pattern (v0.6.11+ T4): `Environment` 模型加 `ButtonText` 字符串属性 + `EnvironmentListViewModel` 加 `ToggleCommand`(`RelayCommand` + `IsEnvBusy` gate);进度走 inline 状态面板(`RequirementsStatus` / `BaseEnvUninstallStatus` / `ComfyUiManagerStatus`)。不新设计 toggle UI |
| **G2** | 保留所有现有 install / uninstall 子命令接口不变:`InstallRequirementsCommand` / `UninstallRequirementsCommand` / `OpenBaseEnvProgress` / `UninstallBaseEnvCommand` 4 个 RelayCommand + 4 个 private async 方法。`MessageBoxOverride` / `ConfirmDialogOverride` / `ShowConfirmDialogOverride` / `PickerDialogOverride` / `ShowProgressDialogOverride` test seam 不动 |
| **G3** | toolbar `BaseEnvCommand` 删除(per-env toggle 取代);`BaseEnvCommand` RelayCommand property + `OpenBaseEnvProgressAsync()` 无参 helper 在 EnvListVM 中**保留**(给未来 caller);toolbar XAML 按钮(行 25-26)一定删 |
| **G4** | VM 接口冻结(扩展):不删 `OpenBaseEnvProgress` 任何 caller;`BaseEnvProfilePickerDialog` / `BaseEnvProfileLoader` / `BaseEnvInstaller` / `BaseEnvUninstaller` / `RequirementsInstaller` / `RequirementsUninstaller` 服务类不动;Settings.cs / SQLite schema 不动 |
| **G5** | 不引入新依赖;所有现有 resx / Brush / Style / Button style / 命令 pattern 复用 |
| **G6** | Toggle label 跟 ComfyUiManagerButtonText 一致用硬编码中文:在 `Environment` 模型默认值 + inline 更新处硬编码 `"装依赖"` / `"卸依赖"` / `"装依赖中..."` / `"卸依赖中..."` / `"安装基础环境"` / `"卸载基础环境"` / `"安装基础环境中..."` / `"卸载基础环境中..."`。**不进 resx**。`Strings.resx` / `Strings.zh-CN.resx` 已存在的 `EnvList_UninstallBaseEnv` / `EnvList_UninstallRequirements` / `UninstallBaseEnv_Title` / `UninstallRequirements_Title` 保留 |
| **G7** | 测试不写脆弱 UI 行为:VM 单测覆盖 toggle 命令路由 + busy 门控 + label 变更;XAML STA-thread load test 验证操作列渲染不抛;T1 implementer 必须用 fake `_installer` / `_uninstaller` 隔离(避免真跑 pip install) |
| **G8** | 每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer,严格匹配 progress.md ledger |
| **G9** | Settings 字段冻结:不动 Settings.cs / appsettings.json / 任何 UI preferences |
| **G10** | 失败 retry 走完整 install 流程:BED 失败时 button label 回 `"安装基础环境"`(不是 `"重试"`),点击走 picker dialog 同首次安装;Requirements 失败时 button label 回 `"装依赖"`,点击重跑 pip install |

---

## File Structure

**Modified (3 source + 1 test):**
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (T1:加 2 ToggleCommand + 2 private async + ctor init + label 更新点 5 处 + busy 顶部 label 4 处 + RaiseCommandsChanged 扩)
- `src-wpf/ComfyUI.Manager/Models/Environment.cs` (T1:加 2 ButtonText + 2 IsInstalled bool 属性)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (T2:删 toolbar button + 4-按钮 → 2-toggle 合并 + Grid 6 列 → 5 列)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs` (T1:新增 ~7 测试)

**Created (1 test):**
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs` (T1,~150 行:6-8 toggle 测试 + FakeBaseEnvUninstaller / FakeRequirementsInstaller helpers)

**Test files modified (1):**
- `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs` (T3:加 1 STA load test 验证 5 列操作列布局)

**未触及文件**(G2 + G4 冻结):
- `Services/BaseEnvInstaller.cs` / `BaseEnvUninstaller.cs` / `BaseEnvProfileLoader.cs` / `BaseEnvProfilePickerDialog.xaml/cs` / `RequirementsInstaller.cs` / `RequirementsUninstaller.cs`
- `Models/BaseEnvProfile.cs` / `BaseEnvUninstallStatus.cs` / `RequirementsStatus.cs` / `RequirementsStatusViewModel.cs` / `BaseEnvUninstallStatusViewModel.cs` / `ComfyUIManagerStatusViewModel.cs`
- `Settings.cs` / 所有 SQLite schema / 所有 dialogs
- `Resources/Strings.resx` / `Strings.zh-CN.resx`(G6 不新增)
- `Services/ComfyUIManagerInstaller.cs` / `Views/ComfyUIManagerStatus.xaml`(v0.6.11+ T4 已落地)

---

## Task 1: VM + Model — Add Toggle Commands and Button Text Properties

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs:60-66` (加 4 个属性)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:80-82` (ToggleCommands declaration)+ `:218-289` (ctor init)+ `:338-353` (Load label 更新)+ `:580-616` (InstallRequirementsAsync label busy + 末尾更新)+ `:695-762` (UninstallBaseEnvAsync label busy + 末尾更新)+ `:774+` (UninstallRequirementsAsync label busy + 末尾更新)+ `:1077-1083` (RaiseCommandsChanged 扩)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs`

**Interfaces (produces):**
```csharp
// In EnvironmentListViewModel.cs:
public RelayCommand ToggleRequirementsCommand { get; }
public RelayCommand ToggleBaseEnvCommand { get; }
internal async Task ToggleRequirementsAsync(Environment? env);  // 路由:IsInstalled → install/uninstall
internal async Task ToggleBaseEnvAsync(Environment? env);       // 路由:IsInstalled → install/uninstall

// In Environment.cs:
[JsonIgnore] public bool IsRequirementsInstalled { get; set; }
[JsonIgnore] public bool IsBaseEnvInstalled { get; set; }
[JsonIgnore] public string RequirementsButtonText { get; set; } = "装依赖";
[JsonIgnore] public string BaseEnvButtonText { get; set; } = "安装基础环境";
```

### Step 1: Write failing test for `Environment` model properties

在新建的 `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs` 顶部:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelToggleButtonsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelToggleButtonsTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistvm-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status = "stopped",
        string? bedStatus = null, bool writeMarker = false,
        bool writeManagerDir = false)
    {
        var root = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(root);
        var env = new Environment
        {
            Id = id, Name = id, RootPath = root,
            VenvPath = Path.Combine(root, "venv"),
            PythonExecutable = Path.Combine(root, "venv", "python.exe"),
            ComfyuiLayout = "isolated",
            ComfyuiSource = Path.Combine(root, "ComfyUI"),
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = status,
            BedStatus = bedStatus,
        };
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "SQLAlchemy");
        if (writeMarker)
            File.WriteAllText(
                Path.Combine(root, RequirementsInstaller.MarkerFileName),
                "2026-08-11T12:00:00Z");
        if (writeManagerDir)
        {
            var dir = Path.Combine(root, "ComfyUI", "custom_nodes", "ComfyUI-Manager");
            Directory.CreateDirectory(dir);
        }
        _repo.Upsert(env);
        return env;
    }

    [Fact]
    public void Model_RequirementsButtonText_DefaultsToInstallLabel()
    {
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("装依赖", env.RequirementsButtonText);
        Assert.False(env.IsRequirementsInstalled);
    }

    [Fact]
    public void Model_BaseEnvButtonText_DefaultsToInstallLabel()
    {
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("安装基础环境", env.BaseEnvButtonText);
        Assert.False(env.IsBaseEnvInstalled);
    }

    [Fact]
    public void Model_PropertiesAreJsonIgnored_NotSerialized()
    {
        // 关键:这些属性不进 SQLite,跟 IsComfyUiManagerInstalled 一致
        // 用反射 + JsonIgnore attribute 验证(避免 System.Text.Json 引入额外测试代码)
        var t = typeof(Environment);
        var reqText = t.GetProperty(nameof(Environment.RequirementsButtonText))!;
        var baseText = t.GetProperty(nameof(Environment.BaseEnvButtonText))!;
        var reqInstalled = t.GetProperty(nameof(Environment.IsRequirementsInstalled))!;
        var baseInstalled = t.GetProperty(nameof(Environment.IsBaseEnvInstalled))!;
        Assert.NotNull(reqText.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(baseText.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(reqInstalled.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(baseInstalled.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
    }
}
```

### Step 2: Run failing test (model props not yet added)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs --filter "FullyQualifiedName~Model_" -v minimal
```

Expected: **FAIL** with compile errors (properties don't exist yet) — that's OK, the build phase will catch missing properties. Move to Step 3 to add them.

### Step 3: Add 4 model properties

In `src-wpf/ComfyUI.Manager/Models/Environment.cs`, after line 66 (the existing `ComfyUiManagerButtonText` declaration), add:

```csharp
/// <summary>
/// v0.6.11+ toggle 按钮用:Requirements 是否已装(marker 文件存在)。
/// 每次 Load 末尾重新算,不持久化(同 IsComfyUiManagerInstalled pattern)。
/// </summary>
[JsonIgnore]
public bool IsRequirementsInstalled { get; set; }

/// <summary>
/// v0.6.11+ toggle 按钮文字,根据 IsRequirementsInstalled 切换。
/// </summary>
[JsonIgnore]
public string RequirementsButtonText { get; set; } = "装依赖";

/// <summary>
/// v0.6.11+ toggle 按钮用:BED 是否已装(BedStatus == "done")。
/// 每次 Load 末尾重新算,不持久化(同 IsComfyUiManagerInstalled pattern)。
/// </summary>
[JsonIgnore]
public bool IsBaseEnvInstalled { get; set; }

/// <summary>
/// v0.6.11+ toggle 按钮文字,根据 IsBaseEnvInstalled 切换。
/// </summary>
[JsonIgnore]
public string BaseEnvButtonText { get; set; } = "安装基础环境";
```

### Step 4: Run model tests to verify pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs --filter "FullyQualifiedName~Model_" -v minimal
```

Expected: **PASS** (3/3).

### Step 5: Write failing test for Toggle command routing + label updates

Append to the same test file (after Step 1 helper methods, before `Dispose()`):

```csharp
    private EnvironmentListViewModel NewSut(
        BaseEnvUninstaller? baseUninstaller = null,
        RequirementsInstaller? reqInstaller = null)
    {
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot,
            reqInstaller ?? new RequirementsInstaller(),
            baseUninstaller ?? new BaseEnvUninstaller(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// 假 RequirementsInstaller:不真跑 pip,返 canned result + 记录调用次数。
    /// 跟 EnvironmentListViewModelUninstallTests 的 FakeRequirementsUninstaller 模式对称。
    /// </summary>
    private class FakeRequirementsInstaller : RequirementsInstaller
    {
        public int InstallCallCount { get; private set; }
        public RequirementsInstallResult NextResult { get; set; } =
            new RequirementsInstallResult(true, false, null, 1);

        public FakeRequirementsInstaller() : base(null, null) { }

        public override Task<RequirementsInstallResult> InstallAsync(
            Environment env, IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            InstallCallCount++;
            logProgress?.Report("fake-install-line");
            if (NextResult.Success)
            {
                var markerPath = Path.Combine(
                    env.RootPath, RequirementsInstaller.MarkerFileName);
                try { File.WriteAllText(markerPath, "fake-ts"); } catch { }
            }
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Uninstalled_InvokesInstall()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1"); // no marker
        var fakeInstaller = new FakeRequirementsInstaller();
        var sut = NewSut(reqInstaller: fakeInstaller);

        await sut.ToggleRequirementsAsync(env);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.True(env.IsRequirementsInstalled);
        Assert.Equal("卸依赖", env.RequirementsButtonText);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Installed_InvokesUninstall()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1", writeMarker: true);
        var sut = NewSut();
        // 用真 RequirementsUninstaller:env 有 marker,卸载后删 marker
        await sut.ToggleRequirementsAsync(env);

        Assert.False(env.IsRequirementsInstalled);
        Assert.Equal("装依赖", env.RequirementsButtonText);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Busy_DisabledAndNoOp()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1");
        var fakeInstaller = new FakeRequirementsInstaller();
        var sut = NewSut(reqInstaller: fakeInstaller);

        // 手动 mark busy(模拟其他 long-running 操作占用 env)
        sut.SetEnvBusyForTest(env);
        Assert.False(sut.ToggleRequirementsCommand.CanExecute(env));

        await sut.ToggleRequirementsAsync(env);

        Assert.Equal(0, fakeInstaller.InstallCallCount);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_InstallFails_LabelStaysAtInstall()
    {
        // G10:失败 → label 回原状态(不是"重试"),按钮 enabled,点击 retry 走完整 install 流程
        using var db = new TestDb();
        var env = SeedEnv("e1");
        var fakeInstaller = new FakeRequirementsInstaller
        {
            NextResult = new RequirementsInstallResult(false, false, "fake fail", 0),
        };
        var sut = NewSut(reqInstaller: fakeInstaller);

        await sut.ToggleRequirementsAsync(env);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.False(env.IsRequirementsInstalled);
        Assert.Equal("装依赖", env.RequirementsButtonText); // 失败回 install label
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Uninstalled_InvokesOpenPicker()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1", bedStatus: null);
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);
        // PickerDialogOverride 返单 profile → 等价于用户选了安装
        sut.PickerDialogOverride = (_, _, _) =>
            new List<BaseEnvProfile> { new("test-profile", "Test", null, null, null, null) };
        // ShowProgressDialogOverride 拦截 BaseEnvProgressDialog 显示
        var progressCalled = false;
        sut.ShowProgressDialogOverride = (_, _, _) => progressCalled = true;

        await sut.ToggleBaseEnvAsync(env);

        Assert.True(progressCalled);
        Assert.True(env.IsBaseEnvInstalled);
        Assert.Equal("卸载基础环境", env.BaseEnvButtonText);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Installed_InvokesUninstall()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1", bedStatus: "done");
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);

        await sut.ToggleBaseEnvAsync(env);

        Assert.Equal(1, fakeUninstaller.CallCount);
        Assert.False(env.IsBaseEnvInstalled);
        Assert.Equal("安装基础环境", env.BaseEnvButtonText);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Busy_DisabledAndNoOp()
    {
        using var db = new TestDb();
        var env = SeedEnv("e1");
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);
        sut.SetEnvBusyForTest(env);

        Assert.False(sut.ToggleBaseEnvCommand.CanExecute(env));
        await sut.ToggleBaseEnvAsync(env);
        Assert.Equal(0, fakeUninstaller.CallCount);
    }

    [Fact]
    public void Load_PopulatesRequirementsButtonTextFromMarkerFile()
    {
        using var db = new TestDb();
        var env1 = SeedEnv("e1", writeMarker: true);
        var env2 = SeedEnv("e2"); // no marker
        var sut = NewSut();

        Assert.Equal("卸依赖", sut.Environments[0].RequirementsButtonText);
        Assert.True(sut.Environments[0].IsRequirementsInstalled);
        Assert.Equal("装依赖", sut.Environments[1].RequirementsButtonText);
        Assert.False(sut.Environments[1].IsRequirementsInstalled);
    }

    [Fact]
    public void Load_PopulatesBaseEnvButtonTextFromBedStatus()
    {
        using var db = new TestDb();
        var env1 = SeedEnv("e1", bedStatus: "done");
        var env2 = SeedEnv("e2", bedStatus: null);
        var sut = NewSut();

        Assert.Equal("卸载基础环境", sut.Environments[0].BaseEnvButtonText);
        Assert.True(sut.Environments[0].IsBaseEnvInstalled);
        Assert.Equal("安装基础环境", sut.Environments[1].BaseEnvButtonText);
        Assert.False(sut.Environments[1].IsBaseEnvInstalled);
    }

    /// <summary>
    /// 假 BED uninstaller:沿 EnvironmentListViewModelUninstallTests.cs:97 FakeBaseEnvUninstaller 模式。
    /// 跟踪 Install / Uninstall 路径。
    /// </summary>
    private class FakeBaseEnvUninstaller : BaseEnvUninstaller
    {
        public int CallCount { get; private set; }
        public Environment? LastEnv { get; private set; }
        public BaseEnvUninstallResult NextResult { get; set; } = new(
            Success: true, AlreadyUninstalled: false, EnvWasRunning: false, Reason: null);

        public override BaseEnvUninstallResult Uninstall(Environment env)
        {
            CallCount++;
            LastEnv = env;
            if (NextResult.Success && !NextResult.AlreadyUninstalled)
            {
                env.BedStatus = null;
                env.BedProfileId = null;
                env.BedFailedReason = null;
            }
            return NextResult;
        }
    }
}
```

### Step 6: Run new tests (compile error — methods not yet added)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs -v minimal
```

Expected: **FAIL** with compile errors (ToggleRequirementsCommand / ToggleRequirementsAsync / SetEnvBusyForTest / FakeRequirementsInstaller / FakeBaseEnvUninstaller etc. don't exist). Move to Step 7.

### Step 7: Add `FakeRequirementsInstaller` constructor parameter handling

`RequirementsInstaller` ctor takes `(RequirementsFileInstaller? reqFileInstaller = null, ComfyUIManagerInstaller? comfyUiManagerInstaller = null, CommonNodeInstaller? commonNodeInstaller = null)`. The `FakeRequirementsInstaller` already in Step 5 calls `base(null, null)` — verify this matches the actual ctor signature by reading `Services/RequirementsInstaller.cs` (T1 implementer MUST grep before writing).

If ctor signature differs, adjust `FakeRequirementsInstaller` ctor accordingly. The pattern is `base(<optional params>)` with `null` to skip real logic.

### Step 8: Add `SetEnvBusyForTest` test seam to EnvListVM

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`, after the existing `IsEnvBusy` method (line 61-62), add:

```csharp
/// <summary>
/// Test seam:手动 mark env 为 busy(模拟其他 long-running 操作占用)。
/// 测试用 — 让 Toggle command CanExecute 验 false 而不依赖其他 fixture 副作用。
/// </summary>
internal void SetEnvBusyForTest(Environment env)
{
    if (env is null) return;
    MarkEnvBusy(env, BusyKind.ReqInstall);
}
```

(若 reviewer 建议改方法名/位置,implementer 自由调整;关键是让测试能触发 busy state)

### Step 9: Add ToggleRequirementsCommand + ToggleBaseEnvCommand property + ctor init

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:

After line 81 (`UninstallRequirementsCommand`) add:

```csharp
/// <summary>
/// v0.6.11+ T1:env-list 行 toggle "装依赖/卸依赖" 命令 — 根据
/// IsRequirementsInstalled 切换 Install / Uninstall。复用现有
/// InstallRequirementsAsync / UninstallRequirementsAsync 子命令。
/// </summary>
public RelayCommand ToggleRequirementsCommand { get; }

/// <summary>
/// v0.6.11+ T1:env-list 行 toggle "安装基础环境/卸载基础环境" 命令 — 根据
/// IsBaseEnvInstalled 切换 Install (走 picker dialog) / Uninstall。
/// </summary>
public RelayCommand ToggleBaseEnvCommand { get; }
```

After line 288 (end of `ToggleComfyUiManagerCommand` init), add:

```csharp
        ToggleRequirementsCommand = new RelayCommand(
            async p => await ToggleRequirementsAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });

        ToggleBaseEnvCommand = new RelayCommand(
            async p => await ToggleBaseEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
```

### Step 10: Add `ToggleRequirementsAsync` + `ToggleBaseEnvAsync` private methods

After `ToggleComfyUiManagerAsync` method (line 871+, search for `internal async System.Threading.Tasks.Task ToggleComfyUiManagerAsync`), add:

```csharp
    /// <summary>
    /// v0.6.11+ T1:Requirements toggle 路由 — 已装 → uninstall,未装 → install。
    /// 复用现有 InstallRequirementsAsync / UninstallRequirementsAsync 子命令(v0.6.5.12 /
    /// v0.6.5.22 已落地),不重写 pip / uninstall 逻辑。
    /// </summary>
    internal async System.Threading.Tasks.Task ToggleRequirementsAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        if (env.IsRequirementsInstalled)
            await UninstallRequirementsAsync(env);
        else
            await InstallRequirementsAsync(env);
    }

    /// <summary>
    /// v0.6.11+ T1:BED toggle 路由 — 已装 → uninstall,未装 → 走 picker dialog install。
    /// 复用 OpenBaseEnvProgressAsync(env) per-env helper。
    /// </summary>
    internal async System.Threading.Tasks.Task ToggleBaseEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        if (env.IsBaseEnvInstalled)
            await UninstallBaseEnvAsync(env);
        else
            await OpenBaseEnvProgressAsync(env);
    }
```

### Step 11: Add `OpenBaseEnvProgressAsync(Environment env)` overload

`OpenBaseEnvProgressAsync()` at line 474 takes no param (uses `Selected`). For toggle to call with per-env, add an overload that takes `Environment` explicitly. Strategy: refactor existing method to take optional `Environment?`, default `null` = use `Selected`.

Modify line 474 signature:

```csharp
private async System.Threading.Tasks.Task OpenBaseEnvProgressAsync(Environment? targetEnv = null)
```

Then in line 476-477 replace:
```csharp
if (Selected is null && Environments.Count == 0) return;
var envIds = Selected is not null
    ? new List<string> { Selected.Id }
    : Environments.Select(e => e.Id).ToList();
```

With:
```csharp
if (targetEnv is not null)
{
    // Per-env toggle 入口:只对这个 env 弹 picker
    await OpenBaseEnvProgressForSingleAsync(targetEnv);
    return;
}
if (Selected is null && Environments.Count == 0) return;
var envIds = Selected is not null
    ? new List<string> { Selected.Id }
    : Environments.Select(e => e.Id).ToList();
```

Then add a new private method `OpenBaseEnvProgressForSingleAsync(Environment env)` that does the picker + progress dialog for a single env (extracted from existing body with `existingEnvs = [env]`).

**Important**: The new helper must also update `env.IsBaseEnvInstalled = true` + `env.BaseEnvButtonText = "卸载基础环境"` after successful install completion (in the `finally` block where `Load()` is called, before `RaiseCommandsChanged()`).

Implementer decision: either inline the single-env case in the modified method, or extract a helper. **Default**: extract a helper method to keep diff minimal and existing `BaseEnvCommand` (toolbar) behavior unchanged.

### Step 12: Add label update points in `Load()` method

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:338-353`, the `Load()` method updates `ComfyUiManagerButtonText`. Extend the foreach loop body to also compute Requirements + BED state:

```csharp
foreach (var env in Environments)
{
    var installed = _comfyUiManagerInstaller.IsInstalled(env);
    env.IsComfyUiManagerInstalled = installed;
    env.ComfyUiManagerButtonText = installed ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";

    // v0.6.11+ T1:Requirements + BED toggle state(marker 文件 / BedStatus)。
    var reqInstalled = RequirementsInstaller.IsInstalled(env);
    env.IsRequirementsInstalled = reqInstalled;
    env.RequirementsButtonText = reqInstalled ? "卸依赖" : "装依赖";

    var bedInstalled = BaseEnvUninstaller.IsInstalled(env);
    env.IsBaseEnvInstalled = bedInstalled;
    env.BaseEnvButtonText = bedInstalled ? "卸载基础环境" : "安装基础环境";
}
```

### Step 13: Add label update points in Install/Uninstall subcommands

In `InstallRequirementsAsync` (line 580-616), modify to:
- Before `MarkEnvBusy(env, BusyKind.ReqInstall)` (line 599): add `env.RequirementsButtonText = "装依赖中...";`
- In the `if (status.IsComplete && !status.HasError)` block (line 604-608), after the `await Task.Delay` and before `status.Hide()`, add:
  ```csharp
  env.IsRequirementsInstalled = true;
  env.RequirementsButtonText = "卸依赖";
  ```

In `UninstallRequirementsAsync` (line 774+), modify similarly:
- Before `MarkEnvBusy(env, BusyKind.ReqUninstall)` (line 785): add `env.RequirementsButtonText = "卸依赖中...";`
- After successful uninstall (find the success path in the existing method): add `env.IsRequirementsInstalled = false; env.RequirementsButtonText = "装依赖";`

In `OpenBaseEnvProgressAsync` (now line 474+ with overload), in the success path (after `_baseEnvInstaller.InstallAsync` returns success — find the existing success block), add `env.IsBaseEnvInstalled = true; env.BaseEnvButtonText = "卸载基础环境";` (only for the per-env helper; toolbar's BaseEnvCommand still uses existing behavior — `Load()` at the end will recompute these from BedStatus).

In `UninstallBaseEnvAsync` (line 695-762), modify:
- Before `MarkEnvBusy(env, BusyKind.BEDUninstall)` (line 704): add `env.BaseEnvButtonText = "卸载基础环境中...";`
- After `_repo.Upsert(env)` (line 747) on success path: add `env.IsBaseEnvInstalled = false; env.BaseEnvButtonText = "安装基础环境";`

**G10 失败不更新 label**:failed paths (`status.Fail` / catch blocks) 不更新 `IsXxxInstalled` / `XxxButtonText`,保留原状态。Load() 末尾重新计算保证最终一致。

### Step 14: Update `RaiseCommandsChanged()` to include new commands

In `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:1077-1083`, extend:

```csharp
        InstallRequirementsCommand.RaiseCanExecuteChanged();
        UninstallBaseEnvCommand.RaiseCanExecuteChanged();
        UninstallRequirementsCommand.RaiseCanExecuteChanged();
        // v0.6.11+ T1:toggle 命令也要 refresh,否则 busy 切换后按钮不会自动 enable/disable
        ToggleRequirementsCommand.RaiseCanExecuteChanged();
        ToggleBaseEnvCommand.RaiseCanExecuteChanged();
```

### Step 15: Run tests

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs -v minimal
```

Expected: **PASS** (all 12 tests).

If FAIL: read error, fix (likely: signature mismatch in ctor, missing `using`, wrong line numbers from refactor).

### Step 16: Verify EnvListVM-related tests still pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal
```

Expected: All existing EnvListVM tests PASS (no regression from refactor). If any FAIL, fix before commit.

### Step 17: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/Environment.cs \
       src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
       tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelToggleButtonsTests.cs
git commit -m "feat(wpf): add Requirements + BED toggle commands and dynamic labels"
```

---

## Task 2: XAML — Replace 4 Buttons with 2 Toggle Buttons + Remove Toolbar BaseEnv Button

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:25-26` (toolbar button delete)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:331-336` (Grid ColumnDefinitions 6→5)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:351-376` (4 buttons → 2 toggle + ComfyUI-Manager shift)

### Step 1: Remove toolbar "基础环境部署" button

In `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`, delete lines 25-26:

```xml
                <Button Content="基础环境部署" Command="{Binding BaseEnvCommand}"
                        Style="{StaticResource MaterialButton}" Margin="6,0,0,0" />
```

Result: toolbar (line 21-26) becomes `<Button 刷新> <Button + 新建环境>` (2 buttons instead of 3).

### Step 2: Reduce Grid ColumnDefinitions from 6 to 5

In `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:331-336`, replace:

```xml
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
```

With:

```xml
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
```

### Step 3: Replace col 2-4 (装依赖 + 卸依赖 + 卸载基础环境) with 2 toggle buttons

In `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:351-376`, replace:

```xml
                                    <Button Grid.Row="0" Grid.Column="2" Content="装依赖" Margin="2" MinWidth="0"
                                            Style="{StaticResource MaterialButton}"
                                            Command="{Binding DataContext.InstallRequirementsCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="运行 pip install -r requirements.txt(过滤 torch 行)" />
                                    <Button Grid.Row="0" Grid.Column="3" Content="卸载依赖" Margin="2" MinWidth="0"
                                            Style="{StaticResource DangerButton}"
                                            Command="{Binding DataContext.UninstallRequirementsCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="卸载 ComfyUI requirements.txt 已装的包(SQLAlchemy/einops/transformers 等,不动 torch 系列)" />
                                    <Button Grid.Row="0" Grid.Column="4" Content="卸载基础环境" Margin="2" MinWidth="0"
                                            Style="{StaticResource DangerButton}"
                                            Command="{Binding DataContext.UninstallBaseEnvCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="重置 BedStatus,保留 venv 文件,可重新部署基础环境" />
                                    <!-- v0.6.11+ T4:ComfyUI Manager toggle — 文字随每行 ComfyUiManagerButtonText
                                         动态切换(安装/卸载)。绑 ToggleComfyUiManagerCommand 显示 inline 状态面板。 -->
                                    <Button Grid.Row="0" Grid.Column="5" Content="{Binding ComfyUiManagerButtonText}" Margin="2" MinWidth="0"
                                            Style="{StaticResource MaterialButton}"
                                            Command="{Binding DataContext.ToggleComfyUiManagerCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="git clone ltdrdata/ComfyUI-Manager 到 custom_nodes 并装 requirements.txt;已装则 rm -rf 整个目录" />
```

With:

```xml
                                    <!-- v0.6.11+ T1:Requirements toggle — 已装显"卸依赖",未装显"装依赖",
                                         busy 时按钮灰 + label 变"装依赖中..."/"卸依赖中..."。
                                         复用现有 RequirementsStatus inline 面板显示进度。 -->
                                    <Button Grid.Row="0" Grid.Column="2" Content="{Binding RequirementsButtonText}" Margin="2" MinWidth="0"
                                            Style="{StaticResource MaterialButton}"
                                            Command="{Binding DataContext.ToggleRequirementsCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="运行 pip install -r requirements.txt(过滤 torch 行);已装则卸载。busy 时按钮禁用" />
                                    <!-- v0.6.11+ T1:BED toggle — 已装显"卸载基础环境",未装显"安装基础环境",
                                         busy 时按钮灰 + label 变"安装基础环境中..."/"卸载基础环境中..."。
                                         复用 BaseEnvUninstallStatus inline 面板;BED picker dialog 走 OpenBaseEnvProgressAsync。 -->
                                    <Button Grid.Row="0" Grid.Column="3" Content="{Binding BaseEnvButtonText}" Margin="2" MinWidth="0"
                                            Style="{StaticResource MaterialButton}"
                                            Command="{Binding DataContext.ToggleBaseEnvCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="走 picker dialog 装 PyTorch+CUDA 组合;已装则重置 BedStatus 保留 venv。busy 时按钮禁用" />
                                    <!-- v0.6.11+ T4:ComfyUI Manager toggle — 同 col 0-3 pattern,共用 5 列 Grid。 -->
                                    <Button Grid.Row="0" Grid.Column="4" Content="{Binding ComfyUiManagerButtonText}" Margin="2" MinWidth="0"
                                            Style="{StaticResource MaterialButton}"
                                            Command="{Binding DataContext.ToggleComfyUiManagerCommand,
                                                      RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"
                                            ToolTip="git clone ltdrdata/ComfyUI-Manager 到 custom_nodes 并装 requirements.txt;已装则 rm -rf 整个目录" />
```

**Key changes**:
- col 2-4 (3 buttons) → col 2-3 (2 toggle buttons),ComfyUI-Manager shift from col 5 to col 4
- All buttons use `MaterialButton` style(避免固定 DangerButton,因为 install + uninstall 共享按钮)
- Content bound to dynamic `RequirementsButtonText` / `BaseEnvButtonText`(per-env state)
- Command bound to `ToggleRequirementsCommand` / `ToggleBaseEnvCommand`
- ToolTip 描述当前按钮的双向功能 + busy 禁用提示

### Step 4: Build verify

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: **0 warnings / 0 errors**. If XAML parse fails, check:
- Binding paths match model property names exactly
- `RelativeSource AncestorType=UserControl` syntax correct(已有 pattern,沿用)
- Style keys exist in Theme.xaml

### Step 5: Commit

```bash
git add src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml
git commit -m "refactor(wpf): consolidate 4 buttons to 2 toggle in env-list operation row"
```

---

## Task 3: STA Load Test + Full Suite Verification

**Files:**
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs` (加 1 STA load test)

### Step 1: Add STA load test for 5-column toggle layout

Append to `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs`:

```csharp
    /// <summary>
    /// v0.6.11+ T3:操作列从 6 列变 5 列,toggle 按钮 (Requirements/BED/ComfyUI-Manager)
    /// 共享 MaterialButton style。验 headless load 不抛 XamlParseException。
    /// </summary>
    [Fact]
    public void EnvironmentListView_FiveColumnToggleRow_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new EnvironmentListView();
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"EnvironmentListView 5-col toggle layout load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }
```

### Step 2: Run STA load test

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs -v minimal
```

Expected: **PASS** (3 tests: existing 2 + new 1). If FAIL with `XamlParseException`, check Theme.xaml styles for `MaterialButton` (the existing toggle button pattern should reuse the same style).

### Step 3: Run full test suite

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

Expected: **PASS** 818 (baseline) + ~12 (T1 new) = ~830 / 0 FAIL / 1 SKIP. (Actual count varies by which flaky tests are skipped; +/- 5 acceptable per project convention.)

If FAIL count > 0:
- Read the failure trace
- If regression in unrelated test, investigate (likely unrelated flake, retry)
- If regression in EnvListVM toggle tests, fix before commit

### Step 4: Verify build clean

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: **0/0**.

### Step 5: Commit

```bash
git add tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs
git commit -m "test(wpf): STA load test for env-list 5-column toggle operation row"
```

---

## Task 4: Final Review + MEMORY + Staging Rebuild

**Files:**
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_env_list_toggle_buttons.md`
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (append 1 line)

### Step 1: Run full suite one more time on main

```bash
git log --oneline a565f9c..HEAD   # Should show: T1 commit, T2 commit, T3 commit
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build      # ~830/0/1
```

Expected: All green. If any FAIL, fix before continuing.

### Step 2: Rebuild staging

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: **0/0**, exe rebuilt. Verify `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` exists with new mtime.

### Step 3: Write MEMORY topic file

Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_env_list_toggle_buttons.md`:

```markdown
---
name: Env-List Toggle Buttons v0.6.11+
description: 3-task SDD SHIP-READY — env-list 4 按钮(装依赖/卸依赖/装基础环境/卸基础环境)合并成 2 toggle(动态 label + 动态 action + busy 禁用);toolbar "基础环境部署" 删除
type: project
originSessionId: <session-id>
---

## Quick Facts
- **base SHA**: `a565f9c` (v0.6.11+ Remove BaseEnv sidebar SHIP-READY,818/0/1 baseline)
- **HEAD**: <T1 commit> + <T2 commit> + <T3 commit>
- **Test count**: ~830 PASS / 0 FAIL / 1 SKIP (+12 net)
- **Build**: 0/0
- **Files**: 3 modified source + 2 modified test + 1 created test
- **GUI smoke**: TBD user desktop verification

## User Intent (original quotes)
- "装依赖和卸载依赖缩成一个按钮,不再需要两个按钮"
- "安装基础环境和卸载基础环境也是一个[按钮]"
- (clarified) Dynamic label (装/卸 + busy 文案)
- (clarified) Busy 时禁用 + 进度文案(跟 inline 状态面板互补)
- (clarified) Full SDD 流程

## Design Decisions
- **复用 v0.6.11+ T4 ComfyUI-Manager toggle pattern**:Environment model 加 2 ButtonText + 2 IsInstalled bool(JsonIgnore),EnvListVM 加 2 ToggleCommand(IsEnvBusy gate + 路由到 install/uninstall 子命令),XAML `Content="{Binding XxxButtonText}"` + `Command="{Binding ...ToggleCommand}"`
- **toolbar "基础环境部署" 按钮删除**:per-env toggle 取代 G3 保留 BaseEnvCommand RelayCommand as helper
- **State machine**:未装(装依赖/安装基础环境 enabled)→ busy(装依赖中.../安装基础环境中... disabled,inline 状态面板显示进度)→ 已装(卸依赖/卸载基础环境 enabled);失败 → label 回 install label(不是"重试"),retry 走完整 install 流程(G10)
- **Failure label policy (G10)**:失败不更新 label(Load() 末尾 recompute 保证最终一致)

## Spec → Plan → Implement
- spec: `docs/superpowers/specs/2026-08-11-env-list-toggle-buttons-design.md` (commit `f27d25e`)
- plan: `docs/superpowers/plans/2026-08-11-env-list-toggle-buttons.md`

## Carry-forward
- (None blocking — user 若反馈 toggle label "装依赖中..." 突兀,可改 WPF template + spinner;若想要 toggle 按钮 color 区分 uninstall DangerBrush,可加 Style Trigger)
- v0.6.5.19 + v0.6.5.19.1 IsInstalled guards(toggle 路由调子命令,guards 部分失效但 IsEnvBusy mutex 仍有效)— 后续 cleanup 可删冗余 guards

## GUI Smoke (TBD)
1. 启动 staging → env-list 操作列每行:启动/停止/[Requirements toggle]/[BED toggle]/[ComfyUI-Manager toggle](5 列)+ 调试删除链路(5 列)
2. 启动 staging → toolbar 只有 刷新 + 新建环境(2 个,无"基础环境部署")
3. 未装 env → 点 Requirements toggle → 按钮变灰 + label "装依赖中..." + inline RequirementsStatus 面板出现
4. 装完 → 按钮 enabled + label "卸依赖"
5. 点 toggle → 卸中 → 按钮变灰 + label "卸依赖中..." + inline 状态
6. 同 3-5 BED 流程
7. busy mutex:同时点 3 toggle → 只有第一个可点(其他 disabled)
8. 失败 retry:fake installer throw → GUI 验 staging 时可用 dialog "强制失败" 测试按钮

## Process Lessons
- (T1) Refactor existing `OpenBaseEnvProgressAsync()` → `OpenBaseEnvProgressAsync(Environment? targetEnv = null)` 是干净的扩展点,优于 set `Selected = env` 后调原方法
- (T1) Label update 在 4 处(Load + 3 子命令末尾)而非 1 处集中 — 跟 toggle 状态机匹配的细粒度控制
```

### Step 4: Add MEMORY.md index entry

Edit `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`, append after the most recent `v0.6.11+ Remove BaseEnv sidebar` line:

```markdown
- [Env-List Toggle Buttons](project_env_list_toggle_buttons.md) — v0.6.11+ 4 按钮 → 2 toggle (Requirements + BED), toolbar "基础环境部署" 删除; reuse ComfyUI-Manager toggle pattern
```

### Step 5: Commit MEMORY

```bash
git add "C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_env_list_toggle_buttons.md" \
       "C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md"
# Note: memory files may not be in this repo. If they're outside, skip commit.
# (Most projects keep memory outside the git tree — confirm with `ls` first.)
```

If memory files are in repo, commit. If outside repo (user's claude config dir), skip — already saved to user's machine.

### Step 6: Final summary to user

Report:
- 3 task commits completed
- Test count delta (+12 net)
- Build 0/0
- Staging rebuilt, exe path
- MEMORY topic created + index updated
- GUI smoke 8 步(用户桌面验证)

---

## Verification (end-to-end)

按顺序验证 4 task commit 全 PASS:

```bash
git log --oneline a565f9c..HEAD
# Should show:
# <T3 commit> test(wpf): STA load test for env-list 5-column toggle operation row
# <T2 commit> refactor(wpf): consolidate 4 buttons to 2 toggle in env-list operation row
# <T1 commit> feat(wpf): add Requirements + BED toggle commands and dynamic labels

dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build      # ~830/0/1
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal                        # 0/0
```

## Risks

| 风险 | 缓解 |
|---|---|
| `OpenBaseEnvProgressAsync` 签名扩展破坏现有 toolbar `BaseEnvCommand` 行为 | overload 加 `Environment? targetEnv = null` 默认参数;existing caller `BaseEnvCommand.Execute(null)` 走 Selected 路径,行为不变;per-env toggle 走 targetEnv 路径 |
| Toggle 命令路由调子命令,IsEnvBusy mutex 二次检查死锁 | 子命令顶部已有 `if (IsEnvBusy(env)) return;`,toggle 路由前 + 子命令顶部双重 check,第二层是 no-op 安全网 |
| 5 列 `*` 自动均分,但 ComfyUI-Manager 文字"安装 ComfyUI Manager"(8 字)过长 | GUI smoke 桌面验证;若 overflow 调 MinWidth 或换"装 ComfyUI Manager"(7 字);v0.6.11+ T4 同款 6 列 `*` 已工作 |
| 失败 → label 不更新(G10),用户困惑"按钮一直显示装依赖" | Load() 末尾 recompute + toggle label 已经是 state-driven;retry 时 toggle 仍可点,走完整 install;GUI smoke 验证失败状态 UX |
| `FakeRequirementsInstaller` base ctor 参数顺序/默认值与真 ctor 不匹配 | T1 implementer 必须先 grep 真 ctor 签名;若不同,调整 base ctor 调用 |
| `_envBusy` Dictionary 用 RootPath 作 key(env.Name 可能重名)— toggle 找 key 失败 | toggle 走 IsEnvBusy(env) → _envBusy[env.RootPath],同其他子命令;Per-env mutex 已有 v0.6.5.22 fix-wave 验证 |
| Test seam `SetEnvBusyForTest` 暴露 internal,可能被滥用 | `internal` 修饰 + doc comment 说明仅测试用;同 v0.6.11+ T4 `SetComfyUiManagerBusyForTest` pattern |
| EnvListVM 文件 1100+ 行,继续增长 | 本次只 +20/-10 行,远低于拆分阈值(1500+);carry-forward 标注后续如继续增长再拆 |

---

## Carry-forward

- 用户桌面验后若 toggle label 觉得突兀,可改 WPF template + Animation(Spinner)
- 用户若想要 toggle 按钮 color 区分 uninstall DangerBrush,可加 Style Trigger,工作量 +1 task
- v0.6.5.19 + v0.6.5.19.1 IsInstalled guards(toggle 内调子命令,guards 失效)— 后续 cleanup 可删冗余 guards
- 若 5 列 `*` overflow(ComfyUI-Manager 文字过长)— 改 MinWidth 或缩到 7 字

---

## Scope Check

**Focused:** VM + Model + XAML 单 view 按钮合并。无新功能,无架构变更,无 DB 变更,无 Settings 字段,无 dialog 改动,无新依赖。**单一实施 plan 覆盖完整**。

**Decompose?** 不需要。3 task 自然顺序(VM/Model → XAML → STA load test + 全套验),scope 独立。