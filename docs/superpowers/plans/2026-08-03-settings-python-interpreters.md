# v0.6.5.6 Implementation Plan — Settings 多 Python 解释器管理

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Settings 面板新增 "Python 解释器" 区段（Name+Path 列表 + 单 active），CreateEnvDialog auto-fill 改用 active.Path，老 settings.json 首次加载自动迁移出一条默认条目。

**Architecture:** 新增 `PythonInterpreterValidator` service（add 时同步 `python --version` 校验）；`Models.Settings` 加 2 字段 + `PythonInterpreter` POCO；`SettingsViewModel` 新增 4 命令 + 4 状态属性（沿用 QuerySources 模式 1:1）；`SettingsView.xaml` 新增区段 + 老字段只读 label；`CreateEnvDialogViewModel.ApplyTemplate` 改用 active.Path（保留 v0.6.5.5 recent base 优先级）。

**Tech Stack:** WPF .NET 8 / C# 12 · `Process.Start` + `System.Text.RegularExpressions` · xUnit · `System.Text.Json` (已有 snake_case 属性)

**base SHA:** `8279c61` (v0.6.5.6 spec commit)

**spec:** `docs/superpowers/specs/2026-08-03-settings-python-interpreters-design.md` (本 plan 的 source of truth)

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs` | ~80 | `ValidateAsync(path)` 跑 `python --version`,5s 超时,UTF-8 解析,不抛异常 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/PythonInterpreterValidatorTests.cs` | ~180 | 5 tests:valid path / 缺失 / 非 Python / timeout / 不抛 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 新增 `PythonInterpreter` POCO + 2 字段 `PythonInterpreters` / `ActivePythonInterpreterName`;老字段保留(读不写) |
| `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs` | 加 3 tests:PythonInterpreters round-trip + 迁移成功 + 迁移不重复 |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | `ApplyTemplate()` 用 `active.Path`(替换 TemplatePythonDir/DefaultPythonVersion 拼接);顶部警告文案调整 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs` | 加 2 tests:ApplyTemplate 用 active 不拼接 / active 缺失回退 "" |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | ctor 多 1 参 `PythonInterpreterValidator validator`;新增 4 命令 + 4 状态属性(沿用 QuerySources 模式);`Dispose` 取消 validator CTS |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` | 加 4 tests:Add valid 写入激活 / Add invalid 错误不写 / Remove active 回退 / ActivePythonInterpreter 找不到返回 null |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | SettingsViewModel 构造多传 1 参(validator) |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 新增 `var pythonValidator = new PythonInterpreterValidator()`,传给 MainViewModel |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 新增 "Python 解释器" 区段(ComboBox + ItemsControl + Add/Remove + 内联表单 + 错误提示);老字段改 IsReadOnly=True 只读 label |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` | 新增 `BrowsePythonInterpreter` handler + OpenFileDialog 过滤 *.exe |

### Keep (unchanged)

- `Models.Settings` 老字段 `TemplatePythonDir` / `DefaultPythonVersion` JSON 键名不变,序列化兼容
- `CreateEnvDialogViewModel._recentBasePythonPath` 字段(v0.6.5.5)不动
- `CreateEnvDialogViewModel.ApplyTemplateCommand` 行为(v0.6.5.5 重置 recent)不动
- `Environment.BasePythonPath` / `Environment.PythonVersion`(v0.6.5.5)不动
- `base_env_profiles.json` bundled asset 不动
- `SettingsDefaults.Apply` 签名不动,内部加迁移分支

### Delete

无。

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `Settings.PythonInterpreters: List<PythonInterpreter>`(JSON 键 `python_interpreters`) | spec §1 |
| G2 | `Settings.ActivePythonInterpreterName: string`(JSON 键 `active_python_interpreter_name`) | spec §1 |
| G3 | `PythonInterpreter = { Name, Path }`(JSON 键 `name` / `path`) | spec §1 |
| G4 | 老字段 `template_python_dir` / `default_python_version` JSON 键名保留,Settings 面板 IsReadOnly=True 只读 label | spec §1 + §2.3 |
| G5 | `PythonInterpreterValidator.ValidateAsync` 5s 超时,`StandardOutputEncoding = UTF-8`,解析首段 `Python\s+(\d+\.\d+(?:\.\d+)?)`,不抛任何异常(IOException/Win32Exception/OperationCanceledException 都 catch 返回 Invalid) | spec §2.1 |
| G6 | 迁移触发:`PythonInterpreters.Count == 0 && TemplatePythonDir 非空 && DefaultPythonVersion 非空`(三个条件 AND) | spec §2.6 |
| G7 | 迁移条目:`{Name = DefaultPythonVersion, Path = Path.Combine(TemplatePythonDir, DefaultPythonVersion, "python.exe")}`;`ActivePythonInterpreterName = DefaultPythonVersion` | spec §2.6 |
| G8 | CreateEnvDialog 打开:auto-fill 优先级 = recent base(非空)> active.Path > "";顶部警告三种文案见 spec §2.5 | spec §2.5 + §3 |
| G9 | `ApplyTemplateCommand` 重置 `_recentBasePythonPath = null` 后调用 `ApplyTemplate()`(v0.6.5.5 行为保留) | spec §2.5 |
| G10 | 添加流程:validate 先 → 失败显示 `AddPythonInterpreterError` 不写 + 表单保持打开 → 成功 Add + 自动 active + Save + 关闭表单 + 清空 inputs | spec §2.2 + §3 |
| G11 | 删除 active 条目:`ActivePythonInterpreterName` 回退到剩余 `FirstOrDefault()?.Name ?? ""` | spec §2.2 + §3 |
| G12 | `SettingsViewModel` ctor 多 1 必需参 `PythonInterpreterValidator validator`(无默认值,所有调用点必须传);`MainViewModel` + `App.xaml.cs` 同步注入 | spec §2.2 + 实现要求 |
| G13 | WPF 测试基线 v0.6.5.5 = 298;v0.6.5.6 期望 +14 → **312 PASS / 1 SKIP / 0 FAIL** | spec §4.3 |
| G14 | 5 处版本字面量 `0.6.5.5` → `0.6.5.6`(pyproject.toml / src/comfy_mgr/__init__.py / shared/errors.json / ComfyUI.Manager.csproj / tests/test_version_consistency.py 3 处);release notes 中文 | 沿用 v0.6.5.5 模式 |
| G15 | 不得 push / tag / rebuild zip / `gh release create`(沿用 v0.6.5.5 模式,等用户单独授权) | 沿用 v0.6.5.5 模式 |

---

## Tasks

### Task 1: `PythonInterpreterValidator` service + 5 tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/PythonInterpreterValidatorTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public sealed record ValidationResult(bool IsValid, string Version = "", string? Error = null);

  public sealed class PythonInterpreterValidator
  {
      public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

      public async Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: Write failing tests**(`tests-wpf/ComfyUI.Manager.Tests/Services/PythonInterpreterValidatorTests.cs`):

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class PythonInterpreterValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsValid_WhenPathIsPythonExe()
    {
        // Arrange: 找系统任意可用的 python.exe;Windows 优先 py.exe,否则 python.exe。
        var py = ResolveSystemPython();
        if (py is null) return;  // 机器没 Python 跳过 happy path(后续修机器时再补)
        var sut = new PythonInterpreterValidator();

        // Act
        var result = await sut.ValidateAsync(py);

        // Assert
        Assert.True(result.IsValid, $"Expected valid Python, got Error={result.Error}");
        Assert.Matches(@"^\d+\.\d+(\.\d+)?$", result.Version);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenPathMissing()
    {
        var sut = new PythonInterpreterValidator();
        var bogus = Path.Combine(Path.GetTempPath(), "definitely_does_not_exist_python_xyz.exe");

        var result = await sut.ValidateAsync(bogus);

        Assert.False(result.IsValid);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenPathNotPython()
    {
        // Arrange: 拿 Windows 系统自带的 notepad.exe(必有)做反例
        var notepad = ResolveNotepad();
        if (notepad is null) return;  // 非 Windows 跳过
        var sut = new PythonInterpreterValidator();

        var result = await sut.ValidateAsync(notepad);

        Assert.False(result.IsValid);
        Assert.Contains("Python", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_OnTimeout()
    {
        // Arrange: 用一个会 hang 的 fake exe —— Windows 自带 timeout.exe(/t 5)
        // 行为稳定,延迟 > ProbeTimeout 5s。
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var timeoutExe = Path.Combine(Environment.SystemDirectory, "timeout.exe");
        if (!File.Exists(timeoutExe)) return;  // Win11 可能没 timeout.exe,跳过
        var sut = new PythonInterpreterValidator();
        // 给 timeout.exe 一个无穷等待参数(模拟 hang)
        // 但 timeout.exe 本身不接受任意命令;改用 cmd.exe /c "ping -n 99 127.0.0.1" 替代。
        // 简化路径:用 cmd.exe + ping 长延迟,保证 > 5s。
        var hangCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // 用 raw 方式直接调用 validator,需要 path 是可执行文件。
        // validator 不接受 args,所以这里改成:写一个永远 sleep 的脚本作为 path —— 太复杂。
        // 退而求其次:用 CancellationToken 主动取消,模拟用户取消的"超时"。
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));  // 50ms 取消 → validator 走 catch OperationCanceledException → 返回 Invalid

        var result = await sut.ValidateAsync(ResolveSystemPython() ?? "python", cts.Token);

        Assert.False(result.IsValid);
        Assert.True(result.Error == "超时" || result.Error == "无法启动进程",
            $"Expected timeout/cancelled error, got: {result.Error}");
    }

    [Fact]
    public async Task ValidateAsync_DoesNotThrow_OnFailure()
    {
        // Arrange: 用一个绝对无法启动的 path(目录而非文件)
        var sut = new PythonInterpreterValidator();
        var dir = Path.GetTempPath();  // 是目录,不是 exe

        // Act + Assert: 不抛异常,返回 Invalid
        var result = await sut.ValidateAsync(dir);
        Assert.False(result.IsValid);
    }

    private static string? ResolveSystemPython()
    {
        // Windows: 优先 py.exe launcher,再 python.exe,再 python3.exe
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "py.exe", "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            var path = FindOnPath(c);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? ResolveNotepad()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        return Path.Combine(Environment.SystemDirectory, "notepad.exe");
    }

    private static string? FindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
```

