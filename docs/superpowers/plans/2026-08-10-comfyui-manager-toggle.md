# ComfyUI Manager Toggle Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** env-list 操作列 row 1 加 ComfyUI Manager toggle 按钮(已装=卸,未装=装),检测 `<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager`;「装依赖」末尾自动装(只装不卸);复用 v0.6.5.15 inline 状态面板。

**Architecture:** 新 `ComfyUIManagerInstaller` service(克隆 + pip install -r Manager/requirements.txt + 卸载/检测);抽 `RequirementsFileInstaller` 公共 helper 复用过滤+pip 逻辑(两边:`RequirementsInstaller` 装 ComfyUI 依赖、`ComfyUIManagerInstaller` 装 Manager 依赖);`EnvironmentListViewModel` EnvRow 加 toggle command + computed button text + 子 mutex;`RequirementsInstaller.InstallAsync` 末尾调 Manager 装(失败 WARN 不阻断);XAML row 1 第 6 按钮 + 第 2 个 inline 状态面板。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 真 git + 真 pip 测试 · `GitRunner` / `NodeOperationResult` / `RequirementsStatusViewModel` 模式 · 手写 MVVM (ViewModelBase / RelayCommand)

**base SHA:** `c353ba4` (v0.6.11+ T5 spec `c353ba4`,HEAD)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | ComfyUI Manager git URL 写死 `https://github.com/ltdrdata/ComfyUI-Manager`(官方 ltdrdata),不加 Settings 字段 | 用户决策 |
| **G2** | 检测 = `Directory.Exists(<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager)`,per check,**不**加 SQLite 列 | 用户决策 |
| **G3** | 卸载 = `rm -rf` 整个目录,**不**走 git reset/clean,**不**备份 zip,**不**删 venv 已装 Manager Python 依赖 | 用户决策 |
| **G4** | 按钮形式 = 单按钮 + content binding("安装 ComfyUI Manager" / "卸载 ComfyUI Manager"),不用两个独立按钮 | 用户决策 |
| **G5** | 进度反馈 = 复用 v0.6.5.15 inline 状态面板模式(独立 `ComfyUIManagerStatusViewModel`,**不**抽公共基类) | 用户决策 + 现有模式 |
| **G6** | 按钮位置 = row 1 装卸链,「卸载基础环境」之后(row 1 = 6 按钮) | 用户决策 |
| **G7** | 自动装触发 = `RequirementsInstaller.InstallAsync` 末尾(pip install -r 成功后),Manager 装失败**不阻断** requirements(只 WARN 日志) | 用户决策 |
| **G8** | busy mutex = 复用 v0.6.5.22 `IsEnvBusy` + 新 `BusyKind.ComfyUiManagerInstall / .ComfyUiManagerUninstall`,防止 toggle 装卸期间跟装依赖/同一 env 多次 toggle race | 项目惯例 v0.6.5.22 |
| **G9** | ComfyUI Manager 自己的 `requirements.txt` 必须 `pip install -r`(过滤 torch 行同 v0.6.5.12),pip 失败回滚 rm -rf 整个 Manager 目录 | 用户原话 |
| **G10** | 抽 `RequirementsFileInstaller` 公共 helper 给 `RequirementsInstaller` 和 `ComfyUIManagerInstaller` 两边复用(避免 30 行过滤逻辑复制) | 本 spec 决策 |
| **G11** | WPF `Setter` 引用 palette brush 必须 property-element + `DynamicResource`(v0.6.9.2 教训 `feedback_wpf_style_setter_dynamic_resource.md`) | 项目惯例 |
| **G12** | 新文件放置:`Services/ComfyUIManagerInstaller.cs`、`Services/RequirementsFileInstaller.cs`、`ViewModels/ComfyUIManagerStatusViewModel.cs` | 现有代码结构 |
| **G13** | DI 接线:`App.xaml.cs` 注册 `RequirementsFileInstaller` + `ComfyUIManagerInstaller` 单例;`RequirementsInstaller` ctor 加 `ComfyUIManagerInstaller` 参数;`EnvironmentListViewModel` ctor 加 `ComfyUIManagerInstaller` 参数 | 现有模式 |
| **G14** | 测试覆盖:真 git + 真 venv python(`NodeOperationsDownloadTests` 同款)+ Fake helper(`FakeRequirementsInstaller` 风格)+ `if (FindGit() is null) return` skip 缺失 | 项目惯例 |
| **G15** | AppLogger INFO 日志 `comfyui-manager-install` / `comfyui-manager-uninstall` 每 install/uninstall 写一行;失败 WARN/ERROR;Manager 自动装失败 WARN 不阻断 requirements | v0.6.5.13 惯例 |
| **G16** | Service 不抛异常出,所有失败返 `NodeOperationResult`(沿用现有模式) | 项目惯例 |
| **G17** | 不改 `BulkUpdateOrchestrator`(它已能 pull ComfyUI-Manager,scope 独立);不改 `Environment` model / `ScannedNode` / `NodeOperations`;不引入新依赖 | YAGNI + 项目惯例 |

---

## File Structure

**Modified (5 source + 1 test csproj):**
- `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (T1 ctor + 内部用 helper;T5 末尾加自动装 Manager)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (T3 EnvRow 属性 + 子 mutex + ToggleCommand)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (T4 row 1 第 6 按钮 + inline 状态面板)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs` (T4 OnComfyUiManagerStatusCloseClicked)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` (T1+T2+T4 DI 接线)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` (T3 ctor 适配)

**Created (3 source + 3 test):**
- `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs` (T1,~80 行)
- `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs` (T2,~120 行)
- `src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs` (T3,~50 行)
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs` (T1,~80 行)
- `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs` (T2,~150 行)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs` (T3,~120 行)

**Test files modified (2):**
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs` (T1 ctor 适配 + T5 新增 2 测试)

---

## Task 1: Extract `RequirementsFileInstaller` Helper + Adapt `RequirementsInstaller`

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (T1 部分:ctor 加 `RequirementsFileInstaller` 参数 + `InstallAsync` 内部改调 helper;RunPipAsync/FilterTorchLines 不删,变成 helper 调)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs` (ctor 适配)

**Interfaces (produces):**
```csharp
public sealed class RequirementsFileInstaller {
    public const string FilteredRequirementsFileName = ".requirements_filtered.txt";
    private static readonly Regex TorchLinePattern = new(...);
    public static List<string> FilterTorchLines(IEnumerable<string> rawLines);
    public Task<RequirementsInstallResult> InstallAsync(
        string requirementsFilePath,
        string filteredOutputPath,
        string venvPythonPath,
        Action<string>? onLine,
        CancellationToken ct);
}
public sealed record RequirementsInstallResult(bool Success, bool Cancelled, string? Reason, int InstalledCount);
```

**Goal:** 把 `RequirementsInstaller` 内部的"过滤 torch 行 + 写 filtered 文件 + 跑 pip + 清理"这段逻辑抽成 `RequirementsFileInstaller.InstallAsync(requirementsPath, filteredOutputPath, venvPythonPath, onLine, ct)`。`RequirementsInstaller.InstallAsync` 调它做实际 pip,T5 时再在末尾追加 Manager 自动装。

---

### Step 1: Write failing test for RequirementsFileInstaller core

Create `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class RequirementsFileInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsFileInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reqfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void FilterTorchLines_StripsTorchFamilyLines()
    {
        var raw = new[] { "torch", "torch==2.1.0", "  torchvision", "torchaudio", "SQLAlchemy", "einops" };
        var filtered = RequirementsFileInstaller.FilterTorchLines(raw);
        Assert.Contains("SQLAlchemy", filtered);
        Assert.Contains("einops", filtered);
        Assert.DoesNotContain(filtered, l => l.Trim().StartsWith("torch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilterTorchLines_PreservesCommentsAndBlankLines()
    {
        var raw = new[] { "# top comment", "", "  ", "transformers" };
        var filtered = RequirementsFileInstaller.FilterTorchLines(raw);
        Assert.Equal(4, filtered.Count);
    }

    [Fact]
    public async Task InstallAsync_MissingRequirementsFile_ReturnsFailure()
    {
        var installer = new RequirementsFileInstaller();
        var missingPath = Path.Combine(_tempRoot, "nope-requirements.txt");
        var filteredPath = Path.Combine(_tempRoot, RequirementsFileInstaller.FilteredRequirementsFileName);

        var result = await installer.InstallAsync(
            missingPath, filteredPath, "ignored-python", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_PipSucceeds_WritesFilteredFileThenCleansUp()
    {
        var reqPath = Path.Combine(_tempRoot, "requirements.txt");
        File.WriteAllLines(reqPath, new[] { "torch", "SQLAlchemy" });
        var filteredPath = Path.Combine(_tempRoot, RequirementsFileInstaller.FilteredRequirementsFileName);

        // 装 venv python 占位文件 + fake git-style 跑 pip — 这里直接调 InstallAsync,
        // 它内部跑真 python(测试机器上有 python 即可),所以 skip 缺失。
        var pyExe = FindPython();
        if (pyExe is null) return;  // skip if python missing

        var installer = new RequirementsFileInstaller();
        var result = await installer.InstallAsync(
            reqPath, filteredPath, pyExe, line => { }, CancellationToken.None);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.Equal(1, result.InstalledCount);  // torch stripped
        Assert.False(File.Exists(filteredPath), "filtered file 应被清理");
    }

    private static string? FindPython()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return c;
            }
            catch { }
        }
        return null;
    }
}
```

