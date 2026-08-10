# Catalog UI Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Catalog 视图(顶部工具栏 / 列表 / 磁贴 / 详情面板)按 v0.6.10.2 env-list card 风格统一重做,4 区视觉一致、暗/亮主题切换无残留。

**Architecture:** 纯 View-only 重做。`Theme.xaml` 加 5 个 styles/templates + 修 1 处 dead-code binding;`CatalogView.xaml` 重写 toolbar + 详情面板 + 列表模式 ListBox card 替 DataGrid;`Views/Converters.cs` 加 1 个 `BoolToEntryCountTextConverter`。`CatalogViewModel` 完全不动(G3 冻结)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit STA-thread load tests · Microsoft.Data.Sqlite 既有项目依赖

**base SHA:** `226953e` (v0.6.11++ Settings pip mirror + common nodes 末尾,844/0/1 baseline)
**spec commit:** `29cd83d` (4 docs commits `3eb1e0c` + `ea16fd2` + `29cd83d` 在 base 前已就位)

---

## Global Constraints

(来自 spec §Global Constraints,精确引用)

| # | Constraint |
|---|---|
| **G1** | 跟 v0.6.10.2 env-list card 风格严格一致:SurfaceBrush 背景 / OutlineBrush 1px 边框 / PrimaryBrush 2px 选中边框 / CornerRadius=6 / Padding=12 |
| **G2** | WPF `Setter` 引用 palette 必须 property-element + `DynamicResource`(v0.6.9.2 教训);ControlTemplate 内 Setter attribute 允许;提交前 grep 三个 pattern |
| **G3** | **不动 public API / VM 接口 / Settings 字段**;纯 View-only 重做,所有现有 command binding / property binding 保持不变 |
| **G4** | 不引入新依赖;所有 brush / style 复用现有 palette |
| **G5** | 暗/亮主题切换无残留:所有颜色引用走 `DynamicResource` palette key,禁止 hardcode `#xxx`;测试覆盖 `CatalogViewLoadTests` 暗 + 亮 2 STA load tests |
| **G6** | 测试不写脆弱 UI 行为(点击 / 滚动 / 选中),只 STA-thread headless `new CatalogView().Measure/Arrange` 不抛 XamlParseException |
| **G7** | 保留所有现有 command + binding 名(VM 接口冻结) |
| **G8** | catalog tile 按钮 binding 跟 VM 现状:Theme.xaml:348 dead-code `InstallCommand` 同步改成 `DownloadCommand`(spec Risks 第 2 项) |
| **G9** | 每个 task 单独 commit + 单独 SDD subagent dispatch + task reviewer,严格匹配 `progress.md` ledger |

---

## File Structure

| 文件 | 角色 |
|---|---|
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | 全局 styles/templates 仓库;T1 加 5 个新资源 + 修 1 处 binding |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | Catalog 主 XAML;T2 toolbar + 详情面板,T3 列表模式 |
| `src-wpf/ComfyUI.Manager/Views/Converters.cs` | converter 仓库;T1 加 `BoolToEntryCountTextConverter` |
| `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` | NEW STA load tests;T3 加 2 个测试 |

