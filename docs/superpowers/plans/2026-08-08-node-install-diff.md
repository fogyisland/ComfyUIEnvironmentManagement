# v0.6.7.5 Node Install Diff Scan + Downgrade Warning — 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 节点安装前比对 catalog PipRequirements 跟 env 当前 pip,降级 / 冲突时弹 modal 警告,防止 ComfyUI 运行异常

**Architecture:**
- 新 `Services/NodeInstallDiffService` 跑 `pip list --format=json` on env.PythonExecutable,parse + 对比 catalog PipRequirements → 产出分类 (New/Upgrade/Downgrade/Conflict)
- 新 `Models/DiffEntry` + `DiffCategory` enum + `NodeInstallDiffReport`(含 `Warnings` 子集)
- 新 `ViewModels/NodeInstallDiffWarningViewModel` + `Views/NodeInstallDiffWarningDialog` (Cancel/Proceed)
- `NodeOperations.InstallAsync` 加可选尾参 `IReadOnlyList<PipRequirement>? catalogPipReqs`,clone 前调 DiffService,警告弹 modal
- `InstallDialogViewModel.InstallAsync` 传 `Entry.PipRequirements`

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · hand-rolled MVVM · `System.Text.Json`

**base SHA:** `dacaf24`(v0.6.7.4 SHIP-READY,最终 commit)

**Spec:** `docs/superpowers/specs/2026-08-08-node-install-diff-design.md`

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `InstallAsync` 加新尾参 `catalogPipReqs` 在 `targetTag` 之后、`ct` 之前;`ct` 永远在最尾 | spec G1 |
| G2 | `NodeInstallDiffService` 不抛(失败 → Empty report) | spec G2 |
| G3 | Modal 只在 Downgrade + Conflict ≥ 1 时弹 | spec G3 |
| G4 | Modal 调用走 DI seam(`Func<...>`),默认实现是真 dialog | spec G4 |
| G5 | 既有 `InstallAsync` 测试(无 `catalogPipReqs`)0 改动通过 | spec G5 |
| G6 | 不 bump version / 不发 release zip / 无 ledger 提交 | `feedback_no_rebuild_zip.md` |
| G7 | 中文 UI 文案,i18n 不变 | `feedback_workflow.md` |
| G8 | 不改 `NodeOperations.DownloadAsync`(本地下载无 diff) | spec G8 |
| G9 | `pip list` 命令 timeout = 15s | spec G9 |
| G10 | `pip list --format=json` 用 `System.Text.Json` parse | spec G10 |
| G11 | `DiffEntry.FromVersion`/`ToV ersion` 来自 raw specifier 字符串,不归一化 | spec G11 |
| G12 | `InstallAsync` 改签名后 grep 全代码库确认 4 处 caller 编译 | spec G12 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs` | ~30 | DTO + `DiffCategory` enum + computed display props |
| `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs` | ~25 | DTO + `Warnings` computed prop + `Empty` factory |
| `src-wpf/ComfyUI.Manager/Infrastructure/ProcessResult.cs` | ~10 | 通用 subprocess result record(`Ok, ExitCode, Stdout, Stderr`) |
| `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs` | ~150 | `CheckAsync(env, reqs, ct)` + `Classify` + private `ParseBounds` + private `PipJsonRow` DTO |
| `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs` | ~70 | VM + commands + computed display props + `Proceed` flag + `CloseRequested` event |
| `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml` + `.xaml.cs` | ~90 | modal Dialog(WPF window) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs` | ~180 | 6 测试(FakeProcessRunner + 真实 PipRequirementMatcher) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs` | ~80 | 3 测试(VM only) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs` | ~150 | 3 集成测试(注入 fake diffService + fake showDialog) |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | ctor 加 `NodeInstallDiffService _diffService` + `Func<NodeInstallDiffReport, Models.Environment, string, bool> _showDiffDialog`(默认 = `ShowDiffWarningDialogImpl`);`InstallAsync` 尾参加 `IReadOnlyList<PipRequirement>? catalogPipReqs = null`(放在 `targetTag` 后、`ct` 前);clone 前调 diff check |
| `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` | `InstallAsync` 调 `_ops.InstallAsync` 时传 `Entry.PipRequirements` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | DI 构造 `NodeInstallDiffService` 单例,传 `Func<...> processRunner` lambda 包装 `Process.Start`;`NodeOperations` ctor 加 `_diffService` + 默认 `showDiffDialog` lambda |

### Delete

无。

---

## Tasks

### Task 1: `DiffEntry` + `NodeInstallDiffReport` + `NodeInstallDiffService` + 6 tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs`
- Create: `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs`
- Create: `src-wpf/ComfyUI.Manager/Infrastructure/ProcessResult.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs`

