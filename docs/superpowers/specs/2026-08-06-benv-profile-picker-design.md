# BED 安装版本选择 picker 设计 spec

**Status:** 设计已通过用户 review
**Date:** 2026-08-06
**Target version:** v0.6.6
**Author:** brainstorming session

---

## 背景与动机

### 当前问题
`EnvironmentListViewModel` 工具栏的"基础环境部署"按钮(用户在 env-list tab 顶部看到)点击后:
- 直接读 `_profileLoader.GetHardcodedDefaults().FirstOrDefault()`(默认 torch 2.4.1+cu118)
- **不**让用户选版本,**不**让用户选 CUDA 变体
- 直接弹 `BaseEnvProgressDialog` 开始装

`BaseEnvView` 侧栏 tab 反而有完整选择 UI(ComboBox torch + ListBox CUDA),但用户从 env-list tab 进 BED install 走的是工具栏按钮,这条路径没机会选。

### 用户原话
> "基础环境安装,需要弹出不同的版本,严格来说需要弹框让用户选择安装哪个版本"

### 影响
- 用户装错 CUDA 版本(显卡是 RTX 4090 装成 cu118 而不是 cu126/128)需要卸载重装
- 老 GPU 用户不知道自己该选哪个(默认 torch 2.4.1+cu118 是为新卡)
- 用户对 BED 安装流程"全自动"缺乏控制感

### 设计决策(已 brainstorm 锁定)
| 决策 | 选项 | 用户选 |
|---|---|---|
| Scope | env-list 工具栏 / 两个入口统一 / 抽出可复用组件 | **抽出可复用组件** |
| Picker UI | ComboBox+ListBox / 单 ListBox / 单 ComboBox | **ComboBox + ListBox**(同 BaseEnvView) |
| 多选 | 单选 / 全多选 / BaseEnvView 也单选 | **env-list 单选,BaseEnvView 保留多选** |
| User override 模式 | 镜像 / picker 也走 | **镜像(独立行为)** |
| 实现思路 | UserControl+Window wrapper / 只 Dialog / 双 Mode | **方案 A:UserControl + Window wrapper** |

---

## 架构

### 新增文件

```
src-wpf/ComfyUI.Manager/
   ViewModels/BaseEnvProfilePickerViewModel.cs    [新]
   Views/BaseEnvProfilePickerView.xaml            [新,UserControl]
   Views/BaseEnvProfilePickerView.xaml.cs        [新]
   Views/BaseEnvProfilePickerDialog.xaml          [新,Window 套壳]
   Views/BaseEnvProfilePickerDialog.xaml.cs      [新]

tests-wpf/ComfyUI.Manager.Tests/
   ViewModels/BaseEnvProfilePickerViewModelTests.cs    [新,~10 测试]
   Views/BaseEnvProfilePickerViewTests.cs              [新,~3 测试]
   ViewModels/EnvironmentListViewModelOpenBaseEnvTests.cs  [新,~5 测试]
   ViewModels/BaseEnvViewModelReselectTests.cs          [新,~3 测试]
```

### 修改文件

```
src-wpf/ComfyUI.Manager/
   ViewModels/EnvironmentListViewModel.cs   [改 OpenBaseEnvProgress 加 picker]
   ViewModels/BaseEnvViewModel.cs           [加 ReselectCommand,SetSelectedProfiles 适配 picker 返回]
   Views/BaseEnvView.xaml                   [删 inline ComboBox+ListBox,加 [改选] 按钮]
   Views/BaseEnvView.xaml.cs                [删 OnProfileSelectionChanged,加 OnReselectClicked]
   Resources/Strings.resx + Strings.zh-CN.resx  [+3 keys:Picker_Title / Picker_Ok / Picker_Cancel]

tests-wpf/ComfyUI.Manager.Tests/
   ViewModels/EnvironmentListViewModelTests.cs  [构造函数适配 picker test seam]
   ViewModels/BaseEnvViewModelTests.cs          [OnSelectedVersionChanged 跨版本残留保护测试]
```

### 数据源分流(关键)

