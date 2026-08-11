---
date: 2026-08-11
topic: Settings UI 改版 — 浮动 Save + 移除 SharedModelsDirectory
base_sha: 79af0f3
spec_status: DRAFT
plan_status: PENDING
---

# Settings UI 改版 — 设计

## Scope

1. 加 dirty tracking + 浮动 Save 按钮(改一切立即生效 → 改一切走 Save 按钮)
2. 移除 `SharedModelsDirectory` 字段(保留 `DefaultModelsDirectory`)

## 锁定决策(用户已选)

- **Save 粒度**: 整页一个浮动 Save 按钮 + 每行 ⚠️/✅ 状态指示(无 per-row 按钮)
- **立即生效范围**: 全走 Save 按钮(VM setter 改 → 不写盘,只标 dirty;Save 时全部 dirty 写盘)
- **关闭未保存**: 弹 confirm dialog(3 按钮: 保存 / 丢弃 / 取消)
- **旧 `shared_models_directory` JSON 字段**: 静默忽略 + 启动时一次性 Info log

## 架构

### §1 Dirty tracking

XAML 要按行显示 ⚠️,就需要按 property name 查 dirty。为避免给 35 个属性各写一个
`IsDirtyXxx` bool,用一个**带索引器的小对象** `DirtyLookup` 暴露给绑定
(WPF 索引器绑定语法 `{Binding Dirty[DefaultModelsDirectory]}`,方括号内当字符串 key):

```csharp
// ViewModels/DirtyLookup.cs(新建)
public sealed class DirtyLookup : INotifyPropertyChanged
{
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    public bool this[string propertyName] => _dirty.Contains(propertyName);
    public int Count => _dirty.Count;
    public bool Any => _dirty.Count > 0;

    public void Mark(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;
        if (!_dirty.Add(propertyName)) return;
        RaiseAll();
    }

    public void Clear()
    {
        if (_dirty.Count == 0) return;
        _dirty.Clear();
        RaiseAll();
    }

    private void RaiseAll()
    {
        // "Item[]" 是 WPF 约定:让所有索引器绑定重新求值
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Any)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

`SettingsViewModel` 侧:

```csharp
public DirtyLookup Dirty { get; } = new();

public bool HasUnsavedChanges => Dirty.Any;
public int UnsavedCount => Dirty.Count;

private void MarkDirty(string propertyName)
{
    Dirty.Mark(propertyName);
    RaisePropertyChanged(nameof(HasUnsavedChanges));
    RaisePropertyChanged(nameof(UnsavedCount));
    SaveCommand.RaiseCanExecuteChanged();
    DiscardCommand.RaiseCanExecuteChanged();
}

private void ClearDirty()
{
    Dirty.Clear();
    RaisePropertyChanged(nameof(HasUnsavedChanges));
    RaisePropertyChanged(nameof(UnsavedCount));
    SaveCommand.RaiseCanExecuteChanged();
    DiscardCommand.RaiseCanExecuteChanged();
}
```

### §2 每个 setter 改

原来:
```csharp
public string DefaultModelsDirectory
{
    get => _settings.DefaultModelsDirectory;
    set
    {
        if (_settings.DefaultModelsDirectory == value) return;
        _settings.DefaultModelsDirectory = value;
        _repo.Save(_settings);
        RaisePropertyChanged();
    }
}
```

改为:
```csharp
public string DefaultModelsDirectory
{
    get => _settings.DefaultModelsDirectory;
    set
    {
        if (_settings.DefaultModelsDirectory == value) return;
        _settings.DefaultModelsDirectory = value;
        MarkDirty(nameof(DefaultModelsDirectory));
        RaisePropertyChanged();
    }
}
```

每一个 setter 都这样改:`_repo.Save` 删,加 `MarkDirty`。
当前 `SettingsViewModel.cs` 有 **35 处 `_repo.Save(_settings);`** 调用点,逐个替换。

例外(**保留** `_repo.Save`,不走 dirty 流程):
- ctor 里 `SettingsDefaults.Apply` 之后那次(`SettingsViewModel.cs:61`)——
  这是启动时的默认值回填,不是用户编辑
- `ExtraPaths` / `CommonNodes` / `PythonInterpreters` 这类集合的增删命令 ——
  它们是"点按钮立即生效"的动作,不是行内编辑;dirty 语义不适用

### §3 SaveCommand

```csharp
public RelayCommand SaveCommand { get; }

