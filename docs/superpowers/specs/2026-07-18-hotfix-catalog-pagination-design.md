# v0.6.4 Hotfix — Catalog 页面:Settings 手动刷新 + 分页 + 磁贴/列表切换 + Cache 拆分

**里程碑:** v0.6.4 hotfix(v0.6.3 之后)
**日期:** 2026-07-18
**状态:** 待用户审阅
**Base SHA:** v0.6.3 tag(HEAD `2ac2829`)

---

## 0. 摘要

WPF Catalog 页面 UX 改造 + 一项架构调整 + 一项流程调整:

1. **去掉"打开页面就看到 10 个默认包"**:改为用户**手动**去 Settings 页点"刷新节点目录"按钮,把数据从 active QuerySource 下载到本地 SQLite。Catalog 页只渲染本地 cache,无 auto-refresh。
2. **视图模式可切换**:`列表`(DataGrid 现状)vs `磁贴`(WrapPanel 详细卡片),顶部 toggle 切换,持久化到 settings.json
3. **Cache 数据库拆分**:`catalog_cache` 表移到绿色包目录 `<AppBaseDir>/data/catalog-cache.db`(随包发布走),其他用户表(env list / scanned_nodes / version_history 等)继续留在 `%APPDATA%\ComfyUI-Manager\state.db`
4. **流程入口集中到 Settings**:刷新节点目录的唯一入口在 Settings 页(query source section 下方加按钮),Catalog 页的"刷新"按钮**保留**(共享内部实现,作为快速重试)

### 关键决策

| 决策 | 选择 |
|---|---|
| 刷新入口(主) | Settings 页 →"查询节点的源" section 下方 →"刷新节点目录"按钮 → 调 `RefreshCatalogAsync` → 写 SQLite |
| 刷新入口(快速重试) | Catalog 页 → 保留"刷新"按钮 → 调同一个 `RefreshCatalogAsync` 内部实现 |
| 自动加载 | **去掉**(CatalogViewModel ctor 不再 fire-and-forget `InitialRefreshAsync`) |
| 分页大小 | `Settings.CatalogPageSize = 20`(可配置,默认 20) |
| 视图模式持久化 | `Settings.CatalogViewMode` 枚举(`List` / `Tile`),默认 `List` |
| 磁贴样式 | 详细卡片(WrapPanel 2-3 列,每张 320 宽,显示 包名/作者/⭐/说明/安装按钮) |
| 视图切换实现 | `DataTemplateSelector`(而非 ContentControl + Style trigger),两个 `DataTemplate` x:Key |
| Cache db 路径 | `<AppBaseDir>/data/catalog-cache.db`(目录自动 mkdir) |
| 用户 db 路径 | `%APPDATA%\ComfyUI-Manager\state.db`(原 catalog.db 路径改名,只放用户表) |
| 旧 catalog_cache 数据迁移 | **不做迁移**;首次启动后用户点 Settings 刷新按钮重新填充 |
| 空状态 | "暂无数据,去 Settings 刷新"(空)/ "刷新失败: {msg},重试"(失败)/ "刷新成功,共 N 个"(成功 toast) |
| `Ctor_LoadsAllCatalogEntries` test | 改名 + 重写语义(变"读本地 cache 到第一页,无 auto refresh") |
| `RefreshCommand` 共享 | 抽取 `CatalogRefreshService`(或共享静态方法)供 Settings + Catalog 共用 |

### 不动的东西

- `compat_api_base_url` 字段(兼容性检查,与本次无关)
- `SettingsRepository`(仍用 `%APPDATA%`)
- `EnvironmentRepository` / `NodeRepository` / `ProcessStateRepository` / `VersionRepository` / `DepRepository`(仍用 `%APPDATA%`)
- `CatalogFetcher`(接口/行为不变,只是被 `CatalogCacheStore` 间接使用)
- `CatalogEntry` 模型(不动)
- `NodeOperations`(不动)
- v0.6.3 节点源(query/download)下拉框(不动)

---

## 1. 目标 & 非目标

### 1.1 目标(本次完成时)