| 入口 | profile 源 | SelectionMode | Preselected |
|---|---|---|---|
| BaseEnvView tab "改选..." 按钮 | 既有 `BaseEnvViewModel.LoadAsync()`(user override JSON → 走 JSON;否则 `GetLiveDefaultsAsync` live→fallback) | Multi | `_selectedProfiles.FirstOrDefault()` |
| env-list 工具栏"基础环境部署"按钮 | **同步** `_profileLoader.GetHardcodedDefaults()`(v0.6.5.22 升 torch 2.4.1,共 9 个 profile;**不**走 live fetch 也**不**走 user override JSON,跟既有 OpenBaseEnvProgress 行为一致) | Single | profiles.FirstOrDefault()(默认 torch 2.4.1+cu118) |

**为什么不走 live fetch:** `OpenBaseEnvProgress` 现有调用点是同步链(`RelayCommand` lambda),改成 async 需要调整 ctor/wiring。同步用 `GetHardcodedDefaults()` 已经返回 9 个 profile 覆盖 stable 5 + nightly 1 + cpu 1 + cu128 系列,够用户选。**future work**(不阻塞):改成 async 走 live fetch 能让用户看到 pytorch.org 当前 stable 而非硬编码。

---

## 组件设计

### `BaseEnvProfilePickerViewModel`

```csharp
public enum PickerSelectionMode { Single, Multi }

public sealed class BaseEnvProfilePickerViewModel : ViewModelBase
{
    public BaseEnvProfilePickerViewModel(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode selectionMode);

    public PickerSelectionMode SelectionMode { get; }
    public IReadOnlyList<PyTorchVersionEntry> Versions { get; }      // 绑 ComboBox
    public PyTorchVersionEntry? SelectedVersion { get; set; }         // 改它 reload Profiles
    public IReadOnlyList<BaseEnvProfile> Profiles { get; }            // 绑 ListBox,按 SelectedVersion 过滤
    public IReadOnlyList<BaseEnvProfile> SelectedProfiles { get; set; }  // 输出

    /// <summary>
    /// 取消或无可用 profile 时返回 null;OK 返回选中列表(单选模式永远 0 或 1 项)
    /// </summary>
    public IReadOnlyList<BaseEnvProfile>? Result { get; private set; }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
}
```

**关键行为:**
- 构造时 `Versions` = `profiles.Select(p => TorchVersionEntry.From(p)).Distinct()`(去重)
- 默认 `SelectedVersion` = `Versions.First()`(首个 stable torch)
- `Profiles` 由 `SelectedVersion` 过滤:torch version 匹配的 profile 子集
- `SelectedVersion` setter:改 → `Profiles` 重新过滤 → `SelectedProfiles` **清空**(避免跨 torch 残留)
- `SelectedProfiles` setter:必须属于 `Profiles` 子集,否则抛 `ArgumentException`(防御性)
- `SelectionMode = Single`:`SelectedProfiles.Count > 1` 时 set 抛 `ArgumentException`
- `OkCommand.CanExecute`:`SelectionMode == Single` 时 `SelectedProfiles.Count == 1`;Multi 时 `SelectedProfiles.Count >= 1`
- `CancelCommand`:设 `Result = null`
- `OkCommand`:设 `Result = SelectedProfiles`(返回 list 副本)

### `BaseEnvProfilePickerView`(UserControl)

```
DockPanel
├── DockPanel.Dock="Top"
│   ├── TextBlock "torch 版本:"
│   └── ComboBox ItemsSource={Binding Versions}
│                SelectedItem={Binding SelectedVersion}
│                DisplayMemberPath="DisplayName"
├── DockPanel.Dock="Bottom"
│   ├── TextBlock {Binding SelectedProfiles[0].Description}
│   └── (预留 OK / Cancel 在 Dialog 里,不放在 UserControl)
└── (Fill)
    └── ListBox ItemsSource={Binding Profiles}
                SelectedItem={Binding SelectedProfiles[0]}  // 单选模式 binding
                OR
                SelectedItems={Binding SelectedProfiles}   // 多选模式 binding
                SelectionMode={Binding ListBoxSelectionMode}
                DisplayMemberPath="Id"
                (DataTemplate: 显示 Name + CudaVersion badge + Description)
```