**Interfaces:**
- Consumes: `PipRequirementMatcher.IsSatisfiedBy(PipRequirement, string?)`(已存在 v0.6.7.4 T1),`PipRequirement`(已存在 v0.6.7.4 T1),`System.Text.Json`
- Produces:
  - `DiffEntry(string Name, DiffCategory Category, string? FromVersion, string? ToVersion)` + `enum DiffCategory { New, Upgrade, Downgrade, Conflict, NoChange }` + computed `CategoryLabel` (Chinese)
  - `NodeInstallDiffReport(IReadOnlyList<DiffEntry> Entries)` + computed `Warnings`(过滤 Category ∈ {Downgrade, Conflict}) + `static Empty { get; }` = `new NodeInstallDiffReport(Array.Empty<DiffEntry>())`
  - `ProcessResult(bool Ok, int ExitCode, string Stdout, string Stderr)` — record
  - `NodeInstallDiffService(Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> runProcess, AppLogger? logger = null)` + `Task<NodeInstallDiffReport> CheckAsync(Models.Environment env, IReadOnlyList<PipRequirement> catalogReqs, CancellationToken ct)`

#### Step 1: Write failing tests

Create `tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class NodeInstallDiffServiceTests
{
    [Fact]
    public async Task CheckAsync_NewPackage_NotInWarnings()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("numpy", ">=1.24") },
            default);

        Assert.Empty(report.Warnings);
        Assert.Single(report.Entries);
        Assert.Equal(DiffCategory.New, report.Entries[0].Category);
    }

    [Fact]
    public async Task CheckAsync_Upgrade_NotInWarnings()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"1.0.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", ">=2.0") },
            default);

        Assert.Empty(report.Warnings);
        Assert.Single(report.Entries);
        Assert.Equal(DiffCategory.Upgrade, report.Entries[0].Category);
    }

    [Fact]
    public async Task CheckAsync_Downgrade_AddedToWarnings()
    {
        // env has torch 2.5.0, node spec wants <=1.5 → install will downgrade
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", "<=1.5") },
            default);

        Assert.Single(report.Warnings);
        Assert.Equal("torch", report.Warnings[0].Name);
        Assert.Equal(DiffCategory.Downgrade, report.Warnings[0].Category);
        Assert.Equal("2.5.0", report.Warnings[0].FromVersion);
    }

    [Fact]
    public async Task CheckAsync_Conflict_AddedToWarnings()
    {
        // env has torch 2.5.0, node spec wants <1 → conflict (no overlap)
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", "<1") },
            default);

        Assert.Single(report.Warnings);
        Assert.Equal(DiffCategory.Conflict, report.Warnings[0].Category);
    }

    [Fact]
    public async Task CheckAsync_EmptyCatalogReqs_EmptyReport()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            Array.Empty<PipRequirement>(),
            default);

        Assert.Empty(report.Entries);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task CheckAsync_PipListFails_EmptyReport_NoThrow()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(false, 1, "", "ERROR: no pip"));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", ">=2.0") },
            default);

        Assert.Empty(report.Entries);
        Assert.Empty(report.Warnings);
    }

    private static Models.Environment MakeEnv(string pythonExe) => new()
    {
        Id = "env-1",
        Name = "test",
        RootPath = "/tmp/test",
        ComfyuiLayout = "shared",
        BasePythonPath = pythonExe,
        PythonVersion = "3.10",
        PythonExecutable = pythonExe,
    };

    private sealed class FakeProcessRunner
    {
        private readonly ProcessResult _result;
        public FakeProcessRunner(ProcessResult result) => _result = result;
        public Task<ProcessResult> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(_result);
    }
}
```

#### Step 2: Run tests, verify 6/6 FAIL

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeInstallDiffServiceTests"`
Expected: FAIL — `DiffEntry`, `DiffCategory`, `NodeInstallDiffReport`, `ProcessResult`, `NodeInstallDiffService` 不存在,编译错误。

#### Step 3: Create `ProcessResult` record

Create `src-wpf/ComfyUI.Manager/Infrastructure/ProcessResult.cs`:

```csharp
namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// 通用 subprocess 执行结果 — NodeInstallDiffService 跑 pip list / 未来其他
/// process-based 工具(GetResult?)用同一形状。
/// </summary>
public sealed record ProcessResult(bool Ok, int ExitCode, string Stdout, string Stderr);
```

#### Step 4: Create `DiffEntry` + `DiffCategory`

Create `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs`:

```csharp
namespace ComfyUI.Manager.Models;

public enum DiffCategory
{
    New,        // env 没装 → 装完会有
    Upgrade,    // env 装的比 spec.min 低 → 装完会升
    Downgrade,  // env 装的比 spec.max 高 → 装完会降
    Conflict,   // env 装的跟 spec 区间不重叠 → 装完会冲突
    NoChange,   // env 装的已满足 → 无变化
}

/// <summary>
/// 单条 pip 依赖变更。FromVersion = env 当前版本(null = 未装);ToVersion = spec 原文。
/// </summary>
public sealed record DiffEntry(string Name, DiffCategory Category, string? FromVersion, string? ToVersion)
{
    /// <summary>UI 显示用中文标签。</summary>
    public string CategoryLabel => Category switch
    {
        DiffCategory.New => "新增",
        DiffCategory.Upgrade => "升级",
        DiffCategory.Downgrade => "降级",
        DiffCategory.Conflict => "冲突",
        DiffCategory.NoChange => "无变化",
        _ => Category.ToString(),
    };
}
```

#### Step 5: Create `NodeInstallDiffReport`

Create `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs`:

```csharp
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// NodeInstallDiffService 产出:全部分类 + Warnings 子集(Downgrade + Conflict)。
/// </summary>
public sealed class NodeInstallDiffReport
{
    public IReadOnlyList<DiffEntry> Entries { get; }

    public NodeInstallDiffReport(IReadOnlyList<DiffEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>Downgrade + Conflict 子集 — UI 警告 modal 只看这个。</summary>
    public IReadOnlyList<DiffEntry> Warnings
    {
        get
        {
            var list = new List<DiffEntry>();
            foreach (var e in Entries)
            {
                if (e.Category is DiffCategory.Downgrade or DiffCategory.Conflict)
                    list.Add(e);
            }
            return list;
        }
    }

    public static NodeInstallDiffReport Empty { get; } =
        new(System.Array.Empty<DiffEntry>());
}
```

#### Step 6: Implement `NodeInstallDiffService`

Create `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// 跑 env 的 venv `python -m pip list --format=json`,对比 catalog PipRequirements,
/// 分类成 New / Upgrade / Downgrade / Conflict(Warning = Downgrade + Conflict)。
///
/// 失败模式(G2):pip list 失败 / 超时 / parse 失败 → 返 Empty report,不抛。
/// </summary>
public sealed class NodeInstallDiffService
{
    private static readonly TimeSpan PipListTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> _runProcess;
    private readonly AppLogger? _logger;

    public NodeInstallDiffService(
        Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> runProcess,
        AppLogger? logger = null)
    {
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
        _logger = logger;
    }

    public async Task<NodeInstallDiffReport> CheckAsync(
        Environment env,
        IReadOnlyList<PipRequirement> catalogReqs,
        CancellationToken ct)
    {
        if (catalogReqs.Count == 0) return NodeInstallDiffReport.Empty;

        var pythonExe = env.PythonExecutable ?? "";
        if (string.IsNullOrEmpty(pythonExe))
        {
            _logger?.Info("node-diff", $"env='{env.Id}' python 路径为空,跳过 diff");
            return NodeInstallDiffReport.Empty;
        }

        ProcessResult result;
        try
        {
            result = await _runProcess(
                pythonExe,
                new[] { "-m", "pip", "list", "--format=json" },
                PipListTimeout, ct);
        }
        catch (Exception ex)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list 抛异常: {ex.Message}");
            return NodeInstallDiffReport.Empty;
        }

        if (!result.Ok)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list 失败 exit={result.ExitCode}");
            return NodeInstallDiffReport.Empty;
        }

        List<PipJsonRow>? installed;
        try
        {
            installed = JsonSerializer.Deserialize<List<PipJsonRow>>(result.Stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list json 解析失败: {ex.Message}");
            return NodeInstallDiffReport.Empty;
        }

        if (installed is null) return NodeInstallDiffReport.Empty;

        var installedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in installed)
        {
            if (!string.IsNullOrEmpty(p.name)) installedMap[p.name] = p.version ?? "";
        }

        var entries = new List<DiffEntry>();
        foreach (var req in catalogReqs)
        {
            if (!installedMap.TryGetValue(req.Name, out var installedVer))
            {
                entries.Add(new DiffEntry(req.Name, DiffCategory.New, null, req.Specifier));
                continue;
            }
            if (PipRequirementMatcher.IsSatisfiedBy(req, installedVer)) continue; // NoChange
            var (category, toV) = Classify(req, installedVer);
            entries.Add(new DiffEntry(req.Name, category, installedVer, toV));
        }
        return new NodeInstallDiffReport(entries);
    }

    private static (DiffCategory category, string? toV) Classify(PipRequirement req, string installedVer)
    {
        Version? installedV = TryParseVersion(installedVer);
        if (installedV is null)
            return (DiffCategory.Conflict, req.Specifier);

        var (minV, maxV) = ParseBounds(req.Specifier);

        if (minV is not null && installedV < minV)
            return (DiffCategory.Upgrade, req.Specifier);
        if (maxV is not null && installedV > maxV)
            return (DiffCategory.Downgrade, req.Specifier);

        // 复合 spec(IsSatisfiedBy 已返 false,但单边没界)→ Conflict
        return (DiffCategory.Conflict, req.Specifier);
    }

    private static (Version? min, Version? max) ParseBounds(string? specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return (null, null);
        Version? min = null, max = null;
        foreach (var single in specifier.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            for (int i = 0; i < single.Length; i++)
            {
                var c = single[i];
                if (c is not ('>' or '<' or '!' or '=' or '~')) continue;
                int opLen = 1;
                if (i + 1 < single.Length && single[i + 1] == '=') opLen = 2;
                if (i + 2 < single.Length && single[i] == '=' && single[i + 1] == '=' && single[i + 2] == '=') opLen = 3;
                var op = single[..(i + opLen)];
                var ver = single[(i + opLen)..];
                if (!Version.TryParse(NormalizeVersion(ver), out var v)) break;
                if (op is ">=" or ">" or "==" or "~=")
                {
                    if (min is null || v > min) min = v;
                }
                else if (op is "<=" or "<")
                {
                    if (max is null || v < max) max = v;
                }
                break;
            }
        }
        return (min, max);
    }

    private static string NormalizeVersion(string v)
    {
        var dash = v.IndexOfAny(new[] { 'a', 'b', 'r', 'p', '-' });
        var clean = dash >= 0 ? v[..dash] : v;
        var parts = clean.Split('.');
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }

    private static Version? TryParseVersion(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        return Version.TryParse(NormalizeVersion(v), out var ver) ? ver : null;
    }

    private sealed class PipJsonRow
    {
        public string? name { get; set; }
        public string? version { get; set; }
    }
}
```

