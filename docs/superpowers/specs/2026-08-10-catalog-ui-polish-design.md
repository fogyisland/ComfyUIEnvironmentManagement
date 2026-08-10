# Catalog 界面 UI Polish 设计

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task.

**Goal:** 把 Catalog 视图(列表 DataGrid / 磁贴 / 详情面板 / 顶部工具栏)按 v0.6.10.2 env-list card 风格统一重做,4 区视觉一致、组件感强、暗/亮主题全 palette 切换无残留。

**Architecture:** 纯 View-only 重做。`Theme.xaml` 加新 styles + `DataTemplate`(segmented control + catalog card + detail section);`CatalogView.xaml` 重写 toolbar + 详情面板分组 + list 模式 ListBox card 替 DataGrid;`CatalogViewModel` **不动**(行为/字段全保留)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · STA-thread headless load tests

**base SHA:** `226953e` (v0.6.11++ Settings pip mirror + common nodes 末尾,HEAD,844/0/1 baseline)

---

## Context

v0.6.10.2 hotfix 把 env-list 从 DataGrid 改成 ListBox card 风格后,用户评价 "好看了但 Catalog 还是太丑"。Catalog 沿用了 v0.6.7 末期的 DataGrid + 裸 ComboBox + 平铺 StackPanel 样式,跟新版 env-list 形成明显落差。本次重做范围:4 个区一次到位。

保留所有现有功能(搜索、分页、视图切换、刷新、详情加载、下载、Requirements 折叠、超链接)。不引入新依赖。不改 VM。

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | **跟 v0.6.10.2 env-list card 风格严格一致**:SurfaceBrush 背景 / OutlineBrush 1px 边框 / PrimaryBrush 2px 选中边框 / CornerRadius=6 / Padding=12;badges 用 SecondaryBrush / SuccessBrush / OutlineBrush | v0.6.10.2 视觉规范 |
| **G2** | **WPF `Setter` 引用 palette 必须 property-element + `DynamicResource`**;ControlTemplate 内 Setter 用 attribute 写法允许;任何新加 `Setter Property="..." Value="{StaticResource ...}"` 必须 grep 三个 pattern 验证 | v0.6.9.2 教训,`feedback_wpf_style_setter_dynamic_resource.md` |
| **G3** | **不动 public API / VM 接口 / Settings 字段**;纯 View-only 重做,所有现有 command binding / property binding 保持不变 | 项目惯例(G9 不破坏既有子系统) |
| **G4** | **不引入新依赖**(System.Text.Json / xUnit / WPF / Microsoft.Data.Sqlite / 现有主题系统);所有 brush / style 复用现有 palette | G7 延展 |
| **G5** | **暗/亮主题切换无残留**:所有颜色引用走 `DynamicResource` palette key,禁止 hardcode `#xxx`;测试覆盖 `CatalogViewLoadTests` 暗 + 亮 2 个 STA load tests | v0.6.10.2 同款 |
| **G6** | **测试不写脆弱 UI 行为**(点击 / 滚动 / 选中),只 STA-thread headless `new CatalogView().Measure/Arrange` 不抛 XamlParseException;沿用 v0.6.10.2 `EnvironmentListViewLoadTests` 模式 | 项目惯例(G8) |
| **G7** | **保留所有现有 command + binding**:`SetListViewCommand` / `SetTileViewCommand` / `RefreshCommand` / `CancelRefreshCommand` / `DownloadCommand` / `PrevPageCommand` / `NextPageCommand` / `Query` / `Selected` / `SelectedTitle` / `SelectedVersions` / `SelectedVersion` / `SelectedVersionDate` / `SelectedAuthor` / `SelectedInstallType` / `SelectedLastUpdate` / `SelectedDescription` / `SelectedReference` / `SelectedReferenceUrl` / `HasSelected` / `SelectedPipRequirements` / `HasPipRequirements` / `IsListMode` / `IsTileMode` / `IsBusy` / `RefreshPercent` / `ErrorMessage` / `InfoMessage` / `ProgressMessage` / `HasEntries` / `DownloadButtonLabel`;`OnRepoLinkClick` 保留 | VM 接口冻结 |
| **G8** | **catalog tile Install 按钮**:现状是 `InstallCommand`(下载到 env),如已有 v0.6.5.9 改为 `DownloadCommand`(下载到 local-nodes),按当前代码真实状态;不修改 VM | G3 延伸 |
| **G9** | **每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer**,严格匹配 `progress.md` ledger | SDD 流程 |

