# v1.0.0 Multi-Template Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single hardcoded `Settings.TemplateComfyuiDir` with a flexible `Settings.Templates : Dictionary<string, TemplateConfig>` pool. Ship ComfyUI + A1111 as built-in kinds, add Custom kind via new "模板管理" sidebar page. Env creation always copies (no junction) and freezes a `TemplateConfig` snapshot per env for reproducibility.

**Architecture:**
- **Data model:** `TemplateConfig` (Kind + LocalSourceDir + EntryScript + EntryArgs + ModelsSubdir + ExtraJunctionTargets + UserExtraArgs). `Settings.Templates` replaces `Settings.TemplateComfyuiDir`. `Environment` gains `TemplateKind` + `TemplateConfigSnapshot` (frozen at env creation).
- **UI:** New 9th sidebar RadioButton "模板管理" → `TemplateManagementView` (card list, built-in protected, add/edit/delete). `EditTemplateDialog` for adding/editing individual templates. `CreateEnvDialog` drops "shared/independent" Layout radio, adds TemplateKind RadioButton picker that auto-fills `ComfyuiSource` from selected template.
- **Service layer:** `EnvCreatorService` always copies `LocalSourceDir → <envsDir>/<envName>/` (junction option removed). `ProcessLauncher` switches on `env.TemplateKind` to build entry command (ComfyUI: `main.py --port {port} --listen 0.0.0.0`; A1111: `webui.py --port {port}` + user extras; Custom: snapshot-driven). `ModelSymlinker` reads `env.TemplateConfigSnapshot.ModelsSubdir` (ComfyUI: "models"; A1111: "models/Stable-diffusion"). `ComfyUITemplateUpdater` generalized to `TemplateSourceUpdater` (accepts target dir + repo URL). A1111/Custom envs skip `ComfyUIManagerInstaller` + `CommonNodeInstaller` (env.TemplateKind check).

**Tech Stack:** .NET 8 / WPF / C# 12 / xUnit / SQLite / `HttpClient` singleton / `AppLogger`. No new third-party dependencies. Reuses existing patterns (`IProgress<string>`, `RelayCommand`, `BoolToVisibility` converter, AppLogger subsystems).

**Spec:** `docs/superpowers/specs/2026-08-22-multi-template-design.md` (HEAD `9a3a5da`)

**Base branch:** main at `9a3a5da` (post v1.0.0 Phase 1 dev mode unblock + spec).

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | `Settings.Templates` is `Dictionary<string, TemplateConfig>` keyed by `Kind` string ("ComfyUI" / "A1111" / "MySwarmUI"). NO enum — string keys per user decision. | spec §1, §3 |
| **G2** | `Environment.TemplateKind` + `Environment.TemplateConfigSnapshot` — snapshot frozen at env creation. User updates to template defaults do NOT affect existing envs. | spec §3, §6, §7 |
| **G3** | `EnvCreatorService` ALWAYS copies `LocalSourceDir → <envsDir>/<envName>/`. No junction. The current "shared" Layout option is REMOVED. | spec §2, §6 |
| **G4** | Built-in kinds (ComfyUI, A1111) seeded by `SettingsDefaults.Apply` only when their entry is missing. Never overwrite user's customized `LocalSourceDir` / `EntryArgs` / `UserExtraArgs`. | spec §3, §2 |
| **G5** | Default ComfyUI `EntryScript="main.py"`, `EntryArgs="--port {port} --listen 0.0.0.0"`, `ModelsSubdir="models"`. Default A1111: `EntryScript="webui.py"`, `EntryArgs="--port {port}"`, `ModelsSubdir="models/Stable-diffusion"`. | spec §3 |
| **G6** | Migration: old `Settings.TemplateComfyuiDir` (if present) → `Settings.Templates["ComfyUI"].LocalSourceDir`; old field dropped. Old `Environment` rows (no `template_kind` column) get backfilled `TemplateKind="ComfyUI"` + snapshot from current settings on first load. | spec §11 |
| **G7** | `ProcessLauncher` entry command: `<venvPython> <EntryScript> <EntryArgs-with-{port}-substituted> [UserExtraArgs]`, cwd = `<envRoot>`. venv python = `<envRoot>/venv/Scripts/python.exe` (Windows) or `bin/python` (Unix). | spec §7 |
| **G8** | `ModelSymlinker` reads `env.TemplateConfigSnapshot.ModelsSubdir` (fallback "models" if snapshot missing). For A1111 default this becomes "models/Stable-diffusion". | spec §8 |
| **G9** | ComfyUIManagerInstaller + CommonNodeInstaller SKIP for non-ComfyUI kinds (env.TemplateKind != "ComfyUI"). | spec §2 |
| **G10** | Tool menu "模板更新" entry REMOVED. Per-template "更新源码" button in `TemplateManagementView` (uses generalized `TemplateSourceUpdater`). | spec §2, §11 |
| **G11** | New sidebar RadioButton "模板管理" is 9th entry parallel to "工作流市场" (8th) and "模型市场". Backed by `TemplateManagementView` + `TemplateManagementViewModel` in `MainViewModel`. | spec §2, §5 |
| **G12** | `EditTemplateDialog` is modal `Window`, single form (Name + Kind ComboBox + LocalSourceDir textbox + Browse + EntryScript + EntryArgs multiline + ModelsSubdir + UserExtraArgs multiline + ExtraJunctionTargets list). Built-in kinds (ComfyUI, A1111) have Kind=ReadOnly when editing existing; only Name + LocalSourceDir + EntryScript + EntryArgs + UserExtraArgs mutable. Custom kind is fully editable. | spec §5 |
| **G13** | Built-in templates (ComfyUI, A1111) **cannot be deleted** from `TemplateManagementView` (Delete button disabled or absent). They can be edited (Name + LocalSourceDir + EntryScript + EntryArgs + UserExtraArgs). Custom templates fully managed. | spec §2, §5 |
| **G14** | `IProgress<string>` pattern for long ops (`TemplateSourceUpdater.UpdateAsync` progress report). Logs to AppLogger subsystem `template-source-update`. | project convention (v0.6.18.4) |
| **G15** | All transient test files use `Path.Combine(Path.GetTempPath(), "ComfyUIMgr<Name>_" + Guid.NewGuid().ToString("N"))` + cleanup in `Dispose`. | project convention |
| **G16** | UI strings: no emoji per v0.6.17.1 WPF font fallback lesson. Icons use `<Path>` SVG. | v0.6.17.1 lesson |
| **G17** | Test count target: 1675 baseline + ~30 new tests (template model 4 + settings seed 4 + env 3 + EnvCreator 5 + ProcessLauncher 4 + Symlinker 3 + CreateEnv 3 + TemplateManagement 4 + EditTemplate 3 + misc 1) = ~1705 PASS / 2-4 FAIL pre-existing flaky / 6 SKIP. | test plan |
| **G18** | YAGNI: no SwarmUI/Forge/SD.Next as built-in kinds, no git URL auto-clone, no per-env UserExtraArgs override, no template marketplace, no per-env template switch. | spec §2 |

---

## Files to Touch

### New files

| Path | Purpose | Task |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs` | TemplateConfig class with JSON serialization | T1 |
| `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` | Static built-in ComfyUI + A1111 default configs (immutable singletons) | T2 |
| `src-wpf/ComfyUI.Manager/Services/TemplateSourceUpdater.cs` | Generalized git clone --depth=1 update (replaces ComfyUITemplateUpdater) | T11 |
| `src-wpf/ComfyUI.Manager/ViewModels/TemplateManagementViewModel.cs` | Sidebar page VM (list + add/edit/delete) | T8 |
| `src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs` | Add/edit dialog VM (validation + apply) | T10 |
| `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml` + `.cs` | Sidebar page UI (card list) | T9 |
| `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml` + `.cs` | Add/edit dialog UI (form) | T10 |
| `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs` | 4 tests (JSON round-trip, defaults, kind validation) | T1 |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTemplateSeedTests.cs` | 4 tests (seed ComfyUI+A1111, migrate old field, no-overwrite) | T2 |
| `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTemplateKindTests.cs` | 3 tests (old row backfill, snapshot persistence) | T3 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceMultiTemplateTests.cs` | 5 tests (ComfyUI+A1111+Custom env creation, snapshot intact) | T4 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherTemplateKindTests.cs` | 4 tests (ComfyUI/A1111/Custom launch args, UserExtraArgs) | T5 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTemplateKindTests.cs` | 3 tests (ComfyUI default, A1111 Stable-diffusion subdir) | T6 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogTemplateKindTests.cs` | 3 tests (picker populates source, validation) | T7 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/TemplateManagementViewModelTests.cs` | 4 tests (list/add/edit/delete, built-in protected) | T8 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EditTemplateDialogViewModelTests.cs` | 3 tests (validation, apply) | T10 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateSourceUpdaterTests.cs` | 2 tests (generalization: target dir + repo URL params) | T11 |

### Modified files

| Path | Change | Task |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | Remove `TemplateComfyuiDir`; add `Templates : Dictionary<string, TemplateConfig>` | T2 |
| `src-wpf/ComfyUI.Manager/Models/Environment.cs` | Add `TemplateKind : string` + `TemplateConfigSnapshot : TemplateConfig?` | T3 |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | Seed ComfyUI+A1111 templates; migrate old `TemplateComfyuiDir` field; add `TryMigrateOldTemplateComfyuiDir` helper | T2 |
| `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` | Backfill `TemplateKind="ComfyUI"` for old rows on LoadAll; persist new fields | T3 |
| `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | Remove Layout branching; take `TemplateKind` param; always copy; persist snapshot; skip ComfyUIManagerInstaller/CommonNodeInstaller for non-ComfyUI | T4 + T9 |
| `src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs` | Switch on `env.TemplateKind` for entry script + args + UserExtraArgs | T5 |
| `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs` | Read `env.TemplateConfigSnapshot.ModelsSubdir` | T6 |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | Drop `Layout` field; add `SelectedTemplateKind` + `TemplateOptions`; auto-fill `ComfyuiSource` from selected template | T7 |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` | Remove Layout radio; add TemplateKind RadioButton group | T7 |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | Add `ShowTemplateManagementCommand` + `TemplateManagementViewModel` instance + `TemplateManagementView` factory | T8 |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Add 9th sidebar RadioButton "模板管理" | T8 |
| `src-wpf/ComfyUI.Manager/Views/Settings/SettingsView.xaml` + `.cs` | Remove `TemplateComfyuiDir` textbox + Browse button | T12 |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | Remove `TemplateComfyuiDir` setter + `BrowseTemplateComfyui` command | T12 |
| `src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs` | Rename to `TemplateSourceUpdater.cs`; accept `(string targetDir, string repoUrl)` params | T11 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | Update DI ctor calls (remove ComfyUITemplateUpdater, add TemplateSourceUpdater); remove tool menu "模板更新" entry | T11 |

---

## Task 1: TemplateConfig model class + tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs`

