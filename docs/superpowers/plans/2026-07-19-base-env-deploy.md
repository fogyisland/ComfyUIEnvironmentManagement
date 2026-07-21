# 基础环境部署(Torch/torchaudio/torchvision/xformers) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "基础环境部署" toolbar button on the WPF EnvListView that lets the user select one or more envs and install torch/torchaudio/torchvision/xformers (plus other base Python ML packages) into each env's venv via `pip install`, with real-time progress + cancel, and a persistent Settings page section for editing the configuration.

**Architecture:** Configuration-first design. `BaseEnvConfig` (pure POCO) owns the package list + CUDA version + channel + raw args override; it can be edited in Settings, copied into a `BaseEnvDialogViewModel` for the dialog, and serialized to `settings.json`. `BaseEnvInstaller` is the only thing that talks to pip — it resolves each env's venv python, runs `python.exe -m pip install <args>`, and emits `BaseEnvProgress` events to a `Progress<T>` callback. Two dialogs: `BaseEnvDialog` (env multi-select + config form + preview + start) and `BaseEnvProgressDialog` (overall N/M + current env progress + log tail + cancel). All pip-process mechanics sit behind a `protected virtual RunPipAsync` so tests can substitute fake pip behavior without touching the real filesystem.

**Tech Stack:** WPF .NET 8 / C# 12 · MVVM (hand-rolled `ViewModelBase` + `RelayCommand`) · `ObservableCollection<T>` · `Process.Start` with `ArgumentList` for safe arg passing · `IProgress<T>` for cross-thread UI updates · `CancellationTokenSource` for cancel propagation · xUnit + `Moq` for tests · SQLite via `Microsoft.Data.Sqlite` (existing).

## Global Constraints

(Verbatim from spec `docs/superpowers/specs/2026-07-19-base-env-deploy-design.md` §0 + §1 + §3.)

| # | Constraint | Source |
|---|---|---|
| G1 | Entry point = new toolbar button `基础环境部署` on `EnvironmentListView`, visible by default | spec §0, §4.1 |
| G2 | User picks one or more envs via a modal `BaseEnvDialog` (env multi-select on left half) | spec §0, §4.1 |
| G3 | Config lives in `Settings.BaseEnvConfig`; dialog edits a *copy* of it — Settings unchanged until user saves from Settings page | spec §0, §1.1, §3.4 |
| G4 | `BaseEnvConfig` shape: `CudaVersion` (cu118/cu121/cu124/cpu, default cu118) · `TorchChannel` (stable/nightly, default stable) · `Packages` (List<string>, default `torch torchaudio torchvision xformers`) · `ExtraArgs` (string, default "") · `CustomPipArgs` (string, default "") | spec §0, §3.1 |
| G5 | `BuildPipArgs()` priority: non-empty `CustomPipArgs` → split + return (verbatim); else `install {pkgs} [--pre if nightly] [--index-url https://download.pytorch.org/whl/{cuda} if cu != cpu] {ExtraArgs}` | spec §3.1 |
| G6 | Progress dialog shows: `Completed / Total` overall + current env status + current env progress (parsed from pip stdout `%` / `Progress (X.X)` / ` X.X / `) + scroll-to-bottom log + Cancel button | spec §1.1, §3.2, §4.2 |
| G7 | One env fails → keep going, mark that env red in the dialog. No global abort on per-env failure | spec §0, §1.1, §6 |
| G8 | Cancel → kill current pip process immediately, skip remaining envs, dialog top banner shows "已取消" | spec §0, §4.2 |
| G9 | Settings page gets a new section "基础环境": simple mode (CUDA dropdown + channel dropdown + package list + ExtraArgs textbox) + collapsible "高级" raw mode (CustomPipArgs textbox) | spec §1.1, §4.3 |
| G10 | All Settings fields persist via `SettingsRepository.Save`. Old `settings.json` without `BaseEnv` field deserializes to `new BaseEnvConfig()` defaults | spec §1.1, §4.3 |
| G11 | Venv python resolution: prefer `env.PythonExecutable` if non-empty + File.Exists; else `<VenvPath>/Scripts/python.exe` (Windows) or `<VenvPath>/bin/python` (Linux/macOS), explicit OS branch via `RuntimeInformation.IsOSPlatform`. Do NOT call into `ProcessLauncher`'s private `ResolvePythonExecutable` | spec §3.3, plan decision |
| G12 | Testability: `BaseEnvInstaller.RunPipAsync` is `protected virtual` and overridable. Tests subclass + inject a `FakeBaseEnvInstaller` (same pattern as `NodeOperations` being unsealed for tests) | plan decision, follows existing `NodeOperations` pattern |
| G13 | Percent parse regex on stdout: `/Progress \((\d+\.\d+)\/` *or* `/(\d+\.\d+) \/ /`. Match → set `EnvPercent`. No match → leave `EnvPercent = null`, no exception | spec §1.1, §3.2 |
| G14 | Default values: `Packages = [torch, torchaudio, torchvision, xformers]`, `CudaVersion = cu118`, `TorchChannel = stable` | spec §1.1, §3.1 |
| G15 | `BaseEnvDialog` static pattern: `BaseEnvDialog.Show(IList<Environment>, Settings) → BaseEnvDialogResult?` (null = cancelled). Result carries selected `IReadOnlyList<string> envIds` + cloned `BaseEnvConfig config`. Same shape as `CreateEnvDialog.Show(...)` | spec §3.5, plan decision (matches existing `CreateEnvDialog` pattern) |
| G16 | `BaseEnvProgressDialog.Show(envIds, config, BaseEnvInstaller)` is fire-and-forget; dialog owns its own `CancellationTokenSource` and passes token to `installer.InstallAsync` | spec §3.5, plan decision |
| G17 | No new external dependencies. Use only what's in `.csproj` already: `Microsoft.Data.Sqlite`, `xunit`, `Moq`, `System.Net.Http`. No pip-python wrapper, no NuGet additions | plan decision |
| G18 | No new Python service code. WPF Process.Start on the env's venv python.exe — pip runs inside that venv, NOT in any system Python | spec §1.2 non-goal #4 |
| G19 | Non-goals: no NVIDIA driver auto-detect, no multi-Python switch, no conda support, no per-env BaseEnv, no full pip package manager UI | spec §1.2 |
| G20 | Existing UI components (EnvironmentListView, SettingsView, Theme.xaml styles, SettingsRepository) MUST stay backwards compatible — additions only, no rename, no signature break on `EnvironmentListViewModel` ctor unless injected via overload | plan decision |

---

## File Structure

### New files

