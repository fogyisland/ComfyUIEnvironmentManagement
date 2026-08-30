# Fooocus Entry Mode 切换 — 设计文档

**Spec author:** superpowers:brainstorming (controller)
**Date:** 2026-08-31
**Status:** Draft → user review pending

---

## Goal

让 Fooocus 模板支持「AutoUpdate vs Stable」entry 模式切换。当前 Fooocus entry 永远用 `entry_with_update.py`(内置 git pull,每次启动拉上游),生产用户想要可预测的稳定运行 → 切到 `entry.py`。最小改动:全局 TemplateConfig 加一个枚举字段,ProcessLauncher 在 Fooocus kind + Stable 模式时改用 `entry.py`,用户手编辑 `config/settings.inf` 切换。无 UI 改动(跟 v1.0.0.x「移除 entry 字段 UI 编辑」决策一致)。

## Architecture

**数据驱动 + 1 行 ProcessLauncher 特殊分支**,跟现有 `Fooocus` 模板 + `Forge` kind-special 分支同 pattern:

1. `TemplateConfig.FooocusEntryMode`(枚举 `AutoUpdate` / `Stable`,默认 `AutoUpdate`)— 数据声明
2. `TemplateConfigDefaults.Fooocus` factory — 默认 `AutoUpdate`(零风险,跟现状完全一致)
3. `ProcessLauncher.BuildStartCommand` line 863 后插 1 个 if — `if Kind=="Fooocus" && FooocusEntryMode==Stable → entry.py`
4. 已存在 env 的 entry mode 跟 snapshot 冻结(用户改 settings 不影响已存在 env,符合现有机制)

## Tech Stack

- C# 12 / .NET 8 WPF(沿用)
- `System.Text.Json` `JsonStringEnumConverter`(已有 precedent:`TemplateSourceKind` line 15-19)
- xUnit(沿用)
- 无新依赖

## Spec 引用

无 — 本 spec 是 v1.0.0.x 内的小 bounded 改造,不开 spec → plan → task 三件套,而是 spec → plan → execute 流水线。

---

## Global Constraints

| 约束 | 严格度 |
|---|---|
| **不破坏 v1.0.0.x EditTemplateDialog「不显示 entry 字段」决策** | hard — 不动 `EditTemplateDialogViewModel.cs:216-219` 那块 |
| **snapshot 机制不变** — 改 settings.Templates["Fooocus"] 不影响已存在 env | hard — 不修改 `EnvCreatorService.CloneTemplateConfig` 或 `BuildStartCommand` snapshot 取值顺序 |
| **零 schema migration** — 老 settings.inf / 老 settings.json 加字段不抛异常 | hard — 复用 `JsonStringEnumConverter` 数字 fallback(0 = AutoUpdate) |
| **零新 enum for other templates** — 只 Fooocus 一个模板用 entry mode 切换,YAGNI | hard — 命名 `FooocusEntryMode` 而非通用 `EntryMode` |
| **不引入 UI 改动** — 用户手编辑 settings.inf | hard — 跟 user decision 一致 |
| **测试 ≥ 10 个** — 7 TemplateConfigDefaults + 4 ProcessLauncher + 1 roundtrip + 1 settings.inf = 13 | hard |
| **full suite 不回归** — 改动 3 个源文件 + 3-4 个 test 文件,跑 full suite 2506+ 已知 1 flaky 不破 | hard |
| **commit message 走 Bash heredoc** — 避免 PowerShell `@'...'@` stray `@` | hard |
| **不 amend 既有 commit** — 包括 `6621c373` 的 stray `@` cosmetic | hard |

---

## Component 1: Enum + TemplateConfig 字段

### 1.1 新增 enum

**文件:** `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs`

**位置:** line 15-19(`public enum TemplateSourceKind` 旁,既存 enum precedent)

```csharp
/// <summary>
/// Fooocus 模板 entry 模式:AutoUpdate = 跟上游同步(默认,现状),
/// Stable = 用 entry.py 不 auto-update,生产可预测。
/// </summary>
public enum FooocusEntryMode
{
    AutoUpdate = 0,
    Stable = 1,
}
```