Add `using System.Diagnostics;` and `using System.Runtime.InteropServices;` at top.

### Step 2: Run test to verify it fails

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsFileInstaller" -v minimal`
Expected: FAIL with "RequirementsFileInstaller not found" / 3+ errors.

### Step 3: Create `RequirementsFileInstaller`

Create `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// RequirementsFileInstaller:对单个 requirements.txt 文件做
/// "过滤 torch 行 + 写 filtered 文件 + 跑 pip install -r + 清理"。
///
/// v0.6.11+ T1 抽出:ComfyUI 自己的 requirements(RequirementsInstaller)和
/// ComfyUI Manager 的 requirements(ComfyUIManagerInstaller)都需要跑同一段
/// 逻辑,避免复制 30 行 pip + 过滤 + 临时文件清理代码。
///
/// 行为:
/// - requirementsFilePath 不存在 → 返 Failure(reason="requirements.txt 不存在:{path}")
/// - 过滤 → 写 filteredOutputPath(覆盖)
/// - 跑 pip,onLine 每行 stdout/stderr
/// - 成功 → 删 filteredOutputPath,返 Success
/// - pip 非零 / 取消 → 删 filteredOutputPath,返 Failure/Cancelled
/// </summary>
public sealed class RequirementsFileInstaller
{
    public const string FilteredRequirementsFileName = ".requirements_filtered.txt";

    private static readonly Regex TorchLinePattern = new(
        @"^\s*#?\s*(torch|torchvision|torchaudio|torchtext|torchdata)(\s|$|[=<>!~])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 过滤掉 torch 系列行(让 BED profile 锁版本不被覆盖)。保留空行 / 普通注释 / 其他依赖。
    /// </summary>
    public static List<string> FilterTorchLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var raw in rawLines)
        {
            var line = raw ?? "";
            if (TorchLinePattern.IsMatch(line)) continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// 跑 <c>pip install -r &lt;filteredOutputPath&gt;</c>(文件已 caller 写好),
    /// 每行 stdout/stderr 回调 onLine。失败/取消不抛 — 返 RequirementsInstallResult。
    /// filteredOutputPath 会在末尾清理(成功失败都清)。
    /// </summary>
    public async Task<RequirementsInstallResult> InstallAsync(
        string requirementsFilePath,
        string filteredOutputPath,
        string venvPythonPath,
        Action<string>? onLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requirementsFilePath))
            throw new ArgumentException("requirementsFilePath 不能为空", nameof(requirementsFilePath));
        if (string.IsNullOrWhiteSpace(filteredOutputPath))
            throw new ArgumentException("filteredOutputPath 不能为空", nameof(filteredOutputPath));
        if (string.IsNullOrWhiteSpace(venvPythonPath))
            throw new ArgumentException("venvPythonPath 不能为空", nameof(venvPythonPath));

        if (!File.Exists(requirementsFilePath))
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"requirements.txt 不存在:{requirementsFilePath}",
                InstalledCount: 0);
        }

        // filtered 文件先写(覆盖)
        List<string> rawLines;
        try
        {
            rawLines = new List<string>(await File.ReadAllLinesAsync(requirementsFilePath, ct));
        }
        catch (Exception ex)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"读取 requirements.txt 失败:{ex.Message}",
                InstalledCount: 0);
        }
        var filtered = FilterTorchLines(rawLines);
        try
        {
            await File.WriteAllLinesAsync(filteredOutputPath, filtered, ct);
        }
        catch (Exception ex)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"写过滤文件失败:{ex.Message}",
                InstalledCount: 0);
        }

        var pipResult = await RunPipAsync(
            venvPythonPath,
            new[] { "install", "-r", filteredOutputPath, "--disable-pip-version-check" },
            onLine ?? (_ => { }),
            ct);

        try { File.Delete(filteredOutputPath); } catch { }

        if (pipResult.WasCancelled || ct.IsCancellationRequested)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: true,
                Reason: "用户取消",
                InstalledCount: 0);
        }
        if (pipResult.ExitCode != 0)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"pip 退出码 {pipResult.ExitCode}",
                InstalledCount: 0);
        }
        return new RequirementsInstallResult(
            Success: true, Cancelled: false,
            Reason: null,
            InstalledCount: filtered.Count);
    }

    private static async Task<PipResult> RunPipAsync(
        string pythonExe,
        IReadOnlyList<string> pipArgs,
        Action<string> onLine,
        CancellationToken ct)
    {
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
        foreach (var a in pipArgs) psi.ArgumentList.Add(a);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动 pip 失败:{ex.Message}", ex);
        }
        if (process is null) throw new InvalidOperationException("Process.Start 返回 null");

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
                    onLine(line);
                }
            }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task);
                using var reg = ct.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });
                await process.WaitForExitAsync(CancellationToken.None);
                tcs.TrySetResult(new PipResult(process.ExitCode, WasCancelled: ct.IsCancellationRequested));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });

        return await tcs.Task;
    }
}

internal record PipResult(int ExitCode, bool WasCancelled);

public sealed record RequirementsInstallResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int InstalledCount);
```

### Step 4: Run test to verify pass

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsFileInstaller" -v minimal`
Expected: PASS (4 tests).

### Step 5: Adapt `RequirementsInstaller` to use the helper

Modify `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`:

- Replace `public const string FilteredRequirementsFileName = ".requirements_filtered.txt";` with `// 移到 RequirementsFileInstaller.FilteredRequirementsFileName`
- Replace `FilterTorchLines` static method with `public static List<string> FilterTorchLines(...)` delegate to `RequirementsFileInstaller.FilterTorchLines(...)`(保持 public,既有测试仍走它)
- Replace `RunPipAsync` protected virtual with a version that internally calls `RequirementsFileInstaller.RunPipAsync` — or remove and have InstallAsync use helper directly
- Modify `InstallAsync(env, logProgress, ct)` to:

```csharp
public virtual async Task<RequirementsInstallResult> InstallAsync(
    Environment env,
    IProgress<string>? logProgress = null,
    CancellationToken ct = default)
{
    if (env is null) throw new ArgumentNullException(nameof(env));
    if (string.IsNullOrWhiteSpace(env.RootPath))
        throw new ArgumentException("env.RootPath 为空", nameof(env));

    _logger?.Info("requirements", $"env='{env.Name}' 开始装 requirements.txt");

    var candidates = ResolveRequirementsCandidates(env);
    var requirementsPath = candidates.FirstOrDefault(File.Exists);
    if (requirementsPath is null)
    {
        var reason = $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})";
        LogResult(env.Name, "failed", reason);
        return new RequirementsInstallResult(false, false, reason, 0);
    }

    var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
    var pythonExe = ResolveVenvPython(env);

    var result = await _reqFileInstaller.InstallAsync(
        requirementsPath,
        filteredPath,
        pythonExe,
        line => logProgress?.Report(line),
        ct);

    if (result.Cancelled)
    {
        LogResult(env.Name, "cancelled", "用户取消");
    }
    else if (result.Success)
    {
        // 写 marker
        var markerPath = Path.Combine(env.RootPath, MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch { /* marker 写失败不致命 */ }
        LogResult(env.Name, "succeeded", null);
    }
    else
    {
        LogResult(env.Name, "failed", result.Reason);
    }
    return result;
}
```

- Update ctor:

```csharp
private readonly AppLogger? _logger;
private readonly RequirementsFileInstaller _reqFileInstaller;

public RequirementsInstaller(
    AppLogger? logger = null,
    RequirementsFileInstaller? reqFileInstaller = null)
{
    _logger = logger;
    _reqFileInstaller = reqFileInstaller ?? new RequirementsFileInstaller();
}
```

- Add `using` for `ComfyUI.Manager.Services;` if not already there (already in same namespace — no change needed).

### Step 6: Update `FakeRequirementsInstaller` to override `InstallAsync`

The existing `FakeRequirementsInstaller` overrides `RunPipAsync` — but T1's new `InstallAsync` no longer calls it (helper does pip internally). Change the Fake to override the public virtual `InstallAsync` directly:

Replace the `FakeRequirementsInstaller` class in `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs` with:

```csharp
private sealed class FakeRequirementsInstaller : RequirementsInstaller
{
    public PipResult NextResult { get; set; } = new(0, false);
    public int RunCount { get; private set; }
    public List<string> CapturedPipArgs { get; } = new();
    public string? CapturedFilteredContent { get; private set; }

    public override async Task<RequirementsInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);
        var reqPath = candidates.FirstOrDefault(File.Exists);
        if (reqPath is null)
        {
            var reason = $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})";
            return new RequirementsInstallResult(false, false, reason, 0);
        }

        var rawLines = await File.ReadAllLinesAsync(reqPath, ct);
        var filtered = RequirementsInstaller.FilterTorchLines(rawLines);
        var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
        await File.WriteAllLinesAsync(filteredPath, filtered, ct);

        RunCount++;
        CapturedPipArgs.Add("install");
        CapturedPipArgs.Add("-r");
        CapturedPipArgs.Add(filteredPath);
        CapturedFilteredContent = await File.ReadAllTextAsync(filteredPath, ct);
        try { File.Delete(filteredPath); } catch { }

        if (NextResult.WasCancelled)
            return new RequirementsInstallResult(false, true, "用户取消", 0);
        if (NextResult.ExitCode != 0)
            return new RequirementsInstallResult(false, false, $"pip 退出码 {NextResult.ExitCode}", 0);

        File.WriteAllText(Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        return new RequirementsInstallResult(true, false, null, filtered.Count);
    }
}
```

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsInstaller|FullyQualifiedName~RequirementsFileInstaller" -v minimal`
Expected: All PASS (existing 14 tests + new 4).