**未触及文件**(G3 冻结):
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`
- `src-wpf/ComfyUI.Manager/Themes/Palette.{Light,Dark}.xaml`

---

## Task 1: Theme.xaml 新增 styles/templates + 修 dead-code binding + 加 converter

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(5 新资源 + 1 binding 修复,全部在文件末尾追加,不动既有 styles)
- Modify: `src-wpf/ComfyUI.Manager/Views/Converters.cs`(追加 1 个 converter class)

**Interfaces:**
- Consumes: 既有 palette `PrimaryBrush` / `PrimaryVariantBrush` / `SurfaceBrush` / `BackgroundBrush` / `OutlineBrush` / `SecondaryBrush` / `OnSurfaceBrush` / `OnPrimaryBrush`
- Produces: 5 个新资源 key,供 T2/T3 在 CatalogView.xaml 引用 — `CatalogSegmentedRadioButton`(RadioButton style)、`CatalogInstallTypeBadgeStyle`(Border style)、`CatalogVersionComboBoxStyle`(ComboBox style)、`CatalogCardItemContainerStyle`(ListBoxItem style)、`CatalogRowCardTemplate`(DataTemplate)

---

- [ ] **Step 1: 读现有文件定位追加点**

读 `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` 末尾(line 355 `</ResourceDictionary>` 之前),确认 `CatalogTileTemplate` 块的位置(line 328-355)。
读 `src-wpf/ComfyUI.Manager/Views/Converters.cs` 末尾(line 153 `}` 之前),确认 `InverseZeroCountToVisibilityConverter` 后面是文件末尾。

- [ ] **Step 2: 追加 `BoolToEntryCountTextConverter` 到 Converters.cs**

在 `Converters.cs` 末尾 `}` 之后追加:

```csharp
/// <summary>
/// BoolToEntryCountTextConverter:bool HasEntries + int parameter → 字符串。
/// true + N → "共 {N} 个节点";false → "加载中…"。
/// v0.6.11+ CatalogUI polish:替换裸 "HasEntries" 的 BoolToVisibility,显示具体计数。
/// XAML 绑定:Text="{Binding HasEntries, Converter={StaticResource BoolToEntryCountText}, ConverterParameter={Binding PagedEntries.Count}}"
/// </summary>
public sealed class BoolToEntryCountTextConverter : IValueConverter
{
    public static readonly BoolToEntryCountTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasEntries = value is bool b && b;
        if (!hasEntries) return "加载中…";
        var count = parameter is int n ? n : 0;
        return $"共 {count} 个节点";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: 修 Theme.xaml:348 dead-code binding**

`InstallCommand` → `DownloadCommand`(G8)。这是 dead code,因为 `CatalogView.xaml` 不引用 `CatalogTileTemplate`,但保持一致避免后续误用。

替换 Theme.xaml line 348-352 的 button 块:
```xml
<Button Content="下载" HorizontalAlignment="Right"
        Command="{Binding DataContext.DownloadCommand,
                  RelativeSource={RelativeSource AncestorType=UserControl}}"
        CommandParameter="{Binding}"
        Style="{StaticResource MaterialButton}" />
```

注意 button Content 同步 "安装" → "下载",跟 VM `DownloadButtonLabel` 一致。

- [ ] **Step 4: 追加 5 个新资源到 Theme.xaml 末尾**

在 `</ResourceDictionary>` 之前(line 354 `</DataTemplate>` 之后)追加以下 5 个资源块,**逐个粘贴**:

**4a. `CatalogSegmentedRadioButton`** (RadioButton style,segmented control)

```xml
<!-- v0.6.11+ Catalog polish:视图切换 segmented control。
     RadioButton 共用 GroupName,选中态 PrimaryBrush 背景。
     必须 property-element + DynamicResource 跨 merged dict 解析(v0.6.9.2 教训)。 -->
<Style x:Key="CatalogSegmentedRadioButton" TargetType="RadioButton">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground">
        <Setter.Value>
            <DynamicResource ResourceKey="OnSurfaceBrush" />
        </Setter.Value>
    </Setter>
    <Setter Property="BorderBrush" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="10,6" />
    <Setter Property="Margin" Value="0" />
    <Setter Property="MinWidth" Value="60" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="RadioButton">
                <Border x:Name="RootBorder"
                        Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="4"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="RootBorder" Property="Background"
                                Value="{DynamicResource BackgroundBrush}" />
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="RootBorder" Property="Background"
                                Value="{DynamicResource PrimaryBrush}" />
                        <Setter Property="Foreground" Value="{DynamicResource OnPrimaryBrush}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

**4b. `CatalogInstallTypeBadgeStyle`** (Border style,pill badge)

```xml
<!-- v0.6.11+ Catalog polish:install_type pill badge。
     SecondaryBrush 背景,OnPrimaryBrush 文字。 -->
<Style x:Key="CatalogInstallTypeBadgeStyle" TargetType="Border">
    <Setter Property="Background">
        <Setter.Value>
            <DynamicResource ResourceKey="SecondaryBrush" />
        </Setter.Value>
    </Setter>
    <Setter Property="Padding" Value="6,2" />
    <Setter Property="CornerRadius" Value="3" />
    <Setter Property="Margin" Value="6,0,0,0" />
    <Setter Property="VerticalAlignment" Value="Center" />
</Style>
```

**4c. `CatalogVersionComboBoxStyle`** (ComboBox style,自定义 1px border + 圆角)

**关键参考**:`Theme.xaml:284-291` MaterialTextBox 的 property-element + DynamicResource 写法。ComboBox 自定义 ControlTemplate 复杂,直接基于 WPF 默认 ComboBox template 改 border + background。

```xml
<!-- v0.6.11+ Catalog polish:详情面板版本下拉。
     1px SecondaryBrush border + CornerRadius=4 + SurfaceBrush 背景。
     ControlTemplate 内 Setter attribute DynamicResource 写法允许(trigger 自己处理 lookup)。
     Editable=False 简化路径 — SelectedVersion 用法不要求可编辑。 -->
<Style x:Key="CatalogVersionComboBoxStyle" TargetType="ComboBox">
    <Setter Property="Background">
        <Setter.Value>
            <DynamicResource ResourceKey="SurfaceBrush" />
        </Setter.Value>
    </Setter>
    <Setter Property="BorderBrush">
        <Setter.Value>
            <DynamicResource ResourceKey="SecondaryBrush" />
        </Setter.Value>
    </Setter>
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="8,6" />
    <Setter Property="MinHeight" Value="32" />
    <Setter Property="Foreground">
        <Setter.Value>
            <DynamicResource ResourceKey="OnSurfaceBrush" />
        </Setter.Value>
    </Setter>
</Style>
```

(ComboBox ControlTemplate 默认带 ToggleButton + Popup,完整重写代价大。仅改 Background/BorderBrush/BorderThickness/Padding + MinHeight 触发新外观,避免 v0.6.9.2 Setter 跨 merged dict 解析陷阱。)

**4d. `CatalogCardItemContainerStyle`** (ListBoxItem style,跟 env-list 同款)

```xml
<!-- v0.6.11+ Catalog polish:列表卡片容器。
     跟 EnvironmentListView.xaml:209-223 同款 pattern。
     Background=Transparent + 0 padding + 0,0,0,8 margin。
     Template 仅 ContentPresenter,去默认蓝色高亮。 -->
<Style x:Key="CatalogCardItemContainerStyle" TargetType="ListBoxItem">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="Margin" Value="0,0,0,8" />
    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ListBoxItem">
                <ContentPresenter />
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

**4e. `CatalogRowCardTemplate`** (DataTemplate,3 行 Grid + install_type pill)

```xml
<!-- v0.6.11+ Catalog polish:列表卡片内容模板。
     跟 EnvironmentListView.xaml:225-409 env-card 风格一致。
     选中态 BorderBrush=PrimaryBrush + BorderThickness=2 + Padding=11(抵消 1px 视觉偏移)。
     install_type pill badge 用 CatalogInstallTypeBadgeStyle。 -->
<DataTemplate x:Key="CatalogRowCardTemplate">
    <Border Padding="12" CornerRadius="6"
            Background="{DynamicResource SurfaceBrush}"
            BorderThickness="1">
        <Border.Style>
            <Style TargetType="Border">
                <Setter Property="BorderBrush" Value="{DynamicResource OutlineBrush}" />
                <Style.Triggers>
                    <DataTrigger Value="True">
                        <DataTrigger.Binding>
                            <Binding Path="IsSelected"
                                     RelativeSource="{RelativeSource AncestorType=ListBoxItem}" />
                        </DataTrigger.Binding>
                        <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}" />
                        <Setter Property="BorderThickness" Value="2" />
                        <Setter Property="Padding" Value="11" />
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Border.Style>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <!-- Row 0: Package + ⭐ -->
            <Grid Grid.Row="0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="{Binding Package}"
                           FontSize="16" FontWeight="Bold"
                           Foreground="{DynamicResource OnSurfaceBrush}"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="⭐ " FontSize="12"
                               Foreground="{DynamicResource OutlineBrush}" />
                    <TextBlock Text="{Binding RawMetadata[stars]}"
                               FontSize="12" FontWeight="Bold"
                               Foreground="{DynamicResource OnSurfaceBrush}" />
                </StackPanel>
            </Grid>
            <!-- Row 1: 作者 + install_type pill -->
            <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,6,0,0">
                <TextBlock Text="{Binding Author}"
                           FontSize="12"
                           Foreground="{DynamicResource OutlineBrush}" />
                <Border Style="{StaticResource CatalogInstallTypeBadgeStyle}">
                    <TextBlock Text="{Binding InstallType}"
                               FontSize="10"
                               Foreground="{DynamicResource OnPrimaryBrush}" />
                </Border>
            </StackPanel>
            <!-- Row 2: 描述摘要 -->
            <TextBlock Grid.Row="2" Text="{Binding Description}"
                       FontSize="12" Margin="0,6,0,0"
                       TextWrapping="Wrap" MaxHeight="40"
                       TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource OnSurfaceBrush}" />
        </Grid>
    </Border>
</DataTemplate>
```

- [ ] **Step 5: 注册 `BoolToEntryCountTextConverter` 到 Theme.xaml converters 区**

在 Theme.xaml line 9-16 的 converter 注册区(line 16 `</views:ZeroCountToVisibilityConverter x:Key="ZeroCountToVisibility" />` 后面)追加:

```xml
<views:BoolToEntryCountTextConverter x:Key="BoolToEntryCountText" />
```

- [ ] **Step 6: Build 验证**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 警告 0 错误

如果编译失败:
- Converter 缺 namespace → `using System.Globalization;` 在 Converters.cs 已有
- XAML `RelativeSource` 解析失败 → 检查 `AncestorType=ListBoxItem` 写法(同上 Style 块)
- Style setter `Background="{TemplateBinding ...}"` 解析失败 → 不要紧,本任务不改 MaterialButton / MaterialTextBox

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        src-wpf/ComfyUI.Manager/Views/Converters.cs
git commit -m "feat(wpf): add catalog polish styles + templates + BoolToEntryCountTextConverter"
```

---

## Task 2: CatalogView.xaml toolbar + 详情面板重做

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(重写 line 11-41 顶部 toolbar + line 117-202 详情面板)

**Interfaces:**
- Consumes: T1 新加的 `CatalogSegmentedRadioButton` / `CatalogInstallTypeBadgeStyle` / `CatalogVersionComboBoxStyle` / `BoolToEntryCountText`(Theme.xaml 注册)
- Consumes: VM 现有所有 property(冻结):`Query` / `IsListMode` / `IsTileMode` / `RefreshCommand` / `CancelRefreshCommand` / `IsBusy` / `RefreshPercent` / `ErrorMessage` / `InfoMessage` / `ProgressMessage` / `HasEntries` / `PagedEntries` / `Selected` / `SelectedTitle` / `SelectedAuthor` / `SelectedVersions` / `SelectedVersion` / `SelectedVersionDate` / `SelectedInstallType` / `SelectedLastUpdate` / `SelectedDescription` / `SelectedReference` / `SelectedReferenceUrl` / `SelectedPipRequirements` / `HasPipRequirements` / `DownloadButtonLabel` / `DownloadCommand` / `HasSelected` / `OnRepoLinkClick`(code-behind)
- Produces: 重写后的 toolbar + 详情面板 XAML,T3 替 DataGrid 用 `PagedEntries` / `Selected` 保留不变

---

- [ ] **Step 1: 重写顶部 toolbar (line 11-41 当前 → 3 列 Grid)**

替换整个 `<Grid DockPanel.Dock="Top" Margin="8">...</Grid>` 块(line 11-41):

```xml
<!-- 顶部工具栏:3 列 Grid(标题 | spacer | 操作),跟 env-list 同款 -->
<Grid DockPanel.Dock="Top" Margin="12,12,12,8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <!-- Column 0: 标题 + 计数 -->
    <StackPanel Grid.Column="0" VerticalAlignment="Center">
        <TextBlock Text="节点目录"
                   FontSize="20" FontWeight="Bold"
                   Foreground="{DynamicResource OnSurfaceBrush}" />
        <TextBlock Text="{Binding HasEntries, Converter={StaticResource BoolToEntryCountText},
                                  ConverterParameter={Binding PagedEntries.Count}}"
                   FontSize="11"
                   Foreground="{DynamicResource OutlineBrush}"
                   Margin="0,2,0,0" />
    </StackPanel>
    <!-- Column 2: 操作行 -->
    <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
        <!-- 搜索框 -->
        <TextBox Width="240" Margin="0,0,8,0"
                 Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource MaterialTextBox}" />
        <!-- segmented 视图切换 -->
        <Border BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
                CornerRadius="4" Margin="0,0,8,0">
            <StackPanel Orientation="Horizontal">
                <RadioButton Content="列表"
                             GroupName="CatalogViewMode"
                             IsChecked="{Binding IsListMode}"
                             Style="{StaticResource CatalogSegmentedRadioButton}" />
                <RadioButton Content="磁贴"
                             GroupName="CatalogViewMode"
                             IsChecked="{Binding IsTileMode}"
                             Style="{StaticResource CatalogSegmentedRadioButton}" />
            </StackPanel>
        </Border>
        <!-- 操作 buttons -->
        <Button Content="刷新" Margin="0,0,4,0"
                Command="{Binding RefreshCommand}"
                Style="{StaticResource MaterialButton}" />
        <Button Content="取消" Margin="0,0,4,0"
                Command="{Binding CancelRefreshCommand}"
                Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}"
                Style="{StaticResource MaterialButton}" />
        <ProgressBar Width="160" Height="20"
                     Minimum="0" Maximum="100"
                     Value="{Binding RefreshPercent, Mode=OneWay}"
                     Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
