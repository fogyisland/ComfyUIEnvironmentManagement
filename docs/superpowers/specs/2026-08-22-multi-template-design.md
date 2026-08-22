# v1.0.0 Multi-Template Architecture (Templates + A1111 + Custom kinds)

> **Status:** DRAFT — awaiting user review before plan/implementation.
> **Preceded by:** v1.0.0 Phase 1 (dev mode unblock, SHIPPED `99df90c`).
> **Inspired by:** User request "是不是可以多一个模板管理,我们的模板包含多个不同的环境,例如环境1为ComfyUI  环境2 为Stable Diffusion" + "创建的时候选择相应的库开启" + later "第三种可能是 SwarmUI,我们后续可以通过模板管理手动加入源码,然后开发配置如何启动".

**Goal:** Decouple env creation from a single hardcoded `TemplateComfyuiDir` to a flexible template pool. Built-in kinds (ComfyUI + A1111) ship fully working; Custom kind lets users add arbitrary git-cloned source code (e.g., SwarmUI) via the new "Templates" sidebar page. Env creation always copies (no junction) into `<envsDir>/<envName>/` and stores a frozen `TemplateConfig` snapshot per env for reproducibility.

**Architecture:** New `TemplateConfig` class (string-keyed, no enum). `Settings.Templates` is `Dictionary<string, TemplateConfig>` replacing single `TemplateComfyuiDir`; `SettingsDefaults.Apply` seeds ComfyUI + A1111 entries on first run. New sidebar entry "模板管理" in MainWindow (`TemplateManagementView` + `TemplateManagementViewModel`) lists/adds/edits/deletes templates. New `EditTemplateDialog` for adding/editing individual templates (consistent with CreateEnvDialog pattern). `Environment` model gains `TemplateKind : string` + `TemplateConfig : TemplateConfig` (snapshot). `EnvCreatorService` always copies `LocalSourceDir → envsDir/envName/` (the shared/independent layout option is removed; user feedback "现在是将ComfyUI 不再通过连接方式,而是通过复制ComfyUI到对应的目录" = always copy). `ProcessLauncher` switches on env's `TemplateKind` to build the right entry command (`main.py --port --listen` vs `webui.py --port` + user extras). `ModelSymlinker` reads `TemplateConfig.ModelsSubdir` to know which subdir to scan/link.

**Tech stack:** WPF .NET 8 / C# 12 / xUnit / SQLite (unchanged). New `Models/TemplateConfig.cs`, `Services/TemplateConfigStore.cs`, `Views/TemplateManagement/TemplateManagementView.xaml`, `Views/TemplateManagement/EditTemplateDialog.xaml`. No new third-party dependencies.

**base SHA:** `99df90c` (post v1.0.0 Phase 1 dev mode unblock).

---

## 1. Background & user request

### Current template architecture

Today the "template" concept is a single string path `Settings.TemplateComfyuiDir` (persisted as `"template_comfyui_dir"`) pointing at `<projectRoot>/ComfyUITemplate/` — a git-cloned ComfyUI checkout updated by `ComfyUITemplateUpdater` (tool menu). Env creation in `EnvCreatorService.CreateAsync` either **junctions** (`<envRoot>/ComfyUI` → shared template dir, "shared" layout) or **copies** (`_linker.CopyDirectory`, "independent" layout) the template into the new env.

User's mental model has shifted: they always use "independent layout" (per-env copy, no shared mutable state) and now want to support **multiple distinct template kinds**, not just one ComfyUI template. User asked for Stable Diffusion (later confirmed as **A1111 WebUI** — different entry `webui.py`, different model dir layout `models/Stable-diffusion/` vs ComfyUI's `models/checkpoints/`, no `custom_nodes/` but has `extensions/`). User explicitly mentioned future **SwarmUI** support through "template management" — i.e., a workflow where they can add arbitrary source code + start config without code changes.

### User-clarified decisions (this brainstorm)