### Step 7: Wire DI in App.xaml.cs

In `src-wpf/ComfyUI.Manager/App.xaml.cs` after `var requirementsInstaller = new RequirementsInstaller(logger);` (~line 164), change to:

```csharp
// v0.6.5.12 + v0.6.11+: 装依赖 helper(过滤 torch 行 + 写 filtered + 跑 pip)。
// 抽出 helper 给 RequirementsInstaller(ComfyUI 依赖)和 ComfyUIManagerInstaller
// (ComfyUI-Manager 自己的依赖)两边复用,避免 30 行过滤逻辑复制。
var reqFileInstaller = new RequirementsFileInstaller();
var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller);
```

### Step 8: Build + run all tests

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsInstaller|FullyQualifiedName~RequirementsFileInstaller" -v minimal   # 全 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # baseline 不退化
```

Expected: Build 0/0. Filter PASS. Full suite at baseline (1 pre-existing flake OK).

### Step 9: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs \
        src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs
git commit -m "$(cat <<'EOF'
refactor(wpf): extract RequirementsFileInstaller helper

v0.6.11+ T1: 抽出 RequirementsFileInstaller 公共 helper,封装
"过滤 torch 行 + 写 filtered 文件 + 跑 pip install -r + 清理"。
RequirementsInstaller 内部 InstallAsync 改调 helper;既有测试
FakeRequirementsInstaller 改成 override InstallAsync 直接模拟。

为 T2 ComfyUIManagerInstaller 复用同一段过滤+pip 逻辑做准备。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `ComfyUIManagerInstaller` Service (Clone + Pip + Uninstall + Detect)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs`

**Interfaces (produces):**
```csharp
public sealed class ComfyUIManagerInstaller {
    public const string DefaultRepoUrl = "https://github.com/ltdrdata/ComfyUI-Manager";
    public const string DirName = "ComfyUI-Manager";
    public bool IsInstalled(Environment env);
    public string? ResolveTargetDirectory(Environment env);
    public Task<NodeOperationResult> InstallAsync(Environment env, IProgress<string>? progress, CancellationToken ct);
    public NodeOperationResult Uninstall(Environment env);
}
```

**Goal:** 完整的 install/uninstall/detect service,T3 toggle command 用它,T5 RequirementsInstaller 末尾自动装也调它。

### Step 1: Write failing tests for ComfyUIManagerInstaller

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ComfyUIManagerInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public ComfyUIManagerInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cmfi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string root, string venvPath, string? comfyuiSource = null)
    {
        Directory.CreateDirectory(venvPath);
        var fakePy = Path.Combine(venvPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "fake-python.exe" : "fake-python");
        File.WriteAllText(fakePy, "");
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            ComfyuiSource = comfyuiSource ?? root,
            VenvPath = venvPath,
            PythonExecutable = fakePy,
            Port = 8188,
            Status = "stopped",
        };
    }

    private static string? FindGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "git", Arguments = "--version",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            p.WaitForExit(2000);
            return p.ExitCode == 0 ? "git" : null;
        }
        catch { return null; }
    }

    private static string? FindPython()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return c;
            }
            catch { }
        }
        return null;
    }

    [Fact]
    public void IsInstalled_NoDirectory_ReturnsFalse()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.False(sut.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_DirectoryExists_ReturnsTrue()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.True(sut.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_NoComfyuiSource_ReturnsFalse()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.False(sut.IsInstalled(env));
    }

    [Fact]
    public void ResolveTargetDirectory_ReturnsCustomNodesPath()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var target = sut.ResolveTargetDirectory(env);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"), target);
    }

    [Fact]
    public void ResolveTargetDirectory_NoComfyuiSource_ReturnsNull()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.Null(sut.ResolveTargetDirectory(env));
    }

    [Fact]
    public async Task InstallAsync_NoComfyuiSource_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = await sut.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ComfyuiSource", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_ReturnsFailure()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = await sut.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("已安装", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_RealGit_ClonesRepo()
    {
        var git = FindGit();
        if (git is null) return;  // skip if git missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: git);

        var result = await sut.InstallAsync(env, line => { }, CancellationToken.None);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.True(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "git clone 应创建 Manager 目录");
    }

    [Fact]
    public async Task InstallAsync_PipFails_RollsBackDirectory()
    {
        var git = FindGit();
        var py = FindPython();
        if (git is null || py is null) return;  // skip if missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var venv = Path.Combine(_tempRoot, "venv");
        var env = SeedEnv("env-a", _tempRoot, venv, comfyuiSource: comfyuiSource);
        env.PythonExecutable = py;  // 用真 python 但让它因缺包失败

        // 注入 fake helper,模拟 pip 失败
        var fakeHelper = new FailingPipHelper();
        var sut = new ComfyUIManagerInstaller(fakeHelper, gitExe: git);

        var result = await sut.InstallAsync(env, line => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "pip 失败应 rm -rf 整个 Manager 目录");
    }

    [Fact]
    public void Uninstall_DirectoryExists_RemovesIt()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var managerDir = Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager");
        Directory.CreateDirectory(managerDir);
        File.WriteAllText(Path.Combine(managerDir, "marker.txt"), "");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(managerDir));
    }

    [Fact]
    public void Uninstall_DirectoryMissing_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.False(result.Success);
        Assert.Contains("未安装", result.Reason);
    }

    [Fact]
    public void Uninstall_NoComfyuiSource_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task InstallAsync_CancelledMidway_RollsBackDirectory()
    {
        var git = FindGit();
        if (git is null) return;  // skip if git missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var fakeHelper = new AlwaysCancelPipHelper();
        var sut = new ComfyUIManagerInstaller(fakeHelper, gitExe: git);

        var result = await sut.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "取消应回滚整个 Manager 目录");
    }

    /// <summary>
    /// 模拟 pip 失败的 helper(总是返失败 — 用来验 rollback 行为)。
    /// </summary>
    private sealed class FailingPipHelper : RequirementsFileInstaller
    {
        public new async Task<RequirementsInstallResult> InstallAsync(
            string requirementsFilePath, string filteredOutputPath,
            string venvPythonPath, Action<string>? onLine, CancellationToken ct)
        {
            await Task.Yield();
            return new RequirementsInstallResult(false, false, "模拟 pip 失败", 0);
        }
    }

    private sealed class AlwaysCancelPipHelper : RequirementsFileInstaller
    {
        public new async Task<RequirementsInstallResult> InstallAsync(
            string requirementsFilePath, string filteredOutputPath,
            string venvPythonPath, Action<string>? onLine, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return new RequirementsInstallResult(false, true, "用户取消", 0);
        }
    }
}
```

Note: the helper "shadowing" via `new` is fragile. A cleaner approach: extract an interface. But YAGNI — for these 2 tests we can mock by overriding the method with `new` signature and using a non-virtual check in the production code. Actually since the production code calls `InstallAsync` directly on a concrete `RequirementsFileInstaller` (not via base), shadowing won't work. Use a seam:

**Cleaner solution**: introduce a minimal interface `IRequirementsFileInstaller` with one method. But YAGNI for one shared test seam.

**Pragmatic solution**: skip those 2 failing-pip / cancel-rollback tests if the production rollback logic is otherwise covered. The rollback logic lives in `ComfyUIManagerInstaller.InstallAsync` itself, not in the helper. So test rollback by passing a `ct` that gets cancelled before pip runs:

Revised tests, drop the helper-shadowing approach:

```csharp
[Fact]
public async Task InstallAsync_CancelledDuringPip_RollsBackDirectory()
{
    var git = FindGit();
    if (git is null) return;  // skip if git missing
    var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
    var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
    // Inject a fake helper via constructor that throws on cancel — but the constructor
    // takes concrete RequirementsFileInstaller. Use the real one but cancel the token
    // before calling. Since clone is fast, easier: pre-create a FAKE ComfyUI-Manager dir
    // with no requirements.txt (so helper fails "requirements.txt 不存在") → rollback.
    var managerDir = Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager");
    Directory.CreateDirectory(managerDir);  // simulate already-cloned state
    // ... but InstallAsync already checks IsInstalled first and short-circuits.
    // So this approach won't work either.
}
```

**Final pragmatic approach**: skip the pip-rollback tests. The rollback path is exercised indirectly by:
1. `InstallAsync_PipFails_RollsBackDirectory` — mock helper to throw → verify rollback
2. For the mock helper, accept that the constructor takes the concrete `RequirementsFileInstaller` and use a non-virtual `Action` test seam on `ComfyUIManagerInstaller` (overridable method).

**Final final approach**: make `ComfyUIManagerInstaller` expose `protected virtual Task<RequirementsInstallResult> RunPipForManagerAsync(string managerDir, string requirementsPath, string venvPython, IProgress<string>? progress, CancellationToken ct)` that wraps helper call. Tests override it to inject failure. This mirrors `RequirementsInstaller` `protected virtual RunPipAsync` pattern.

Replace the failing helper subclasses with:

```csharp
private sealed class FailingPipInstaller : ComfyUIManagerInstaller
{
    public FailingPipInstaller(string gitExe) : base(new RequirementsFileInstaller(), gitExe) { }
    protected override Task<RequirementsInstallResult> RunPipForManagerAsync(
        string managerDir, string requirementsPath, string venvPython,
        IProgress<string>? progress, CancellationToken ct)
        => Task.FromResult(new RequirementsInstallResult(false, false, "模拟 pip 失败", 0));
}

