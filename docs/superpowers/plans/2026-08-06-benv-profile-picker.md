# v0.6.6 BED 安装版本选择 picker 实施 plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 env-list 工具栏"基础环境部署"按钮和 BaseEnvView 侧栏 tab 都通过同一个 picker 组件让用户选 profile(ComboBox torch + ListBox CUDA),不再默认直接装 torch 2.4.1+cu118。

**Architecture:** 抽出 `BaseEnvProfilePickerViewModel`(VM)+ `BaseEnvProfilePickerView`(UserControl)+ `BaseEnvProfilePickerDialog`(Window 套壳)共享组件;env-list 工具栏入口同步走 `GetHardcodedDefaults()`(9 个 profile),BaseEnvView tab 入口走既有 `LoadAsync()`(user override → live → fallback)。env-list 单选 CUDA,BaseEnvView 多选 CUDA(保留 legacy)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · hand-rolled MVVM (`RelayCommand`) · `Microsoft.Data.Sqlite` · 既有 `BaseEnvProfileLoader` + `BaseEnvProfile` + `PyTorchVersionEntry` · 无新依赖

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | picker VM 的 `Profiles` 按 `SelectedVersion.Version` 过滤(profile.TorchVersion 匹配);切 torch 自动清空 `SelectedProfiles`(避免跨版本残留) | spec §5 + §6 |
| G2 | `SelectionMode` enum:`{ Single, Multi }`;Single 模式 `SelectedProfiles.Count > 1` 抛 `ArgumentException`(防御性);Multi 允许多个 | spec §4 + §6 |
| G3 | `BaseEnvProfilePickerDialog.Show()` 是 static 入口,行为:profiles 空 → MessageBox + 返 null;`ShowOverride` test seam 设了 → 调它返;否则 WPF ShowDialog + vm.Result | spec §4 + §7 |
| G4 | env-list 工具栏 `OpenBaseEnvProgress` 改 `Show(profiles, preselected, Single)` → 返 null bail;`preselected = profiles.FirstOrDefault()`(默认 torch 2.4.1+cu118) | spec §5 + G_decision 数据源分流 |
| G5 | BaseEnvView tab 加 `ReselectCommand` → 弹 picker Dialog(Multi 模式,preselected=当前 `_selectedProfiles.FirstOrDefault()`)→ 返值调既有 `SetSelectedProfiles` + `SetSelectedVersion` | spec §5 |
| G6 | env-list 入口同步走 `BaseEnvProfileLoader.GetHardcodedDefaults()`(9 个 profile,**不**走 live fetch 也**不**走 user override JSON,跟既有 OpenBaseEnvProgress 兼容);BaseEnvView 走既有 `LoadAsync()`(user override → live → fallback) | spec §3 数据源分流 |
| G7 | picker UserControl 暴露 `SelectedProfiles` 双向 binding 到 ListBox;`SelectionMode` 改时同步 ListBox.SelectionMode | spec §4 |
| G8 | picker Dialog 静态入口跟 `BaseEnvProgressDialog.Show` 同 pattern(test seam `ShowOverride: Func<...>` 避免 WPF STA 死锁) | spec §4 + 既有 BaseEnvProgressDialog |
| G9 | 既有 v0.6.5.22 卸载/互斥逻辑、既有"已装 guard"、mutex mark / unmark、Load + RaiseCommandsChanged 全部不动 | spec §风险 |
| G10 | resx +3 keys:`Picker_Title` / `Picker_Ok` / `Picker_Cancel`(en + zh-CN) | spec §1 新增文件 |
| G11 | 测试不依赖 git;无新 nuget;base SHA = `a17458a` (spec commit) | 工程惯例 |
| G12 | 无 v-bump / 无 release zip(per `feedback_no_rebuild_zip.md` 不变不 rebuild;`feedback_no_zip.md` 不主动 zip);但 staging rebuild per `feedback_staging_self_contained.md`(self-contained publish) | 工程惯例 |
| G13 | `MarkIncompatibleOlderVersions` 既给 hardcoded 路径加也保留 pytorch.org live 路径加;picker 接收的 profiles 由 caller 决定,VM 不重复打标 | spec §3 数据源分流 + v0.6.5.22 |
| G14 | 既有 v0.6.5.22 `BaseEnvViewModel.OnSelectedVersionChangedAsync` 行为保留;picker 改 SelectedVersion 走 VM 自己的 `_loadGeneration` 防 stale 覆盖 | spec §6 + BaseEnvViewModel.cs:42-44 |
| G15 | 中文错误文案(跟 v0.6.5.6+ 一致):"无可用 profile,无法部署" / "请至少选择 1 个 profile" / "请选择" | spec §6 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProfilePickerViewModel.cs` | ~110 | PickerSelectionMode enum + Versions/Profiles/SelectedVersion/SelectedProfiles/Result + OkCommand/CancelCommand |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml` | ~60 | ComboBox(torch) + ListBox(CUDA) + Description 面板的 UserControl |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml.cs` | ~25 | code-behind,ListBox ↔ UserControl.SelectedProfiles 双向同步 |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml` | ~30 | Window 套壳,标题 + 嵌入 UserControl + OK/Cancel 按钮 |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml.cs` | ~50 | 静态 `Show(...)` 入口 + `ShowOverride` test seam |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProfilePickerViewModelTests.cs` | ~180 | 10 测试 |
| `tests-wpf/ComfyUI.Manager.Tests/Views/BaseEnvProfilePickerViewTests.cs` | ~50 | 3 测试(无 WPF,纯 property 同步逻辑) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenBaseEnvTests.cs` | ~120 | 5 测试(picker cancel / 返回 profile / 空 profiles / env busy) |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelReselectTests.cs` | ~70 | 3 测试(改选 / 取消 / preselected 传 picker) |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml` | 删 inline ComboBox + ListBox;改顶部 [当前选择: ...] TextBlock + [改选...] 按钮 |
| `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs` | 删 `OnProfileSelectionChanged` / `OnEnvSelectionChanged`;加 `OnReselectClicked` |
| `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs` | 加 `ReselectCommand`(单参数或无参数)+ `PickerDialogOverride` test seam;既有 `SetSelectedProfiles` / `SetSelectedVersion` 不动 |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | `OpenBaseEnvProgress` 加 picker Dialog 弹窗;既有 all-done guard + mutex guard 不动 |
| `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx` | +3 keys:`Picker_Title` / `Picker_Ok` / `Picker_Cancel` |

### Delete

无。

### Keep (unchanged)

- `BaseEnvInstaller` / `BaseEnvProgressDialog` / `BaseEnvProgressViewModel`(G9)
- v0.6.5.22 mutex / per-env 互斥逻辑(G9)
- `BaseEnvProfileLoader.GetHardcodedDefaults` / `GetLiveDefaultsAsync` / `LoadAsync`(只读)
- `BaseEnvViewModel.SelectedVersion` / `SelectedProfiles` / `Versions` / `IsUserOverrideActive` / `LoadAsync` / `Start` / `SetSelectedProfiles` / `SetSelectedEnvIds`(G14 + G5)

---

## Tasks

### Task 1: `BaseEnvProfilePickerViewModel` + 10 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProfilePickerViewModel.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProfilePickerViewModelTests.cs`

**Interfaces:**

