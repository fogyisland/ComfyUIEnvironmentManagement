# Fooocus Entry Mode 切换 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Fooocus 模板支持「AutoUpdate vs Stable」entry 模式切换 — `entry_with_update.py` (默认,跟上游同步) ↔ `entry.py` (生产可预测不 auto-update)。用户通过手编辑 `config/settings.inf` 切换(`fooocus_entry_mode = Stable`),已存在 env 不受影响(snapshot 机制)。

**Architecture:** 数据驱动 + 1 个 ProcessLauncher kind-special 分支,跟现有 `Forge` kind-special 分支(line 904)同 pattern。
1. `TemplateConfig` 加 `FooocusEntryMode` 枚举字段(`AutoUpdate=0` / `Stable=1`),`JsonStringEnumConverter` 数字 fallback → 零迁移
2. `TemplateConfigDefaults.Fooocus` factory 显式赋默认值 `AutoUpdate`(默认行为零变化)
3. `ProcessLauncher.BuildStartCommand` line 863 后插 1 个 if:`Kind=="Fooocus" && FooocusEntryMode==Stable` → 用 `entry.py` 替 `snapshot.EntryScript`
4. snapshot 机制(ProcessLauncher.cs:843-846)保留 — 改 `settings.Templates["Fooocus"]` 不影响已存在 env

**Tech Stack:** C# 12 / .NET 8 WPF, `System.Text.Json` `JsonStringEnumConverter` (已有 precedent `TemplateSourceKind`), xUnit, 无新依赖。

**Spec:** `D:/ToolDevelop/ComfyUI/docs/superpowers/specs/2026-08-31-fooocus-entry-mode-design.md`

---

## Global Constraints

| 约束 | 严格度 |
|---|---|
| **不破坏 v1.0.0.x EditTemplateDialog「不显示 entry 字段」决策** — 不动 `EditTemplateDialogViewModel.cs:216-219` 那块 | hard |
| **snapshot 机制不变** — 改 `settings.Templates["Fooocus"]` 不影响已存在 env | hard |
| **零 schema migration** — 老 settings.inf / 老 settings.json 加字段不抛异常 | hard |
| **零新 enum for other templates** — 只 Fooocus 一个模板用 entry mode 切换,YAGNI | hard |
| **不引入 UI 改动** — 用户手编辑 settings.inf(跟 v1.0.0.x EditTemplateDialog 决策对齐) | hard |
| **测试 ≥ 10 个** — 9 TemplateConfigDefaults + 4 ProcessLauncher + 1 TemplateConfigTests round-trip + 1 settings.inf round-trip = **15 个新增 test** | hard |
| **full suite 不回归** — 改动 3 源 + 4 test 文件,跑 full suite ≥ 2506 PASS(已知 1 known flaky `BaseEnvStatusViewModelTests.LogLines_CappedAtMaxLogLines` 不破) | hard |
| **commit message 走 Bash heredoc `<<'EOF'...EOF`** — 避免 PowerShell `@'...'@` stray `@` | hard |
| **不 amend 既有 commit** — 包括 `6621c373` 的 stray `@` cosmetic | hard |
| **Branch:`main` direct** — per user decision,不开 feature branch / 不开 worktree;SDD 在 main 上跑 | hard |

---

## File Structure

### 新增(2 文件)
| 路径 | 职责 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs` | 9 个 factory 字段断言 test |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs` | 4 个 BuildStartCommand Fooocus mode test |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfRoundTripFooocusTests.cs` | 1 个 settings.inf → TemplateConfig.FooocusEntryMode round-trip test |

### 修改(3 文件)
| 路径 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs` | line 56 后插 `FooocusEntryMode` 字段(7 行:含 doc comment + JsonPropertyName + JsonConverter + property declaration);line 15-19 `TemplateSourceKind` 旁插 `FooocusEntryMode` enum(8 行:含 doc comment + 2 个 enum 值) |
| `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` | line 228-240 Fooocus factory +1 行 `FooocusEntryMode = FooocusEntryMode.AutoUpdate,` |
| `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` | line 863 后插 5 行 Fooocus stable 分支 |
| `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs` | line 11-43 `RoundTrip_AllFields_PreservesValues` +2 行(`FooocusEntryMode = Stable` + 断言) |