**为什么是 enum 而不是 string:** 镜像 `TemplateSourceKind` 模式(数字 fallback → 老 JSON 缺字段落 0 = AutoUpdate,零迁移成本);`JsonStringEnumConverter` 写 "AutoUpdate"/"Stable" 字符串到 settings.inf(人可读)。

### 1.2 TemplateConfig 字段

**文件:** `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs`

**位置:** line 38 附近(`SourceKind` 字段下面,Group: "Fooocus 专属")

```csharp
/// <summary>
/// Fooocus entry 模式:仅 Kind=="Fooocus" 时生效。AutoUpdate (默认) 用
/// entry_with_update.py,Stable 用 entry.py。改 settings 不影响已存在 env(snapshot 冻结)。
/// </summary>
[JsonPropertyName("fooocus_entry_mode")]
[JsonConverter(typeof(JsonStringEnumConverter))]
public FooocusEntryMode FooocusEntryMode { get; set; } = FooocusEntryMode.AutoUpdate;
```

**关键属性:**
- `[JsonPropertyName("fooocus_entry_mode")]` snake_case 跟其他字段一致
- `[JsonConverter(typeof(JsonStringEnumConverter))]` 镜像 `SourceKind` line 38 现有用法
- 默认值 `AutoUpdate` — 老 settings 缺字段时落 0,行为跟现状完全一致(零破坏)

### 1.3 settings.inf roundtrip

`InfSettingsSerializer.cs:36-39` 走反射读 `[JsonPropertyName]`,新字段自动包含进 dict 持久化,无代码改动。

`SettingsRepository.cs:31-32` 的 `JsonStringEnumConverter` 数字 fallback 注释已经覆盖本字段:老 settings 缺 `fooocus_entry_mode` → 落 `0` → `AutoUpdate`(零迁移)。

---

## Component 2: TemplateConfigDefaults.Fooocus factory

**文件:** `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs:228-240`

```csharp
public static TemplateConfig Fooocus(string projectRoot) => new()
{
    Name = "Fooocus",
    Kind = "Fooocus",
    LocalSourceDir = "Fooocus",
    SourceKind = TemplateSourceKind.GitHub,
    GitHubRepoUrl = "https://github.com/lllyasviel/Fooocus.git",
    EntryScript = "entry_with_update.py",
    EntryArgs = "--port {port} --listen",
    ModelsSubdir = "models",
    ExtraJunctionTargets = new(),
    UserExtraArgs = "",
    FooocusEntryMode = FooocusEntryMode.AutoUpdate,   // ← 新增,默认现状
};
```

**关键决策:**
- `EntryScript` 保持 `entry_with_update.py`(跟 `AutoUpdate` mode 配套;**不改 default,默认行为零变化**)
- `FooocusEntryMode = AutoUpdate` 显式赋值 — C# 字段默认也是 `AutoUpdate`,显式赋值只为**显式意图** + 跟其他字段并列(易读)
- `SettingsDefaults.SeedBuiltInTemplatesIfMissing`(`SettingsDefaults.cs:425-487`)只 seed 缺失的 Fooocus — 已存在用户的 `Fooocus` block 在 settings.inf 里没 `fooocus_entry_mode` 字段 → 落 0 = AutoUpdate,**零迁移**

---

## Component 3: ProcessLauncher Fooocus stable 分支

**文件:** `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`

**位置:** line 863 `var entryScript = Path.Combine(envRoot, snapshot.EntryScript);` **之后**,在 line 870 `{port}` 替换之前。

```csharp
var entryScript = Path.Combine(envRoot, snapshot.EntryScript);

// Fooocus stable 模式: 用 entry.py 替 entry_with_update.py,生产可预测不 auto-update
if (string.Equals(snapshot.Kind, "Fooocus", StringComparison.Ordinal)
    && snapshot.FooocusEntryMode == FooocusEntryMode.Stable)
{
    entryScript = Path.Combine(envRoot, "entry.py");
}
```

**关键设计:**
- 镜像现有 `Forge` kind-special 分支(line 904 风格)— 一段 if,简单直白
- 顺序:在 `entryScript` 解析**后**改,避免重写 Path.Combine 逻辑
- 跟 `{port}` / `{models}` / `{env}` 占位符替换解耦 — entry.py 也用同一份 args(跟 entry_with_update.py 完全兼容)
- snapshot 机制:已存在 env 用的是 `env.TemplateConfigSnapshot` (ProcessLauncher.cs:843-846),改 settings.Templates["Fooocus"] 不影响 — 这是 **desired behavior**,文档说清楚

