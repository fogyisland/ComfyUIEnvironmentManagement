# 节点管理 + 升级节点 Bottom-Popup 设计

> 替代 v0.6.15.7 T7 的 side-panel master-detail layout。
> 在 env-list 底部弹两个独立的 inline panel(节点管理 + 升级节点),per-env VM 缓存保留状态。

## 目标

把 v0.6.15.7 ship 的 env-detail 节点信息展示从 env-list **右侧面板** 重构为 env-list **底部弹出 panel**(沿用现有 4 个状态面板的 inline 模式),并新增 **升级节点** 入口(per-env 行内按钮,弹只含 outdated 节点的独立面板)。两个 panel 都 per-env 缓存 VM,切换 env 不重建。

## 架构

- **Per-env 行内按钮**(2 行 × 6 列 Grid):
  - Row 0: 启动 / 停止 / 装依赖 / BED / ComfyUI Manager / [空 cell]
  - Row 1: 查看日志 / 打开浏览器 / **节点管理**(原"安装节点") / **升级节点**(新) / 组件报告 / 删除
- **底部弹出 panel — 节点管理**:
  - 沿用现有 inline status-panel 模式(SurfaceBrush/OutlineBrush/Border + Visibility binding + ✕ 关按钮)
  - Height ~400 给 DataGrid 用(比现有 4 个 status panel 更高)
  - 顶栏:`{env.Name} 的节点管理` + ✕ 关 + (右) `扫描` + (右) `安装节点`
  - Body:9 列 DataGrid(包名/版本/作者/状态/锁/仓库URL/加载时间/版本tag/加载错误/来源/操作)
  - 操作列:切换 / 删除
  - 打开时自动 rescan
- **底部弹出 panel — 升级节点**:
  - 同款 pattern,Height ~300
  - 顶栏:`{env.Name} 需要升级的节点` + ✕ 关
  - Body:DataGrid 仅 outdated 节点(`ScanMeta["installed_tag"]` ≠ catalog `LatestVersion`,且两者都非空)
  - 操作列:每行 `升级` 按钮
- **Per-env VM 缓存** `Dictionary<envId, NodeManagementViewModel>` + `Dictionary<envId, UpgradeNodesViewModel>`:
  - 切换 env → hide 旧 panel / show 新 panel,但 VM 不重建
  - 保留状态:selected row / scroll position / DataGrid 内部状态;`NodeManagement.InstallCommand` 开的 CatalogEntryPicker 子弹窗关闭后刷新 items

## Tech Stack

- .NET 8 + WPF + C# 12
- 现有 inline status-panel pattern(`Views/EnvironmentListView.xaml` Row 30-180 四个 `<Border>`)
- 现有 `CatalogEntryPickerDialog`(节点管理的 "安装节点" 按钮直接调它,传 envId)
- `AppLogger` 全程 INFO log(v0.6.5.13 模式)
- `IProgress<T>` 跨线程推 UI(v0.6.5.11 模式)
- `ConfirmDialogOverride` / `MessageBoxOverride` test seam(v0.6.5.19 模式)

## 全局约束

- 测试套件基线 1206 PASS / 3 FAIL / 1 SKIP(3 FAIL 都是 pre-existing,本 SDD 不引入新 FAIL)
- 不动 v0.6.15.7 已 ship 的 `EnvironmentDetailViewModel`(T3/T4 那套是上一轮 dead-end,本轮由 `NodeManagementViewModel` 取代)
- 不动 `CatalogEntryPickerDialog`(复用,本轮只调它)
- 不动 `EnvComponentReportBuilder`(组件报告按钮保留独立路径,本轮不重构共享扫描逻辑)
- 不动 `EnvironmentDetailView.xaml`(本轮无引用,可后续单独删)
- 所有新 `bool`/enum binding 走 `BoolToVisibility`/`NullToVisibility` converter(Theme.xaml 已注册)
- 所有相对时间显示走 `RelativeTimeConverter`(v0.6.15.7 T8)
- Per-env 操作 button 用 `RelayCommand` + `CommandParameter` 传 env,CanExecute 检查 `!IsEnvBusy(env)`
- 中文 UI 文案保持一致("节点管理" / "升级节点" / "扫描" / "安装节点" / "已安装" / "已过时" / "未知" 等)

