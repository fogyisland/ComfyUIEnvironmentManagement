# v0.6.5.6 — Settings 多 Python 解释器管理

> **Status:** Draft (brainstorm §1-§4 approved 2026-08-03)
> **Target:** v0.6.5.6 hotfix (post v0.6.5.5)
> **Base SHA:** `6d4d211` (v0.6.5.5 已发布)
> **Author:** Sonnet + 用户

---

## §0 背景与目标

### 当前行为(v0.6.5.5)

`Settings` 面板只有一条路径:
- `TemplatePythonDir`(单字符串):模板 Python 根目录
- `DefaultPythonVersion`(单字符串,默认 `"3.10"`):auto-fill 时取 `TemplatePythonDir/{DefaultPythonVersion}/python.exe`

`CreateEnvDialog` 打开时:
- 有 recent base(v0.6.5.5 新增)→ 用 recent base
- 否则 → 用 `TemplatePythonDir/DefaultPythonVersion/python.exe`
- 缺失 → 顶部黄色提示"Python 模板 3.10 未安装,请先在设置页下载"

### 问题

用户管理多个 Python 解释器(3.10 / 3.11 / 3.12 / 系统自带 / 便携版)时:
1. 单条 `TemplatePythonDir` 只能容纳一个根目录,跨 root 的解释器无法共存
2. `DefaultPythonVersion` 单一字符串,只能选一个 active
3. 编辑/切换需要手动改文本框,易出错
4. v0.6.5.5 recent base 虽能"继承上一个",但首次安装或换 root 后仍需手动配

### 目标

Settings 面板新增 "Python 解释器" 区段,以"完整解释器条目"(Name + Path)列表管理多个 Python 解释器,选一个为"当前使用"。`CreateEnvDialog` 的 auto-fill 改用 active 那条的 Path,不再依赖 `TemplatePythonDir + DefaultPythonVersion` 拼接。

老 settings.json 升级时**自动迁移**出一条默认条目,老字段保留读不写(UI 展示为只读 label)。

---

## §1 数据模型

### `Models/Settings.cs` 新增

```csharp
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

### 老字段保留(读不写)

```csharp
[JsonPropertyName("template_python_dir")] public string TemplatePythonDir { get; set; } = "";
[JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";
```

- JSON 序列化为 `template_python_dir` / `default_python_version`,加载时仍读
- v0.6.5.6 起**只写不读**:Settings 面板展示为只读 label,提示用户改用新列表
- 现有 v0.6.5.5 settings.json 加载时不再拼接这两个字段

---

## §2 组件

### §2.1 `Services/PythonInterpreterValidator.cs`(新建)

```csharp
namespace ComfyUI.Manager.Services;

public sealed record ValidationResult(bool IsValid, string Version = "", string? Error = null);

public sealed class PythonInterpreterValidator
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

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
            var waitTask = p.WaitForExitAsync(cts.Token);

            await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, cts.Token));
            // 取第一行,优先 stdout,fallback stderr
            var output = (await stdoutTask).Trim();
            if (string.IsNullOrEmpty(output)) output = (await stderrTask).Trim();
            if (string.IsNullOrEmpty(output))
                return new ValidationResult(false, Error: "无输出");

            // Python 3.x.y (tags)
            var m = System.Text.RegularExpressions.Regex.Match(output, @"Python\s+(\d+\.\d+(?:\.\d+)?)");
            if (!m.Success) return new ValidationResult(false, Error: "不是合法 Python 解释器");

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

**契约:** 不抛任何异常;5 秒超时;返回 `IsValid` + `Version` (e.g. "3.10.18") 或 `Error` (中文 message)。

### §2.2 `ViewModels/SettingsViewModel.cs` 新增区段

沿用 QuerySources / DownloadSources 模式 1:1。

```csharp
// 只读 computed 属性(供 UI 绑定)
public IReadOnlyList<PythonInterpreter> PythonInterpreters =>
    _settings.PythonInterpreters;

public PythonInterpreter? ActivePythonInterpreter
{
    get
    {
        var name = _settings.ActivePythonInterpreterName;
        if (string.IsNullOrEmpty(name)) return null;
        return _settings.PythonInterpreters.FirstOrDefault(p => p.Name == name);
    }
}

// 写入
public RelayCommand AddPythonInterpreterCommand { get; }     // 打开内联表单
public RelayCommand ConfirmAddPythonInterpreterCommand { get; }  // 校验 + 写入
public RelayCommand CancelAddPythonInterpreterCommand { get; }   // 关闭 + 清空
public RelayCommand RemovePythonInterpreterCommand { get; }       // param = entry

public string NewPythonInterpreterName { get; set; } = "";
public string NewPythonInterpreterPath { get; set; } = "";
public string AddPythonInterpreterError { get; private set; } = "";
public bool IsAddPythonInterpreterOpen { get; private set; }

private bool _addPythonInterpreterInFlight;     // 防止重复点"确定"并发校验
private CancellationTokenSource? _addValidatorCts;
```