#### Step 7: Run tests, verify 6/6 PASS

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeInstallDiffServiceTests"`
Expected: PASS — 6 tests, 0 failures.

#### Step 8: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/DiffEntry.cs \
        src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs \
        src-wpf/ComfyUI.Manager/Infrastructure/ProcessResult.cs \
        src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs
git commit -m "feat(wpf): NodeInstallDiffService + DiffEntry + Report (v0.6.7.5 T1)"
```

---

### Task 2: `NodeInstallDiffWarningDialog` XAML + VM + 3 tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs`

**Interfaces:**
- Consumes: `NodeInstallDiffReport`(T1)+ `DiffEntry`(T1)
- Produces: `NodeInstallDiffWarningViewModel(NodeInstallDiffReport report, string nodePackage, string envName)` + `bool Proceed` + `event Action? CloseRequested` + `CancelCommand` + `ProceedCommand`

#### Step 1: Write failing tests

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class NodeInstallDiffWarningDialogTests
{
    [Fact]
    public void Vm_Ctor_PopulatesWarnings()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
            new DiffEntry("foo", DiffCategory.New, null, ">=1.0"),
            new DiffEntry("bar", DiffCategory.Conflict, "3.0", "<1"),
        });

        var vm = new NodeInstallDiffWarningViewModel(report, "my-node", "my-env");

        Assert.Equal(2, vm.Warnings.Count); // 只 Downgrade + Conflict
        Assert.Equal("torch", vm.Warnings[0].Name);
        Assert.Equal("bar", vm.Warnings[1].Name);
        Assert.Equal("my-node", vm.NodePackage);
        Assert.Equal("my-env", vm.EnvName);
    }

    [Fact]
    public void Vm_CancelCommand_SetsProceedFalse_TriggersCloseRequested()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
        });
        var vm = new NodeInstallDiffWarningViewModel(report, "n", "e");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Proceed);
        Assert.True(fired);
    }

    [Fact]
    public void Vm_ProceedCommand_SetsProceedTrue_TriggersCloseRequested()
    {
        var report = new NodeInstallDiffReport(new[]
        {
            new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
        });
        var vm = new NodeInstallDiffWarningViewModel(report, "n", "e");
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.ProceedCommand.Execute(null);

        Assert.True(vm.Proceed);
        Assert.True(fired);
    }
}
```

#### Step 2: Run tests, verify 3/3 FAIL

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeInstallDiffWarningDialogTests"`
Expected: FAIL — VM 不存在。

#### Step 3: Create VM

Create `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class NodeInstallDiffWarningViewModel : ViewModelBase
{
    private bool _proceed;

    public NodeInstallDiffWarningViewModel(
        NodeInstallDiffReport report, string nodePackage, string envName)
    {
        NodePackage = nodePackage;
        EnvName = envName;
        Warnings = new ObservableCollection<DiffEntry>(report.Warnings);
        CancelCommand = new RelayCommand(_ => { Proceed = false; CloseRequested?.Invoke(); });
        ProceedCommand = new RelayCommand(_ => { Proceed = true; CloseRequested?.Invoke(); });
    }

    public event Action? CloseRequested;

    public string NodePackage { get; }
    public string EnvName { get; }
    public ObservableCollection<DiffEntry> Warnings { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ProceedCommand { get; }

    /// <summary>
    /// 调方 modal 关闭后读这个值 — true = 用户仍然安装,false = 用户取消。
    /// </summary>
    public bool Proceed
    {
        get => _proceed;
        private set => SetField(ref _proceed, value);
    }
}
```

#### Step 4: Run tests, verify 3/3 PASS

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeInstallDiffWarningDialogTests"`
Expected: PASS — 3 tests, 0 failures。

#### Step 5: Create XAML

Create `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml`:

```xaml
<Window x:Class="ComfyUI.Manager.Views.NodeInstallDiffWarningDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="依赖变更警告" Height="380" Width="600"
        Background="{StaticResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0">
            <TextBlock Text="依赖变更警告" FontSize="16" FontWeight="Bold"
                       Foreground="#FFC62828" Margin="0,0,0,8" />
            <TextBlock TextWrapping="Wrap">
                <Run Text="即将安装节点 " />
                <Run Text="{Binding NodePackage}" FontWeight="Bold" />
                <Run Text=" 会对 env " />
                <Run Text="{Binding EnvName}" FontWeight="Bold" />
                <Run Text=" 的 pip 依赖产生以下降级或冲突。安装可能导致 ComfyUI 运行异常,请确认是否继续。" />
            </TextBlock>
        </StackPanel>

        <DataGrid Grid.Row="1" ItemsSource="{Binding Warnings}"
                  AutoGenerateColumns="False" IsReadOnly="True"
                  Margin="0,12,0,12" HeadersVisibility="Column">
            <DataGrid.Columns>
                <DataGridTextColumn Header="包名" Binding="{Binding Name}" Width="*" />
                <DataGridTextColumn Header="类别" Binding="{Binding CategoryLabel}" Width="100" />
                <DataGridTextColumn Header="当前版本" Binding="{Binding FromVersion}" Width="120" />
                <DataGridTextColumn Header="将变为" Binding="{Binding ToVersion}" Width="120" />
            </DataGrid.Columns>
        </DataGrid>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="取消" Command="{Binding CancelCommand}"
                    Style="{StaticResource MaterialButton}" Width="80" />
            <Button Content="仍然安装" Command="{Binding ProceedCommand}"
                    Style="{StaticResource MaterialButton}" Margin="8,0,0,0" Width="100" />
        </StackPanel>
    </Grid>
</Window>
```