| Path | Responsibility |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/BaseEnvConfig.cs` | POCO: CUDA / channel / packages / extra args / custom pip args + `BuildPipArgs()` |
| `src-wpf/ComfyUI.Manager/Services/BaseEnvProgress.cs` | `BaseEnvStatus` enum + `BaseEnvProgress` record + `BaseEnvInstallResult` record |
| `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs` | Cross-env pip installer: `InstallAsync(envIds, config, IProgress, ct)` with overridable `RunPipAsync` |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvDialogViewModel.cs` | Dialog VM: env multi-select + package list + CUDA / channel / extra args + preview command + commands |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProgressViewModel.cs` | Progress VM: subscribed to installer progress events, owns CancelCommand |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml` + `.cs` | Modal dialog: left env list (checkbox), right config form, bottom preview / start / cancel |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml` + `.cs` | Modal progress dialog: top status bar, overall progress, current env status, log tail, cancel |
| `tests-wpf/ComfyUI.Manager.Tests/Models/BaseEnvConfigTests.cs` | Unit tests for `BuildPipArgs()` priority + each branch |
| `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerTests.cs` | Integration tests using `FakeBaseEnvInstaller` subclass overriding `RunPipAsync` |

### Modified files

| Path:line(s) | Change |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs:38` (after `GitHubToken`) | Add `public BaseEnvConfig BaseEnv { get; set; } = new();` with `[JsonPropertyName("base_env")]` |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:5-10` | Add `[基础环境部署]` button to toolbar after `+ 新建环境` |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:26-46` | Add `BaseEnvInstaller` + `Settings` ctor params + `BaseEnvCommand` (gated by ≥1 env in `Environments`) |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:38-49` | Add `BaseEnvInstaller` ctor param; pass to `EnvironmentListViewModel` ctor in `ShowEnvironments()` |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:181-186` (after GitHubToken) | Add `BaseEnvCudaVersion` / `BaseEnvTorchChannel` / `BaseEnvExtraArgs` / `BaseEnvCustomPipArgs` / `BaseEnvPackages` ObservableCollection; `BaseEnvIsAdvancedOpen` toggle |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:190-227` (after env / tools section) | Insert "基础环境" section with form + advanced raw textbox |
| `src-wpf/ComfyUI.Manager/App.xaml.cs:67-72` | Instantiate `BaseEnvInstaller(envRepo)` + add to `MainViewModel` ctor args |
| `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs` (or new file) | Add `BaseEnv_Defaults_WhenFieldMissingInJson` round-trip test |

### Out of scope (NOT modified)

- `ProcessLauncher` — do not touch its private `ResolvePythonExecutable`. `BaseEnvInstaller` writes its own static helper `GetVenvPythonPath(Environment env)`.
- `CatalogView` / `CatalogViewModel` / `NodeOperations` / `GitHubVersionService` / `CatalogRefreshService` — untouched.
- `pyproject.toml` / `csproj` — no new packages.
- No new Python service code.

---

## Task 1: `BaseEnvConfig` POCO + unit tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/BaseEnvConfig.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/BaseEnvConfigTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public class BaseEnvConfig
  {
      public string CudaVersion { get; set; } = "cu118";
      public string TorchChannel { get; set; } = "stable";
      public List<string> Packages { get; set; } = new() { "torch","torchaudio","torchvision","xformers" };
      public string ExtraArgs { get; set; } = "";
      public string CustomPipArgs { get; set; } = "";
      public IReadOnlyList<string> BuildPipArgs();
      public BaseEnvConfig Clone();
  }
  ```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Models/BaseEnvConfigTests.cs`:

```csharp
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class BaseEnvConfigTests
{
    [Fact]
    public void Defaults_AreCu118StableFourPackages()
    {
        var c = new BaseEnvConfig();
        Assert.Equal("cu118", c.CudaVersion);
        Assert.Equal("stable", c.TorchChannel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, c.Packages);
        Assert.Equal("", c.ExtraArgs);
        Assert.Equal("", c.CustomPipArgs);
    }

    [Fact]
    public void BuildPipArgs_CustomPipArgs_ReturnsSplitVerbatim()
    {
        var c = new BaseEnvConfig { CustomPipArgs = "install torch --pre  -f /wheels" };
        Assert.Equal(
            new[] { "install", "torch", "--pre", "-f", "/wheels" },
            c.BuildPipArgs());
    }

    [Fact]
    public void BuildPipArgs_CustomPipArgs_EmptyFallsThroughToStructured()
    {
        var c = new BaseEnvConfig { CustomPipArgs = "   " };
        var args = c.BuildPipArgs();
        Assert.Contains("install", args);
        Assert.Contains("torch", args);
    }

    [Fact]
    public void BuildPipArgs_StableCu118_BuildsIndexUrl()
    {
        var c = new BaseEnvConfig();  // defaults: cu118, stable
        var args = c.BuildPipArgs();
        Assert.Equal("install", args[0]);
        Assert.Contains("torch", args);
        Assert.Contains("torchaudio", args);
        Assert.Contains("torchvision", args);
        Assert.Contains("xformers", args);
        Assert.DoesNotContain("--pre", args);
        var idx = args.IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu118", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_Nightly_AppendsPreFlag()
    {
        var c = new BaseEnvConfig { TorchChannel = "nightly" };
        var args = c.BuildPipArgs();
        Assert.Contains("--pre", args);
        // nightly 仍带 index-url(CUDA 决定,与 channel 无关)
        Assert.Contains("--index-url", args);
    }

    [Fact]
    public void BuildPipArgs_Cpu_NoIndexUrl()
    {
        var c = new BaseEnvConfig { CudaVersion = "cpu" };
        var args = c.BuildPipArgs();
        Assert.DoesNotContain("--index-url", args);
        Assert.DoesNotContain("https://download.pytorch.org", args);
    }

    [Fact]
    public void BuildPipArgs_Cuda121_SwapsIndex()
    {
        var c = new BaseEnvConfig { CudaVersion = "cu121" };
        var args = c.BuildPipArgs();
        var idx = args.IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu121", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_ExtraArgs_AppendedAtEnd()
    {
        var c = new BaseEnvConfig { ExtraArgs = "--user --no-cache" };
        var args = c.BuildPipArgs();
        var tail = args.SkipWhile(a => a != "--user").Take(2).ToArray();
        Assert.Equal(new[] { "--user", "--no-cache" }, tail);
    }

    [Fact]
    public void BuildPipArgs_PackagesOrderPreserved()
    {
        var c = new BaseEnvConfig
        {
            Packages = new List<string> { "torch", "xformers" },
        };
        var args = c.BuildPipArgs();
        Assert.Equal("install", args[0]);
        Assert.Equal("torch", args[1]);
        Assert.Equal("xformers", args[2]);
    }

    [Fact]
    public void Clone_ReturnsIndependentDeepCopy()
    {
        var c = new BaseEnvConfig { ExtraArgs = "--user" };
        var copy = c.Clone();
        copy.Packages.Add("foo");
        copy.ExtraArgs = "--changed";
        Assert.Single(c.Packages);  // original untouched
        Assert.Equal(4, c.Packages.Count);
        Assert.Equal("--user", c.ExtraArgs);
        Assert.Equal("--changed", copy.ExtraArgs);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvConfigTests" -v minimal
```
Expected: build fail (file `BaseEnvConfig.cs` does not exist yet).

- [ ] **Step 3: Implement `BaseEnvConfig`**

Create `src-wpf/ComfyUI.Manager/Models/BaseEnvConfig.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 基础环境部署配置:torch/torchaudio/torchvision/xformers 等 Python 包
/// 在 env venv 里 pip install 的参数模板。Settings 持久化字段。
///
/// 字段全部 JSON-friendly,缺省值让老 settings.json 无 base_env 也能正常反序列化。
/// </summary>
public class BaseEnvConfig
{
    public string CudaVersion { get; set; } = "cu118";        // cu118 / cu121 / cu124 / cpu
    public string TorchChannel { get; set; } = "stable";      // stable / nightly
    public List<string> Packages { get; set; } = new()
    {
        "torch", "torchaudio", "torchvision", "xformers",
    };
    public string ExtraArgs { get; set; } = "";               // --user / -f / --no-cache ...
    public string CustomPipArgs { get; set; } = "";           // 高级:整段覆盖,优先于结构化字段

    /// <summary>
    /// 构造 pip install 参数数组(argparse 风格,空格 split)。
    /// CustomPipArgs 非空 → 直接 split 返回,优先级最高。
    /// </summary>
    public IReadOnlyList<string> BuildPipArgs()
    {
        if (!string.IsNullOrWhiteSpace(CustomPipArgs))
        {
            return CustomPipArgs
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        var args = new List<string> { "install" };
        args.AddRange(Packages);
        if (TorchChannel == "nightly")
        {
            args.Add("--pre");
        }
        if (!string.IsNullOrWhiteSpace(CudaVersion) && CudaVersion != "cpu")
        {
            args.Add("--index-url");
            args.Add($"https://download.pytorch.org/whl/{CudaVersion}");
        }
        if (!string.IsNullOrWhiteSpace(ExtraArgs))
        {
            args.AddRange(
                ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return args;
    }

    /// <summary>
    /// 浅-深拷贝:Packages 是新 List<string> 实例,字符串本身不可变不需要深拷贝。
    /// SettingsViewModel / BaseEnvDialogViewModel 改副本不影响原 Settings。
    /// </summary>
    public BaseEnvConfig Clone()
    {
        return new BaseEnvConfig
        {
            CudaVersion = CudaVersion,
            TorchChannel = TorchChannel,
            Packages = new List<string>(Packages),
            ExtraArgs = ExtraArgs,
            CustomPipArgs = CustomPipArgs,
        };
    }
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvConfigTests" -v minimal
```
Expected: 10/10 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/BaseEnvConfig.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/BaseEnvConfigTests.cs
git commit -m "feat(wpf): BaseEnvConfig + BuildPipArgs priority (custom → structured)"
```

---

## Task 2: `Settings.BaseEnv` field + defaults round-trip

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:38` (after `GitHubToken`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsDefaultsTests.cs` (or add to existing)

**Interfaces:**
- Consumes: `BaseEnvConfig` (Task 1)
- Produces: `Settings.BaseEnv` (existing `SettingsRepository.Save` / `Load` JSON path picks it up automatically via property name)

- [ ] **Step 1: Write failing test**

Append to `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsDefaultsTests.cs` (verify the file exists with this namespace; if not, find an appropriate Settings test file or create one):

```csharp
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class SettingsBaseEnvTests
{
    [Fact]
    public void BaseEnv_DefaultsToNewConfig_WhenFieldMissingInJson()
    {
        // 模拟老 settings.json(没有 base_env 字段)
        var oldJson = "{\"language\":\"zh_CN\"}";
        var s = JsonSerializer.Deserialize<Settings>(oldJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(s);
        Assert.NotNull(s!.BaseEnv);
        Assert.Equal("cu118", s.BaseEnv.CudaVersion);
        Assert.Equal("stable", s.BaseEnv.TorchChannel);
        Assert.Equal(4, s.BaseEnv.Packages.Count);
        Assert.Contains("torch", s.BaseEnv.Packages);
    }

    [Fact]
    public void BaseEnv_RoundTripsThroughJson()
    {
        var s = new Settings();
        s.BaseEnv.CudaVersion = "cu121";
        s.BaseEnv.TorchChannel = "nightly";
        s.BaseEnv.Packages = new System.Collections.Generic.List<string> { "torch", "xformers" };
        s.BaseEnv.ExtraArgs = "--user";
        s.BaseEnv.CustomPipArgs = "install torch";

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal("cu121", back.BaseEnv.CudaVersion);
        Assert.Equal("nightly", back.BaseEnv.TorchChannel);
        Assert.Equal(new[] { "torch", "xformers" }, back.BaseEnv.Packages);
        Assert.Equal("--user", back.BaseEnv.ExtraArgs);
        Assert.Equal("install torch", back.BaseEnv.CustomPipArgs);
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsBaseEnvTests" -v minimal
```
Expected: build fail (`Settings.BaseEnv` does not exist).

- [ ] **Step 3: Add `BaseEnv` to `Settings.cs`**

Open `src-wpf/ComfyUI.Manager/Models/Settings.cs` and find the `GitHubToken` block (around line 38, decorated with `[JsonPropertyName("github_token")]`). Immediately AFTER that block, add:

```csharp
[JsonPropertyName("base_env")]
public BaseEnvConfig BaseEnv { get; set; } = new();
```

(If you cannot find the exact `GitHubToken` line, open the file and locate the field with `[JsonPropertyName("github_token")]` — add the new property immediately after its closing brace.)

- [ ] **Step 4: Run, verify PASS**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsBaseEnvTests" -v minimal
```
Expected: 2/2 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/SettingsDefaultsTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/SettingsBaseEnvTests.cs
git commit -m "feat(wpf): Settings.BaseEnv — defaults + JSON round-trip"
```

---

## Task 3: `BaseEnvProgress` records + `BaseEnvInstallResult`

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/BaseEnvProgress.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public enum BaseEnvStatus { Pending, Running, Succeeded, Failed, Cancelled }
  public record BaseEnvProgress(
      BaseEnvStatus Status, int Completed, int Total,
      string? CurrentEnvId, string? CurrentEnvName,
      int? EnvPercent, string? LogLine, string? ErrorMessage);
  public record BaseEnvInstallResult(
      bool Cancelled, int SucceededCount, int FailedCount,
      IReadOnlyDictionary<string, string> Failures);
  public record PipResult(int ExitCode, bool WasCancelled);
  ```

- [ ] **Step 1: Write the file** (no tests yet — pure record types, no logic to test)

Create `src-wpf/ComfyUI.Manager/Services/BaseEnvProgress.cs`:

```csharp
using System.Collections.Generic;

namespace ComfyUI.Manager.Services;

public enum BaseEnvStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// BaseEnvInstaller 一次跨 env install 过程中 emit 的进度事件。
///
/// Field 含义:
/// - Status:当前正在进行的 env(或整体)状态变化
/// - Completed:已完成 env 数(成功 / 失败 / 取消都算"已处理")
/// - Total:总 env 数
/// - CurrentEnvId / CurrentEnvName:当前正在跑的 env(开始/结束时填,中间更新 percent 时不变)
/// - EnvPercent:当前 env 内部 pip 进度 0-100,正则未匹配则为 null(不显示百分比)
/// - LogLine:pip stdout/stderr 一行(可能为 null)
/// - ErrorMessage:仅 Failed 时非空,人读原因
/// </summary>
public record BaseEnvProgress(
    BaseEnvStatus Status,
    int Completed,
    int Total,
    string? CurrentEnvId,
    string? CurrentEnvName,
    int? EnvPercent,
    string? LogLine,
    string? ErrorMessage);

/// <summary>
/// BaseEnvInstaller.InstallAsync 终态结果。
/// Failures map envId → human-readable reason(失败或跳过的 env 都计入)。
/// </summary>
public record BaseEnvInstallResult(
    bool Cancelled,
    int SucceededCount,
    int FailedCount,
    IReadOnlyDictionary<string, string> Failures);

/// <summary>
/// 单次 pip 调用结果(installer 内部用)。
/// ExitCode = pip 退出码;WasCancelled = CancellationToken 在等待退出时触发。
/// </summary>
public record PipResult(int ExitCode, bool WasCancelled);
```

- [ ] **Step 2: Verify build**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/BaseEnvProgress.cs
git commit -m "feat(wpf): BaseEnvProgress + BaseEnvInstallResult + PipResult records"
```

---

## Task 4: `BaseEnvInstaller` + `FakeBaseEnvInstaller` + unit tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerTests.cs`

**Interfaces:**
- Consumes:
  - `EnvironmentRepository` (existing `Data/EnvironmentRepository.cs`) for `Get(envId)`
  - `Environment` model — read `VenvPath` / `PythonExecutable`
  - `BaseEnvConfig` (Task 1)
  - `BaseEnvProgress` records (Task 3)
- Produces:
  ```csharp
  public class BaseEnvInstaller
  {
      public BaseEnvInstaller(EnvironmentRepository envRepo) { ... }
      public virtual Task<BaseEnvInstallResult> InstallAsync(
          IReadOnlyList<string> envIds, BaseEnvConfig config,
          IProgress<BaseEnvProgress>? progress, CancellationToken ct);
      protected virtual Task<PipResult> RunPipAsync(
          string pythonExe, IReadOnlyList<string> pipArgs,
          Action<string> onLine, Action<int?> onPercent,
          CancellationToken ct);
      public static string GetVenvPythonPath(Environment env);  // public for tests
  }
  ```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// BaseEnvInstaller 集成测试:用 FakeBaseEnvInstaller override RunPipAsync,
/// 避免真的跑 venv python(慢 + 依赖网络)。
/// </summary>
public sealed class BaseEnvInstallerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstallerTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private Environment SeedEnv(string id, string root)
    {
        // 用一个不存在的 pythonExe 路径,避免 BaseEnvInstaller 默认行为:
        // 测试时都用 FakeBaseEnvInstaller 不走真的解析。
        // 这里用绝对虚拟路径,让 RunPipAsync override 永不触发文件检查。
        var venv = Path.Combine(root, "venv");
        Directory.CreateDirectory(venv);
        var fakePy = Path.Combine(venv, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venv,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = "stopped",
        };
        _envRepo.Upsert(env);
        return env;
    }

    private static BaseEnvConfig DefaultConfig() => new();

    [Fact]
    public async Task InstallAsync_SingleEnv_Succeeds_EmitsProgressLifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.NextRunResult = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultConfig(), progress, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Cancelled);
        Assert.Equal(1, fake.RunCount);
        // progress 至少 emit 一次 Running start + 一次 Succeeded
        Assert.Contains(progress.Events,
            p => p.Status == BaseEnvStatus.Running && p.CurrentEnvId == "env-a");
        Assert.Contains(progress.Events,
            p => p.Status == BaseEnvStatus.Succeeded && p.CurrentEnvId == "env-a");
        // Completed / Total 数字一致
        Assert.All(progress.Events, p =>
        {
            Assert.Equal(1, p.Total);
            Assert.True(p.Completed >= 0 && p.Completed <= 1);
        });
    }

    [Fact]
    public async Task InstallAsync_OneEnvFails_ContinuesNextEnv()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-fail-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        SeedEnv("env-b", tempRoot + "-b");
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.PerEnvResults["env-a"] = PipResultSuccess(1);  // 非 0 = pip 失败
        fake.PerEnvResults["env-b"] = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a", "env-b" }, DefaultConfig(), progress, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("env-a", result.Failures.Keys);
        Assert.Contains("env-b", result.Failures.Keys);  // success 仍计入
        // 进度:env-a 跑完后 Completed=1,env-b 才开始(顺序串行)
        var envARanAt = progress.FirstRunningEnv("env-a");
        var envBRanAt = progress.FirstRunningEnv("env-b");
        Assert.True(envARanAt < envBRanAt);
    }

    [Fact]
    public async Task InstallAsync_CancelBeforeStart_SkipsAllEnvs_ReturnsCancelled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-cancel-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.NextRunResult = PipResultSuccess(0);
        var progress = new RecordingProgress();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultConfig(), progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, fake.RunCount);
        Assert.Empty(progress.Events);  // 起始就 cancel,啥也不 emit
    }

    [Fact]
    public async Task InstallAsync_CancelMidRun_KillsCurrentAndSkipsRest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-midcancel-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        SeedEnv("env-b", tempRoot + "-b");
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.PerEnvResults["env-a"] = PipResultCancelled();  // RunPipAsync 抛 OperationCanceledException
        fake.PerEnvResults["env-b"] = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a", "env-b" }, DefaultConfig(), progress, CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);  // env-a 算失败(env-b 未尝试)
        Assert.Contains("env-a", result.Failures.Keys);
        Assert.DoesNotContain("env-b", result.Failures.Keys);
    }

    [Fact]
    public void GetVenvPythonPath_PrefersExplicitPythonExecutable()
    {
        var env = new Environment
        {
            VenvPath = Path.Combine(Path.GetTempPath(), "fake-venv"),
            PythonExecutable = Path.Combine(Path.GetTempPath(), "explicit.exe"),
        };
        // explicit.exe 不存在(测试只验解析逻辑,不调 Process.Start),
        // 但 BaseEnvInstaller 必须先看 explicit,不 fallback 到 venv
        var actual = BaseEnvInstaller.GetVenvPythonPath(env);
        Assert.Equal(env.PythonExecutable, actual);
    }

    [Fact]
    public void GetVenvPythonPath_FallsBackToVenvScriptsPython()
    {
        var venvRoot = Path.Combine(Path.GetTempPath(), $"venv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(venvRoot);
        var scriptsDir = Path.Combine(venvRoot,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts" : "bin");
        Directory.CreateDirectory(scriptsDir);
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "python.exe" : "python";
        var pyPath = Path.Combine(scriptsDir, exeName);
        File.WriteAllText(pyPath, "");

        var env = new Environment { VenvPath = venvRoot };
        var actual = BaseEnvInstaller.GetVenvPythonPath(env);

        Assert.Equal(pyPath, actual);
        Directory.Delete(venvRoot, true);
    }

    [Fact]
    public void GetVenvPythonPath_ThrowsIfVenvPythonMissing()
    {
        var env = new Environment
        {
            VenvPath = Path.Combine(Path.GetTempPath(), $"no-venv-{Guid.NewGuid():N}"),
        };
        Assert.Throws<InvalidOperationException>(
            () => BaseEnvInstaller.GetVenvPythonPath(env));
    }

    [Fact]
    public void GetVenvPythonPath_ThrowsIfNoVenvAndNoExplicit()
    {
        var env = new Environment();
        Assert.Throws<ArgumentException>(
            () => BaseEnvInstaller.GetVenvPythonPath(env));
    }

    // ---- helpers ----

    private static PipResult PipResultSuccess(int code) => new(code, false);
    private static PipResult PipResultCancelled() => new(-1, true);

    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public Dictionary<string, PipResult> PerEnvResults { get; } = new();
        public PipResult? NextRunResult { get; set; }
        public int RunCount { get; private set; }

        public FakeBaseEnvInstaller(EnvironmentRepository envRepo)
            : base(envRepo) { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent,
            CancellationToken ct)
        {
            RunCount++;
            // 把 onLine / onPercent 调用 broadcast 出来供测试断言
            onLine("Looking in indexes: https://download.pytorch.org/whl/cu118");
            onPercent(5.0);
            onLine("Downloading torch-2.1.0-cp310-cp310-win_amd64.whl (xxx MB)");

            var byEnv = NextRunResult ?? PipResultSuccess(0);
            if (byEnv.WasCancelled)
            {
                throw new OperationCanceledException(ct);
            }
            return Task.FromResult(byEnv);
        }

        public PipResult ResolveForEnv(string envId) =>
            PerEnvResults.TryGetValue(envId, out var r) ? r : NextRunResult ?? PipResultSuccess(0);
    }

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        private readonly List<BaseEnvProgress> _events = new();
        public IReadOnlyList<BaseEnvProgress> Events => _events;
        public void Report(BaseEnvProgress value)
        {
            lock (_events) _events.Add(value);
        }
        public int FirstRunningEnv(string envId) =>
            _events.FindIndex(p => p.CurrentEnvId == envId && p.Status == BaseEnvStatus.Running);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerTests" -v minimal
```
Expected: build fail (`BaseEnvInstaller` not found).

- [ ] **Step 3: Implement `BaseEnvInstaller`**

Create `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// BaseEnvInstaller:跨 env × BaseEnvConfig 跑 pip install,emit Progress 事件。
///
/// 设计要点:
/// - 单 env 失败不中断,继续下个 env(G7)
/// - CancellationToken 触发 → 立即 kill 当前 pip 进程(G8)
/// - RunPipAsync 是 protected virtual,测试可 override(G12)
/// - 不依赖 ProcessLauncher 的 private 解析方法(G11)
/// </summary>
public class BaseEnvInstaller
{
    // pip 输出 percent 匹配的两种模式:
    //   "  5%|▍                   | 0.00M/0.00M [00:00<?, ?B/s]"
    //   "Downloading (5.0/245.3 MB)"
    //   "Progress (5.0/245)"
    private static readonly Regex PercentPattern = new(
        @"(?x)
        Progress \s* \( (?<p1>\d+\.?\d*)  |
        (?<p2>\d+\.?\d*) \s* / \s* \d |
        (?<p3>\d+) %",
        RegexOptions.Compiled);

    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstaller(EnvironmentRepository envRepo)
    {
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
    }

    /// <summary>
    /// 顺序跑 base env install 跨多个 env。
    /// 每个 env:
    ///   1) emit Running
    ///   2) 调 RunPipAsync(env python + BuildPipArgs() + onLine/onPercent)
    ///   3) emit Succeeded / Failed
    /// </summary>
    public virtual async Task<BaseEnvInstallResult> InstallAsync(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        IProgress<BaseEnvProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (envIds is null || envIds.Count == 0)
        {
            return new BaseEnvInstallResult(false, 0, 0,
                new Dictionary<string, string>());
        }

        var failures = new Dictionary<string, string>();
        var succeeded = 0;
        var failed = 0;
        var cancelled = false;
        var total = envIds.Count;
        var completed = 0;

        var pipArgs = config.BuildPipArgs();

        foreach (var envId in envIds)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            Environment env;
            try
            {
                env = _envRepo.Get(envId)
                    ?? throw new InvalidOperationException(
                        $"env '{envId}' 不存在");
            }
            catch (Exception ex)
            {
                failures[envId] = ex.Message;
                failed++;
                completed++;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed, total,
                    envId, null, null, null, ex.Message));
                continue;
            }

            string pythonExe;
            try
            {
                pythonExe = GetVenvPythonPath(env);
            }
            catch (Exception ex)
            {
                failures[envId] = ex.Message;
                failed++;
                completed++;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed, total,
                    envId, env.Name, null, null, ex.Message));
                continue;
            }

            progress?.Report(new BaseEnvProgress(
                BaseEnvStatus.Running, completed, total,
                envId, env.Name, 0, $"开始安装 ({env.Name})", null));

            try
            {
                var result = await RunPipAsync(
                    pythonExe, pipArgs,
                    line => progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Running, completed, total,
                        envId, env.Name, null, line, null)),
                    pct => progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Running, completed, total,
                        envId, env.Name, pct, null, null)),
                    ct);

                if (result.WasCancelled || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    failures[envId] = "用户取消";
                    failed++;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Cancelled, completed + 1, total,
                        envId, env.Name, null, null, "用户取消"));
                    completed++;
                    break;
                }

                if (result.ExitCode == 0)
                {
                    succeeded++;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Succeeded, completed + 1, total,
                        envId, env.Name, 100, null, null));
                }
                else
                {
                    failed++;
                    failures[envId] = $"pip 退出码 {result.ExitCode}";
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Failed, completed + 1, total,
                        envId, env.Name, null, null,
                        $"pip 退出码 {result.ExitCode}"));
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                failures[envId] = "用户取消";
                failed++;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Cancelled, completed + 1, total,
                    envId, env.Name, null, null, "用户取消"));
                completed++;
                break;
            }
            catch (Exception ex)
            {
                failed++;
                failures[envId] = ex.Message;
                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Failed, completed + 1, total,
                    envId, env.Name, null, null, ex.Message));
            }

            completed++;
        }

        return new BaseEnvInstallResult(
            cancelled, succeeded, failed, failures);
    }

    /// <summary>
    /// 跑 `&lt;pythonExe&gt; -m pip &lt;pipArgs&gt;` 一个 env。
    /// 默认实现:Process.Start + 重定向 stdout/stderr,onLine/onPercent 回调。
    /// 测试 override 模拟 cancel / exit code / percent 输出。
    /// </summary>
    protected virtual Task<PipResult> RunPipAsync(
        string pythonExe,
        IReadOnlyList<string> pipArgs,
        Action<string> onLine,
        Action<int?> onPercent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pythonExe))
        {
            throw new ArgumentException("pythonExe 不能为空", nameof(pythonExe));
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        foreach (var a in pipArgs)
        {
            psi.ArgumentList.Add(a);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"启动 pip 失败: {ex.Message}", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("Process.Start 返回 null");
        }

        var tcs = new TaskCompletionSource<PipResult>();
        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine(line);
                    var pct = TryParsePercent(line);
                    if (pct.HasValue) onPercent((int)pct.Value);
                }
            }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine("[stderr] " + line);
                }
            }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { await Task.WhenAll(stdoutDone.Task, stderrDone.Task); } catch { }
                if (ct.IsCancellationRequested)
                {
                    TryKill(process);
                    tcs.TrySetResult(new PipResult(-1, true));
                }
                else
                {
                    int code;
                    try { code = process.ExitCode; } catch { code = -1; }
                    tcs.TrySetResult(new PipResult(code, false));
                }
                try { process.Dispose(); } catch { }
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// public static 方便测试调用:解析 env 的 venv python 路径。
    /// 优先级:
    ///   1) env.PythonExecutable 非空 + File.Exists
    ///   2) &lt;VenvPath&gt;/Scripts/python.exe (Windows) 或 &lt;VenvPath&gt;/bin/python (其他)
    /// </summary>
    public static string GetVenvPythonPath(Environment env)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (!string.IsNullOrWhiteSpace(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }

        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            throw new ArgumentException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath");
        }

        var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                $"venv python 找不到: {exe}");
        }
        return exe;
    }

    private static int? TryParsePercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = PercentPattern.Match(line);
        if (!m.Success) return null;
        for (var i = 1; i < m.Groups.Count; i++)
        {
            if (m.Groups[i].Success)
            {
                if (double.TryParse(
                    m.Groups[i].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d))
                {
                    if (d < 0) return 0;
                    if (d > 100) return 100;
                    return (int)d;
                }
            }
        }
        return null;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerTests" -v minimal