| Decision | User answer | Rationale |
|----------|-------------|-----------|
| Concrete SD project | **A1111 WebUI** | Most popular SD UI; distinct from ComfyUI layout |
| Abstraction level | **Mixed**: built-in kinds hardcoded, future Custom kinds via UI | Both stable (ComfyUI/A1111 tested) and flexible (SwarmUI/etc. user-added) |
| v1 scope | **Full**: ComfyUI + A1111 + Custom kind UI | Per user "全套" — wants v1 to actually work end-to-end |
| Layout model | **Per-env copy** (not junction) | User: "现在是将ComfyUI 不再通过连接方式,而是通过复制ComfyUI到对应的目录" — current independent layout becomes the only mode |
| UI for Custom templates | **New "模板管理" entry in left sidebar** | User: "模板管理在左侧菜单中提供" — separate top-level nav, not Settings sub-section |
| TemplateKind representation | **Pure string keys** | User: "完全字符串键" — env.TemplateKind = "ComfyUI" / "A1111" / "MySwarmUI"; Settings.Templates = Dictionary<string, TemplateConfig> |
| Env config persistence | **Frozen snapshot per env** (my recommendation, accepted silently) | Reproducible envs — updating template defaults doesn't change old envs |
| Settings UI organization | **Left sidebar nav entry** | Consistent with answer above |

---

## 2. Scope

### In scope (v1)

- **New `TemplateConfig` model class** (`Models/TemplateConfig.cs`) — name, kind (string), local source dir, entry script, entry args, models subdir, user extra args, extra junction targets.
- **`Settings.Templates` dictionary** replaces `TemplateComfyuiDir`. Migration: read old `template_comfyui_dir` → seed ComfyUI template entry with `LocalSourceDir = <old value or default>` → drop old field.
- **`SettingsDefaults.Apply`** seeds ComfyUI + A1111 template entries on first run (only fills `LocalSourceDir` if empty — never overwrites user customization).
- **New `Environment` fields**: `TemplateKind : string` + `TemplateConfigSnapshot : TemplateConfig` (snapshot at creation time). Old envs backfill `TemplateKind = "ComfyUI"` + snapshot from current settings on first load.
- **`EnvCreatorService.CreateAsync`** always copies `LocalSourceDir → <envsDir>/<envName>/`. The "shared/independent" `Layout` enum on `CreateEnvDialog` is **removed** (Layout field deleted). Junction option is gone.
- **`ProcessLauncher`** switches on env's `TemplateKind`: ComfyUI launches `python main.py --port {port} --listen 0.0.0.0`; A1111 launches `python webui.py --port {port}` + user extras; Custom kind reads entry script + args from env's snapshot.
- **`ModelSymlinker`** reads `env.TemplateConfigSnapshot.ModelsSubdir` to know which subdirectory to scan (`models/checkpoints/...` for ComfyUI, `models/Stable-diffusion/...` for A1111).
- **New sidebar nav entry "模板管理"** in `MainWindow.xaml` (9th RadioButton, parallel to "工作流市场" / "模型市场"). Backing view `TemplateManagementView` + `TemplateManagementViewModel`.
- **`TemplateManagementView`** lists templates as cards (Name + Kind + LocalSourceDir preview + Edit/Delete buttons). "+ 添加模板" button opens `EditTemplateDialog` in "add" mode.
- **`EditTemplateDialog`** for adding/editing a single template: Name (string), Kind (ComboBox — built-in options "ComfyUI" / "A1111" / "Custom"), LocalSourceDir (textbox + Browse), EntryScript (textbox, default per kind), EntryArgs (multiline, default per kind), ModelsSubdir (textbox, default per kind), UserExtraArgs (multiline, persists across sessions). Save validates required fields.
- **`CreateEnvDialog` RadioButton template kind picker** — replaces the "shared/independent" Layout radio buttons. RadioButton picks the template kind; rest of dialog auto-fills PythonExe + initial LocalSourceDir from the template. User can override source path before confirming.
- **Existing `Settings.TemplateComfyuiDir` removed from `SettingsView.xaml`** (path moves into the new Templates page, under ComfyUI template card).
- **Existing `ComfyUITemplateUpdater` (tool menu → 模板更新) replaced** with per-template update (each template card in the 模板管理 view has its own "更新源码" button using the same git clone --depth=1 logic, generalized to accept a target dir + repo URL from any template). The tool menu entry is removed (cleaner — Templates page is the canonical place).

### Out of scope (explicit)