## 文件改动

| 文件 | 改动 |
|------|--------|
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` | revert master-detail(删 Grid `*`/Auto/`600` 三列)+ 改 2 行 6 列按钮 grid + 加 5th/6th inline status panel(节点管理 + 升级节点)+ 加 `OnNodeManagementCloseClicked` + `OnUpgradeNodesCloseClicked` code-behind |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml.cs` | 加两个 close click handler |
| `src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml`(新) | DataGrid + 顶栏按钮封装(类似现有 `EnvironmentDetailView.xaml` pattern,但带顶右"扫描"/"安装节点"按钮) |
| `src-wpf/ComfyUI.Manager/Views/NodeManagementView.xaml.cs`(新) | `DataContext = vm` 默认 ctor |
| `src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml`(新) | 同上,但无顶右按钮,顶栏只有 ✕ |
| `src-wpf/ComfyUI.Manager/Views/UpgradeNodesView.xaml.cs`(新) | `DataContext = vm` 默认 ctor |
| `src-wpf/ComfyUI.Manager/ViewModels/NodeManagementViewModel.cs`(新) | `ObservableCollection<ScannedNode> Nodes` + `RelayCommand ScanCommand` + `RelayCommand InstallCommand`(开 CatalogEntryPicker)+ `RelayCommand CloseCommand` + `RelayCommand DeleteCommand`(复用 T4 ConfirmDialogOverride)+ `Busy` + 构造时自动 rescan |
| `src-wpf/ComfyUI.Manager/ViewModels/UpgradeNodesViewModel.cs`(新) | `ObservableCollection<ScannedNode> OutdatedNodes` + `RelayCommand UpgradeCommand`(per-row,参数 ScannedNode → NodeOperations.UpgradeAsync)+ `RelayCommand CloseCommand` + `Busy` + 构造时拉 catalog + 过滤 outdated + 自动 rescan |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | 删 `SelectedChangedHandler`/`EnvironmentDetail`/`HasEnvironmentDetail`/`_environmentDetail`/`_environmentDetailEnvId`;加 `Dictionary<string, NodeManagementViewModel> _nodeMgmtCache` + `Dictionary<string, UpgradeNodesViewModel> _upgradeCache` + `NodeManagement`/`IsNodeManagementVisible` property + `UpgradeNodes`/`IsUpgradeNodesVisible` property + `OpenNodeManagementCommand` / `OpenUpgradeNodesCommand` / `CloseNodeManagementCommand` / `CloseUpgradeNodesCommand`;ctor 注入 `CatalogRepository` + `NodeVersionRepository` 给 UpgradeNodesVM 用 |
| `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` | 加 `RescanAsync(string envId, CancellationToken ct = default)` — 扫描 env CustomNodesPath,upsert ScannedNode,返 list |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | DI 注入新增依赖(若需要)|
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/NodeManagementViewModelTests.cs`(新) | constructor auto-rescan / ScanCommand / InstallCommand opens picker / DeleteCommand with ConfirmDialogOverride / 重复 open 同 envId 不重建 / switch env 缓存命中 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/UpgradeNodesViewModelTests.cs`(新) | 过滤 outdated only / UpgradeCommand 成功更新 installed_tag / UpgradeCommand 失败保留旧状态 / 重复 open 同 envId 不重建 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelNodeManagementTests.cs`(新) | 节点管理 open / 关闭 / switch env 缓存命中 / 升级节点 open / 关闭 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsRescanAsyncTests.cs`(新) | 扫描 happy path(创建 custom_nodes 目录 + 子目录)→ upsert + return list / 不存在目录返空 list |

## 组件设计

### 1. `NodeOperations.RescanAsync`