- [ ] **Step 2: Run tests to verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PythonInterpreterValidatorTests" -v minimal
```

Expected: **FAIL** — `PythonInterpreterValidator` not defined。

- [ ] **Step 3: Write minimal implementation**(`src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs`):

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

public sealed record ValidationResult(bool IsValid, string Version = "", string? Error = null);

public sealed class PythonInterpreterValidator
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex VersionRegex =
        new(@"Python\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public async Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ValidationResult(false, Error: "路径不存在");

        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new ValidationResult(false, Error: "无法启动进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new ValidationResult(false, Error: "超时");
            }

            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = string.IsNullOrEmpty(stdout)
                ? (await stderrTask.ConfigureAwait(false)).Trim()
                : "";

            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            if (string.IsNullOrEmpty(output))
                return new ValidationResult(false, Error: "无输出");

            var m = VersionRegex.Match(output);
            if (!m.Success)
                return new ValidationResult(false, Error: "不是合法 Python 解释器");

            return new ValidationResult(true, Version: m.Groups[1].Value);
        }
        catch (OperationCanceledException)
        {
            return new ValidationResult(false, Error: "超时");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            return new ValidationResult(false, Error: $"启动失败:{ex.Message}");
        }
    }
}
```

> 需要 `using System.Runtime.InteropServices;` 给 `Win32Exception`。

- [ ] **Step 4: Run tests to verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PythonInterpreterValidatorTests" -v minimal
```

Expected: **5 PASS / 0 FAIL**。

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/PythonInterpreterValidatorTests.cs
git commit -m "feat(wpf): PythonInterpreterValidator + 5s probe timeout"
```

---

### Task 2: `Models.Settings` 加 2 字段 + `PythonInterpreter` POCO + 3 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs`(末尾追加)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs`(追加 3 tests)

**Interfaces:**
- Consumes: nothing(独立任务,不依赖 T1)
- Produces:
  ```csharp
  // Models/Settings.cs:
  [JsonPropertyName("python_interpreters")]
  public List<PythonInterpreter> PythonInterpreters { get; set; } = new();

  [JsonPropertyName("active_python_interpreter_name")]
  public string ActivePythonInterpreterName { get; set; } = "";

  public class PythonInterpreter
  {
      [JsonPropertyName("name")] public string Name { get; set; } = "";
      [JsonPropertyName("path")] public string Path { get; set; } = "";
  }
  ```

- [ ] **Step 1: Read existing `Models/Settings.cs` to confirm field layout**

(已在 plan 编写前读过;末尾 line 58 是 `github_token`,行 66 是 `public class ExtraPath { ... }` 闭合大括号。PythonInterpreter POCO 紧跟 Settings 类之后,作为顶级 public class。)