- **SwarmUI / Forge / SD.Next as built-in kinds** — only Custom kind supported in v1. Users can configure SwarmUI manually as a Custom template (local source already cloned).
- **Auto-clone from git URL** — Custom kind requires user to point to a local checkout that already exists. Cloning is separate v1+ work ("template management" can be extended later to support git URL → clone flow).
- **A1111-specific Python deps management** — A1111 has its own `requirements_versions.txt` + `requirements.txt` chain. v1 uses the same `BaseEnvProfileLoader` flow as ComfyUI to install deps post-clone. Future work can add per-kind dependency overrides.
- **A1111 ComfyUI-Manager equivalent** — A1111 has no node manager. `ComfyUIManagerInstaller` / `CommonNodeInstaller` skip for non-ComfyUI kinds (decide on `env.TemplateKind == "ComfyUI"`).
- **Template version pinning** — no notion of "ComfyUI v0.3.30 template" vs "ComfyUI v0.4.0 template". Each template is the latest of whatever's checked out at `LocalSourceDir`. Per-env snapshot freezes the file state at env-creation time, not a version tag.
- **Multi-template-per-env** — one env = one template kind. No env that mixes ComfyUI + A1111 in one venv.
- **Template marketplace / remote template library** — local only. No "download template from gallery".
- **Per-env template switch** — once an env is created, its template is frozen. User can't change an existing ComfyUI env to A1111 (would require re-cloning + re-installing).

---

## 3. Data model

### `Models/TemplateConfig.cs` (new)

```csharp
public class TemplateConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // String ID, NOT enum. "ComfyUI" / "A1111" / "MySwarmUI" etc.
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    // Absolute or projectRoot-relative path to local source checkout.
    [JsonPropertyName("local_source_dir")] public string LocalSourceDir { get; set; } = "";
    // Path relative to env root, e.g. "main.py" or "webui.py".
    [JsonPropertyName("entry_script")] public string EntryScript { get; set; } = "";
    // Default CLI args. Supports {port} placeholder. e.g. "--port {port} --listen 0.0.0.0"
    [JsonPropertyName("entry_args")] public string EntryArgs { get; set; } = "";
    // Subdir under env root for models. "models" for ComfyUI (checkpoints go in models/checkpoints/),
    // "models/Stable-diffusion" for A1111 (A1111 puts all checkpoints directly under models/Stable-diffusion/).
    // Used by ModelSymlinker to know where to symlink downloaded models.
    [JsonPropertyName("models_subdir")] public string ModelsSubdir { get; set; } = "models";
    // Comma/space-separated extra dirs to junction-link from env to a global location.
    // Empty for built-in kinds.
    [JsonPropertyName("extra_junction_targets")]
    public List<string> ExtraJunctionTargets { get; set; } = new();
    // User-configurable extra args appended at start time. Mutable, per-template-global
    // (not per-env). NOT snapshotted into env — env freezes EntryScript + EntryArgs +
    // ModelsSubdir + ExtraJunctionTargets at creation time. UserExtraArgs is "live":
    // user can tweak in the Templates page and it applies to all envs of that kind
    // on next start. Per-env override of UserExtraArgs is NOT in v1.
}
```

### `Models/Settings.cs` (modified)

```csharp
// REMOVE:
[JsonPropertyName("template_comfyui_dir")] public string TemplateComfyuiDir { get; set; } = "";

// ADD:
[JsonPropertyName("templates")]
public Dictionary<string, TemplateConfig> Templates { get; set; } = new();
```

### `Models/Environment.cs` (modified)

```csharp
// ADD:
[JsonPropertyName("template_kind")] public string TemplateKind { get; set; } = "ComfyUI";
[JsonPropertyName("template_config_snapshot")]
public TemplateConfig? TemplateConfigSnapshot { get; set; }
```

### Migration

- `SettingsRepository.Load`: if `templates` field is empty AND old `template_comfyui_dir` field exists → seed `templates["ComfyUI"] = new TemplateConfig { Name="ComfyUI", Kind="ComfyUI", LocalSourceDir=<old value>, EntryScript="main.py", EntryArgs="--port {port} --listen 0.0.0.0", ModelsSubdir="models" }` → drop old field.
- `SettingsDefaults.Apply`: if `templates` is empty → seed ComfyUI + A1111 entries with default `LocalSourceDir` pointing to `<projectRoot>/Templates/ComfyUI/` and `<projectRoot>/Templates/A1111/` respectively. Don't overwrite existing entries (only fill missing fields).
- `EnvironmentRepository.LoadAll`: for envs missing `template_kind` or `template_config_snapshot` → backfill `template_kind = "ComfyUI"` + snapshot from current `settings.Templates["ComfyUI"]`. Persist back to SQLite on next save.

---

## 4. File structure

### New files