---

## 设计详解

### 1. 顶部工具栏(替换 v0.6.11++ 末期的 5 列扁平 Grid)

**当前丑点:** 5 个元素(搜索 / 列表·磁贴 / 刷新 / 取消 / 进度条)挤一行,列表·磁贴用 2 个 MaterialButton + BoolToBrush 高亮其中一个(看上去像 toggle 但其实是两个独立按钮),无 section title。

**目标布局(3 列 Grid,跟 env-list 同款):**
```
[Column 0: Auto]    [Column 1: *]    [Column 2: Auto]
┌──────────┐                            ┌────────────────────────┐
│ 节点目录 │                            │ [搜索框 240px]         │
│ 共 N 个  │                            │ [☰列表 ▦磁贴]  [刷新]  │
└──────────┘                            │ [取消] [进度条]        │
                                        └────────────────────────┘
```

- Column 0: StackPanel 左对齐
  - `TextBlock "节点目录"` FontSize=20 FontWeight=Bold,Foreground=`{DynamicResource OnSurfaceBrush}`
  - `TextBlock "共 N 个节点"` FontSize=11 Foreground=`{DynamicResource OutlineBrush}`,Margin="0,2,0,0",绑定 `Entries.Count`(新 VM 暴露 property,从 `PagedEntries.Count` 改名 → 保留原 `HasEntries` 也 OK,**新增 `EntryCountText` property 更清晰**)
- Column 1: spacer
- Column 2: 4 行 StackPanel 右对齐
  - Row 0: `MaterialTextBox` Search Query + segmented view toggle
  - Row 1: `MaterialButton` 刷新 / `MaterialButton` 取消(`Visibility=IsBusy`)/ `ProgressBar`(Width=160 Height=20,`Visibility=IsBusy`)
- segmented view toggle:2 个 `RadioButton` 共享 `GroupName="CatalogViewMode"`,各自绑 `IsChecked="{Binding IsListMode}"` / `IsChecked="{Binding IsTileMode}"`,用新加的 `Style="CatalogSegmentedRadioButton"` (新建在 Theme.xaml)

**Segmented Toggle 样式(`Theme.xaml` 新增):**
- 容器:`StackPanel Orientation="Horizontal"`,外层 `Border CornerRadius=4 BorderBrush={DynamicResource OutlineBrush}` 内含 2 个 `RadioButton`
- RadioButton 样式:无默认圆点,`Padding=10,6`,选中态 `Background={DynamicResource PrimaryBrush} Foreground={DynamicResource OnPrimaryBrush}`,未选 `Background=Transparent Foreground={DynamicResource OnSurfaceBrush}`

### 2. 列表模式(替换 v0.6.7 末期裸 DataGrid)

**当前丑点:** 默认 WPF DataGrid,无 padding/zebra/卡片感,column header 是中文硬编码,行高默认紧凑,选中态蓝色高亮(非 palette)。

**目标:`ListBox` card,跟 env-list 完全一致**

- 容器:`ListBox`,`Background=Transparent`,`BorderThickness=0`,`ScrollViewer.HorizontalScrollBarVisibility=Disabled`,`HorizontalContentAlignment=Stretch`
- `ListBox.ItemContainerStyle`:跟 env-list 同款 — Background=Transparent, Padding=0, Margin=0,0,0,8, HorizontalContentAlignment=Stretch, Template=`ControlTemplate` 仅渲染 `ContentPresenter`(去默认蓝色高亮)
- `ListBox.ItemTemplate`:`DataTemplate` 套 `Border` — Padding=12, CornerRadius=6, Background=`{DynamicResource SurfaceBrush}`, BorderThickness=1(默认), Style.Triggers 选中态 BorderBrush=PrimaryBrush + BorderThickness=2 + Padding=11(抵消 1px 视觉偏移)
- 每张卡内容:`Grid` 3 行
  - Row 0: Package (FontSize=16 Bold, OnSurfaceBrush) + ⭐ 数 (FontSize=12, OutlineBrush),右对齐
  - Row 1: 作者 (OutlineBrush) · install_type pill badge
  - Row 2: 说明摘要 (FontSize=12, OnSurfaceBrush, MaxHeight=40, TextTrimming=CharacterEllipsis)