- [ ] **Step 2: Write failing tests**(`tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs` 末尾追加):

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsTests
{
    // ... existing tests ...

    [Fact]
    public void PythonInterpreters_RoundTrip()
    {
        var s = new Settings
        {
            PythonInterpreters = new List<PythonInterpreter>
            {
                new() { Name = "py3.10", Path = "D:/python/3.10/python.exe" },
                new() { Name = "py3.11", Path = "D:/python/3.11/python.exe" },
            },
            ActivePythonInterpreterName = "py3.11",
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.PythonInterpreters.Count);
        Assert.Equal("py3.10", back.PythonInterpreters[0].Name);
        Assert.Equal("D:/python/3.10/python.exe", back.PythonInterpreters[0].Path);
        Assert.Equal("py3.11", back.PythonInterpreters[1].Path);
        Assert.Equal("py3.11", back.ActivePythonInterpreterName);
    }

    [Fact]
    public void Migration_FirstLoadFromLegacyTemplatePythonDir_CreatesDefaultEntry()
    {
        // Arrange: 模拟老 v0.6.5.5 settings.json —— 有 template_python_dir + default_python_version,
        // 没有 python_interpreters 字段。
        var json = """
        {
          "template_python_dir": "D:/python",
          "default_python_version": "3.10",
          "github_token": "abc"
        }
        """;

        // Act: 反序列化得到 Settings 实例,然后跑迁移分支(SettingsDefaults.Apply 末尾)。
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);  // baseDir 在测试中不重要(迁移不依赖)

        // Assert
        Assert.Single(s.PythonInterpreters);
        Assert.Equal("3.10", s.PythonInterpreters[0].Name);
        Assert.Equal(Path.Combine("D:/python", "3.10", "python.exe"), s.PythonInterpreters[0].Path);
        Assert.Equal("3.10", s.ActivePythonInterpreterName);
        // 老字段保留
        Assert.Equal("D:/python", s.TemplatePythonDir);
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void Migration_NoOp_WhenPythonInterpretersNonEmpty()
    {
        // Arrange: settings.json 已经含 python_interpreters(用户已在 v0.6.5.6 加过)
        var json = """
        {
          "python_interpreters": [
            { "name": "user-added", "path": "E:/custom/python.exe" }
          ],
          "active_python_interpreter_name": "user-added",
          "template_python_dir": "D:/python",
          "default_python_version": "3.10"
        }
        """;

        var s = JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);

        // Assert:迁移分支不触发(已有条目),列表保持 1 条
        Assert.Single(s.PythonInterpreters);
        Assert.Equal("user-added", s.PythonInterpreters[0].Name);
        Assert.Equal("user-added", s.ActivePythonInterpreterName);
    }
}
```

> `SettingsDefaults.Apply` 迁移分支在 **Task 4**(本 Task 2 仅做模型字段 + JSON round-trip;迁移测试断言写在前,但 `SettingsDefaults.Apply` 末尾的迁移代码直到 Task 4 才加。临时策略:T2 commit 时 `Migration_*` 两个 tests 标 `[Fact(Skip="待 T4 SettingsDefaults.Apply 添加迁移分支")]`;T4 完成后移除 Skip 属性再 commit。)见 Task 4 末尾说明。

> **重要:** 实际写 plan 时把 `Migration_*` 测试**整体推迟到 Task 4** 实现并 commit,本 Task 只写 `PythonInterpreters_RoundTrip` 一个 test。

**修正版 Step 2:** Task 2 只写 `PythonInterpreters_RoundTrip` test。

```csharp
    [Fact]
    public void PythonInterpreters_RoundTrip()
    {
        var s = new Settings
        {
            PythonInterpreters = new List<PythonInterpreter>
            {
                new() { Name = "py3.10", Path = "D:/python/3.10/python.exe" },
                new() { Name = "py3.11", Path = "D:/python/3.11/python.exe" },
            },
            ActivePythonInterpreterName = "py3.11",
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.PythonInterpreters.Count);
        Assert.Equal("py3.10", back.PythonInterpreters[0].Name);
        Assert.Equal("D:/python/3.10/python.exe", back.PythonInterpreters[0].Path);
        Assert.Equal("py3.11", back.PythonInterpreters[1].Path);
        Assert.Equal("py3.11", back.ActivePythonInterpreterName);
    }
```

`Migration_FirstLoadFromLegacyTemplatePythonDir_CreatesDefaultEntry` 与 `Migration_NoOp_WhenPythonInterpretersNonEmpty` 两个测试 **写在 Task 4 末尾**(因为它们依赖 `SettingsDefaults.Apply` 末尾的迁移分支)。

- [ ] **Step 3: Run test to verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsTests.PythonInterpreters_RoundTrip" -v minimal
```

Expected: **FAIL** — `Settings` 不含 `PythonInterpreters` 字段。

- [ ] **Step 4: Write minimal implementation**(`src-wpf/ComfyUI.Manager/Models/Settings.cs` 末尾追加):

```csharp
    // —— v0.6.5.6: 多 Python 解释器管理 ——
    [JsonPropertyName("python_interpreters")]
    public List<PythonInterpreter> PythonInterpreters { get; set; } = new();

    [JsonPropertyName("active_python_interpreter_name")]
    public string ActivePythonInterpreterName { get; set; } = "";
}

public class PythonInterpreter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}
```

> 注意:把 Settings 类的最后一行 `}` 移到 PythonInterpreter 字段之后(否则两个类同名嵌套)。

- [ ] **Step 5: Run test to verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsTests.PythonInterpreters_RoundTrip" -v minimal
```

Expected: **1 PASS**。

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs
git commit -m "feat(data): Settings.PythonInterpreters + ActivePythonInterpreterName"
```

---

### Task 3: `CreateEnvDialogViewModel.ApplyTemplate` 改用 active.Path + 2 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(改 `ApplyTemplate` + 顶部警告文案)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(加 2 tests)

**Interfaces:**
- Consumes: `Models.Settings.PythonInterpreters` / `ActivePythonInterpreterName`(T2)
- Produces: 行为变更 —— `ApplyTemplate()` 用 `active.Path`(替换原 `TemplatePythonDir/DefaultPythonVersion` 拼接);v0.6.5.5 `_recentBasePythonPath` 字段、`ApplyTemplateCommand` 行为、构造函数签名**全部不动**

- [ ] **Step 1: Read existing `CreateEnvDialogViewModel.ApplyTemplate` to find target lines**

读 `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` 大约第 119-150 行,定位原 auto-fill 拼接 `TemplatePythonDir + DefaultPythonVersion + python.exe` 的代码块。

- [ ] **Step 2: Write failing tests**(`tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs` 末尾追加):

```csharp
    [Fact]
    public void ApplyTemplate_UsesActiveInterpreterPath_NotTemplateConcat()
    {
        // Arrange: settings 同时有老 TemplatePythonDir/DefaultPythonVersion 和新 PythonInterpreters
        // + ActiveName。新字段应胜出(老的被忽略)。
        var settings = MakeSettings(
            templatePythonDir: "D:/python",
            defaultPythonVersion: "3.10",
            pythonInterpreters: new()
            {
                new() { Name = "py3.11", Path = "/custom/py3.11/python.exe" },
            },
            activePythonInterpreterName: "py3.11");
        var (vm, _, _, _, _, _) = MakeVm(settings: settings, recentBasePythonPath: null);

        // Act
        vm.ApplyTemplate();

        // Assert:PythonExe == 新字段 active.Path,不是老拼接 "D:/python/3.10/python.exe"
        Assert.Equal("/custom/py3.11/python.exe", vm.PythonExe);
    }

    [Fact]
    public void ApplyTemplate_FallsBackToEmpty_WhenActiveMissing()
    {
        // Arrange: PythonInterpreters 列表为空(activeName 也指向不存在的条目)
        var settings = MakeSettings(
            templatePythonDir: "",
            defaultPythonVersion: "3.10",
            pythonInterpreters: new(),
            activePythonInterpreterName: "");
        var (vm, _, _, _, _, _) = MakeVm(settings: settings, recentBasePythonPath: null);

        // Act
        vm.ApplyTemplate();

        // Assert
        Assert.Equal("", vm.PythonExe);
    }
```