- `src-wpf/ComfyUI.Manager/Models/TemplateConfig.cs` — TemplateConfig class + serialization
- `src-wpf/ComfyUI.Manager/Services/TemplateConfigStore.cs` — central helper for reading/writing templates, applying defaults, kind metadata
- `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs` — built-in ComfyUI + A1111 default configs (immutable singletons)
- `src-wpf/ComfyUI.Manager/ViewModels/TemplateManagementViewModel.cs` — sidebar page VM
- `src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs` — add/edit dialog VM
- `src-wpf/ComfyUI.Manager/Views/TemplateManagement/TemplateManagementView.xaml` + `.cs` — sidebar page UI
- `src-wpf/ComfyUI.Manager/Views/TemplateManagement/EditTemplateDialog.xaml` + `.cs` — add/edit dialog

### Modified files

- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — drop `TemplateComfyuiDir`, add `Templates` dictionary
- `src-wpf/ComfyUI.Manager/Models/Environment.cs` — add `TemplateKind`, `TemplateConfigSnapshot`
- `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` — seed ComfyUI + A1111, migrate old field
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` — always copy, remove Layout branching, take TemplateKind param
- `src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs` — switch on env.TemplateKind for entry script + args
- `src-wpf/ComfyUI.Manager/Services/ModelSymlinker.cs` — read env.TemplateConfigSnapshot.ModelsSubdir
- `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` — drop Layout, add TemplateKind picker
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` — remove Layout radio buttons, add TemplateKind picker
- `src-wpf/ComfyUI.Manager/Views/Settings/SettingsView.xaml` — remove TemplateComfyuiDir textbox
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` — add 9th sidebar RadioButton "模板管理"
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — wire up TemplateManagementView
- `src-wpf/ComfyUI.Manager/Services/ComfyUITemplateUpdater.cs` — generalize to TemplateSourceUpdater (target dir + repo URL params)

### Test files

- `tests-wpf/ComfyUI.Manager.Tests/Models/TemplateConfigTests.cs` — serialization round-trip, defaults
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTemplateSeedTests.cs` — first-run seeding, migration from old field, no-overwrite-user-fields
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceMultiTemplateTests.cs` — ComfyUI + A1111 env creation
- `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherTemplateKindTests.cs` — switch on kind builds correct args
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogTemplateKindTests.cs` — picker populates source + python defaults
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/TemplateManagementViewModelTests.cs` — list/add/edit/delete template flows
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EditTemplateDialogViewModelTests.cs` — validation + apply template

---

## 5. UI surfaces

### Sidebar nav

`MainWindow.xaml` — add 9th RadioButton parallel to existing "工作流市场" (8th) and "模型市场":

```xml
<RadioButton Content="模板管理"
        Command="{Binding ShowTemplateManagementCommand}"
        GroupName="SidebarNav"
        Tag="templates"/>
```

`MainViewModel.cs` — new `ShowTemplateManagementCommand` + `TemplateManagementView` instance + `TemplateManagementViewModel` ctor.

### TemplateManagementView layout

```
┌───────────────────────────────────────────────────────────────┐
│ 模板管理                              [+ 添加模板]              │
├───────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ ComfyUI                                                  │ │
│ │ Kind: ComfyUI   Source: <projectRoot>/Templates/ComfyUI/ │ │
│ │ Entry: main.py --port {port} --listen 0.0.0.0           │ │
│ │ Models: models                                           │ │
│ │ Extra args: (empty)                                      │ │
│ │ [更新源码] [编辑] [删除]                                  │ │
│ └──────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ A1111                                                    │ │
│ │ Kind: A1111    Source: <projectRoot>/Templates/A1111/    │ │
│ │ Entry: webui.py --port {port}                            │ │
│ │ Models: models                                           │ │
│ │ Extra args: (empty)                                      │ │
│ │ [更新源码] [编辑] [删除]                                  │ │
│ └──────────────────────────────────────────────────────────┘ │
│ (no Custom templates yet)                                      │
└───────────────────────────────────────────────────────────────┘
```

### EditTemplateDialog layout

