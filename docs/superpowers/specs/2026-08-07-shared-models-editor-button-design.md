# v0.6.7.3 Shared Models 目录 + 编辑器按钮 设计 spec

> **For agentic workers:** This is a design spec. Read once before writing the implementation plan; do not modify without user approval.

## 1. Goal

解决两个用户痛点:

1. **多 env 共享同一份 Models 文件夹**(避免每建一个 env 复制几 GB 模型)。通过 junction(目录连接)+ `Settings.SharedModelsDirectory` 实现。所有 env 的 `<env-root>/ComfyUI/models` 都指向同一个全局路径,ComfyUI 启动时 `args.models_dir = <base>/models` 自动解析到共享路径。
2. **快速编辑 ComfyUI 配置**(目前只能用系统文件管理器手动找)。顶部「工具」菜单加 2 个按钮:打开 `<env-root>/ComfyUI/user/default/comfy.settings.json`,打开 `<env-root>/ComfyUI/extra_model_paths.yaml`,都通过系统默认关联程序(`Process.Start(UseShellExecute=true)`)。

**用户原话**:
> "在这里我们增加一个编辑器按钮，通常用于常规的项目编辑，另外在设置里面设置一个通用Models目录，所有的环境都会调用这个目录，不再需要在写入环境变量"
>
> "和当前虚拟环境一样，构建软连接方式，但是需要注意的是我们需要将Comfyui模板中的Models 文件夹删除，然后再构建"

## 2. Background

### 2.1 现状

- `Environment.ExtraModelPathsYaml` 字段(`Environment.cs:27-28`)是死代码:`EnvCreatorService.cs:155` 只写了一个 `# TODO: M1 填充\n` 占位,YAML 内容从未生效。
- 每个 env 的 `<env-root>/ComfyUI/models/` 都来自模板拷贝(in env-create 时 `JunctionLinker.CopyDirectory`,文件几百 MB 到几 GB),占盘严重。
- shared layout env 用 `JunctionLinker.CreateAsync` 把 `<env-root>/ComfyUI` junction 到共享 `<comfyui-source>`(复用同一份 ComfyUI 源码)。本 spec 复用同款 junction 思路用于 models 共享。
- 编辑器入口缺失:用户要改 `comfy.settings.json`(已经支持 locale 自动写入,v0.6.7.2)、`extra_model_paths.yaml` 都要去文件管理器找,体验差。

### 2.2 ComfyUI 端对 Models 路径的支持

经查 ComfyUI 源码(`comfyanonymous/ComfyUI/folder_paths.py:19-23` + `cli_args.py`):
- ComfyUI 通过 `args.models_directory`(CLI 参数 `--models-directory`)解析 models 目录
- 默认 `<comfyui-root>/models`(由 `args.base_directory` 决定)
- **不支持** `COMFYUI_MODELS_PATH` 环境变量(用户最初提议,已被否决)
- `extra_model_paths.yaml` + `--extra-model-paths-config` 是另一条路径,但 YAML 不在本题范围

### 2.3 v0.6.7.2 已落地基础设施(可复用)

- `ComfySettingsWriter.WriteLocale(comfyuiRoot, locale)` 写 `<comfyui-root>/user/default/comfy.settings.json` —— 类似的目录保证 + JSON roundtrip 模式可借鉴
- `ProcessLauncher` ctor 已支持多 optional string 参数(locale / timeout),可再加 `sharedModelsDirectory`
- `Settings.json` JSON 持久化 + `SettingsViewModel` INPC UI 模式可复用

## 3. Design

### 3.1 Settings 层

`Settings.cs` 加新字段:

```csharp
// v0.6.7.3: 全局共享 Models 目录(env-create / env-start 时
// 把 <env-root>/ComfyUI/models junction 到此路径)。
// 空字符串 = 不共享 Models,每 env 自己持有一份(向后兼容)。
[JsonPropertyName("shared_models_directory")]
public string SharedModelsDirectory { get; set; } = "";
```

`SettingsView.xaml` 在「外部冲突 API URL」之后加一行(textbox + 浏览按钮):

```xaml
<TextBlock Text="共享 Models 目录(留空 = 不共享)" Margin="0,8,0,4" />
<DockPanel Margin="0,2,0,0">
    <Button DockPanel.Dock="Right" Content="浏览..."
            Click="BrowseSharedModelsDirectory"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
    <TextBox Text="{Binding SharedModelsDirectory, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource MaterialTextBox}" />
</DockPanel>
<TextBlock Text="所有 env 的 ComfyUI/models 都 junction 到此目录;新建 env 自动生效,已建 env 启动时检查并重建。"
           Foreground="Gray" FontSize="11" Margin="0,2,0,0" TextWrapping="Wrap" MaxWidth="480"
           HorizontalAlignment="Left" />
```