**添加流程:**

```
AddPythonInterpreterCommand.Execute
  → IsAddPythonInterpreterOpen = true
  → AddPythonInterpreterError = ""
  → NewPythonInterpreterName/Path = ""

ConfirmAddPythonInterpreterCommand.Execute
  → if _addPythonInterpreterInFlight: return
  → _addPythonInterpreterInFlight = true
  → _addValidatorCts?.Cancel(); _addValidatorCts = new CancellationTokenSource();
  → var result = await validator.ValidateAsync(NewPythonInterpreterPath, _addValidatorCts.Token)
  → if !result.IsValid:
        AddPythonInterpreterError = result.Error
        return
  → _settings.PythonInterpreters.Add(new PythonInterpreter {
        Name = NewPythonInterpreterName,
        Path = NewPythonInterpreterPath,
    })
  → _settings.ActivePythonInterpreterName = NewPythonInterpreterName   // 新增即激活
  → _repo.Save(_settings)
  → IsAddPythonInterpreterOpen = false; AddPythonInterpreterError = ""
  → finally: _addPythonInterpreterInFlight = false; _addValidatorCts?.Dispose(); _addValidatorCts = null
  → RaisePropertiesChanged(PythonInterpreters, ActivePythonInterpreter)

RemovePythonInterpreterCommand.Execute (param = entry)
  → _settings.PythonInterpreters.Remove(entry)
  → if _settings.ActivePythonInterpreterName == entry.Name:
        _settings.ActivePythonInterpreterName = _settings.PythonInterpreters.FirstOrDefault()?.Name ?? ""
  → _repo.Save(_settings)
  → RaisePropertiesChanged(PythonInterpreters, ActivePythonInterpreter)

Dispose
  → _addValidatorCts?.Cancel(); _addValidatorCts?.Dispose()
```

### §2.3 `Views/SettingsView.xaml` 新增区段

插入位置:紧跟"路径"section 之后,"环境 / 工具"section 之前。

```xml
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
        <ColumnDefinition Width="160" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="名称(唯一)" VerticalAlignment="Center" />
    <TextBox Grid.Column="1" Style="{StaticResource MaterialTextBox}" Margin="8,0,0,0"
             Text="{Binding NewPythonInterpreterName, UpdateSourceTrigger=PropertyChanged}" />
    <Button Grid.Column="2" Content="确定" Margin="8,0,0,0"
            Command="{Binding ConfirmAddPythonInterpreterCommand}"
            Style="{StaticResource MaterialButton}" />
    <Button Grid.Column="3" Content="取消" Margin="4,0,0,0"
            Command="{Binding CancelAddPythonInterpreterCommand}"
            Style="{StaticResource MaterialButton}" />
</Grid>
<Grid Margin="0,8,0,0"
      Visibility="{Binding HasAddPythonInterpreterError,
                    Converter={StaticResource BoolToVisibility}}">
    <TextBlock Text="{Binding AddPythonInterpreterError}" Foreground="OrangeRed" FontSize="11" />
</Grid>
```

**老字段 read-only label**(在"路径"section 末尾):

```xml
<TextBlock Text="(已废弃 - 以下字段保留读不写,请用上方 'Python 解释器' 区段)"
           Foreground="Gray" FontSize="11" Margin="0,16,0,0" />
<TextBlock Text="模板 Python 目录(只读,来自 v0.6.5.5 及更早)" Margin="0,4,0,4" />
<TextBox Text="{Binding TemplatePythonDir}" IsReadOnly="True"
         Style="{StaticResource MaterialTextBox}" />
<TextBlock Text="默认 Python 版本(只读)" Margin="0,8,0,4" />
<TextBox Text="{Binding DefaultPythonVersion}" IsReadOnly="True"
         Style="{StaticResource MaterialTextBox}" />
```

> 注:`TemplatePythonDir`/`DefaultPythonVersion` setter 仍触发 Save,但 UI 不暴露给用户编辑(IsReadOnly=True,鼠标无法 focus)。

### §2.4 `Views/SettingsView.xaml.cs` 新增浏览