- **去掉 Catalog 页打开自动刷新**:ctor 不再 fire-and-forget `InitialRefreshAsync`;Catalog 页只读本地 cache,空则显示"暂无数据,去 Settings 刷新"
- **Settings 页加"刷新节点目录"按钮**:位于"查询节点的源" section 下方 → 点击 → 从 active QuerySource 拉 JSON → 解析 → 写入 `<AppBaseDir>/data/catalog-cache.db` 的 `catalog_cache` 表 → 完成后显示 toast 提示
- **Catalog 页保留"刷新"按钮**:作为快速重试入口,与 Settings 刷新共享同一个内部方法(共享 Service 抽取)
- **分页**:每页 20 个,底部 `Prev / 1/N / Next` 翻页,搜索 query 变化时重置回第 1 页
- **视图模式 toggle**:顶部两个按钮 `列表` / `磁贴`,active 高亮,点击切换 + 写 `Settings.CatalogViewMode`
- 列表模式:复用现有 DataGrid
- 磁贴模式:WrapPanel 2-3 列 + 详细卡片(包名 + 作者 + ⭐ + 说明 2-3 行 + 安装按钮)
- 空状态文案:三态 `"暂无数据,去 Settings 刷新"`(空)/ `"刷新失败: {message}"`(失败后显示在 ErrorMessage)/ `"刷新成功,共 N 个条目"`(成功后显示在 InfoMessage 5s 自动消失)
- `catalog_cache` 表从 `%APPDATA%/catalog.db` 拆分到 `<AppBaseDir>/data/catalog-cache.db`,新 db 文件首次创建时自动 init schema(`CREATE TABLE IF NOT EXISTS catalog_cache ...`)
- `Settings` 加 `CatalogViewMode` 枚举字段 + `CatalogPageSize` int 字段;`SettingsDefaults.Apply` 默认 `List` / 20
- 现有 74 个 WPF tests 不掉(改 2 个 + 加 8-9 个,总 82-83 个)
- 改完后 `dotnet test` 全绿,`dotnet build` 0 警告 0 错误

### 1.2 非目标(明确不做)

- ❌ Catalog 页 ctor 自动 refresh(去掉,只在 Settings 手动)
- ❌ 多 query source 并行合并拉取(沿用 v0.6.3:同时只 1 个 active)
- ❌ 旧 `%APPDATA%` 里 catalog_cache 数据迁移(用户接受"首次启动后手动点 Settings 刷新")
- ❌ 磁贴模式虚拟化(`VirtualizingWrapPanel` 集成);20 个/页 OK,不优化
- ❌ 分页跳页(只 Prev/Next,不输入页码)
- ❌ 磁贴模式自定义排序/筛选(沿用现有 query 过滤)
- ❌ `state.db` 改名(原 catalog.db 仍叫 state.db,只放用户表)
- ❌ 跨平台路径处理(只 Windows,沿用现有 `Path.Combine` + `AppContext.BaseDirectory`)
- ❌ `Environment.SpecialFolder.LocalApplicationData` vs `ApplicationData` 切换(沿用现有 `ApplicationData`)
- ❌ JSON schema 校验(沿用 v0.6.3:解析失败降级)
- ❌ 离线/在线状态自动检测(沿用现有"fetch 失败 → ErrorMessage + 本地 cache 仍渲染")
- ❌ 切换视图模式时丢失当前页(切完 ViewMode,CurrentPage 保持;但 `PagedEntries` 重新生成)

---

## 2. 用户故事

### US-1:首次启动(无任何 cache)

1. 用户解压 v0.6.4 绿色包,双击 `ComfyUI.Manager.exe`
2. WPF 启动 → 进入 Catalog 页(默认 List 视图)
3. 页面显示 "暂无数据,去 Settings 刷新"(空状态文案)
4. 用户切到 Settings 页 → 看到 "查询节点的源" section 下方新增的"刷新节点目录"按钮(默认 active source = "comfyui manager")
5. 用户点 "刷新节点目录" → 按钮变 disabled + 显示 "刷新中..." → 调 `RefreshCatalogAsync` → 拉 `https://raw.githubusercontent.com/.../custom-node-list.json`(~5-15s)
6. 拉到后:写入 `<AppBaseDir>/data/catalog-cache.db` → 按钮恢复 → toast "刷新成功,共 120 个条目"
7. 用户切回 Catalog 页 → 看到 DataGrid 满 20 行 + 底部 `1 / 6`(120 / 20 = 6 页)

### US-2:视图模式切换

1. 用户点顶部 `磁贴` 按钮
2. DataGrid 立即变成 WrapPanel 2-3 列 + 详细卡片
3. 按钮高亮切到 `磁贴`
4. 关 WPF → 重开 → 仍是 `磁贴` 模式(Settings 持久化)
5. 用户点 `列表` 按钮 → 切回 DataGrid,高亮切到 `列表`

### US-3:翻页