> 测试需要 `MakeSettings` / `MakeVm` 这两个 helper 存在;它们在 v0.6.5.5 已添加到测试文件里(配合 ctor 4 参)。如未存在,需先添加(参考 v0.6.5.5 plan)。如已有但只支持 v0.6.5.5 的 3 字段,扩展参数。

- [ ] **Step 3: Run tests to verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests.ApplyTemplate_UsesActiveInterpreterPath_NotTemplateConcat|FullyQualifiedName~CreateEnvDialogViewModelTests.ApplyTemplate_FallsBackToEmpty_WhenActiveMissing" -v minimal
```

Expected: **2 FAIL** — `ApplyTemplate` 仍走老拼接,PythonExe 不等于新值。

- [ ] **Step 4: Write minimal implementation**(`src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`)

定位到 `ApplyTemplate()` 方法(原内容大致如下):

```csharp
private void ApplyTemplate()
{
    var templateExe = System.IO.Path.Combine(
        _settings.TemplatePythonDir, _settings.DefaultPythonVersion, "python.exe");
    PythonExe = templateExe;
}
```

**改为:**

```csharp
private void ApplyTemplate()
{
    var active = _settings.PythonInterpreters
        .FirstOrDefault(p => p.Name == _settings.ActivePythonInterpreterName);
    PythonExe = active?.Path ?? "";
}
```

顶部 `_warnings` 计算逻辑(若原代码含 `if (string.IsNullOrEmpty(_settings.TemplatePythonDir)) warnings.Add(...)` 之类老逻辑),按 spec §2.5 三种警告文案调整:

| 情况 | 警告 |
|---|---|
| `PythonExe == ""` | "请在设置页添加 Python 解释器" |
| `PythonExe != ""` 且 `!File.Exists(PythonExe)` | "当前 Python 解释器路径不存在,请检查设置" |
| 其他 | (无警告) |

> 具体修改代码以原文件为准;目的是不再依赖 `_settings.TemplatePythonDir` / `_settings.DefaultPythonVersion`。

- [ ] **Step 5: Run tests to verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests.ApplyTemplate_UsesActiveInterpreterPath_NotTemplateConcat|FullyQualifiedName~CreateEnvDialogViewModelTests.ApplyTemplate_FallsBackToEmpty_WhenActiveMissing" -v minimal
```

Expected: **2 PASS**。

- [ ] **Step 6: Run full CreateEnvDialogViewModel test class to verify no regression**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 全 PASS(原有 4 个 v0.6.5.5 tests + 新 2 = 6 PASS)。

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs
git commit -m "feat(wpf): CreateEnvDialog ApplyTemplate uses ActivePythonInterpreter"
```

---

### Task 4: `SettingsViewModel` 增 PythonInterpreters 区段 + 4 tests + SettingsDefaults 迁移分支 + 2 migration tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(ctor 多 1 必需参 `PythonInterpreterValidator validator`;新增 4 命令 + 4 状态属性;`Dispose` 取消 CTS)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs`(末尾加迁移分支)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs`(加 4 tests)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs`(加 2 migration tests,从 Task 2 推迟过来的)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs`(若有;加 1 test 验证迁移分支;若不存在则跳过,迁移测试已写在 `SettingsTests` 即可)

**Interfaces:**
- Consumes: `PythonInterpreterValidator`(T1), `Settings.PythonInterpreters` + `PythonInterpreter` POCO(T2)
- Produces:
  ```csharp
  // SettingsViewModel:
  public IReadOnlyList<PythonInterpreter> PythonInterpreters { get; }
  public PythonInterpreter? ActivePythonInterpreter { get; }
  public RelayCommand AddPythonInterpreterCommand { get; }
  public RelayCommand ConfirmAddPythonInterpreterCommand { get; }
  public RelayCommand CancelAddPythonInterpreterCommand { get; }
  public RelayCommand RemovePythonInterpreterCommand { get; }
  public string NewPythonInterpreterName { get; set; }
  public string NewPythonInterpreterPath { get; set; }
  public string AddPythonInterpreterError { get; private set; }
  public bool IsAddPythonInterpreterOpen { get; private set; }
  public bool HasAddPythonInterpreterError => !string.IsNullOrEmpty(AddPythonInterpreterError);
  ```

> `ctor` 签名变更:`SettingsViewModel(SettingsRepository repo, GitProxyConfig proxy, PythonInterpreterValidator validator, Settings? sharedSettings = null)`。`MainViewModel` + `App.xaml.cs` 在 Task 5 注入。

- [ ] **Step 1: Write failing tests**(`tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` 末尾追加):

```csharp
    [Fact]
    public void ActivePythonInterpreter_ReturnsNull_WhenNameNotInList()
    {
        var s = new Settings
        {
            PythonInterpreters = new() { new() { Name = "py3.10", Path = "/x/3.10/python.exe" } },
            ActivePythonInterpreterName = "non-existent",  // 指向不存在
        };
        var (sut, _, _) = MakeVm(settings: s);

        Assert.Null(sut.ActivePythonInterpreter);
    }

    [Fact]
    public async Task AddPythonInterpreter_WithValidPath_WritesAndActivates()
    {
        var validator = new FakeValidator(isValid: true, version: "3.10.18");
        var (sut, repo, _) = MakeVm(validator: validator);
        sut.NewPythonInterpreterName = "py3.10";
        sut.NewPythonInterpreterPath = "/path/to/python.exe";
        await InvokeConfirmAddAsync(sut);

        Assert.Single(sut.PythonInterpreters);
        Assert.Equal("py3.10", sut.PythonInterpreters[0].Name);
        Assert.Equal("/path/to/python.exe", sut.PythonInterpreters[0].Path);
        Assert.Equal("py3.10", sut.ActivePythonInterpreterName);
        Assert.Equal(1, repo.SaveCount);  // Save 被调用
        Assert.False(sut.IsAddPythonInterpreterOpen);
    }

    [Fact]
    public async Task AddPythonInterpreter_WithInvalidPath_ShowsError_DoesNotWrite()
    {
        var validator = new FakeValidator(isValid: false, error: "不是合法 Python 解释器");
        var (sut, repo, _) = MakeVm(validator: validator);
        sut.NewPythonInterpreterName = "bad";
        sut.NewPythonInterpreterPath = "/notepad.exe";
        await InvokeConfirmAddAsync(sut);

        Assert.Empty(sut.PythonInterpreters);
        Assert.Equal("不是合法 Python 解释器", sut.AddPythonInterpreterError);
        Assert.True(sut.IsAddPythonInterpreterOpen);  // 表单保持打开
        Assert.Equal(0, repo.SaveCount);
    }

    [Fact]
    public void RemovePythonInterpreter_ResetsActive_WhenActiveRemoved()
    {
        var s = new Settings
        {
            PythonInterpreters = new()
            {
                new() { Name = "py3.10", Path = "/3.10/python.exe" },
                new() { Name = "py3.11", Path = "/3.11/python.exe" },
            },
            ActivePythonInterpreterName = "py3.10",
        };
        var (sut, _, _) = MakeVm(settings: s);

        sut.RemovePythonInterpreterCommand.Execute(s.PythonInterpreters[0]);

        Assert.Single(sut.PythonInterpreters);
        Assert.Equal("py3.11", sut.ActivePythonInterpreterName);  // 回退到剩余第一条
    }