```csharp
private void BrowsePythonInterpreter(object sender, RoutedEventArgs e)
{
    var dlg = new OpenFileDialog
    {
        Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        Title = "选择 Python 解释器"
    };
    if (dlg.ShowDialog() == true)
    {
        var vm = (SettingsViewModel)DataContext;
        vm.NewPythonInterpreterPath = dlg.FileName;
    }
}
```

### §2.5 `ViewModels/CreateEnvDialogViewModel.cs` 改 `ApplyTemplate`

```csharp
private void ApplyTemplate()
{
    var active = _settings.PythonInterpreters
        .FirstOrDefault(p => p.Name == _settings.ActivePythonInterpreterName);
    PythonExe = active?.Path ?? "";     // ← 替换原 TemplatePythonDir/DefaultPythonVersion 拼接
}

// ApplyTemplateCommand 行为不变(v0.6.5.5):
//   _recentBasePythonPath = null → ApplyTemplate()
```

**顶部警告文案调整**(`_warnings` 计算逻辑):

| 情况 | 警告 |
|---|---|
| `_pythonExe == ""`(recent base + active 都为空) | "请在设置页添加 Python 解释器" |
| active.Path 不存在(添加后被外部删除) | "当前 Python 解释器路径不存在,请检查设置" |
| recent base 非空且存在 | (无警告,recent base 是合法 path) |

### §2.6 `Infrastructure/SettingsDefaults.cs` 首次加载迁移

在 `LoadOrDefault(projectRoot)` 末尾、Save 之前插入:

```csharp
// —— v0.6.5.6:首次加载老 settings.json 时,自动迁移出一条 PythonInterpreter ——
if (s.PythonInterpreters.Count == 0
    && !string.IsNullOrWhiteSpace(s.TemplatePythonDir)
    && !string.IsNullOrWhiteSpace(s.DefaultPythonVersion))
{
    var candidatePath = Path.Combine(s.TemplatePythonDir, s.DefaultPythonVersion, "python.exe");
    s.PythonInterpreters.Add(new PythonInterpreter
    {
        Name = s.DefaultPythonVersion,
        Path = candidatePath,
    });
    s.ActivePythonInterpreterName = s.DefaultPythonVersion;
    // 老字段 TemplatePythonDir / DefaultPythonVersion 保留不动
}
```

> 注意:`SettingsDefaults.ApplyDefaults` 是为 missing settings.json 服务的;迁移只在"settings.json 存在 + list 为空 + 老字段非空"时触发。如果 settings.json 不存在,老字段也空,ApplyDefaults 不动,新 list 保持空 → 用户首次打开 Settings 看到空 list + 顶部提示。

---

## §3 数据流

### 首次加载(老 settings.json 升级 v0.6.5.6)

```
Read settings.json (含 template_python_dir="D:\python", default_python_version="3.10")
  ↓
PythonInterpreters = []
  ↓
SettingsDefaults.LoadOrDefault 末尾迁移分支:
  ↓
PythonInterpreters.Add({Name="3.10", Path="D:\python\3.10\python.exe"})
ActivePythonInterpreterName = "3.10"
  ↓
Save()  → settings.json 现含 python_interpreters + active_python_interpreter_name
  ↓
老字段 template_python_dir / default_python_version 仍存在,UI 只读展示
```

### 首次打开 Settings UI(空 list)

```
List 空,ActiveName 空
  ↓
ComboBox "当前使用" 显示空
  ↓
用户点 "+ 添加解释器" → 内联表单
  ↓
填 Name="py3.11",Path="D:\python\3.11\python.exe"(或浏览)
  ↓
点 "确定" → PythonInterpreterValidator.ValidateAsync
  ↓
成功: 写入 + 激活 + Save + 关闭表单
失败: AddPythonInterpreterError = "路径不存在" / "不是合法 Python 解释器" / "超时"
      保持表单打开,让用户改 Path 后重试
```

### 打开 CreateEnvDialog

```
Open(creator, settings, projectRoot, recentBase)
  ↓
vm.ctor → _recentBasePythonPath = recentBase
  ↓
ApplyTemplate()
  if recentBase != null:   PythonExe = recentBase         (v0.6.5.5 优先)
  elif active != null:     PythonExe = active.Path        (新行为)
  else:                    PythonExe = ""
  ↓
顶部警告计算:
  if PythonExe == "":      warning = "请在设置页添加 Python 解释器"
  elif PythonExe 设了但 File.Exists == false:
                           warning = "当前 Python 解释器路径不存在,请检查设置"
  else:                    warning = ""
```

### 用户点 "应用模板"

```
ApplyTemplateCommand.Execute
  ↓
_recentBasePythonPath = null
  ↓
ApplyTemplate() → PythonExe = active.Path (若 active 非空)
                  或 PythonExe = "" (若 active 空)
```