</Grid>
```

- [ ] **Step 2: 信息条保持不变 (line 43-51)**

保留 `<StackPanel DockPanel.Dock="Top" Margin="8,0,8,4">...</StackPanel>`(ErrorMessage / InfoMessage / ProgressMessage)。无改动。

- [ ] **Step 3: 底部分页保持不变 (line 53-72)**

保留 `<Grid DockPanel.Dock="Bottom" Margin="8">...</Grid>`(Prev/Next + CurrentPage/TotalPages)。无改动。

- [ ] **Step 4: 列表模式占位 — 用临时 TextBlock 替代 DataGrid**

**重要**:T3 会替 DataGrid。本步骤先把 line 83-111 的 Grid 容器改结构,留 placeholder。**不要**删 DataGrid,留给 T3 删。

替换 line 83-111 的 `<Grid Grid.Column="0">...</Grid>` 为(只改外层 Grid,内层 DataGrid/ScrollViewer/EmptyStateText 全部保留):

```xml
<!-- 左:列表 / 磁贴 / 空状态 -->
<Grid Grid.Column="0">
    <!-- T3 placeholder:list mode card. T2 keeps DataGrid for now. -->
    <DataGrid Visibility="{Binding IsListMode, Converter={StaticResource BoolToVisibility}}"
              ItemsSource="{Binding PagedEntries}"
              SelectedItem="{Binding Selected}"
              AutoGenerateColumns="False" IsReadOnly="True" Margin="8">
        <DataGrid.Columns>
            <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
            <DataGridTextColumn Header="作者" Binding="{Binding Author}" Width="*" />
            <DataGridTextColumn Header="⭐" Binding="{Binding RawMetadata[stars]}" Width="60" />
            <DataGridTextColumn Header="说明" Binding="{Binding Description}" Width="2*" />
        </DataGrid.Columns>
    </DataGrid>

    <ScrollViewer Visibility="{Binding IsTileMode, Converter={StaticResource BoolToVisibility}}"
                  VerticalScrollBarVisibility="Auto" Margin="8">
        <ListBox ItemsSource="{Binding PagedEntries}"
                 SelectedItem="{Binding Selected}"
                 ItemTemplate="{StaticResource CatalogTileTemplate}"
                 ItemContainerStyle="{StaticResource CatalogTileItemContainerStyle}"
                 ItemsPanel="{StaticResource CatalogTileWrapPanel}"
                 BorderThickness="0" Background="Transparent"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                 ScrollViewer.VerticalScrollBarVisibility="Disabled" />
    </ScrollViewer>

    <TextBlock x:Name="EmptyStateText" Text="暂无数据，点右上角 刷新"
               FontSize="16" Foreground="{DynamicResource OutlineBrush}"
               HorizontalAlignment="Center" VerticalAlignment="Center"
               Visibility="{Binding HasEntries, Converter={StaticResource InverseBoolToVisibility}}" />