1. 用户在第 1 页(显示 20 行)
2. 点 `Next >` → 切到第 2 页(显示 21-40 行),底部 `2 / 3`
3. 点 `< Prev` → 切回第 1 页
4. 在第 1 页输入搜索 query "ComfyUI-Manager" → 自动重置到第 1 页(filter 后的结果从 0 开始)

### US-4:离线场景

1. 用户断网后重启 WPF
2. Catalog 页打开:`<AppBaseDir>/data/catalog-cache.db` 已有上次拉的 60 个 → 立即显示第 1 页(本地 cache)
3. 用户点 "刷新" 按钮重试 → 拉失败 → `ErrorMessage = "刷新失败: <reason>"`
4. 本地 cache 仍渲染,DataGrid 仍可用

### US-5:Catalog 页快速刷新(已有数据场景)

1. 用户已有 cache(60 个条目),想更新到最新版
2. 切到 Catalog 页 → 看到旧的 60 个
3. 点 Catalog 顶部 "刷新" 按钮 → 调 `RefreshCatalogAsync`(跟 Settings 同一个内部方法)→ 拉新版 → 完成后自动重读 + 跳回第 1 页
4. ErrorMessage / InfoMessage 区域显示 "刷新成功,共 N 个"

### US-6:用户升级 v0.6.3 → v0.6.4

1. 用户覆盖安装 v0.6.4
2. 旧 `%APPDATA%/ComfyUI-Manager/catalog.db` 还在(里面只有 catalog_cache + 其他用户表的混合)
3. v0.6.4 启动:
   - `SqliteConnectionFactory.ResolveDbPath()` 检测到旧 `catalog.db` 但无 `state.db` → 自动 `File.Move(catalog.db, state.db)`
   - `state.db` 含 `environments` / `scanned_nodes` 等用户表,WPF 正常读
   - `CatalogCacheStore` 路径是 `<AppBaseDir>/data/catalog-cache.db`,首次启动文件不存在 → `init_schema`(空表)
   - Catalog 页显示"暂无数据,去 Settings 刷新"
4. 用户去 Settings 点"刷新节点目录" → 数据下载到新位置
5. 用户 env list / scanned nodes 完整保留,只是 catalog 重新拉一遍

---

## 3. 架构 & 数据流

### 3.1 db 拆分(关键架构)

```
旧:
  %APPDATA%\ComfyUI-Manager\catalog.db (混合)
    ├── catalog_cache         (随包,移到包目录)
    ├── environments          (用户数据,留 %APPDATA%)
    ├── scanned_nodes         (用户数据,留 %APPDATA%)
    ├── process_state         (用户数据,留 %APPDATA%)
    ├── version_history       (用户数据,留 %APPDATA%)
    ├── nodes                 (用户数据,留 %APPDATA%)
    ├── ... 其他 6+ 表

新:
  <AppBaseDir>\data\catalog-cache.db
    └── catalog_cache         (随包,init schema by WPF)

  %APPDATA%\ComfyUI-Manager\state.db
    ├── environments
    ├── scanned_nodes
    ├── process_state
    ├── version_history
    ├── nodes
    ├── ... 其他 6+ 表(原 catalog.db 重命名为 state.db,WPF init schema)
```

**实现**:
- `SqliteConnectionFactory.ResolveDbPath()`:检测到旧 `catalog.db` 存在且无 `state.db` → 自动 `File.Move(catalog.db, state.db)`(首次升级时一次性迁移)
- 路径常量:新加 `static class DbPaths { public const string StateDbName = "state.db"; public const string CacheDbName = "catalog-cache.db"; public const string CacheDbSubdir = "data"; }`
- `CatalogCacheStore`(新类,本质窄化版 SqliteConnectionFactory):path = `<AppBaseDir>/data/catalog-cache.db`,自己 `init_schema_catalog_cache`(`CREATE TABLE IF NOT EXISTS catalog_cache (id TEXT PRIMARY KEY, package TEXT, source_url TEXT, raw_metadata TEXT, cached_at TEXT, expires_at TEXT, ...)` + 索引)
- `CatalogRepository` 改用 `CatalogCacheStore`,其他 Repository 继续用 `SqliteConnectionFactory`

### 3.2 数据流(共享 `CatalogRefreshService`)

**新加服务** `Services/CatalogRefreshService.cs` — 集中所有 refresh 逻辑,供 Settings + Catalog 共用:

```csharp
public class CatalogRefreshService {
    private readonly CatalogFetcher _fetcher;
    private readonly CatalogRepository _repo;
    private readonly Settings _settings;

    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default) {
        var src = _settings.QuerySources.FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        try {
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            foreach (var e in entries) { e.SourceUrl = src.Url; _repo.Upsert(e); }
            return RefreshResult.Ok(entries.Count);
        } catch (Exception ex) {
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }
}

public record RefreshResult(bool Success, int EntryCount, string? Error) {
    public static RefreshResult Ok(int n) => new(true, n, null);
    public static RefreshResult Fail(string err) => new(false, 0, err);
}
```

**CatalogViewModel** 数据流(打开页面):

```
ctor:
  ↓
  读 Settings.ViewMode → _viewMode
  ↓
  Search()             // 读 _catalogCacheStore 全部 entries → _allEntries (List<CatalogEntry>)
  ↓
  ApplyPage()          // _allEntries.Skip((Page-1)*Size).Take(Size) → PagedEntries
  ↓
  // 去掉 fire-and-forget InitialRefreshAsync — no auto
  // 用户须手动点 Settings/Catalog 刷新按钮

RefreshCommand (Catalog 页手动刷新):
  ↓
  IsBusy = true
  ↓
  await _refreshService.RefreshAsync()
  ↓
  if (Success): Search() → ApplyPage() → 跳第 1 页 → InfoMessage = "刷新成功,共 N 个"
  else: ErrorMessage = result.Error
  ↓
  finally: IsBusy = false

Search(query):
  _query = query
  _allEntries = _catalogCacheStore.Search(query, 0)  // 0 = no limit
  CurrentPage = 1  // 搜索后重置
  ApplyPage()

ApplyPage():
  PagedEntries.Clear()
  skip = (CurrentPage - 1) * PageSize
  for e in _allEntries.Skip(skip).Take(PageSize): PagedEntries.Add(e)
  TotalPages = max(1, ceil(_allEntries.Count / PageSize))
  OnPropertyChanged(CurrentPage, TotalPages, HasEntries)

ViewMode setter:
  _settings.CatalogViewMode = value
  _settingsRepo.Save(_settings)
  OnPropertyChanged(ViewMode)
  // XAML DataTemplateSelector 自动重新评估
```

**SettingsViewModel** 数据流(点刷新按钮):

```
RefreshCatalogCommand.Execute():
  ↓
  if (no active source) → ErrorMessage = "未配置查询源,请先添加"
  ↓
  IsBusy = true, StatusMessage = "正在刷新..."
  ↓
  await _refreshService.RefreshAsync() (同一个 Service 实例)
  ↓
  if (Success): StatusMessage = $"刷新成功,共 N 个条目"(绿色,5s 后清空)
  else: ErrorMessage = result.Error(红色)
  ↓
  finally: IsBusy = false
```

### 3.3 XAML 渲染路径

**CatalogView.xaml**:

```
UserControl
├── StackPanel 顶部
│   ├── StackPanel (Orientation=Horizontal) toggle 按钮
│   │   ├── Button "列表" Command={Binding SetListViewCommand}
│   │   │   Style: IsChecked triggers 高亮 (用现有 MaterialButton 加 state)
│   │   └── Button "磁贴" Command={Binding SetTileViewCommand}
│   ├── Button "刷新" Command={Binding RefreshCommand} (快速重试入口)
│   ├── ProgressBar IsBusy=True 时显示 (Visibility 由 converter 控)
│   └── TextBlock ErrorMessage (红色,可选显示) / InfoMessage (绿色,5s 自动消失)
│
├── ItemsControl (List 模式时激活) Visibility={Binding IsListMode, Converter=BoolToVis}
│   └── DataGrid + 现有 5 列(包名/作者/⭐/说明/操作)
│
├── ItemsControl (Tile 模式时激活) Visibility={Binding IsTileMode, Converter=BoolToVis}
│   └── ItemsPanelTemplate: WrapPanel
│   └── ItemTemplate: 详细卡片 StackPanel (320 宽, 圆角 8, 边距 8)
│
├── TextBlock 空状态 (Visibility={Binding !HasEntries, Converter=BoolToVis})
│   └── Text 二态: "暂无数据,去 Settings 刷新"(默认)/ "刷新失败: {msg}"(失败后,ErrorMessage)
│
└── StackPanel 底部 (Visibility={Binding HasEntries, Converter=BoolToVis})
    ├── Button "< Prev" Command={Binding PrevPageCommand} (第 1 页时 IsEnabled=false)
    ├── TextBlock "1 / 3"
    └── Button "Next >" Command={Binding NextPageCommand} (最后页时 IsEnabled=false)
```