`SettingsViewModel.cs` 加:

```csharp
public string SharedModelsDirectory
{
    get => _settings.SharedModelsDirectory;
    set { _settings.SharedModelsDirectory = value ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
}
```

`BrowseSharedModelsDirectory` 复用现有 `SettingsViewModel.PickFolder()` 模式。

### 3.2 env-create 阶段:链接共享 Models

`EnvCreatorService.CreateAsync` 在步骤 5(链接/复制 ComfyUI)**之后**、步骤 6(创建 venv)**之前**,加步骤 5.5:

```
5.5 链接共享 Models(若 SharedModelsDirectory 非空)
    - 如果 <env-root>/ComfyUI/models 已存在(junction 或目录),先删
      (shared layout 时这个是 junction 链回 <comfyui-source>/models;
       删 junction 不删源。independent 时是本地拷贝,删本地没事)
    - JunctionLinker.CreateAsync(<env-root>/ComfyUI/models, SharedModelsDirectory, ct)
    - 失败 → 抛 CREATE_MODELS_LINK_FAILED,EnvCreatorService catch 后回滚
      (跟 venv 失败同款:删 env 根目录)
```

伪代码(嵌入 EnvCreatorService.CreateAsync):

```csharp
// 5.5 链接共享 Models(若配置了 SharedModelsDirectory)
if (!string.IsNullOrWhiteSpace(_settings.SharedModelsDirectory))
{
    progress?.Report(new CreateStepReport("链接共享 Models",
        $"junction: {rootPath}/ComfyUI/models → {_settings.SharedModelsDirectory}"));
    var modelsLink = Path.Combine(comfyuiLink, "models");
    try
    {
        if (Directory.Exists(modelsLink))
        {
            // 删 junction(junction 上 rmdir 不删源)或目录(local copy)
            Directory.Delete(modelsLink, recursive: true);
        }
        await _linker.CreateAsync(modelsLink, _settings.SharedModelsDirectory, ct);
    }
    catch (Exception ex)
    {
        // 回滚:删 env 根目录,跟 venv 失败同款
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw new CreateEnvException("CREATE_MODELS_LINK_FAILED",
            $"Models junction 创建失败: {ex.Message}");
    }
}

// 6. 创建 venv (不变)
```

边界条件:
- `SharedModelsDirectory` 不存在 → `JunctionLinker.CreateAsync` 抛 `JunctionCreationException("target 不存在")`,被 catch 转 `CREATE_MODELS_LINK_FAILED` 整体回滚
- `SharedModelsDirectory` 是相对路径 → `JunctionLinker.CreateAsync` 用 `cmd /c mklink /D`,cmd 不解析相对路径,会失败。让 `EnvCreatorService` 在调用前做 `Path.GetFullPath(_settings.SharedModelsDirectory)` 规范化
- `SharedModelsDirectory` == env-local 路径(用户错配)→ junction 不会报错,但 ComfyUI 会读到自己的 models 子目录。**用户自负**;我们在说明文字里提示

### 3.3 env-start 阶段:启动前检查并重建 Models junction

`ProcessLauncher` ctor 加 optional 参数:

```csharp
public ProcessLauncher(
    string projectRoot,
    SqliteConnectionFactory dbFactory,
    IEnvironmentRepository envRepo,
    IProcessStateRepository processStateRepo,
    AppLogger? logger = null,
    int comfyUiStartupTimeoutSeconds = 600,
    string comfyUiLocale = "",
    string sharedModelsDirectory = ""   // 新增
)
```

`ProcessLauncher.StartEnvAsync(env, ...)` 在现有步骤之间(在写 comfy.settings.json locale **之前**或**之后**都行,建议 locale 之前,确保两个配置写入相邻):

```csharp
// 启动前检查 Models junction
if (!string.IsNullOrWhiteSpace(_sharedModelsDirectory))
{
    var modelsLink = Path.Combine(<comfyui-root>, "models");
    var needsRelink =
        !Directory.Exists(modelsLink) ||
        !TryGetJunctionTarget(modelsLink, out var existingTarget) ||
        !PathsEqual(existingTarget, _sharedModelsDirectory);
    if (needsRelink)
    {
        try
        {
            if (Directory.Exists(modelsLink))
                Directory.Delete(modelsLink, recursive: true);  // 删 junction 不删源
            _linker.CreateAsync(modelsLink, _sharedModelsDirectory).GetAwaiter().GetResult();
            _logger?.Info("env-start", $"重新链接 Models: {modelsLink} → {_sharedModelsDirectory}");
        }
        catch (Exception ex)
        {
            _logger?.Info("env-start", $"Models junction 重建失败(继续启动,Models 路径可能错): {ex.Message}");
            // 不阻塞启动(启动阶段失败回滚代价大,仅日志;用户在 ComfyUI 里看不到 Models)
        }
    }
}
```