```
┌──────────────────────────────────────────────────────────────┐
│ 添加模板 / 编辑模板                                            │
├──────────────────────────────────────────────────────────────┤
│ 名称:    [___________________________]                        │
│ 类型:    [ ComfyUI ▼ ]  (built-in options: ComfyUI, A1111,    │
│                          Custom)                              │
│ 源码:    [______________________] [Browse...]                │
│ 入口脚本: [______________________]                            │
│ 入口参数: [______________________]                            │
│          (多行,supports {port})                               │
│ 模型目录: [______________________]                            │
│ 额外参数: [______________________]                            │
│          (运行时附加,不进 snapshot)                            │
│ 额外 junction 目标:                                          │
│   [______________________________] [+]                       │
│   [______________________________] [-]                       │
│                                                               │
│                              [取消]  [保存]                   │
└──────────────────────────────────────────────────────────────┘
```

### CreateEnvDialog layout (modified)

```
┌──────────────────────────────────────────────────────────────┐
│ 创建环境                                                       │
├──────────────────────────────────────────────────────────────┤
│ 名称:    [___________________________]                        │
│ 模板:    (•) ComfyUI                                           │
│          ( ) A1111                                             │
│          ( ) MySwarmUI                                         │
│ 源码:    [______________________] [Browse...]                │
│          (auto-filled from selected template, editable)       │
│ Python:  [______________________] [Browse...]                │
│ 端口:    [____]                                                │
│ 备注:    [___________________________]                        │
│                                                               │
│                              [取消]  [创建]                   │
└──────────────────────────────────────────────────────────────┘
```

(Layout radio "shared/independent" removed.)

---

## 6. Env creation flow (modified)

`CreateEnvDialogViewModel.OnConfirm`:
1. Validate `Name`, `PythonExe`, `SelectedTemplateKind`.
2. Look up `Settings.Templates[SelectedTemplateKind]` — if not found, error "模板不存在".
3. Clone that template's `TemplateConfig` as `snapshot`.
4. Call `EnvCreatorService.CreateAsync(name, snapshot, pythonExe, port, notes, ct)`.

`EnvCreatorService.CreateAsync`:
1. Validate env name + python exe + snapshot.
2. Create `<envsDir>/<envName>/` directory.
3. Call `_linker.CopyDirectory(snapshot.LocalSourceDir, <envsDir>/<envName>/)`. (Always copy. No junction.)
4. Create `<envsDir>/<envName>/venv/` via `_venvCreator.CreateAsync(pythonExe, venvPath)`.
5. Persist env record with `TemplateKind = snapshot.Kind`, `TemplateConfigSnapshot = snapshot`, `Status = "stopped"`.
6. Return env record.

(ComfyUI-Manager install + CommonNodes install — currently unconditional — must now check `env.TemplateKind == "ComfyUI"` and skip for A1111/Custom.)

---

## 7. Env start flow (modified)

`ProcessLauncher.StartEnvAsync(env)`:
1. Read `env.TemplateConfigSnapshot` (fallback to `Settings.Templates[env.TemplateKind]` if snapshot missing — backward compat).
2. Resolve `EntryScript` path: `<envRoot>/<EntryScript>` (EntryScript is relative to env root).
3. Resolve `EntryArgs`: substitute `{port}` placeholder. Append `Settings.Templates[env.TemplateKind].UserExtraArgs` (if not empty).
4. Use venv python: `<envRoot>/venv/Scripts/python.exe` (Windows) or `bin/python` (Unix).
5. Spawn process: `<venvPython> <EntryScript> <EntryArgs>` with cwd = `<envRoot>`.

Readiness detection: keep existing logic (probe `localhost:{port}` HTTP). Different process name in logs is fine (just shows the actual exe).

---

## 8. Models symlink flow (modified)

`ModelSymlinker.SyncAsync(env)`:
1. Read `env.TemplateConfigSnapshot.ModelsSubdir` (default "models" if missing).
2. Scan `<Settings.DefaultModelsDirectory>/<ModelsSubdir>/...` for versioned models.
3. Symlink each model version into `<envRoot>/<ModelsSubdir>/<kind>/<slug>__<vid8>`.
4. For A1111: subdir is `models/Stable-diffusion/` (or however user configured it in `ModelsSubdir`).
5. For ComfyUI: subdir is `models/checkpoints/` etc. — current behavior preserved when ModelsSubdir is just "models".

(Behavior change: today ModelSymlinker hardcodes `models/` as the env-side dir. v1 makes the env-side dir = `env.TemplateConfigSnapshot.ModelsSubdir`. For backward compat, if `ModelsSubdir` is unset or "models", behavior is unchanged.)

---

## 9. Error handling