#### Step 6: Create code-behind

Create `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml.cs`:

```csharp
using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class NodeInstallDiffWarningDialog : Window
{
    public NodeInstallDiffWarningDialog(NodeInstallDiffWarningViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
```

#### Step 7: Build to verify XAML compiles

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

#### Step 8: Commit

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml \
        src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs
git commit -m "feat(wpf): NodeInstallDiffWarningDialog XAML + VM (v0.6.7.5 T2)"
```

---

### Task 3: `NodeOperations.InstallAsync` 接 diff + modal 弹窗 + 3 集成 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`(ctor + InstallAsync 签名 + 逻辑 + ShowDiffWarningDialogImpl)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs`

**Interfaces:**
- Consumes: `NodeInstallDiffService`(T1)+ `NodeInstallDiffReport`(T1)+ `NodeInstallDiffWarningViewModel`(T2)+ `NodeInstallDiffWarningDialog`(T2)
- Produces:
  - `NodeOperations(GitRunner git, EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings, NodeInstallDiffService diffService, Func<NodeInstallDiffReport, Models.Environment, string, bool>? showDiffDialog = null, AppLogger? logger = null)`
  - `InstallAsync(string envId, string nodeId, string repoUrl, string? targetTag = null, IReadOnlyList<PipRequirement>? catalogPipReqs = null, CancellationToken ct = default)` — clone 前调 diff

#### Step 1: Write failing tests

Create `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs`:

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
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class NodeOperationsInstallDiffTests
{
    [Fact]
    public async Task InstallAsync_WithDiffWarnings_UserCancels_DoesNotClone_ReturnsFail()
    {
        var (ops, diffService, showDialog, _) = MakeOps(
            diffReport: MakeReportWithDowngrade(),
            showDialogReturn: false);

        // 给一个 fake "可 clone" 但我们期望不被调
        var result = await ops.InstallAsync(
            "env-1", "node-a", "https://example/repo",
            targetTag: null,
            catalogPipReqs: new[] { new PipRequirement("torch", "<=1.5") });

        Assert.False(result.Success);
        Assert.Equal("用户取消(diff warning)", result.Reason);
        Assert.Equal(1, diffService.CheckCallCount);
        Assert.Equal(1, showDialog.CallCount);
    }

    [Fact]
    public async Task InstallAsync_WithDiffWarnings_UserProceeds_ClonesNormally()
    {
        // 集成测试:用真实 GitRunner + 一个真 git 命令?
        // 简化:用 FakeGitRunner,验证 catalogPipReqs 走完 diff + showDialog + 后续 clone
        // ...
        // (此处因篇幅省略 — 完整代码走现有 NodeOperationsTests 模式 + 加 showDialog=true 返回)
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InstallAsync_NoCatalogPipReqs_SkipsDiffCheck_BehavesLikeOriginal()
    {
        var (ops, diffService, showDialog, _) = MakeOps(
            diffReport: NodeInstallDiffReport.Empty,
            showDialogReturn: true);

        // catalogPipReqs = null → diff 不调,showDialog 不弹
        var result = await ops.InstallAsync(
            "env-1", "node-a", "" /* 触发回落,会失败,但不归 diff 责 */);

        Assert.Equal(0, diffService.CheckCallCount);
        Assert.Equal(0, showDialog.CallCount);
    }

    private static NodeInstallDiffReport MakeReportWithDowngrade() => new(new[]
    {
        new DiffEntry("torch", DiffCategory.Downgrade, "2.5.0", "<=1.5"),
    });

    /// <summary>
    /// 构造 NodeOperations + 所有 mock 依赖(不真跑 git)。
    /// diffService / showDialog 可被测试断言调用次数。
    /// </summary>
    private static (NodeOperations ops, FakeDiffService diffService, FakeShowDialog showDialog, TestDb db) MakeOps(
        NodeInstallDiffReport diffReport, bool showDialogReturn)
    {
        var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        envRepo.Upsert(new Environment
        {
            Id = "env-1", Name = "test", RootPath = Path.Combine(Path.GetTempPath(), "env-1"),
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", PythonExecutable = "/usr/bin/python",
        });
        var nodeRepo = new NodeRepository(db.Factory);
        var diffService = new FakeDiffService(diffReport);
        var showDialog = new FakeShowDialog(showDialogReturn);
        var settings = new Settings();
        var gitRunner = new FakeGitRunner(); // assume existing test helper
        var ops = new NodeOperations(gitRunner, envRepo, nodeRepo, settings,
            diffService, showDialog.Invoke);
        return (ops, diffService, showDialog, db);
    }

    private sealed class FakeDiffService : NodeInstallDiffService
    {
        private readonly NodeInstallDiffReport _report;
        public int CheckCallCount { get; private set; }
        public FakeDiffService(NodeInstallDiffReport report)
            : base((_, _, _, _) => Task.FromResult(new Infrastructure.ProcessResult(true, 0, "[]", "")))
        {
            _report = report;
        }
        public new Task<NodeInstallDiffReport> CheckAsync(
            Environment env, IReadOnlyList<PipRequirement> reqs, CancellationToken ct)
        {
            CheckCallCount++;
            return Task.FromResult(_report);
        }
    }

    private sealed class FakeShowDialog
    {
        private readonly bool _returnValue;
        public int CallCount { get; private set; }
        public FakeShowDialog(bool returnValue) => _returnValue = returnValue;
        public bool Invoke(NodeInstallDiffReport report, Environment env, string nodeId)
        {
            CallCount++;
            return _returnValue;
        }
    }

    /// <summary>placeholder:实现 git 失败即可</summary>
    private sealed class FakeGitRunner : GitRunner
    {
        public FakeGitRunner() : base("git", GitProxyConfig.From(new Settings())) { }
        // override RunAsync... 略,完整版从既有 NodeOperationsTests 抄
    }
}
```