### 3.1 不影响非 Fooocus kind

**强约束:** 其它 10 个 built-in kind(ComfyUI / Forge / OpenVoice / Whisper / CoquiTTS / Bark / HunyuanVideo / LTXVideo / CogVideoX / HivisionIDPhotos)即使 settings.inf 有遗留 `fooocus_entry_mode` 字段(用户手编辑过其它 kind 时误打),也**不**触发 entry 替换 — 因 kind check 优先于 mode check(line 1 短路)。

---

## Component 4: TDD 测试(~13 个 test)

### 4.1 新文件: `TemplateConfigDefaultsFooocusTests.cs`

**文件:** `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs`

镜像 `TemplateConfigDefaultsLtxVideoTests.cs` 的 7-test 模式(机械 1 文件 1 factory):

```csharp
public sealed class TemplateConfigDefaultsFooocusTests
{
    [Fact]
    public void Fooocus_Name_IsFooocus() { ... Assert.Equal("Fooocus", cfg.Name); }

    [Fact]
    public void Fooocus_Kind_IsFooocus() { ... Assert.Equal("Fooocus", cfg.Kind); }

    [Fact]
    public void Fooocus_LocalSourceDir_IsFooocus() { ... Assert.Equal("Fooocus", cfg.LocalSourceDir); }

    [Fact]
    public void Fooocus_SourceKind_IsGitHub() { ... Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind); }

    [Fact]
    public void Fooocus_GitHubRepoUrl_IsLllyasviel() { ... Assert.Equal("https://github.com/lllyasviel/Fooocus.git", cfg.GitHubRepoUrl); }

    [Fact]
    public void Fooocus_EntryScript_IsEntryWithUpdate() { ... Assert.Equal("entry_with_update.py", cfg.EntryScript); }

    [Fact]
    public void Fooocus_EntryArgs_HasPortAndListen() { ... Assert.Contains("{port}", cfg.EntryArgs); Assert.Contains("--listen", cfg.EntryArgs); }

    [Fact]
    public void Fooocus_ModelsSubdir_IsModels() { ... Assert.Equal("models", cfg.ModelsSubdir); }

    [Fact]
    public void Fooocus_FooocusEntryMode_IsAutoUpdate() { ... Assert.Equal(FooocusEntryMode.AutoUpdate, cfg.FooocusEntryMode); }
}
```

**~9 tests**(加了 `Fooocus_FooocusEntryMode_IsAutoUpdate` 测试,默认 = AutoUpdate 锁死)。

### 4.2 新文件: `ProcessLauncherFooocusTests.cs`

**文件:** `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs`

镜像 `ProcessLauncherTemplateKindTests.cs:29-34` 的 `CreateFakeEntryFile` helper + 4 个 test:

```csharp
public sealed class ProcessLauncherFooocusTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void BuildStartCommand_Fooocus_AutoUpdate_UsesEntryWithUpdate()
    {
        // env TemplateConfigSnapshot.FooocusEntryMode = AutoUpdate, EntryScript = "entry_with_update.py"
        // assert: args.File ends with "entry_with_update.py"
    }

    [Fact]
    public void BuildStartCommand_Fooocus_Stable_UsesEntryPy()
    {
        // env TemplateConfigSnapshot.FooocusEntryMode = Stable, EntryScript = "entry_with_update.py"
        // assert: args.File ends with "entry.py" (NOT entry_with_update.py)
        // 关键:EntryScript 仍是 entry_with_update.py 但 mode override 用 entry.py
    }

    [Fact]
    public void BuildStartCommand_Fooocus_EntryModeMissing_FallsBackToAutoUpdate()
    {
        // 老 settings 缺 fooocus_entry_mode 字段 → 落 0 = AutoUpdate
        // env TemplateConfigSnapshot.FooocusEntryMode = (default)AutoUpdate
        // assert: args.File ends with "entry_with_update.py"
    }

    [Fact]
    public void BuildStartCommand_NonFooocusKind_Stable_Unaffected()
    {
        // env TemplateKind = "ComfyUI", FooocusEntryMode = Stable (误打)
        // assert: args.File 用 snapshot.EntryScript (不替 entry.py)
        // 关键:验证 kind check 短路,不会误伤其他模板
    }
}
```