```csharp
public virtual async Task<IReadOnlyList<ScannedNode>> RescanAsync(
    string envId, CancellationToken ct = default)
{
    _logger?.Info("node-rescan", $"env='{envId}' 开始扫描 custom_nodes");
    var env = _envRepo.Get(envId);
    if (env is null) return Array.Empty<ScannedNode>();

    var customNodesPath = env.CustomNodesPath;
    if (string.IsNullOrEmpty(customNodesPath) || !Directory.Exists(customNodesPath))
    {
        _logger?.Warn("node-rescan", $"env='{envId}' CustomNodesPath 不存在或为空");
        return Array.Empty<ScannedNode>();
    }

    var scanned = new List<ScannedNode>();
    foreach (var dir in Directory.EnumerateDirectories(customNodesPath))
    {
        ct.ThrowIfCancellationRequested();
        var nodeId = Path.GetFileName(dir);
        // 读 package 名(优先 __init__.py 顶部 'Name: x' / fallback to dir name)
        var package = TryReadPackageName(dir) ?? nodeId;
        var sha = await TryReadHeadShaAsync(dir, ct);
        var tag = await TryReadInstalledTagAsync(dir, ct);
        var node = new ScannedNode
        {
            Id = nodeId,
            EnvId = envId,
            Package = package,
            PackagePath = dir,
            Version = sha ?? "",
            Source = "env",
            ScanMeta = new Dictionary<string, string>
            {
                ["installed_tag"] = tag ?? "",
            },
        };
        _nodeRepo.Upsert(node);
        scanned.Add(node);
    }
    _logger?.Info("node-rescan", $"env='{envId}' 扫描完成,共 {scanned.Count} 个节点");
    return scanned;
}
```

辅助 `TryReadPackageName(dir)` / `TryReadHeadShaAsync(dir, ct)` / `TryReadInstalledTagAsync(dir, ct)` — 私有方法,git part 复用 `_git.RunAsync(dir, new[] {"describe", "--tags", "--abbrev=0"}, TimeSpan.FromSeconds(10), ct)`。

`installed_tag` 写空字符串而非 null —— DB column 是 TEXT NULL OK 但写空字符串方便后续 `Outdated` 判断 `tag != latest && tag != ""` 短路。

### 2. `NodeManagementViewModel`

```csharp
public class NodeManagementViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly ErrorBannerViewModel _errorBanner;
    private readonly string _envId;
    private readonly Func<...>? _openPickerOverride; // test seam

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand ScanCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CloseCommand { get; }

    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public bool Busy { get; set; }

    public NodeManagementViewModel(
        NodeRepository repo, NodeOperations nodeOps,
        ErrorBannerViewModel errorBanner, string envId)
    {
        _nodeRepo = repo;
        _nodeOps = nodeOps;
        _errorBanner = errorBanner;
        _envId = envId;
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !Busy);
        InstallCommand = new RelayCommand(_ => OpenInstallPicker(), _ => !Busy);
        DeleteCommand = new RelayCommand(
            async p => await DeleteAsync(p as ScannedNode),
            p => p is ScannedNode);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = ScanAsync(); // auto-rescan on open
    }

    public event Action? CloseRequested;

    private async Task ScanAsync() { /* 重置 Busy + 调 _nodeOps.RescanAsync + 刷新 Nodes */ }
    private void OpenInstallPicker() { /* 弹 CatalogEntryPickerDialog.Show,关后 ScanAsync */ }
    public async Task DeleteAsync(ScannedNode? node) { /* 同 T4 EnvironmentDetailViewModel.DeleteAsync */ }
}
```

### 3. `UpgradeNodesViewModel`