### 用户删除 active 条目

```
RemovePythonInterpreterCommand(entry)
  ↓
PythonInterpreters.Remove(entry)
  ↓
if ActivePythonInterpreterName == entry.Name:
    ActivePythonInterpreterName = PythonInterpreters.FirstOrDefault()?.Name ?? ""
  ↓
Save()
```

---

## §4 测试

### §4.1 新增测试

**`tests-wpf/ComfyUI.Manager.Tests/Services/PythonInterpreterValidatorTests.cs`** (~5 tests)

| Test | 断言 |
|---|---|
| `ValidateAsync_ReturnsValid_WhenPathIsPythonExe` | 喂 `python --version` 可运行的真实 exe,断言 `IsValid=true` + `Version` 匹配 `\d+\.\d+(\.\d+)?` |
| `ValidateAsync_ReturnsInvalid_WhenPathMissing` | 喂不存在路径,断言 `IsValid=false` + `Error` 含"不存在" |
| `ValidateAsync_ReturnsInvalid_WhenPathNotPython` | 喂 `notepad.exe`,断言 `IsValid=false` + `Error` 含"Python" |
| `ValidateAsync_ReturnsInvalid_OnTimeout` | fake handler 延迟 > 5s,断言 `IsValid=false` + `Error="超时"` |
| `ValidateAsync_DoesNotThrow_OnFailure` | 喂无效权限路径(Windows 下用 UNC 错误格式),断言返回 Invalid 而非抛异常 |

> Tests 用真实 Python(机器上 `python` 或 `py`)做 happy path,确保 validator 实际工作;坏路径测试用 fake `Process.Start` 行为或 Windows 系统自带的 `notepad.exe`。

### §4.2 扩展测试

**`tests-wpf/ComfyUI.Manager.Tests/Models/SettingsTests.cs`** 新增 3 tests:

| Test | 断言 |
|---|---|
| `PythonInterpreters_RoundTrip` | 写入 2 条 + ActiveName,序列化反序列化一致 |
| `Migration_FirstLoadFromLegacyTemplatePythonDir_CreatesDefaultEntry` | 喂 settings.json 含 template_python_dir="D:/python" + default_python_version="3.10",调用 `LoadOrDefault`,断言 PythonInterpreters 含 1 条 + ActiveName="3.10" + 老字段保留 |
| `Migration_NoOp_WhenPythonInterpretersNonEmpty` | 喂 settings.json 已含 1 条 PythonInterpreter,断言迁移不重复添加 |

**`tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`** 新增 2 tests:

| Test | 断言 |
|---|---|
| `ApplyTemplate_UsesActiveInterpreterPath_NotTemplateConcat` | settings 设 PythonInterpreters=[{py3.11, /x/python.exe}], ActiveName=py3.11, TemplatePythonDir="D:/python", DefaultPythonVersion="3.10" → ApplyTemplate 后 PythonExe == "/x/python.exe"(不是 "D:/python/3.10/python.exe") |
| `ApplyTemplate_FallsBackToEmpty_WhenActiveMissing` | PythonInterpreters=[] → ApplyTemplate 后 PythonExe="" |

**`tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs`** 新增 3 tests:

| Test | 断言 |
|---|---|
| `AddPythonInterpreter_WithValidPath_WritesAndActivates` | mock validator 返回 IsValid=true → PythonInterpreters 含新条目 + ActiveName==Name + Save 被调 |
| `AddPythonInterpreter_WithInvalidPath_ShowsError_DoesNotWrite` | mock validator 返回 IsValid=false → AddPythonInterpreterError 非空 + PythonInterpreters 未变 |
| `RemovePythonInterpreter_ResetsActive_WhenActiveRemoved` | 删除 ActiveName 对应条目 → ActiveName 回退到剩余第一条 |

### §4.3 基线期望

- v0.6.5.5 基线:298 PASS / 1 SKIP / 0 FAIL
- v0.6.5.6 新增:~13 tests (Validator 5 + Settings 3 + DialogVM 2 + SettingsVM 3)
- v0.6.5.6 期望:**311 PASS / 1 SKIP / 0 FAIL**

---

## §5 边界 / 风险