### 总改动
- **生产代码:** 3 文件,约 +22 行净增(enum + field + factory 1 行 + ProcessLauncher 5 行)
- **测试代码:** 4 文件,约 +250 行净增(15 个新 test + 2 行 TemplateConfigTests 修改)

---

## Task 1: TemplateConfig 数据层 + Fooocus factory 默认值 + 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs` (line 15-19 加 enum;line 56 后加 field)
- Modify: `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` (line 228-240 Fooocus factory +1 行)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs` (~9 tests, ~90 行)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs` (line 14-26 +line 14-42 加 FooocusEntryMode round-trip 断言)

**Interfaces:**
- Consumes: `JsonStringEnumConverter` (from `System.Text.Json.Serialization`,已在 `using` line 4)
- Consumes: `TemplateSourceKind` enum (mirror line 15-19 pattern)
- Consumes: `JsonPropertyName` attribute pattern (mirror line 28-56 fields)
- Produces: `TemplateConfig.FooocusEntryMode` property — `FooocusEntryMode` enum, default `AutoUpdate = 0`
- Produces: `FooocusEntryMode.AutoUpdate = 0`(老 settings 缺字段 fallback), `FooocusEntryMode.Stable = 1`
- Produces: `TemplateConfigDefaults.Fooocus(...)` 返回的 cfg `FooocusEntryMode == FooocusEntryMode.AutoUpdate`

### Step 1: Write the failing `TemplateConfigDefaultsFooocusTests` 9 tests

**File:** `D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs`

镜像 `TemplateConfigDefaultsLtxVideoTests.cs` 的风格(8 tests,1 file,1 factory)。**完整代码**:

```csharp
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class TemplateConfigDefaultsFooocusTests
{
    [Fact]
    public void Fooocus_Name_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.Name);
    }

    [Fact]
    public void Fooocus_Kind_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.Kind);
    }

    [Fact]
    public void Fooocus_LocalSourceDir_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.LocalSourceDir);
    }

    [Fact]
    public void Fooocus_SourceKind_IsGitHub()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind);
    }

    [Fact]
    public void Fooocus_GitHubRepoUrl_IsLllyasviel()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("https://github.com/lllyasviel/Fooocus.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void Fooocus_EntryScript_IsEntryWithUpdate()
    {
        // 默认 AutoUpdate 模式:EntryScript 仍是 entry_with_update.py(现状)
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("entry_with_update.py", cfg.EntryScript);
    }

    [Fact]
    public void Fooocus_EntryArgs_ContainsPortAndListen()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Contains("{port}", cfg.EntryArgs);
        Assert.Contains("--listen", cfg.EntryArgs);
    }

    [Fact]
    public void Fooocus_ModelsSubdir_IsModels()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("models", cfg.ModelsSubdir);
    }

    // v1.0.0.x: 新增字段 — Fooocus entry mode 默认 = AutoUpdate (0, 跟现状 100% 一致)
    [Fact]
    public void Fooocus_FooocusEntryMode_IsAutoUpdate()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal(FooocusEntryMode.AutoUpdate, cfg.FooocusEntryMode);
    }
}
```

### Step 2: Run tests to verify they fail

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~TemplateConfigDefaultsFooocusTests" \
  -v minimal
```

Expected: 9 tests FAIL with **CS0117 / CS0246** error(`FooocusEntryMode` 符号不存在 — 因为还没加 enum 和 field)。

### Step 3: Add `FooocusEntryMode` enum 到 `TemplateConfig.cs`

**File:** `D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs`

**位置:** 在 line 15-19 `public enum TemplateSourceKind` 块**之后**(line 19 之后空行前)插入:

```csharp