- **`Settings.Templates[kind]` not found at env create/start**: error "模板 '{kind}' 不存在,可能在 Settings 中已被删除"。
- **`LocalSourceDir` doesn't exist at env create**: error "模板源码目录不存在: {path}" with suggestion to use 模板管理 → 更新源码.
- **`EntryScript` not found at env start**: error "入口脚本不存在: <envRoot>/<EntryScript>".
- **Built-in template deletion**: disable Delete button on ComfyUI/A1111 cards (built-in = protected). User can edit `LocalSourceDir` and `UserExtraArgs` but not delete the entry.
- **Duplicate template name**: EditTemplateDialog validates unique Name across all templates.

---

## 10. Testing

### Unit tests (focused, fast)

- `TemplateConfigTests`: JSON round-trip with all fields, defaults applied.
- `SettingsDefaultsTemplateSeedTests`: empty Settings gets ComfyUI + A1111 seeded; existing templates not overwritten; old `template_comfyui_dir` migrates to `templates["ComfyUI"]`.
- `EnvCreatorServiceMultiTemplateTests`: ComfyUI env created with correct snapshot; A1111 env created with correct snapshot; Custom env with user-defined entry script; env persisted with snapshot intact.
- `ProcessLauncherTemplateKindTests`: ComfyUI launches with `main.py --port X --listen 0.0.0.0`; A1111 launches with `webui.py --port X` + user extras appended; Custom kind uses snapshot's entry.
- `CreateEnvDialogTemplateKindTests`: picker selection auto-fills `ComfyuiSource` (renamed to `TemplateSource`); validation rejects unknown kind.
- `TemplateManagementViewModelTests`: list loaded from Settings; add opens dialog with empty fields; edit loads existing values; delete removes from Settings; built-in ComfyUI/A1111 blocked from delete.
- `EditTemplateDialogViewModelTests`: validation (Name non-empty, Kind non-empty, LocalSourceDir exists); save commits to Settings.Templates.

### Integration tests

- `EnvironmentRepositoryMigrationTests`: old env rows (no `template_kind`) get backfilled `template_kind = "ComfyUI"` + snapshot from current Settings on first load.

### Manual GUI smoke (post-merge)

1. Fresh install → open 模板管理 → see ComfyUI + A1111 seeded.
2. 创建环境 → template picker shows ComfyUI + A1111 + (Custom if added) → select ComfyUI → auto-fills source path.
3. 创建环境 → select A1111 → fill Python path → 创建 → env dir gets webui.py copied → start → A1111 launches.
4. 模板管理 → add Custom template → name "MySwarmUI" → fill source path + entry script → save → 创建环境 → Custom kind shows in picker → create → env starts with custom entry.
5. Update ComfyUI template (模板管理 → 更新源码) → 创建一个新 env → new env gets fresh source; existing envs unaffected (snapshot frozen).

### Regression check

- Full suite target: 1700+ PASS / 6 FAIL pre-existing flaky / 6 SKIP (baseline 1675/2/6 + ~25-30 new tests for template layer).

---

## 11. Migration & compatibility

### Backward compat

- Old `Settings.TemplateComfyuiDir` field: read once, migrate to `Templates["ComfyUI"]`, drop. Settings.json file stays valid.
- Old `Environment` rows (no `template_kind` column): backfill on first load, persist on next save. No data loss.
- Old `ComfyUITemplateUpdater` (tool menu): replaced with per-template update buttons in 模板管理 view. Tool menu entry removed. (Alternative: keep tool menu entry that updates the FIRST built-in kind, but cleaner to remove since the Templates page is the canonical place.)
- Old `CreateEnvDialog` "Layout" radio (shared/independent): removed. Existing user choice is irrelevant — default is now always copy. If user's last dialog state had `Layout=independent`, that selection is silently ignored (default to copy).

### Forcing migration

- Settings.json migration runs in `SettingsRepository.Load()` (existing path) — auto-applies on first launch with v1.
- No manual user action required beyond "launch the new version".

---

## 12. Out-of-scope reminders (recap from §2 — don't accidentally include)

- SwarmUI / Forge / SD.Next as built-in kinds
- Auto-clone from git URL in Custom kind
- A1111-specific dependency overrides
- A1111 ComfyUI-Manager equivalent (skip ComfyUI-Manager / CommonNodes for non-ComfyUI kinds)
- Template version pinning
- Multi-template-per-env (one env = one kind)
- Template marketplace / remote template library
- Per-env template switch (frozen at creation)