private void Save()
{
    _repo.Save(_settings);
    ClearDirty();
}

private bool CanSave() => HasUnsavedChanges;
```

### §4 DiscardCommand

```csharp
public RelayCommand DiscardCommand { get; }

private void Discard()
{
    // _settings 是 App 全局共享的同一个实例(App.xaml.cs 把它传给
    // ProcessLauncher / EnvCreatorService / CatalogRefreshService ...),
    // 所以**不能**重新赋值字段 —— 必须把磁盘上的值逐字段拷回原对象,
    // 否则其它服务仍持有被丢弃的那份。
    var onDisk = _repo.Load();
    CopyInto(_settings, onDisk);
    RaiseAllPropertiesChanged();   // 既有方法,SettingsViewModel.cs:734
    ClearDirty();
}

private bool CanDiscard() => HasUnsavedChanges;
```

`CopyInto(target, source)` 是 `Models/Settings.cs` 上的新静态方法,逐字段赋值
(`Settings` 当前 43 个 `{ get; set; }` 属性;集合类字段做浅拷贝替换内容而非换引用)。

### §5 未保存改动的拦截点

**`SettingsView` 是 `UserControl`,不是 `Window`** —— 它由
`MainViewModel.ShowSettings()`(`MainViewModel.cs:308-322`)塞进 `CurrentView`,
所以**没有** `Window.Closing` 事件可挂。拦截点只有一个:**主窗口关闭**。

切换侧栏离开设置页**不拦截**:`_settingsViewModel` 被 `MainViewModel.cs:82` 缓存,
dirty 状态跟着 VM 活着,用户切回来时浮动 toolbar 仍显示"⚠️ N 项未保存"。
每次切 tab 都弹框只会制造噪音,不会防止任何数据丢失。真正会丢数据的是进程退出。

```csharp
// MainWindow.xaml.cs — OnClosing 已存在(:134),在现有逻辑最前面插入
private void OnClosing(object? sender, CancelEventArgs e)
{
    if (DataContext is MainViewModel mvm && !mvm.ConfirmDiscardUnsavedSettings())
    {
        e.Cancel = true;
        return;
    }
    // ... 现有的 UI 偏好写盘等逻辑不变
}
```

```csharp
// MainViewModel.cs
internal enum UnsavedChoice { Save, Discard, Cancel }

/// 测试 seam:STA 环境外不能弹真 MessageBox
internal Func<int, UnsavedChoice>? UnsavedPromptOverride;

/// 返回 true = 可以继续关闭;false = 用户选了"取消",中止关闭
internal bool ConfirmDiscardUnsavedSettings()
{
    var vm = CurrentSettingsViewModel;          // 既有属性,MainViewModel.cs:539
    if (vm is null || !vm.HasUnsavedChanges) return true;

    var choice = (UnsavedPromptOverride ?? PromptUnsaved)(vm.UnsavedCount);
    switch (choice)
    {
        case UnsavedChoice.Save:    vm.SaveCommand.Execute(null);    return true;
        case UnsavedChoice.Discard: vm.DiscardCommand.Execute(null); return true;
        default:                    return false;
    }
}