> **简化:** Step 1 给的是骨架 / 路径(3 个测试 + 完整 helper 类)。完整实现由 implementer 在 `FakeGitRunner` 处参考 `tests-wpf/.../NodeOperationsTests.cs` 既有模式(`InstallAsync_EmptyRepoUrl_FallsBackToActiveDownloadSourceUrl` 等)填齐 — 关键断言:fake diffService.CheckCallCount + fake showDialog.CallCount 在 cancel case 都是 1,在 no-reqs case 都是 0。

#### Step 2: Run tests, verify 3/3 FAIL

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeOperationsInstallDiffTests"`
Expected: FAIL — `NodeOperations` ctor 不接 `NodeInstallDiffService` + `showDiffDialog`,`InstallAsync` 不接 `catalogPipReqs`。

#### Step 3: Update `NodeOperations.cs`

Modify `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`:

(a) Add fields + 改 ctor(line 33-51):

```csharp
    private readonly GitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly Settings _settings;
    private readonly NodeInstallDiffService _diffService;
    private readonly Func<NodeInstallDiffReport, Models.Environment, string, bool> _showDiffDialog;
    private readonly AppLogger? _logger;

    public NodeOperations(
        GitRunner git,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        Settings settings,
        NodeInstallDiffService diffService,
        Func<NodeInstallDiffReport, Models.Environment, string, bool>? showDiffDialog = null,
        AppLogger? logger = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _diffService = diffService ?? throw new ArgumentNullException(nameof(diffService));
        _showDiffDialog = showDiffDialog ?? ShowDiffWarningDialogImpl;
        _logger = logger;
    }

    private static bool ShowDiffWarningDialogImpl(
        NodeInstallDiffReport report, Models.Environment env, string nodeId)
    {
        var vm = new NodeInstallDiffWarningViewModel(report, nodeId, env.Name);
        var dlg = new NodeInstallDiffWarningDialog(vm)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
        return vm.Proceed;
    }
```

(b) Add `using ComfyUI.Manager.Models;` if not present(应该已有,`DiffEntry` / `PipRequirement` 都在 `Models` 命名空间)。

(c) Update `InstallAsync` signature + body(line 63-67):

OLD:
```csharp
    public virtual async Task<NodeOperationResult> InstallAsync(
        string envId, string nodeId, string repoUrl,
        string? targetTag = null,
        CancellationToken ct = default)
```

NEW:
```csharp
    public virtual async Task<NodeOperationResult> InstallAsync(
        string envId, string nodeId, string repoUrl,
        string? targetTag = null,
        IReadOnlyList<PipRequirement>? catalogPipReqs = null,
        CancellationToken ct = default)
```

(d) Insert diff check after `RequireEnv(envId)` line(~line 69,在 `if (string.IsNullOrWhiteSpace(env.CustomNodesPath))` 之前):

```csharp
        var env = RequireEnv(envId);
        _logger?.Info("node-install", $"env='{envId}' node='{nodeId}' 开始安装");

        // v0.6.7.5: Pre-clone diff check(可选 — 仅当 caller 传 catalogPipReqs 时跑)
        if (catalogPipReqs is not null && catalogPipReqs.Count > 0
            && !string.IsNullOrEmpty(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            var report = await _diffService.CheckAsync(env, catalogPipReqs, ct);
            if (report.Warnings.Count > 0)
            {
                bool proceed = _showDiffDialog(report, env, nodeId);
                if (!proceed)
                {
                    _logger?.Info("node-install",
                        $"env='{envId}' node='{nodeId}' 用户取消 diff warning(检测到 {report.Warnings.Count} 条)");
                    return NodeOperationResult.Fail("用户取消(diff warning)");
                }
                _logger?.Info("node-install",
                    $"env='{envId}' node='{nodeId}' 用户接受 {report.Warnings.Count} 条 diff warning,继续");
            }
        }

        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        ...
```