Produces:
```csharp
namespace ComfyUI.Manager.ViewModels;

public enum PickerSelectionMode { Single, Multi }

public sealed class BaseEnvProfilePickerViewModel : ViewModelBase
{
    public BaseEnvProfilePickerViewModel(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode selectionMode);

    public PickerSelectionMode SelectionMode { get; }
    public IReadOnlyList<PyTorchVersionEntry> Versions { get; }
    public PyTorchVersionEntry? SelectedVersion { get; set; }
    public IReadOnlyList<BaseEnvProfile> Profiles { get; private set; }
    public IReadOnlyList<BaseEnvProfile> SelectedProfiles { get; set; }

    /// <summary>OK/Cancel Command 设置;Show() 用它返 null/非 null。</summary>
    public IReadOnlyList<BaseEnvProfile>? Result { get; private set; }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
}
```

- [ ] **Step 1: Write 10 failing tests**(verbatim — `BaseEnvProfilePickerViewModelTests.cs`):

```csharp
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BaseEnvProfilePickerViewModelTests
{
    private static BaseEnvProfile Profile(string torch, string cuda = "cu118") =>
        new() { Id = $"torch=={torch}+{cuda}", TorchVersion = torch, CudaVersion = cuda, CudaVariant = cuda };

    private static PyTorchVersionEntry Entry(string version, bool nightly = false) =>
        new() { Version = version, IsNightly = nightly, DisplayName = nightly ? "PyTorch Nightly" : $"PyTorch {version}" };

    [Fact]
    public void Constructor_Multi_InitializesProfilesFromInput()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        Assert.Equal(PickerSelectionMode.Multi, vm.SelectionMode);
        Assert.NotEmpty(vm.Versions);
        Assert.NotEmpty(vm.Profiles);
    }

    [Fact]
    public void Constructor_Single_PreselectsDefault()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.Single(vm.SelectedProfiles);
        Assert.Equal(profiles[0], vm.SelectedProfiles[0]);
    }

    [Fact]
    public void SelectedVersion_Changes_FiltersProfiles()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.5.0", "cu118") };
        var versions = new[] { Entry("2.4.1"), Entry("2.5.0") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        vm.SelectedVersion = versions[1];
        Assert.Single(vm.Profiles);
        Assert.Equal("2.5.0", vm.Profiles[0].TorchVersion);
    }

    [Fact]
    public void SelectedVersion_Changes_ClearsSelectedProfiles()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.5.0", "cu118") };
        var versions = new[] { Entry("2.4.1"), Entry("2.5.0") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Multi);
        Assert.Single(vm.SelectedProfiles);
        vm.SelectedVersion = versions[1];
        Assert.Empty(vm.SelectedProfiles);
    }

    [Fact]
    public void Constructor_EmptyProfiles_DoesNotThrow()
    {
        var vm = new BaseEnvProfilePickerViewModel(Array.Empty<BaseEnvProfile>(), preselected: null, PickerSelectionMode.Single);
        Assert.Empty(vm.Profiles);
        Assert.Empty(vm.Versions);
        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedProfiles_Set_NotifiesBinding()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedProfiles)) raised = true; };
        vm.SelectedProfiles = new[] { profiles[1] };
        Assert.True(raised);
    }

    [Fact]
    public void PickerMode_Multi_OkReturnsAllSelected()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        vm.SelectedProfiles = profiles;
        Assert.True(vm.OkCommand.CanExecute(null));
        vm.OkCommand.Execute(null);
        Assert.Equal(profiles, vm.Result);
    }

    [Fact]
    public void PickerMode_Single_OkReturnsFirstOrNull()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.True(vm.OkCommand.CanExecute(null));
        vm.OkCommand.Execute(null);
        Assert.NotNull(vm.Result);
        Assert.Single(vm.Result!);

        vm.SelectedProfiles = Array.Empty<BaseEnvProfile>();
        Assert.False(vm.OkCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void OkCommand_CanExecute_Multi_RequiresAtLeastOne()
    {
        var profiles = new[] { Profile("2.4.1", "cu118") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: null, PickerSelectionMode.Multi);
        Assert.False(vm.OkCommand.CanExecute(null));
        vm.SelectedProfiles = profiles;
        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void SelectionMode_Single_SetMoreThanOne_Throws()
    {
        var profiles = new[] { Profile("2.4.1", "cu118"), Profile("2.4.1", "cu121") };
        var vm = new BaseEnvProfilePickerViewModel(profiles, preselected: profiles[0], PickerSelectionMode.Single);
        Assert.Throws<ArgumentException>(() => vm.SelectedProfiles = profiles);
    }
}
```

- [ ] **Step 2: Run tests, verify 10/10 FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfilePickerViewModelTests" -v minimal`
Expected: 编译错 `BaseEnvProfilePickerViewModel` 不存在 + 测试 FAIL

- [ ] **Step 3: 实现 VM**(`BaseEnvProfilePickerViewModel.cs` verbatim):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public enum PickerSelectionMode { Single, Multi }

/// <summary>
/// v0.6.6:被 <see cref="Views.BaseEnvProfilePickerDialog"/> 包装的 picker VM。
/// ComboBox 选 torch 版本 → ListBox 过滤出该 torch 下的 CUDA 变体 → 单/多选。
/// 跨 torch 切换自动清空 SelectedProfiles,避免跨版本残留。
/// </summary>
public sealed class BaseEnvProfilePickerViewModel : ViewModelBase
{
    private readonly List<BaseEnvProfile> _allProfiles;
    private IReadOnlyList<BaseEnvProfile> _filteredProfiles = Array.Empty<BaseEnvProfile>();
    private PyTorchVersionEntry? _selectedVersion;
    private IReadOnlyList<BaseEnvProfile> _selectedProfiles = Array.Empty<BaseEnvProfile>();

    public BaseEnvProfilePickerViewModel(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode selectionMode)
    {
        if (profiles is null) throw new ArgumentNullException(nameof(profiles));
        SelectionMode = selectionMode;
        _allProfiles = profiles.ToList();

        // torch versions 去重 → ComboBox source。
        Versions = _allProfiles
            .Where(p => p.TorchVersion is not null)
            .Select(p => new PyTorchVersionEntry
            {
                Version = p.TorchVersion!,
                IsNightly = false,
                DisplayName = $"PyTorch {p.TorchVersion}",
            })
            .DistinctBy(e => e.Version)
            .ToList();

        // 默认选第一个 stable torch。
        if (Versions.Count > 0)
        {
            _selectedVersion = Versions[0];
            ApplyFilter();
        }

        if (preselected is not null && _filteredProfiles.Contains(preselected))
        {
            _selectedProfiles = new[] { preselected };
        }

        OkCommand = new RelayCommand(
            _ => Result = SelectedProfiles.ToList(),
            _ => SelectedProfiles.Count >= 1 && (SelectionMode == Multi || SelectedProfiles.Count == 1));
        CancelCommand = new RelayCommand(_ => Result = null);
    }

    public PickerSelectionMode SelectionMode { get; }

    public IReadOnlyList<PyTorchVersionEntry> Versions { get; }

    public PyTorchVersionEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                _selectedProfiles = Array.Empty<BaseEnvProfile>();
                RaisePropertyChanged(nameof(SelectedProfiles));
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<BaseEnvProfile> Profiles
    {
        get => _filteredProfiles;
        private set => SetField(ref _filteredProfiles, value);
    }

    public IReadOnlyList<BaseEnvProfile> SelectedProfiles
    {
        get => _selectedProfiles;
        set
        {
            if (SelectionMode == PickerSelectionMode.Single && value.Count > 1)
                throw new ArgumentException("Single selection mode 不允许多选", nameof(value));
            SetField(ref _selectedProfiles, value.ToList());
        }
    }