- 暴露 `public IReadOnlyList<BaseEnvProfile> SelectedProfiles { get; set; }` 双向同步 XAML ListBox.SelectedItems
- 暴露 `public PickerSelectionMode SelectionMode` 影响 ListBox.SelectionMode
- DataTemplate 复用 BaseEnvView 既有 `BaseEnvProfile` 渲染(Name + CudaVersion badge + Description)

### `BaseEnvProfilePickerDialog`(Window 套壳)

```
Window Title="选择基础环境组合"
Height=520 Width=640
WindowStartupLocation=CenterOwner
   DockPanel Margin=16
   ├── Border (顶部状态条 "请选择基础环境组合")
   ├── (Fill) BaseEnvProfilePickerView (UserControl)
   └── StackPanel Orientation=Horizontal HorizontalAlignment=Right
       ├── Button Content="确定" IsDefault="True" Click="OnOkClicked"
       └── Button Content="取消" IsCancel="True"  Click="OnCancelClicked"
```

**静态入口:**
```csharp
public static class BaseEnvProfilePickerDialog
{
    /// <summary>
    /// 测试 seam:生产代码 ShowDialog 弹 WPF Window;单测可赋值 ShowOverride
    /// 返 IReadOnlyList&lt;BaseEnvProfile&gt;? 模拟用户选择或取消。
    /// </summary>
    public static Func<
        IReadOnlyList<BaseEnvProfile>,       // profiles
        BaseEnvProfile?,                    // preselected
        PickerSelectionMode,                // mode
        IReadOnlyList<BaseEnvProfile>?>? ShowOverride { get; set; }

    /// <summary>
    /// 弹 picker dialog,返回选中 profile 列表(单选模式 0 或 1 项)。
    /// 用户取消或无可用 profile → 返回 null。
    /// </summary>
    public static IReadOnlyList<BaseEnvProfile>? Show(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode);
}
```

- `Show(profiles, preselected, mode)`:
  - 若 `profiles.Count == 0` → 弹 MessageBox "无可用 profile,无法部署" → 返回 null
  - 若 `ShowOverride != null` → 调 `ShowOverride(profiles, preselected, mode)` 返回
  - 否则构造 `BaseEnvProfilePickerDialog` 实例 → 设 `DataContext = new BaseEnvProfilePickerViewModel(profiles, preselected, mode)` → `ShowDialog()` → DialogResult == true 返回 `vm.Result`;否则 null

---

## 数据流

### env-list 工具栏入口

```
User clicks toolbar "基础环境部署" button
   ↓
EnvironmentListViewModel.OpenBaseEnvProgress()
   ↓ (既有 all-done guard 不动)
   ↓ (既有 mutex guard 不动)
var profiles = _profileLoader.GetHardcodedDefaults();  // PyTorchVersionDirectory live→fallback
var preselected = profiles.FirstOrDefault();
   ↓
var picked = BaseEnvProfilePickerDialog.Show(profiles, preselected, PickerSelectionMode.Single);
   ↓
if (picked is null || picked.Count == 0) return;  // 取消或无可用
var profile = picked.First();
   ↓ (既有 mutex mark + BaseEnvProgressDialog.Show + Load + RaiseCommandsChanged 不动)
```

### BaseEnvView tab 入口

```
User clicks sidebar "基础环境" → BaseEnvView tab 出现
   ↓
BaseEnvViewModel.Load() 跑(既有逻辑,user override 决定 source)
   ↓
UI 顶部:
   - [TextBlock] 当前选择: {SelectedVersion?.DisplayName ?? "(未选)"}({SelectedProfiles.Count} 个 CUDA 变体已选)
   - [Button] "改选..."
   ↓
User clicks "改选..."
   ↓
BaseEnvViewModel.ReselectCommand
   ↓
var picked = BaseEnvProfilePickerDialog.Show(
                profiles: _profileLoader.GetHardcodedDefaults() 或 user override JSON 列表,
                preselected: SelectedProfiles.FirstOrDefault(),
                mode: PickerSelectionMode.Multi);
   ↓
if (picked is null) return;  // 用户取消
   ↓
SetSelectedProfiles(picked);                  // 复用既有方法(更新 _selectedProfiles + RaisePropertyChanged)
SetSelectedVersion(... 新 torch version ...)   // 跟新 SelectedProfiles[0].TorchVersion 同步
   ↓
UI 自动反映新选择([TextBlock] 顶部更新)
   ↓
User clicks "开始部署" → BaseEnvViewModel.Start()(既有逻辑,不动)
```