**新工具方法**:
- `JunctionLinker.GetTargetAsync(linkPath)` 用 `cmd /c dir /AL <linkPath>` 解析 junction target(Windows 上 junction 显示为 `<JUNCTION>` + target 路径)
- `PathsEqual(a, b)`: 用 `Path.GetFullPath` + `string.Equals(OrdinalIgnoreCase)` 判等(Windows 不区分大小写)
- shared layout env 的 `<comfyui-root>` 由 `Path.GetDirectoryName(mainPy)!` 拿到(同 locale 写法),具体值:
  - shared layout:`<comfyui-source>`(junction 源)
  - independent layout:`<env-root>/ComfyUI`(env 本地)

**失败处理**:
- env-start 阶段 junction 重建失败 → **不阻塞启动**,仅 INFO 日志(用户在 ComfyUI 端可能看不到某些模型,但进程能起来)。这是有意权衡:启动阶段回滚代价大,links 失败不致命。

### 3.4 编辑器按钮:打开 ComfyUI 配置

`MainViewModel`(or 顶部菜单对应的 view-model)加 2 个命令:

```csharp
public RelayCommand OpenComfySettingsJsonCommand { get; }   // 工具 → 打开 ComfyUI 设置...
public RelayCommand OpenExtraModelPathsYamlCommand { get; } // 工具 → 打开 Models 配置...
```

实现要点:
- 接受参数 `Environment? env`(从 env-list 当前选中传入)。菜单不依赖 env — 但需要提示用户"对哪个 env"。
- **不依赖 env 的实现**:菜单按钮固定打 `<settings.SharedModelsDirectory>` 或 `<projectRoot>/<some-default-env>/ComfyUI/...`?
  - 实际:env-list 当前 `Selected` env 是唯一上下文来源。菜单按钮调 `OpenComfySettingsJsonCommand` 时:
    - 如果 `EnvironmentListViewModel.Selected != null` → 用这个 env 的 `<env-root>/ComfyUI`
    - 否则禁用 / 弹提示
- XAML:`MainWindow.xaml` 当前「工具」菜单是占位(`<MenuItem Header="_工具 (_T)">`),下挂这 2 个 menu-item
- 文件不存在 → 创建空文件(comfy.settings.json → `{}`,extra_model_paths.yaml → `# 配置 ComfyUI Models 路径\n`)
- `Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })` 用系统默认关联程序

**主菜单文件结构**(复用 v0.6.5.21 menu):

```xaml
<MenuItem Header="_工具 (_T)">
    <MenuItem Header="_打开 ComfyUI 设置..." Command="{Binding OpenComfySettingsJsonCommand}"
              InputGestureText="comfy.settings.json" />
    <MenuItem Header="_打开 Models 配置..." Command="{Binding OpenExtraModelPathsYamlCommand}"
              InputGestureText="extra_model_paths.yaml" />
    <Separator />
    <MenuItem Header="打开日志目录..." Command="{Binding OpenLogsDirectoryCommand}" /> <!-- bonus,顺手加 -->
</MenuItem>
```

**CanExecute gate**:命令的 `CanExecute` = `EnvironmentListViewModel.Selected != null`(Selected 是当前 env-list 选中行)。CommandManager 监听 `PropertyChanged(nameof(Selected))` 自动刷新。

### 3.5 v0.6.7.3 数据流总图

```
User 改 Settings.SharedModelsDirectory
    ↓ (SettingsViewModel.SharedModelsDirectory setter → Save(settings.json))
    ↓
新 env-create:
    EnvCreatorService.CreateAsync
        Step 5 (link/copy ComfyUI)
        Step 5.5 (link Models junction) ─── 失败 → CREATE_MODELS_LINK_FAILED → 回滚
        Step 6 (venv)
        Step 7 (write yaml placeholder)
        Step 8 (insert sqlite)
    ↓
已建 env 启动:
    ProcessLauncher.StartEnvAsync
        Step A (check & rebuild Models junction) ─── 失败 → INFO 日志,不阻塞
        Step B (write comfy.settings.json locale)
        Step C (Process.Start python main.py)

User 点菜单「工具 → 打开 ComfyUI 设置...」:
    MainViewModel.OpenComfySettingsJsonCommand
        env-list Selected = env-xxxxx
        确保 <env-root>/ComfyUI/user/default/comfy.settings.json 存在
        Process.Start(UseShellExecute=true) ← 系统默认关联程序(vscode/记事本/notepad++)
```