| 边界 | 处理 |
|---|---|
| ActivePythonInterpreterName 指向已被删除的 Name | UI ComboBox 空选;ApplyTemplate 返回 "";顶部黄条提示"请在设置页添加 Python 解释器" |
| 迁移时 TemplatePythonDir 是相对路径 | 不 resolve,保留原文写入新条目;UI 展示原文;用户在 Settings 改 Path 为绝对路径后保存 |
| 老 settings.json + python_interpreters 已存在 + ActiveName 指向不存在 | ComboBox 空选(不自动选第一条,避免误改用户意图);用户手动调整 |
| 用户在 Settings 直接改 Path 而不重新校验 | 不校验(避免误判);下次 EnvCreatorService 启动创建时报错 |
| 添加解释器时用户关闭 Settings → validator Process 未结束 | vm Dispose 取消 `_addValidatorCts` → ValidateAsync catch `OperationCanceledException` 返回 Invalid(用户已离开,无副作用) |
| `python --version` 中文 Windows 输出乱码 | `ProcessStartInfo.StandardOutputEncoding = Encoding.UTF8`;解析取首段 major.minor(`Regex.Match(output, @"Python\s+(\d+\.\d+(?:\d+)?)")`) |
| 同名多条 (py3.10 × 2) | 允许;ActiveName 取第一条匹配;用户自行避免(UI 无禁止,但测试可加 contract doc) |
| Path 含中文/空格 | 沿用 `ProcessStartInfo` 标准 quoting;不特殊处理 |
| 添加按钮双击(并发校验) | `_addPythonInterpreterInFlight` flag 阻止二次进入;用户取消表单时一并 reset |
| 用户编辑老 TemplatePythonDir 字段(IsReadOnly 但可能 hack) | setter 仍写 settings.json,但 UI 不暴露;List/Active 字段是 source of truth,老字段实际不影响行为 |
| 启动性能 | 启动时**不校验**任何 path;只在"添加"时校验;5s 超时不会卡启动 |

---

## §6 升级注意 / Release notes 草稿

### 升级注意

- **直接覆盖 v0.6.5.5 文件即可。**
- 老 settings.json 首次打开时自动迁移一条 PythonInterpreter,无需手动操作。
- 老 `template_python_dir` / `default_python_version` 字段保留(只读 label),不会再被读用于 auto-fill。
- 若老 active 解释器已被外部删除,顶部黄色提示出现;在 Settings 添加新条目或修复 Path 即可。

### Release notes §1 新增功能

```markdown
### 1) 新增功能

- **Settings 多 Python 解释器管理**:
  - `Settings.PythonInterpreters`(list):每条 `{Name, Path}`,完整解释器路径
  - `Settings.ActivePythonInterpreterName`(string):当前使用,CreateEnvDialog auto-fill 取这条
  - UI 沿用 QuerySources / DownloadSources 模式(列表 + Add/Remove + 内联表单)
  - 添加时同步跑 `python --version` 校验(5s 超时);非合法 Python 解释器拒绝添加
- **老字段自动迁移**:首次升级 v0.6.5.5 settings.json,自动从 `TemplatePythonDir/DefaultPythonVersion` 合成一条默认条目,无需手动迁移。
- **老字段保留读不写**:`template_python_dir` / `default_python_version` 在 UI 展示为只读 label"已废弃",不再参与 auto-fill。
```

### §4 Verification 草稿

```markdown
- **dotnet test:** 311 PASS / 1 SKIP / 0 FAIL(v0.6.5.5 基线 298 + 新增 13)
- **pytest version consistency:** 3 PASS(v0.6.5.5 → v0.6.5.6)
- **dotnet build Release:** 0 errors
```

---

## §7 已知 carry-over / 不做事项

- **不做**:Path 实时校验列表状态(用户改 Path 不重新跑 validator);运行时 EnvCreatorService 已报错。
- **不做**:同名检测 / 唯一性约束;用户自行避免。
- **不做**:`base_env_profiles.json` 里 profile 引用的 Python 路径统一迁移;v0.6.5.3 已发,BED profile 走 live fetch 或文件 override,不依赖 Settings。
- **不做**:Settings 面板"搜索 / 过滤"解释器列表(YAGNI,数量预期 ≤ 5 条)。
- **v0.6.5.5 GUI smoke** 仍未完成,独立任务,与 v0.6.5.6 无关。

---

## §8 决策摘要

| # | 决策 | 选项 |
|---|---|---|
| D1 | Entry 粒度 | 完整解释器 Name + Path(沿用 QuerySources 模式) |
| D2 | 老字段迁移 | 首次加载自动迁移为 1 条默认条目;老字段保留读不写 |
| D3 | CreateEnvDialog 选释器 | 保留 textbox + "浏览...";默认 active |
| D4 | 添加时校验 | 同步跑 `python --version`(5s 超时) |
| D5 | 删除 active | ActiveName 回退到剩余第一条或空 |
| D6 | 版本号 | v0.6.5.6 hotfix |