---

## 错误处理

| 场景 | 行为 |
|---|---|
| Picker 用户点 Cancel | `Show()` 返回 null → 入口 bail,不进 install / 不改 state |
| `profiles.Count == 0`(live 失败 + hardcoded 也没) | `Show()` 内部弹 MessageBox "无可用 profile,无法部署" → 返回 null |
| User override JSON 存在但解析失败 | 既有 `BaseEnvViewModel.Load` 已 fallback 到 hardcoded,行为不变 |
| env-list preselected 是 null(空 list 边界) | `Show()` 传 null → ListBox 无初始选 → UI 显示 "请选择" 提示 + OK disabled |
| Picker 中途换 torch 版本 | `SelectedVersion` setter → `Profiles` 刷新 → `SelectedProfiles` 自动清空 |
| 多选模式下 OK 时 `SelectedProfiles.Count == 0` | `Show()` 弹 MessageBox "请至少选择 1 个 profile";`DialogResult=false`;返回 null |
| 单选模式下 OK 时 `SelectedProfiles.Count == 0` | 同上 |
| 单选模式下 `SelectedProfiles.Count > 1`(用户开了多选扩展) | VM setter 抛 `ArgumentException`;XAML binding 异常抛出在 UI 线程,被 WPF 吞掉不影响数据 |
| env-list toolbar BED install 单选模式选 torch 后 ListBox 空 | profiles 过滤后 0 项 → 弹 "请选择" 提示 + OK disabled,不让用户取消 CUDA 选择 |

### 现有 BaseEnvViewModel 状态迁移

- `SelectedVersion`(torch version):picker 选完返回时,如果新 `SelectedProfiles[0].TorchVersion != 旧 SelectedVersion` → 更新
- `SelectedProfiles`(CUDA profiles):picker 返回值直接 set
- `Versions`(torch versions 列表):picker dialog 内独立持有,不污染 BaseEnvViewModel
- `IsUserOverrideActive`:BaseEnvViewModel.Load 既有逻辑不动,picker dialog 只接收最终 profile list

---

## 测试策略

### 新增测试文件

**`BaseEnvProfilePickerViewModelTests.cs`**(~10 测试)

| 测试 | 覆盖 |
|---|---|
| `Constructor_Multi_InitializesProfilesFromInput` | VM 构造 + Multi + Profiles 列表正确 |
| `Constructor_Single_PreselectsDefault` | Single + preselected 在 SelectedProfiles[0] |
| `SelectedVersion_Changes_FiltersProfiles` | 改 ComboBox → Profiles 重新过滤 |
| `SelectedVersion_Changes_ClearsSelectedProfiles` | 跨 torch 切换 → SelectedProfiles 清空 |
| `Profiles_Set_NullOrEmpty_DoesNotThrow` | 边界:空 list 不 NRE |
| `SelectedProfiles_Set_UpdatesBinding` | VM set → INPC 触发 UI |
| `PickerMode_Multi_OkReturnsAllSelected` | OK Command → Result = SelectedProfiles 多项 |
| `PickerMode_Single_OkReturnsFirstOrNull` | Single OK → Result.Count == 1;Cancel → Result = null |
| `OkCommand_CanExecute_Multi_RequiresAtLeastOne` | CanExecute 边界:0 项 → false |
| `OkCommand_CanExecute_Single_RequiresExactlyOne` | CanExecute 边界:0 或 >1 项 → false |

**`BaseEnvProfilePickerViewTests.cs`**(~3 测试,无 WPF,纯 property 同步逻辑)