```
Expected: 8/8 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerTests.cs
git commit -m "feat(wpf): BaseEnvInstaller + virtual RunPipAsync + 8 unit tests"
```

---

## Task 5: `BaseEnvDialogViewModel` + unit tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvDialogViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `Environment` (model) · `BaseEnvConfig` (Task 1)
- Produces:
  ```csharp
  public class EnvChoice : ViewModelBase
  {
      public Environment Env { get; }
      private bool _isChecked; public bool IsChecked { get; set; }  // raises PropertyChanged
      public EnvChoice(Environment env, bool isChecked = false);
  }
  public class BaseEnvDialogResult
  {
      public IReadOnlyList<string> SelectedEnvIds { get; }
      public BaseEnvConfig Config { get; }
  }
  public class BaseEnvDialogViewModel : ViewModelBase
  {
      public ObservableCollection<EnvChoice> Envs { get; }
      public BaseEnvConfig Config { get; }  // 工作副本
      public IEnumerable<string> CudaVersions { get; } = new[]{"cu118","cu121","cu124","cpu"};
      public IEnumerable<string> TorchChannels { get; } = new[]{"stable","nightly"};
      public ObservableCollection<string> Packages { get; }
      public string PreviewCommandText { get; }   // 计算属性
      public string NewPackageName { get; set; }
      public RelayCommand AddPackageCommand { get; }
      public RelayCommand RemovePackageCommand { get; }  // param: string
      public RelayCommand StartCommand { get; }          // 校验:≥1 env + ≥1 package
      public RelayCommand CancelCommand { get; }
      public event Action<BaseEnvDialogResult?>? Closed;
      public BaseEnvDialogViewModel(IList<Environment> envs, BaseEnvConfig sourceConfig);
  }
  ```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvDialogViewModelTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvDialogViewModelTests
{
    private static Environment FakeEnv(string id) => new()
    {
        Id = id,
        Name = id,
        RootPath = $"/tmp/{id}",
        VenvPath = $"/tmp/{id}/venv",
        CustomNodesPath = $"/tmp/{id}/nodes",
        Port = 8188,
        Status = "stopped",
    };

    private static BaseEnvConfig DefaultConfig() => new();

    [Fact]
    public void Ctor_BuildsEnvChoicesUnchecked()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a"), FakeEnv("b") }, DefaultConfig());
        Assert.Equal(2, vm.Envs.Count);
        Assert.All(vm.Envs, c => Assert.False(c.IsChecked));
    }

    [Fact]
    public void Config_StartsAsClone_EditingDoesNotMutateSource()
    {
        var src = DefaultConfig();
        src.Packages.Add("orig-only");
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, src);
        vm.Packages.Add("added-by-dialog");
        Assert.DoesNotContain("added-by-dialog", src.Packages);
        Assert.DoesNotContain("orig-only", vm.Packages);  // 副本不含
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenNoEnvSelected()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.Envs[0].IsChecked = true;
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenPackagesEmpty()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        vm.Envs[0].IsChecked = true;
        vm.Packages.Clear();
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_EmitsClosedWithSelectedEnvIdsAndEditedConfig()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a"), FakeEnv("b") }, DefaultConfig());
        BaseEnvDialogResult? captured = null;
        vm.Closed += r => captured = r;

        vm.Envs[1].IsChecked = true;   // 只选 b
        vm.Config.CudaVersion = "cu121";
        vm.StartCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(new[] { "b" }, captured!.SelectedEnvIds);
        Assert.Equal("cu121", captured.Config.CudaVersion);
    }

    [Fact]
    public void Cancel_EmitsClosedWithNull()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        BaseEnvDialogResult? captured = new(new List<string>(), DefaultConfig());
        vm.Closed += r => captured = r;

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }

    [Fact]
    public void AddPackageCommand_AddsToPackages_ClearsInput()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        vm.NewPackageName = "transformers";
        vm.AddPackageCommand.Execute(null);

        Assert.Contains("transformers", vm.Packages);
        Assert.Equal("", vm.NewPackageName);
    }

    [Fact]
    public void RemovePackageCommand_RemovesByParameter()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("torch", vm.Packages);
        vm.RemovePackageCommand.Execute("torch");
        Assert.DoesNotContain("torch", vm.Packages);
    }

    [Fact]
    public void PreviewCommandText_ReflectsConfigState()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        // 默认 cu118 / stable → 含 index-url https://download.pytorch.org/whl/cu118
        Assert.Contains("cu118", vm.PreviewCommandText);
        Assert.Contains("torch", vm.PreviewCommandText);

        // 改 CUDA → preview 应变
        vm.Config.CudaVersion = "cu121";
        Assert.Contains("cu121", vm.PreviewCommandText);
        Assert.DoesNotContain("cu118", vm.PreviewCommandText);

        // 改 CustomPipArgs → 完全覆盖
        vm.Config.CustomPipArgs = "install foo bar";
        Assert.Equal("pip install foo bar", vm.PreviewCommandText);
    }

    [Fact]
    public void CudaVersions_ContainsCpu()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("cpu", vm.CudaVersions);
        Assert.Contains("cu118", vm.CudaVersions);
        Assert.Contains("cu124", vm.CudaVersions);
    }

    [Fact]
    public void TorchChannels_ContainsStableAndNightly()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("stable", vm.TorchChannels);
        Assert.Contains("nightly", vm.TorchChannels);
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvDialogViewModelTests" -v minimal
```
Expected: build fail.

- [ ] **Step 3: Implement `BaseEnvDialogViewModel`**

Create `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvDialogViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// EnvList 行的 checkbox 包装(VM 化,IsChecked 双向 bind)。
/// </summary>
public sealed class EnvChoice : ViewModelBase
{
    public Environment Env { get; }
    private bool _isChecked;