- 选中逻辑:`SelectedItem="{Binding Selected}"` 保持不变

**Pill badge 样式(在 Theme.xaml 加 `CatalogInstallTypeBadgeStyle`):**
- `Border Padding=6,2 CornerRadius=3 Background={DynamicResource SecondaryBrush} Margin=4,0,0,0`
- 内 `TextBlock FontSize=10 Foreground={DynamicResource OnPrimaryBrush}` 绑 install_type string

### 3. 磁贴模式(基于现有 `CatalogTileTemplate` 升级)

**当前状态:** Width=320 Padding=12 Margin=8,3 行内容(标题 + 作者·⭐ + 描述 + 安装按钮)。已卡片化但偏小,信息密度低。

**目标:** 拓宽 + 加 1 行 metadata,信息密度跟列表模式持平

- Width 320 → 340,Height 不固定(`<Border.VerticalAlignment>Top</Border.VerticalAlignment>`)
- Margin="8" 不变;Padding="14";CornerRadius="8"
- Border 背景/边框/选中态继承现有 `CatalogTileItemContainerStyle`(OutlineBrush 1px,Selected PrimaryBrush 2px)
- 内部 `StackPanel` 4 行:
  - Row 0: Package (FontSize=16 Bold) + ⭐ 数 (FontSize=12),右对齐 ⭐
  - Row 1: 作者 · install_type pill badge(同列表模式)
  - Row 2: 描述 (MaxHeight=80, TextTrimming=CharacterEllipsis)
  - Row 3: `MaterialButton` "下载" HorizontalAlignment=Right(从"安装"改"下载",跟 G8 现状对齐;若 VM 仍叫 `DownloadCommand` 则 button label 跟随 `DownloadButtonLabel`)

### 4. 详情面板(现有 Border 内 StackPanel 重做分组)

**当前丑点:** 版本下拉是裸 ComboBox(非 MaterialButton 同款风格);Requirements 行背景跟 Surface 同色边界感弱;Download 按钮 Padding=20,6 略偏大;Description 无间距;无分组 Divider。

**目标:** 卡片化 + 分组 + Divider + 自定义 ComboBox 样式

- 外层 Border:`Padding=16→20`,`CornerRadius=6→8`,Background+BorderBrush 保持 SurfaceBrush/OutlineBrush 1px
- 内部 `StackPanel` 各组之间插 `Border Height=1 Background={DynamicResource OutlineBrush} Margin=0,12`(分隔线)
- **Group 1: Header** — 标题 FontSize=22 Bold + install_type pill badge 同行
- **Group 2: Metadata** — `Grid` 2 列 × N 行,"Label" 列 OnSurfaceBrush FontSize=12,"Value" 列 OutlineBrush FontSize=12,Row Margin=6
  - 作者 / 安装类型 / 最后更新
- **Group 3: 版本选择** — `Grid` 2 列
  - Col 0: 自定义 ComboBox 样式 `CatalogVersionComboBoxStyle`(新建 Theme.xaml) — 跟 MaterialButton 同款 CornerRadius=4 BorderBrush=SecondaryBrush Background=SurfaceBrush 1px border Padding=8,6
  - Col 1: "发布: YYYY-MM-DD" TextBlock
- **Group 4: Requirements**(保留 Expander) — 每条 requirement Border(Padding=6,4, BackgroundBrush, CornerRadius=3)+ Consolas FontSize=11
- **Group 5: 链接** — Hyperlink + 仓库 URL,Foreground=`{DynamicResource PrimaryBrush}`
- **Group 6: 描述** — TextBlock TextWrapping=Wrap,FontSize=13,LineHeight multiplier 4
- **Group 7: 操作** — `MaterialButton` 绑 `DownloadCommand`,Padding=24,8,Margin=0,16,0,0

---

## Public API(冻结,无新增)