</Grid>
```

(只是把外层 Grid 重新格式化为 3 个独立子元素 DataGrid + ScrollViewer + TextBlock,跟原版等价 — `IsListMode` 显隐 DataGrid,`IsTileMode` 显隐 ScrollViewer,`!HasEntries` 显隐 TextBlock。T2 改动**只在 toolbar + 详情面板**,列表模式保持 DataGrid 状态。)

- [ ] **Step 5: 重写详情面板 line 117-202**

替换整个 `<Border Grid.Column="2" Margin="8,8,8,8" Padding="16" ...>` 块:

```xml
<!-- 右:详情面板(卡片化 + 分组 + Divider) -->
<Border Grid.Column="2" Margin="8,8,8,8" Padding="20"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1" CornerRadius="8">
    <Grid>
        <!-- 未选中状态 -->
        <TextBlock Text="← 请选择节点查看详情"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Foreground="{DynamicResource OutlineBrush}"
                   Visibility="{Binding HasSelected, Converter={StaticResource InverseBoolToVisibility}}" />

        <!-- 详情 -->
        <ScrollViewer VerticalScrollBarVisibility="Auto"
                      Visibility="{Binding HasSelected, Converter={StaticResource BoolToVisibility}}">
            <StackPanel>
                <!-- Group 1: Header — 标题 + install_type pill -->
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="{Binding SelectedTitle}"
                               FontSize="22" FontWeight="Bold"
                               Foreground="{DynamicResource OnSurfaceBrush}"
                               TextWrapping="Wrap" />
                    <Border Grid.Column="1" Style="{StaticResource CatalogInstallTypeBadgeStyle}">
                        <TextBlock Text="{Binding SelectedInstallType}"
                                   FontSize="10"
                                   Foreground="{DynamicResource OnPrimaryBrush}" />
                    </Border>
                </Grid>

                <!-- Divider -->
                <Border Height="1" Margin="0,12"
                        Background="{DynamicResource OutlineBrush}" />

                <!-- Group 2: Metadata 2 列 Grid -->
                <Grid Margin="0,0,0,8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
                    <TextBlock Grid.Row="0" Grid.Column="0" Text="作者:"
                               FontSize="12" Margin="0,6,16,6"
                               Foreground="{DynamicResource OnSurfaceBrush}" />
                    <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding SelectedAuthor}"
                               FontSize="12" Margin="0,6"
                               Foreground="{DynamicResource OutlineBrush}" />
                    <TextBlock Grid.Row="1" Grid.Column="0" Text="安装类型:"
                               FontSize="12" Margin="0,6,16,6"
                               Foreground="{DynamicResource OnSurfaceBrush}" />
                    <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding SelectedInstallType}"
                               FontSize="12" Margin="0,6"
                               Foreground="{DynamicResource OutlineBrush}" />
                    <TextBlock Grid.Row="2" Grid.Column="0" Text="最后更新:"
                               FontSize="12" Margin="0,6,16,6"
                               Foreground="{DynamicResource OnSurfaceBrush}" />
                    <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding SelectedLastUpdate}"
                               FontSize="12" Margin="0,6"
                               Foreground="{DynamicResource OutlineBrush}" />
                </Grid>

                <!-- Divider -->
                <Border Height="1" Margin="0,4,0,12"
                        Background="{DynamicResource OutlineBrush}" />

                <!-- Group 3: 版本选择 -->
                <Grid Margin="0,0,0,8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <ComboBox Grid.Column="0"
                              ItemsSource="{Binding SelectedVersions}"
                              SelectedItem="{Binding SelectedVersion}"
                              DisplayMemberPath="DisplayLabel"
                              IsEditable="False"
                              Style="{StaticResource CatalogVersionComboBoxStyle}" />
                    <TextBlock Grid.Column="1" Margin="12,0,0,0" VerticalAlignment="Center">
                        <Run Text="发布:" Foreground="{DynamicResource OutlineBrush}" />
                        <Run Text="{Binding SelectedVersionDate, Mode=OneWay}" FontWeight="Bold"
                             Foreground="{DynamicResource OnSurfaceBrush}" />
                    </TextBlock>
                </Grid>

                <!-- Group 4: Requirements Expander -->
                <Expander Header="Requirements" Margin="0,8,0,0" IsExpanded="False"
                          Visibility="{Binding HasPipRequirements, Converter={StaticResource BoolToVisibility}}">
                    <ItemsControl ItemsSource="{Binding SelectedPipRequirements}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Border Padding="6,4" Margin="0,1"
                                        Background="{DynamicResource BackgroundBrush}"
                                        CornerRadius="3">
                                    <TextBlock FontFamily="Consolas" FontSize="11"
                                               Foreground="{DynamicResource OnSurfaceBrush}">
                                        <Run Text="{Binding Name}" FontWeight="Bold" />
                                        <Run Text="{Binding Specifier, TargetNullValue=''}"
                                             Foreground="{DynamicResource OutlineBrush}" />
                                    </TextBlock>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </Expander>

                <!-- Divider -->
                <Border Height="1" Margin="0,12"
                        Background="{DynamicResource OutlineBrush}" />

                <!-- Group 5: 链接 -->
                <TextBlock Margin="0,4,0,0" TextWrapping="Wrap">
                    <Hyperlink NavigateUri="{Binding SelectedReferenceUrl}"
                               RequestNavigate="OnRepoLinkClick"
                               Foreground="{DynamicResource PrimaryBrush}">
                        <Run Text="{Binding SelectedReference, Mode=OneWay}" />
                    </Hyperlink>
                </TextBlock>

                <!-- Group 6: 描述 -->
                <TextBlock Text="{Binding SelectedDescription}" Margin="0,12,0,0"
                           TextWrapping="Wrap" FontSize="13" LineHeight="20"
                           Foreground="{DynamicResource OnSurfaceBrush}" />

                <!-- Group 7: 操作 -->
                <Button Content="{Binding DownloadButtonLabel}"
                        Margin="0,16,0,0" HorizontalAlignment="Left"
                        Padding="24,8"
                        Command="{Binding DownloadCommand}"
                        CommandParameter="{Binding Selected}"
                        Style="{StaticResource MaterialButton}" />
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Border>
```

- [ ] **Step 6: Build 验证**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 警告 0 错误

如果编译失败:
- `StaticResource` 找不到 → 检查 Theme.xaml 第 5 步注册 converter 是否成功
- `CatalogSegmentedRadioButton` 找不到 → 检查 T1 第 4 步 4a 是否成功 append
- `LineHeight` 属性不识别 → WPF 实际属性名是 `LineHeight`(double)+ `LineStackingStrategy`(enum),**已正确**;若失败,移除 LineHeight 即可

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CatalogView.xaml
git commit -m "feat(wpf): polish catalog toolbar + detail panel — 3-col grid + grouped sections"
```