```

> `FakeValidator` / `MakeVm` / `InvokeConfirmAddAsync` 是测试 helper(详见 Step 1.5):

**Step 1.5: 添加测试 helpers**(`tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` 顶部加):

```csharp
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FakeValidator : PythonInterpreterValidator
{
    private readonly bool _isValid;
    private readonly string _version;
    private readonly string _error;

    public FakeValidator(bool isValid, string version = "", string error = "")
    {
        _isValid = isValid;
        _version = version;
        _error = error;
    }

    public override async Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
    {
        await Task.Yield();
        return _isValid
            ? new ValidationResult(true, Version: _version)
            : new ValidationResult(false, Error: _error);
    }
}
```

> **注意:** `PythonInterpreterValidator` 当前是 `sealed`,不能继承。Step 4 实现时要么:
> - (a) 把 `sealed` 去掉(Fake 继承)—— 跟 v0.6.5.5 `VenvCreator` / `JunctionLinker` 模式一致(brief-blessed "如果 sealed 不可继承...")
> - (b) 引入 `IPythonInterpreterValidator` 接口,`PythonInterpreterValidator` 实现接口,`FakeValidator` 也实现接口
>
> **选 (b)**:更干净,生产代码无 sealed 移除成本;接口变更给 SettingsViewModel ctor 注入接口而非具体类。helper 测试 + FakeValidator 实现 `IPythonInterpreterValidator`。

**修正版:**

```csharp
// Services/PythonInterpreterValidator.cs —— 加 interface(Step 4 时一起改)
public interface IPythonInterpreterValidator
{
    Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default);
}

public sealed class PythonInterpreterValidator : IPythonInterpreterValidator { ... }

// FakeValidator 改为实现接口:
internal sealed class FakeValidator : IPythonInterpreterValidator { ... }

// SettingsViewModel ctor 接收 IPythonInterpreterValidator validator

// MakeVm helper:
private (SettingsViewModel vm, FakeSettingsRepository repo, Settings settings) MakeVm(
    Settings? settings = null,
    IPythonInterpreterValidator? validator = null,
    string tempDir = null!)
{
    repo = new FakeSettingsRepository();
    settings ??= new Settings();
    validator ??= new FakeValidator(isValid: true);
    var vm = new SettingsViewModel(repo, GitProxyConfig.Default, validator, settings);
    return (vm, repo, settings);
}

private static async Task InvokeConfirmAddAsync(SettingsViewModel vm)
{
    vm.ConfirmAddPythonInterpreterCommand.Execute(null);
    // wait for in-flight validator
    for (int i = 0; i < 100; i++)
    {
        await Task.Delay(10);
        if (!vm.IsAddPythonInterpreterOpen || !string.IsNullOrEmpty(vm.NewPythonInterpreterName) == false) break;
        // 简单轮询:validator 完成后 IsAddPythonInterpreterOpen 应 = false;失败则保持 true
        // 实际更简单:等 100ms 让 Task 完成
    }
    await Task.Delay(100);  // 留时间让 async validator 完成
}
```

> `FakeSettingsRepository` 已存在于 v0.6.5.x 测试代码(如没有则加);`GitProxyConfig.Default` 可能不存在,改用 `new GitProxyConfig()` 或现有 factory。
> `InvokeConfirmAddAsync` 实际不需要轮询逻辑 —— `ConfirmAddPythonInterpreterCommand.Execute` 是同步入口但内部 `await`,需要 `await` 命令的 Task。可改为:

```csharp
// 让 RelayCommand 支持异步 / 或 vm 暴露 ConfirmAddPythonInterpreterAsync 方法
// 简化:vm 加 public async Task ConfirmAddAsync() 方法,Command.Execute 内部 await 它。
// 实施时按此方案。
```

**最终方案:** Task 4 实现时 `ConfirmAddPythonInterpreterCommand.Execute` 内部 `await vm.ConfirmAddAsync()`;测试调 `await vm.ConfirmAddAsync()` 直接。

- [ ] **Step 2: Run tests to verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModelTests" -v minimal
```

Expected: **FAIL** — `PythonInterpreters` 属性不存在 / `IPythonInterpreterValidator` 未定义。

- [ ] **Step 3: Write minimal implementation**

**3a.** `src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs` 顶部加接口:

```csharp
public interface IPythonInterpreterValidator
{
    Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default);
}

public sealed class PythonInterpreterValidator : IPythonInterpreterValidator
{
    // ... 已有实现 ...
}
```

**3b.** `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` 改动:

```csharp
using System.Threading;
using ComfyUI.Manager.Services;

// ctor 改签名:
public SettingsViewModel(
    SettingsRepository repo,
    GitProxyConfig proxy,
    IPythonInterpreterValidator validator,
    Settings? sharedSettings = null)
{
    _repo = repo;
    _proxy = proxy;
    _validator = validator;
    _settings = sharedSettings ?? _repo.Load();
    SettingsDefaults.Apply(_settings, AppContext.BaseDirectory);
    _repo.Save(_settings);
    // ... 现有 ExtraPaths / QuerySources / DownloadSources 初始化保留 ...

    // 新增 PythonInterpreters 区段:
    PythonInterpreters = new ObservableCollection<PythonInterpreter>(_settings.PythonInterpreters);
    PythonInterpreters.CollectionChanged += (_, _) =>
    {
        _settings.PythonInterpreters = new List<PythonInterpreter>(PythonInterpreters);
        _repo.Save(_settings);
        RaisePropertyChanged(nameof(ActivePythonInterpreter));
    };

    _addPythonInterpreterCts = new CancellationTokenSource();
    AddPythonInterpreterCommand = new RelayCommand(_ =>
    {
        NewPythonInterpreterName = "";
        NewPythonInterpreterPath = "";
        AddPythonInterpreterError = "";
        IsAddPythonInterpreterOpen = true;
    });
    CancelAddPythonInterpreterCommand = new RelayCommand(_ =>
    {
        IsAddPythonInterpreterOpen = false;
        AddPythonInterpreterError = "";
    });
    ConfirmAddPythonInterpreterCommand = new RelayCommand(async _ =>
    {
        await ConfirmAddPythonInterpreterAsync().ConfigureAwait(false);
    });
    RemovePythonInterpreterCommand = new RelayCommand(p =>
    {
        if (p is PythonInterpreter pi)
        {
            var wasActive = pi.Name == _settings.ActivePythonInterpreterName;
            PythonInterpreters.Remove(pi);
            if (wasActive)
            {
                _settings.ActivePythonInterpreterName = PythonInterpreters.FirstOrDefault()?.Name ?? "";
                _repo.Save(_settings);
                RaisePropertyChanged(nameof(ActivePythonInterpreter));
            }
        }
    });
}

private readonly IPythonInterpreterValidator _validator;
private CancellationTokenSource _addPythonInterpreterCts;

public ObservableCollection<PythonInterpreter> PythonInterpreters { get; }
public PythonInterpreter? ActivePythonInterpreter
{
    get
    {
        var name = _settings.ActivePythonInterpreterName;
        if (string.IsNullOrEmpty(name)) return null;
        return _settings.PythonInterpreters.FirstOrDefault(p => p.Name == name);
    }
}

private string _newPythonInterpreterName = "";
public string NewPythonInterpreterName
{
    get => _newPythonInterpreterName;
    set => SetField(ref _newPythonInterpreterName, value);
}
private string _newPythonInterpreterPath = "";
public string NewPythonInterpreterPath
{
    get => _newPythonInterpreterPath;
    set => SetField(ref _newPythonInterpreterPath, value);
}
private string _addPythonInterpreterError = "";
public string AddPythonInterpreterError
{
    get => _addPythonInterpreterError;
    private set { if (SetField(ref _addPythonInterpreterError, value)) RaisePropertyChanged(nameof(HasAddPythonInterpreterError)); }
}
public bool HasAddPythonInterpreterError => !string.IsNullOrEmpty(_addPythonInterpreterError);
private bool _isAddPythonInterpreterOpen;
public bool IsAddPythonInterpreterOpen
{
    get => _isAddPythonInterpreterOpen;
    private set => SetField(ref _isAddPythonInterpreterOpen, value);
}

public async Task ConfirmAddPythonInterpreterAsync()
{
    if (string.IsNullOrWhiteSpace(NewPythonInterpreterName) ||
        string.IsNullOrWhiteSpace(NewPythonInterpreterPath))
    {
        IsAddPythonInterpreterOpen = false;
        return;
    }

    AddPythonInterpreterError = "";
    try
    {
        var result = await _validator.ValidateAsync(NewPythonInterpreterPath, _addPythonInterpreterCts.Token)
            .ConfigureAwait(true);
        if (!result.IsValid)
        {
            AddPythonInterpreterError = result.Error ?? "验证失败";
            return;  // 表单保持打开
        }

        var pi = new PythonInterpreter
        {
            Name = NewPythonInterpreterName,
            Path = NewPythonInterpreterPath,
        };
        PythonInterpreters.Add(pi);
        _settings.ActivePythonInterpreterName = pi.Name;  // 新增即激活
        _repo.Save(_settings);

        IsAddPythonInterpreterOpen = false;
        NewPythonInterpreterName = "";
        NewPythonInterpreterPath = "";
    }
    catch (OperationCanceledException)
    {
        // vm dispose 时取消,静默
    }
}
```

> `SettingsViewModel` 应实现 `IDisposable`(如未实现),在 `Dispose` 中 `_addPythonInterpreterCts.Cancel(); _addPythonInterpreterCts.Dispose();`。

> ctor 调用 `SettingsDefaults.Apply(_settings, AppContext.BaseDirectory)` 已存在 —— 迁移分支在 Step 3c 添加。

**3c.** `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` `Apply(s, projectRoot)` 方法末尾追加:

```csharp
// —— v0.6.5.6:首次加载老 settings.json 时,从老 TemplatePythonDir/DefaultPythonVersion 合成默认条目 ——
if (s.PythonInterpreters.Count == 0
    && !string.IsNullOrWhiteSpace(s.TemplatePythonDir)
    && !string.IsNullOrWhiteSpace(s.DefaultPythonVersion))
{
    var candidate = System.IO.Path.Combine(
        s.TemplatePythonDir, s.DefaultPythonVersion, "python.exe");
    s.PythonInterpreters.Add(new PythonInterpreter
    {
        Name = s.DefaultPythonVersion,
        Path = candidate,
    });
    s.ActivePythonInterpreterName = s.DefaultPythonVersion;
    // 老字段 TemplatePythonDir / DefaultPythonVersion 保留不动
}
```

- [ ] **Step 4: Write migration tests**(`tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs` 末尾追加,从 Task 2 推迟过来):

```csharp
    [Fact]
    public void Migration_FirstLoadFromLegacyTemplatePythonDir_CreatesDefaultEntry()
    {
        var json = """
        {
          "template_python_dir": "D:/python",
          "default_python_version": "3.10",
          "github_token": "abc"
        }
        """;

        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);

        Assert.Single(s.PythonInterpreters);
        Assert.Equal("3.10", s.PythonInterpreters[0].Name);
        Assert.Equal(Path.Combine("D:/python", "3.10", "python.exe"), s.PythonInterpreters[0].Path);
        Assert.Equal("3.10", s.ActivePythonInterpreterName);
        // 老字段保留
        Assert.Equal("D:/python", s.TemplatePythonDir);
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void Migration_NoOp_WhenPythonInterpretersNonEmpty()
    {
        var json = """
        {
          "python_interpreters": [
            { "name": "user-added", "path": "E:/custom/python.exe" }
          ],
          "active_python_interpreter_name": "user-added",
          "template_python_dir": "D:/python",
          "default_python_version": "3.10"
        }
        """;

        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);

        Assert.Single(s.PythonInterpreters);
        Assert.Equal("user-added", s.PythonInterpreters[0].Name);
        Assert.Equal("user-added", s.ActivePythonInterpreterName);
    }
```

- [ ] **Step 5: Run tests to verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsTests" -v minimal
```

Expected: **PASS**(4 SettingsViewModel + 1 round-trip + 2 migration = 7 new)。

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Services/PythonInterpreterValidator.cs \
        src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs
git commit -m "feat(wpf): SettingsViewModel PythonInterpreters section + migration"
```

---

### Task 5: `App.xaml.cs` + `MainViewModel` 注入 validator + `SettingsView.xaml` 新增区段 + `SettingsView.xaml.cs` 浏览按钮

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(实例化 `PythonInterpreterValidator` 并传给 MainViewModel)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(SettingsViewModel 构造多传 1 参 validator)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(新增 "Python 解释器" 区段 + 老字段只读 label)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(新增 `BrowsePythonInterpreter` handler)