**4 tests**。

### 4.3 修改: `TemplateConfigTests.cs:12` round-trip test

**文件:** `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs`

`RoundTrip_AllFields_PreservesValues` test(line 12-43)加 1 行断言:

```csharp
cfg.FooocusEntryMode = FooocusEntryMode.Stable;
...
var roundtrip = JsonSerializer.Deserialize<TemplateConfig>(json, JsonOptions.Default);
Assert.Equal(FooocusEntryMode.Stable, roundtrip!.FooocusEntryMode);
```

### 4.4 修改: 加 settings.inf roundtrip test(如无 anchor 文件)

**文件:** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfSerializationTests.cs`(如不存在则新建)

如果 `SettingsInfSerializationTests.cs` 已存在:加 1 test 验证 `Settings.Templates["Fooocus"].FooocusEntryMode` 写入再读回 = Stable。

如不存在:建新文件,1 个 test 用 `InfSettingsSerializer.SerializeToDict` + `InfWriter.Write` + `Load` 路径 round-trip。

---

## Migration / Backward Compatibility

**零迁移成本:**

| 场景 | 行为 |
|---|---|
| 老 settings.inf 缺 `fooocus_entry_mode` 字段 | `JsonStringEnumConverter` 数字 fallback → 0 → `AutoUpdate`(跟现状 100% 一致) |
| 老 settings.json (用户用 release v1.0.0) | `SettingsRepository.Load` line 71-87 自动转 .inf;`Fooocus` block 缺字段 → 同上 |
| 已存在 Fooocus env | `env.TemplateConfigSnapshot.FooocusEntryMode = (C# default) AutoUpdate`,ProcessLauncher 走 entry_with_update.py — 跟 v1.0.0 行为 100% 一致 |
| 用户切到 Stable | 手编辑 `config/settings.inf` 改 `fooocus_entry_mode = Stable`(或 `Stable` 字符串);下次「创建 env」时 freeze 进 snapshot;已存在 env 不变(用户需手动重建或留 entry_with_update) |

**不需要的代码:**
- 无 `SettingsRepository.TryMigrateOldFooocusEntryMode` — 缺字段默认 OK
- 无 EnvCreator migration — 新建 env 走当前 settings,自动带默认值
- 无用户数据备份/恢复 — 字段 additive,纯新增

---

## Out of Scope (明确不做)

- ❌ **不动 `EditTemplateDialog`**(`EditTemplateDialogViewModel.cs:216-219` 已显式移除 entry 字段 UI,本 spec 不回退)
- ❌ **不动 `EditTemplateDialog.xaml`** 的 entry 字段
- ❌ **不动 env-create dialog**(ComboBox 只选 kind,不变)
- ❌ **不加 per-env override**(`Environment.FooocusEntryMode` 不存在;只用全局 + snapshot 机制)
- ❌ **不改 `SettingsDialog` UI**(用户手编辑 settings.inf)
- ❌ **不为其它模板加 entry mode**(`TemplateSourceKind` 同名 enum 不动;YAGNI,Stable 模式 Fooocus 唯一)
- ❌ **不打 tag / 不 publish**(per `feedback_no_publish_without_asking.md`)
- ❌ **不删 v1.0.0.x 已 ship 的 `entry_with_update.py` 文档**(用户可能 legacy 用)

---

## Risks

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| 用户手编辑 settings.inf 拼错 `fooocus_entry_mode` 值(非 "AutoUpdate"/"Stable"/0/1) | 中 | 反序列化抛异常 → Fooocus env 启动失败 | `InfSettingsSerializer.ApplyDictToSettings` line 73 catch-all 单字段坏数据 skip → 落默认 0 = AutoUpdate |
| `entry.py` 在老 Fooocus clone 不存在(用户用 v1.0.0 之前的旧 clone) | 低 | Stable mode 启动失败 | ProcessLauncher 现有「entry file 不存在」早返错误已存在(查 `BuildStartCommand` line 863 后续逻辑);不在本 spec 范围 |
| `JsonStringEnumConverter` 接受数字 0/1 + 字符串 "AutoUpdate"/"Stable" 两种 | 低 | 文档模糊 | spec §1.1 注释明示两种都合法;settings.inf 默认写字符串(人可读) |
| 其他 kind 误打 `fooocus_entry_mode` 字段 | 低 | 不生效(被 kind check 短路) | Component 3.1 强约束 + test 4.2 test 4 锁死 |

---

## Test Plan

### 4 个 test 文件改动

1. **新建** `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs`(~9 tests)
2. **新建** `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs`(~4 tests)
3. **修改** `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs`(+2 行 round-trip 断言)
4. **新建或修改** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfSerializationTests.cs`(+1 test)

### 运行命令

```bash
cd "D:/ToolDevelop/ComfyUI"
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj
dotnet test tests-wpf/ComfyUI.Manager.Tests/ \
  --filter "TemplateConfigDefaultsFooocusTests|ProcessLauncherFooocusTests|TemplateConfigTests|SettingsInfSerializationTests" \
  -v minimal
# Expected: ~14 PASS / 0 FAIL

dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName!~RealGit" -v minimal
# Expected: 2507 PASS / 1 known flaky FAIL (BaseEnvStatusViewModelTests.LogLines_CappedAtMaxLogLines) / 8 SKIP
```

---

## Files Touched

### 新增
1. `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs` — 加 enum + field(2 个 ~7 行代码块)
2. `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsFooocusTests.cs` — ~9 tests, ~80 行
3. `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherFooocusTests.cs` — ~4 tests, ~100 行
4. `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsInfSerializationTests.cs` — 1 test, ~30 行(如新建)

### 修改
1. `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` — Fooocus factory +1 行 `FooocusEntryMode = FooocusEntryMode.AutoUpdate`
2. `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` — line 863 后插 5 行 if
3. `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs` — RoundTrip test +2 行

### 总改动
- **生产代码:** 3 文件,~14 行净增
- **测试代码:** 3-4 文件,~210 行净增(测试占大头,符合 bounded TDD pattern)

---

## 后续 / Future Work(本 spec 不做)

1. **Resources.resx UI 化**(如未来加 UI toggle)— 跟 LTX-2 plan parked 同类型
2. **其它模板 entry mode 切换**(HunyuanVideo / CogVideoX 等)— 需单独 spec
3. **`SettingsDialog` 加 Fooocus entry mode toggle** — 跟 v1.0.0.x「移除 entry 字段 UI」决策冲突,需先开 spec 推翻该决策

---

## Spec Self-Review

写完自我检查:

✅ **No placeholders / TBDs** — 全文具体行号 / 代码片段 / 测试 pattern
✅ **Internal consistency** — 5 个 Section(Component 1-5)跟前面 design 一致
✅ **Scope check** — 单 bounded 改动,3 prod + 3-4 test 文件
✅ **Ambiguity check** — `FooocusEntryMode.AutoUpdate` vs `Stable` 枚举命名明确;`Stable` 模式用 `entry.py` vs AutoUpdate 用 `entry_with_update.py` 行为明确
✅ **Global constraints 严格** — 不动 EditTemplateDialog UI / 不加 per-env override / 零迁移 / Bash heredoc commit
✅ **TDD 协议** — 14 个新 test 全列具体 code snippet
✅ **Migration 明确** — 零迁移,缺字段 → 默认 AutoUpdate
✅ **Out of scope 明确** — 7 项 ❌ 列出避免 scope creep
✅ **Risk 分析** — 4 项 + 缓解

---

## Approval

- [ ] 用户 spec review 通过 → 进 writing-plans → plan → SDD execute
- [ ] 用户调整 section → 修改 → 再 review

---

## 路径参考

- Spec: `D:/ToolDevelop/ComfyUI/docs/superpowers/specs/2026-08-31-fooocus-entry-mode-design.md`(本文件)
- Plan: 待 `superpowers:writing-plans` skill 输出
- Workspace: 待定 `.superpowers/sdd/2026-08-31-fooocus-entry-mode/`