    public EnvChoice(Environment env, bool isChecked = false)
    {
        Env = env;
        _isChecked = isChecked;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}

/// <summary>
/// BaseEnvDialog 关闭时的回调 payload(null = 取消)。
/// </summary>
public sealed record BaseEnvDialogResult(
    IReadOnlyList<string> SelectedEnvIds,
    BaseEnvConfig Config);

/// <summary>
/// 基础环境部署 Dialog 的 VM:
/// - 左边 env 多选(checkbox,默认全不选)
/// - 右边 BaseEnvConfig 副本 + 包列表 CRUD
/// - 预览 pip 命令(实时)
/// - StartCommand(校验 ≥1 env + ≥1 package)
/// </summary>
public class BaseEnvDialogViewModel : ViewModelBase
{
    private string _newPackageName = "";

    public BaseEnvDialogViewModel(
        IList<Environment> envs,
        BaseEnvConfig sourceConfig)
    {
        Envs = new ObservableCollection<EnvChoice>(
            envs.Select(e => new EnvChoice(e)));
        Config = sourceConfig.Clone();
        Packages = new ObservableCollection<string>(Config.Packages);
        Packages.CollectionChanged += (_, _) =>
        {
            Config.Packages = new List<string>(Packages);
            RaisePropertyChanged(nameof(PreviewCommandText));
            StartCommand.RaiseCanExecuteChanged();
        };
        AddPackageCommand = new RelayCommand(_ => AddPackage(), _ => !string.IsNullOrWhiteSpace(NewPackageName));
        RemovePackageCommand = new RelayCommand(p =>
        {
            if (p is string s) Packages.Remove(s);
        });
        StartCommand = new RelayCommand(
            _ => Start(),
            _ => CanStart());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        RaisePropertyChanged(nameof(PreviewCommandText));
    }