```csharp
// CatalogViewModel — 不动
public ObservableCollection<CatalogEntry> PagedEntries { get; }
public ObservableCollection<VersionInfo> SelectedVersions { get; }
public RelayCommand RefreshCommand { get; }
public RelayCommand DownloadCommand { get; }
// ... 所有现有 public members 全保留

// 新增 View-only property(可选,非必须):
public string EntryCountText => $"共 {_allEntries.Count} 个节点";
// — 若实现,T1 在 VM 末尾追加一行;否则 Column 0 计数绑 HasEntries + 计算 converter
```

---

## Style 资源清单(`Resources/Theme.xaml` 新增)

| Key | TargetType | 用途 |
|---|---|---|
| `CatalogSegmentedRadioButton` | RadioButton | 视图切换 segmented control,PrimaryBrush 选中态 |
| `CatalogInstallTypeBadgeStyle` | Border | install_type pill badge(SecondaryBrush 背景) |
| `CatalogVersionComboBoxStyle` | ComboBox | 详情面板版本下拉(自定义 1px border + 圆角) |
| `CatalogCardItemContainerStyle` | ListBoxItem | 列表模式容器(透明 + 0 padding + 0,0,0,8 margin,Template 仅 ContentPresenter) |
| `CatalogRowCardTemplate` | DataTemplate | 列表卡片内容(Border + 3 行 Grid) |

`CatalogTileItemContainerStyle` + `CatalogTileTemplate` 已有,**只改 `Width` / `Padding` / 行数**。

---

## 测试策略(STA load tests,沿用 v0.6.10.2)

```csharp
[Fact]
public void CatalogView_DarkTheme_LoadsWithoutException() { /* STA thread, Measure+Arrange */ }

[Fact]
public void CatalogView_LightTheme_LoadsWithoutException() { /* STA thread, Measure+Arrange */ }
```

2 个测试,headless 加载 CatalogView,断言不抛 XamlParseException。
不写交互测试(选中 / 点击 / 滚动)— 项目惯例。

---

## 4 Task 分解(SDD)

### T1: Theme.xaml 新增 styles + DataTemplates
**Files:** `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`
- 加 `CatalogSegmentedRadioButton`(RadioButton style + ControlTemplate)
- 加 `CatalogInstallTypeBadgeStyle`(Border style + pill background)
- 加 `CatalogVersionComboBoxStyle`(ComboBox style + ControlTemplate 同 MaterialButton 套路)
- 加 `CatalogCardItemContainerStyle`(ListBoxItem style + Template 仅 ContentPresenter)
- 加 `CatalogRowCardTemplate`(DataTemplate: Border + 3 行 Grid + install_type pill)
- 修改 `CatalogTileTemplate`(Width 320→340 + 1 行 metadata)

**Verification:**
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal  # 0/0
```

### T2: CatalogView.xaml toolbar + 详情面板重做
**Files:** `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`
- 5 列扁平 Grid → 3 列 Grid(title | spacer | actions)
- segmented RadioButton 替 2 个 MaterialButton
- Column 0 加 "节点目录" + "共 N 个节点"
- Column 2 actions StackPanel 4 行
- 详情面板外层 Border Padding 16→20 CornerRadius 6→8
- 详情面板内部 StackPanel 各组之间插 Divider
- 标题 FontSize 20→22
- Metadata 2 列 Grid
- 版本 ComboBox 用新 CatalogVersionComboBoxStyle
- Requirements 行 Border Padding 6,4 BackgroundBrush
- Description FontSize 13 + LineHeight
- Download 按钮 MaterialButton Padding=24,8

**Verification:**
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal  # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal --no-build  # 2 PASS
```

### T3: 列表模式 DataGrid → ListBox card
**Files:** `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`
- 删 DataGrid(行 84-94 当前)
- 加 ListBox 用 T1 的 `CatalogCardItemContainerStyle` + `CatalogRowCardTemplate`
- `ItemsSource="{Binding PagedEntries}" SelectedItem="{Binding Selected}"` 保留

**Verification:**
```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal  # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal --no-build  # 2 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build  # 844 PASS / 0 FAIL / 1 SKIP baseline 持平
```