    public IReadOnlyList<BaseEnvProfile>? Result { get; private set; }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void ApplyFilter()
    {
        Profiles = _selectedVersion is null
            ? Array.Empty<BaseEnvProfile>()
            : _allProfiles.Where(p => p.TorchVersion == _selectedVersion.Version).ToList();
    }
}
```

- [ ] **Step 4: Run tests, verify 10/10 PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfilePickerViewModelTests" -v minimal`
Expected: 10 passed

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProfilePickerViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProfilePickerViewModelTests.cs
git commit -m "feat(wpf): BaseEnvProfilePickerViewModel ComboBox+ListBox 单/多选 picker (v0.6.6 T1)"
```

---

### Task 2: `BaseEnvProfilePickerView` UserControl + 3 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Views/BaseEnvProfilePickerViewTests.cs`

**Interfaces:**

Produces:
```csharp
namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerView : UserControl
{
    public BaseEnvProfilePickerView();

    public static readonly DependencyProperty ViewModelProperty = ...;
    public BaseEnvProfilePickerViewModel? ViewModel { get; set; }

    public static readonly DependencyProperty SelectionModeProperty = ...;
    public PickerSelectionMode SelectionMode { get; set; }

    public static readonly DependencyProperty SelectedProfilesProperty = ...;
    public IReadOnlyList<BaseEnvProfile> SelectedProfiles { get; set; }
}
```