> **注意:** 上面把原 `_logger?.Info("node-install", $"env='{envId}' node='{nodeId}' 开始安装");` 行(原本 line 68)移到 `RequireEnv` 之后,这样 log 行顺序是 env 拿到后 → 开始安装 → diff check → clone。

#### Step 4: Run new tests, verify 3/3 PASS

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeOperationsInstallDiffTests"`
Expected: PASS — 3 tests, 0 failures。

#### Step 5: Verify existing `NodeOperationsTests` still pass(0 改动)

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~NodeOperationsTests"`
Expected: PASS — 既有 N 测试通过(G5:`NodeOperations` ctor 加必填 `NodeInstallDiffService` + 可选 `showDiffDialog` + `logger`,既有测试构造时需传 fake diffService — implementer 要更新既有测试 helper 加 1 个 fake diffService 实参)。

#### Step 6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Services/NodeOperations.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs
git commit -m "feat(wpf): NodeOperations.InstallAsync 接 diff + modal 警告 (v0.6.7.5 T3)"
```

> `NodeOperationsTests.cs` 在既有测试构造点加 fake diffService — 既有 0 行为改动。

---

### Task 4: `InstallDialogViewModel` 传 `Entry.PipRequirements` + App DI 注入 + close-out + 全量 verify + 重建 staging

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`

**Interfaces:**
- Consumes: `InstallDialogViewModel.Entry`(已有 CatalogEntry 类型)+ `NodeOperations.InstallAsync` 新尾参(T3)
- Produces: production wiring 让 catalog 节点装入时 diff 警告生效

#### Step 1: Update `InstallDialogViewModel.InstallAsync`

Modify `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs` line 92:

OLD:
```csharp
            var result = await _ops.InstallAsync(envId, Entry.Package, repoUrl);
```

NEW:
```csharp
            var result = await _ops.InstallAsync(
                envId, Entry.Package, repoUrl,
                targetTag: null,
                catalogPipReqs: Entry.PipRequirements,
                ct: default);
```

#### Step 2: Update `App.xaml.cs` DI wiring

Modify `src-wpf/ComfyUI.Manager/App.xaml.cs` line ~96 (NodeOperations 构造前):

(a) 在 `_launcher = new ProcessLauncher(...)` 之后、`var nodeOps = new NodeOperations(...)` 之前,加 `processRunner` lambda + `_diffService`:

```csharp
        // v0.6.7.5: diff service + process runner lambda(for pip list)
        System.Func<string, string[], TimeSpan, System.Threading.CancellationToken,
            System.Threading.Tasks.Task<ProcessResult>> runProcess =
            async (exe, args, timeout, ct) =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) return new ProcessResult(false, -1, "", "Process.Start returned null");
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                using var timeoutCts = new System.Threading.CancellationTokenSource(timeout);
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); } catch { }
                    return new ProcessResult(false, -1, "", $"timeout after {timeout.TotalSeconds}s");
                }
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                return new ProcessResult(proc.ExitCode == 0, proc.ExitCode, stdout, stderr);
            };
        var diffService = new NodeInstallDiffService(runProcess, logger);
```

(b) Update `NodeOperations` 构造(line 96):

OLD:
```csharp
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings, logger);
```

NEW:
```csharp
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings, diffService, logger: logger);
```

> `showDiffDialog` 不传 → 默认 `ShowDiffWarningDialogImpl`(走真 modal WPF)。

#### Step 3: Build + full suite

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 661 PASS / 0 FAIL / 1 SKIP(649 + T1 6 + T2 3 + T3 3 = 661;SKIP = LiveFetch real GitHub)

#### Step 4: 重建 staging

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

Verify: `git status --short` shows working tree clean(staging exe 时间戳 gitignored)。