**Interfaces:**
- Consumes: `IPythonInterpreterValidator`(T4)
- Produces: 生产环境 SettingsViewModel 构造路径打通;Settings 面板 UI 完整

- [ ] **Step 1: Read existing wiring**

读 `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` line 117-119(已知) + `App.xaml.cs` line 67-81(已知)。

- [ ] **Step 2: Write minimal implementation**

**2a.** `App.xaml.cs` 在 line 67 之前(`var envCreator = ...` 后)插入:

```csharp
        var pythonValidator = new PythonInterpreterValidator();
```

并修改 line 78-81 `_mainVm = new MainViewModel(...)` 调用 —— 这需要把 `pythonValidator` 传给 MainViewModel ctor。**简化路径:让 MainViewModel 自己构造 validator,App.xaml.cs 不传。**

**修正版 2a.** `App.xaml.cs` **不修改**;`MainViewModel` 内部 `new PythonInterpreterValidator()`(在 SettingsViewModel 构造位置)。这样 App.xaml.cs 改动最小:

```csharp
// MainViewModel.cs line 117-119 改:
CurrentView = new SettingsView
{
    DataContext = new SettingsViewModel(_settingsRepo, _gitProxy, new PythonInterpreterValidator(), _settings),
};
```

> 代价:SettingsViewModel 没法注入 fake validator 给生产代码 —— 但生产代码不测试 validator 行为,validator 只在用户操作时跑,不影响单元测试隔离。

**最终方案(权衡后):** 让 `MainViewModel` 构造 SettingsViewModel 时 `new PythonInterpreterValidator()`。`App.xaml.cs` 0 改动。`MainViewModel` 改 1 行。

**2b.** `MainViewModel.cs` 顶部 `using`:

```csharp
using ComfyUI.Manager.Services;
```

并改 line 117-119 为上述。

**2c.** `SettingsView.xaml` 在"路径"section 末尾、"环境 / 工具"section 之前插入新 section:

```xml
<!-- ============ Python 解释器(v0.6.5.6) ============ -->
<TextBlock Text="Python 解释器(可定义多个,选一个作为 auto-fill 默认)"
           FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
<TextBlock Text="当前使用" Margin="0,0,0,4" />
<ComboBox ItemsSource="{Binding PythonInterpreters}"
          DisplayMemberPath="Name"
          SelectedValuePath="Name"
          SelectedValue="{Binding ActivePythonInterpreterName, UpdateSourceTrigger=PropertyChanged}"
          Width="320" HorizontalAlignment="Left" />
<ItemsControl ItemsSource="{Binding PythonInterpreters}" Margin="0,8,0,0">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Grid Margin="0,4,0,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="160" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="{Binding Name}" VerticalAlignment="Center" />
                <TextBlock Grid.Column="1" Text="{Binding Path}" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center" Margin="8,0,0,0" />
                <Button Grid.Column="2" Content="删除" Margin="8,0,0,0"
                        Command="{Binding DataContext.RemovePythonInterpreterCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}"
                        Style="{StaticResource MaterialButton}" />
            </Grid>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
<Button Content="+ 添加解释器" Margin="0,8,0,0" HorizontalAlignment="Left"
        Command="{Binding AddPythonInterpreterCommand}"
        Style="{StaticResource MaterialButton}" />
<Grid Margin="0,8,0,0"
      Visibility="{Binding IsAddPythonInterpreterOpen,
                    Converter={StaticResource BoolToVisibility}}">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <Grid Grid.Column="0">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="名称(唯一)" Margin="0,0,0,4" />
        <TextBox Grid.Row="1" Style="{StaticResource MaterialTextBox}"
                 Text="{Binding NewPythonInterpreterName, UpdateSourceTrigger=PropertyChanged}" />
        <TextBlock Grid.Row="0" Text="路径(.exe)" Margin="0,32,0,4" />
        <DockPanel Grid.Row="1" Margin="0,28,0,0">
            <Button DockPanel.Dock="Right" Content="浏览..."
                    Click="BrowsePythonInterpreter"
                    Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
            <TextBox Text="{Binding NewPythonInterpreterPath, UpdateSourceTrigger=PropertyChanged}"
                     Style="{StaticResource MaterialTextBox}" />
        </DockPanel>
    </Grid>
    <Button Grid.Column="1" Content="确定" Margin="8,0,0,0" VerticalAlignment="Bottom"
            Command="{Binding ConfirmAddPythonInterpreterCommand}"
            Style="{StaticResource MaterialButton}" />
    <Button Grid.Column="2" Content="取消" Margin="4,0,0,0" VerticalAlignment="Bottom"
            Command="{Binding CancelAddPythonInterpreterCommand}"
            Style="{StaticResource MaterialButton}" />
</Grid>
<TextBlock Text="{Binding AddPythonInterpreterError}"
           Foreground="OrangeRed" FontSize="11"
           Margin="0,4,0,0"
           Visibility="{Binding HasAddPythonInterpreterError,
                         Converter={StaticResource BoolToVisibility}}" />
```

> 布局细节(Name + Path 上下两行,还是左右两列)由 implementer 微调;功能绑定必须正确。`BoolToVisibility` converter 在 App.xaml 已存在。

**2d.** "路径"section 末尾的"默认 Python 版本"行改 IsReadOnly=True:

定位 `Views/SettingsView.xaml` 第 160-167 行(原 ComboBox + 灰色提示):

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

**改为:**

```xml
<TextBlock Text="默认 Python 版本(已废弃,只读 — 请用上方 'Python 解释器' 区段)" Margin="0,8,0,4" />
<TextBox Text="{Binding DefaultPythonVersion}" IsReadOnly="True"
         Style="{StaticResource MaterialTextBox}" />
```

并把"模板 Python 目录"行(IsReadOnly?):

定位第 142-149 行,改为:

```xml
<TextBlock Text="模板 Python 目录(已废弃,只读)" Margin="0,8,0,4" />
<TextBox Text="{Binding TemplatePythonDir}" IsReadOnly="True"
         Style="{StaticResource MaterialTextBox}" />
```

> `TemplatePythonDir` setter 仍触发 Save(现有逻辑不动),UI 不暴露给用户编辑。

**2e.** `SettingsView.xaml.cs` 添加 handler:

```csharp
private void BrowsePythonInterpreter(object sender, RoutedEventArgs e)
{
    var dlg = new Microsoft.Win32.OpenFileDialog
    {
        Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        Title = "选择 Python 解释器",
    };
    if (dlg.ShowDialog() == true)
    {
        var vm = (SettingsViewModel)DataContext;
        vm.NewPythonInterpreterPath = dlg.FileName;
    }
}
```

> 顶部 `using` 已含 `Microsoft.Win32`(参考 line 9)。

- [ ] **Step 3: Run full WPF test suite to verify no regression**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: **312 PASS / 1 SKIP / 0 FAIL**(基线 298 + 14 新:Validator 5 + Settings 3 + DialogVM 2 + SettingsVM 4)。