/// <summary>
/// Fooocus 模板 entry 模式:<see cref="AutoUpdate"/> = 跟上游同步(默认,现状,跟 v1.0.0 行为 100% 一致);
/// <see cref="Stable"/> = 用 <c>entry.py</c> 不 auto-update,生产可预测。
/// 镜像 <see cref="TemplateSourceKind"/> 的数字 fallback 模式 — 老 settings 缺字段 → 0 → AutoUpdate,
/// 零迁移成本。JsonStringEnumConverter 把数字 / "AutoUpdate" / "Stable" 都接受为合法值。
/// </summary>
public enum FooocusEntryMode
{
    AutoUpdate = 0,
    Stable = 1,
}
```

### Step 4: Add `FooocusEntryMode` field 到 `TemplateConfig` class

**位置:** 在 line 56 `public string UserExtraArgs { get; set; } = "";` **之后**,line 58 `/// <summary>` (Meta 字段的 doc comment 开始) **之前**插入:

```csharp

    /// <summary>
    /// v1.0.0.x (2026-08-31):Fooocus entry 模式 — 仅 Kind=="Fooocus" 时由
    /// <see cref="ComfyUI.Manager.Infrastructure.ProcessLauncher.BuildStartCommand"/> 读取。
    /// <see cref="FooocusEntryMode.AutoUpdate"/> (默认) 用 entry_with_update.py;
    /// <see cref="FooocusEntryMode.Stable"/> 用 entry.py。
    /// 改 settings 不影响已存在 env(env.TemplateConfigSnapshot 冻结,ProcessLauncher.cs:843-846)。
    /// 老 settings 缺字段 → JsonStringEnumConverter 数字 fallback → AutoUpdate。
    /// </summary>
    [JsonPropertyName("fooocus_entry_mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FooocusEntryMode FooocusEntryMode { get; set; } = FooocusEntryMode.AutoUpdate;
```

**确认 line 56 后有空行(line 57 是空行)** — Edit 的 old_string 必须包含这个空行上下文以保证唯一性。

### Step 5: Add `FooocusEntryMode = FooocusEntryMode.AutoUpdate` 到 `TemplateConfigDefaults.Fooocus` factory

**File:** `D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs`

**位置:** 在 line 239 `UserExtraArgs = "",` **之后**,line 240 `};` **之前**插入:

```csharp
        FooocusEntryMode = FooocusEntryMode.AutoUpdate,   // v1.0.0.x 默认 = 现状 (entry_with_update.py)
```

(`Fooocus` factory 用 expression body `=> new() { ... }`,属性间用逗号 + 换行;最后一行属性前加逗号前导空格对齐。)

### Step 6: Run the 9 tests to verify they pass

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~TemplateConfigDefaultsFooocusTests" \
  -v minimal
```

Expected: 9/9 PASS。

### Step 7: Add `FooocusEntryMode` round-trip assertion 到 `TemplateConfigTests.RoundTrip_AllFields_PreservesValues`

**File:** `D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs`

**位置 A** (line 14-26 `var original = new TemplateConfig { ... };` 块):在 line 25 `UserExtraArgs = "--preview-method auto",` **之后**插入:

```csharp
            FooocusEntryMode = FooocusEntryMode.Stable,   // v1.0.0.x 新字段,确保 round-trip
```

**位置 B** (line 32-42 断言块):在 line 42 `Assert.Equal("--preview-method auto", restored.UserExtraArgs);` **之后**插入:

```csharp
        Assert.Equal(FooocusEntryMode.Stable, restored!.FooocusEntryMode);
```

### Step 8: Run round-trip test to verify it passes

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~TemplateConfigTests.RoundTrip_AllFields_PreservesValues" \
  -v minimal
```

Expected: PASS(原 10 断言 + 新 1 断言全过)。

### Step 9: Commit

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs \
  src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs \
  tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs \
  && git commit -F- <<'EOF'
feat(fooocus): FooocusEntryMode enum + TemplateConfig.fooocus_entry_mode 字段

加 FooocusEntryMode { AutoUpdate=0, Stable=1 } enum,TemplateConfig
字段 [JsonPropertyName("fooocus_entry_mode")] + JsonStringEnumConverter
数字 fallback 零迁移;TemplateConfigDefaults.Fooocus factory 显式赋
默认值 AutoUpdate(默认行为零变化)。9 TemplateConfigDefaults test + 1
TemplateConfig round-trip 断言。

