# 新建环境 Auto-Fill from Settings — Design

> **Spec status:** Draft — 待用户复核
> **Date:** 2026-08-01
> **Brainstorm origin:** 用户原话:"新建环境的时候,如果是 shared 布局,我们这里的 python 解释器和 Comfyui 的模板能够从设置中带出来"
> **Target release:** v0.6.5.4 (hotfix)

## 0. 目标 & 范围

`CreateEnvDialog` 打开后,Python 解释器 / ComfyUI 源两个字段当前是空的,即使
settings 里 `TemplatePythonDir` / `TemplateComfyuiDir` 已经配好。新功能让用户从
settings 带出常用模板路径,避免每次手填。

**In scope:**
- dialog 初次打开根据当前 `Layout`(shared 或 independent)auto-fill `PythonExe` +
  `ComfyuiSource`
- 一个 **"应用模板"** 按钮,用户可手动重新从 settings 拉
- 新增 `Settings.DefaultPythonVersion` 字段(默认 `"3.10"`),用于解析
  `<TemplatePythonDir>/<DefaultPythonVersion>/python.exe`
- 模板缺失时静默留空 + dialog 顶部黄色提示
- Settings 页加一行让用户改 `DefaultPythonVersion`

**Out of scope:**
- 不修改 `EnvCreatorService` / `VenvCreator` / `JunctionLinker` 任何现有逻辑
- 不监听 `Layout` ComboBox 切换事件
- 不持久化"上次使用的 python 版本"
- 不动 Settings 现有 UI(只追加 `DefaultPythonVersion` 一行)
- 不动 BED / Catalog / 其他页面

## 1. 数据源:Settings 字段

`Models/Settings.cs` 当前已有两个相关字段:

| 字段 | 含义 | 默认值 |
|---|---|---|
| `TemplatePythonDir` | Python 模板根目录(相对 projectRoot,含 `3.10/3.11/3.12/` 等版本子目录) | `"python"` |
| `TemplateComfyuiDir` | ComfyUI 模板目录(相对 projectRoot) | `"ComfyUI"` |

新增一个字段:

| 字段 | 含义 | 默认值 |
|---|---|---|
| `DefaultPythonVersion` | auto-fill 时选哪个版本子目录 | `"3.10"` |

**为什么需要 `DefaultPythonVersion`:** `TemplatePythonDir` 是个含多个版本子目录
的根,auto-fill 时必须确定具体 `<version>/python.exe` 子目录。Settings 不应
直接存单个 python 路径(会随 python 升级而过期),存一个版本号更稳定。

## 2. 组件

### 2.1 Settings 加字段
`src-wpf/ComfyUI.Manager/Models/Settings.cs`:
```csharp
public string DefaultPythonVersion { get; set; } = "3.10";
```

### 2.2 SettingsView 加一行 UI
`src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`:在 `TemplatePythonDir` /
`TemplateComfyuiDir` 两行下方加一行,绑 `DefaultPythonVersion`(ComboBox 选项
`3.10` / `3.11` / `3.12` / `3.13` + 用户可手填)。

`SettingsViewModel` 加 `DefaultPythonVersion` passthrough 属性,settings 改动
时直接写回 `_settings.DefaultPythonVersion`。

### 2.3 CreateEnvDialogViewModel 改造
当前 ctor:
```csharp
public CreateEnvDialogViewModel(EnvCreatorService creator)
```
改为:
```csharp
public CreateEnvDialogViewModel(
    EnvCreatorService creator,
    Settings settings,
    string projectRoot)
```

加字段:
- `string? TemplateWarningMessage { get; private set; }` — 顶部黄色提示文字,空时不显示
- `IRelayCommand ApplyTemplateCommand { get; }` — "应用模板" 按钮绑的命令

加方法:
```csharp
public void ApplyTemplate()
{
    var pythonExe = Path.Combine(
        _projectRoot,
        _settings.TemplatePythonDir,
        _settings.DefaultPythonVersion,
        "python.exe");
    var comfyuiSource = Path.Combine(
        _projectRoot,
        _settings.TemplateComfyuiDir);

    var warnings = new List<string>();

    if (File.Exists(pythonExe))
    {
        PythonExe = pythonExe;
    }
    else
    {
        warnings.Add(
            $"Python 模板 {_settings.DefaultPythonVersion} 未安装,请先在设置页下载");
        PythonExe = "";
    }

    if (Directory.Exists(comfyuiSource))
    {
        ComfyuiSource = comfyuiSource;
    }
    else
    {
        warnings.Add("ComfyUI 模板目录未安装,请先在设置页下载");
        ComfyuiSource = "";
    }

    TemplateWarningMessage = warnings.Count == 0
        ? null
        : string.Join("\n", warnings);
    OnPropertyChanged(nameof(TemplateWarningMessage));
}
```

ctor 末尾调 `ApplyTemplate()` 一次完成初次填充。

### 2.4 XAML 改动
`src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`:
- 在 Name 字段下方加:
  ```xml
  <TextBlock Text="{Binding TemplateWarningMessage}"
             Foreground="Orange"
             Margin="0,4,0,8"
             TextWrapping="Wrap" />
  ```
  (WPF 默认 `null` 绑定会显示空字符串而非 Collapsed,需要 converter;
  沿用项目里已有的 `NullToVisibilityConverter` 或自写一个 null→Collapsed。)
- 在 Python / ComfyUI 字段行(旁边)加一个按钮:
  ```xml
  <Button Content="应用模板"
          Command="{Binding ApplyTemplateCommand}"
          Padding="8,4" Margin="4,0,0,0" />
  ```