| 测试 | 覆盖 |
|---|---|
| `SelectedProfiles_Get_ReturnsListBoxSelection` | UserControl.SelectedProfiles ↔ ListBox.SelectedItems |
| `SelectedProfiles_Set_UpdatesListBox` | 反向 sync |
| `SelectionMode_Set_UpdatesListBoxSelectionMode` | UserControl.SelectionMode → ListBox.SelectionMode |

**`EnvironmentListViewModelOpenBaseEnvTests.cs`**(~5 测试)

| 测试 | 覆盖 |
|---|---|
| `OpenBaseEnvProgress_NoProfiles_ShowsMessageAndReturns` | profiles 空 → MessageBox + 不进 install |
| `OpenBaseEnvProgress_PickerCancel_DoesNotLaunchInstall` | ShowOverride 返 null → BaseEnvProgressDialog 未调 |
| `OpenBaseEnvProgress_PickerReturnsProfile_LaunchesInstall` | ShowOverride 返 [profile] → BaseEnvProgressDialog 收到 |
| `OpenBaseEnvProgress_PickerReturnsEmpty_ShowsMessage` | ShowOverride 返 [] → MessageBox |
| `OpenBaseEnvProgress_EnvBusy_BailsBeforePicker` | 既有 guard 既有测试适配 |

**`BaseEnvViewModelReselectTests.cs`**(~3 测试)

| 测试 | 覆盖 |
|---|---|
| `ReselectCommand_PickerReturnsSelection_UpdatesSelectedProfiles` | 改选 → ShowOverride 返 [p1,p2] → _selectedProfiles 更新 + INPC |
| `ReselectCommand_PickerCancel_DoesNotChangeSelection` | ShowOverride 返 null → SelectedProfiles 不动 |
| `ReselectCommand_Preselected_PassesToPicker` | ShowOverride 被调时,preselected 参数 = 当前 _selectedProfiles.First() |

### 修改的既有测试

- `BaseEnvViewModelTests.cs`:
  - 加 `OnSelectedVersionChanged_ClearsSelectedProfiles`(跨版本残留保护)
  - 加 `ReselectCommand_CanExecute_OnlyWhenLoadComplete`(ReselectCommand gating)
  - 既有 `StartCommand` / `Load` 测试不变
- `EnvironmentListViewModelTests.cs` + `BedTests.cs`:
  - 改 5 处构造函数(测试构造 picker dialog test mock)
  - 既有 `OpenBaseEnvProgress` 测试适配:加 `ShowPickerDialogOverride` 默认 stub(避免 WPF 弹窗)

### 不测什么

- XAML 渲染 / WPF 控件交互(test 环境无 WPF)→ GUI smoke 验
- 实际 `ShowDialog()` 阻塞 → test seam `ShowOverride` 替换
- Resx key 字符串(只测 key 存在 → 编译过)

### GUI smoke checklist

1. 侧栏点"基础环境" → BaseEnvView tab → 应看到顶部 [当前选择: torch 2.4.1+cu118 (1 个 CUDA)] + [改选] 按钮(不再有 inline ComboBox/ListBox)
2. 点 [改选] → 弹 picker dialog → ComboBox 显示 torch 2.4.1 → ListBox 显示 5 个 CUDA 变体 → 选 torch 2.5.0 → ListBox 自动刷新成 2.5.0 的 CUDA → 选 1 个 CUDA → OK → BaseEnvView 顶部 [当前选择] 更新成 torch 2.5.0 + 该 CUDA
3. 选完点 [开始部署] → 进 BaseEnvProgressDialog → 装刚才选的 profile → 完成后 env 行 BedStatus="done"
4. 回到 env-list tab → 点工具栏 "基础环境部署" → 弹同一 picker dialog(单选模式)→ 选 torch 2.3.0 + 1 CUDA → OK → 进 BaseEnvProgressDialog → 装 2.3.0 那个 profile
5. 验证 "已安装" guard 仍生效:同 env 全 done → 弹 MessageBox "已安装",**不**弹 picker
6. Picker dialog 中途换 torch 验证:在 ListBox 已选 1 个 CUDA 时切 torch → ListBox 选择清空 + SelectedProfiles 空(避免跨版本残留)
7. Picker dialog 取消按钮:点 Cancel → 不改 state,入口 bail