不引入 UI 改动(跟 v1.0.0.x EditTemplateDialog「不显示 entry 字段」决策一致),
用户手编辑 settings.inf 切换。

Spec: docs/superpowers/specs/2026-08-31-fooocus-entry-mode-design.md
EOF
```

Expected: 1 commit created, working tree clean for these 4 files。

---

## Task 2: ProcessLauncher Fooocus stable 分支 + 4 tests + settings.inf round-trip

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` (line 863 后插 5 行 if)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs` (~4 tests, ~110 行)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfRoundTripFooocusTests.cs` (1 test, ~40 行)

**Interfaces:**
- Consumes: `TemplateConfig.FooocusEntryMode` (Task 1 produced) — `FooocusEntryMode.AutoUpdate` / `FooocusEntryMode.Stable`
- Consumes: `Environment.TemplateConfigSnapshot` 解析顺序 — `env.TemplateConfigSnapshot ?? settings.Templates[env.TemplateKind]`(line 843-844)
- Consumes: `InfSettingsSerializer.SerializeToDict` / `ApplyDictToSettings` (从 `ComfyUI.Manager.Services.Inf`)
- Produces: `BuildStartCommand(env, settings, projectRoot)` 在 `snapshot.Kind == "Fooocus" && snapshot.FooocusEntryMode == Stable` 时,`args.File` 落在 `<envRoot>/entry.py`(而非 `snapshot.EntryScript`)

### Step 1: Write the failing `ProcessLauncherFooocusTests` 4 tests

**File:** `D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs`

镜像 `ProcessLauncherTemplateKindTests.cs` 的 IDisposable + `_projectRoot` + `CreateFakeEntryFile` 模式(line 8-34)。**完整代码**:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31):Fooocus entry mode 切换 — BuildStartCommand 在
/// Kind=="Fooocus" 且 FooocusEntryMode==Stable 时改用 entry.py(替 snapshot.EntryScript 的
/// entry_with_update.py),其它 kind 跟其它 mode 完全不受影响。
/// </summary>
public sealed class ProcessLauncherFooocusTests : IDisposable
{
    private readonly string _projectRoot;

    public ProcessLauncherFooocusTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "proc-launch-fooocus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// v1.0.0.x: BuildStartCommand 校验入口脚本存在性(Spec §9),测试 pre-create 假 entry script
    /// 否则新逻辑会先抛 FileNotFound。BuildStartCommand 用 env.RootPath 派生 envRoot。
    /// </summary>
    private void CreateFakeEntryFile(string envName, string entryScript, string? absoluteRootPath = null)
    {
        var envRoot = absoluteRootPath ?? Path.Combine(_projectRoot, "envs", envName);
        Directory.CreateDirectory(envRoot);
        File.WriteAllText(Path.Combine(envRoot, entryScript), "# fake");
    }