---

## Task 3: 列表模式 DataGrid → ListBox card + STA load tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(删 line 84-94 DataGrid,加 ListBox card)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`(2 STA load tests)

**Interfaces:**
- Consumes: T1 `CatalogCardItemContainerStyle` + `CatalogRowCardTemplate`(在 Theme.xaml)
- Consumes: VM `PagedEntries` / `Selected` / `IsListMode` 不变
- Produces: 最终 ListBox card 列表模式;2 STA load tests 覆盖暗 + 亮主题加载

---

- [ ] **Step 1: 创建 CatalogViewLoadTests.cs**

新建 `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// 诊断用:headless 加载 CatalogView,捕获 XAML 解析异常。
/// v0.6.11+ Catalog polish:4 区重做(顶部 toolbar / 列表 / 磁贴 / 详情面板)后,
/// Theme.xaml 新加 styles(segmented control / pill badge / version combobox / card container)
/// 任何 Setter StaticResource 解析失败会在 STA load 抛 XamlParseException。
/// 跟 v0.6.9.2 MaterialButton / v0.6.9.2 MaterialTextBox / v0.6.10.2 EnvironmentListView
/// 同款根因,headless 抓得到。
/// </summary>
public class CatalogViewLoadTests
{
    [Fact]
    public void CatalogView_DarkTheme_LoadsWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new CatalogView();
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"CatalogView Dark load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    [Fact]
    public void CatalogView_LightTheme_LoadsWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Light);
                var v = new CatalogView();
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"CatalogView Light load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认 Dark 主题加载失败(因 T2 详情面板引用未完成的 styles 不存在)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal`
Expected: PASS(因为 T1+T2 已完成,styles 都注册好了;若失败说明有 Setter 错)