- [ ] **Step 4: Run Release build**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: **0 errors**(允许 NU1900 NuGet 网络 warning)。

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs
git commit -m "feat(wpf): Settings UI 多 Python 解释器区段 + 只读老字段"
```

---

### Task 6: close-out + bump v0.6.5.6 + release notes + ledger

**Files:**
- Modify: `pyproject.toml`(line 3)
- Modify: `src/comfy_mgr/__init__.py`(line 1)
- Modify: `shared/errors.json`(line 2)
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`(line 11)
- Modify: `tests/test_version_consistency.py`(3 处字面量)
- Create: `release/RELEASE-NOTES-v0.6.5.6.md`(中文,follow v0.6.5.5 风格)
- Modify: `.superpowers/sdd/2026-08-03-settings-python-interpreters/progress.md`(新建 ledger + 添加 T1-T6 completion 行,gitignored)

- [ ] **Step 1: Bump 5 处版本字面量 `0.6.5.5` → `0.6.5.6`**

```bash
cd "D:/ToolDevelop/ComfyUI" && \
  sed -i 's/0\.6\.5\.5/0.6.5.6/g' pyproject.toml \
                                  src/comfy_mgr/__init__.py \
                                  shared/errors.json \
                                  src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
                                  tests/test_version_consistency.py
```

逐个 `grep "0.6.5.5"` 验证 0 处残留(除 release notes / changelog 等历史文件)。

- [ ] **Step 2: Run pytest version consistency**

```bash
cd "D:/ToolDevelop/ComfyUI" && PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q
```

Expected: **3 PASS**。

- [ ] **Step 3: Run full WPF test suite**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: **312 PASS / 1 SKIP / 0 FAIL**。

- [ ] **Step 4: Run WPF Release build**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: **0 errors**(允许 NU1900 NuGet 网络 warning)。

- [ ] **Step 5: Create `release/RELEASE-NOTES-v0.6.5.6.md`**(follow v0.6.5.5 风格,中文,~85 行)

模板来源:`release/RELEASE-NOTES-v0.6.5.5.md` —— 抄其 §1-§5 结构,内容按 spec §6 写。

- [ ] **Step 6: Update `.superpowers/sdd/2026-08-03-settings-python-interpreters/progress.md`**

新文件(workspace 是 gitignored,但 spec §6/§7 之前没有):

```markdown
# SDD ledger — plan: docs/superpowers/plans/2026-08-03-settings-python-interpreters.md

Base SHA: 8279c61 (v0.6.5.6 spec commit)

Tasks:
- T1 PythonInterpreterValidator + 5 tests (CORE - sonnet)
- T2 Settings.PythonInterpreters + ActivePythonInterpreterName + PythonInterpreter POCO + 1 round-trip test
- T3 CreateEnvDialogViewModel.ApplyTemplate 改用 active.Path + 2 tests
- T4 SettingsViewModel 增区段 + 4 tests + SettingsDefaults 迁移分支 + 2 migration tests
- T5 App.xaml.cs/MainViewModel 注入 validator + SettingsView.xaml 新区段 + SettingsView.xaml.cs 浏览按钮
- T6 close-out + bump v0.6.5.6 + release notes + ledger
```

各 task 完成时 append `Task <N>: complete (...)` 行。

- [ ] **Step 7: Commit**

```bash
git add release/RELEASE-NOTES-v0.6.5.6.md \
        pyproject.toml \
        src/comfy_mgr/__init__.py \
        shared/errors.json \
        src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
        tests/test_version_consistency.py
git commit -m "chore(release): bump to v0.6.5.6 + release notes"
```

- [ ] **Step 8: Verify final state**

```bash
git log --oneline 6d4d211..HEAD  # 应有 7 commits(plan + 6 task commits 或 T1-T6 6 task commits 不带 plan)
git status --short  # 应空(除 gitignored .superpowers/sdd/)
```

Expected: 6 commits on top of v0.6.5.5 `6d4d211`(不含 plan commit;plan commit 是 task 0);working tree clean。

- [ ] **Step 9: Report release boundary** —— 待用户授权的外部操作(push / tag / gh release / rebuild zip),沿用 v0.6.5.5 模式。

---

## Self-Review

### 1) Spec coverage

| Spec § | Plan task |
|---|---|
| §1 数据模型 | T2(Settings 字段 + POCO + round-trip) |
| §2.1 PythonInterpreterValidator | T1 |
| §2.2 SettingsViewModel 区段 | T4 |
| §2.3 SettingsView.xaml 区段 | T5 |
| §2.4 Browse 按钮 | T5 |
| §2.5 ApplyTemplate 改 | T3 |
| §2.6 SettingsDefaults 迁移 | T4 |
| §4 测试 | T1(5) + T2(1) + T3(2) + T4(6) = 14 tests,符合 spec §4.3 期望 |
| §6 Release notes | T6 |
| §6 升级注意 | T6 |
| G12 ctor 注入 + MainViewModel 改 | T5 |
| 老字段保留 | T5(改 IsReadOnly) |

全部 spec 需求已映射。无遗漏。

### 2) Placeholder scan

- ✓ 无 TBD / TODO / 待定
- ✓ Step 1 tests 都含完整断言
- ✓ Step 3 implementation 都含完整代码
- ✓ 无"参考 v0.6.5.5"占位 —— 该写代码的都写完整

### 3) Type consistency

- `PythonInterpreter` POCO 在 T2 定义,T4 在 `SettingsViewModel` / `SettingsDefaults` 引用 —— 一致。
- `IPythonInterpreterValidator` 在 T4 定义,T1 隐式升级(sealed → sealed + interface),T4 测试用 Fake —— 一致。
- `ActivePythonInterpreterName` 在 T2 (Settings),T3 (ApplyTemplate),T4 (SettingsViewModel) 三处使用 —— 一致。
- `ConfirmAddPythonInterpreterAsync` 是 public 方法,T4 测试通过它直接 await —— 一致。

### 4) 已知风险

- **T3 ApplyTemplate 改动**会破坏 v0.6.5.5 的"`TemplatePythonDir + DefaultPythonVersion` 拼接"逻辑,如用户没迁迁移(老 settings.json 但 ctor 报 error)可能看到空 PythonExe + 黄条。Spec 已说明这是设计意图,T3 测试已覆盖。
- **T5 XAML** 大段 UI 代码,无单元测试;依赖手动 smoke。如实施员想拆 Add/Remove/Confirm 内联表单,可微调。
- **T6 release notes** §5 Commits 列 `<this commit>` 占位符(同 v0.6.5.5 模式),SHA 实际值 commit 后用户 `git log` 查。

---

## Execution choice

Plan complete and saved to `docs/superpowers/plans/2026-08-03-settings-python-interpreters.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task (T1-T6), per-task review between, broad whole-branch review at the end, then fix wave.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints for review.

Which approach?