### T4: final review(opus) + MEMORY + staging rebuild
- final whole-branch review dispatch(opus)
- 处理 findings(预计 0 fix rounds)
- staging rebuild `dotnet publish -c Release -r win-x64 --self-contained true`
- 跑全套 `dotnet test`
- 更新 MEMORY `project_catalog_ui_polish.md`

---

## Critical Files

**Modified:**
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` (+5 styles/templates + 1 modified template)
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(重写 toolbar + 详情面板 + 列表模式 ListBox card)
- `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`(NEW,2 STA load tests)

**Unchanged:**
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(G3 冻结)
- `src-wpf/ComfyUI.Manager/Themes/Palette.{Light,Dark}.xaml`(palette 不动)
- 所有 command / property / binding 名

---

## Verification(end-to-end)

按顺序验证 4 task commit 全 PASS:

```bash
# T1
git status --short
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# T2
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal  # 2 PASS

# T3
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal  # 2 PASS

# T4 final review
# (opus dispatch; expected APPROVED 0 findings)

# 合并后全套验证
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build       # 844+/0/1 baseline
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke(桌面验证,user):**
1. 启动 staging → 点侧栏"节点目录"
2. 顶部:看到 "节点目录" + "共 N 个节点" 标题;右侧 segmented 视图切换高亮当前模式
3. 列表模式:卡片堆叠,选中卡片 PrimaryBrush 2px 边框,无 DataGrid
4. 切磁贴:卡片更宽,带 install_type pill badge
5. 选 entry → 详情面板卡片化,各组间有 Divider
6. 版本下拉:跟 Material 风格一致(不是裸 ComboBox)
7. Requirements Expander 展开:每行 BackgroundBrush 边界
8. 暗/亮主题切换:无残留硬色

---

## Risks

| 风险 | 缓解 |
|---|---|
| ListBox card 选中态视觉偏移 1px(Padding 11 抵消) | 跟 v0.6.10.2 env-list 同款 pattern,已验证无偏移 |
| Segment RadioButton 替代 2 个 MaterialButton 可能让 `IsListMode` / `IsTileMode` setter 双向 binding 出问题 | T2 实施前先 grep `IsListMode` / `IsTileMode` 用法,确认现有 setter 接受外部 `IsChecked=true` |
| ComboBox 自定义样式跨 palette 切换时 Editable/Non-Editable 视觉差异 | T1 实施前先 `git show v0.6.10.2` 看 MaterialTextBox 同款 property-element 写法,严格照抄 |
| STA load test 不抓 ItemsControl 模板内 Setter bug(v0.6.9.2 教训) | T1 提交前 grep 三个 pattern:`<Setter Property="..." Value="{StaticResource ...}"`, `<Setter ... Value="{StaticResource ...}"`, `<Style.Triggers>` 内 `<Setter ... StaticResource>` |
| T1 `CatalogRowCardTemplate` install_type 数据结构若不存在于 CatalogEntry metadata | 实施前 grep `CatalogEntry.RawMetadata[install_type]` 是否被 service 填充;若否,用 `RawMetadata[author]` + ⭐ 替代,G3 不改 VM |
| `CatalogTileTemplate` 现有 `InstallCommand` binding 若已改名 `DownloadCommand`(v0.6.5.9 之后) | T2 实施前先 grep 真实 binding 名,严格按 VM 现状 |

---

## Self-Review

1. **Placeholder scan:** ✓ 无 TBD/TODO
2. **Internal consistency:** ✓ 4 区设计都引 DynamicResource palette;G1-G9 全覆盖
3. **Scope check:** ✓ 单 catalog view polish,未触及 env-list / dashboard / settings / splash
4. **Ambiguity check:**
   - `InstallCommand` → `DownloadCommand` 已在 Risks 第 6 项明确检查路径
   - "共 N 个节点" 数据源在 Public API section 标注可选(可新 VM property,也可 `HasEntries` + converter)

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例)**:
- 4 task × (implementer + reviewer) ≈ 8 dispatch
- T1/T2/T3 各 1 implementer + 1 reviewer
- T4 final whole-branch review(opus)+ MEMORY + staging rebuild
- 3 commits on main,1 final review commit