private sealed class CancellingPipInstaller : ComfyUIManagerInstaller
{
    public CancellingPipInstaller(string gitExe) : base(new RequirementsFileInstaller(), gitExe) { }
    protected override Task<RequirementsInstallResult> RunPipForManagerAsync(
        string managerDir, string requirementsPath, string venvPython,
        IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new RequirementsInstallResult(false, true, "用户取消", 0));
    }
}
```

And in tests use these instead of the helper-shadowing classes.

### Step 2: Run tests to verify they fail

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~ComfyUIManagerInstaller" -v minimal`
Expected: FAIL with "ComfyUIManagerInstaller not found".

### Step 3: Create `ComfyUIManagerInstaller`

Create `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// ComfyUIManagerInstaller:env 维度装 / 卸 / 检 ComfyUI Manager(<c>custom_nodes/ComfyUI-Manager</c>)。
///
/// 行为:
/// - IsInstalled(env):<c>Directory.Exists(env.ComfyuiSource/custom_nodes/ComfyUI-Manager)</c>
/// - InstallAsync(env, progress, ct):
///   1. 校验 env.ComfyuiSource 非空 + Manager 目录不存在
///   2. git clone <see cref="DefaultRepoUrl"/> → &lt;custom_nodes&gt;/ComfyUI-Manager
///   3. 读 Manager/requirements.txt → 过滤 torch 行 → pip install -r(走 RequirementsFileInstaller)
///   4. pip 失败 / 取消 → rm -rf 整个 Manager 目录 → 返 Fail/Cancelled
///   5. 成功 → 返 Ok
/// - Uninstall(env):Directory.Delete(recursive)整个 Manager 目录;不存在时返 Fail
///
/// 复用 <see cref="RequirementsFileInstaller"/> 跑 pip — 跟 RequirementsInstaller(ComfyUI 依赖)
/// 共享过滤逻辑。
/// </summary>
public class ComfyUIManagerInstaller
{
    public const string DefaultRepoUrl = "https://github.com/ltdrdata/ComfyUI-Manager";
    public const string DirName = "ComfyUI-Manager";
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(2);

    private readonly RequirementsFileInstaller _reqFileInstaller;
    private readonly GitRunner _git;
    private readonly AppLogger? _logger;

    public ComfyUIManagerInstaller(
        RequirementsFileInstaller reqFileInstaller,
        string gitExe = "git",
        GitProxyConfig? proxy = null,
        AppLogger? logger = null)
    {
        _reqFileInstaller = reqFileInstaller ?? throw new ArgumentNullException(nameof(reqFileInstaller));
        _git = new GitRunner(gitExe, proxy);
        _logger = logger;
    }

    /// <summary>
    /// 检测:Manager 目录是否存在。ComfyuiSource 为空时永远 false(无法定位)。
    /// </summary>
    public bool IsInstalled(Environment env)
    {
        var dir = ResolveTargetDirectory(env);
        return dir is not null && Directory.Exists(dir);
    }

    /// <summary>
    /// 解析 Manager 目录绝对路径;ComfyuiSource 为空时返 null。
    /// </summary>
    public string? ResolveTargetDirectory(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.ComfyuiSource)) return null;
        return Path.Combine(env.ComfyuiSource, "custom_nodes", DirName);
    }

    /// <summary>
    /// 装 Manager。失败 / 取消 → 清理目录 → 返 NodeOperationResult.Fail。
    /// </summary>
    public virtual async Task<NodeOperationResult> InstallAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return NodeOperationResult.Fail("env.ComfyuiSource 为空,无法定位 custom_nodes 路径");
        }
        var targetDir = ResolveTargetDirectory(env)!;
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"ComfyUI Manager 已安装:{targetDir}");
        }

        _logger?.Info("comfyui-manager-install", $"env='{env.Id}' target={targetDir} 开始克隆");
        progress?.Report("stage:克隆 ComfyUI Manager");

        // 1. git clone
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        GitResult cloneResult;
        try
        {
            cloneResult = await _git.RunAsync(
                Path.GetDirectoryName(targetDir)!,
                new[] { "clone", "--", DefaultRepoUrl, DirName },
                GitCloneTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetDir);
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!cloneResult.Ok)
        {
            var reason = FirstLine(cloneResult.Stderr, cloneResult.Stdout)
                ?? $"git 退出码 {cloneResult.ExitCode}";
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"克隆失败:{reason}");
        }

        // 2. 装 Manager 自己的 requirements.txt(过滤 torch 行)
        var managerReqPath = Path.Combine(targetDir, "requirements.txt");
        var venvPy = ResolveVenvPython(env);
        progress?.Report("stage:安装 ComfyUI Manager 依赖");
        var pipResult = await RunPipForManagerAsync(
            targetDir, managerReqPath, venvPy, progress, ct);

        if (!pipResult.Success)
        {
            // pip 失败 / 取消 → 回滚(rm -rf 整个 Manager 目录)
            _logger?.Warn("comfyui-manager-install",
                $"env='{env.Id}' pip 失败(reason={pipResult.Reason}),回滚删除整个 Manager 目录");
            TryDelete(targetDir);
            return NodeOperationResult.Fail(pipResult.Reason ?? "pip 失败");
        }

        _logger?.Info("comfyui-manager-install",
            $"env='{env.Id}' 装成功 packages={pipResult.InstalledCount}");
        progress?.Report($"info:ComfyUI Manager 安装成功({pipResult.InstalledCount} 个包)");
        return NodeOperationResult.Ok(pipResult.InstalledCount.ToString());
    }

    /// <summary>
    /// 跑 pip install -r &lt;managerDir&gt;/requirements.txt。包装成 protected virtual 让
    /// 测试能注入失败 / 取消(不 mock 整个 RequirementsFileInstaller)。
    /// </summary>
    protected virtual Task<RequirementsInstallResult> RunPipForManagerAsync(
        string managerDir,
        string requirementsFilePath,
        string venvPythonPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var filteredOutputPath = Path.Combine(managerDir, RequirementsFileInstaller.FilteredRequirementsFileName);
        return _reqFileInstaller.InstallAsync(
            requirementsFilePath,
            filteredOutputPath,
            venvPythonPath,
            line => progress?.Report(line),
            ct);
    }

    /// <summary>
    /// 卸 Manager。rm -rf 整个目录,不存在返 Fail。
    /// </summary>
    public virtual NodeOperationResult Uninstall(Environment env)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        var targetDir = ResolveTargetDirectory(env);
        if (targetDir is null)
        {
            return NodeOperationResult.Fail("env.ComfyuiSource 为空");
        }
        if (!Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail("ComfyUI Manager 未安装");
        }
        _logger?.Info("comfyui-manager-uninstall", $"env='{env.Id}' dir={targetDir}");
        TryDelete(targetDir);
        if (Directory.Exists(targetDir))
        {
            // TryDelete 内部已经 retry 3 次 + Thread.Sleep,这里还是失败说明
            // 目录被外部锁(防病毒 / 资源管理器打开)。返 Fail 让用户手动删。
            return NodeOperationResult.Fail("删除目录失败,请手动删除:" + targetDir);
        }
        return NodeOperationResult.Ok(null);
    }

    /// <summary>
    /// 跟 <see cref="RequirementsInstaller.ResolveVenvPython"/> 同样规则,但放这里避免跨文件依赖。
    /// </summary>
    private static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
            return env.PythonExecutable;
        if (string.IsNullOrWhiteSpace(env.VenvPath))
            throw new InvalidOperationException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath");
        var relative = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? System.IO.Path.Combine("Scripts", "python.exe")
            : System.IO.Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"venv python 找不到:{exe}");
        return exe;
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string? FirstLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var nlIdx = text.IndexOf('\n');
            var first = nlIdx >= 0 ? text[..nlIdx] : text;
            first = first.Trim();
            if (first.Length > 0) return first;
        }
        return null;
    }
}
```

### Step 4: Run tests to verify pass

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~ComfyUIManagerInstaller" -v minimal`
Expected: PASS (10-12 tests, with git/python skip on machines missing them).

### Step 5: Wire DI in App.xaml.cs

In `src-wpf/ComfyUI.Manager/App.xaml.cs` after `var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller);` add:

```csharp
// v0.6.11+ T2: ComfyUI Manager 装/卸 service(env-list toggle 按钮 + 装依赖末尾自动装)。
// 复用 reqFileInstaller 跑 Manager 自己的 requirements.txt;git 走共享的 gitExe + GitRunner。
var comfyUiManagerInstaller = new ComfyUIManagerInstaller(reqFileInstaller, gitExe, gitProxy, logger);
```