**SettingsView.xaml**(在"查询节点的源" section 下方加刷新按钮):

```
"查询节点的源" section (现有)
  ├── section header "查询节点的源"
  ├── ComboBox (active source)
  ├── ItemsControl (list of sources)
  ├── "+ 添加" button
  └── inline add form
+ 新增:
  └── Button "刷新节点目录" Command={Binding RefreshCatalogCommand}
      Style: MaterialButton
      IsEnabled: !IsBusy
      Text: 正常 "刷新节点目录" / IsBusy 时 "刷新中..."
  + StatusMessage TextBlock (绿色 / 红色,5s 自动消失)
```

### 3.4 关键类签名

```csharp
// Models/Settings.cs
public enum CatalogViewMode { List, Tile }

public class Settings {
    // ... existing fields
    [JsonPropertyName("catalog_view_mode")]
    public CatalogViewMode CatalogViewMode { get; set; } = CatalogViewMode.List;

    [JsonPropertyName("catalog_page_size")]
    public int CatalogPageSize { get; set; } = 20;
}

// Data/CatalogCacheStore.cs (新)
public sealed class CatalogCacheStore {
    public string DbPath { get; }
    public CatalogCacheStore();  // 解析 <AppBaseDir>/data/catalog-cache.db, mkdir, init schema
    public CatalogCacheStore(string dbPath);  // test 注入
    public SqliteConnection Open();
    private void InitSchema(SqliteConnection conn);  // CREATE TABLE IF NOT EXISTS catalog_cache
}

// Data/CatalogRepository.cs (改)
public class CatalogRepository {
    public CatalogRepository(CatalogCacheStore store);  // 替换原 SqliteConnectionFactory
    public List<CatalogEntry> Search(string query, int limit);
    public void Upsert(CatalogEntry entry);
    // 其他方法不变
}

// Data/SqliteConnectionFactory.cs (改)
public sealed class SqliteConnectionFactory {
    public SqliteConnectionFactory();  // 默认 %APPDATA%/state.db
    public SqliteConnectionFactory(string dbPath);
    private static string ResolveDbPath();  // 旧 catalog.db 存在 → 改名为 state.db
    public SqliteConnection Open();
    private void InitSchemaIfMissing(SqliteConnection conn);  // 新:init 用户表 schema(WPF 自给自足)
}

// Infrastructure/SettingsDefaults.cs (改)
public static class SettingsDefaults {
    public const CatalogViewMode DefaultCatalogViewMode = CatalogViewMode.List;
    public const int DefaultCatalogPageSize = 20;
    public static void Apply(Settings s, string baseDir);  // 加 2 个新默认值
}

// Services/CatalogRefreshService.cs (新,共享)
public class CatalogRefreshService {
    public CatalogRefreshService(CatalogFetcher fetcher, CatalogRepository repo, Settings settings);
    public Task<RefreshResult> RefreshAsync(CancellationToken ct = default);
}
public record RefreshResult(bool Success, int EntryCount, string? Error) {
    public static RefreshResult Ok(int n);
    public static RefreshResult Fail(string err);
}

// ViewModels/CatalogViewModel.cs (大改)
public class CatalogViewModel : ViewModelBase {
    public ObservableCollection<CatalogEntry> PagedEntries { get; } = new();
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public int PageSize => _settings.CatalogPageSize;
    public CatalogViewMode ViewMode { get; private set; }
    public bool IsListMode => ViewMode == CatalogViewMode.List;
    public bool IsTileMode => ViewMode == CatalogViewMode.Tile;
    public bool HasEntries => _allEntries.Count > 0;  // 通知属性
    public string? ErrorMessage { get; private set; }  // 已存在
    public string? InfoMessage { get; private set; }  // 新:成功提示
    public bool IsBusy { get; private set; }  // 已存在

    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public RelayCommand SetTileViewCommand { get; }
    public RelayCommand RefreshCommand { get; }  // 改:内部调 _refreshService

    public CatalogViewModel(CatalogRepository repo, EnvironmentRepository envRepo,
                            NodeOperations nodeOps, CatalogRefreshService refreshService, Settings settings);
    // 删除: 4-arg 旧 ctor (fetcher)

    // 私有
    private List<CatalogEntry> _allEntries = new();
    private void Search();
    private void ApplyPage();
    private void SetViewMode(CatalogViewMode mode);  // 写 Settings
    // 删: InitialRefreshAsync (no auto)
}

// ViewModels/SettingsViewModel.cs (改,加 RefreshCatalogCommand)
public class SettingsViewModel : ViewModelBase {
    // ... 现有 6 UI state props + 8 commands
    public bool IsBusy { get; private set; }  // 新
    public string? StatusMessage { get; private set; }  // 新(替换 EmptyStateText)
    public string? ErrorMessage { get; private set; }  // 新
    public RelayCommand RefreshCatalogCommand { get; }  // 新

    public SettingsViewModel(SettingsRepository repo, GitProxyConfig gitProxy,
                             CatalogRefreshService refreshService);  // ctor 加 refreshService
    private async Task RefreshCatalogAsync();
}

// Views/CatalogView.xaml (大改)
// - 顶部 toggle
// - ItemsControl + DataTemplateSelector (新增)
// - 底部分页
// - 空状态 panel

// Views/SettingsView.xaml (改,加刷新按钮)
// - "查询节点的源" section 末尾加 Button "刷新节点目录" + StatusMessage TextBlock

// Views/CatalogViewTemplateSelector.cs (新)
public class CatalogViewTemplateSelector : DataTemplateSelector {
    public DataTemplate? ListTemplate { get; set; }
    public DataTemplate? TileTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container);
    // 根据 (container as FrameworkElement).DataContext as CatalogViewModel 的 ViewMode 返回
}

// Resources/Theme.xaml (加)
// <DataTemplate x:Key="CatalogTileTemplate">...</DataTemplate>
// <ItemsPanelTemplate x:Key="CatalogTileWrapPanel"><WrapPanel .../></ItemsPanelTemplate>
```