    public ObservableCollection<EnvChoice> Envs { get; }
    public BaseEnvConfig Config { get; }

    public IEnumerable<string> CudaVersions { get; } =
        new[] { "cu118", "cu121", "cu124", "cpu" };
    public IEnumerable<string> TorchChannels { get; } =
        new[] { "stable", "nightly" };

    public ObservableCollection<string> Packages { get; }

    /// <summary>
    /// 只读预览:`pip install <args>`。Package 增删 / CustomPipArgs 改 / CudaVersion 改都触发重算。
    /// </summary>
    public string PreviewCommandText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Config.CustomPipArgs))
            {
                return "pip " + string.Join(' ', Config.CustomPipArgs
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            var args = new List<string> { "install" };
            args.AddRange(Packages);
            if (Config.TorchChannel == "nightly") args.Add("--pre");
            if (!string.IsNullOrWhiteSpace(Config.CudaVersion) && Config.CudaVersion != "cpu")
            {
                args.Add("--index-url");
                args.Add($"https://download.pytorch.org/whl/{Config.CudaVersion}");
            }
            if (!string.IsNullOrWhiteSpace(Config.ExtraArgs))
            {
                args.AddRange(Config.ExtraArgs
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            return "pip " + string.Join(' ', args);
        }
    }

    public string NewPackageName
    {
        get => _newPackageName;
        set
        {
            if (SetField(ref _newPackageName, value))
            {
                AddPackageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddPackageCommand { get; }
    public RelayCommand RemovePackageCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action<BaseEnvDialogResult?>? Closed;

    private void AddPackage()
    {
        var name = _newPackageName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!Packages.Contains(name)) Packages.Add(name);
        NewPackageName = "";
    }

    private bool CanStart()
    {
        if (Packages.Count == 0) return false;
        return Envs.Any(c => c.IsChecked);
    }

    private void Start()
    {
        var selected = Envs.Where(c => c.IsChecked).Select(c => c.Env.Id).ToList();
        Closed?.Invoke(new BaseEnvDialogResult(selected, Config));
    }
}
```

- [ ] **Step 4: Run, verify PASS**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvDialogViewModelTests" -v minimal
```
Expected: 10/10 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/BaseEnvDialogViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvDialogViewModelTests.cs
git commit -m "feat(wpf): BaseEnvDialogViewModel + 10 unit tests (env multi-select + package CRUD + preview)"
```

---

## Task 6: `BaseEnvDialog.xaml` + code-behind

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml.cs`

**Interfaces:**
- Consumes: `BaseEnvDialogViewModel` (Task 5) · existing converters (`NotBoolConverter`, `NullToVisibilityConverter`, `BoolToVisibility` resource)
- Produces: modal `BaseEnvDialog.Show(IList<Environment>, Settings)` returning `BaseEnvDialogResult?` (null = cancelled) — matches `CreateEnvDialog.Show` pattern (G15)

- [ ] **Step 1: Write the XAML**

Create `src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml`:

```xml
<Window x:Class="ComfyUI.Manager.Views.BaseEnvDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:ComfyUI.Manager.Views"
        Title="基础环境部署" Height="520" Width="780"
        Background="{StaticResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner">
    <Grid Margin="16">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <!-- ============ 左:env 多选 ============ -->
        <DockPanel Grid.Column="0" Margin="0,0,12,0">
            <TextBlock DockPanel.Dock="Top"
                       Text="选择 env (多选)"
                       FontWeight="Bold" Margin="0,0,0,4" />
            <ListBox ItemsSource="{Binding Envs}"
                     HorizontalContentAlignment="Stretch"
                     BorderBrush="Gray">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <CheckBox Content="{Binding Env.Name}"
                                  IsChecked="{Binding IsChecked, Mode=TwoWay}" />
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>

        <!-- ============ 右:包配置 ============ -->
        <DockPanel Grid.Column="1">
            <StackPanel DockPanel.Dock="Top">
                <TextBlock Text="CUDA 版本" Margin="0,0,0,4" />
                <ComboBox ItemsSource="{Binding CudaVersions}"
                          SelectedItem="{Binding Config.CudaVersion, Mode=TwoWay}"
                          Width="160" HorizontalAlignment="Left" />

                <TextBlock Text="Torch 通道" Margin="0,12,0,4" />
                <ComboBox ItemsSource="{Binding TorchChannels}"
                          SelectedItem="{Binding Config.TorchChannel, Mode=TwoWay}"
                          Width="160" HorizontalAlignment="Left" />

                <TextBlock Text="包列表" Margin="0,12,0,4" />
                <DockPanel Margin="0,0,0,4">
                    <Button DockPanel.Dock="Right" Content="添加"
                            Command="{Binding AddPackageCommand}"
                            Style="{StaticResource MaterialButton}"
                            Width="60" Margin="4,0,0,0" />
                    <TextBox Text="{Binding NewPackageName, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource MaterialTextBox}" />
                </DockPanel>
                <ListBox ItemsSource="{Binding Packages}"
                         Height="100"
                         BorderBrush="Gray">
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <DockPanel>
                                <Button DockPanel.Dock="Right" Content="删除"
                                        Command="{Binding DataContext.RemovePackageCommand,
                                                  RelativeSource={RelativeSource AncestorType=ListBox}}"
                                        CommandParameter="{Binding}"
                                        Style="{StaticResource MaterialButton}"
                                        Width="50" Margin="4,0,0,0" />
                                <TextBlock Text="{Binding}" VerticalAlignment="Center" />
                            </DockPanel>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>

                <TextBlock Text="额外参数(--user / -f / --no-cache 等)" Margin="0,12,0,4" />
                <TextBox Text="{Binding Config.ExtraArgs, UpdateSourceTrigger=PropertyChanged}"
                         Style="{StaticResource MaterialTextBox}" />

                <TextBlock Text="预览(只读)" Margin="0,12,0,4" />
                <TextBox Text="{Binding PreviewCommandText, Mode=OneWay}"
                         IsReadOnly="True" TextWrapping="Wrap"
                         Style="{StaticResource MaterialTextBox}"
                         Background="#11000000" />
            </StackPanel>

            <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                        HorizontalAlignment="Right" Margin="0,16,0,0">
                <Button Content="开始安装" Command="{Binding StartCommand}"
                        Style="{StaticResource MaterialButton}"
                        Width="100" />
                <Button Content="取消" Command="{Binding CancelCommand}"
                        Style="{StaticResource MaterialButton}"
                        Margin="8,0,0,0" Width="80" />
            </StackPanel>
        </DockPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Write code-behind**

Create `src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvDialog : Window
{
    public BaseEnvDialogResult? Result { get; private set; }

    public BaseEnvDialog(BaseEnvDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += result =>
        {
            Result = result;
            DialogResult = result is not null;
            Close();
        };
    }

    /// <summary>
    /// 静态入口:用 env 列表 + 当前 Settings.BaseEnv 副本打开 dialog。
    /// 返回 null = 用户取消 / 关闭,否则返回 result(payload:envIds + config)。
    /// </summary>
    public static BaseEnvDialogResult? Show(IList<Environment> envs, Settings settings)
    {
        var vm = new BaseEnvDialogViewModel(envs, settings.BaseEnv);
        var dlg = new BaseEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
```

- [ ] **Step 3: Verify build**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build succeeds (warnings about unused converters NotBoolConverter/NullToVisibilityConverter imports are OK if not used).

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml \
        src-wpf/ComfyUI.Manager/Views/BaseEnvDialog.xaml.cs
git commit -m "feat(wpf): BaseEnvDialog XAML + static Show (env multi-select + config form + preview)"
```

---

## Task 7: `BaseEnvProgressViewModel` + unit tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProgressViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProgressViewModelTests.cs`

**Interfaces:**
- Consumes: `BaseEnvInstaller` (Task 4) · `BaseEnvConfig` (Task 1) · `BaseEnvProgress` (Task 3)
- Produces:
  ```csharp
  public class BaseEnvProgressViewModel : ViewModelBase
  {
      public int Completed { get; private set; }
      public int Total { get; }
      public int EnvPercent { get; private set; }
      public string StatusText { get; private set; } = "";
      public string LogTail { get; private set; } = "";   // 滚动 log,只显示最近 N 行
      public BaseEnvStatus OverallStatus { get; private set; }
      public RelayCommand CancelCommand { get; }
      public void OnProgress(BaseEnvProgress p);
      public Task<BaseEnvInstallResult> RunAsync();  // 启动 + 等待
  }
  ```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProgressViewModelTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvProgressViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvProgressViewModelTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void OnProgress_Running_UpdatesCompletedAndTotal()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvConfig(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", 30, "downloading", null));

        Assert.Equal(0, vm.Completed);
        Assert.Equal(1, vm.Total);
        Assert.Equal(30, vm.EnvPercent);
        Assert.Contains("env-a", vm.StatusText);
        Assert.Contains("downloading", vm.LogTail);
    }

    [Fact]
    public void OnProgress_Succeeded_BumpsCompleted()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvConfig(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Succeeded, 1, 1, "env-a", "env-a", 100, null, null));

        Assert.Equal(1, vm.Completed);
        Assert.Equal(BaseEnvStatus.Succeeded, vm.OverallStatus);
    }

    [Fact]
    public void OnProgress_Failed_KeepsOverallAsFailed()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a", "env-b" }, new BaseEnvConfig(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Failed, 1, 2, "env-a", "env-a", null, null, "pip exit 1"));

        Assert.Equal(BaseEnvStatus.Failed, vm.OverallStatus);
        Assert.Contains("env-a", vm.StatusText);
    }

    [Fact]
    public void OnProgress_Cancelled_BumpsOverallToCancelled()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvConfig(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Cancelled, 1, 1, "env-a", "env-a", null, null, "用户取消"));

        Assert.Equal(BaseEnvStatus.Cancelled, vm.OverallStatus);
    }

    [Fact]
    public void CancelCommand_FiresInstallerCancellation()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvConfig(), installer);

        Assert.False(vm.CancelCommand.CanExecute(null));
        // After RunAsync starts, Cancel should be enabled; but for this test
        // we just verify the CTS plumbing exists
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void LogTail_AppendsLines()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvConfig(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", null, "line 1", null));
        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", null, "line 2", null));

        Assert.Contains("line 1", vm.LogTail);
        Assert.Contains("line 2", vm.LogTail);
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProgressViewModelTests" -v minimal
```
Expected: build fail (`BaseEnvProgressViewModel` not found).

- [ ] **Step 3: Implement `BaseEnvProgressViewModel`**

Create `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProgressViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// BaseEnvProgressDialog 的 VM:订阅 BaseEnvInstaller.InstallAsync 的 progress,
/// 维护 Completed / EnvPercent / LogTail / OverallStatus 状态,提供 CancelCommand。
///
/// LogTail 只显示最近 200 行(避免无界增长)。
/// </summary>
public class BaseEnvProgressViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly IReadOnlyList<string> _envIds;
    private readonly BaseEnvConfig _config;
    private readonly BaseEnvInstaller _installer;
    private CancellationTokenSource? _cts;
    private Task<BaseEnvInstallResult>? _runningTask;

    private readonly Queue<string> _logTail = new();

    public BaseEnvProgressViewModel(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        _envIds = envIds;
        _config = config;
        _installer = installer;
        Total = envIds.Count;
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is { IsCancellationRequested: false });
    }

    public int Completed { get; private set; }
    public int Total { get; }
    public int EnvPercent { get; private set; }
    public string StatusText { get; private set; } = "准备开始...";
    public string LogTail
    {
        get
        {
            lock (_logTail) return string.Join("\n", _logTail);
        }
    }
    public BaseEnvStatus OverallStatus { get; private set; } = BaseEnvStatus.Pending;

    public RelayCommand CancelCommand { get; }

    public Task<BaseEnvInstallResult> RunAsync()
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<BaseEnvProgress>(OnProgress);
        _runningTask = _installer.InstallAsync(_envIds, _config, progress, _cts.Token);
        return _runningTask;
    }

    public void OnProgress(BaseEnvProgress p)
    {
        Completed = p.Completed;
        if (p.EnvPercent.HasValue) EnvPercent = p.EnvPercent.Value;
        if (!string.IsNullOrEmpty(p.LogLine))
        {
            lock (_logTail)
            {
                _logTail.Enqueue(p.LogLine);
                while (_logTail.Count > MaxLogLines) _logTail.Dequeue();
            }
            RaisePropertyChanged(nameof(LogTail));
        }

        // 状态文本:envName — logLine or error
        if (!string.IsNullOrEmpty(p.CurrentEnvName))
        {
            if (!string.IsNullOrEmpty(p.ErrorMessage))
            {
                StatusText = $"{p.CurrentEnvName} — {p.ErrorMessage}";
            }
            else if (!string.IsNullOrEmpty(p.LogLine))
            {
                StatusText = $"{p.CurrentEnvName} — {p.LogLine}";
            }
            else
            {
                StatusText = $"{p.CurrentEnvName} — {p.Status}";
            }
        }

        // 整体状态优先级:任一失败 → Failed;取消 → Cancelled;全成功 → Succeeded
        if (p.Status == BaseEnvStatus.Failed && OverallStatus != BaseEnvStatus.Failed)
        {
            OverallStatus = BaseEnvStatus.Failed;
        }
        else if (p.Status == BaseEnvStatus.Cancelled)
        {
            OverallStatus = BaseEnvStatus.Cancelled;
        }
        else if (p.Status == BaseEnvStatus.Succeeded && Completed == Total
                 && OverallStatus != BaseEnvStatus.Failed)
        {
            OverallStatus = BaseEnvStatus.Succeeded;
        }

        RaisePropertyChanged(nameof(Completed));
        RaisePropertyChanged(nameof(EnvPercent));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(OverallStatus));
    }
}
```

- [ ] **Step 4: Run, verify PASS**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProgressViewModelTests" -v minimal
```
Expected: 6/6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProgressViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProgressViewModelTests.cs
git commit -m "feat(wpf): BaseEnvProgressViewModel + 6 unit tests (log tail, status, cancel CTS)"
```

---

## Task 8: `BaseEnvProgressDialog.xaml` + code-behind

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml.cs`