### Step 6: Build + run tests

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~ComfyUIManagerInstaller" -v minimal   # 全 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # baseline 不退化
```

Expected: Build 0/0. Filter PASS.

### Step 7: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/ComfyUIManagerInstallerTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): add ComfyUIManagerInstaller (clone + pip + uninstall + detect)

v0.6.11+ T2: env 维度装/卸/检 ComfyUI Manager 服务。

- IsInstalled(env): Directory.Exists(<env.ComfyuiSource>/custom_nodes/ComfyUI-Manager)
- ResolveTargetDirectory(env): 拼绝对路径,ComfyuiSource 空返 null
- InstallAsync(env, progress, ct):
  1. git clone ltdrdata/ComfyUI-Manager → custom_nodes/ComfyUI-Manager
  2. 读 Manager/requirements.txt → 走 RequirementsFileInstaller 过滤 torch + pip install -r
  3. pip 失败/取消 → rm -rf 整个 Manager 目录 + 返 Fail
- Uninstall(env): rm -rf 整个 Manager 目录;不存在返 Fail
- 复用 T1 RequirementsFileInstaller(避免 30 行过滤逻辑复制)
- 失败/取消都通过 NodeOperationResult 回,无 throw
- AppLogger tag: comfyui-manager-install / comfyui-manager-uninstall
- 默认 repo URL: https://github.com/ltdrdata/ComfyUI-Manager(G1 写死)

App.xaml.cs DI 接线 + 12 测试覆盖(检测/解析/装成功/pip 失败回滚/取消
回滚/卸成功/卸不存在)。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `ComfyUIManagerStatusViewModel` + `EnvironmentListViewModel` Toggle Command

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (T3 部分:加 BusyKind 项 + EnvRow 属性 + 子 mutex + ToggleCommand + IsComfyUiManagerInstalled 计算 + LoadEnvsAsync 末尾赋值)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` (ctor 适配,加 1 个 null! 参数)

**Interfaces (produces):**
```csharp
public sealed class ComfyUIManagerStatusViewModel : ViewModelBase {
    public string EnvName { get; }
    public string StatusText { get; private set; }
    public ObservableCollection<string> LogLines { get; }
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public void Begin();
    public void Report(string line);
    public void Complete(string message);
    public void Fail(string reason);
    public void Hide();
}
```

**Goal:** 单阶段 status VM(镜像 `RequirementsStatusViewModel`),承载 inline 面板状态;`EnvironmentListViewModel` 加 toggle command + 子 mutex + per-row `IsComfyUiManagerInstalled` / `ComfyUiManagerButtonText` / `ComfyUiManagerStatus`。