```csharp
public class UpgradeNodesViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> OutdatedNodes { get; } = new();
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand CloseCommand { get; }
    public bool Busy { get; set; }

    public Func<string, string, string, bool>? ConfirmDialogOverride { get; set; }

    public UpgradeNodesViewModel(
        NodeRepository nodeRepo, NodeOperations nodeOps,
        CatalogRepository catalogRepo, NodeVersionRepository versionRepo,
        string envId)
    {
        _nodeRepo = nodeRepo; _nodeOps = nodeOps;
        _catalogRepo = catalogRepo; _versionRepo = versionRepo;
        _envId = envId;
        UpgradeCommand = new RelayCommand(
            async p => await UpgradeAsync(p as ScannedNode),
            p => p is ScannedNode && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = LoadAsync(); // auto rescan + filter outdated
    }

    public event Action? CloseRequested;

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            await _nodeOps.RescanAsync(_envId); // 重新扫描拿最新 installed_tag
            var scanned = _nodeRepo.ListByEnv(_envId).ToList();
            var catalog = _catalogRepo.Search("", 5000).ToList();
            var outdated = scanned.Where(s =>
            {
                if (!s.ScanMeta.TryGetValue("installed_tag", out var tag)
                    || string.IsNullOrEmpty(tag)) return false;
                var entry = catalog.FirstOrDefault(e => e.Package == s.Package);
                if (entry is null) return false;
                var latest = entry.LatestVersion;
                return !string.IsNullOrEmpty(latest) && tag != latest;
            }).ToList();
            OutdatedNodes.Clear();
            foreach (var n in outdated) OutdatedNodes.Add(n);
        }
        finally { Busy = false; }
    }

    private async Task UpgradeAsync(ScannedNode? node) { /* 调 _nodeOps.UpgradeAsync + 完成后 LoadAsync */ }
}
```

### 4. `EnvironmentListViewModel` 缓存 + 命令

```csharp
private readonly Dictionary<string, NodeManagementViewModel> _nodeMgmtCache = new();
private readonly Dictionary<string, UpgradeNodesViewModel> _upgradeCache = new();

private NodeManagementViewModel? _nodeManagement;
public NodeManagementViewModel? NodeManagement
{
    get => _nodeManagement;
    private set { if (SetField(ref _nodeManagement, value)) RaisePropertyChanged(nameof(IsNodeManagementVisible)); }
}
public bool IsNodeManagementVisible => _nodeManagement is not null;

private UpgradeNodesViewModel? _upgradeNodes;
// (同上 pattern)

public RelayCommand OpenNodeManagementCommand { get; }
public RelayCommand OpenUpgradeNodesCommand { get; }
public RelayCommand CloseNodeManagementCommand { get; }
public RelayCommand CloseUpgradeNodesCommand { get; }

// ctor 末尾(在所有 _xxxRepo 字段赋值之后):
OpenNodeManagementCommand = new RelayCommand(
    p => OpenNodeManagement(p as Environment ?? Selected),
    p => (p as Environment ?? Selected) is not null && !IsEnvBusy(p as Environment ?? Selected));
OpenUpgradeNodesCommand = new RelayCommand(
    p => OpenUpgradeNodes(p as Environment ?? Selected),
    p => (p as Environment ?? Selected) is not null && !IsEnvBusy(p as Environment ?? Selected));
CloseNodeManagementCommand = new RelayCommand(_ => NodeManagement = null);
CloseUpgradeNodesCommand = new RelayCommand(_ => UpgradeNodes = null);

private void OpenNodeManagement(Environment? env)
{
    if (env is null || _nodeRepo is null) return;
    if (!_nodeMgmtCache.TryGetValue(env.Id, out var vm))
    {
        vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, env.Id);
        vm.CloseRequested += () => NodeManagement = null;
        _nodeMgmtCache[env.Id] = vm;
    }
    NodeManagement = vm;
}

private void OpenUpgradeNodes(Environment? env)
{
    if (env is null || _nodeRepo is null || _catalogRepo is null || _versionRepo is null) return;
    if (!_upgradeCache.TryGetValue(env.Id, out var vm))
    {
        vm = new UpgradeNodesViewModel(_nodeRepo, _nodeOps, _catalogRepo, _versionRepo, env.Id);
        vm.CloseRequested += () => UpgradeNodes = null;
        _upgradeCache[env.Id] = vm;
    }
    UpgradeNodes = vm;
}
```

切 env 切换 panel 行为:panel **不** 跟 Selected 走。用户点 env-list 行只切换 Selected 状态(用于其他行内按钮的 CanExecute / tooltip),不切换 panel 内容。Panel 只在用户点 `节点管理` / `升级节点` 按钮时切换 env。

`Selected setter` 不再触发任何 panel 切换(删除 v0.6.15.7 T7 的 `SelectedChangedHandler` 整个方法)。仅保留 `StartTooltip` 的 RaisePropertyChanged(已有逻辑)。

panel 切换路径只有 `OpenNodeManagementCommand` / `OpenUpgradeNodesCommand`:

```csharp
private void OpenNodeManagement(Environment? env)
{
    if (env is null || _nodeRepo is null) return;
    if (!_nodeMgmtCache.TryGetValue(env.Id, out var vm))
    {
        vm = new NodeManagementViewModel(_nodeRepo, _nodeOps, _errorBanner, env.Id);
        vm.CloseRequested += () => NodeManagement = null;
        _nodeMgmtCache[env.Id] = vm;
    }
    NodeManagement = vm;
}
```

用户点同一 env 的 `节点管理` 按钮:`_nodeMgmtCache[env.Id]` 命中,直接复用(状态保留)。
用户点另一 env 的 `节点管理` 按钮:cache miss → new VM(或命中复用),`NodeManagement = vm` 触发 panel 切换 + DataGrid 重绑新 ItemsSource。
用户点 ✕ 关 panel:`CloseRequested` event → `NodeManagement = null`(隐藏),cache 保留,下次同 env 再点 `节点管理` 仍复用。

### 5. `EnvironmentListView.xaml` 行内按钮 grid

Row 0 + Row 1 改 6 列:

```xml
<Grid.RowDefinitions>
    <RowDefinition Height="Auto" />
    <RowDefinition Height="Auto" />
</Grid.RowDefinitions>
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="*" />
</Grid.ColumnDefinitions>
```

Row 0 第 6 列:留空(spacer 或 后续填)。

Row 1 第 4 列:`升级节点` 按钮(新增),第 3 列:`节点管理`(改名自"安装节点")。

### 6. `EnvironmentListView.xaml` 两个新 inline status panel

复用现有 inline status panel pattern(Row 30-180 已有 4 个 `<Border>`),在第 4 个(ComfyUI Manager)之后加:

```xml
<!-- 节点管理 -->
<Border Margin="0,6,0,0" Padding="12"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
        CornerRadius="6"
        Visibility="{Binding IsNodeManagementVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
    <StackPanel DataContext="{Binding NodeManagement}">
        <DockPanel>
            <TextBlock DockPanel.Dock="Left" VerticalAlignment="Center">
                <Run Text="{Binding EnvName, Mode=OneWay, StringFormat='{0} 的节点管理'}" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
            </TextBlock>
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                <Button Content="扫描" Command="{Binding ScanCommand}" Style="{StaticResource MaterialButton}" />
                <Button Content="安装节点" Command="{Binding InstallCommand}" Style="{StaticResource MaterialButton}" Margin="6,0,0,0" />
                <Button Content="✕" Margin="6,0,0,0"
                        Click="OnNodeManagementCloseClicked"
                        Style="{StaticResource GearIconButtonStyle}"
                        Foreground="{DynamicResource OnSurfaceBrush}" />
            </StackPanel>
        </DockPanel>
        <ContentControl Content="{Binding}" Margin="0,8,0,0" MinHeight="300">
            <ContentControl.Resources>
                <DataTemplate DataType="{x:Type vm:NodeManagementViewModel}">
                    <v:NodeManagementView />
                </DataTemplate>
            </ContentControl.Resources>
        </ContentControl>
    </StackPanel>
</Border>

<!-- 升级节点(同款 pattern,无顶右按钮) -->
<Border Margin="0,6,0,0" Padding="12"
        Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource OutlineBrush}" BorderThickness="1"
        CornerRadius="6"
        Visibility="{Binding IsUpgradeNodesVisible, Converter={StaticResource BoolToVisibility}, FallbackValue=Collapsed}">
    <StackPanel DataContext="{Binding UpgradeNodes}">
        <DockPanel>
            <TextBlock DockPanel.Dock="Left" VerticalAlignment="Center">
                <Run Text="{Binding EnvName, Mode=OneWay, StringFormat='{0} 需要升级的节点'}" FontWeight="Bold" FontSize="14" Foreground="{DynamicResource OnSurfaceBrush}" />
            </TextBlock>
            <Button DockPanel.Dock="Right" Content="✕"
                    Click="OnUpgradeNodesCloseClicked"
                    Style="{StaticResource GearIconButtonStyle}"
                    Foreground="{DynamicResource OnSurfaceBrush}" />
        </DockPanel>
        <ContentControl Content="{Binding}" Margin="0,8,0,0" MinHeight="200">
            <ContentControl.Resources>
                <DataTemplate DataType="{x:Type vm:UpgradeNodesViewModel}">
                    <v:UpgradeNodesView />
                </DataTemplate>
            </ContentControl.Resources>
        </ContentControl>
    </StackPanel>
</Border>
```