### 2.5 CreateEnvDialog.xaml.cs 签名改
当前:
```csharp
public static void Show(EnvCreatorService creator)
```
改为:
```csharp
public static void Show(EnvCreatorService creator, Settings settings, string projectRoot)
```
内部 VM 构造从 `new CreateEnvDialogViewModel(creator)` 改为
`new CreateEnvDialogViewModel(creator, settings, projectRoot)`。

### 2.6 MainViewModel 调用点改
`src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` 中所有
`CreateEnvDialog.Show(...)` 调用点:
- `ShowEnvironments` 命令附近 — 已有 settings 引用
- `OpenBulkUpdate` 命令附近 — bulk 走 EnvCreatorService 不走 dialog,无需改
- 新建 env 入口处加 `projectRoot` 参数(从 ctor 注入,或从 processPath 解析)

## 3. 数据流

```
User clicks "新建环境" in EnvListView
  ↓
MainViewModel.ShowEnvironmentsCommand
  ↓
CreateEnvDialog.Show(creator, settings, projectRoot)
  ↓
CreateEnvDialogViewModel ctor → ApplyTemplate()
  ↓
ApplyTemplate reads settings + projectRoot
  ↓
Fills PythonExe + ComfyuiSource + (optional) TemplateWarningMessage
  ↓
Dialog shown — fields pre-populated, user can edit or click "应用模板" to refetch
  ↓
User clicks "创建" → EnvCreatorService.CreateAsync (unchanged)
```

## 4. 错误处理

| 场景 | 行为 |
|---|---|
| `TemplatePythonDir` / `DefaultPythonVersion` / `TemplateComfyuiDir` 全部存在 | `PythonExe` + `ComfyuiSource` 都填,`TemplateWarningMessage = null`(黄色提示不显示) |
| Python 模板缺失 | `PythonExe = ""`,提示"Python 模板 X.Y 未安装,请先在设置页下载" |
| ComfyUI 模板缺失 | `ComfyuiSource = ""`,提示"ComfyUI 模板目录未安装,请先在设置页下载" |
| 两个都缺失 | 两个提示合并,`TemplateWarningMessage = "Python 模板 X.Y 未安装...\nComfyUI 模板目录未安装..."` |
| `DefaultPythonVersion` 用户填了一个不存在的子目录(如 `"3.99"`) | 走"Python 模板缺失"分支(行为一致) |

`PythonExe` / `ComfyuiSource` 留空后,`EnvCreatorService.CreateAsync` 现有
validation 会拦下来报"路径不存在",UX 流程自然衔接。

## 5. 测试

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`
新增(沿用现有测试文件,如不存在则创建):

1. `ApplyTemplate_PopulatesPythonExe_WhenTemplateExists`
2. `ApplyTemplate_PopulatesComfyuiSource_WhenTemplateExists`
3. `ApplyTemplate_LeavesPythonExeBlank_WhenTemplateMissing`
4. `ApplyTemplate_LeavesComfyuiSourceBlank_WhenTemplateMissing`
5. `ApplyTemplate_SetsWarning_WhenPythonTemplateMissing`
6. `ApplyTemplate_SetsWarning_WhenComfyuiTemplateMissing`
7. `ApplyTemplate_RespectsDefaultPythonVersion`(用 `3.11` 验证路径拼接)
8. `ApplyTemplateCommand_ReappliesTemplate`(手动改字段后调命令,验证被覆盖)
9. `Constructor_AppliesTemplateOnInit`(验证 ctor 自动调了一次 ApplyTemplate)

测试不依赖真实文件 IO,可以用 `FakeSettings` 或一个测试 Settings 实例 +
项目里已有的 temp dir 工具。

## 6. 不破坏现有

- 用户已能手动填所有字段,新功能是**追加**而非替换 — 现有手动 UX 完整保留
- `Layout` ComboBox 切换**不**触发 auto-fill(用户决策,见 spec §0 范围)
- `EnvCreatorService.CreateAsync` 签名 / 行为 / validation 一字不动
- `CreateEnvDialog.xaml` 现有 5 字段布局不改顺序,只在顶部插入提示 + 字段旁加按钮

## 7. 验证

- **dotnet test:** 273/1/0(基线 v0.6.5.3)→ +9 个新 test,期望 282/1/0
- **pytest version consistency:** 3 PASS(v0.6.5.3 → v0.6.5.4)
- **dotnet build Release:** 0 warnings / 0 errors
- **手动 GUI smoke:**
  1. 启动 WPF → 侧边栏 → 环境 → 新建环境
  2. 验证 `PythonExe` + `ComfyuiSource` 已 auto-fill(模板齐的情况下)
  3. 改 `DefaultPythonVersion` 到一个有对应子目录的版本(如 `3.11`),回到新建 dialog,点"应用模板",验证 `PythonExe` 跟着改;若 `DefaultPythonVersion` 改成不存在的版本,顶部应出现黄色提示
  4. 删 Python 模板的 `3.10/` 子目录,重启 WPF,新建 dialog 顶部应有黄色提示
  5. shared + independent 切换 layout 字段**不**变(决策 2)

## 8. 不做

- 不在 dialog 里加 "下载 Python 模板" / "下载 ComfyUI 模板" 按钮 — 那是 Settings 页的事
- 不持久化"上次实际使用的 Python 版本" — 用户改 settings 即可
- 不监听 `Layout` ComboBox 变化(决策 2)
- 不抽 `EnvTemplateAutoFillService`(决策 5,YAGNI)