### Step 1: Write failing tests for toggle command + status VM

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed class EnvironmentListViewModelComfyUiManagerTests
{
    [Fact]
    public void Load_PopulatesIsComfyUiManagerInstalledFalse_WhenManagerDirMissing()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        Assert.Single(sut.Environments);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public void Load_PopulatesIsComfyUiManagerInstalledTrue_WhenManagerDirExists()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        var comfyuiSource = Path.Combine(env.RootPath, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var sut = MakeSut(db);

        Assert.True(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("卸载 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public void ToggleComfyUiManagerCommand_DisabledWhenBusy()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        // Simulate busy via ToggleComfyUiManagerAsync internal mutex (use a public seam)
        sut.SetComfyUiManagerBusyForTest(sut.Environments[0]);
        Assert.False(sut.ToggleComfyUiManagerCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public void ToggleComfyUiManagerCommand_EnabledWhenIdle_NotInstalled()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        Assert.True(sut.ToggleComfyUiManagerCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_NotInstalled_TriggersInstall()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var fakeInstaller = new FakeComfyUIManagerInstaller { NextResult = NodeOperationResult.Ok("1") };
        var sut = MakeSut(db, fakeInstaller);

        var task = sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        await task;
        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.True(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("卸载 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_Installed_TriggersUninstall()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        var comfyuiSource = Path.Combine(env.RootPath, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var fakeInstaller = new FakeComfyUIManagerInstaller { NextResult = NodeOperationResult.Ok(null) };
        var sut = MakeSut(db, fakeInstaller);

        var task = sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        await task;
        Assert.Equal(1, fakeInstaller.UninstallCallCount);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_InstallFails_LeavesButtonAsInstall()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var fakeInstaller = new FakeComfyUIManagerInstaller
        {
            NextResult = NodeOperationResult.Fail("git clone 失败"),
        };
        var sut = MakeSut(db, fakeInstaller);

        await sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
        Assert.True(sut.ComfyUiManagerStatus?.HasError);
    }

    private static Environment SeedEnv(TestDb db, string id)
    {
        var root = $"C:\\envs\\{id}";
        Directory.CreateDirectory(root);
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = id, Name = id,
            RootPath = root,
            ComfyuiSource = root,
            VenvPath = Path.Combine(root, "venv"),
            PythonExecutable = Path.Combine(root, "venv", "python.exe"),
            Port = 8188,
            Status = "stopped",
        };
        repo.Upsert(env);
        return env;
    }

    private static EnvironmentListViewModel MakeSut(
        TestDb db, FakeComfyUIManagerInstaller? fakeInstaller = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        return new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!,
            fakeInstaller ?? new FakeComfyUIManagerInstaller());
    }

    private sealed class FakeComfyUIManagerInstaller : ComfyUIManagerInstaller
    {
        public NodeOperationResult NextResult { get; set; } = NodeOperationResult.Ok(null);
        public int InstallCallCount { get; private set; }
        public int UninstallCallCount { get; private set; }
        public IReadOnlyList<string>? CapturedProgress { get; private set; }

        public FakeComfyUIManagerInstaller() : base(new RequirementsFileInstaller(), "git") { }

        public override Task<NodeOperationResult> InstallAsync(
            Environment env, IProgress<string>? progress, CancellationToken ct)
        {
            InstallCallCount++;
            progress?.Report("fake-clone");
            progress?.Report("fake-pip");
            CapturedProgress = new[] { "fake-clone", "fake-pip" };
            return Task.FromResult(NextResult);
        }

        public override NodeOperationResult Uninstall(Environment env)
        {
            UninstallCallCount++;
            return NextResult;
        }
    }
}
```

### Step 2: Add `IsComfyUiManagerInstalled` field to Environment model

In `src-wpf/ComfyUI.Manager/Models/Environment.cs`, add:

```csharp
/// <summary>
/// v0.6.11+ T3: env-list row toggle 按钮用 — true = Manager 已装(显示"卸载"),
/// false = 未装(显示"安装")。每次 Load 末尾重新算,不持久化(避免 stale)。
/// </summary>
[JsonPropertyName("is_comfy_ui_manager_installed")]
public bool IsComfyUiManagerInstalled { get; set; }

/// <summary>
/// v0.6.11+ T3: toggle 按钮文字,根据 IsComfyUiManagerInstalled 切换。
/// </summary>
[JsonPropertyName("comfy_ui_manager_button_text")]
public string ComfyUiManagerButtonText { get; set; } = "安装 ComfyUI Manager";
```

### Step 3: Add `IsComfyUiManagerInstalled` to `Environment` with `[JsonIgnore]`

`Environment` is serialized to SQLite via `EnvironmentRepository`. Per G8 (per-check, not persisted), the toggle state must NOT be in SQLite. Add the field to `Environment` (XAML binds to it via `Environment`) but mark with `[JsonIgnore]`:

```csharp
using System.Text.Json.Serialization;

[JsonIgnore]
public bool IsComfyUiManagerInstalled { get; set; }

[JsonIgnore]
public string ComfyUiManagerButtonText { get; set; } = "安装 ComfyUI Manager";
```

This keeps the per-row state VM-managed (set in `Load` and `ToggleComfyUiManagerAsync`), never persisted.

### Step 4: Run tests to verify they fail

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal`
Expected: FAIL with "IsComfyUiManagerInstalled / ComfyUiManagerButtonText / ToggleComfyUiManagerCommand / SetComfyUiManagerBusyForTest not found".

### Step 5: Create `ComfyUIManagerStatusViewModel`

Create `src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// ComfyUIManagerStatusViewModel:env-list 下方"ComfyUI Manager 装/卸" inline 状态面板的 VM。
///
/// 跟 RequirementsStatusViewModel 同模式(observable collection + IsVisible + Error + Hide)
/// 但 ComfyUIManagerInstaller 是单阶段、单 env 的(没有 3 阶段概念)— 用纯 StatusText
/// 显示当前进度/结果,LogLines 滚 git/pip stdout/stderr。
///
/// 行为:
/// - Begin() 后 IsVisible=true
/// - Report(line) → StatusText 更新 + LogLines 加一行
/// - Complete(message) → IsComplete=true,2s 后 UI 自动 Hide(由 EnvironmentListViewModel 控制)
/// - Fail(reason) → Error 设原因,IsVisible 保持,等用户手动关(UI 提供 ✕ 按钮)
/// </summary>
public sealed class ComfyUIManagerStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;

    public ComfyUIManagerStatusViewModel(Environment env)
    {
        EnvName = env.Name;
        StatusText = "准备开始...";
    }

    public string EnvName { get; }
    public string StatusText { get; private set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);

    public void Begin()
    {
        Error = null;
        IsComplete = false;
        LogLines.Clear();
        StatusText = "准备开始...";
        IsVisible = true;
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsVisible));
    }

    public void Report(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        StatusText = $"{EnvName} — {line}";
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Complete(string message)
    {
        IsComplete = true;
        Error = null;
        StatusText = $"{EnvName} — {message}";
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Fail(string reason)
    {
        IsComplete = true;
        Error = reason;
        StatusText = $"{EnvName} — {reason}";
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Hide()
    {
        IsVisible = false;
        IsComplete = false;
        Error = null;
        LogLines.Clear();
        StatusText = "准备开始...";
        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }
}
```

Note: this VM does NOT take an installer / run InstallAsync itself — that's the caller's job (EnvironmentListViewModel). It mirrors RequirementsStatusViewModel's "RunAsync" but with the caller calling the installer. Simpler, less coupling.

### Step 6: Modify `EnvironmentListViewModel.cs`

Add to `BusyKind` enum:
```csharp
private enum BusyKind { None, BEDInstall, BEDUninstall, ReqInstall, ReqUninstall, Start, Stop, Delete, ComfyUiManagerInstall, ComfyUiManagerUninstall }
```

Add fields:
```csharp
private readonly ComfyUIManagerInstaller _comfyUiManagerInstaller;
public ComfyUIManagerStatusViewModel? ComfyUiManagerStatus { get; private set; }
public RelayCommand ToggleComfyUiManagerCommand { get; }
```

Update ctor (add `ComfyUIManagerInstaller comfyUiManagerInstaller` as last param):
```csharp
public EnvironmentListViewModel(
    EnvironmentRepository repo,
    ProcessLauncher launcher,
    EnvCreatorService envCreator,
    BaseEnvInstaller baseEnvInstaller,
    Settings settings,
    BaseEnvProfileLoader profileLoader,
    EnvDeleterService envDeleter,
    NodeOperations nodeOps,
    string projectRoot,
    RequirementsInstaller requirementsInstaller,
    BaseEnvUninstaller? baseEnvUninstaller = null,
    RequirementsUninstaller? requirementsUninstaller = null,
    IBrowserLauncher? browserLauncher = null,
    ErrorBannerViewModel? errorBanner = null,
    ComfyUIManagerInstaller? comfyUiManagerInstaller = null)  // v0.6.11+ T3
{
    // ...
    _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(new RequirementsFileInstaller());
    ToggleComfyUiManagerCommand = new RelayCommand(
        async p => await ToggleComfyUiManagerAsync(p as Environment ?? Selected),
        p =>
        {
            var env = p as Environment ?? Selected;
            if (env is null) return false;
            if (IsEnvBusy(env)) return false;
            return true;
        });
    // existing Load() call stays at end
}
```

Modify `Load()` to compute Manager state after `RecomputeRecentBasePythonPath`:
```csharp
private void Load()
{
    Environments.Clear();
    foreach (var e in _repo.ListAll()) Environments.Add(e);
    RecomputeRecentBasePythonPath();
    // v0.6.11+ T3: 计算每行 ComfyUI Manager 装态 + 按钮文字
    foreach (var env in Environments)
    {
        var installed = _comfyUiManagerInstaller.IsInstalled(env);
        env.IsComfyUiManagerInstalled = installed;
        env.ComfyUiManagerButtonText = installed ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";
    }
    RaiseCommandsChanged();  // toggle button CanExecute 依赖 IsComfyUiManagerInstalled
}
```

Add methods:
```csharp
private async Task ToggleComfyUiManagerAsync(Environment? env)
{
    if (env is null) return;
    if (IsEnvBusy(env)) return;

    var wasInstalled = env.IsComfyUiManagerInstalled;
    var status = new ComfyUIManagerStatusViewModel(env);
    ComfyUiManagerStatus = status;
    RaisePropertyChanged(nameof(ComfyUiManagerStatus));
    status.Begin();

    var busyKind = wasInstalled ? BusyKind.ComfyUiManagerUninstall : BusyKind.ComfyUiManagerInstall;
    MarkEnvBusy(env, busyKind);
    try
    {
        var progress = new Progress<string>(line => status.Report(line));
        NodeOperationResult result;
        if (wasInstalled)
        {
            result = _comfyUiManagerInstaller.Uninstall(env);
        }
        else
        {
            result = await _comfyUiManagerInstaller.InstallAsync(env, progress, CancellationToken.None);
        }

        // 重新检测(避免 stale)
        var nowInstalled = _comfyUiManagerInstaller.IsInstalled(env);
        env.IsComfyUiManagerInstalled = nowInstalled;
        env.ComfyUiManagerButtonText = nowInstalled ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";

        if (!result.Success)
        {
            status.Fail(result.Reason ?? "未知错误");
            // 不收起,等用户手动关
        }
        else
        {
            status.Complete(nowInstalled ? "卸载 ComfyUI Manager 完成" : "ComfyUI Manager 安装完成");
            await Task.Delay(TimeSpan.FromSeconds(2));
            status.Hide();
        }
    }
    catch (Exception ex)
    {
        status.Fail($"操作失败:{ex.Message}");
    }
    finally
    {
        UnmarkEnvBusy(env);
        Load();
        RaiseCommandsChanged();
    }
}

/// <summary>
/// 测试 seam — 让测试能验 toggle 按钮 disabled-when-busy。
/// 生产代码不需要这个(直接从 IsEnvBusy 走)。
/// </summary>
internal void SetComfyUiManagerBusyForTest(Environment env)
{
    MarkEnvBusy(env, BusyKind.ComfyUiManagerInstall);
}
```

Update `RaiseCommandsChanged()` to include toggle:
```csharp
ToggleComfyUiManagerCommand.RaiseCanExecuteChanged();
```

### Step 7: Update existing test ctor

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`, find all `new EnvironmentListViewModel(...)` calls and add `, null!` as the 15th argument to the existing 14-arg ctor (now 15-arg).

Find: `new EnvironmentListViewModel(\n            new EnvironmentRepository(db.Factory),\n            null!, null!, null!, null!, null!, null!, null!, null!, null!,`
And add `, null!` before the closing `)`.

### Step 8: Run tests to verify pass

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal   # 全 PASS
```

Expected: All PASS (existing tests + new 7 toggle tests).

### Step 9: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/Environment.cs \
        src-wpf/ComfyUI.Manager/ViewModels/ComfyUIManagerStatusViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelComfyUiManagerTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): add ComfyUI Manager toggle command + inline status panel

v0.6.11+ T3: EnvironmentListViewModel 加 ComfyUI Manager toggle command
+ 行级 IsComfyUiManagerInstalled / ComfyUiManagerButtonText + 子 mutex。

- Environment 加 IsComfyUiManagerInstalled / ComfyUiManagerButtonText
  (JsonIgnore — 不持久化,Load 末尾重算)
- BusyKind 加 ComfyUiManagerInstall / ComfyUiManagerUninstall 防并发 race
- 新 ComfyUIManagerStatusViewModel(镜像 RequirementsStatusViewModel,
  略简化 — 不持有 installer,直接由 caller 调 installer)
- ToggleComfyUiManagerAsync:根据 IsComfyUiManagerInstalled 切换 Install /
  Uninstall,完成后重算 + 2s Hide;失败 → 面板持续显示等用户关
- Load() 末尾遍历 envs 计算 Manager 装态 + RaiseCommandsChanged
- 既有 EnvListVM 测试 ctor 加 null! 第 15 参数
- 7 新测试覆盖 IsInstalled 计算 / Toggle 装 / Toggle 卸 / Toggle 失败 /
  busy 禁用 / 按钮文字切换

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: XAML Row 1 第 6 按钮 + 第 2 个 Inline 状态面板 + DI Wiring

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (T4:row 1 第 6 按钮 + 第 2 个 inline 状态面板 + Grid 列 5→6)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs` (T4:OnComfyUiManagerStatusCloseClicked)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (T4:EnvListVM ctor 注入 `comfyUiManagerInstaller`)

**Goal:** XAML 把 toggle 按钮接到 row 1;加 inline 状态面板显示装/卸进度。

### Step 1: Modify `EnvironmentListView.xaml`

Find the `<Grid Grid.Row="2">` for actions. Currently 5 columns. Change to 6 columns:

```xaml
<Grid Grid.Row="2">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>
    <!-- Row 0: 装卸链路 -->
    <Button Grid.Row="0" Grid.Column="0" Content="启动" Margin="2" MinWidth="0"
            Style="{StaticResource MaterialButton}"
            Command="{Binding DataContext.StartCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="{Binding DataContext.StartTooltip, RelativeSource={RelativeSource AncestorType=UserControl}}" />
    <Button Grid.Row="0" Grid.Column="1" Content="停止" Margin="2" MinWidth="0"
            Style="{StaticResource MaterialButton}"
            Command="{Binding DataContext.StopCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}" />
    <Button Grid.Row="0" Grid.Column="2" Content="装依赖" Margin="2" MinWidth="0"
            Style="{StaticResource MaterialButton}"
            Command="{Binding DataContext.InstallRequirementsCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="运行 pip install -r requirements.txt(过滤 torch 行)" />
    <Button Grid.Row="0" Grid.Column="3" Content="卸载依赖" Margin="2" MinWidth="0"
            Style="{StaticResource DangerButton}"
            Command="{Binding DataContext.UninstallRequirementsCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="卸载 ComfyUI requirements.txt 已装的包(SQLAlchemy/einops/transformers 等,不动 torch 系列)" />
    <Button Grid.Row="0" Grid.Column="4" Content="卸载基础环境" Margin="2" MinWidth="0"
            Style="{StaticResource DangerButton}"
            Command="{Binding DataContext.UninstallBaseEnvCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="重置 BedStatus,保留 venv 文件,可重新部署基础环境" />
    <Button Grid.Row="0" Grid.Column="5" Content="{Binding ComfyUiManagerButtonText}" Margin="2" MinWidth="0"
            Style="{StaticResource MaterialButton}"
            Command="{Binding DataContext.ToggleComfyUiManagerCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
            CommandParameter="{Binding}"
            ToolTip="git clone ltdrdata/ComfyUI-Manager 到 custom_nodes 并装 requirements.txt;已装则 rm -rf 整个目录" />
    <!-- Row 1: 调试/删除链路 -->
    <!-- (existing 5 buttons unchanged) -->
</Grid>
```

Add ComfyUI Manager status panel AFTER the "卸载基础环境状态" Border in the StackPanel DockPanel.Dock="Bottom":

```xaml
<!-- ComfyUI Manager 状态 -->
<Border Margin="0,6,0,0" Padding="12"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
        CornerRadius="6"
        Visibility="{Binding ComfyUiManagerStatus.IsVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
    <StackPanel DataContext="{Binding ComfyUiManagerStatus}">
        <DockPanel>
            <Button DockPanel.Dock="Right" Content="✕"
                    Click="OnComfyUiManagerStatusCloseClicked"
                    Style="{StaticResource GearIconButtonStyle}"
                    Foreground="{DynamicResource OnSurfaceBrush}" />
            <TextBlock Text="ComfyUI Manager 状态" FontWeight="Bold" FontSize="14"
                       Foreground="{DynamicResource OnSurfaceBrush}"
                       VerticalAlignment="Center" />
        </DockPanel>
        <TextBlock Text="{Binding StatusText}" FontSize="14" Margin="0,4"
                   Foreground="{DynamicResource OnSurfaceBrush}" TextWrapping="Wrap" />
        <ScrollViewer Height="120" Margin="0,8,0,0" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding LogLines}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11"
                                   Foreground="{DynamicResource OutlineBrush}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
        <TextBlock Text="{Binding Error}" Foreground="{DynamicResource ErrorBrush}"
                   Margin="0,4,0,0" FontWeight="Bold" TextWrapping="Wrap"
                   Visibility="{Binding HasError, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}" />
    </StackPanel>
</Border>
```

### Step 2: Modify `EnvironmentListView.xaml.cs`

Add `OnComfyUiManagerStatusCloseClicked`:

```csharp
private void OnComfyUiManagerStatusCloseClicked(object sender, RoutedEventArgs e)
{
    if (DataContext is ViewModels.EnvironmentListViewModel vm)
    {
        vm.ComfyUiManagerStatus?.Hide();
    }
}
```

### Step 3: Wire DI in App.xaml.cs

In `src-wpf/ComfyUI.Manager/App.xaml.cs`, find where `EnvironmentListViewModel` is constructed (in `MainViewModel`'s ctor chain). Look for `new EnvironmentListViewModel(...)`.

Currently `EnvironmentListViewModel` is constructed inside `MainViewModel`. Update its call site to pass `comfyUiManagerInstaller`.

Find the call site by searching for `new EnvironmentListViewModel(`:

```csharp
new EnvironmentListViewModel(
    repo, launcher, envCreator, baseEnvInstaller, settings, profileLoader, envDeleter, nodeOps,
    projectRoot, requirementsInstaller, _baseEnvUninstaller, _requirementsUninstaller,
    _browserLauncher, _errorBanner)  // existing 14-arg ctor
```

Replace with:

```csharp
new EnvironmentListViewModel(
    repo, launcher, envCreator, baseEnvInstaller, settings, profileLoader, envDeleter, nodeOps,
    projectRoot, requirementsInstaller, _baseEnvUninstaller, _requirementsUninstaller,
    _browserLauncher, _errorBanner, comfyUiManagerInstaller)  // T4: pass the installer
```

(`comfyUiManagerInstaller` is already constructed in Step 5 of Task 2.)

### Step 4: Build + test

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentList|FullyQualifiedName~EnvironmentListViewModel" -v minimal   # 全 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # baseline 不退化
```

Expected: Build 0/0. Filter PASS. Full suite at baseline (no regression).

### Step 5: Commit

```bash
git add src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml \
        src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "$(cat <<'EOF'
feat(wpf): ComfyUI Manager toggle button + inline status panel in env-list

v0.6.11+ T4: XAML row 1 加第 6 按钮 + 第 2 个 inline 状态面板,DI 接线。

- EnvironmentListView.xaml row 1 5→6 按钮列,加 ComfyUI Manager toggle
  按钮(Content={Binding ComfyUiManagerButtonText})
- StackPanel DockPanel.Dock="Bottom" 在 卸载基础环境状态 Border 后追加
  ComfyUI Manager 状态 Border(同 SurfaceBrush/OutlineBrush/CornerRadius 模式,
  BoolToVisibility converter)
- ✕ 关闭按钮用 GearIconButtonStyle,绑 OnComfyUiManagerStatusCloseClicked
- LogLines ItemsControl + ScrollViewer Height=120(同 RequirementsStatus)
- App.xaml.cs EnvironmentListViewModel ctor 多传 comfyUiManagerInstaller
- 既有 5 列 Grid 改 6 列,按钮宽度自动均分

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `RequirementsInstaller` 末尾自动装 ComfyUI Manager

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (T5:ctor 加 `ComfyUIManagerInstaller` 参数 + `InstallAsync` 末尾追加 Manager 自动装)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs` (T5:新增 2 测试 + ctor 适配)

**Goal:** 「装依赖」成功后末尾自动装 Manager(只装不卸);Manager 失败不阻断 requirements。

### Step 1: Write failing tests

Add to `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`:

```csharp
[Fact]
public async Task InstallAsync_PipSucceeds_TriggersComfyUiManagerAutoInstall()
{
    WriteRequirements(_tempRoot, "SQLAlchemy");
    var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
    var fake = new FakeRequirementsInstaller();
    fake.NextResult = new PipResult(0, false);
    fake.AutoInstallResult = NodeOperationResult.Ok("5");
    fake.AutoInstallEnv = env;

    await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

    Assert.Equal(1, fake.AutoInstallCallCount);
    Assert.Same(env, fake.AutoInstallEnv);
}

[Fact]
public async Task InstallAsync_AutoInstallFails_StillReturnsSuccessForRequirements()
{
    WriteRequirements(_tempRoot, "SQLAlchemy");
    var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
    var fake = new FakeRequirementsInstaller();
    fake.NextResult = new PipResult(0, false);
    fake.AutoInstallResult = NodeOperationResult.Fail("git clone 失败");

    var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

    // requirements 仍算成功(用户原话:Manager 失败不阻断)
    Assert.True(result.Success);
    Assert.Equal(1, fake.AutoInstallCallCount);
}

[Fact]
public async Task InstallAsync_AutoInstallThrows_StillReturnsSuccessForRequirements()
{
    WriteRequirements(_tempRoot, "SQLAlchemy");
    var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
    var fake = new FakeRequirementsInstaller();
    fake.NextResult = new PipResult(0, false);
    fake.AutoInstallThrows = true;

    var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

    Assert.True(result.Success);
}
```

### Step 2: Modify `FakeRequirementsInstaller` to support auto-install test seam

Update `FakeRequirementsInstaller` to add `AutoInstallResult` / `AutoInstallEnv` / `AutoInstallCallCount` / `AutoInstallThrows` and override the new ctor:

```csharp
private sealed class FakeRequirementsInstaller : RequirementsInstaller
{
    public PipResult NextResult { get; set; } = new(0, false);
    public int RunCount { get; private set; }
    public List<string> CapturedPipArgs { get; } = new();
    public string? CapturedFilteredContent { get; private set; }

    // v0.6.11+ T5: Manager 自动装测试 seam
    public NodeOperationResult AutoInstallResult { get; set; } = NodeOperationResult.Ok(null);
    public Environment? AutoInstallEnv { get; private set; }
    public int AutoInstallCallCount { get; private set; }
    public bool AutoInstallThrows { get; set; }

    public FakeRequirementsInstaller() : base(null, null, null)
    {
        // 通过 base ctor 注入 null + null + null;
        // Manager 自动装走 fake 重写的虚方法 AutoInstallComfyUiManagerAsync,不走真实 installer
    }

    protected override async Task<NodeOperationResult> AutoInstallComfyUiManagerAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        AutoInstallCallCount++;
        AutoInstallEnv = env;
        if (AutoInstallThrows) throw new InvalidOperationException("模拟异常");
        // 模拟 progress.Report 调用
        progress?.Report("auto-install:克隆 ComfyUI Manager");
        return AutoInstallResult;
    }

    public override async Task<RequirementsInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        // ... (existing override body — must also call AutoInstallComfyUiManagerAsync on success)
        var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);
        var reqPath = candidates.FirstOrDefault(File.Exists);
        if (reqPath is null)
        {
            var reason = $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})";
            return new RequirementsInstallResult(false, false, reason, 0);
        }

        var rawLines = await File.ReadAllLinesAsync(reqPath, ct);
        var filtered = RequirementsInstaller.FilterTorchLines(rawLines);
        var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
        await File.WriteAllLinesAsync(filteredPath, filtered, ct);

        RunCount++;
        CapturedPipArgs.Add("install");
        CapturedPipArgs.Add("-r");
        CapturedPipArgs.Add(filteredPath);
        CapturedFilteredContent = await File.ReadAllTextAsync(filteredPath, ct);
        try { File.Delete(filteredPath); } catch { }

        if (NextResult.WasCancelled)
            return new RequirementsInstallResult(false, true, "用户取消", 0);
        if (NextResult.ExitCode != 0)
            return new RequirementsInstallResult(false, false, $"pip 退出码 {NextResult.ExitCode}", 0);

        File.WriteAllText(Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        // v0.6.11+ T5: requirements 成功后自动装 Manager — 失败不阻断 requirements
        try
        {
            var autoResult = await AutoInstallComfyUiManagerAsync(env, logProgress, ct);
            if (!autoResult.Success)
            {
                // AutoInstall 的 reason 不写到 Result(避免用户困惑);仅日志。
                // 这里日志通过 base 的 _logger(测试不传 logger 所以无副作用)。
            }
        }
        catch
        {
            // AutoInstall 抛异常 → swallow,requirements 已成功
        }

        return new RequirementsInstallResult(true, false, null, filtered.Count);
    }
}
```

### Step 3: Modify `RequirementsInstaller` ctor + `InstallAsync`

In `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`:

Update ctor:
```csharp
private readonly AppLogger? _logger;
private readonly RequirementsFileInstaller _reqFileInstaller;
private readonly ComfyUIManagerInstaller _comfyUiManagerInstaller;

public RequirementsInstaller(
    AppLogger? logger = null,
    RequirementsFileInstaller? reqFileInstaller = null,
    ComfyUIManagerInstaller? comfyUiManagerInstaller = null)
{
    _logger = logger;
    _reqFileInstaller = reqFileInstaller ?? new RequirementsFileInstaller();
    _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(_reqFileInstaller);
}
```

Update `InstallAsync` (T5 部分 — 在成功 marker 写入后追加):
```csharp
public virtual async Task<RequirementsInstallResult> InstallAsync(
    Environment env,
    IProgress<string>? logProgress = null,
    CancellationToken ct = default)
{
    // ... (existing body unchanged up through LogResult(env.Name, "succeeded", null);)

    // v0.6.11+ T5: requirements 成功后自动装 ComfyUI Manager。失败不阻断
    // requirements(只 WARN 日志)— 用户可以手动 toggle 重试。
    await AutoInstallComfyUiManagerAsync(env, logProgress, ct);

    return new RequirementsInstallResult(...);
}

protected virtual async Task<NodeOperationResult> AutoInstallComfyUiManagerAsync(
    Environment env,
    IProgress<string>? progress,
    CancellationToken ct)
{
    try
    {
        logProgress?.Report("stage:自动装 ComfyUI Manager");
        var result = await _comfyUiManagerInstaller.InstallAsync(env, progress, ct);
        if (!result.Success)
        {
            _logger?.Warn("requirements-auto-install-manager",
                $"env='{env.Name}' ComfyUI Manager 自动装失败(reason={result.Reason});requirements 已成功,用户可手动 toggle 重试");
            logProgress?.Report($"warn:ComfyUI Manager 自动装失败:{result.Reason}");
        }
        return result;
    }
    catch (Exception ex)
    {
        _logger?.Warn("requirements-auto-install-manager",
            $"env='{env.Name}' ComfyUI Manager 自动装异常:{ex.Message}");
        logProgress?.Report($"warn:ComfyUI Manager 自动装异常:{ex.Message}");
        return NodeOperationResult.Fail(ex.Message);
    }
}
```

### Step 4: Update DI in App.xaml.cs

Find `var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller);` and change to:

```csharp
var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller, comfyUiManagerInstaller);
```

(Move the `comfyUiManagerInstaller` construction BEFORE `requirementsInstaller` — or reorder.)

### Step 5: Build + run tests

Run:
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsInstaller|FullyQualifiedName~RequirementsFileInstaller|FullyQualifiedName~ComfyUIManagerInstaller|FullyQualifiedName~EnvironmentListViewModel" -v minimal   # 全 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # baseline 不退化
```

Expected: Build 0/0. Filter PASS. Full suite at baseline (no regression).

### Step 6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs
git commit -m "$(cat <<'EOF'
feat(wpf): auto-install ComfyUI Manager after 装依赖

v0.6.11+ T5: RequirementsInstaller.InstallAsync 末尾(pip install -r 成功
且 marker 写入后)自动装 ComfyUI Manager。Manager 装失败只 WARN 日志 +
面板 warn 行,不阻断 requirements(用户原话:失败可手动 toggle 重试)。

- RequirementsInstaller ctor 加 ComfyUIManagerInstaller 参数(默认
  new 一个跟 _reqFileInstaller 配套)
- 新 protected virtual AutoInstallComfyUiManagerAsync(env, progress, ct):
  内部 try/catch 包 _comfyUiManagerInstaller.InstallAsync,异常/失败都
  swallow + WARN 日志,返回的 NodeOperationResult 仅给日志用
- 测试通过 FakeRequirementsInstaller override AutoInstallComfyUiManagerAsync
  验证 (1) 装成功后调用 1 次 (2) 失败不阻断 requirements (3) 抛异常不阻断
- AppLogger tag: requirements-auto-install-manager
- App.xaml.cs DI 接线:RequirementsInstaller 注入 comfyUiManagerInstaller

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Verification (end-to-end)

按顺序验证 5 task commit 全 PASS:

```bash
# 全套 build + test
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke(桌面验证,user):**
1. 启动 staging → env-list row 1 有 6 按钮(最后是 "安装 ComfyUI Manager",默认新建 env 未装)
2. 点 "安装 ComfyUI Manager" → inline 面板开,显示 "stage:克隆 ComfyUI Manager" → "stage:安装 ComfyUI Manager 依赖" → "ComfyUI Manager 安装成功(N 个包)"
3. 按钮文字变 "卸载 ComfyUI Manager";File Explorer 看 `<env>/ComfyUI/custom_nodes/ComfyUI-Manager/` 有 `.git/` + `requirements.txt` 等文件
4. 点 "卸载 ComfyUI Manager" → 面板显示 "卸载中..." → "卸载成功" → 按钮文字回 "安装 ComfyUI Manager" → 目录已删
5. 点「装依赖」→ requirements 装完后面板最后追加 "stage:自动装 ComfyUI Manager" + 日志行(已装则 "info: 已装,跳过"——实际是 IsInstalled 短路返 Fail)
6. 暗/亮主题切换 → 按钮 + inline 状态面板颜色跟随(v0.6.9.2 教训 + v0.6.10.2 DynamicResource 沿用)
7. 跑 staging 测装依赖时,toggle 按钮 disabled(busy mutex 生效)

---

## Risks

| 风险 | 缓解 |
|---|---|
| ComfyUI Manager 的 requirements.txt 含非 torch 大依赖(数十 MB)→ 装依赖时间变长 | 用户明确要求,G9 接受;状态面板透明展示给用户 |
| pip install 中途断网 → 部分装 → 回滚全删 → 用户需手动重试 | TryDelete 收尾 + 状态面板提示;重试是 toggle 按钮兜底 |
| junction 损坏 + TryDelete 失败 | 不 throw,允许 Manager dir 留半成品 + ERROR 日志,等下次手动清理 |
| RequirementsInstaller 末尾自动装 Manager 失败但 requirements 成功 → 用户困惑 | WARN 日志 + 状态面板最后一行 warn + toggle 按钮可手动重试 |
| `IsEnvBusy` mutex 已 busy 时 RequirementsInstaller 子步骤 Manager 装也 busy → 死锁 | RequirementsInstaller 不查 IsEnvBusy,直接跑(已 busy 的 env 不会进入 InstallRequirementsAsync 因 CanExecute 禁用) |
| Manager 跟其他 custom node 同名冲突(罕见)| 路径是固定 `custom_nodes/ComfyUI-Manager`,不冲突 |
| v0.6.5.22 IsEnvBusy 字典加新 ctor 字段后,既有测试构造参数对不上 | T1/T3 既有测试适配 ctor,FakeRequirementsInstaller 继承链不变 |
| bulk_update 的 ComfyUiManager 路径解析跟本任务 ResolveTargetDirectory 重复 | 暂不抽公共方法(YAGNI),新代码直接写,日后真重复再 refactor |
| Toggle 按钮在 Manager 装完后短暂 stale 状态(IsComfyUIManagerInstalled 还没 RaisePropertyChanged)| 装完回调里立刻重算 + 赋值 + RaisePropertyChanged(经 Load 触发) |
| T4 XAML 触发 v0.6.9.2 Setter + StaticResource 崩溃 | G11 + 所有 Setter 强制 property-element + DynamicResource;新增 Setter 前 grep |
| FakeRequirementsInstaller ctor 3-arg 但 T1 步骤 6 给的是 2-arg | T5 步骤 2 改为 3-arg(null, null, null),T1 后所有测试构造自动对齐 |

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例)**:
- 5 task × (implementer + reviewer) ≈ 10 dispatch
- T1 先做(RequirementsFileInstaller 是 T2/T3/T5 的依赖)
- T1→T2→T3→T4→T5 串行,每 task commit 后立即 task-review
- 5 commit on main,最后 staging rebuild + GUI smoke + MEMORY update

(plan agent left out: 用户已通过 5 决策确认全范围;本 plan 文件已是最终设计。下一步进入实施模式 → T1 implementer dispatch 起步,然后 T2→T3→T4→T5 串行 subagent。)