(`EnvName` 通过构造时传 `_envName` 字段写入,或 `NodeManagementViewModel` 暴露 `string EnvName` get-only property)

### 7. XAML DataGrid pattern(`NodeManagementView.xaml`)

完全复用 `EnvironmentDetailView.xaml` 的 9 列 DataGrid,只删 `DockPanel.Dock="Top"` 的 `RescanCommand` + `Busy` TextBlock(改放到 inline panel 顶栏)。

```xml
<UserControl ...>
    <DataGrid ItemsSource="{Binding Nodes}"
              SelectedItem="{Binding Selected}"
              AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
            <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
            <DataGridTextColumn Header="版本" Binding="{Binding Version}" Width="100" />
            <DataGridTextColumn Header="作者" Binding="{Binding Author}" Width="*" />
            <DataGridTextColumn Header="状态" Binding="{Binding Status}" Width="80" />
            <DataGridCheckBoxColumn Header="锁" Binding="{Binding Locked}" Width="40" />
            <DataGridTextColumn Header="仓库 URL" Binding="{Binding RepositoryUrl}" Width="200" />
            <DataGridTextColumn Header="加载时间" Width="100"
                                Binding="{Binding LastScannedAt, Converter={StaticResource RelativeTime}}" />
            <DataGridTextColumn Header="版本 tag" Binding="{Binding ScanMeta[installed_tag]}" Width="100" />
            <DataGridTemplateColumn Header="加载错误" Width="100">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <Border Background="#D32F2F" CornerRadius="4" Padding="4,2"
                                HorizontalAlignment="Left"
                                ToolTip="{Binding ScanMeta[load_error]}"
                                Visibility="{Binding ScanMeta[load_error],
                                             Converter={StaticResource NullToVisibility},
                                             FallbackValue=Collapsed}">
                            <TextBlock Text="加载失败" Foreground="White" FontSize="11" />
                        </Border>
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
            <DataGridTextColumn Header="来源" Binding="{Binding Source}" Width="70" />
            <DataGridTemplateColumn Header="操作" Width="160">
                <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                        <StackPanel Orientation="Horizontal">
                            <Button Content="切换" Command="{Binding DataContext.ToggleCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" />
                            <Button Content="删除" Style="{StaticResource DangerButton}" Margin="4,0,0,0"
                                    Command="{Binding DataContext.DeleteCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}" />
                        </StackPanel>
                    </DataTemplate>
                </DataGridTemplateColumn.CellTemplate>
            </DataGridTemplateColumn>
        </DataGrid.Columns>
    </DataGrid>
</UserControl>
```

(`EnvironmentDetailView.xaml` 的 ToggleCommand 在 `EnvironmentDetailViewModel`;本轮 `NodeManagementViewModel` 也提供 `ToggleCommand` 同款 MessageBox TODO 占位 — 不阻塞 ship)

`UpgradeNodesView.xaml` 简化版,只 4 列:

```xml
<DataGrid ItemsSource="{Binding OutdatedNodes}" AutoGenerateColumns="False" IsReadOnly="True">
    <DataGrid.Columns>
        <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
        <DataGridTextColumn Header="已装 tag" Binding="{Binding ScanMeta[installed_tag]}" Width="120" />
        <DataGridTextColumn Header="最新版本" Binding="{Binding LatestVersion}" Width="120" />
        <DataGridTemplateColumn Header="操作" Width="100">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <Button Content="升级" Command="{Binding DataContext.UpgradeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" />
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
    </DataGrid.Columns>
</DataGrid>
```

(`LatestVersion` 是 `UpgradeNodesViewModel` 提供的额外 property `string? LatestVersion`,从 catalog entry 拉;或简化 — 直接 catalog 查,在 VM 上挂 `Dictionary<string, string> LatestVersionsByPackage`,DataGrid column 走 `Converter` 取值)