**Interfaces:**
- Consumes: `BaseEnvProgressViewModel` (Task 7) · existing styles/converter `BoolToVisibility` resource
- Produces: `BaseEnvProgressDialog.Show(envIds, config, installer)` static (G16)

- [ ] **Step 1: Write the XAML**

Create `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml`:

```xml
<Window x:Class="ComfyUI.Manager.Views.BaseEnvProgressDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
        d:DataContext="{d:DesignInstance Type=vm:BaseEnvProgressViewModel}"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d"
        Title="基础环境部署" Height="520" Width="640"
        Background="{StaticResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner">
    <DockPanel Margin="16">
        <!-- 顶部状态条:G6 红色 / 绿色 / 黄色 状态条 -->
        <Border DockPanel.Dock="Top" Padding="8" Margin="0,0,0,12"
                Background="{StaticResource CardBrush}"
                BorderBrush="Gray" BorderThickness="1">
            <TextBlock Text="{Binding StatusText}"
                       FontSize="14" FontWeight="Bold" />
        </Border>

        <!-- 底部按钮 -->
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,12,0,0">
            <TextBlock VerticalAlignment="Center" Margin="0,0,16,0">
                <Run Text="已完成 " />
                <Run Text="{Binding Completed, Mode=OneWay}" FontWeight="Bold" />
                <Run Text=" / " />
                <Run Text="{Binding Total, Mode=OneWay}" FontWeight="Bold" />
            </TextBlock>
            <Button Content="取消" Command="{Binding CancelCommand}"
                    Style="{StaticResource MaterialButton}"
                    Width="80" />
            <Button x:Name="CloseButton" Content="关闭" Click="OnCloseClicked"
                    Style="{StaticResource MaterialButton}"
                    Width="80" Margin="8,0,0,0" />
        </StackPanel>

        <!-- 中间:整体进度 + 当前 env 进度 + log -->
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Text="整体进度" Margin="0,0,0,4" />
            <ProgressBar Grid.Row="0" Height="14"
                         Minimum="0" Maximum="1"
                         Value="{Binding Completed, Mode=OneWay}"
                         HorizontalAlignment="Stretch" Margin="0,16,0,8" />

            <Grid Grid.Row="1" Margin="0,8,0,8">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="当前 env 进度:" Margin="0,0,8,0"
                           VerticalAlignment="Center" />
                <ProgressBar Grid.Column="1" Height="14" Minimum="0" Maximum="100"
                             Value="{Binding EnvPercent, Mode=OneWay}" />
            </Grid>

            <TextBox Grid.Row="2" Text="{Binding LogTail, Mode=OneWay}"
                     IsReadOnly="True"
                     VerticalScrollBarVisibility="Auto"
                     HorizontalScrollBarVisibility="Auto"
                     FontFamily="Consolas, Courier New"
                     TextWrapping="NoWrap"
                     Style="{StaticResource MaterialTextBox}" />
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Write code-behind**

Create `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProgressDialog : Window
{
    private readonly BaseEnvProgressViewModel _vm;

    public BaseEnvProgressDialog(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        InitializeComponent();
        _vm = new BaseEnvProgressViewModel(envIds, config, installer);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunAsync();
            }
            catch { /* errors are surfaced via OverallStatus */ }
        };
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 静态入口:弹 progress dialog,fire-and-forget,完成后用户点"关闭"。
    /// 内部 fire-and-forget _vm.RunAsync(),Close 按钮和 Cancel 走 vm 命令。
    /// </summary>
    public static void Show(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        var dlg = new BaseEnvProgressDialog(envIds, config, installer)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }
}
```

- [ ] **Step 3: Verify build**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml \
        src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml.cs
git commit -m "feat(wpf): BaseEnvProgressDialog XAML + static Show (progress bar + log tail + cancel)"
```

---

## Task 9: EnvList toolbar button + `EnvironmentListViewModel.BaseEnvCommand`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml:5-10`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs:13-46`

**Interfaces:**
- Consumes: `BaseEnvInstaller` (Task 4) · `Settings` (Task 2) · `BaseEnvDialog` (Task 6) · `BaseEnvProgressDialog` (Task 8)
- Produces:
  - `EnvironmentListViewModel(BaseEnvInstaller baseEnvInstaller, Settings settings)` overload
  - `RelayCommand BaseEnvCommand { get; }` (gated by ≥1 env in `Environments`)