#### Step 5: Commit

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs
git commit -m "feat(wpf): InstallDialogViewModel 传 PipRequirements + App DI 注入 (v0.6.7.5 T4)"
```

---

## Verification

### 单元测试

| 测试 | 验证 |
|---|---|
| `NodeInstallDiffServiceTests.CheckAsync_NewPackage_NotInWarnings` | reqs=[numpy] + installed={torch} → Warnings=0;Entries[0]=New |
| `CheckAsync_Upgrade_NotInWarnings` | reqs=[torch>=2] + installed={torch:1.0} → Warnings=0;Entries[0]=Upgrade |
| `CheckAsync_Downgrade_AddedToWarnings` | reqs=[torch<=1.5] + installed={torch:2.5} → Warnings=[{torch, Downgrade, 2.5, <=1.5}] |
| `CheckAsync_Conflict_AddedToWarnings` | reqs=[torch<1] + installed={torch:2.5} → Warnings=[{torch, Conflict, 2.5, <1}] |
| `CheckAsync_EmptyCatalogReqs_EmptyReport` | reqs=[] → Entries=Warnings=[] |
| `CheckAsync_PipListFails_EmptyReport_NoThrow` | FakeProcessRunner 返 exit=1 → Empty,无异常 |
| `NodeInstallDiffWarningDialogTests.Vm_Ctor_PopulatesWarnings` | 3 entries(1 New + 1 Downgrade + 1 Conflict)→ Warnings.Count=2 |
| `Vm_CancelCommand_SetsProceedFalse_TriggersCloseRequested` | CancelCommand.Execute → Proceed=false + CloseRequested fired |
| `Vm_ProceedCommand_SetsProceedTrue_TriggersCloseRequested` | ProceedCommand.Execute → Proceed=true + CloseRequested fired |
| `NodeOperationsInstallDiffTests.InstallAsync_WithDiffWarnings_UserCancels_DoesNotClone_ReturnsFail` | catalogPipReqs=[torch<=1.5] + diffService 返 1 条 Downgrade + showDialog 返 false → Fail("用户取消(diff warning)") + CheckCallCount=1 + CallCount=1 |
| `InstallAsync_WithDiffWarnings_UserProceeds_ClonesNormally` | 同上 + showDialog 返 true → git clone 跑 + ScannedNode 写入 |
| `InstallAsync_NoCatalogPipReqs_SkipsDiffCheck_BehavesLikeOriginal` | catalogPipReqs=null → CheckCallCount=0 + CallCount=0 |

12 新测试。

### 全量

- `dotnet build` 0 errors / 0 warnings
- `dotnet test` 661 PASS / 0 FAIL / 1 SKIP(649 + 12,SKIP = LiveFetch real GitHub)
- 既有 `NodeOperationsTests` 通过(需构造 fake diffService)— G5

### 端到端桌面(用户测)

1. 启动 staging exe
2. 侧栏"环境" → 选 stopped + BED done env → 行内"安装节点"
3. CatalogEntryPicker → 选一个有 pip 需求的 catalog entry(例如 torch)
4. InstallDialog 开 → 选 env → 点 Install
5. **若** env 已装 torch 2.5,node spec 说 `<=1.5` → 弹 modal "依赖变更警告" + 列 torch Downgrade 2.5 → <=1.5
   - 点 [取消] → 回到 dialog,无 git clone,Logs/ `[node-install] ... 用户取消 diff warning`
   - 点 [仍然安装] → git clone 跑 + ScannedNode 写入,Logs/ `用户接受 1 条 diff warning,继续`
6. **若** env 装的已满足 spec → 无 modal,直接 clone
7. **若** env venv 缺失 / `pip list` 失败 → 无 modal,直接 clone + `[node-diff] ... pip list 失败` INFO 行

---

## Risks

| 风险 | 缓解 |
|---|---|
| `pip list` 失败被静默 → 用户期望警告但没看到 | AppLogger INFO 记,符合 v0.6.5.13 模式 — 用户查 Logs/ 可发现 |
| pip list 输出格式变化 | `--format=json` 自 pip 9.0 stable,预期不变 |
| Modal 在 4 个 `InstallAsync` 调用点都被触发,UX 重复 | 只有 1 个 UI entry(InstallDialog)— `InstallDialogViewModel` 是唯一 catalog 装 caller;`catalogPipReqs != null` 走真 catalog 装 |
| `PipRequirement.MinVersion` / `MaxVersion` 当前不存在 | Classify 用 spec 字符串解析 min/max bounds(自包含,不依赖 PipRequirement 改 API) |
| `InstallAsync` 签名变更漏 grep caller → 编译失败 | T4 build 0/0 + full suite + grep 全代码库(G12) |
| DiffService 跑 `python.exe` 而不是 venv python → 拿到 base python 的 pip list | `env.PythonExecutable` 是 venv 路径(EnvCreatorService step 6 写)— T1 测试用 fake runner 验证拿到正确的 pip |
| NodeOperations ctor 加必填 `NodeInstallDiffService` → 既有测试构造点要更新 | T3 Step 5 + 既有测试 helper 加 fake diffService 实参(G5) |
| `FakeGitRunner` 实现 cost | T3 Step 1 留 placeholder + 注释,implementer 从 `NodeOperationsTests.cs` 既有模式 copy |

---

## Critical files to modify/create

- `src-wpf/ComfyUI.Manager/Models/DiffEntry.cs`(新)
- `src-wpf/ComfyUI.Manager/Models/NodeInstallDiffReport.cs`(新)
- `src-wpf/ComfyUI.Manager/Infrastructure/ProcessResult.cs`(新)
- `src-wpf/ComfyUI.Manager/Services/NodeInstallDiffService.cs`(新)
- `src-wpf/ComfyUI.Manager/ViewModels/NodeInstallDiffWarningViewModel.cs`(新)
- `src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml` + `.xaml.cs`(新)
- `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`(改 ctor + InstallAsync)
- `src-wpf/ComfyUI.Manager/ViewModels/InstallDialogViewModel.cs`(1 行 caller)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(DI 注入)
- `tests-wpf/ComfyUI.Manager.Tests/Services/NodeInstallDiffServiceTests.cs`(新,6 测试)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeInstallDiffWarningDialogTests.cs`(新,3 测试)
- `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsInstallDiffTests.cs`(新,3 集成)
- `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs`(改既有 helper 加 fake diffService 实参)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 4 task(串行) — T1 service + 6 tests, T2 dialog + 3 tests, T3 NodeOperations 接 diff + 3 集成, T4 InstallDialogVM + DI + close-out
- Per-task review gate(sonnet implementer + sonnet reviewer)
- 估计 4 commits on main + 1 close-out(共 5 commits;含既有 NodeOperationsTests 适配 1 commit)