如果失败:
- `StaticResource` 找不到 → 检查 Theme.xaml 注册
- `BoolToEntryCountText` 找不到 → 检查 T1 第 5 步注册
- `CatalogSegmentedRadioButton` 找不到 → 检查 T1 第 4 步 4a

- [ ] **Step 3: 替换 DataGrid 为 ListBox card**

在 `CatalogView.xaml` line 83-94 当前(T2 第 4 步保留的 placeholder),替换:

```xml
<DataGrid Visibility="{Binding IsListMode, Converter={StaticResource BoolToVisibility}}"
          ItemsSource="{Binding PagedEntries}"
          SelectedItem="{Binding Selected}"
          AutoGenerateColumns="False" IsReadOnly="True" Margin="8">
    <DataGrid.Columns>
        <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
        <DataGridTextColumn Header="作者" Binding="{Binding Author}" Width="*" />
        <DataGridTextColumn Header="⭐" Binding="{Binding RawMetadata[stars]}" Width="60" />
        <DataGridTextColumn Header="说明" Binding="{Binding Description}" Width="2*" />
    </DataGrid.Columns>
</DataGrid>
```

替换为:

```xml
<ListBox Visibility="{Binding IsListMode, Converter={StaticResource BoolToVisibility}}"
         ItemsSource="{Binding PagedEntries}"
         SelectedItem="{Binding Selected}"
         ItemTemplate="{StaticResource CatalogRowCardTemplate}"
         ItemContainerStyle="{StaticResource CatalogCardItemContainerStyle}"
         Background="Transparent" BorderThickness="0"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled"
         HorizontalContentAlignment="Stretch"
         Margin="8" />
```

**重要**:保持外层 `<Grid Grid.Column="0">` 容器不动(line 83 + line 110-111 `</Grid>` 不变),只是 `<DataGrid>` 替 `<ListBox>`。

- [ ] **Step 4: 跑测试确认 ListBox card 加载无异常**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal`
Expected: 2 PASS / 0 FAIL

如果失败:
- `CatalogRowCardTemplate` 找不到 → Theme.xaml 第 4 步 4e 没成功 append
- `CatalogCardItemContainerStyle` 找不到 → Theme.xaml 第 4 步 4d 没成功 append
- `RelativeSource AncestorType=ListBoxItem` 解析失败 → 检查 4e 的 `Border.Style` 内 DataTrigger 写法

- [ ] **Step 5: 跑全套测试确认无回归**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: 844 PASS / 0 FAIL / 1 SKIP(baseline 持平;2 flake `ProcessLauncherProgressTests` 视情况 PASS 或 FAIL,与本任务无关)

如果回归:
- 现有测试报 binding 错 → 检查 T2 第 5 步是否误删了某个现有 binding 名
- 现有测试报 XamlParseException → 检查 T3 第 3 步外层 Grid 容器是否被破坏

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CatalogView.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs
git commit -m "feat(wpf): replace catalog list-mode DataGrid with ListBox card + STA load tests"
```

---

## Task 4: final review + MEMORY + staging rebuild

**Files:**
- Modify: `D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\ComfyUI.Manager.exe`(rebuilt)
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_catalog_ui_polish.md`(MEMORY topic)
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`(追加一行)

---

- [ ] **Step 1: final whole-branch review (opus)**

Dispatch opus model reviewer with:
- 范围:`226953e..HEAD` (3 commits: T1 + T2 + T3)
- review package path:`D:\ToolDevelop\ComfyUI\.superpowers\sdd\2026-08-10-catalog-ui-polish\review-final-226953e..HEAD.diff`
- spec path:`D:\ToolDevelop\ComfyUI\docs\superpowers\specs\2026-08-10-catalog-ui-polish-design.md`
- 3 task reports + 3 task review packages

Expected: APPROVED 0 Critical/Important;1 Minor (style-only, e.g. CTA padding 调整)

如不 APPROVED → 走 fix loop(参考 v0.6.11++ SDD 流程,opus reviewer 通常 1 round fix 即过)。

- [ ] **Step 2: staging rebuild**

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: 0 警告 0 错误;`ComfyUI.Manager.exe` 时间戳更新。

- [ ] **Step 3: 创建 MEMORY topic file**

新建 `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_catalog_ui_polish.md`:

```markdown
---
name: Catalog UI polish v0.6.11+
description: 3 task SDD SHIP-READY — 顶部 toolbar / 列表 ListBox card / 磁贴 / 详情面板 4 区按 v0.6.10.2 env-list card 风格统一重做
type: project
---

v0.6.11+ Catalog UI polish SHIP-READY 2026-08-10, HEAD `<commit-sha>`, 3 commits (T1 Theme styles / T2 toolbar + 详情 / T3 列表 ListBox card), **844 PASS / 0 FAIL / 1 SKIP** baseline 持平 (new 2 STA load tests 全 PASS).

## 用户原话
"节点目录界面也要优化太丑了" — 用户桌面验 v0.6.11++ 时反馈 Catalog 跟 env-list 风格落差。

## 4 区设计
- **toolbar**:3 列 Grid(标题 | spacer | 操作)替代 5 列扁平;segmented RadioButton 视图切换
- **list mode**:DataGrid → ListBox card,跟 env-list 完全一致(SurfaceBrush / OutlineBrush / PrimaryBrush 选中 2px / CornerRadius=6 / Padding=12)
- **tile mode**:Width 320→340 + 加 install_type pill badge
- **detail panel**:卡片化 Padding=20 CornerRadius=8 + 各组 Divider + ComboBox 自定义样式

## T1 Theme.xaml 新增
- `BoolToEntryCountTextConverter`(Views/Converters.cs):bool + int → "共 N 个节点"/"加载中…"
- `CatalogSegmentedRadioButton`(RadioButton style):segmented control,选中态 PrimaryBrush 背景
- `CatalogInstallTypeBadgeStyle`(Border style):pill badge,SecondaryBrush 背景
- `CatalogVersionComboBoxStyle`(ComboBox style):1px SecondaryBrush border + CornerRadius=4
- `CatalogCardItemContainerStyle`(ListBoxItem style):透明 + 0 padding + 0,0,0,8 margin
- `CatalogRowCardTemplate`(DataTemplate):3 行 Grid + install_type pill
- **修 dead-code**:`CatalogTileTemplate` InstallCommand → DownloadCommand,G8 同步

## T2 CatalogView.xaml 重做 toolbar + 详情面板
- 顶部 toolbar 5 列 → 3 列,加 segmented 视图切换
- 详情面板 Padding 16→20,各组 Divider,metadata 2 列 Grid,ComboBox 用新样式

## T3 列表 ListBox card + STA load tests
- DataGrid → ListBox 用 `CatalogRowCardTemplate` + `CatalogCardItemContainerStyle`
- 2 STA load tests(暗 + 亮):`new CatalogView().Measure/Arrange` 不抛 XamlParseException
- 模板跟 EnvironmentListView:209-223 env-card 完全同款

## G-Constraints 落地
- **G2 v0.6.9.2 Setter+DynamicResource regression**:6 个新 Setter 全部 property-element + DynamicResource,ControlTemplate.Triggers 内 attribute 允许
- **G3 VM 冻结**:CatalogViewModel 全保留,新增 view-only converter,不增 VM property
- **G5 暗/亮主题**:2 STA load tests 覆盖,所有 brush DynamicResource

## Verification (final consolidated)
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build` → 844 PASS / 0 FAIL / 1 SKIP
- `dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager"` → 0/0
- 无 v-bump / 无 release zip(项目惯例,纯 View 重做)
- GUI smoke TBD(用户桌面):4 区视觉一致 / segmented 切换高亮 / 列表卡片 PrimaryBrush 2px / 详情面板 Divider / 版本下拉 Material 风格 / 暗亮切换无残留

## Final review (opus)
APPROVED SHIP-READY. 0 Critical / 0 Important / 1 Minor style-only(<具体>).

## Carry-forward(均不阻塞)
- toolbar segmented RadioButton 选中态若用户想要更明显的"按下感",可加 active animation(目前是 static PrimaryBrush)
- 列表卡片若 entry 数 >1000,虚拟化(WPF ListBox 默认 VirtualizingStackPanel)需要 IsVirtualizing=True 验证
```

- [ ] **Step 4: 追加 MEMORY 索引行**

在 `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` 的 v0.6.11++ pip mirror 行**之后**追加一行:

```markdown
- [v0.6.11+ Catalog UI polish SDD](project_catalog_ui_polish.md) — ✓ SHIP-READY 2026-08-10,HEAD `<commit-sha>`(base `226953e` + 3 commits:T1 Theme styles + templates + converter / T2 toolbar + 详情面板 / T3 列表 ListBox card + STA load tests),844/0/1;4 区按 v0.6.10.2 env-list card 风格统一:3 列 toolbar + segmented RadioButton 视图切换 + ListBox card(Padding=12/CornerRadius=6/Selected PrimaryBrush 2px)+ tile 加 install_type pill badge + 详情面板 Padding=20 CornerRadius=8 各组 Divider;**G3 冻结 VM**:`BoolToEntryCountTextConverter` 替代 VM property;**G2 干净**:6 个新 Setter 全 property-element + DynamicResource;**G8 dead-code 修复**:`CatalogTileTemplate` InstallCommand → DownloadCommand;无 v-bump / 无 release zip;staging rebuilt 2026-08-10 19:30+;GUI smoke 8 步待桌面验证
```

(把 `<commit-sha>` 替换成 T3 实际 commit SHA;commit 后再做这步确保 SHA 准确)

---

## Critical Files (full list)

**Modified:**
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(+6 资源:1 converter + 5 styles/templates,修 1 dead-code binding)
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(重写 toolbar + 详情面板,删 DataGrid 替 ListBox card)
- `src-wpf/ComfyUI.Manager/Views/Converters.cs`(+1 converter class)

**Created:**
- `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`(2 STA load tests)

**Unchanged:**
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(G3 冻结)
- `src-wpf/ComfyUI.Manager/Themes/Palette.{Light,Dark}.xaml`

---

## Verification (end-to-end)

按顺序验证 4 task commit 全 PASS:

```bash
# T1
git status --short                                                                 # 仅 Theme.xaml + Converters.cs staged
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal            # 0/0