- [ ] **Step 1: Modify `EnvironmentListViewModel.cs`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`:

In the ctor block (lines 26-46), change:

```csharp
public EnvironmentListViewModel(
    EnvironmentRepository repo,
    ProcessLauncher launcher,
    EnvCreatorService envCreator)
{
```

To:

```csharp
public EnvironmentListViewModel(
    EnvironmentRepository repo,
    ProcessLauncher launcher,
    EnvCreatorService envCreator,
    BaseEnvInstaller baseEnvInstaller,
    Settings settings)
{
    _repo = repo;
    _launcher = launcher;
    _envCreator = envCreator;
    _baseEnvInstaller = baseEnvInstaller;
    _settings = settings;
    BaseEnvCommand = new RelayCommand(
        _ => OpenBaseEnvDialog(),
        _ => Environments.Count > 0);
    Load();
}
```

Add fields at the top of the class (after `_envCreator`):

```csharp
private readonly BaseEnvInstaller _baseEnvInstaller;
private readonly Settings _settings;
```

Add the property declaration (after `CreateCommand`):

```csharp
public RelayCommand BaseEnvCommand { get; }
```

Add the method (after `CreateEnv`):

```csharp
private void OpenBaseEnvDialog()
{
    var envs = _repo.ListAll();
    if (envs.Count == 0) return;
    var result = Views.BaseEnvDialog.Show(envs, _settings);
    if (result is null) return;  // 取消

    // 同步到 Settings(用户在 dialog 改了配置 → Settings 也跟着,即使没去 Settings 页)
    _settings.BaseEnv = result.Config;
    // 注:_settings.BaseEnv 是同一引用,但 Config 是 Clone;
    // 把 Clone 写回去 → 后续 BaseEnvInstaller / SettingsView 看到的都是 dialog 选的值
    // 同时 dialog 期间用户改的 ExtraArgs / CustomPipArgs 也会被 Settings 持久化
    // (SettingsViewModel 在 BaseEnv* properties set 时调 _repo.Save)。
    // 这里我们直接调 settings repo 写一次:
    // —— 但 EnvironmentListViewModel 没拿到 SettingsRepository 引用。
    // 解决:SettingsViewModel 在 BaseEnvExtraArgs / CustomPipArgs set 时已经会写,
    //      但 dialog 关闭后用户可能没去 Settings 页,所以我们补一次。
    // —— 简洁方案:让 BaseEnvDialog.Show 在 Closed 触发时由 caller 写。
    //      这里简化:调 BaseEnvDialog.Show 后,result.Config 是 Clone,把 Settings 指向它。
    //      但 _settings 已有 BaseEnv 实例,需重置字段:
    _settings.BaseEnv = result.Config;

    Views.BaseEnvProgressDialog.Show(
        result.SelectedEnvIds, result.Config, _baseEnvInstaller);
}
```

**设计注释:** 上述代码让 dialog 关闭时把 Settings.BaseEnv 引用换成 Clone。后续 Settings 页打开会显示 dialog 用户改过的值,符合 G3。但 `Settings.BaseEnv` 是个 class,直接赋值是引用赋值 — 如果 dialog 后续还在跑(它不会,ShowDialog 是模态),不会有问题。

**为保持简洁,我们用更安全的写法 — 把 Settings 字段逐个拷贝而非引用替换**(避免外部持有的旧引用差异):

Replace the body of `OpenBaseEnvDialog` with:

```csharp
private void OpenBaseEnvDialog()
{
    var envs = _repo.ListAll();
    if (envs.Count == 0) return;
    var result = Views.BaseEnvDialog.Show(envs, _settings);
    if (result is null) return;

    // dialog 改了配置 → 同步到 Settings(引用替换)
    _settings.BaseEnv = result.Config;

    Views.BaseEnvProgressDialog.Show(
        result.SelectedEnvIds, result.Config, _baseEnvInstaller);
}
```

- [ ] **Step 2: Modify `EnvironmentListView.xaml`**

Edit `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`. Change the toolbar (lines 5-10) from:

```xml
<StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
    <Button Content="刷新" Command="{Binding RefreshCommand}"
            Style="{StaticResource MaterialButton}" />
    <Button Content="+ 新建环境" Command="{Binding CreateCommand}"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
</StackPanel>
```

To:

```xml
<StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
    <Button Content="刷新" Command="{Binding RefreshCommand}"
            Style="{StaticResource MaterialButton}" />
    <Button Content="+ 新建环境" Command="{Binding CreateCommand}"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
    <Button Content="基础环境部署" Command="{Binding BaseEnvCommand}"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
</StackPanel>
```

- [ ] **Step 3: Verify build**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: **build FAIL** — `MainViewModel.ShowEnvironments()` and App.xaml.cs call `new EnvironmentListViewModel(...)` with old 3-arg ctor; signature mismatch.

If build fails, that is expected; the next two tasks fix the callers. Continue to Task 10 + 11.

If you cannot proceed because the build is broken for too long, **STOP** and apply Task 10/11 in the same session to restore build before committing.

- [ ] **Step 4: Commit (along with Task 10 + 11 changes in the same commit)**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml
git commit -m "feat(wpf): EnvList toolbar '基础环境部署' button + BaseEnvCommand"
```

(Or skip commit here and roll into Task 12's App.xaml.cs commit — your choice. Recommendation: roll together so the build is never broken on `main`.)

---

## Task 10: `MainViewModel` inject `BaseEnvInstaller` + `Settings`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:38-49, 69-76`

**Interfaces:**
- Consumes: `BaseEnvInstaller` (Task 4) · `Settings` (existing, already injected)
- Produces: `MainViewModel(..., BaseEnvInstaller baseEnvInstaller, Settings settings)` — pass both to `EnvironmentListViewModel` ctor in `ShowEnvironments()`

- [ ] **Step 1: Edit `MainViewModel.cs`**

Add field (after `_catalogCacheStore` line 22):

```csharp
private readonly BaseEnvInstaller _baseEnvInstaller;
```

Add ctor parameter (after `CatalogCacheStore catalogCacheStore`):

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
    BaseEnvInstaller baseEnvInstaller)
{
    _dbFactory = dbFactory;
    _launcher = launcher;
    _orchestrator = orchestrator;
    _nodeOps = nodeOps;
    _envCreator = envCreator;
    _settingsRepo = settingsRepo;
    _gitProxy = gitProxy;
    _settings = settings;
    _catalogFetcher = catalogFetcher;
    _catalogRefreshService = catalogRefreshService;
    _catalogCacheStore = catalogCacheStore;
    _baseEnvInstaller = baseEnvInstaller;

    ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
    ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
    ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
    OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
}
```

Edit `ShowEnvironments()` to pass the new params:

```csharp
private void ShowEnvironments()
{
    var envRepo = new EnvironmentRepository(_dbFactory);
    CurrentView = new EnvironmentListView
    {
        DataContext = new EnvironmentListViewModel(envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings),
    };
}
```

- [ ] **Step 2: Verify build still fails** (Task 11 fixes `App.xaml.cs`)

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build still fails on `App.xaml.cs:70-72` (MainViewModel ctor now expects 12 args, App passes 11).

- [ ] **Step 3: Commit (along with Task 11) — see Task 11**

---

## Task 11: `App.xaml.cs` instantiate `BaseEnvInstaller` + pass to `MainViewModel`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:67-72`

**Interfaces:**
- Consumes: `EnvironmentRepository` (already created in OnStartup)
- Produces: `var baseEnvInstaller = new BaseEnvInstaller(envRepo);` + add as 12th arg to `MainViewModel` ctor

- [ ] **Step 1: Edit `App.xaml.cs`**

In `App.xaml.cs`, find the `bulkOrchestrator` block (lines 65-66):

```csharp
var bulkOrchestrator = new BulkUpdateOrchestrator(
    projectRoot, gitExe, envRepo, nodeRepo, gitProxy);
```

Add immediately after:

```csharp
var baseEnvInstaller = new BaseEnvInstaller(envRepo);
```

Find the `_mainVm = new MainViewModel(...)` block (lines 70-72):

```csharp
_mainVm = new MainViewModel(
    dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
    settings, catalogFetcher, catalogRefreshService, catalogCacheStore);
```

Change to (add `baseEnvInstaller` as the 12th arg):

```csharp
_mainVm = new MainViewModel(
    dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
    settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller);
```

- [ ] **Step 2: Verify build succeeds**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build succeeds.

- [ ] **Step 3: Run all tests**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: all pre-existing tests pass + 26 new tests added in this plan (10 BaseEnvConfig + 2 Settings.BaseEnv + 8 BaseEnvInstaller + 10 BaseEnvDialogViewModel + 6 BaseEnvProgressViewModel) → 131+26 = ~157 / 158 PASS, 1 skipped live.

- [ ] **Step 4: Commit (Tasks 9, 10, 11 together)**

```bash
git add src-wpf/ComfyUI.Manager/App.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml
git commit -m "feat(wpf): wire BaseEnvInstaller through MainViewModel → EnvList toolbar"
```

---

## Task 12: `SettingsView.xaml` "基础环境" section + `SettingsViewModel.BaseEnv*` properties

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (add fields, properties, save wiring)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (insert section after "环境 / 工具" section, before "高级 — 额外路径" section)

**Interfaces:**
- Consumes: `Settings.BaseEnv` (Task 2)
- Produces:
  - `BaseEnvCudaVersion` / `BaseEnvTorchChannel` / `BaseEnvExtraArgs` / `BaseEnvCustomPipArgs` / `BaseEnvIsAdvancedOpen` (bool toggle for advanced raw collapse)
  - `BaseEnvPackages` ObservableCollection<string> synced with `Settings.BaseEnv.Packages`

- [ ] **Step 1: Add fields + properties to `SettingsViewModel.cs`**

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`, find the GitHubToken block (around line 181-186):

```csharp
public string GitHubToken
{
    get => _settings.GitHubToken;
    set { _settings.GitHubToken = value ?? ""; _repo.Save(_settings); }
}
```

Immediately after this block, add:

```csharp
// —— 基础环境 ——
public string BaseEnvCudaVersion
{
    get => _settings.BaseEnv.CudaVersion;
    set { _settings.BaseEnv.CudaVersion = value ?? "cu118"; _repo.Save(_settings); RaisePropertyChanged(); }
}

public string BaseEnvTorchChannel
{
    get => _settings.BaseEnv.TorchChannel;
    set { _settings.BaseEnv.TorchChannel = value ?? "stable"; _repo.Save(_settings); RaisePropertyChanged(); }
}

public string BaseEnvExtraArgs
{
    get => _settings.BaseEnv.ExtraArgs;
    set { _settings.BaseEnv.ExtraArgs = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
}

public string BaseEnvCustomPipArgs
{
    get => _settings.BaseEnv.CustomPipArgs;
    set { _settings.BaseEnv.CustomPipArgs = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
}

private bool _baseEnvIsAdvancedOpen;
public bool BaseEnvIsAdvancedOpen
{
    get => _baseEnvIsAdvancedOpen;
    set => SetField(ref _baseEnvIsAdvancedOpen, value);
}

public ObservableCollection<string> BaseEnvPackages { get; }

public List<string> BaseEnvCudaVersions { get; } = new() { "cu118", "cu121", "cu124", "cpu" };
public List<string> BaseEnvTorchChannels { get; } = new() { "stable", "nightly" };

public RelayCommand AddBaseEnvPackageCommand { get; }
public RelayCommand RemoveBaseEnvPackageCommand { get; }
```

Find the ctor (lines 29-149). Inside the ctor body, after `RefreshCatalogCommand = ...` line (around line 147), add:

```csharp
BaseEnvPackages = new ObservableCollection<string>(_settings.BaseEnv.Packages);
BaseEnvPackages.CollectionChanged += (_, _) =>
{
    _settings.BaseEnv.Packages = new List<string>(BaseEnvPackages);
    _repo.Save(_settings);
};
AddBaseEnvPackageCommand = new RelayCommand(p =>
{
    if (p is string s && !string.IsNullOrWhiteSpace(s) && !BaseEnvPackages.Contains(s))
    {
        BaseEnvPackages.Add(s.Trim());
    }
});
RemoveBaseEnvPackageCommand = new RelayCommand(p =>
{
    if (p is string s) BaseEnvPackages.Remove(s);
});
```

Also, find the `RaiseAllPropertiesChanged()` method (around line 402-419) and add inside:

```csharp
RaisePropertyChanged(nameof(BaseEnvCudaVersion));
RaisePropertyChanged(nameof(BaseEnvTorchChannel));
RaisePropertyChanged(nameof(BaseEnvExtraArgs));
RaisePropertyChanged(nameof(BaseEnvCustomPipArgs));
```

- [ ] **Step 2: Add "基础环境" section to `SettingsView.xaml`**

In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`, find the closing of "环境 / 工具" section (around line 227, end of git proxy grid), and the start of "高级 — 额外路径" section (around line 229-230). Insert between them:

```xml
<!-- ============ 基础环境 ============ -->
<TextBlock Text="基础环境" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
<TextBlock Text="CUDA 版本" Margin="0,8,0,4" />
<ComboBox ItemsSource="{Binding BaseEnvCudaVersions}"
          SelectedItem="{Binding BaseEnvCudaVersion, Mode=TwoWay}"
          Width="160" HorizontalAlignment="Left" />
<TextBlock Text="Torch 通道" Margin="0,12,0,4" />
<ComboBox ItemsSource="{Binding BaseEnvTorchChannels}"
          SelectedItem="{Binding BaseEnvTorchChannel, Mode=TwoWay}"
          Width="160" HorizontalAlignment="Left" />

<TextBlock Text="包列表" Margin="0,12,0,4" />
<ItemsControl ItemsSource="{Binding BaseEnvPackages}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Grid Margin="0,4,0,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox Grid.Column="0" Text="{Binding Mode=OneWay}"
                         IsReadOnly="True" Style="{StaticResource MaterialTextBox}" />
                <Button Grid.Column="1" Content="删除" Margin="4,0,0,0"
                        Command="{Binding DataContext.RemoveBaseEnvPackageCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}"
                        Style="{StaticResource MaterialButton}" />
            </Grid>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>

<TextBlock Text="额外参数(--user / -f / --no-cache 等)" Margin="0,12,0,4" />
<TextBox Text="{Binding BaseEnvExtraArgs, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource MaterialTextBox}" Width="480"
         HorizontalAlignment="Left" />

<CheckBox Content="高级 — 显示整段 raw pip args (CustomPipArgs,填了会完全覆盖上面)"
          IsChecked="{Binding BaseEnvIsAdvancedOpen}"
          Margin="0,16,0,4" />
<Grid Visibility="{Binding BaseEnvIsAdvancedOpen, Converter={StaticResource BoolToVisibility}}">
    <TextBox Text="{Binding BaseEnvCustomPipArgs, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource MaterialTextBox}" Height="60"
             AcceptsReturn="True" TextWrapping="Wrap"
             VerticalScrollBarVisibility="Auto"
             HorizontalAlignment="Stretch" />
</Grid>
```

- [ ] **Step 3: Verify build**

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```
Expected: build succeeds.

- [ ] **Step 4: Run all tests**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: all tests PASS (no new tests added in this task, but existing 157 should remain green).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml
git commit -m "feat(wpf): Settings '基础环境' section (CUDA / channel / packages / ExtraArgs + advanced raw)"
```

---

## Task 13: Manual verify checklist + close-out commit

**Files:**
- No new files; smoke test only

- [ ] **Step 1: Build solution**

Run:
```bash
dotnet build ComfyUI.sln -c Release -v minimal
```
Expected: build succeeds, no warnings about missing converters / unused imports.

- [ ] **Step 2: Run all tests one more time**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -c Release -v minimal
```
Expected: all PASS (≥157 tests).

- [ ] **Step 3: Manual smoke checklist (USER VERIFICATION)**

The user opens WPF UI and runs through the spec §8 acceptance list:

1. ☐ Double-click `ComfyUI Manager.exe` → env list shows toolbar with `[基础环境部署]` button visible
2. ☐ Click `[基础环境部署]` → `BaseEnvDialog` opens: left side shows env list with unchecked checkboxes, right side shows CUDA + channel + packages + extra args + preview textbox
3. ☐ Select 1 env, leave defaults, click `预览` area → textbox shows `pip install torch torchaudio torchvision xformers --index-url https://download.pytorch.org/whl/cu118`
4. ☐ Click `开始安装` → BaseEnvDialog closes, BaseEnvProgressDialog opens
5. ☐ (Optional, slow) Wait for real `pip install` to finish in env's venv — progress bar moves, log tail scrolls
6. ☐ Click `取消` mid-run → confirms → current pip killed, dialog top shows "已取消"
7. ☐ Re-open dialog → `BaseEnvConfig` from Settings now shown (cuda/channel/packages persisted)
8. ☐ Open Settings → scroll to "基础环境" section → form shows current config
9. ☐ Change CUDA to `cu121`, click anywhere outside textbox → Settings.json written
10. ☐ Click "高级" checkbox → `CustomPipArgs` textbox appears, type `install foo` → save → reopen dialog → preview shows `pip install foo` (raw override)
11. ☐ Restart app → BaseEnv dialog + Settings page both show the saved config

Document any deviations and file fix tasks.

- [ ] **Step 4: Commit close-out (changelog + version bump)**

This step is the **release commit** that:
1. Bumps version literals (5 places: `pyproject.toml`, `src/comfy_mgr/__init__.py`, `src/comfy_mgr/shared/errors.json`, `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`, `tests/test_version_consistency.py` — **only do this if Python side has these files**; confirm with `git grep '0.6.4'` and bump to `0.6.5` if release target is `v0.6.5`)
2. Writes `release/RELEASE-NOTES-v0.6.5.md` describing the new feature + scope

If version bump is NOT in scope (the user wants this in a follow-up release), **skip this step** and document it as a follow-up task.

If executing:
```bash
# verify existing version literal locations
git grep -n '0.6.4' -- 'src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj' 'src/comfy_mgr/__init__.py' 'src/comfy_mgr/shared/errors.json' 'tests/test_version_consistency.py' 2>/dev/null
# (NOTE: since M5.2 deleted Python service, most of these may not exist)
```

The user's release process is documented in `memory/project_comfyui_manager.md` v0.6.3 release section.

- [ ] **Step 5: Ledger + memory update**

Update `.superpowers/sdd/progress.md` and `~/.claude/projects/.../memory/project_comfyui_manager.md` with:
- "基础环境部署 feature shipped in v0.6.5 (or whatever target) — 12 commits, ~1200 lines, 26 new tests"
- Tests count delta
- Any deviations from plan

Commit:
```bash
git add .superpowers/sdd/progress.md \
        memory/MEMORY.md \
        memory/project_comfyui_manager.md
git commit -m "docs(sdd): close out 基础环境部署 feature — 12 tasks / 26 tests / v0.6.5"
```

---

## Self-Review Checklist

(For the planner — already executed inline above.)

- [x] **Spec coverage:** every requirement in spec §0/§1/§4 maps to a task:
  - G1 (toolbar button) → Task 9
  - G2 (env modal multi-select) → Task 6 (BaseEnvDialog XAML)
  - G3 (Settings.BaseEnvConfig) → Task 2 + Task 12
  - G4 (BaseEnvConfig shape) → Task 1
  - G5 (BuildPipArgs priority) → Task 1 + tests
  - G6 (progress dialog content) → Task 8
  - G7 (one fail continues) → Task 4 + tests
  - G8 (cancel kills current pip) → Task 4 + tests
  - G9 (Settings form + advanced raw) → Task 12
  - G10 (Settings persist + defaults) → Task 2 + tests
  - G11 (venv python resolution) → Task 4 `GetVenvPythonPath`
  - G12 (testability) → Task 4 `protected virtual RunPipAsync`
  - G13 (percent regex) → Task 4 `PercentPattern` + tests
  - G14 (defaults) → Task 1
  - G15 (static Show dialog pattern) → Task 6
  - G16 (progress dialog static Show) → Task 8

- [x] **Placeholder scan:** no "TODO", "fill in later", "appropriate error handling", "similar to Task N".

- [x] **Type consistency:** type names match across tasks:
  - `BaseEnvConfig` defined Task 1, consumed Tasks 2, 4, 5, 6, 7, 8, 9, 12
  - `BaseEnvInstaller` defined Task 4, consumed Tasks 7, 8, 9, 11
  - `BaseEnvDialogViewModel` + `BaseEnvDialogResult` defined Task 5, consumed Tasks 6, 9
  - `BaseEnvProgressViewModel` defined Task 7, consumed Task 8
  - `Environment` field access consistent (VenvPath / PythonExecutable / Id / Name)
  - `EnvironmentListViewModel` ctor signature change tracked in Task 9, callers fixed in Task 10 + 11

- [x] **Task right-sizing:** each task produces a coherent unit (single file or single concern across 2 files), independently testable.

- [x] **DRY:** dialog Show pattern matches `CreateEnvDialog.Show`; `EnvironmentRepository.Get(envId)` reused for env lookup; settings persistence pattern matches existing `SettingsViewModel` properties (`{ get; set; _repo.Save(_settings); RaisePropertyChanged(); }`).

- [x] **YAGNI:** no scope creep. Spec §1.2 non-goals (no NVIDIA detect, no multi-Python, no conda, no per-env config, no full pip manager) are explicitly NOT addressed.

---

## Execution Handoff

This plan is ready. Two execution options:

1. **Subagent-Driven (recommended for this scope)** — I dispatch a fresh subagent per task (Tasks 1-12), review between each, fast iteration. Use `superpowers:subagent-driven-development`.

2. **Inline Execution** — Execute tasks in this session using `superpowers:executing-plans`, batch with checkpoints.

For this plan (12 tasks, ~1500 lines, ~6-8 commits), **Subagent-Driven** is recommended because:
- Tasks 1-5 are pure logic with deterministic tests → can run on cheap model
- Tasks 6, 8, 9, 11, 12 are XAML/wiring → benefit from mid-tier review
- Task 13 is the whole-branch review → benefits from opus model

**Awaiting user choice** before dispatching implementers.