## 数据流

### 节点管理打开流程
```
User 点 env-list row 的 "节点管理" 按钮
  → EnvListVM.OpenNodeManagement(env)
    → 查 _nodeMgmtCache[env.Id]
      → cache miss → new NodeManagementViewModel(nodeRepo, nodeOps, errorBanner, env.Id)
        → ctor 末尾 fire-and-forget _ = ScanAsync()
      → cache hit → 直接复用
    → NodeManagement = vm  (setter 触发 IsNodeManagementVisible = true)
  → WPF binding 看到 IsNodeManagementVisible=true → inline Border 显示
    → ContentControl 找到 DataTemplate (NodeManagementViewModel → NodeManagementView)
      → DataGrid ItemsSource={Binding Nodes} 绑 vm.Nodes
        → ScanAsync 完成后 _nodeRepo.ListByEnv(env.Id) 填充 Nodes
```

### 升级节点流程
```
User 点 "升级节点" 按钮
  → EnvListVM.OpenUpgradeNodes(env)
    → 查 _upgradeCache[env.Id],miss → new UpgradeNodesViewModel(...)
      → ctor 末尾 fire-and-forget _ = LoadAsync()
        → RescanAsync → ListByEnv → catalog.Search → 过滤 outdated → 填 OutdatedNodes
    → UpgradeNodes = vm
  → inline Border 显示 → DataGrid 显示 outdated 节点
User 点 row "升级" 按钮
  → UpgradeCommand.Execute(node)
    → ConfirmDialogOverride("确认升级 {pkg}?", "升级", "取消")
    → NodeOperations.UpgradeAsync(envId, pkg)
      → 完成后 LoadAsync() 刷新 OutdatedNodes(刚刚升级的不再 outdated,会从 list 消失)
```

## 错误处理

- `RescanAsync` 自定义节点目录不存在 → 返空 list + WARN log,不抛
- `NodeOperations.UpgradeAsync` 失败 → ErrorBanner 显示错误,OutdatedNodes 保留原样(不刷掉)
- `NodeManagementViewModel.DeleteAsync` 失败 → ErrorBanner + Nodes 不动(同 T4)
- `OpenInstallPicker` picker 弹窗失败 → 静默 ignore,WPF ShowDialog 异常由 outer catch 接住
- Cache VM 长时间持有(切走再切回)— 不主动 dispose,跟随 EnvListVM lifetime;EnvListVM 在 env 删时无 cleanup hook — 接受 leak 风险(用户删 env 后 cache 死,几百字节级)

## 测试

### `NodeOperationsRescanAsyncTests`(新)
- `RescanAsync_HappyPath_CreatesRowsForEachSubdir` — 创建 mock env + custom_nodes 目录 + 3 个子目录,assert ScannedNode row 数 + 字段(包名/sha/tag)
- `RescanAsync_CustomNodesPathMissing_ReturnsEmpty`
- `RescanAsync_NoSubdirs_ReturnsEmpty`
- `RescanAsync_NonExistentEnv_ReturnsEmpty`
- `RescanAsync_UpsertsExistingNode` — 同 nodeId 第二次跑 → 数量不变 + 字段刷新

### `NodeManagementViewModelTests`(新)
- `Constructor_TriggersScanAsync_PopulatesNodes` — 用 FakeNodeOperations.RescanAsync 返 mock list
- `ScanCommand_AfterBusyFalse_TriggersRescan` — 重置 Busy + 跑第二次
- `InstallCommand_OpensPicker` — FakeCatalogEntryPickerDialog.Capture 看 invocations
- `DeleteCommand_ConfirmsAndDeletes_AndRemovesFromNodes` — ConfirmDialogOverride → 删除成功 → Nodes 移除
- `DeleteCommand_CancelledByUser_LeavesNodesIntact`
- `DeleteCommand_FailedResult_LeavesNodesIntact_AddsErrorBanner`
- `CloseCommand_FiresCloseRequested_Event`