---

## 4. UI 设计

### 4.1 列表模式(现状,微调)

DataGrid 5 列:包名 / 作者 / ⭐ / 说明 / 操作(安装按钮)。每行 20 条(原 50 → 改 20)。

### 4.2 磁贴模式(新)

```
WrapPanel 2-3 列 (响应式,窗口窄时 1 列)

┌────────────────────────────┐
│ 包名(bold, 14pt)            │
│ 作者 · ⭐ 1.2k              │
│                            │
│ 说明(2-3 行, ellipsis)      │
│ ...                         │
│                            │
│            [ 安装 ]         │
└────────────────────────────┘

每张卡:
- Width = 320
- Margin = 8
- Background = White
- BorderBrush = LightGray, BorderThickness = 1
- CornerRadius = 8
- Padding = 12
- StackPanel Orientation=Vertical
```

### 4.3 顶部 toggle(两按钮 group)

```
[ 列表 ] [ 磁贴 ]
^^^^^^^
高亮(active)

样式:用现有 MaterialButton 加 IsChecked visual state;
或简化为 Border + 背景色切换(IsListMode → 蓝底白字 / 普通边框)
```

### 4.4 底部分页

```
[ < Prev ]   1 / 3   [ Next > ]

- 第 1 页:Prev disabled
- 最后页:Next disabled
- IsListMode 和 IsTileMode 都显示分页
```

### 4.5 空状态

```
页面中心 TextBlock (三态):
  - 加载中: "正在刷新..."(灰色 14pt, IsBusy=true 时显示)
  - 失败:   "刷新失败: <message>"(红色 14pt, ErrorMessage 区域显示)
  - 空(无 cache): "暂无数据,去 Settings 刷新"(灰色 14pt,默认空状态)
```

### 4.6 Settings 页"刷新节点目录"按钮(新)

```
"查询节点的源" section 末尾:
  + Button "刷新节点目录" Command={Binding RefreshCatalogCommand}
    - IsEnabled: !IsBusy
    - Text: 正常 "刷新节点目录" / IsBusy 时 "刷新中..."(禁用)
    - Style: MaterialButton
  + TextBlock StatusMessage (绿色 12pt, 5s 后自动清空)
  + TextBlock ErrorMessage (红色 12pt, 手动清空或下次操作覆盖)
```

---

## 5. 测试

### 5.1 改(2 个)

- `Ctor_LoadsAllCatalogEntries` → `Ctor_LoadsLocalCache_AsFirstPage`:ctor 读 2 个 seed entries → `PagedEntries.Count == 2`(因为 2 < PageSize=20,只 1 页)
- `Query_FiltersEntries` → `Query_FiltersAndResetsToFirstPage`:query "alph" → 只有 alpha → CurrentPage=1

### 5.2 新加(8-9 个)