- [ ] **Step 1: Write 3 failing tests**(verbatim — `BaseEnvProfilePickerViewTests.cs`):

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class BaseEnvProfilePickerViewTests
{
    private static BaseEnvProfile Profile(string cuda) =>
        new() { Id = $"torch==2.4.1+{cuda}", TorchVersion = "2.4.1", CudaVersion = cuda, CudaVariant = cuda };

    [Fact]
    public void SelectionMode_Set_UpdatesListBoxSelectionMode()
    {
        var view = new BaseEnvProfilePickerView { SelectionMode = PickerSelectionMode.Single };
        Assert.Equal(SelectionMode.Single, view.ProfileListBox.SelectionMode);
        view.SelectionMode = PickerSelectionMode.Multi;
        Assert.Equal(SelectionMode.Extended, view.ProfileListBox.SelectionMode);
    }

    [Fact]
    public void SelectedProfiles_Set_SelectsMatchingItemsInListBox()
    {
        var profiles = new[] { Profile("cu118"), Profile("cu121"), Profile("cu126") };
        var view = new BaseEnvProfilePickerView
        {
            ViewModel = new BaseEnvProfilePickerViewModel(profiles, null, PickerSelectionMode.Multi),
        };
        view.SelectedProfiles = new[] { profiles[0], profiles[2] };
        Assert.Equal(2, view.ProfileListBox.SelectedItems.Count);
        Assert.Contains(profiles[0], view.ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
        Assert.Contains(profiles[2], view.ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
    }

    [Fact]
    public void SelectedProfiles_Get_ReturnsListBoxSelection()
    {
        var profiles = new[] { Profile("cu118"), Profile("cu121"), Profile("cu126") };
        var view = new BaseEnvProfilePickerView
        {
            ViewModel = new BaseEnvProfilePickerViewModel(profiles, null, PickerSelectionMode.Multi),
        };
        view.ProfileListBox.SelectedItems.Add(profiles[1]);
        view.ProfileListBox.SelectedItems.Add(profiles[2]);
        var selected = view.SelectedProfiles;
        Assert.Equal(2, selected.Count);
        Assert.Contains(profiles[1], selected);
        Assert.Contains(profiles[2], selected);
    }
}
```

- [ ] **Step 2: Run tests, verify 3/3 FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfilePickerViewTests" -v minimal`
Expected: 编译错(`BaseEnvProfilePickerView` / `ProfileListBox` 不存在)

- [ ] **Step 3: 实现 View XAML + code-behind**

`Views/BaseEnvProfilePickerView.xaml`:
```xml
<UserControl x:Class="ComfyUI.Manager.Views.BaseEnvProfilePickerView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             xmlns:models="clr-namespace:ComfyUI.Manager.Models"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             d:DataContext="{d:DesignInstance Type=vm:BaseEnvProfilePickerViewModel}"
             mc:Ignorable="d"
             x:Name="Root">
    <UserControl.Resources>
        <Style TargetType="TextBlock" x:Key="CaptionStyle">
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Margin" Value="0,0,0,4" />
        </Style>
    </UserControl.Resources>
    <DockPanel>
        <!-- 顶部:torch 版本 ComboBox -->
        <StackPanel DockPanel.Dock="Top" Margin="0,0,0,12">
            <TextBlock Text="torch 版本:" Style="{StaticResource CaptionStyle}" />
            <ComboBox ItemsSource="{Binding ElementName=Root, Path=ViewModel.Versions}"
                      SelectedItem="{Binding ElementName=Root, Path=ViewModel.SelectedVersion}"
                      DisplayMemberPath="DisplayName" />
        </StackPanel>

        <!-- 中段:当前选中 profile 的 description(单选模式有用) -->
        <TextBlock DockPanel.Dock="Top" Margin="0,0,0,8"
                   Text="{Binding ElementName=Root, Path=ViewModel.SelectedProfiles[0].Description}"
                   Foreground="#888" TextWrapping="Wrap"
                   Visibility="{Binding ElementName=Root, Path=ViewModel.SelectedProfiles.Count, Converter={StaticResource ZeroCountToVisibility}}" />

        <!-- 底部 label -->
        <TextBlock DockPanel.Dock="Bottom" Text="CUDA 变体:" Style="{StaticResource CaptionStyle}" />

        <!-- 主体:CUDA 变体 ListBox -->
        <ListBox x:Name="ProfileListBox"
                 ItemsSource="{Binding ElementName=Root, Path=ViewModel.Profiles}"
                 SelectionMode="Extended"
                 SelectionChanged="OnListBoxSelectionChanged">
            <ListBox.ItemTemplate>
                <DataTemplate DataType="{x:Type models:BaseEnvProfile}">
                    <StackPanel Margin="6">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                            <Border Background="#1E88E5" CornerRadius="4"
                                    Padding="6,2" Margin="8,0,0,0"
                                    VerticalAlignment="Center">
                                <TextBlock Text="{Binding CudaVersion}" Foreground="White" FontSize="11" />
                            </Border>
                        </StackPanel>
                        <TextBlock Text="{Binding Description}" Foreground="#888"
                                   TextWrapping="Wrap" Margin="0,2,0,0" />
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </DockPanel>
</UserControl>
```

`Views/BaseEnvProfilePickerView.xaml.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(BaseEnvProfilePickerViewModel), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(PickerSelectionMode), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(PickerSelectionMode.Multi, (d, _) => ((BaseEnvProfilePickerView)d).ApplySelectionMode()));

    public static readonly DependencyProperty SelectedProfilesProperty = DependencyProperty.Register(
        nameof(SelectedProfiles), typeof(IReadOnlyList<BaseEnvProfile>), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(Array.Empty<BaseEnvProfile>(), (d, _) => ((BaseEnvProfilePickerView)d).ApplySelectedProfiles()));

    public BaseEnvProfilePickerView()
    {
        InitializeComponent();
        ApplySelectionMode();
    }

    public BaseEnvProfilePickerViewModel? ViewModel
    {
        get => (BaseEnvProfilePickerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PickerSelectionMode SelectionMode
    {
        get => (PickerSelectionMode)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public IReadOnlyList<BaseEnvProfile> SelectedProfiles
    {
        get => (IReadOnlyList<BaseEnvProfile>)GetValue(SelectedProfilesProperty);
        set => SetValue(SelectedProfilesProperty, value);
    }

    /// <summary>测试 seam:让测试访问 ListBox 实例来断言 SelectedItems 同步。</summary>
    public ListBox ProfileListBox => _profileListBox ??= (ListBox)FindName(nameof(ProfileListBox));
    private ListBox? _profileListBox;

    private void ApplySelectionMode()
    {
        if (ProfileListBox is null) return;
        ProfileListBox.SelectionMode = SelectionMode == PickerSelectionMode.Single
            ? SelectionMode.Single
            : SelectionMode.Extended;
    }

    private void ApplySelectedProfiles()
    {
        if (ProfileListBox is null || ViewModel is null) return;
        ProfileListBox.SelectedItems.Clear();
        foreach (var p in SelectedProfiles)
        {
            if (ProfileListBox.Items.Contains(p)) ProfileListBox.SelectedItems.Add(p);
        }
        ViewModel.SelectedProfiles = SelectedProfiles;
    }

    private void OnListBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        var selected = ProfileListBox.SelectedItems.Cast<BaseEnvProfile>().ToList();
        try
        {
            ViewModel.SelectedProfiles = selected;
            SetValue(SelectedProfilesProperty, selected);
        }
        catch (ArgumentException)
        {
            // Single mode 下用户用 Ctrl+Click 多选 → VM 抛异常,UI 状态回滚到第一项
            ProfileListBox.SelectedItems.Clear();
            if (selected.Count > 0) ProfileListBox.SelectedItems.Add(selected[0]);
            ViewModel.SelectedProfiles = ProfileListBox.SelectedItems.Cast<BaseEnvProfile>().ToList();
            SetValue(SelectedProfilesProperty, ViewModel.SelectedProfiles);
        }
    }
}
```

- [ ] **Step 4: Run tests, verify 3/3 PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfilePickerViewTests" -v minimal`
Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml.cs tests-wpf/ComfyUI.Manager.Tests/Views/BaseEnvProfilePickerViewTests.cs
git commit -m "feat(wpf): BaseEnvProfilePickerView UserControl ComboBox+ListBox picker (v0.6.6 T2)"
```

---

### Task 3: `BaseEnvProfilePickerDialog` Window + static `Show()` + test seam

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml.cs`

**Interfaces:**

Produces:
```csharp
namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerDialog : Window
{
    public BaseEnvProfilePickerDialog(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode);

    /// <summary>测试 seam:单测赋值模拟用户选择或取消。</summary>
    public static Func<
        IReadOnlyList<BaseEnvProfile>,
        BaseEnvProfile?,
        PickerSelectionMode,
        IReadOnlyList<BaseEnvProfile>?>? ShowOverride { get; set; }

    public static IReadOnlyList<BaseEnvProfile>? Show(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode);
}
```

- [ ] **Step 1: 实现 XAML + code-behind**

`Views/BaseEnvProfilePickerDialog.xaml`:
```xml
<Window x:Class="ComfyUI.Manager.Views.BaseEnvProfilePickerDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:ComfyUI.Manager.Views"
        Title="选择基础环境组合"
        Height="520" Width="640"
        Background="{StaticResource BackgroundBrush}"
        WindowStartupLocation="CenterOwner">
    <DockPanel Margin="16">
        <!-- 顶部说明 -->
        <Border DockPanel.Dock="Top" Padding="8" Margin="0,0,0,12"
                Background="#2A2A2A" BorderBrush="Gray" BorderThickness="1">
            <TextBlock x:Name="HintTextBlock" Text="请选择基础环境组合"
                       FontSize="13" />
        </Border>

        <!-- 底部按钮 -->
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button x:Name="OkButton" Content="确定" Width="80"
                    IsDefault="True" Click="OnOkClicked"
                    Style="{StaticResource MaterialButton}" />
            <Button x:Name="CancelButton" Content="取消" Width="80"
                    Margin="8,0,0,0" IsCancel="True" Click="OnCancelClicked"
                    Style="{StaticResource MaterialButton}" />
        </StackPanel>

        <!-- 中间:picker UserControl -->
        <views:BaseEnvProfilePickerView x:Name="Picker" />
    </DockPanel>
</Window>
```

`Views/BaseEnvProfilePickerDialog.xaml.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerDialog : Window
{
    /// <summary>
    /// 测试 seam:生产代码 ShowDialog 弹 WPF Window 阻塞 UI 线程;
    /// 单测可赋值 ShowOverride 模拟用户选择或取消。
    /// </summary>
    public static Func<
        IReadOnlyList<BaseEnvProfile>,
        BaseEnvProfile?,
        PickerSelectionMode,
        IReadOnlyList<BaseEnvProfile>?>? ShowOverride { get; set; }

    private readonly BaseEnvProfilePickerViewModel _vm;

    public BaseEnvProfilePickerDialog(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode)
    {
        InitializeComponent();
        _vm = new BaseEnvProfilePickerViewModel(profiles, preselected, mode);
        Picker.ViewModel = _vm;
        Picker.SelectionMode = mode;
        if (preselected is not null) Picker.SelectedProfiles = new[] { preselected };

        // OK 按钮 enable/disable 跟 OkCommand.CanExecute 联动。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.SelectedProfiles))
            {
                OkButton.IsEnabled = _vm.OkCommand.CanExecute(null);
                if (_vm.SelectedProfiles.Count > 0)
                {
                    HintTextBlock.Text = $"已选 {_vm.SelectedProfiles.Count} 个 profile";
                }
                else
                {
                    HintTextBlock.Text = mode == PickerSelectionMode.Single
                        ? "请选择 1 个 profile"
                        : "请选择至少 1 个 profile";
                }
            }
        };
    }

    /// <summary>
    /// 弹 picker dialog,返回选中 profile 列表。
    /// 用户取消 → 返回 null;无可用 profile → 弹 MessageBox + 返回 null。
    /// </summary>
    public static IReadOnlyList<BaseEnvProfile>? Show(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode)
    {
        if (profiles is null || profiles.Count == 0)
        {
            MessageBox.Show("无可用 profile,无法部署", "基础环境部署",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (ShowOverride is not null)
            return ShowOverride(profiles, preselected, mode);

        var dlg = new BaseEnvProfilePickerDialog(profiles, preselected, mode)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true ? dlg._vm.Result : null;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.OkCommand.CanExecute(null))
        {
            MessageBox.Show(
                _vm.SelectionMode == PickerSelectionMode.Single
                    ? "请选择 1 个 profile"
                    : "请选择至少 1 个 profile",
                "未选择",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _vm.OkCommand.Execute(null);
        DialogResult = _vm.Result is not null;
        if (DialogResult != true) Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _vm.CancelCommand.Execute(null);
        DialogResult = false;
        Close();
    }
}
```

- [ ] **Step 2: 编译 verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings(无新测试,本 task 编译验证即可)

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml.cs
git commit -m "feat(wpf): BaseEnvProfilePickerDialog Window 套壳 + Show() 静态入口 (v0.6.6 T3)"
```

---

### Task 4: `BaseEnvView` tab 改 UI + `BaseEnvViewModel.ReselectCommand` + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`(删 inline ComboBox+ListBox,加 [改选] 按钮)
- Modify: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs`(删 `OnProfileSelectionChanged` / `OnEnvSelectionChanged`,加 `OnReselectClicked`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`(加 `ReselectCommand` + `PickerDialogOverride` test seam)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelReselectTests.cs`(3 测试)

**Interfaces:**

Produces(在 `BaseEnvViewModel.cs` 加):
```csharp
public RelayCommand ReselectCommand { get; }

/// <summary>
/// 测试 seam:覆盖 BaseEnvProfilePickerDialog.Show;不设走真实 WPF dialog。
/// Func(profiles, preselected, mode) → selected list 或 null(取消)。
/// </summary>
public Func<
    IReadOnlyList<BaseEnvProfile>,
    BaseEnvProfile?,
    PickerSelectionMode,
    IReadOnlyList<BaseEnvProfile>?>? PickerDialogOverride { get; set; }
```

`ReselectCommand.CanExecute`:Load 完成(`Versions.Count > 0` 或 user override profile list 已加载)→ true;否则 false。
`ReselectCommand.Execute`:
1. 取当前 `_selectedProfiles.FirstOrDefault()` 作 preselected
2. 取当前 `Versions`(多版本)或 user override profile list 作 profiles
3. 调 `PickerDialogOverride` 或 `BaseEnvProfilePickerDialog.Show(profiles, preselected, Multi)`
4. 返 null → bail
5. 返 list → 调 `SetSelectedProfiles(picked)` + `SetSelectedVersion(newSelectedVersion)`

`SetSelectedVersion` 复用既有方法(已有 `_loadGeneration` 防 stale 覆盖,G14)。

- [ ] **Step 1: Write 3 failing tests**(verbatim — `BaseEnvViewModelReselectTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BaseEnvViewModelReselectTests : IDisposable
{
    private static BaseEnvProfile Profile(string torch, string cuda) =>
        new() { Id = $"torch=={torch}+{cuda}", TorchVersion = torch, CudaVersion = cuda, CudaVariant = cuda };

    private static (BaseEnvViewModel vm, TestBaseEnvProfileLoader loader, TestDb db) MakeVm()
    {
        var db = new TestDb();
        var appDataDir = Path.Combine(Path.GetTempPath(), $"picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDataDir);
        var loader = new TestBaseEnvProfileLoader(appDataDir);
        var directory = new TestPyTorchVersionDirectory(appDataDir);
        var envRepo = new EnvironmentRepository(db.Factory);
        var installer = new FakeBaseEnvInstaller(envRepo);
        var vm = new BaseEnvViewModel(loader, envRepo, installer, directory, appDataDir);
        vm.PickerDialogOverride = (_, _, _) => null;
        return (vm, loader, db);
    }

    [Fact]
    public void ReselectCommand_PickerReturnsSelection_UpdatesSelectedProfiles()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        var p2 = Profile("2.4.1", "cu121");
        loader.Hardcoded = new[] { p1, p2 };
        vm.LoadAsync().GetAwaiter().GetResult();
        vm.PickerDialogOverride = (_, _, _) => new[] { p2 };
        vm.ReselectCommand.Execute(null);
        Assert.Single(vm.SelectedProfiles);
        Assert.Equal(p2, vm.SelectedProfiles[0]);
        db.Dispose();
    }

    [Fact]
    public void ReselectCommand_PickerCancel_DoesNotChangeSelection()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        loader.Hardcoded = new[] { p1 };
        vm.LoadAsync().GetAwaiter().GetResult();
        Assert.Single(vm.SelectedProfiles);
        var before = vm.SelectedProfiles.ToList();
        vm.PickerDialogOverride = (_, _, _) => null;
        vm.ReselectCommand.Execute(null);
        Assert.Equal(before, vm.SelectedProfiles);
        db.Dispose();
    }

    [Fact]
    public void ReselectCommand_Preselected_PassesToPicker()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        var p2 = Profile("2.4.1", "cu121");
        loader.Hardcoded = new[] { p1, p2 };
        vm.LoadAsync().GetAwaiter().GetResult();
        BaseEnvProfile? capturedPreselected = null;
        vm.PickerDialogOverride = (_, pre, _) => { capturedPreselected = pre; return null; };
        vm.ReselectCommand.Execute(null);
        Assert.NotNull(capturedPreselected);
        Assert.Equal(p1, capturedPreselected);
        db.Dispose();
    }

    // ---- test fakes(参考既有 BaseEnvViewModelTests pattern:TestDb + FakeDirectory + FakeBaseEnvInstaller) ----
    private sealed class TestBaseEnvProfileLoader : BaseEnvProfileLoader
    {
        public IReadOnlyList<BaseEnvProfile> Hardcoded { get; set; } = Array.Empty<BaseEnvProfile>();
        public TestBaseEnvProfileLoader(string appDataDir) : base(appDataDir) { }
        public override Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(System.Threading.CancellationToken ct = default)
            => Task.FromResult(Hardcoded);
        public override IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults() => Hardcoded;
    }

    private sealed class TestPyTorchVersionDirectory : PyTorchVersionDirectory
    {
        public TestPyTorchVersionDirectory(string scratchDir)
            : base(new PyTorchVersionCatalog(http: null!), new NoopCache(scratchDir)) { }
        public override Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(System.Threading.CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersionEntry>>(new[]
            {
                new PyTorchVersionEntry { Version = "2.4.1", DisplayName = "PyTorch 2.4.1" },
            });
    }

    private sealed class NoopCache : PyTorchVersionCatalogCache
    {
        public NoopCache(string appDataDir) : base(appDataDir) { }
        public override Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(System.Threading.CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersion>?>(null);
        public override Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, System.Threading.CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller(IEnvironmentRepository envRepo) : base(envRepo) { }
    }
}
```

**注意:** `TestBaseEnvProfileLoader` 用 `: base(http: null, cacheDir: null, logger: null)` 跟既有 fake pattern 一致(看既有 `BaseEnvViewModelTests.cs`)。如果 `BaseEnvProfileLoader` 的 ctor 签名不同,以实际为准(看 `src-wpf/.../Data/BaseEnvProfileLoader.cs:39`)。

- [ ] **Step 2: Run tests, verify 3/3 FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvViewModelReselectTests" -v minimal`
Expected: 编译错(`PickerDialogOverride` / `ReselectCommand` 不存在)

- [ ] **Step 3: 改 `BaseEnvViewModel.cs`**

在 `ReselectCommand` 之前位置加(参考既有 `StartCommand` 模式):
```csharp
/// <summary>
/// v0.6.6:弹 picker dialog 重新选 profile(改选...按钮)。
/// CanExecute:Load 完成(profiles 已加载)。
/// </summary>
public RelayCommand ReselectCommand { get; }

/// <summary>
/// 测试 seam:覆盖 BaseEnvProfilePickerDialog.Show;不设走真实 WPF dialog。
/// </summary>
public Func<
    IReadOnlyList<BaseEnvProfile>,
    BaseEnvProfile?,
    PickerSelectionMode,
    IReadOnlyList<BaseEnvProfile>?>? PickerDialogOverride { get; set; }
```

在 ctor 末尾(既有 `StartCommand = new RelayCommand(...)` 之后)加:
```csharp
ReselectCommand = new RelayCommand(
    _ => Reselect(),
    _ => _selectedProfiles.Count >= 0 && (IsUserOverrideActive || _directory is not null));
```

加 `Reselect()` 私有方法(放在 `Start()` 之前):
```csharp
private void Reselect()
{
    // 多版本模式从 Versions 拿当前 torch 的 profile;user override 模式从 _selectedProfiles 拿
    var profiles = _allLoadedProfilesCache;  // 见下面 Load() 改动
    var preselected = _selectedProfiles.FirstOrDefault();

    var picked = PickerDialogOverride is not null
        ? PickerDialogOverride(profiles, preselected, PickerSelectionMode.Multi)
        : BaseEnvProfilePickerDialog.Show(profiles, preselected, PickerSelectionMode.Multi);

    if (picked is null || picked.Count == 0) return;

    SetSelectedProfiles(picked);
    // 同步 SelectedVersion 跟新 profile 的 torch 版本
    var newTorch = picked[0].TorchVersion;
    var matchingVersion = Versions.FirstOrDefault(v => v.Version == newTorch);
    if (matchingVersion is not null && matchingVersion != SelectedVersion)
    {
        SetSelectedVersion(matchingVersion);
    }
    StartCommand.RaiseCanExecuteChanged();
}
```

加 `_allLoadedProfilesCache` 字段(在 ctor 顶部字段声明区):
```csharp
private IReadOnlyList<BaseEnvProfile> _allLoadedProfilesCache = Array.Empty<BaseEnvProfile>();
```

在 `LoadAsync()` 末尾(user override 路径 + 多版本路径分别)给 `_allLoadedProfilesCache` 赋值:
```csharp
// user override 路径(已有 _selectedProfiles.AddRange(...))
_allLoadedProfilesCache = userOverrideProfiles;
...
// 多版本路径(已有 Versions.Add(...); ... ReplaceProfiles(...))
// 注意:多版本路径需要把所有 torch 的 profile 都拉下来 — 走 _directory.GetAllAsync 拿到
// entry 后,每个 entry 调 _loader.LoadProfilesForVersionAsync 拿 profile
// 然后合并成 _allLoadedProfilesCache
_allLoadedProfilesCache = allProfilesFlattened;
```

**简化**:user override 路径 cache 已经是 user override profile list;多版本路径用既有 `LoadAsync` 已经处理的 profiles(需要看现有代码是怎么把所有 profile 拉平的)。参考 `src-wpf/.../ViewModels/BaseEnvViewModel.cs:158-202` 现有 Load 逻辑,把最终的 profile list 同步给 `_allLoadedProfilesCache`。

**SetSelectedVersion 方法**:`BaseEnvViewModel.cs` 现有 setter 已经有 `_loadGeneration` 保护(G14),如果不存在就加:
```csharp
public void SetSelectedVersion(PyTorchVersionEntry? value)
{
    if (value == _selectedVersion) return;
    _loadGeneration++;
    _selectedVersion = value;
    RaisePropertyChanged(nameof(SelectedVersion));
    // fire-and-forget reload
    _ = OnSelectedVersionChangedAsync(value);
}
```

- [ ] **Step 4: 改 `BaseEnvView.xaml`(删 inline,加 改选 button)**

完整新 XAML(replace 整个 UserControl 内容):
```xml
<UserControl x:Class="ComfyUI.Manager.Views.BaseEnvView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             xmlns:models="clr-namespace:ComfyUI.Manager.Models"
             d:DataContext="{d:DesignInstance Type=vm:BaseEnvViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <DockPanel>
        <!-- 顶部:当前选择 + 改选按钮 -->
        <Border DockPanel.Dock="Top" Padding="8" Margin="12,12,12,8"
                Background="#2A2A2A" BorderBrush="Gray" BorderThickness="1">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="当前选择:" FontWeight="SemiBold" />
                    <TextBlock x:Name="CurrentSelectionText"
                               Text="{Binding SelectedProfiles.Count, Converter={StaticResource ...}, FallbackValue=未选择}"
                               Foreground="#888" Margin="0,2,0,0" />
                </StackPanel>
                <Button Grid.Column="1" Content="改选..."
                        Command="{Binding ReselectCommand}"
                        VerticalAlignment="Center"
                        Style="{StaticResource MaterialButton}" />
            </Grid>
        </Border>

        <!-- 底部:开始部署 -->
        <Button DockPanel.Dock="Bottom" Content="开始部署"
                Margin="12,8,12,12" HorizontalAlignment="Right"
                Command="{Binding StartCommand}"
                Style="{StaticResource MaterialButton}" />

        <!-- 主体:env 选择(ListBox,保留多选) -->
        <DockPanel Margin="12,0">
            <TextBlock DockPanel.Dock="Top" Text="目标环境"
                       FontWeight="SemiBold" Margin="0,0,0,4" />
            <ListBox x:Name="EnvListBox"
                     ItemsSource="{Binding Envs}"
                     SelectionMode="Extended"
                     SelectionChanged="OnEnvSelectionChanged">
                <ListBox.ItemTemplate>
                    <DataTemplate DataType="{x:Type models:Environment}">
                        <StackPanel Margin="6">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="{Binding Id}" FontWeight="SemiBold" />
                                <TextBlock Text="{Binding Name}" Margin="8,0,0,0" Foreground="#666" />
                            </StackPanel>
                            <TextBlock Text="{Binding Status}" Foreground="#888" FontSize="11" />
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>
    </DockPanel>
</UserControl>
```

**注意**:`CurrentSelectionText` 需要一个 converter 把 `SelectedProfiles.Count` 转成"torch 2.4.1 + cu118 (1 项已选)"。**简化**:用绑定到 `SelectedProfiles` 的多 binding converter 或者直接在 VM 加一个 `CurrentSelectionDisplay` property。**采纳后者** — 在 BaseEnvViewModel 加:
```csharp
public string CurrentSelectionDisplay =>
    _selectedProfiles.Count == 0
        ? "(未选择)"
        : $"{_selectedProfiles[0].TorchVersion} — {_selectedProfiles.Count} 个 CUDA 变体已选";
```
XAML 绑 `Text="{Binding CurrentSelectionDisplay}"`。

- [ ] **Step 5: 改 `BaseEnvView.xaml.cs`**

替换文件内容:
```csharp
using System.Windows.Controls;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvView : UserControl
{
    public BaseEnvView()
    {
        InitializeComponent();
    }

    private void OnEnvSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is BaseEnvViewModel vm)
        {
            vm.SetSelectedEnvIds(EnvListBox.SelectedItems.Cast<EnvModel>());
        }
    }
}
```

注意:删掉了 `OnProfileSelectionChanged` / `OnProfileListBox`。`ProfileListBox` / `Versions` ComboBox 全删。

- [ ] **Step 6: Run tests, verify 3/3 PASS + 既有 BaseEnvViewModelTests 不退化**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvViewModelReselectTests|FullyQualifiedName~BaseEnvViewModelTests" -v minimal`
Expected: 新增 3 + 既有 N 全 PASS

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelReselectTests.cs
git commit -m "feat(wpf): BaseEnvView 改选按钮 + picker dialog 共享 (v0.6.6 T4)"
```

---

### Task 5: `EnvironmentListViewModel.OpenBaseEnvProgress` 接 picker + 5 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`OpenBaseEnvProgress` 加 picker 弹窗)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenBaseEnvTests.cs`(5 测试)

**Interfaces:**

Modifies `EnvironmentListViewModel.OpenBaseEnvProgress`:
```csharp
private void OpenBaseEnvProgress()
{
    if (Selected is null && Environments.Count == 0) return;
    var envIds = Selected is not null
        ? new List<string> { Selected.Id }
        : Environments.Select(e => e.Id).ToList();
    if (envIds.Count == 0) return;

    // [既有] all-done guard + mutex guard(不动)
    var existingEnvs = envIds.Select(id => _repo.Get(id)).Where(e => e is not null).ToList();
    if (existingEnvs.Count == envIds.Count && existingEnvs.All(e => e!.BedStatus == "done"))
    {
        ShowAlreadyInstalled($"所选 env 已安装基础环境,无需再装:{string.Join(", ", existingEnvs.Select(e => e!.Name))}");
        return;
    }
    var busyEnv = existingEnvs.FirstOrDefault(e => e is not null && IsEnvBusy(e!));
    if (busyEnv is not null)
    {
        ShowInfoDialog($"env '{busyEnv.Name}' 正在执行其他操作,请稍候", "无法部署基础环境");
        return;
    }

    // [v0.6.6] picker dialog
    var profiles = _profileLoader.GetHardcodedDefaults();  // 同步 9 个 profile
    var preselected = profiles.FirstOrDefault();
    var picked = PickerDialogOverride is not null
        ? PickerDialogOverride(profiles, preselected, PickerSelectionMode.Single)
        : BaseEnvProfilePickerDialog.Show(profiles, preselected, PickerSelectionMode.Single);
    if (picked is null || picked.Count == 0) return;  // 用户取消或无可用
    var profile = picked.First();

    foreach (var e in existingEnvs) MarkEnvBusy(e!, BusyKind.BEDInstall);
    try
    {
        if (ShowProgressDialogOverride is not null)
        {
            ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
            return;
        }
        Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
    }
    finally
    {
        foreach (var e in existingEnvs) UnmarkEnvBusy(e!);
        Load();
        RaiseCommandsChanged();
    }
}
```

在 `EnvironmentListViewModel` 加 test seam:
```csharp
/// <summary>
/// 测试 seam:覆盖 BaseEnvProfilePickerDialog.Show。
/// Func(profiles, preselected, mode) → selected list 或 null(取消)。
/// </summary>
public Func<
    IReadOnlyList<BaseEnvProfile>,
    BaseEnvProfile?,
    PickerSelectionMode,
    IReadOnlyList<BaseEnvProfile>?>? PickerDialogOverride { get; set; }
```

- [ ] **Step 1: Write 5 failing tests**(verbatim — `EnvironmentListViewModelOpenBaseEnvTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelOpenBaseEnvTests
{
    private static EnvironmentListViewModel MakeVm(TestDb db)
    {
        // 用真实 BaseEnvProfileLoader(硬编码 9 个 profile)+ TestDb + null deps。
        // PickerDialogOverride + ShowProgressDialogOverride + MessageBoxOverride 都通过 vm 赋值。
        // ctor 顺序:repo, launcher, envCreator, baseInstaller, settings, profileLoader,
        // envDeleter, nodeOps, projectRoot, requirementsInstaller, baseEnvUninstaller?, requirementsUninstaller?
        // 跟既有 EnvironmentListViewModelUninstallTests.cs:81 一致。
        var profileLoader = new BaseEnvProfileLoader(
            Path.Combine(Path.GetTempPath(), "picker-env-list-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            Path.Combine(Path.GetTempPath(), "picker-env-list-proj-" + Guid.NewGuid()),
            null!);
        return vm;
    }

    private static Environment MakeEnv(string id, string status, string? bedStatus = null) =>
        new()
        {
            Id = id, Name = id, RootPath = $"C:\\envs\\{id}",
            Status = status, BedStatus = bedStatus,
        };

    [Fact]
    public void OpenBaseEnvProgress_EnvAlreadyDone_BailsBeforePicker()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: "done");
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        string? lastMsg = null;
        vm.MessageBoxOverride = msg => lastMsg = msg;
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(pickerCalled);
        Assert.False(launched);
        Assert.NotNull(lastMsg);
        Assert.Contains("已安装", lastMsg!);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerCancel_DoesNotLaunchInstall()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.True(pickerCalled);
        Assert.False(launched);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerReturnsProfile_LaunchesInstall()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        var profile = new BaseEnvProfile { Id = "torch==2.4.1+cu128" };
        vm.PickerDialogOverride = (_, _, _) => new[] { profile };
        BaseEnvProfile? capturedProfile = null;
        vm.ShowProgressDialogOverride = (_, p, _) => capturedProfile = p;
        vm.BaseEnvCommand.Execute(null);
        Assert.NotNull(capturedProfile);
        Assert.Equal("torch==2.4.1+cu128", capturedProfile!.Id);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerReturnsEmpty_BailsWithMessage()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        vm.PickerDialogOverride = (_, _, _) => Array.Empty<BaseEnvProfile>();
        string? lastMsg = null;
        vm.MessageBoxOverride = msg => lastMsg = msg;
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(launched);
        Assert.NotNull(lastMsg);
        Assert.Contains("请选择", lastMsg!);
    }

    [Fact]
    public void OpenBaseEnvProgress_EnvBusy_BailsBeforePicker()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        // 模拟 env busy:走反射访问 private _envBusy 字典。
        // 注意:key 是 RootPath 不是 Id(per v0.6.5.22 T4 mutex 设计)。
        // BusyKind 是 private nested enum,这里用 enum 索引值 5(Start)
        // 跟 EnvironmentListViewModel.cs:39 声明顺序对应。
        var busyField = typeof(EnvironmentListViewModel).GetField("_envBusy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = busyField!.GetValue(vm) as System.Collections.IDictionary;
        dict!.Add(env.RootPath, 5);  // BusyKind.Start
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(pickerCalled);
        Assert.False(launched);
    }
}
```

**注意**:
- 第一个 test 是 `OpenBaseEnvProgress_EnvAlreadyDone_BailsBeforePicker`(取代原占位 `OpenBaseEnvProgress_NoProfiles_ShowsMessageAndReturns`)— 沿用 v0.6.5.19.1 hotfix 的 all-done 短路路径,验证 env 已装基础环境时 picker 不被弹起 + MessageBox "已安装" 触发。原占位要测的"无可用 profile"分支需要 mock network,留作集成测试覆盖。
- 测试构造函数沿用既有 `EnvironmentListViewModelTests.cs` 的 10 参数 `new EnvironmentListViewModel(...)` 模式(repo, nodeOps, baseInstaller, requirementsInstaller, requirementsStatus, profileLoader, logger, baseEnvUninstaller, requirementsUninstaller, ...)— 以工程实际 ctor 为准。

- [ ] **Step 2: Run tests, verify 5/5 FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelOpenBaseEnvTests" -v minimal`
Expected: 编译错或 FAIL

- [ ] **Step 3: 改 `EnvironmentListViewModel.cs`**

按上面 `OpenBaseEnvProgress` 完整改写;加 `PickerDialogOverride` test seam;既有 `ShowProgressDialogOverride` / `MessageBoxOverride` / `ConfirmDialogOverride` 不动。

- [ ] **Step 4: Run tests, verify 5/5 PASS + 既有 EnvListVM 测试不退化**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelOpenBaseEnvTests|FullyQualifiedName~EnvironmentListViewModelTests|FullyQualifiedName~EnvironmentListViewModelBedTests|FullyQualifiedName~EnvironmentListViewModelUninstallTests" -v minimal`
Expected: 5 新 + 既有 N 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenBaseEnvTests.cs
git commit -m "feat(wpf): EnvironmentListViewModel OpenBaseEnvProgress 接 picker (v0.6.6 T5)"
```

---

### Task 6: resx +3 keys + final verify + staging rebuild

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.resx`
- Modify: `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx`

**修改清单:**
resx +3 keys:
- `Picker_Title`:zh-CN=`"选择基础环境组合"` / en=`"Choose Base Environment"`
- `Picker_Ok`:zh-CN=`"确定"` / en=`"OK"`
- `Picker_Cancel`:zh-CN=`"取消"` / en=`"Cancel"`

**关键设计点:**
- 这 3 个 key 在 `BaseEnvProfilePickerDialog.xaml` / code-behind 里直接 hardcode 中文(G15)— resx key 是为后续 i18n 准备,不立即接线
- `BaseEnvView.xaml` 的 "改选..." "当前选择:" "开始部署" "目标环境" 也是 hardcode 中文,跟既有 BaseEnvView 一致(既有也是 hardcode,没走 resx)— 不动

- [ ] **Step 1: 加 resx keys**

`Strings.resx`(英文版,加在文件末尾附近既有 `EnvList_UninstallBaseEnv` 等 key 之后):
```xml
<data name="Picker_Title" xml:space="preserve">
    <value>Choose Base Environment</value>
</data>
<data name="Picker_Ok" xml:space="preserve">
    <value>OK</value>
</data>
<data name="Picker_Cancel" xml:space="preserve">
    <value>Cancel</value>
</data>
```

`Strings.zh-CN.resx`(中文版):
```xml
<data name="Picker_Title" xml:space="preserve">
    <value>选择基础环境组合</value>
</data>
<data name="Picker_Ok" xml:space="preserve">
    <value>确定</value>
</data>
<data name="Picker_Cancel" xml:space="preserve">
    <value>取消</value>
</data>
```

- [ ] **Step 2: full build verify**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 3: full test verify**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 基线 522 + 新增 ~21 = **~543 PASS / 0 FAIL / 1 SKIP**

- [ ] **Step 4: 重建 staging per `feedback_staging_self_contained.md`**

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

- [ ] **Step 5: `git status --short`**

Expected: working tree clean(staging exe 时间戳变动 gitignored)

- [ ] **Step 6: 无 v-bump / 无 zip / 无 ledger commit**

per G12 + `feedback_no_zip.md` + `feedback_no_rebuild_zip.md` 不变不 rebuild 规则

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Resources/Strings.resx src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx
git commit -m "feat(wpf): v0.6.6 picker resx +3 keys + staging rebuild"
```

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
  期望 **~543 PASS / 0 FAIL / 1 SKIP**(基线 522 + T1 10 + T2 3 + T4 3 + T5 5 = 543)
- Python: 不涉及

### 端到端手动测试(用户 desktop,走 staging exe)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧栏"基础环境" → BaseEnvView tab:
   - **不再**看到 inline ComboBox + ListBox
   - 顶部 [当前选择: torch 2.4.1 — 1 个 CUDA 变体已选] + [改选...] 按钮
3. 点 [改选...] → 弹 picker dialog:
   - ComboBox 显示 torch 2.4.1(默认第一个)
   - ListBox 显示 5 个 CUDA 变体
   - 选 torch 2.5.0 → ListBox 自动刷新成 2.5.0 的 CUDA + SelectedProfiles 清空
   - 选 1 个 CUDA → OK → dialog 关闭 → BaseEnvView 顶部更新成 torch 2.5.0 + 该 CUDA
4. 选完点 [开始部署] → 进 BaseEnvProgressDialog → 装刚才选的 profile
5. 回到 env-list tab → 点工具栏 "基础环境部署" → 弹 picker dialog(**单选模式**,ListBox.SelectionMode=Single):
   - ComboBox 默认 torch 2.4.1
   - ListBox 显示 5 个 CUDA 变体
   - 选 torch 2.3.0 + cu121 → OK → 进 BaseEnvProgressDialog → 装 2.3.0+cu121
6. 验证 "已装" guard 仍生效:env 全 BedStatus="done" → 弹 MessageBox "已安装",**不**弹 picker
7. Picker dialog 中途换 torch 验证:在 ListBox 已选 1 个 CUDA 时切 torch → ListBox 选择清空 + SelectedProfiles 空(避免跨版本残留)
8. Picker dialog 取消按钮:点 Cancel → 不改 state,入口 bail

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| `BaseEnvProfilePickerView` 用 `ProfileListBox` field 后 `FindName` 在 test 环境可能 NRE | 测试构造时 `InitializeComponent()` 跑过 → `FindName` 找得到;既有 `EnvListView.xaml.cs` 同样 pattern |
| `BaseEnvView.xaml` 删 inline ComboBox + ListBox → 失去 inline 列表预览 UX | 用户先点 [改选] 看 dialog 列表,看完再 OK;dialog 是模态全屏,信息密度不降 |
| env-list 路径同步用 `GetHardcodedDefaults()` 而非 `GetLiveDefaultsAsync()`(live fetch) | 硬编码 9 个 profile 够选;改 async 需调整 ctor/wiring;future work 转 async |
| `SetSelectedVersion` 现有 setter 没有 public method,要加 public 包装 | BaseEnvViewModel 现有 setter 是 private;加 public `SetSelectedVersion(value)` 包装 |
| 单选模式 OK 时 `SelectedProfiles.Count > 1`(用户用 Ctrl+Click 多选) | VM setter 抛 `ArgumentException`;View code-behind catch 后回滚 ListBox 选择 + 重新 set VM;实际 WPF ListBox.SelectionMode=Single 已经阻止多选 |
| User override 用户设了 JSON,env-list 工具栏仍装硬编码 torch 2.4.1 | 设计决策(G6);若后续想统一,在 OpenBaseEnvProgress 也走 override 即可 |
| `_allLoadedProfilesCache` 在 LoadAsync 多版本路径需要把所有 torch 的 profile 拉平 | 走 `_directory.GetAllAsync()` 拿 entry 列表 → 每个 entry 调 `_loader.LoadProfilesForVersionAsync(entry.Version, ct)` → 合并;参考既有 `BaseEnvViewModel.LoadAsync` 多版本分支 |
| `ZeroCountToVisibility` converter 是否有注册 | 既有 Theme.xaml 应该注册了(v0.6.5.14 hotfix 加了);XAML 里 `ZeroCountToVisibility` 引用;若没有就在 Theme.xaml 加,跟 `BoolToVisibility` 同模式 |
| `CurrentSelectionDisplay` property 在 SelectedProfiles 改后不 INPC | 加 INPC:property getter 委托 `RaisePropertyChanged(nameof(CurrentSelectionDisplay))` 当 SelectedProfiles 改时(`SetSelectedProfiles` 末尾) |
| Test seam `PickerDialogOverride` 在 OpenBaseEnvProgress 和 BaseEnvViewModel.ReselectCommand 都用 | 两边独立 property(各 VM 各自的),不是 static;不会冲突 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvProfilePickerViewModel.cs`(new)
- `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerView.xaml` + `.cs`(new)
- `src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml` + `.cs`(new)
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml` + `.cs`(改)
- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`(加 ReselectCommand + SetSelectedVersion + CurrentSelectionDisplay)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(OpenBaseEnvProgress 接 picker)
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` + `Strings.zh-CN.resx`(+3 keys)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvProfilePickerViewModelTests.cs`(new, 10)
- `tests-wpf/ComfyUI.Manager.Tests/Views/BaseEnvProfilePickerViewTests.cs`(new, 3)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelReselectTests.cs`(new, 3)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelOpenBaseEnvTests.cs`(new, 5)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 6 task(VM + View + Dialog + BaseEnvView 接 + EnvListVM 接 + final verify)= 6 dispatch
- Per-task review gate(sonnet implementer + sonnet reviewer)
- T1 + T2 + T4 + T5 单元测试覆盖核心逻辑;T3 + T6 编译 + 集成测试
- Estimated 6 commits on main

(Plan agent left out: 设计 spec 已由用户 brainstorm 通过(5 决策),无 design ambiguity;既有 `BaseEnvProfileLoader` / `BaseEnvView` / `BaseEnvViewModel` / `EnvironmentListViewModel` 都熟悉,可直接给 implementer brief 跟 task 一起跑。)

If this plan is relevant to the current work and not already complete, continue working on it.