## 4. Files to Touch

| # | 文件 | 改动 |
|---|---|---|
| 1 | `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 加 `SharedModelsDirectory` 字段 |
| 2 | `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | 加 property + Browse 回调 |
| 3 | `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 加 TextBox + 浏览按钮 + 说明 |
| 4 | `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | 步骤 5.5:链接共享 Models + 失败回滚 |
| 5 | `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` | ctor 加参;StartEnvAsync 加 Models junction 检查重建 |
| 6 | `src-wpf/ComfyUI.Manager/Infrastructure/JunctionLinker.cs` | 加 `GetTargetAsync(linkPath)` 方法(`cmd /c dir /AL`) |
| 7 | `src-wpf/ComfyUI.Manager/App.xaml.cs` | 把 `settings.SharedModelsDirectory` 传给 launcher ctor |
| 8 | `src-wpf/ComfyUI.Manager/Views/MainWindow.xaml` | 「工具」菜单下挂 2 个 menu-item |
| 9 | `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(或新建 `MenuViewModel`) | 加 `OpenComfySettingsJsonCommand` / `OpenExtraModelPathsYamlCommand` |
| 10 | `src-wpf/ComfyUI.Manager/Views/EnvironmentListViewModel.cs`(or MainViewModel 代理) | 暴露 `Selected` 让命令 CanExecute 监听 |

### 新增测试文件

| 文件 | 估计测试数 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/JunctionLinkerTests.cs`(扩展) | +3 测试:`GetTargetAsync_ExistingJunction_ReturnsTarget` / `GetTargetAsync_RegularDirectory_ReturnsNull` / `GetTargetAsync_NonExistentPath_Throws` |
| `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceSharedModelsTests.cs` | +5 测试:`SharedModelsDirectorySet_JunctionsModels` / `SharedModelsEmpty_DoesNotTouchModels` / `SharedModelsDirectoryDoesNotExist_FailsAndRollsBack` / `IndependentLayout_AlsoJunctions` / `SharedLayout_ReplacesExistingJunction` |
| `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherSharedModelsTests.cs` | +3 测试:`StartEnvAsync_TargetMatches_DoesNotRelink` / `StartEnvAsync_TargetDiffers_Relinks` / `StartEnvAsync_EmptySetting_DoesNothing` |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelSharedModelsTests.cs` | +2 测试:`SharedModelsDirectory_SetterPersists` / `SharedModelsDirectory_DefaultIsEmpty` |

合计 +13 新测试。

## 5. Test Plan

### 5.1 单元测试(自动,本轮跑)

`dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
预期 ~620 PASS / 0 FAIL / 1 SKIP(607 + 13)

### 5.2 手动 GUI smoke(staging exe)

1. **Settings UI**:打开 Settings,看到新增「共享 Models 目录」行;textbox + 浏览;留空 + 保存 → `settings.json` 包含 `"shared_models_directory": ""`
2. **env-create 无共享**:留空 SharedModelsDirectory,新建 env → `<env-root>/ComfyUI/models/` 是本地拷贝(几百 MB),不是 junction
3. **env-create 共享**:SharedModelsDirectory 填一个已存在的空目录 → 新建 env → `<env-root>/ComfyUI/models` 是 junction(`dir /AL` 显示 `<JUNCTION> ...`)
4. **shared layout env 共享**:同步骤 3,但 layout = shared → junction 链回 `<comfyui-source>/models` 先被删(junction,删的是链),然后新建 junction 到 SharedModelsDirectory
5. **env-start 重建**:SharedModelsDirectory 已改,启动已建 env → 旧 junction target 不一致,被删重建;日志记录 `重新链接 Models: ...`
6. **env-start target 一致**:SharedModelsDirectory 未改 → junction 不重建
7. **失败回滚**:SharedModelsDirectory 指向不存在路径 → env-create 失败,env 根目录被整体删;SQLite 无残留行
8. **编辑器按钮**:env-list 选中 env → 点「工具 → 打开 ComfyUI 设置...」 → 系统默认关联程序(记事本/vscode)打开 `<env-root>/ComfyUI/user/default/comfy.settings.json`(locale 内容保留);点「工具 → 打开 Models 配置...」 → 打开 `<env-root>/ComfyUI/extra_model_paths.yaml`(`# TODO: M1 填充` 占位)
9. **编辑器按钮 选中为空**:没选 env 时点菜单 → 按钮 disabled,或弹提示"请先在环境列表选中一个 env"