---

## 验收标准

- [ ] env-list 工具栏 "基础环境部署" 按钮 → 弹 picker → 用户选 → 装所选 profile
- [ ] BaseEnvView tab "改选..." 按钮 → 弹 picker → 用户选 → 顶部 [当前选择] 更新
- [ ] Picker 中途换 torch → ListBox 刷新 + SelectedProfiles 清空
- [ ] Picker 用户取消 → 入口不报错,state 不变
- [ ] Picker 空 profiles → 弹 MessageBox,不进 install
- [ ] 多选模式( BaseEnvView )允许多选 CUDA,单选模式( env-list )只 1 个
- [ ] env-list 路径忽略 user override JSON(始终 PyTorchVersionDirectory live→fallback)
- [ ] BaseEnvView 路径 user override 行为不变(有 JSON 走 JSON,无走 live→fallback)
- [ ] 既有 "已安装" guard(all-done check)仍生效
- [ ] 既有 mutex(BED install vs 其他操作)仍生效
- [ ] 全量测试通过(基线 522 + 新增 ~21 ≈ 543 PASS / 0 FAIL / 1 SKIP)

---

## 风险与权衡

| 风险 | 缓解 |
|---|---|
| BaseEnvView 失去 inline 列表预览 UX(改成 2 click) | 用户先点 [改选] 看 dialog 列表,看完再 OK;dialog 是模态全屏,信息密度不降 |
| Picker dialog 跟 BaseEnvProgressDialog 双层模态 | 选完 OK 自动关 picker → 自动开 progress;用户感知单层 |
| User override 用户设了 JSON,env-list 工具栏仍装硬编码 torch 2.4.1 | 设计决策(用户选 "镜像 BaseEnvView 现有行为");若后续想统一,在 OpenBaseEnvProgress 也走 override 即可 |
| 跨 torch 切换时 SelectedProfiles 清空,用户需重新选 CUDA | 设计如此:不同 torch 不能共享 CUDA(版本不兼容);UI 在 ComboBox 切换时短暂空 selection 提示用户重选 |
| 单选模式 OK 时 `SelectedProfiles.Count > 1`(用户用 Ctrl+Click 多选) | VM setter 抛异常;测试覆盖;实际 UI 用 RadioButton 行为更稳,但 ListBox 单选模式已经阻止多选 |
| PyTorchVersionDirectory live fetch 失败 + hardcoded 也没 → 完全空 profiles | Show() 内部弹 MessageBox,不进 install;既有行为 |
| `_selectedProfiles` 在 user override 模式下是 user 自定义 profile 集合 → ComboBox torch version 解析可能失败 | 既有 `BaseEnvProfileLoader.Load` 已处理非法 JSON,fallback 到 hardcoded |

---

## 不在 scope

- 不动 `BaseEnvInstaller` 逻辑
- 不动 `BaseEnvProgressDialog` UI
- 不动 `BaseEnvViewModel.Start()` 启动流程
- 不动 env-start / env-stop / 装依赖 / 卸载按钮
- 不动 `Resources/Strings.resx` 既有 key
- 不动 v0.6.5.22 mutex / per-env 互斥逻辑
- 不动 `BaseEnvProfileLoader.MarkIncompatibleOlderVersions` / torch 默认版本逻辑

---

## 实施(预演 task list,后续 SDD plan 细化)

1. **T1** `BaseEnvProfilePickerViewModel` + 10 测试
2. **T2** `BaseEnvProfilePickerView` UserControl + 3 测试
3. **T3** `BaseEnvProfilePickerDialog` Window + static `Show()` + test seam
4. **T4** `BaseEnvView` tab 改 UI(删 inline + 加 [改选] 按钮)+ `BaseEnvViewModel.ReselectCommand`
5. **T5** `EnvironmentListViewModel.OpenBaseEnvProgress` 接入 picker + 5 测试
6. **T6** resx +3 keys + final verify + staging rebuild

预估 6 commits on main。