### `UpgradeNodesViewModelTests`(新)
- `Constructor_LoadsOutdatedOnly` — seed scanned (tag=v1.0, latest=v1.2 → outdated) + scanned (tag=v1.2, latest=v1.2 → not outdated) + scanned (no tag → skip)
- `UpgradeCommand_Successful_TriggersReload_NodeRemovedFromList` — FakeNodeOperations.UpgradeAsync → 成功后 LoadAsync,Node 从 OutdatedNodes 消失
- `UpgradeCommand_Failed_KeepsNodeInList_AddsErrorBanner`
- `CloseCommand_FiresCloseRequested_Event`
- `Constructor_CatalogMissingEntry_NodeExcludedFromOutdated`

### `EnvironmentListViewModelNodeManagementTests`(新)
- `OpenNodeManagement_NewEnv_CreatesVM_ShowsPanel`
- `OpenNodeManagement_SameEnvTwice_ReusesCachedVM` — assert 第二次 == 第一次(ReferenceEquals)
- `OpenNodeManagement_DifferentEnv_SwitchesPanelVM`
- `OpenUpgradeNodes_NewEnv_CreatesVM_ShowsPanel`
- `CloseNodeManagementCommand_HidesPanel_PreservesCache` — 关后再开同 env → 复用 cache
- `OpenNodeManagement_BusyEnv_GatedByCanExecute`

### 兼容 / 回归
- `EnvironmentDetailViewModelTests`(已存在)— 不动;v0.6.15.7 T3 那些测试保留(VM 仍存在,只是 EnvListVM 不再创建它)
- 既有 `EnvironmentListViewModelTests` — 调整 `Selected` setter 测试,移除 `EnvironmentDetail` 相关断言(若有)

## 验证

1. `dotnet build` 0 错误
2. `dotnet test tests-wpf/ComfyUI.Manager.Tests` 全套 PASS(基线 1206 + 新增 ~22 测试 PASS,FAIL 数不变)
3. Staging rebuild:`dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"`
4. GUI smoke(桌面):
   - **节点管理**:
     - env-list 行点 `节点管理` → 底部弹面板,自动 rescan → 节点列表填充
     - 切换 env → 旧 panel 关,新 env 面板开
     - 切回原 env → 复用缓存(节点列表仍在,无重新扫描)
     - 点 `扫描` 按钮 → 列表刷新
     - 点 `安装节点` → CatalogEntryPickerDialog 弹 → 装新节点 → 关 picker → 节点管理列表自动刷新
     - 点行 `删除` → 确认 → 节点从列表消失
   - **升级节点**:
     - seed 一个 outdated 节点(ScanMeta["installed_tag"]=v1.0 + catalog LatestVersion=v1.2)
     - 点 `升级节点` → 底部弹面板 → 只显示该节点
     - 点行 `升级` → NodeOperations.UpgradeAsync → 完成后节点从列表消失(LoadAsync 重过滤)
   - **关闭路径**:
     - 点 ✕ → panel 隐藏,cache 保留
     - 再点行 `节点管理` → 同 VM 实例,无重新扫描

## Out of Scope

- v0.6.15.7 T7 side-panel master-detail layout — 本轮 revert(dead code)
- `EnvironmentDetailView.xaml` + `EnvironmentDetailViewModel.cs` 物理删除 — 跟功能 dead,本轮不动文件结构(后续单独清理)
- v0.6.15.7 T3 RescanCommand MessageBox TODO — `NodeManagementViewModel.ScanAsync` 实现,但 `EnvironmentDetailViewModel.Rescan` 保留 MessageBox(没人调它)
- v0.6.15.7 T3 `FormatRelative` static helper — dead code,本轮不动
- EnvComponentReportBuilder 与 NodeOperations.RescanAsync 的扫描逻辑去重 — 后续重构,本轮保留两份
- 升级节点的批量升级(顶部 `全部升级` 按钮)— 后续,本轮 per-row only
- 节点管理的 "切换" 命令(MessageBox TODO)— 后续,本轮保留占位
- 节点版本 tag 比较语义(`v1.2.0` vs `v1.2` / semver vs commit sha)— 字符串等值比较,后续可升级 semver compare
- 把 catalog picker 的 `installed_tag` 检测逻辑挪到 NodeOperations 层 — 后续重构,本轮 UpgradeNodesViewModel 直接读 catalog