**Interfaces:** None (pure model class).

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs`:

```csharp
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class TemplateConfigTests
{
    [Fact]
    public void RoundTrip_AllFields_PreservesValues()
    {
        var original = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = "Templates/ComfyUI",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
            ExtraJunctionTargets = new System.Collections.Generic.List<string> { "extra1", "extra2" },
            UserExtraArgs = "--preview-method auto",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<TemplateConfig>(json);

        Assert.NotNull(restored);
        Assert.Equal("ComfyUI", restored!.Name);
        Assert.Equal("ComfyUI", restored.Kind);
        Assert.Equal("Templates/ComfyUI", restored.LocalSourceDir);
        Assert.Equal("main.py", restored.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", restored.EntryArgs);
        Assert.Equal("models", restored.ModelsSubdir);
        Assert.Equal(2, restored.ExtraJunctionTargets.Count);
        Assert.Equal("--preview-method auto", restored.UserExtraArgs);
    }

    [Fact]
    public void DefaultValues_AreEmptyStrings_AndEmptyList()
    {
        var c = new TemplateConfig();
        Assert.Equal("", c.Name);
        Assert.Equal("", c.Kind);
        Assert.Equal("", c.LocalSourceDir);
        Assert.Equal("", c.EntryScript);
        Assert.Equal("", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir); // G5 default
        Assert.Empty(c.ExtraJunctionTargets);
        Assert.Equal("", c.UserExtraArgs);
    }

    [Fact]
    public void JsonPropertyNames_MatchSpec()
    {
        // spec §3 verbatim property names
        var c = new TemplateConfig { Name = "X", Kind = "X" };
        var json = JsonSerializer.Serialize(c);
        Assert.Contains("\"name\":\"X\"", json);
        Assert.Contains("\"kind\":\"X\"", json);
        Assert.Contains("\"local_source_dir\":\"\"", json);
        Assert.Contains("\"entry_script\":\"\"", json);
        Assert.Contains("\"entry_args\":\"\"", json);
        Assert.Contains("\"models_subdir\":\"models\"", json);
        Assert.Contains("\"extra_junction_targets\":[]", json);
        Assert.Contains("\"user_extra_args\":\"\"", json);
    }

    [Fact]
    public void JsonOptions_UsesSnakeCase_NamesFromComfySettingsWriter()
    {
        // ComfySettingsWriter / JsonOptions.cs uses PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
        // (or equivalent). Verify TemplateConfig serializes with snake_case without custom attribute.
        var c = new TemplateConfig { LocalSourceDir = "x", ExtraJunctionTargets = new() { "a" } };
        var json = JsonSerializer.Serialize(c);
        Assert.Contains("local_source_dir", json);
        Assert.Contains("extra_junction_targets", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateConfigTests"`
Expected: FAIL with "TemplateConfig not found" (CS0246 / CS0103).

- [ ] **Step 3: Write minimal implementation**

Create `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0 multi-template: per-template configuration. String-keyed by Kind (no enum).
/// Snapshot per env (Environment.TemplateConfigSnapshot) freezes at env creation time;
/// updates to Settings.Templates do NOT affect existing envs.
/// </summary>
public class TemplateConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("local_source_dir")]
    public string LocalSourceDir { get; set; } = "";

    [JsonPropertyName("entry_script")]
    public string EntryScript { get; set; } = "";

    [JsonPropertyName("entry_args")]
    public string EntryArgs { get; set; } = "";

    [JsonPropertyName("models_subdir")]
    public string ModelsSubdir { get; set; } = "models";

    [JsonPropertyName("extra_junction_targets")]
    public List<string> ExtraJunctionTargets { get; set; } = new();

    [JsonPropertyName("user_extra_args")]
    public string UserExtraArgs { get; set; } = "";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateConfigTests"`
Expected: 4 PASS / 0 FAIL.

If `JsonPropertyNames_MatchSpec` test fails on `models_subdir` because the project JsonOptions uses snake_case automatically without explicit attributes → check `Infrastructure/JsonOptions.cs` and either keep explicit `[JsonPropertyName]` (preferred) or remove the explicit attributes if the project policy converts to snake_case by default. Verify the spec's persistence format (snake_case `template_kind`, `template_config_snapshot`) requires the matching field naming in the Environment model too.

- [ ] **Step 5: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs
git commit -m "feat(v1.0.0): TemplateConfig model class for multi-template"
```

---

## Task 2: Settings.Templates dictionary + SettingsDefaults seeding + migration

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (remove `TemplateComfyuiDir`, add `Templates`)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` (seed ComfyUI+A1111, migrate old field)
- Create: `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` (built-in defaults)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTemplateSeedTests.cs`

**Interfaces:**
- Consumes: `TemplateConfig` from T1
- Produces: `Settings.Templates` dict; `SettingsDefaults.Apply(s, projectRoot)` seeds ComfyUI + A1111 if missing

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTemplateSeedTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTemplateSeedTests
{
    private static readonly string ProjectRoot =
        Path.Combine(Path.GetTempPath(), "cmgr-templates-test");

    [Fact]
    public void Apply_EmptySettings_SeedsComfyUIAndA1111Templates()
    {
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.True(s.Templates.ContainsKey("A1111"));
    }

    [Fact]
    public void Apply_EmptySettings_ComfyUITemplateHasCorrectDefaults()
    {
        // G5: ComfyUI defaults
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var c = s.Templates["ComfyUI"];
        Assert.Equal("ComfyUI", c.Name);
        Assert.Equal("ComfyUI", c.Kind);
        Assert.Equal("main.py", c.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir);
    }

    [Fact]
    public void Apply_EmptySettings_A1111TemplateHasCorrectDefaults()
    {
        // G5: A1111 defaults
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var a = s.Templates["A1111"];
        Assert.Equal("A1111", a.Name);
        Assert.Equal("A1111", a.Kind);
        Assert.Equal("webui.py", a.EntryScript);
        Assert.Equal("--port {port}", a.EntryArgs);
        Assert.Equal("models/Stable-diffusion", a.ModelsSubdir);
    }

    [Fact]
    public void Apply_UserCustomizedTemplate_NotOverwritten()
    {
        // G4: never overwrite user customization
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "MyCustomName",
            Kind = "ComfyUI",
            LocalSourceDir = "D:/my-fork",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 127.0.0.1",
            ModelsSubdir = "models",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("MyCustomName", s.Templates["ComfyUI"].Name);
        Assert.Equal("D:/my-fork", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("--port {port} --listen 127.0.0.1", s.Templates["ComfyUI"].EntryArgs);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDirField_MigratedToTemplatesDict()
    {
        // G6: migrate from old Settings.TemplateComfyuiDir → Settings.Templates["ComfyUI"]
        var s = new Settings { TemplateComfyuiDir = "D:/old/comfyui-source" };
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("D:/old/comfyui-source", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("main.py", s.Templates["ComfyUI"].EntryScript);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsDefaultsTemplateSeedTests"`
Expected: FAIL — `Settings.Templates` doesn't exist yet.

- [ ] **Step 3: Modify Settings.cs**

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`:
- Remove the line: `[JsonPropertyName("template_comfyui_dir")] public string TemplateComfyuiDir { get; set; } = "";`
- Add:
  ```csharp
  [JsonPropertyName("templates")]
  public Dictionary<string, TemplateConfig> Templates { get; set; } = new();
  ```
- Add `using System.Collections.Generic;` if not present.

- [ ] **Step 4: Create TemplateConfigDefaults.cs**

Create `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs`:

```csharp
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons for ComfyUI + A1111.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
/// </summary>
public static class TemplateConfigDefaults
{
    public static TemplateConfig ComfyUi(string projectRoot) => new()
    {
        Name = "ComfyUI",
        Kind = "ComfyUI",
        LocalSourceDir = System.IO.Path.Combine(projectRoot, "Templates", "ComfyUI"),
        EntryScript = "main.py",
        EntryArgs = "--port {port} --listen 0.0.0.0",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    public static TemplateConfig A1111(string projectRoot) => new()
    {
        Name = "A1111",
        Kind = "A1111",
        LocalSourceDir = System.IO.Path.Combine(projectRoot, "Templates", "A1111"),
        EntryScript = "webui.py",
        EntryArgs = "--port {port}",
        ModelsSubdir = "models/Stable-diffusion",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}
```

- [ ] **Step 5: Modify SettingsDefaults.cs**

In `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs`, add `using System.Collections.Generic;` (if not present) + `using ComfyUI.Manager.Services;`.

After `s.CommonNodes = SeedCommonNodesIfEmpty(s.CommonNodes);` and before the LogDirectory block, add:

```csharp
// v1.0.0 multi-template: seed built-in templates + migrate old TemplateComfyuiDir field
SeedBuiltInTemplatesIfMissing(s, projectRoot);
TryMigrateOldTemplateComfyuiDir(s);
```

Add the helper methods at the bottom of the class (next to `ApplyDevOverridesIfEnabled`):

```csharp
private static void SeedBuiltInTemplatesIfMissing(Settings s, string projectRoot)
{
    // G4: only seed if missing — never overwrite user customization
    if (!s.Templates.ContainsKey("ComfyUI"))
    {
        s.Templates["ComfyUI"] = TemplateConfigDefaults.ComfyUi(projectRoot);
    }
    if (!s.Templates.ContainsKey("A1111"))
    {
        s.Templates["A1111"] = TemplateConfigDefaults.A1111(projectRoot);
    }
}

private static void TryMigrateOldTemplateComfyuiDir(Settings s)
{
    // G6: old field → new dict
    // If user has no ComfyUI template entry but has the old field, seed from old value
    if (!s.Templates.ContainsKey("ComfyUI") && !string.IsNullOrWhiteSpace(s.TemplateComfyuiDir))
    {
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = s.TemplateComfyuiDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };
        s.TemplateComfyuiDir = ""; // drop old field
    }
}
```

Note: `Settings.TemplateComfyuiDir` field must remain temporarily so migration can read it. After the migration ships, T12 removes it from Settings + SettingsView.

- [ ] **Step 6: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~SettingsDefaultsTemplateSeedTests"`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 7: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Models/Settings.cs src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTemplateSeedTests.cs
git commit -m "feat(v1.0.0): seed ComfyUI+A1111 templates + migrate old TemplateComfyuiDir"
```

---

## Task 3: Environment model fields + SQLite column migration + backfill

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs` (add `TemplateKind` + `TemplateConfigSnapshot`)
- Modify: `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` (schema migration + backfill)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTemplateKindTests.cs`

**Interfaces:**
- Consumes: `TemplateConfig` from T1
- Produces: `Environment.TemplateKind` + `Environment.TemplateConfigSnapshot`; `EnvironmentRepository.LoadAll` backfills old rows

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTemplateKindTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class EnvironmentRepositoryTemplateKindTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;

    public EnvironmentRepositoryTemplateKindTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "cmgr-envtmpl-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        _factory = new SqliteConnectionFactory(new LocalDataPathsForTest(_dbPath));
        using (var conn = _factory.Create())
        {
            conn.Execute(@"
                CREATE TABLE environments (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    python_exe TEXT,
                    port INTEGER,
                    comfyui_source TEXT,
                    comfyui_layout TEXT,
                    notes TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );");
            // Insert old-format row (no template_kind, no template_config_snapshot)
            conn.Execute(@"
                INSERT INTO environments (id, name, status, comfyui_source, comfyui_layout, created_at, updated_at)
                VALUES ('old-env', 'oldEnv', 'stopped', 'D:/old/comfyui', 'shared',
                        '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');");
        }
    }

    [Fact]
    public void LoadAll_OldRow_DefaultsToComfyUIKindAndSnapshot()
    {
        var repo = new EnvironmentRepository(_factory);
        var envs = repo.LoadAll();

        var old = Assert.Single(envs);
        Assert.Equal("oldEnv", old.Name);
        Assert.Equal("ComfyUI", old.TemplateKind);
        Assert.NotNull(old.TemplateConfigSnapshot);
        Assert.Equal("main.py", old.TemplateConfigSnapshot!.EntryScript);
    }

    [Fact]
    public void Save_ThenLoadAll_PreservesTemplateKindAndSnapshot()
    {
        var repo = new EnvironmentRepository(_factory);
        var snapshot = new TemplateConfig
        {
            Name = "A1111",
            Kind = "A1111",
            LocalSourceDir = "Templates/A1111",
            EntryScript = "webui.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models/Stable-diffusion",
        };
        var env = new Environment
        {
            Id = "new-env",
            Name = "newEnv",
            Status = "stopped",
            Port = 9001,
            TemplateKind = "A1111",
            TemplateConfigSnapshot = snapshot,
        };
        repo.Save(env);

        var loaded = repo.LoadAll();
        var found = loaded.Find(e => e.Id == "new-env");
        Assert.NotNull(found);
        Assert.Equal("A1111", found!.TemplateKind);
        Assert.Equal("webui.py", found.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("models/Stable-diffusion", found.TemplateConfigSnapshot.ModelsSubdir);
    }

    [Fact]
    public void Update_ExistingEnv_KeepsTemplateKindAndSnapshot()
    {
        var repo = new EnvironmentRepository(_factory);
        var snapshot = new TemplateConfig { Kind = "ComfyUI", EntryScript = "main.py" };
        var env = new Environment
        {
            Id = "upd-env",
            Name = "updEnv",
            Status = "running",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = snapshot,
        };
        repo.Save(env);
        env.Status = "stopped";
        repo.Update(env);

        var loaded = repo.LoadAll();
        var found = loaded.Find(e => e.Id == "upd-env")!;
        Assert.Equal("stopped", found.Status);
        Assert.Equal("ComfyUI", found.TemplateKind);
        Assert.Equal("main.py", found.TemplateConfigSnapshot!.EntryScript);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        var shm = _dbPath + "-shm";
        var wal = _dbPath + "-wal";
        if (File.Exists(shm)) File.Delete(shm);
        if (File.Exists(wal)) File.Delete(wal);
    }
}

// Test helper to construct LocalDataPaths with a custom db path
internal class LocalDataPathsForTest : ComfyUI.Manager.Infrastructure.LocalDataPaths
{
    public LocalDataPathsForTest(string dbPath) : base(tempRoot: System.IO.Path.GetDirectoryName(dbPath)!) { }
}
```

Inspect `LocalDataPaths` to confirm its ctor signature — adjust the helper if needed.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EnvironmentRepositoryTemplateKindTests"`
Expected: FAIL — `Environment.TemplateKind` doesn't exist.

- [ ] **Step 3: Modify Environment.cs**

In `src-wpf/ComfyUI.Manager/Models/Environment.cs`, add fields:

```csharp
// v1.0.0 multi-template: which template kind this env was created from
[JsonPropertyName("template_kind")]
public string TemplateKind { get; set; } = "ComfyUI";

// v1.0.0 multi-template: snapshot of the TemplateConfig at env creation time.
// Updates to Settings.Templates do NOT affect this env (reproducibility).
[JsonPropertyName("template_config_snapshot")]
public TemplateConfig? TemplateConfigSnapshot { get; set; }
```

Add `using System.Text.Json.Serialization;` if not present.

- [ ] **Step 4: Modify EnvironmentRepository.cs**

In `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`:

1. **Schema migration**: in the table-creation code (or a `Migrate` step), add 2 columns to the `environments` table:
   ```sql
   ALTER TABLE environments ADD COLUMN template_kind TEXT NOT NULL DEFAULT 'ComfyUI';
   ALTER TABLE environments ADD COLUMN template_config_snapshot TEXT; -- JSON serialized TemplateConfig
   ```
   Wrap in `try { ... } catch { /* column already exists */ }` for idempotency. Read existing schema in EnvironmentRepository first to find the right place.

2. **Backfill in LoadAll**: after loading each env row, if `template_kind` is "ComfyUI" AND `template_config_snapshot` is null, populate `TemplateConfigSnapshot` from `Settings.Templates["ComfyUI"]` (passed via ctor injection or read directly from `SettingsRepository`).

3. **Insert/Update SQL**: include `template_kind` and `template_config_snapshot` (JSON-serialized) in both INSERT and UPDATE statements.

Inspect `EnvironmentRepository` to find the exact Save/Update/LoadAll method names + SQL and adapt. Keep tests passing.

- [ ] **Step 5: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EnvironmentRepositoryTemplateKindTests"`
Expected: 3 PASS / 0 FAIL.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Models/Environment.cs src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTemplateKindTests.cs
git commit -m "feat(v1.0.0): Environment template_kind + snapshot + SQLite migration"
```

---

## Task 4: EnvCreatorService always-copy refactor

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` (remove Layout branching, always copy, take TemplateKind param, persist snapshot, skip ComfyUIManagerInstaller/CommonNodeInstaller for non-ComfyUI)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceMultiTemplateTests.cs`

**Interfaces:**
- Consumes: `TemplateConfig` from T1; `Environment.TemplateKind` + `TemplateConfigSnapshot` from T3
- Produces: New `EnvCreatorService.CreateAsync(name, templateConfig, pythonExe, port, notes, ct)` signature; env persisted with snapshot

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceMultiTemplateTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class EnvCreatorServiceMultiTemplateTests : IDisposable
{
    private readonly string _workRoot;
    private readonly string _srcDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Settings _settings;
    private readonly JunctionLinker _linker;
    private readonly VenvCreator _venvCreator;
    private readonly string _dbPath;

    public EnvCreatorServiceMultiTemplateTests()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "cmgr-envcreate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workRoot);

        // Build a fake template source (a single file inside)
        _srcDir = Path.Combine(_workRoot, "fake-template");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "main.py"), "print('hello')");
        File.WriteAllText(Path.Combine(_srcDir, ".gitkeep"), "");

        _dbPath = Path.Combine(_workRoot, "state.db");
        _factory = new SqliteConnectionFactory(new TestLocalDataPaths(_workRoot));
        using (var conn = _factory.Create())
        {
            conn.Execute(@"
                CREATE TABLE environments (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    python_exe TEXT,
                    port INTEGER,
                    comfyui_source TEXT,
                    comfyui_layout TEXT,
                    notes TEXT,
                    template_kind TEXT NOT NULL DEFAULT 'ComfyUI',
                    template_config_snapshot TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );");
        }
        _settings = new Settings();
        _linker = new JunctionLinker();
        _venvCreator = new VenvCreator();
    }

    [Fact]
    public void CreateAsync_ComfyUIEnv_AlwaysCopiesSourceFiles()
    {
        // G3: always copy, no junction
        var envRepo = new EnvironmentRepository(_factory);
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models",
        };

        var env = svc.CreateAsync(
            name: "comfyEnv",
            templateConfig: template,
            pythonExe: "python",
            port: 9000,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.NotNull(env);
        Assert.Equal("ComfyUI", env.TemplateKind);
        Assert.Equal("main.py", env.TemplateConfigSnapshot!.EntryScript);
        // File copied (not junctioned — verify by checking the file exists and is not a junction)
        Assert.True(File.Exists(Path.Combine(_workRoot, "envs", "comfyEnv", "main.py")));
    }

    [Fact]
    public void CreateAsync_A1111Env_SnapshotIncludesWebuiPy()
    {
        // G5: A1111 snapshot uses webui.py entry
        File.WriteAllText(Path.Combine(_srcDir, "webui.py"), "print('a1111')");
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "A1111",
            LocalSourceDir = _srcDir,
            EntryScript = "webui.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models/Stable-diffusion",
        };

        var env = svc.CreateAsync(
            name: "a1111Env",
            templateConfig: template,
            pythonExe: "python",
            port: 9001,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.Equal("A1111", env.TemplateKind);
        Assert.Equal("webui.py", env.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("models/Stable-diffusion", env.TemplateConfigSnapshot.ModelsSubdir);
        Assert.True(File.Exists(Path.Combine(_workRoot, "envs", "a1111Env", "webui.py")));
    }

    [Fact]
    public void CreateAsync_CustomEnv_AcceptsUserEntryScript()
    {
        // G12: Custom kind uses user-defined entry script
        File.WriteAllText(Path.Combine(_srcDir, "my-entry.sh"), "echo custom");
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "MySwarmUI",
            LocalSourceDir = _srcDir,
            EntryScript = "my-entry.sh",
            EntryArgs = "--listen 0.0.0.0",
            ModelsSubdir = "models",
        };

        var env = svc.CreateAsync(
            name: "customEnv",
            templateConfig: template,
            pythonExe: "python",
            port: 9002,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.Equal("MySwarmUI", env.TemplateKind);
        Assert.Equal("my-entry.sh", env.TemplateConfigSnapshot!.EntryScript);
    }

    [Fact]
    public void CreateAsync_SnapshotIsFrozen_NotAffectedBySettingsChanges()
    {
        // G2: snapshot is frozen at creation
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
        };
        var env = svc.CreateAsync("env1", template, "python", 9000, "", default).GetAwaiter().GetResult();
        var snapshotBefore = env.TemplateConfigSnapshot!;

        // User edits template defaults AFTER env creation
        _settings.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI",
            EntryScript = "DIFFERENT.py",
            EntryArgs = "--totally-different",
        };

        // Reload env from DB — snapshot should be unchanged
        var repo = new EnvironmentRepository(_factory);
        var reloaded = repo.LoadAll().Find(e => e.Id == env.Id)!;
        Assert.Equal("main.py", reloaded.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", reloaded.TemplateConfigSnapshot.EntryArgs);
    }

    [Fact]
    public void CreateAsync_DoesNotJunction_ComfyUISourceEvenWhenCallerExpectedShared()
    {
        // G3: even if caller passes shared-layout-style params, no junction
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
        };
        var env = svc.CreateAsync("sharedTest", template, "python", 9000, "", default).GetAwaiter().GetResult();

        var envComfyDir = Path.Combine(_workRoot, "envs", "sharedTest");
        // env dir exists with main.py file (copy), not a junction
        Assert.True(Directory.Exists(envComfyDir));
        Assert.True(File.Exists(Path.Combine(envComfyDir, "main.py")));
        // Sanity: source still has the same file (copy semantics)
        Assert.True(File.Exists(Path.Combine(_srcDir, "main.py")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EnvCreatorServiceMultiTemplateTests"`
Expected: FAIL — ctor signature changed.

- [ ] **Step 3: Modify EnvCreatorService.cs**

In `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`:

1. Change `CreateAsync` signature: take `TemplateConfig templateConfig` instead of `string? comfyuiSource` + `string? layout`. The layout param is removed entirely.

2. Inside CreateAsync:
   - Validate `templateConfig.Kind` non-empty, `templateConfig.LocalSourceDir` non-empty.
   - `var envDir = Path.Combine(projectRoot, "envs", name); Directory.CreateDirectory(envDir);`
   - **Always copy**: `_linker.CopyDirectory(templateConfig.LocalSourceDir, envDir);` (the existing copy branch — no junction path).
   - Create venv as today.
   - Persist env with `TemplateKind = templateConfig.Kind`, `TemplateConfigSnapshot = templateConfig` (clone the object so subsequent settings edits don't mutate the snapshot — use `Clone()` or serialize+deserialize via `JsonSerializer` round-trip).
   - For `ComfyUIManagerInstaller` + `CommonNodeInstaller` calls (G9): wrap in `if (templateConfig.Kind == "ComfyUI")`.

3. Remove any `Layout` enum/parameter from the class.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EnvCreatorServiceMultiTemplateTests"`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 5: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceMultiTemplateTests.cs
git commit -m "feat(v1.0.0): EnvCreatorService always-copies template config snapshot"
```

---

## Task 5: ProcessLauncher per-kind entry config

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs` (switch on `env.TemplateKind` for entry script + args + UserExtraArgs)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherTemplateKindTests.cs`

**Interfaces:**
- Consumes: `Environment.TemplateConfigSnapshot` from T3
- Produces: `StartEnvAsync(env)` builds the right entry command per kind

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherTemplateKindTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ProcessLauncherTemplateKindTests
{
    [Fact]
    public void BuildStartCommand_ComfyUI_UsesMainPyArgsAndUserExtras()
    {
        // G7: <venvPython> <EntryScript> <EntryArgs-with-{port}> [UserExtraArgs]
        var env = new Environment
        {
            Id = "e1", Name = "e1", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9000,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port} --listen 0.0.0.0",
                UserExtraArgs = "--preview-method auto",
            },
        };
        var settings = new Settings();

        var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e1\venv\Scripts\python.exe", exe);
        Assert.Equal(@"D:\fake\envs\e1\main.py", args.File);
        Assert.Contains("--port 9000 --listen 0.0.0.0", args.ArgsString);
        Assert.Contains("--preview-method auto", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_A1111_UsesWebuiPy()
    {
        var env = new Environment
        {
            Id = "e2", Name = "e2", Status = "stopped",
            TemplateKind = "A1111",
            Port = 9001,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "A1111",
                EntryScript = "webui.py",
                EntryArgs = "--port {port}",
                UserExtraArgs = "--xformers",
            },
        };
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e2\webui.py", args.File);
        Assert.Contains("--port 9001", args.ArgsString);
        Assert.Contains("--xformers", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_Custom_UsesSnapshotEntryScript()
    {
        var env = new Environment
        {
            Id = "e3", Name = "e3", Status = "stopped",
            TemplateKind = "MySwarmUI",
            Port = 9002,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "MySwarmUI",
                EntryScript = "swarmui-launcher.sh",
                EntryArgs = "--listen 0.0.0.0",
            },
        };
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e3\swarmui-launcher.sh", args.File);
        Assert.Contains("--listen 0.0.0.0", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_MissingSnapshot_FallsBackToSettingsTemplates()
    {
        // backward compat: old env rows may not have snapshot — fallback to current Settings.Templates
        var env = new Environment
        {
            Id = "e4", Name = "e4", Status = "stopped",
            TemplateKind = "A1111",
            Port = 9003,
            TemplateConfigSnapshot = null,
        };
        var settings = new Settings();
        settings.Templates["A1111"] = new TemplateConfig
        {
            Kind = "A1111",
            EntryScript = "webui.py",
            EntryArgs = "--port {port}",
        };

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e4\webui.py", args.File);
        Assert.Contains("--port 9003", args.ArgsString);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~ProcessLauncherTemplateKindTests"`
Expected: FAIL — `BuildStartCommand` doesn't exist or signature differs.

- [ ] **Step 3: Modify ProcessLauncher.cs**

In `src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs`:

1. **Extract helper**: add a public static `BuildStartCommand(Environment env, Settings settings, string projectRoot)` method returning `(string exe, (string File, string ArgsString) args)`. (This is the test seam — implementation lives here.)

2. **Implementation**:
   ```csharp
   public static (string exe, (string File, string ArgsString) args) BuildStartCommand(
       Environment env, Settings settings, string projectRoot)
   {
       var snapshot = env.TemplateConfigSnapshot
           ?? settings.Templates.GetValueOrDefault(env.TemplateKind)
           ?? throw new InvalidOperationException($"模板 '{env.TemplateKind}' 不存在,可能在 Settings 中已被删除");

       var venvPython = projectRoot + "/envs/" + env.Name + "/venv/Scripts/python.exe";
       var entryScript = projectRoot + "/envs/" + env.Name + "/" + snapshot.EntryScript;
       var port = env.Port?.ToString() ?? "8000";
       var entryArgs = snapshot.EntryArgs.Replace("{port}", port);
       if (!string.IsNullOrWhiteSpace(snapshot.UserExtraArgs))
           entryArgs += " " + snapshot.UserExtraArgs;

       return (venvPython, (entryScript, entryArgs));
   }
   ```

3. **Use in StartEnvAsync**: replace the existing hardcoded `python main.py` block with a call to `BuildStartCommand` and use the returned exe + args.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~ProcessLauncherTemplateKindTests"`
Expected: 4 PASS / 0 FAIL.

- [ ] **Step 5: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherTemplateKindTests.cs
git commit -m "feat(v1.0.0): ProcessLauncher per-kind entry script + UserExtraArgs"
```

---

## Task 6: ModelSymlinker per-kind ModelsSubdir

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs` (read `env.TemplateConfigSnapshot.ModelsSubdir`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTemplateKindTests.cs`

**Interfaces:**
- Consumes: `Environment.TemplateConfigSnapshot` from T3
- Produces: `ModelSymlinker.SyncAsync(env)` uses the snapshot's ModelsSubdir

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTemplateKindTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSymlinkerTemplateKindTests
{
    [Fact]
    public void GetEnvModelsDir_ComfyUI_ReturnsModels()
    {
        // G8: ComfyUI default ModelsSubdir is "models"
        var env = new Environment
        {
            Id = "e1", Name = "e1",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = new TemplateConfig { ModelsSubdir = "models" },
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e1\models", dir);
    }

    [Fact]
    public void GetEnvModelsDir_A1111_ReturnsStableDiffusionSubdir()
    {
        // G8: A1111 ModelsSubdir is "models/Stable-diffusion"
        var env = new Environment
        {
            Id = "e2", Name = "e2",
            TemplateKind = "A1111",
            TemplateConfigSnapshot = new TemplateConfig { ModelsSubdir = "models/Stable-diffusion" },
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e2\models\Stable-diffusion", dir);
    }

    [Fact]
    public void GetEnvModelsDir_MissingSnapshot_FallsBackToModels()
    {
        // backward compat
        var env = new Environment
        {
            Id = "e3", Name = "e3",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = null,
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e3\models", dir);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~ModelSymlinkerTemplateKindTests"`
Expected: FAIL — `GetEnvModelsDir` doesn't exist.

- [ ] **Step 3: Modify ModelSymlinker.cs**

In `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs`:

1. Add a public static helper:
   ```csharp
   public static string GetEnvModelsDir(Environment env, string projectRoot)
   {
       var subdir = env.TemplateConfigSnapshot?.ModelsSubdir;
       if (string.IsNullOrEmpty(subdir)) subdir = "models";
       return Path.Combine(projectRoot, "envs", env.Name, subdir.Replace('/', Path.DirectorySeparatorChar));
   }
   ```

2. In `SyncAsync(env)`, replace any hardcoded `"models"` literal with `GetEnvModelsDir(env, projectRoot)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~ModelSymlinkerTemplateKindTests"`
Expected: 3 PASS / 0 FAIL.

- [ ] **Step 5: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs tests-wpf/ComfyUI.Manager.Tests/Services/ModelSymlinkerTemplateKindTests.cs
git commit -m "feat(v1.0.0): ModelSymlinker per-kind ModelsSubdir"
```

---

## Task 7: CreateEnvDialog template kind picker (VM + XAML)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` (drop Layout, add SelectedTemplateKind, auto-fill source)
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` (remove Layout radio, add TemplateKind RadioButton group)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogTemplateKindTests.cs`

**Interfaces:**
- Consumes: `Settings.Templates` from T2
- Produces: `CreateEnvDialogViewModel.SelectedTemplateKind` + `TemplateOptions` list

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogTemplateKindTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogTemplateKindTests
{
    private (CreateEnvDialogViewModel vm, Settings settings) BuildVm(
        Dictionary<string, TemplateConfig>? templates = null)
    {
        var settings = new Settings();
        if (templates != null) settings.Templates = templates;
        else
        {
            settings.Templates["ComfyUI"] = new TemplateConfig
            {
                Kind = "ComfyUI", LocalSourceDir = "Templates/ComfyUI",
                EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models",
            };
            settings.Templates["A1111"] = new TemplateConfig
            {
                Kind = "A1111", LocalSourceDir = "Templates/A1111",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
        }
        var creator = new EnvCreatorService(
            new SqliteConnectionFactory(new TestLocalDataPathsForCreateDialog()),
            new VenvCreator(), new JunctionLinker(), settings, "C:/fake-root");
        var vm = new CreateEnvDialogViewModel(creator, settings, "C:/fake-root");
        return (vm, settings);
    }

    [Fact]
    public void TemplateOptions_ListsAllSettingsTemplates()
    {
        var (vm, _) = BuildVm();
        var kinds = vm.TemplateOptions.Select(t => t.Kind).ToList();
        Assert.Contains("ComfyUI", kinds);
        Assert.Contains("A1111", kinds);
    }

    [Fact]
    public void SelectedTemplateKind_DefaultIsComfyUI()
    {
        var (vm, _) = BuildVm();
        Assert.Equal("ComfyUI", vm.SelectedTemplateKind);
    }

    [Fact]
    public void SetSelectedTemplateKind_UpdatesComfyuiSource()
    {
        // When user picks a kind, the ComfyuiSource auto-fills from that template
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "A1111";
        Assert.Equal("Templates/A1111", vm.ComfyuiSource);
    }

    [Fact]
    public void CanConfirm_ValidNameAndPython_ReturnsTrue()
    {
        var (vm, _) = BuildVm();
        vm.Name = "myEnv";
        vm.PythonExe = "python";
        vm.SelectedTemplateKind = "ComfyUI";
        vm.ComfyuiSource = "Templates/ComfyUI";
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void CanConfirm_UnknownTemplateKind_ReturnsFalse()
    {
        var (vm, _) = BuildVm();
        vm.Name = "myEnv";
        vm.PythonExe = "python";
        vm.SelectedTemplateKind = "NonExistentKind";
        Assert.False(vm.CanConfirm);
    }
}

internal class TestLocalDataPathsForCreateDialog : ComfyUI.Manager.Infrastructure.LocalDataPaths
{
    public TestLocalDataPathsForCreateDialog() : base(tempRoot: System.IO.Path.GetTempPath()) { }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~CreateEnvDialogTemplateKindTests"`
Expected: FAIL — `TemplateOptions` / `SelectedTemplateKind` don't exist.

- [ ] **Step 3: Modify CreateEnvDialogViewModel.cs**

In `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`:

1. Remove the `Layout` property, `LayoutOptions` list, `_layout` field, and any Layout-related `OnPropertyChanged` plumbing.
2. Add:
   ```csharp
   public IReadOnlyList<TemplateConfig> TemplateOptions { get; }

   private string _selectedTemplateKind = "ComfyUI";
   public string SelectedTemplateKind
   {
       get => _selectedTemplateKind;
       set
       {
           if (SetField(ref _selectedTemplateKind, value))
           {
               // Auto-fill ComfyuiSource from the selected template
               if (_settings.Templates.TryGetValue(value, out var t))
               {
                   ComfyuiSource = t.LocalSourceDir;
               }
               OnPropertyChanged(nameof(CanConfirm));
           }
       }
   }
   ```

3. Initialize `TemplateOptions` from `_settings.Templates.Values.ToList()` in ctor.

4. Update `CanConfirm`: remove the `Layout == "shared"` check; replace with `TemplateOptions.Any(t => t.Kind == SelectedTemplateKind)`.

- [ ] **Step 4: Modify CreateEnvDialog.xaml**

In `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`:

1. Find and remove the existing Layout radio button group (`shared` / `independent`).
2. Add a TemplateKind picker — either RadioButtons bound to `TemplateOptions` with `SelectedItem` two-way bound to `SelectedTemplateKind`, or a ComboBox of `Kind` values. Keep the visual style consistent with existing dialog (no emoji per G16).
3. Show each option as: `{Binding Name} ({Binding LocalSourceDir})` so user can see what each template points to.

- [ ] **Step 5: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~CreateEnvDialogTemplateKindTests"`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogTemplateKindTests.cs
git commit -m "feat(v1.0.0): CreateEnvDialog template kind picker (drop shared/independent)"
```

---

## Task 8: TemplateManagementViewModel + sidebar wire-up

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/TemplateManagementViewModel.cs` (list + add/edit/delete)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (ShowTemplateManagementCommand + TemplateManagementViewModel instance)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml` (9th sidebar RadioButton "模板管理")
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/TemplateManagementViewModelTests.cs`

**Interfaces:**
- Consumes: `Settings.Templates` from T2
- Produces: `TemplateManagementViewModel.Templates` (ObservableCollection<TemplateConfig>); `AddCommand` / `EditCommand` / `DeleteCommand` / `UpdateSourceCommand`

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/TemplateManagementViewModelTests.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class TemplateManagementViewModelTests
{
    private static Settings SeedSettings() => new()
    {
        Templates = new Dictionary<string, TemplateConfig>
        {
            ["ComfyUI"] = new TemplateConfig { Name = "ComfyUI", Kind = "ComfyUI", LocalSourceDir = "Templates/ComfyUI", EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models" },
            ["A1111"] = new TemplateConfig { Name = "A1111", Kind = "A1111", LocalSourceDir = "Templates/A1111", EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion" },
            ["MySwarm"] = new TemplateConfig { Name = "MySwarm", Kind = "MySwarm", LocalSourceDir = "D:/swarmui", EntryScript = "launch.sh", EntryArgs = "--listen", ModelsSubdir = "models" },
        },
    };

    [Fact]
    public void Ctor_LoadsAllTemplatesFromSettings()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.Equal(3, vm.Templates.Count);
        Assert.Contains(vm.Templates, t => t.Kind == "ComfyUI");
        Assert.Contains(vm.Templates, t => t.Kind == "A1111");
        Assert.Contains(vm.Templates, t => t.Kind == "MySwarm");
    }

    [Fact]
    public void DeleteCommand_CustomTemplate_RemovesFromSettings()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");
        vm.DeleteCommand.Execute(custom);
        Assert.Equal(2, vm.Templates.Count);
        Assert.False(s.Templates.ContainsKey("MySwarm"));
    }

    [Fact]
    public void DeleteCommand_BuiltInTemplate_Blocked()
    {
        // G13: built-in ComfyUI/A1111 cannot be deleted
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");
        vm.DeleteCommand.Execute(comfy);
        Assert.Equal(3, vm.Templates.Count);
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
    }

    [Fact]
    public void IsBuiltIn_ComfyUIAndA1111_True_OtherFalse()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.True(vm.IsBuiltIn("ComfyUI"));
        Assert.True(vm.IsBuiltIn("A1111"));
        Assert.False(vm.IsBuiltIn("MySwarm"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateManagementViewModelTests"`
Expected: FAIL — class doesn't exist.

- [ ] **Step 3: Create TemplateManagementViewModel.cs**

Create `src-wpf/ComfyUI.Manager/ViewModels/TemplateManagementViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0 multi-template: sidebar page VM. Lists + adds + edits + deletes templates.
/// Built-in ComfyUI + A1111 are protected from delete (G13).
/// </summary>
public class TemplateManagementViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly Func<EditTemplateDialogViewModel> _editFactory;
    private readonly TemplateSourceUpdater? _updater;

    public ObservableCollection<TemplateConfig> Templates { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UpdateSourceCommand { get; }

    public TemplateManagementViewModel(
        Settings settings,
        Func<EditTemplateDialogViewModel>? editTemplateFactory,
        TemplateSourceUpdater? updater)
    {
        _settings = settings;
        _editFactory = editTemplateFactory ?? (() => new EditTemplateDialogViewModel(_settings, null));
        _updater = updater;

        // Load existing templates
        foreach (var kvp in _settings.Templates)
        {
            Templates.Add(kvp.Value);
        }

        AddCommand = new RelayCommand(AddTemplate);
        EditCommand = new RelayCommand<TemplateConfig>(EditTemplate);
        DeleteCommand = new RelayCommand<TemplateConfig>(DeleteTemplate, t => t != null && !IsBuiltIn(t.Kind));
        UpdateSourceCommand = new RelayCommand<TemplateConfig>(UpdateTemplateSource, t => t != null);
    }

    public bool IsBuiltIn(string kind) => kind == "ComfyUI" || kind == "A1111";

    private void AddTemplate()
    {
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Add;
        // Show dialog (callers will wire this — for now, directly apply if ShowDialog returns true)
        if (vm.ShowDialogRequested != null)
        {
            vm.ShowDialogRequested.Invoke(vm);
            // After dialog closes, check AppliedToSettings flag
            if (vm.AppliedToSettings)
            {
                Templates.Add(vm.WorkingConfig);
                _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
            }
        }
    }

    private void EditTemplate(TemplateConfig? t)
    {
        if (t == null) return;
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Edit;
        vm.LoadFrom(t);
        if (vm.ShowDialogRequested != null)
        {
            vm.ShowDialogRequested.Invoke(vm);
            if (vm.AppliedToSettings)
            {
                _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
                // Refresh list
                var idx = Templates.IndexOf(t);
                if (idx >= 0) Templates[idx] = vm.WorkingConfig;
            }
        }
    }

    private void DeleteTemplate(TemplateConfig? t)
    {
        if (t == null || IsBuiltIn(t.Kind)) return;
        _settings.Templates.Remove(t.Kind);
        Templates.Remove(t);
    }

    private void UpdateTemplateSource(TemplateConfig? t)
    {
        if (t == null || _updater == null) return;
        // Fire-and-forget update — progress reported via AppLogger
        _ = _updater.UpdateAsync(t.LocalSourceDir, GetDefaultRepoUrl(t.Kind), null, default);
    }

    private static string GetDefaultRepoUrl(string kind) => kind switch
    {
        "ComfyUI" => "https://github.com/comfyanonymous/ComfyUI.git",
        "A1111" => "https://github.com/AUTOMATIC1111/stable-diffusion-webui.git",
        _ => "", // custom: caller should not invoke
    };
}
```

- [ ] **Step 4: Modify MainViewModel.cs**

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:

1. Add a `TemplateManagementViewModel TemplateManagementVm { get; }` property (or lazy instance).
2. Add `ShowTemplateManagementCommand` that returns the view to display when sidebar "模板管理" is selected.
3. Wire the ctor to construct `TemplateManagementVm` from `Settings` + a factory for `EditTemplateDialogViewModel` + `TemplateSourceUpdater` (added in T11).

Adjust ctor signature to accept these new dependencies.

- [ ] **Step 5: Modify MainWindow.xaml**

In `src-wpf/ComfyUI.Manager/MainWindow.xaml`, find the existing sidebar RadioButton list (look for the pattern around "工作流市场" and "模型市场" — they share `GroupName="MainView"` or similar). Add a 9th entry:

```xml
<RadioButton Content="模板管理"
        Command="{Binding ShowTemplateManagementCommand}"
        GroupName="MainView"
        Tag="templates"/>
```

Match the existing `Style` + `Margin` conventions.

- [ ] **Step 6: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateManagementViewModelTests"`
Expected: 4 PASS / 0 FAIL.

Note: the AddCommand test isn't in T8 tests (would require a mock dialog). Add + Edit interactions are tested in T10 via `EditTemplateDialogViewModel` directly.

- [ ] **Step 7: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/ViewModels/TemplateManagementViewModel.cs src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/MainWindow.xaml tests-wpf/ComfyUI.Manager.Tests/ViewModels/TemplateManagementViewModelTests.cs
git commit -m "feat(v1.0.0): TemplateManagement sidebar page + VM + 9th RadioButton"
```

---

## Task 9: TemplateManagementView XAML (card list)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Views/TemplateManagementViewLoadTests.cs`

**Interfaces:**
- Consumes: `TemplateManagementViewModel` from T8
- Produces: Visual card list bound to `vm.Templates`

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Views/TemplateManagementViewLoadTests.cs`:

```csharp
using System.IO;
using System.Windows;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views.TemplateManagement;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class TemplateManagementViewLoadTests
{
    [Fact]
    public void View_Loads_WithTemplateManagementViewModel()
    {
        var vm = new TemplateManagementViewModel(
            new ComfyUI.Manager.Models.Settings(),
            editTemplateFactory: null,
            updater: null);
        var view = new TemplateManagementView { DataContext = vm };
        // XAML load is implicit in ctor; if XAML has compile errors, the test won't run
        Assert.NotNull(view);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateManagementViewLoadTests"`
Expected: FAIL — `TemplateManagementView` doesn't exist.

- [ ] **Step 3: Create TemplateManagementView.xaml**

Create `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml`:

```xml
<UserControl x:Class="ComfyUI.Manager.Views.TemplateManagement.TemplateManagementView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             mc:Ignorable="d"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             d:DataContext="{d:DesignInstance Type=vm:TemplateManagementViewModel}">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <DockPanel Grid.Row="0" Margin="0,0,0,12">
            <TextBlock Text="模板管理" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center"/>
            <Button Content="+ 添加模板" DockPanel.Dock="Right"
                    Command="{Binding AddCommand}"
                    HorizontalAlignment="Right"/>
        </DockPanel>

        <!-- Card list -->
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Templates}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Margin="0,0,0,8" Padding="12" CornerRadius="6"
                                BorderBrush="{DynamicResource OutlineBrush}"
                                BorderThickness="1"
                                Background="{DynamicResource SurfaceBrush}">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>

                                <TextBlock Grid.Row="0" Grid.Column="0"
                                           Text="{Binding Name}" FontSize="16" FontWeight="SemiBold"/>
                                <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal">
                                    <Button Content="更新源码" Margin="4,0"
                                            Command="{Binding DataContext.UpdateSourceCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"/>
                                    <Button Content="编辑" Margin="4,0"
                                            Command="{Binding DataContext.EditCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"/>
                                    <Button Content="删除" Margin="4,0"
                                            Command="{Binding DataContext.DeleteCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}"/>
                                </StackPanel>

                                <TextBlock Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" Margin="0,4,0,0">
                                    <Run Text="Kind: "/><Run Text="{Binding Kind}"/>
                                </TextBlock>
                                <TextBlock Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="2">
                                    <Run Text="Source: "/><Run Text="{Binding LocalSourceDir}"/>
                                </TextBlock>
                                <TextBlock Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="2">
                                    <Run Text="Entry: "/><Run Text="{Binding EntryScript}"/><Run Text=" "/><Run Text="{Binding EntryArgs}"/>
                                </TextBlock>
                                <TextBlock Grid.Row="4" Grid.Column="0" Grid.ColumnSpan="2">
                                    <Run Text="Models: "/><Run Text="{Binding ModelsSubdir}"/>
                                </TextBlock>
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </Grid>
</UserControl>
```

- [ ] **Step 4: Create TemplateManagementView.xaml.cs**

Create `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ComfyUI.Manager.Views.TemplateManagement;

public partial class TemplateManagementView : UserControl
{
    public TemplateManagementView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateManagementViewLoadTests"`
Expected: 1 PASS / 0 FAIL.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Views/TemplateManagement/ tests-wpf/ComfyUI.Manager.Tests/Views/TemplateManagementViewLoadTests.cs
git commit -m "feat(v1.0.0): TemplateManagementView card list XAML"
```

---

## Task 10: EditTemplateDialog (VM + XAML)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EditTemplateDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `Settings.Templates` from T2; `TemplateConfig` from T1
- Produces: `EditTemplateDialogViewModel` with `Mode : Add | Edit`, `WorkingConfig`, `AppliedToSettings`, `ShowDialogRequested` event

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EditTemplateDialogViewModelTests.cs`:

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EditTemplateDialogViewModelTests
{
    private static Settings SeedSettings() => new()
    {
        Templates = new Dictionary<string, TemplateConfig>
        {
            ["ComfyUI"] = new TemplateConfig { Name = "ComfyUI", Kind = "ComfyUI" },
        },
    };

    [Fact]
    public void Ctor_AddMode_EmptyWorkingConfig()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        Assert.Equal("", vm.WorkingConfig.Name);
        Assert.Equal("", vm.WorkingConfig.Kind);
    }

    [Fact]
    public void LoadFrom_EditMode_CopiesAllFields()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Edit };
        var existing = new TemplateConfig
        {
            Name = "A1111", Kind = "A1111", LocalSourceDir = "Templates/A1111",
            EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
        };
        vm.LoadFrom(existing);
        Assert.Equal("A1111", vm.WorkingConfig.Name);
        Assert.Equal("webui.py", vm.WorkingConfig.EntryScript);
        Assert.Equal("models/Stable-diffusion", vm.WorkingConfig.ModelsSubdir);
    }

    [Fact]
    public void CanSave_EmptyName_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "";
        vm.WorkingConfig.Kind = "ComfyUI";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_EmptyKind_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTemplate";
        vm.WorkingConfig.Kind = "";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_AddMode_DuplicateKind_False()
    {
        // Cannot add a kind that already exists
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "ComfyUI";
        vm.WorkingConfig.Kind = "ComfyUI";  // already exists
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_AddMode_ValidInputs_True()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MySwarm";
        vm.WorkingConfig.Kind = "MySwarm";
        vm.WorkingConfig.LocalSourceDir = "D:/swarmui";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void SaveCommand_AddMode_AppliesToSettings()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MySwarm";
        vm.WorkingConfig.Kind = "MySwarm";
        vm.WorkingConfig.LocalSourceDir = "D:/swarmui";
        vm.SaveCommand.Execute(null);
        Assert.True(s.Templates.ContainsKey("MySwarm"));
        Assert.True(vm.AppliedToSettings);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EditTemplateDialogViewModelTests"`
Expected: FAIL — class doesn't exist.

- [ ] **Step 3: Create EditTemplateDialogViewModel.cs**

Create `src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs`:

```csharp
using System;
using System.Linq;
using System.Windows.Input;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public enum EditTemplateDialogMode { Add, Edit }

/// <summary>
/// v1.0.0 multi-template: add or edit a single TemplateConfig. Backed by Settings.Templates.
/// View layer wires the XAML to this VM and raises ShowDialogRequested to actually show the window.
/// </summary>
public class EditTemplateDialogViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly Action<EditTemplateDialogViewModel>? _showDialogImpl;
    private string _originalKind = "";  // for edit mode: tracks the original kind to handle rename

    public EditTemplateDialogMode Mode { get; set; } = EditTemplateDialogMode.Add;
    public TemplateConfig WorkingConfig { get; private set; } = new();
    public bool AppliedToSettings { get; private set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Raised when the view should show itself. The view implementation creates the Window
    /// and calls back into the VM (Save / Cancel) on user interaction.
    /// </summary>
    public event Action<EditTemplateDialogViewModel>? ShowDialogRequested;

    public EditTemplateDialogViewModel(
        Settings settings,
        Action<EditTemplateDialogViewModel>? showDialogImpl)
    {
        _settings = settings;
        _showDialogImpl = showDialogImpl;
        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(Cancel);
    }

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(WorkingConfig.Name) &&
        !string.IsNullOrWhiteSpace(WorkingConfig.Kind) &&
        (Mode == EditTemplateDialogMode.Edit || !_settings.Templates.ContainsKey(WorkingConfig.Kind));

    public void LoadFrom(TemplateConfig existing)
    {
        _originalKind = existing.Kind;
        WorkingConfig = new TemplateConfig
        {
            Name = existing.Name,
            Kind = existing.Kind,
            LocalSourceDir = existing.LocalSourceDir,
            EntryScript = existing.EntryScript,
            EntryArgs = existing.EntryArgs,
            ModelsSubdir = existing.ModelsSubdir,
            ExtraJunctionTargets = new System.Collections.Generic.List<string>(existing.ExtraJunctionTargets),
            UserExtraArgs = existing.UserExtraArgs,
        };
        OnPropertyChanged(nameof(WorkingConfig));
        OnPropertyChanged(nameof(CanSave));
    }

    private void Save()
    {
        if (Mode == EditTemplateDialogMode.Edit && _originalKind != WorkingConfig.Kind)
        {
            // Kind renamed: remove old entry
            _settings.Templates.Remove(_originalKind);
        }
        _settings.Templates[WorkingConfig.Kind] = WorkingConfig;
        AppliedToSettings = true;
    }

    private void Cancel()
    {
        AppliedToSettings = false;
    }
}
```

- [ ] **Step 4: Create EditTemplateDialog.xaml + .cs**

Create `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml` (form with Name, Kind ComboBox [with editable text for custom], LocalSourceDir + Browse, EntryScript, EntryArgs multiline, ModelsSubdir, UserExtraArgs multiline, ExtraJunctionTargets list, Save/Cancel buttons).

Create `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml.cs` with the standard WPF Window code-behind that wires `SaveCommand` and `CancelCommand` to `DialogResult = true / false` and `Close()`.

The XAML form fields (mirror the spec §5 layout diagram):
- Name: TextBox bound to `WorkingConfig.Name`
- Kind: ComboBox with items "ComfyUI", "A1111", "Custom" + IsEditable=true for custom kinds
- LocalSourceDir: TextBox + Browse Button
- EntryScript: TextBox
- EntryArgs: TextBox (multiline, AcceptsReturn=true)
- ModelsSubdir: TextBox
- UserExtraArgs: TextBox (multiline)
- Save Button bound to `SaveCommand`
- Cancel Button bound to `CancelCommand` or just `IsCancel=true`

- [ ] **Step 5: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~EditTemplateDialogViewModelTests"`
Expected: 7 PASS / 0 FAIL.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EditTemplateDialogViewModelTests.cs
git commit -m "feat(v1.0.0): EditTemplateDialog (add/edit) for templates"
```

---

## Task 11: TemplateSourceUpdater generalization + tool menu removal

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs` → rename file to `TemplateSourceUpdater.cs`, accept `(string targetDir, string repoUrl, IProgress<string>? progress, CancellationToken ct)` params
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (remove tool menu "模板更新" entry; update DI ctor call sites)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateSourceUpdaterTests.cs`

**Interfaces:**
- Consumes: `TemplateConfig` from T1
- Produces: `TemplateSourceUpdater.UpdateAsync(targetDir, repoUrl, progress, ct)` — generalized

- [ ] **Step 1: Write the failing test**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateSourceUpdaterTests.cs`:

```csharp
using System.IO;
using System.Threading;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class TemplateSourceUpdaterTests : IDisposable
{
    private readonly string _workRoot;

    public TemplateSourceUpdaterTests()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "cmgr-tplsrd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workRoot);
    }

    [Fact]
    public void Ctor_AcceptsCustomTargetDir()
    {
        // generalization: ctor no longer hardcoded to <projectRoot>/ComfyUITemplate
        var updater = new TemplateSourceUpdater(gitExe: "git", gitProxy: null, logger: null);
        Assert.NotNull(updater);
    }

    [Fact]
    public void UpdateAsync_EmptyTargetDir_Validates()
    {
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: "",
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Ok);
        Assert.Contains("targetDir", result.Message);
    }

    [Fact]
    public void UpdateAsync_EmptyRepoUrl_Validates()
    {
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: Path.Combine(_workRoot, "x"),
            repoUrl: "",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        Assert.False(result.Ok);
        Assert.Contains("repoUrl", result.Message);
    }

    [Fact]
    public void UpdateAsync_ValidInputs_ReturnsResult()
    {
        // Smoke test: doesn't actually clone (no network in test), but verifies the
        // method doesn't throw and returns a result object.
        var updater = new TemplateSourceUpdater("git", null, null);
        var result = updater.UpdateAsync(
            targetDir: Path.Combine(_workRoot, "template"),
            repoUrl: "https://github.com/comfyanonymous/ComfyUI.git",
            progress: null,
            ct: default).GetAwaiter().GetResult();
        // Result can be Ok or Fail depending on env; just verify shape
        Assert.NotNull(result);
        Assert.NotNull(result.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateSourceUpdaterTests"`
Expected: FAIL — class doesn't exist (or has different ctor).

- [ ] **Step 3: Generalize ComfyUITemplateUpdater → TemplateSourceUpdater**

In `src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs`:

1. **Rename file** to `TemplateSourceUpdater.cs` (use `git mv`).
2. **Rename class** from `ComfyUITemplateUpdater` to `TemplateSourceUpdater`.
3. **Generalize signature**: `UpdateAsync(string targetDir, string repoUrl, IProgress<string>? progress, CancellationToken ct)`. Remove all hardcoded `projectRoot/ComfyUITemplate/` references and `comfyanonymous/ComfyUI.git` repo URL.
4. Keep the same git clone --depth=1 + TryDelete-each-entry + GitRunner pattern.
5. Return type: existing `NodeOperationResult` (or a new `TemplateUpdateResult` if the existing one doesn't fit — check). If a new result type is needed, define it in the same file.

- [ ] **Step 4: Update App.xaml.cs**

In `src-wpf/ComfyUI.Manager/App.xaml.cs`:

1. Find the line that constructs `ComfyUITemplateUpdater` (around line 295 per my prior read). Replace with:
   ```csharp
   var templateSourceUpdater = new TemplateSourceUpdater(gitExe, gitProxy, logger);
   ```
2. Find any tool menu "模板更新" entry (search the file for "ComfyUITemplateUpdater" or "模板更新"). Remove the menu item.
3. Find the `MainViewModel` ctor call that passes `templateUpdater: comfyUiTemplateUpdater` — rename to `templateSourceUpdater` (or `templateUpdater`).

- [ ] **Step 5: Run test to verify it passes**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo --filter "FullyQualifiedName~TemplateSourceUpdaterTests"`
Expected: 4 PASS / 0 FAIL.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs src-wpf/ComfyUI.Manager/Services/TemplateSourceUpdater.cs src-wpf/ComfyUI.Manager/App.xaml.cs tests-wpf/ComfyUI.Manager.Tests/Services/TemplateSourceUpdaterTests.cs
git commit -m "feat(v1.0.0): generalize ComfyUITemplateUpdater to TemplateSourceUpdater + drop tool menu entry"
```

---

## Task 12: SettingsView cleanup (remove TemplateComfyuiDir textbox) + final integration

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/Settings/SettingsView.xaml` (remove TemplateComfyuiDir textbox + Browse button)
- Modify: `src-wpf/ComfyUI.Manager/Views/Settings/SettingsView.xaml.cs` (remove BrowseTemplateComfyui handler if now dead)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (remove TemplateComfyuiDir setter + BrowseTemplateComfyui command)
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (remove `TemplateComfyuiDir` field — was kept temporarily for migration in T2)
- Run full test suite + integration test

- [ ] **Step 1: Modify SettingsView.xaml + .cs**

In `src-wpf/ComfyUI.Manager/Views/Settings/SettingsView.xaml`, find the "ComfyUI 模板目录" section (around line 435 per prior read). Remove the TextBox + Browse button + label.

If the BrowseTemplateComfyui click handler in the .cs file is now unused, remove it too.

- [ ] **Step 2: Modify SettingsViewModel.cs**

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`, find the `TemplateComfyuiDir` property setter (line 462 per prior read) and the `BrowseTemplateComfyui` command. Remove both.

- [ ] **Step 3: Remove `TemplateComfyuiDir` from Settings.cs**

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`, remove:
```csharp
[JsonPropertyName("template_comfyui_dir")] public string TemplateComfyuiDir { get; set; } = "";
```

The migration in `SettingsDefaults.TryMigrateOldTemplateComfyuiDir` (T2) was set up to read this field then clear it. With the field removed, also remove the `_ = s.TemplateComfyuiDir;` line in the migration helper — replace with a no-op (or just remove the migration helper entirely if the field is gone).

Actually, the migration MUST stay for users with old settings.json files. Update `TryMigrateOldTemplateComfyuiDir` to read the old JSON property via `JsonElement` rather than the now-removed `TemplateComfyuiDir` field. Use a separate DTO class for the old field, or use `JsonDocument.Parse(json)` to read it.

- [ ] **Step 4: Run full test suite**

Run: `cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --nologo`

Expected: 1700+ PASS / 2-4 FAIL pre-existing flaky / 6 SKIP (per G17). No new regressions.

- [ ] **Step 5: Manual GUI smoke test**

Test these flows in a launched dev build:

1. Fresh install (delete .manager/settings.json first) → 模板管理 sidebar → see ComfyUI + A1111 seeded.
2. 创建环境 → template picker shows ComfyUI + A1111 → select ComfyUI → auto-fills source path → 创建 → env dir gets main.py copied.
3. 创建环境 → select A1111 → fill Python path → 创建 → env dir gets webui.py copied (write a fake webui.py to A1111 template source first).
4. 模板管理 → "+ 添加模板" → name "MySwarmUI", Kind "Custom", Source "D:/somewhere-with-launch.sh" → save → 创建环境 → Custom kind shows in picker.
5. 模板管理 → edit ComfyUI template → change UserExtraArgs to "--preview-method auto" → save → next ComfyUI env start includes the extra arg.
6. 模板管理 → try to delete ComfyUI → button is disabled / blocked.
7. Old settings.json (with template_comfyui_dir) → launch app → old field auto-migrates to templates["ComfyUI"]; old field is gone from settings.json.
8. Old env rows (no template_kind) → env list loads, all show as ComfyUI with snapshot.

- [ ] **Step 6: Commit**

```bash
cd "D:/ToolDevelop/ComfyUI" && git add src-wpf/ComfyUI.Manager/Views/Settings/ src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs src-wpf/ComfyUI.Manager/Models/Settings.cs
git commit -m "feat(v1.0.0): remove SettingsView TemplateComfyuiDir (moved to Templates page) + drop field"
```

---

## Self-Review

**1. Spec coverage:** All spec sections covered:
- §2 (In-scope) → T1-T11 + T12 covers all listed items
- §3 (Data model) → T1 (TemplateConfig) + T2 (Settings.Templates) + T3 (Environment fields)
- §4 (File structure) → New + Modified files lists match tasks
- §5 (UI surfaces) → T7 (CreateEnvDialog) + T8-T10 (TemplateManagement page + EditTemplateDialog) + T8 (sidebar entry)
- §6 (Env creation) → T4 (always-copy)
- §7 (Env start) → T5 (per-kind entry)
- §8 (Models symlink) → T6 (per-kind subdir)
- §9 (Error handling) → covered in T2 (migration), T5 (template not found), T7 (unknown kind)
- §10 (Testing) → ~30 new tests across tasks; integration smoke in T12
- §11 (Migration) → T2 (settings field) + T3 (env rows) + T12 (cleanup old field)
- §12 (Out of scope) → explicitly excluded in G18

**2. Placeholder scan:** No TBD/TODO. All values from spec (e.g., `"main.py"`, `"--port {port} --listen 0.0.0.0"`, `"models/Stable-diffusion"`) are quoted verbatim.

**3. Type consistency:**
- `TemplateConfig` fields named in T1 and used identically in T2-T10.
- `Environment.TemplateKind` + `TemplateConfigSnapshot` named in T3 and used in T4-T8.
- `Settings.Templates` named in T2 and used in T3-T8.
- `TemplateSourceUpdater.UpdateAsync(targetDir, repoUrl, progress, ct)` signature consistent in T11.
- `EditTemplateDialogMode { Add, Edit }` enum used in T8 + T10.
- `TemplateManagementViewModel.IsBuiltIn(kind)` method consistent in T8 tests + production code.

No inconsistencies found.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-08-22-multi-template.md`. 12 tasks across 12 commits. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Each task = fresh implementer + spec-compliance review + code-quality review before next task. 12 implementer dispatches + 12 reviews + 1 final whole-branch review.

**2. Inline Execution** — I execute tasks in this session using the executing-plans skill, batched with checkpoints. Faster start, but my context window fills up over 12 tasks.

**Which approach?**