    [Fact]
    public void BuildStartCommand_Fooocus_AutoUpdate_UsesEntryWithUpdate()
    {
        // 默认:EntryScript = entry_with_update.py + FooocusEntryMode = AutoUpdate
        // → 走 snapshot.EntryScript,跟 v1.0.0 行为 100% 一致
        var env = new Environment
        {
            Id = "fooocus-au", Name = "FooocusAU", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.AutoUpdate,
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusAU", "entry_with_update.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusAU", "entry_with_update.py"), args.File);
        Assert.DoesNotContain("entry.py", Path.GetFileName(args.File));   // 不是 entry.py
    }

    [Fact]
    public void BuildStartCommand_Fooocus_Stable_UsesEntryPy()
    {
        // Stable 模式:EntryScript 仍是 entry_with_update.py(快照冻结),但 mode override 用 entry.py
        var env = new Environment
        {
            Id = "fooocus-st", Name = "FooocusStable", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",   // 快照仍记 entry_with_update.py
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.Stable,   // 但 mode = Stable → 替 entry.py
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusStable", "entry.py");   // 实际磁盘上只有 entry.py

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusStable", "entry.py"), args.File);
        Assert.DoesNotContain("entry_with_update.py", args.File);
    }

    [Fact]
    public void BuildStartCommand_Fooocus_EntryModeMissing_FallsBackToAutoUpdate()
    {
        // 老 settings 缺 fooocus_entry_mode 字段 → JsonStringEnumConverter 数字 fallback → 0 → AutoUpdate
        // 行为跟 v1.0.0 完全一致
        var env = new Environment
        {
            Id = "fooocus-fb", Name = "FooocusFallback", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.AutoUpdate,   // 默认值
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusFallback", "entry_with_update.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusFallback", "entry_with_update.py"), args.File);
    }

    [Fact]
    public void BuildStartCommand_NonFooocusKind_StableModeSet_Unaffected()
    {
        // 其它 kind 误打 FooocusEntryMode = Stable(用户手抖) → kind check 短路,完全不影响
        var env = new Environment
        {
            Id = "comfy-st", Name = "ComfyStable", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 8000,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
                FooocusEntryMode = FooocusEntryMode.Stable,   // 误打,应该被 kind check 短路掉
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("ComfyStable", "main.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "ComfyStable", "main.py"), args.File);
        Assert.DoesNotContain("entry.py", args.File);   // 不能替成 entry.py
    }
}
```

### Step 2: Run tests to verify they fail

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~ProcessLauncherFooocusTests" \
  -v minimal
```

Expected:
- `BuildStartCommand_Fooocus_AutoUpdate_UsesEntryWithUpdate` → **PASS**(因为 Fooocus EntryScript default = entry_with_update.py,ProcessLauncher 现状就是用它)
- `BuildStartCommand_Fooocus_Stable_UsesEntryPy` → **FAIL**(因为还没加 if 分支,会用 entry_with_update.py)
- `BuildStartCommand_Fooocus_EntryModeMissing_FallsBackToAutoUpdate` → **PASS**(同上)
- `BuildStartCommand_NonFooocusKind_StableModeSet_Unaffected` → **PASS**(kind check 还没加也不会误伤)

预期的「Stable FAIL」就是 task 2 的 fail-fast 信号 — 确认 if 分支不存在。

### Step 3: Add Fooocus stable 分支 到 `ProcessLauncher.BuildStartCommand`

**File:** `D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`

**位置:** 在 line 863 `var entryScript = Path.Combine(envRoot, snapshot.EntryScript);` **之后**,line 864 `// Spec §9: 入口脚本不存在时 throw 清晰指示...` **之前**插入:

```csharp
        // v1.0.0.x (2026-08-31):Fooocus stable 模式 — 用 entry.py 替 entry_with_update.py,
        // 生产可预测不 auto-update。镜像 Forge kind-special 分支(line 904 风格)。
        // snapshot.EntryScript 仍记 entry_with_update.py,但 Stable mode override 替 entry.py。
        // 其它 kind 跟其它 mode 完全不受影响(kind check 短路)。
        if (string.Equals(snapshot.Kind, "Fooocus", StringComparison.Ordinal)
            && snapshot.FooocusEntryMode == FooocusEntryMode.Stable)
        {
            entryScript = Path.Combine(envRoot, "entry.py");
        }
```

(`ProcessLauncher.cs` 已 `using ComfyUI.Manager.Models;` — `FooocusEntryMode` 直接可用,无需新 using。)

### Step 4: Run 4 tests to verify they pass

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~ProcessLauncherFooocusTests" \
  -v minimal
```

Expected: 4/4 PASS。

### Step 5: Write the failing `SettingsInfRoundTripFooocusTests` 1 test

**File:** `D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfRoundTripFooocusTests.cs`

镜像 `ComfySettingsWriterTests.cs:16-49` 的 IDisposable + temp dir + round-trip 模式。

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Inf;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-08-31):settings.inf → Settings → Templates["Fooocus"].FooocusEntryMode
/// round-trip 测试 — 验证手编辑 settings.inf 把 fooocus_entry_mode = Stable 写入后,
/// 重新读回 Settings 时正确反序列化为 FooocusEntryMode.Stable(InfSettingsSerializer 反射
/// + JsonStringEnumConverter 路径)。
/// </summary>
public sealed class SettingsInfRoundTripFooocusTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsInfRoundTripFooocusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "settings-inf-fooocus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SettingsInf_FooocusEntryMode_Stable_RoundTripPreservesValue()
    {
        // 写一个 settings 含 Fooocus 模板 + fooocus_entry_mode = Stable 到 INF
        var settings = new Settings();
        settings.Templates["Fooocus"] = new TemplateConfig
        {
            Name = "Fooocus",
            Kind = "Fooocus",
            LocalSourceDir = "Fooocus",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/lllyasviel/Fooocus.git",
            EntryScript = "entry_with_update.py",
            EntryArgs = "--port {port} --listen",
            ModelsSubdir = "models",
            ExtraJunctionTargets = new System.Collections.Generic.List<string>(),
            UserExtraArgs = "",
            FooocusEntryMode = FooocusEntryMode.Stable,
        };
        var dict = InfSettingsSerializer.SerializeToDict(settings);

        // 反向应用 dict 到新 Settings 实例
        var restored = new Settings();
        InfSettingsSerializer.ApplyDictToSettings(restored, dict);

        // 关键断言:settings.Templates["Fooocus"].FooocusEntryMode = Stable(用户手编辑生效)
        Assert.True(restored.Templates.ContainsKey("Fooocus"));
        Assert.Equal(FooocusEntryMode.Stable, restored.Templates["Fooocus"].FooocusEntryMode);
    }
}
```

### Step 6: Run test to verify it passes

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~SettingsInfRoundTripFooocusTests" \
  -v minimal
```

Expected: 1/1 PASS。

### Step 7: Commit

```bash
cd "D:/ToolDevelop/ComfyUI" && git add \
  src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs \
  tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs \
  tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfRoundTripFooocusTests.cs \
  && git commit -F- <<'EOF'
feat(fooocus): ProcessLauncher Fooocus stable 分支 + 5 tests

ProcessLauncher.BuildStartCommand line 863 后加 5 行 if:
  Kind=="Fooocus" && FooocusEntryMode==Stable → entry.py
(替 snapshot.EntryScript 的 entry_with_update.py)。

snapshot.EntryScript 仍记 entry_with_update.py(快照冻结);
其它 kind 跟其它 mode 完全不受影响(kind check 短路)。

新增 4 个 ProcessLauncher Fooocus mode test + 1 个 settings.inf
round-trip test(InfSettingsSerializer + JsonStringEnumConverter 路径)。

Spec: docs/superpowers/specs/2026-08-31-fooocus-entry-mode-design.md
EOF
```

Expected: 1 commit created, working tree clean for these 3 files。

---

## Task 3: 验证 + push to fogyisland

**Files:** 无源码改动 — 仅运行 full suite + push。

**Interfaces:** 沿用 Task 1 + Task 2 产出的所有 test + field + factory + ProcessLauncher 分支。

### Step 1: 跑聚焦的 Fooocus 15 个新 test 全过

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~TemplateConfigDefaultsFooocusTests|FullyQualifiedName~ProcessLauncherFooocusTests|FullyQualifiedName~SettingsInfRoundTripFooocusTests|FullyQualifiedName~TemplateConfigTests.RoundTrip_AllFields_PreservesValues" \
  -v minimal
```

Expected: 14 + 1(原本 RoundTrip test 已存在的其它断言)= **15 PASS / 0 FAIL**。

### Step 2: Build 整个 solution 0 error / 0 warning

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: Build succeeded. **0 Error(s)**。Warning 数应跟 main 分支一致(±2 容差 — 其它人修改可能引入新 warning,本任务不动其它代码)。

### Step 3: 跑 full test suite(skip 已知 flaky RealGit 测试)

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName!~RealGit" \
  -v minimal 2>&1 | tail -50
```

Expected: **≥ 2506 PASS / 1 known flaky FAIL** (`BaseEnvStatusViewModelTests.LogLines_CappedAtMaxLogLines`,pre-existing 已知不修) / 8 SKIP (RealGit + 网络 endpoint)。

如果出现 ≥ 2 FAIL 或 0 FAIL(那 1 known flaky 也消失)→ STOP,排查新引入的 regression。

### Step 4: 检查 git log 跟 working tree

Run:
```bash
cd "D:/ToolDevelop/ComfyUI" && git log --oneline -3 && git status --short
```

Expected:
- 2 commits on top of `b592b95`(Task 1 + Task 2 commit message 跟 spec 一致)
- working tree clean(无 uncommitted 改动)

### Step 5: Push to fogyisland fork(不发布 release)

```bash
cd "D:/ToolDevelop/ComfyUI" && git push fork main
```

Expected: `git push fork main` fast-forwards `da291716..<HEAD> main -> main`(注:fogyisland fork 当前 HEAD 是 `b592b95`;新增 2 commit 在它之上)。

Per `feedback_no_publish_without_asking.md`:不创建 tag,不创建 GitHub release。

### Step 6: 报完成

报告给用户:
- branch:`main` @ `<new HEAD hash>`
- 2 commits added(Task 1 + Task 2)
- 15 tests added(9 + 4 + 1 + 1)
- full suite ≥ 2506 PASS / 1 known flaky FAIL / 8 SKIP
- pushed to fogyisland fork(无 tag 无 release)
- 等用户决定是否打 v1.0.0.x release tag

---

## Self-Review

写完 plan 自我检查:

**1. Spec coverage:**
| Spec Component | Task 覆盖 |
|---|---|
| Component 1 — Enum + TemplateConfig 字段 | Task 1 Step 3-4 |
| Component 2 — TemplateConfigDefaults.Fooocus factory default | Task 1 Step 5 |
| Component 3 — ProcessLauncher Fooocus stable 分支 | Task 2 Step 3 |
| Component 4 — TDD ~13 tests | Task 1 Step 1 (9) + Step 7 (1 round-trip) + Task 2 Step 1 (4) + Step 5 (1 settings.inf) = **15 tests** |
| Component 5 — Out of scope | 不开 task(spec 已 lock) |
| Migration / Backward compat | Task 1 Step 3-4 注释明示;Task 2 Step 1 test `EntryModeMissing_FallsBackToAutoUpdate` 锁死 |
| Risks | spec 4 项,本 plan 通过 Step 1 tests 覆盖 3 项(risk 1 反序列化 catch 在 `InfSettingsSerializer.cs:71-74` 已存在,不改) |

✅ 全部 spec requirements 都有对应 task / step。

**2. Placeholder scan:**
- ✅ 无 "TBD" / "TODO" / "fill in"
- ✅ 无 "implement appropriate logic" 类空泛措辞
- ✅ 所有 step 都有具体代码块 / 文件路径 / 行号 / 命令
- ✅ 无 "Similar to Task X" — 每个 test 都给了完整 code
- ✅ 无 reference 未定义的 type / method

**3. Type consistency:**
- ✅ `FooocusEntryMode` 在 Task 1 Step 3 定义 enum → Step 4 用 enum → Step 5 用 enum → Task 2 Step 1 用 enum(4 个 test)
- ✅ `TemplateConfig.FooocusEntryMode` 在 Task 1 Step 4 定义 property → Task 1 Step 7 assertion 用 → Task 2 Step 1 用 → Task 2 Step 3 read
- ✅ `BuildStartCommand` 返回类型 `(string exe, (string File, string ArgsString) args)` 在 Task 2 Step 1 所有 test 引用一致
- ✅ `InfSettingsSerializer.SerializeToDict` / `ApplyDictToSettings` 在 Task 2 Step 5 用法跟 line 26-47 / 50-76 定义一致
- ✅ `Environment.TemplateConfigSnapshot` 在 Task 2 Step 1 4 个 test 全部 mirror line 843-844 解析顺序(只设 snapshot,不 fallback)

---

## Execution Handoff

Plan complete and saved to `D:/ToolDevelop/ComfyUI/docs/superpowers/plans/2026-08-31-fooocus-entry-mode.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — 派 fresh implementer per task,reviewer per task,fast iteration
2. **Inline Execution** — 自己在本 session 跑

Which approach?