## 6. Trade-offs + Risks

| 风险 | 缓解 |
|---|---|
| **shared layout + 删 `<env-root>/ComfyUI/models`** — 误删 `<comfyui-source>/models` | `Directory.Delete` 对 junction 是删 junction 不删源(Windows 行为);但我们要额外保证:**只在 env-create 步骤 5 之后做,且只针对该 env 的 `comfyuiLink` 路径**,不会跨 env 误删 |
| **SharedModelsDirectory 改路径后**,已建 env 的旧 junction target 还在 → 启动前必须重建 | 本 spec 已覆盖(env-start 检查 + 重建);失败仅日志不阻塞,设计权衡 |
| **junction 重建失败** | env-create 失败整体回滚(用户已确认);env-start 失败仅 INFO 日志(用户已确认) |
| **编辑器按钮 在 env-list 未选中时无法工作** | CanExecute gate + tooltip 提示 |
| **`ExtraModelPathsYaml` 字段现状**(`# TODO: M1 填充` 占位) | 本 spec 不动它;编辑器按钮是打开它让用户手动编辑用,不是用它做 Models 配置 |
| **`Settings.SharedModelsDirectory` 是相对路径** | EnvCreatorService / ProcessLauncher 内部 `Path.GetFullPath` 规范化 |
| **`Settings.SharedModelsDirectory` 路径含空格** | `JunctionLinker` 的 `cmd /c mklink /D` 已经加引号,handle 空格 |
| **跨平台** | 项目目前仅 Windows,`cmd /c mklink /D` + `dir /AL` 都 Windows-specific;macOS/Linux 不在本轮范围 |

## 7. Out of Scope

- 多于 1 个 SharedModelsDirectory(每个 env 不同 Models 目录)— 通过 per-env env var 实现,YAGNI
- 把 SharedModelsDirectory 改成 list(支持多个共享)— YAGNI
- 编辑器按钮的"用哪个编辑器"配置 — 默认走系统关联,vscode 已是默认
- env-list 行内「重新链接 Models」按钮 — 启动前自动重建已覆盖,YAGNI
- 「打开日志目录」等额外工具菜单项 — 见 §3.4 可选 bonus,本轮加 1 个(顺手,便于排查)
- `Environment.ExtraModelPathsYaml` 字段清理 — 留着(占位无害),不删

## 8. Version + Release

- **版本**:v0.6.7.3(独立版本号,跟 v0.6.7 / v0.6.7.1 / v0.6.7.2 一致 — 同属 "v0.6.7 post-release hotfix" 系列,都是用户即兴需求)
- **zip**:暂不 release(per `feedback_no_rebuild_zip.md` + `feedback_no_zip.md`)
- **staging**:本轮 ship 后 rebuild self-contained per `feedback_staging_self_contained.md`
- **commit 链**:预计 4-5 commits(每个文件一个 commit),base = `151f5ca`
- **memory**:更新 `project_v0_6_7_3_shared_models_editor.md` + `MEMORY.md`

## 9. References

- ComfyUI source: https://github.com/comfyanonymous/ComfyUI/blob/master/folder_paths.py (lines 19-23)
- ComfyUI CLI args: https://github.com/comfyanonymous/ComfyUI/blob/master/comfy/cli_args.py
- 项目 memory: `memory/MEMORY.md` (项目状态 + 偏好)
- 相关既有 spec:
  - `2026-08-06-top-menu-bar-design.md` (顶部菜单 + Alt 快捷键模式)
  - `2026-08-01-env-create-autofill-design.md` (EnvCreatorService 流程)
  - `2026-08-06-benv-profile-picker-design.md` (Settings dropdown 模式)

---

**Spec self-review**:

1. **Placeholder scan**: 无 TBD/TODO;每个字段、步骤、文件、测试都已具体化
2. **Internal consistency**: §3 步骤 5.5(env-create)和 §3.3(env-start)一致用 `JunctionLinker.CreateAsync`;失败处理分别按用户选择(env-create 整体回滚 / env-start 仅 INFO 日志)
3. **Scope check**: 2 个 feature(shared Models + 编辑器按钮)+ 1 个 helper(GetTargetAsync),聚焦,单一 plan 可涵盖
4. **Ambiguity check**:
   - "Models junction 检查重建"在 §3.3 明确:target 不一致时删重建
   - 编辑器按钮在 §3.4 明确:依赖 Selected env,文件不存在则创建空
   - 失败处理在 §3.2 / §3.3 / §6 各自明确

---

**Status**: ✅ Ready for user review