- ~~`Ctor_InitialRefresh_StartsInBackground`~~ — **删**(no auto refresh)
- `NextPageCommand_AdvancesPage_WhenMorePages`:seed 25 个,CurrentPage=1,NextCommand → CurrentPage=2,PagedEntries.Count==5(25 - 20)
- `NextPageCommand_Disabled_OnLastPage`:seed 5 个,CurrentPage=1,NextCommand.CanExecute == false
- `PrevPageCommand_Disabled_OnFirstPage`:seed 5 个,CurrentPage=1,PrevCommand.CanExecute == false
- `ViewMode_DefaultsFromSettings_List`:new Settings().CatalogViewMode = List → VM.ViewMode == List
- `SetTileViewCommand_PersistsToSettings`:触发 SetTileViewCommand → Settings.CatalogViewMode == Tile(模拟 Save)
- `RefreshCommand_DelegatesToRefreshService`:FakeRefreshService → RefreshCommand.Execute → service.RefreshAsync 调 1 次
- `RefreshCommand_Success_ShowsInfoMessage`:FakeRefreshService 返回 Ok(120) → InfoMessage = "刷新成功,共 120 个"
- `CatalogCacheStore_CreatesDbFileAtAppBaseDir`:new CatalogCacheStore(tempPath) → file exists + `SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_cache'` 返回一行
- `SqliteConnectionFactory_RenamesLegacyCatalogDb_ToStateDb`:在 test 目录建 `catalog.db`(无 `state.db`)→ 触发 ctor → 验证 `state.db` 存在 + `catalog.db` 不存在
- `SettingsViewModel_RefreshCatalogCommand_CallsService`:FakeRefreshService → 触发 → service.RefreshAsync 调 1 次
- `SettingsViewModel_RefreshCatalogCommand_Success_SetsStatusMessage`:FakeRefreshService.Ok(50) → StatusMessage = "刷新成功,共 50 个条目"

### 5.3 总数

旧:74。新增 10-11 + 改 2 个 = **总 82-83 个**。所有 dotnet test 应绿。

---

## 6. 风险 & 缓解

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| R1 | 旧 `%APPDATA%/catalog.db` 里 `catalog_cache` 数据丢 | 中:用户首次升 v0.6.4 后 Catalog 页是空的 | 用户去 Settings 点"刷新节点目录"按钮手动重新拉(15s 内完成);提示文案"暂无数据,去 Settings 刷新"明示 |
| R2 | 旧 db 改名 `catalog.db → state.db` 在 Windows 上 file lock | 低:旧 db 只在 v0.6.3 之前用过,M5.2 后基本没人用旧 db | `File.Move` 失败回退到读旧 db(容错) |
| R3 | WrapPanel 不虚拟化,20 个/页 OK,但 200 个会卡 | 低:PageSize=20 受控 | 若未来要 100/页,改 `VirtualizingWrapPanel` |
| R4 | 用户首次启动不知道要去 Settings 点刷新 | 中:UX 上需明示 | 空状态文案 "暂无数据,去 Settings 刷新" + Settings 页刷新按钮在"查询节点"section 末尾显眼位置 |
| R5 | `AppContext.BaseDirectory` 在单文件 vs 框架依赖发布不同 | 低:已用(grep 验证) | 沿用现有 settings.json 解析模式 |
| R6 | `CatalogRefreshService` 注入到 2 个 VM,Singleton 还是 scoped? | 低:WPF 单一容器,共享同一个实例 | `App.xaml.cs` 构造 1 个 `CatalogRefreshService` 实例,2 个 VM 共享 |
| R6 | `Ctor_LoadsAllCatalogEntries` test 名误导 | 低:只是 test 改名 | 改名 + 改语义 |
| R7 | db 拆分后 `%APPDATA%/state.db` 表结构 init 谁来做? | 中:以前是 Python service init,M5.2 删了 | `SqliteConnectionFactory.Open` 首次调用时 `InitSchemaIfMissing`(CREATE TABLE IF NOT EXISTS for 所有用户表) |
| R8 | 视图模式 toggle 按钮样式无现成 | 低:只是写一个简单 trigger | 用现有 MaterialButton style,加 IsChecked visual state |

---

## 7. 改动文件清单