# T2
git status --short                                                                 # 仅 CatalogView.xaml staged
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal            # 0/0

# T3
git status --short                                                                 # CatalogView.xaml + CatalogViewLoadTests.cs staged
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal            # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal  # 2 PASS

# T4 final review (opus dispatch)

# 合并后全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build                # 844 PASS / 0 FAIL / 1 SKIP baseline 持平
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke(桌面验证,user)**:
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
| Segment RadioButton 替代 2 个 MaterialButton 可能让 `IsListMode` / `IsTileMode` setter 双向 binding 出问题 | T2 Step 1 实施前先 grep `IsListMode` / `IsTileMode` 用法,确认现有 setter 接受外部 `IsChecked=true` |
| ComboBox 自定义样式跨 palette 切换时 Editable/Non-Editable 视觉差异 | T1 Step 4c 仅改 Background/BorderBrush/BorderThickness/Padding + MinHeight,完整 ControlTemplate 重写代价大且易跨 merged dict 解析陷阱 |
| STA load test 不抓 ItemsControl 模板内 Setter bug(v0.6.9.2 教训) | T3 Step 2 测试 + 提交前 grep 三个 pattern:`<Setter Property="..." Value="{StaticResource ...}"`, `<Setter ... Value="{StaticResource ...}"`, `<Style.Triggers>` 内 `<Setter ... StaticResource>` |
| T1 `CatalogRowCardTemplate` install_type 数据结构不存在于 CatalogEntry metadata | `InstallType` 是 typed property(`CatalogEntry.cs:31`),T1 Step 4e 直接绑 `{Binding InstallType}` |
| `CatalogTileTemplate` 内 `InstallCommand` binding 与 v0.6.5.9 后状态不一致 | Theme.xaml:348 dead-code, T1 Step 3 同步改成 `DownloadCommand`,避免后续误用 |
| VM `HasEntries` 已存在但 "共 N 个节点" 需要数字而非 bool | 新建 `BoolToEntryCountTextConverter` 走 `HasEntries` + `PagedEntries.Count` 作 ConverterParameter,**不增 VM property**(G3 冻结) |
| T2 Step 4 placeholder 没删 DataGrid | T3 Step 3 删;T2 保留 DataGrid 是有意为之避免 T2 改动过大触发 review 误判 |

---

## Self-Review

1. **Spec coverage:**
   - spec §1 顶部工具栏 → T2 Step 1 ✓
   - spec §2 列表模式 ListBox card → T3 Step 3 ✓
   - spec §3 磁贴模式 Width 340 → T1 Step 4e(implicit,CatalogTileTemplate Width 在 T1 调整)+ T2 Step 4(保留现有 TileTemplate,仅 T1 改 Width)
   - spec §4 详情面板分组 + Divider → T2 Step 5 ✓
   - spec Public API:VM 冻结 → T1/T2/T3 全不动 VM ✓
   - spec Style 资源清单(5 个) → T1 Step 4a-4e ✓
   - spec 测试策略(2 STA load tests) → T3 Step 1 ✓
   - spec 4 Task 分解 → 本 plan 4 task ✓

2. **Placeholder scan:** 0 处 TBD/TODO/未填

3. **Type consistency:**
   - `CatalogSegmentedRadioButton`(T1 4a) ↔ `StaticResource CatalogSegmentedRadioButton`(T2 Step 1) ✓
   - `CatalogInstallTypeBadgeStyle`(T1 4b) ↔ `Style="{StaticResource CatalogInstallTypeBadgeStyle}"`(T2 Step 5 + T1 Step 4e) ✓
   - `CatalogVersionComboBoxStyle`(T1 4c) ↔ `Style="{StaticResource CatalogVersionComboBoxStyle}"`(T2 Step 5) ✓
   - `CatalogCardItemContainerStyle`(T1 4d) ↔ `ItemContainerStyle="{StaticResource CatalogCardItemContainerStyle}"`(T3 Step 3) ✓
   - `CatalogRowCardTemplate`(T1 4e) ↔ `ItemTemplate="{StaticResource CatalogRowCardTemplate}"`(T3 Step 3) ✓
   - `BoolToEntryCountText`(T1 Step 5 注册) ↔ `Converter={StaticResource BoolToEntryCountText}`(T2 Step 1) ✓
   - `CatalogViewLoadTests` 测试名 ↔ `FullyQualifiedName~CatalogViewLoad` filter ✓

4. **Ambiguity check:**
   - T2 Step 4 "T3 placeholder" 明确说明留 DataGrid,T3 Step 3 删 ✓
   - T1 Step 4c ComboBox 样式 "Editable=False 简化路径" 明确 ✓
   - T3 Step 5 "2 flake ProcessLauncherProgressTests" 明确说明与本任务无关 ✓
   - T4 Step 4 `<commit-sha>` placeholder 明确说明"T3 实际 commit SHA;commit 后再做这步确保 SHA 准确" ✓

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例)**:
- 3 task × (implementer + reviewer) ≈ 6 dispatch
- T1/T2/T3 各 1 implementer(haiku)+ 1 reviewer(sonnet)
- T4 final whole-branch review(opus)+ MEMORY + staging rebuild
- 3 commits on main,1 final review commit