private static UnsavedChoice PromptUnsaved(int count)
{
    // MessageBoxButton.YesNoCancel:是=保存 / 否=丢弃 / 取消=留下
    var r = MessageBox.Show(
        $"您有 {count} 项设置尚未保存。\n\n是 = 保存并退出\n否 = 丢弃并退出\n取消 = 返回",
        "未保存的设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
    return r switch
    {
        MessageBoxResult.Yes => UnsavedChoice.Save,
        MessageBoxResult.No  => UnsavedChoice.Discard,
        _                    => UnsavedChoice.Cancel,
    };
}
```

三按钮用 `MessageBoxButton.YesNoCancel`(WPF 原生),不新建自定义 dialog ——
项目里 `MessageBoxOverride` 这套 test seam 已在 `EnvironmentListViewModel` /
`BaseEnvViewModel` 用过,沿用同一模式。

### §6 浮动 Save 按钮 UI

`SettingsView.xaml` 顶部加粘性 toolbar:

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>  <!-- 新 toolbar -->
        <RowDefinition Height="*"/>     <!-- 现有内容 -->
    </Grid.RowDefinitions>

    <!-- 浮动 Save toolbar -->
    <Border Grid.Row="0" Padding="12,8"
            Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource BorderBrush}"
            BorderThickness="0,0,0,1">
        <DockPanel>
            <TextBlock DockPanel.Dock="Left"
                       Text="设置"
                       FontSize="16" FontWeight="Bold"
                       VerticalAlignment="Center"/>
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal"
                        HorizontalAlignment="Right">
                <TextBlock Text="{Binding UnsavedCount, StringFormat='⚠️ {0} 项未保存'}"
                           VerticalAlignment="Center" Margin="0,0,12,0"
                           Visibility="{Binding HasUnsavedChanges, Converter={StaticResource BoolToVisibility}}"/>
                <Button Content="💾 Save" Width="80"
                        Style="{StaticResource MaterialButton}"
                        Command="{Binding SaveCommand}"/>
                <Button Content="↩ Discard" Width="80" Margin="8,0,0,0"
                        Style="{StaticResource MaterialButton}"
                        Command="{Binding DiscardCommand}"/>
            </StackPanel>
        </DockPanel>
    </Border>

    <!-- 现有内容 -->
    <ScrollViewer Grid.Row="1">...</ScrollViewer>
</Grid>
```

### §7 Per-row dirty marker

每个可编辑行的 label 后面挂一个 ⚠️,**只在该行 dirty 时显示**(clean 时不显示任何图标,
避免 43 个 ✅ 铺满页面):

```xml
<StackPanel Orientation="Horizontal">
    <TextBlock Text="启用自动检查更新" VerticalAlignment="Center"/>
    <TextBlock Text="⚠️" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[EnableAutoCheckUpdates],
                                    Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
```

方括号里写 **property 名字面量**(不加引号),WPF 会按字符串 key 走 `DirtyLookup` 的索引器。

`WarningBrush` 若 `Resources/Palette*.xaml` 里尚不存在则新增(暗/亮两套都要加),
遵循 G4:所有 `Setter` 引用 palette 一律 property-element + `DynamicResource`。

### §8 移除 SharedModelsDirectory

(以下行号基于 base_sha `79af0f3`,已逐个 grep 核实)

| 文件 | 改动 |
|---|---|
| `Models/Settings.cs:52` | 删 `SharedModelsDirectory` 属性 |
| `ViewModels/SettingsViewModel.cs:431-435` | 删 property + setter |
| `ViewModels/SettingsViewModel.cs:747` | 删 `RaiseAllPropertiesChanged` 里那一行 |
| `Views/SettingsView.xaml:62-67` | 删「共享 Models 目录」整块 UI(label + Browse 按钮 + TextBox) |
| `Views/SettingsView.xaml.cs:116-123` | 删 `BrowseSharedModelsDirectory` handler |
| `Services/EnvCreatorService.cs:23,145-149,171-174` | 删步骤 5.5;步骤 5.6 提升为唯一的 models 链接步骤,条件简化成 `DefaultModelsDirectory` 非空 |
| `Infrastructure/ProcessLauncher.cs:33,47,60-62,161,424-438` | 字段/ctor 参数 `sharedModelsDirectory` → `modelsDirectory`;`EnsureModelsJunctionAsync` 语义不变,只是数据源换成 `DefaultModelsDirectory` |
| `App.xaml.cs:121` | 传参改 `settings.DefaultModelsDirectory` |
| `ViewModels/MainViewModel.cs:433` | `extra_model_paths.yaml` 模板注释里的 `Settings.SharedModelsDirectory` 改 `Settings.DefaultModelsDirectory` |

**注意**:`ProcessLauncher` 的 ctor 参数改名会波及所有 `new ProcessLauncher(...)` 的调用点
(生产代码只有 `App.xaml.cs:121`,其余在测试里),都是位置参数,改名不影响调用语法。

老 JSON `shared_models_directory` 字段:
- 启动时 `_repo.Load()` 静默忽略未知字段(原生 `JsonSerializer` 默认行为)
- 启动后一次性 Info log:`已废弃字段 shared_models_directory,已忽略;请使用 DefaultModelsDirectory`

## Tests

### 单元测试

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs`(新建):

- `MarkDirty_SingleProperty_SetsDirtyFlagAndHasUnsavedChanges`
- `MarkDirty_MultipleProperties_UnsavedCountAggregates`
- `MarkDirty_SameProperty_Twice_CountStaysOne`
- `Setter_DoesNotWriteToDisk_BeforeSave`
- `SaveCommand_PersistsSettings_ClearsAllDirty`
- `SaveCommand_CanExecute_FalseWhenClean`
- `DiscardCommand_RevertsInPlace_KeepsSameSettingsInstance`(验共享实例没被换掉)
- `DiscardCommand_ClearsAllDirty_LeavesDiskUnchanged`
- `Dirty_Indexer_ReturnsFalseForUnknownProperty`

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs`(新建):

- `ConfirmDiscard_NoSettingsVm_ReturnsTrue`
- `ConfirmDiscard_Clean_ReturnsTrue_NoPrompt`
- `ConfirmDiscard_Dirty_SaveChoice_PersistsAndReturnsTrue`
- `ConfirmDiscard_Dirty_DiscardChoice_RevertsAndReturnsTrue`
- `ConfirmDiscard_Dirty_CancelChoice_ReturnsFalse_KeepsDirty`

(用 `UnsavedPromptOverride` seam,不弹真 MessageBox)

### 受影响的既有测试

| 文件 | 处理 |
|---|---|
| `ViewModels/SettingsViewModelSharedModelsTests.cs` | **删除**(整个文件都是 SharedModelsDirectory) |
| `Services/EnvCreatorServiceSharedModelsTests.cs` | **删除** |
| `Infrastructure/ProcessLauncherSharedModelsTests.cs` | 改写:数据源换 `DefaultModelsDirectory`,断言不变 |
| `Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs` | 删 `DefaultModelsDirectoryAndSharedModelsDirectory_BothSet_SharedModelsWins`(:106)+ 删 fixture 里的 `SharedModelsDirectory = ""`(:41) |
| `ViewModels/SettingsViewModelTests.cs` | 逐个检查依赖"setter 立即写盘"的断言,改成 setter → `SaveCommand.Execute` → 再断言 |

### STA load test

`tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs`(既有)+ 1 test:

- `SettingsView_WithDirtyRows_RendersDirtyMarkers`(验索引器绑定 `Dirty[...]` 不抛 + toolbar 渲染)

## 改动文件汇总

**源码**
- `src-wpf/ComfyUI.Manager/ViewModels/DirtyLookup.cs` — **新建**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — 删 `SharedModelsDirectory` + 加 `CopyInto`
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` — dirty tracking + Save/Discard + 删 `SharedModelsDirectory`
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — `ConfirmDiscardUnsavedSettings` + seam + :433 注释
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — 浮动 toolbar + per-row ⚠️ + 删共享 Models 块
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` — 删 `BrowseSharedModelsDirectory`
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` — `OnClosing` 前置 guard
- `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` — 参数/字段改名
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` — 5.5/5.6 合并
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — 传参改 `DefaultModelsDirectory`
- `src-wpf/ComfyUI.Manager/Resources/Palette*.xaml` — `WarningBrush`(若缺)

**测试**
- `tests-wpf/.../ViewModels/SettingsViewModelDirtyTests.cs` — 新建
- `tests-wpf/.../ViewModels/MainViewModelUnsavedSettingsTests.cs` — 新建
- `tests-wpf/.../ViewModels/SettingsViewModelSharedModelsTests.cs` — 删除
- `tests-wpf/.../Services/EnvCreatorServiceSharedModelsTests.cs` — 删除
- `tests-wpf/.../Infrastructure/ProcessLauncherSharedModelsTests.cs` — 改写
- `tests-wpf/.../Services/EnvCreatorServiceDefaultModelsDirectoryTests.cs` — 删 1 test + 改 fixture
- `tests-wpf/.../ViewModels/SettingsViewModelTests.cs` — 适配无自动保存
- `tests-wpf/.../Views/SettingsViewLoadTests.cs` — +1 STA test

## YAGNI 划线

- 不做 undo/redo 栈
- 不做 per-row 单独 Save 按钮
- 不做 multi-tab settings
- 不做 settings 导出/导入
- 不做 "auto-apply" 半自动模式

## 风险

| 风险 | 缓解 |
|---|---|
| 35 处 `_repo.Save` 逐个改,漏一个就是"这行改了不用 Save 也生效"的行为不一致 | plan 阶段把 35 个调用点逐行列表;实现后 `grep -c "_repo.Save(_settings);"` 应只剩 ctor + 集合命令那几处 |
| 现有依赖 auto-save 的单元测试会 fail | 已列受影响文件表;`SettingsViewModelTests.cs` 逐个断言过一遍 |
| `Discard` 换掉共享 `Settings` 实例 → 其它服务持有旧对象 | 强制 `CopyInto` 就地改写,加 `DiscardCommand_RevertsInPlace_KeepsSameSettingsInstance` 测试钉住 |
| 用户关闭主窗口忘保存 → 丢失改动 | `OnClosing` 三按钮 guard |
| `SettingsView` 是 UserControl 没有 Closing 事件 | 已确认;guard 放在 `MainWindow.xaml.cs:134` 的 `OnClosing`,不在 SettingsView |
| 索引器绑定 `Dirty[Xxx]` 写错 property 名 → 静默不显示 ⚠️(WPF 绑定失败只写 trace) | STA load test 渲染一次 dirty 状态;GUI smoke 逐行点一遍 |
| `ProcessLauncher` ctor 参数改名波及测试 | 位置参数,改名不影响调用;编译器会抓到命名参数写法 |
| `ui-preferences.json`(v0.6.5.21)误改 | 只动 `Settings.cs`,不动 UI prefs |

## 验证

```bash
# 1. 单元测试
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~MainViewModelUnsavedSettings" -v minimal

# 2. STA load test
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewLoad" -v minimal

# 3. 死引用检查(应为空)
grep -rn "SharedModelsDirectory" src-wpf/ tests-wpf/ --include=*.cs --include=*.xaml

# 4. 全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 862/1/1 ± N

# 5. Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# 5. GUI smoke
# 启动 staging → 设置 → 改任意 field → 该行出现 ⚠️ + toolbar 显示"⚠️ N 项未保存" + Save/Discard enabled
# 点 Save → 所有 ⚠️ 消失,toolbar 计数归零;重启后改动仍在
# 改 field → 切到「环境」tab → 切回设置 → ⚠️ 与计数仍在(不弹框,不丢失)
# 改 field → 关主窗口 → 弹三按钮框
#   是   → 写盘后退出
#   否   → 丢弃后退出(重启验证旧值)
#   取消 → 留在主窗口,⚠️ 仍在
# 设置页不再有「共享 Models 目录」行;「全局默认 Models 目录」仍在且 env-create 链接生效
```

## Carry-forward

- 某些 "修改即应用" 类 feature(主题立刻切换)在 Save 后才生效;若用户体验不佳,follow-up SDD 改为 "auto-apply UI, Save 后才持久化"
- 若 SharedModelsDirectory 概念需要回归(例如后续 multi-shared models),独立 SDD 重新设计