| 文件 | 改动 | 行数估计 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 加 `CatalogViewMode` enum + 2 字段 | +15 |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | 加 2 默认值 | +5 |
| `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` | **新文件**(共享) | +40 |
| `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` | **新文件** | +50 |
| `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` | 改 ResolveDbPath + InitSchemaIfMissing | +30 / -10 |
| `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` | 改用 CatalogCacheStore | +5 / -5 |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | 大改:分页 + refresh 走 Service + 视图模式 | +120 / -50 |
| `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` | 加 RefreshCatalogCommand + IsBusy/Status/Error | +50 / -5 |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 大改:toggle + 视图切换 + 分页 + 空状态 + 刷新按钮 | +90 / -30 |
| `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` | 加"刷新节点目录"按钮 + Status/Error TextBlock | +30 / -0 |
| `src-wpf/ComfyUI.Manager/Views/CatalogViewTemplateSelector.cs` | **新文件** | +25 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 构造 `CatalogRefreshService` 实例,共享给 2 个 VM | +5 / -2 |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | 加 TileTemplate + WrapPanel | +40 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs` | 改 2 + 加 7 | +100 / -20 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` | 加 2(RefreshCatalog) | +40 / -0 |
| `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs` | **新文件** | +60 |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs` | **新文件** | +50 |
| `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs` | **新文件** | +30 |

**总计**:18 个文件(4 新 + 14 改),约 +730 / -120 行

---

## 8. 不在本 spec 范围(后续可做)

- 磁贴视图虚拟化(`VirtualizingWrapPanel`)
- 分页跳页(直接跳到第 N 页)
- 磁贴视图自定义排序/筛选
- catalog JSON 原始文件缓存(目前只在 SQLite)
- 多 query source 并行合并拉取
- 离线/在线状态自动检测 + 跳过 fetch
- Catalog 顶部的快速"刷新"按钮 vs Settings 的"刷新节点目录"按钮合并(目前共存)

---

## 9. 实施 plan

按依赖顺序拆 task(给 writing-plans 阶段用):

1. **T1: Settings 字段 + Defaults** — `CatalogViewMode` 枚举 + 2 字段 + 默认值 + 3 个 test
2. **T2: CatalogCacheStore + SqliteConnectionFactory 拆分** — 新类 + 改名 + InitSchemaIfMissing + 4 个 test
3. **T3: CatalogRepository 改造** — 用 CatalogCacheStore + 1 个集成 test
4. **T4: CatalogRefreshService** — 共享 Service + RefreshResult record + 3-4 个 test
5. **T5: CatalogViewModel 分页 + 视图模式 + Refresh 走 Service** — 改 ctor (加 refreshService) + 去掉 auto + 改 2 + 加 7 个 test
6. **T6: SettingsViewModel 加 RefreshCatalogCommand** — IsBusy / Status / Error + 2 个 test
7. **T7: CatalogView.xaml 大改** — toggle / DataTemplateSelector / 分页 / 空状态 / 快速刷新按钮
8. **T8: SettingsView.xaml 加刷新按钮** — Button + Status TextBlock
9. **T9: App.xaml.cs 注入 CatalogRefreshService** — 共享 1 个实例
10. **T10: Theme.xaml TileTemplate** — WrapPanel + 详细卡片 DataTemplate
11. **T11: 全量 test 跑 + UI 手动验证 + bump v0.6.4 + release**

---

## 10. 验收

- `dotnet build src-wpf/ComfyUI.Manager/` → 0 警告 0 错误
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/` → 82-83 tests 全绿(旧 74 不掉,新 10-11 个)
- `pytest tests/test_version_consistency.py` → 3/3 PASS(版本号 0.6.3 → 0.6.4)
- 手动启动 v0.6.4 release exe:
  1. 打开 Catalog 页 → 看到 "暂无数据,去 Settings 刷新"(空状态)
  2. 切到 Settings → 点 "刷新节点目录" → 5-15s 后看到 "刷新成功,共 120 个条目"
  3. 切回 Catalog → 看到 DataGrid 满 20 行 + 底部 `1 / 6` + 可点 `Next >` 翻页
  4. 点 `磁贴` → 切到卡片视图,关 WPF 重开仍是 `磁贴`
  5. 断网 + Catalog 页 → 点 Catalog 顶"刷新" → 拉失败 → ErrorMessage = "刷新失败: <reason>"
  6. 旧 `%APPDATA%/catalog.db` 自动改名为 `state.db`,新 `<AppBaseDir>/data/catalog-cache.db` 创建
- `release/ComfyUI-Manager-v0.6.4-win-x64.zip` build 成功 + GitHub release 标记 Latest
  5. `%APPDATA%/state.db` 存在 + `<AppBaseDir>/data/catalog-cache.db` 存在
  6. 旧 `%APPDATA%/catalog.db` 自动改名为 `state.db`
- `release/ComfyUI-Manager-v0.6.4-win-x64.zip` build 成功 + GitHub release 标记 Latest
