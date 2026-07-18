# v0.6.4 Hotfix — Catalog 页面:自动加载 + 分页 + 磁贴/列表切换 + Cache 拆分

**里程碑:** v0.6.4 hotfix(v0.6.3 之后)
**日期:** 2026-07-18
**状态:** 待用户审阅
**Base SHA:** v0.6.3 tag(HEAD `2ac2829`)

---

## 0. 摘要

WPF Catalog 页面三项 UX 改造 + 一项架构调整:

1. **去掉"打开页面就看到 10 个默认包"**:改为打开页面自动 fire-and-forget 拉 active QuerySource,拉到后分页(20/页)展示;离线/拉失败时显示空状态 + 错误提示
2. **视图模式可切换**:`列表`(DataGrid 现状)vs `磁贴`(WrapPanel 详细卡片),顶部 toggle 切换,持久化到 settings.json
3. **Cache 数据库拆分**:`catalog_cache` 表移到绿色包目录 `<AppBaseDir>/data/catalog-cache.db`(随包发布走),其他用户表(env list / scanned_nodes / version_history 等)继续留在 `%APPDATA%\ComfyUI-Manager\state.db`

### 关键决策

| 决策 | 选择 |
|---|---|
| 自动加载触发 | ctor fire-and-forget 调 `InitialRefreshAsync()`,不等结果,IsBusy 状态指示 |
| 分页大小 | `Settings.CatalogPageSize = 20`(可配置,默认 20) |
| 视图模式持久化 | `Settings.CatalogViewMode` 枚举(`List` / `Tile`),默认 `List` |
| 磁贴样式 | 详细卡片(WrapPanel 2-3 列,每张 320 宽,显示 包名/作者/⭐/说明/安装按钮) |
| 视图切换实现 | `DataTemplateSelector`(而非 ContentControl + Style trigger),两个 `DataTemplate` x:Key |
| Cache db 路径 | `<AppBaseDir>/data/catalog-cache.db`(目录自动 mkdir) |
| 用户 db 路径 | `%APPDATA%\ComfyUI-Manager\state.db`(原 catalog.db 路径改名,只放用户表) |
| 旧 catalog_cache 数据迁移 | **不做迁移**;首次启动自动 fetch 重新填充(用户最多等 1 个 refresh 周期,15s 超时) |
| 空状态 | "暂无数据,正在自动加载..."(加载中)/ "加载失败,点 刷新 重试"(失败后) |
| `Ctor_LoadsAllCatalogEntries` test | 改名 + 重写语义(变"读本地 cache 到第一页") |

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

- Catalog 页面打开后 **自动从 active QuerySource 拉 JSON** → 解析 → 写入 `<AppBaseDir>/data/catalog-cache.db` 的 `catalog_cache` 表 → 自动切到第 1 页显示
- 显示分页:每页 20 个,底部 `Prev / 1/N / Next` 翻页,搜索 query 变化时重置回第 1 页
- 视图模式 toggle:顶部两个按钮 `列表` / `磁贴`,active 高亮,点击切换 + 写 `Settings.CatalogViewMode`
- 列表模式:复用现有 DataGrid
- 磁贴模式:WrapPanel 2-3 列 + 详细卡片(包名 + 作者 + ⭐ + 说明 2-3 行 + 安装按钮)
- 空状态文案:根据 `IsBusy` / `ErrorMessage` / `HasEntries` 三态显示 "暂无数据,正在自动加载..." / "加载失败: {message},点 刷新 重试" / "暂无数据"
- `catalog_cache` 表从 `%APPDATA%/catalog.db` 拆分到 `<AppBaseDir>/data/catalog-cache.db`,新 db 文件首次创建时自动 init schema(`CREATE TABLE IF NOT EXISTS catalog_cache ...`)
- `Settings` 加 `CatalogViewMode` 枚举字段 + `CatalogPageSize` int 字段;`SettingsDefaults.Apply` 默认 `List` / 20
- 现有 74 个 WPF tests 不掉(改 2 个 + 加 4-5 个,总 78-80 个)
- 改完后 `dotnet test` 全绿,`dotnet build` 0 警告 0 错误

### 1.2 非目标(明确不做)

- ❌ 多 query source 并行合并拉取(沿用 v0.6.3:同时只 1 个 active)
- ❌ 旧 `%APPDATA%` 里 catalog_cache 数据迁移(用户接受"首次启动自动重新 fetch")
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
3. 页面显示 "暂无数据,正在自动加载..."(顶部 IsBusy 进度条)
4. 后台 `InitialRefreshAsync` 拉 `https://raw.githubusercontent.com/.../custom-node-list.json`(~5-15s)
5. 拉到后:写入 `<AppBaseDir>/data/catalog-cache.db` → `Search()` 重读 → `ApplyPage()` → 显示前 20 个
6. 用户看到 DataGrid 满 20 行 + 底部 `1 / 3`(假设有 60 个条目)

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
3. 后台 `InitialRefreshAsync` 拉新失败 → `ErrorMessage = "拉取失败: <reason>(本地缓存仍可用)"`
4. 用户点 `刷新` 按钮重试 → 仍失败 → 错误文案不变,本地 cache 仍渲染

### US-5:用户升级 v0.6.3 → v0.6.4

1. 用户覆盖安装 v0.6.4
2. 旧 `%APPDATA%/ComfyUI-Manager/catalog.db` 还在(里面只有 catalog_cache + 其他用户表的混合)
3. v0.6.4 启动:
   - `SqliteConnectionFactory` 路径仍是 `%APPDATA%/state.db`,正常读 `environments` / `scanned_nodes` 等用户表
   - `CatalogCacheStore` 路径是 `<AppBaseDir>/data/catalog-cache.db`,首次启动文件不存在 → `init_schema` + 自动 fetch → 写入新表
   - 旧 `%APPDATA%/state.db` 里的 `catalog_cache` 表被忽略(无害,WPF 不再读它)
4. 用户 env list / scanned nodes 完整保留,只是 catalog 重新拉一遍

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

### 3.2 CatalogViewModel 数据流

```
ctor:
  ↓
  读 Settings.ViewMode → _viewMode
  ↓
  Search()             // 读 _catalogCacheStore 全部 entries → _allEntries (List<CatalogEntry>)
  ↓
  ApplyPage()          // _allEntries.Skip((Page-1)*Size).Take(Size) → PagedEntries
  ↓
  fire-and-forget InitialRefreshAsync():
    ↓
    IsBusy = true
    ↓
    try: GET active query source URL (HttpClient 15s timeout)
         parse → List<CatalogEntry>
         _catalogCacheStore.Upsert(e) per entry  (注: CatalogRepository.Upsert 走 cache store)
         _allEntries = new List from _catalogCacheStore.Search("", 0) (无限)
         ApplyPage()  // 跳回第 1 页
    catch: ErrorMessage = "拉取失败: {ex.Message}(本地缓存仍可用)"
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

### 3.3 XAML 渲染路径

```
UserControl
├── StackPanel 顶部
│   ├── StackPanel (Orientation=Horizontal) toggle 按钮
│   │   ├── Button "列表" Command={Binding SetListViewCommand}
│   │   │   Style: IsChecked triggers 高亮 (用现有 MaterialButton 加 state)
│   │   └── Button "磁贴" Command={Binding SetTileViewCommand}
│   ├── ProgressBar IsBusy=True 时显示 (Visibility 由 converter 控)
│   └── TextBlock ErrorMessage (红色,可选显示)
│
├── ContentControl Content={Binding PagedEntries}
│   │ (注: 实际用 ItemsControl, PagedEntries 是 ObservableCollection<CatalogEntry>)
│   │
│   ├── ItemsControl (List 模式时激活) Visibility={Binding IsListMode, Converter=BoolToVis}
│   │   └── DataGrid + 现有 5 列(包名/作者/⭐/说明/操作)
│   │
│   └── ItemsControl (Tile 模式时激活) Visibility={Binding IsTileMode, Converter=BoolToVis}
│       └── ItemsPanelTemplate: WrapPanel
│       └── ItemTemplate: 详细卡片 StackPanel (320 宽, 圆角 8, 边距 8)
│
├── TextBlock 空状态 (Visibility={Binding !HasEntries, Converter=BoolToVis})
│   └── Text 三态: "暂无数据,正在自动加载..." / "加载失败: {msg},点 刷新 重试" / "暂无数据"
│
└── StackPanel 底部 (Visibility={Binding HasEntries, Converter=BoolToVis})
    ├── Button "< Prev" Command={Binding PrevPageCommand} (第 1 页时 IsEnabled=false)
    ├── TextBlock "1 / 3"
    └── Button "Next >" Command={Binding NextPageCommand} (最后页时 IsEnabled=false)
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
    public bool IsBusy { get; private set; }  // 已存在
    public string? EmptyStateText { get; private set; }  // 三态文案

    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public RelayCommand SetTileViewCommand { get; }
    public RelayCommand RefreshCommand { get; }  // 已存在

    public CatalogViewModel(CatalogRepository repo, EnvironmentRepository envRepo,
                            NodeOperations nodeOps, CatalogFetcher fetcher, Settings settings);

    // 私有
    private List<CatalogEntry> _allEntries = new();
    private void Search();
    private void ApplyPage();
    private void SetViewMode(CatalogViewMode mode);  // 写 Settings
    private async Task InitialRefreshAsync();
}

// Views/CatalogView.xaml (大改)
// - 顶部 toggle
// - ItemsControl + DataTemplateSelector (新增)
// - 底部分页
// - 空状态 panel

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
页面中心 TextBlock:
  - 加载中: "暂无数据,正在自动加载..."(灰色 14pt)
  - 失败:   "加载失败: <message>。点 刷新 重试。"(红色 14pt)
  - 空(无 cache 也无 fetch): "暂无数据,点 刷新 加载"(灰色)
```

---

## 5. 测试

### 5.1 改(2 个)

- `Ctor_LoadsAllCatalogEntries` → `Ctor_LoadsLocalCache_AsFirstPage`:ctor 读 2 个 seed entries → `PagedEntries.Count == 2`(因为 2 < PageSize=20,只 1 页)
- `Query_FiltersEntries` → `Query_FiltersAndResetsToFirstPage`:query "alph" → 只有 alpha → CurrentPage=1

### 5.2 新加(7-8 个)

- `Ctor_InitialRefresh_StartsInBackground`:`FakeCatalogFetcher` 预设 5 个 entries,await 200ms,断言 PagedEntries.Count == 5
- `NextPageCommand_AdvancesPage_WhenMorePages`:seed 25 个,CurrentPage=1,NextCommand → CurrentPage=2,PagedEntries.Count==5(25 - 20)
- `NextPageCommand_Disabled_OnLastPage`:seed 5 个,CurrentPage=1,NextCommand.CanExecute == false
- `PrevPageCommand_Disabled_OnFirstPage`:seed 5 个,CurrentPage=1,PrevCommand.CanExecute == false
- `ViewMode_DefaultsFromSettings_List`:new Settings().CatalogViewMode = List → VM.ViewMode == List
- `SetTileViewCommand_PersistsToSettings`:触发 SetTileViewCommand → Settings.CatalogViewMode == Tile(模拟 Save)
- `CatalogCacheStore_CreatesDbFileAtAppBaseDir`:new CatalogCacheStore(tempPath) → file exists + `SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_cache'` 返回一行
- `SqliteConnectionFactory_RenamesLegacyCatalogDb_ToStateDb`:在 test 目录建 `catalog.db`(无 `state.db`)→ 触发 ctor → 验证 `state.db` 存在 + `catalog.db` 不存在

### 5.3 总数

旧:74。新增 7-8 + 改 2 个 = **总 80-82 个**。所有 dotnet test 应绿。

---

## 6. 风险 & 缓解

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| R1 | 旧 `%APPDATA%/catalog.db` 里 `catalog_cache` 数据丢 | 中:用户首次升 v0.6.4 后 Catalog 页是空的,要等自动 fetch(15s) | ctor fire-and-forget `InitialRefreshAsync`,失败也不阻塞;若成功用户无感 |
| R2 | 旧 db 改名 `catalog.db → state.db` 在 Windows 上 file lock | 低:旧 db 只在 v0.6.3 之前用过,M5.2 后基本没人用旧 db | `File.Move` 失败回退到读旧 db(容错) |
| R3 | WrapPanel 不虚拟化,20 个/页 OK,但 200 个会卡 | 低:PageSize=20 受控 | 若未来要 100/页,改 `VirtualizingWrapPanel` |
| R4 | 自动 fetch 离线时 15s 卡顿 | 中:用户打开 Catalog 页要等 15s 才看到本地 cache | `InitialRefreshAsync` 不 await,后台跑,本地 cache 立即显示(先 Search 本地,再后台 fetch) |
| R5 | `AppContext.BaseDirectory` 在单文件 vs 框架依赖发布不同 | 低:已用(grep 验证) | 沿用现有 settings.json 解析模式 |
| R6 | `Ctor_LoadsAllCatalogEntries` test 名误导 | 低:只是 test 改名 | 改名 + 改语义 |
| R7 | db 拆分后 `%APPDATA%/state.db` 表结构 init 谁来做? | 中:以前是 Python service init,M5.2 删了 | `SqliteConnectionFactory.Open` 首次调用时 `InitSchemaIfMissing`(CREATE TABLE IF NOT EXISTS for 所有用户表) |
| R8 | 视图模式 toggle 按钮样式无现成 | 低:只是写一个简单 trigger | 用现有 MaterialButton style,加 IsChecked visual state |

---

## 7. 改动文件清单

| 文件 | 改动 | 行数估计 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/Settings.cs` | 加 `CatalogViewMode` enum + 2 字段 | +15 |
| `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` | 加 2 默认值 | +5 |
| `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` | **新文件** | +50 |
| `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` | 改 ResolveDbPath + InitSchemaIfMissing | +30 / -10 |
| `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` | 改用 CatalogCacheStore | +5 / -5 |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | 大改:分页 + 自动 refresh + 视图模式 | +120 / -50 |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 大改:toggle + 视图切换 + 分页 + 空状态 | +80 / -30 |
| `src-wpf/ComfyUI.Manager/Views/CatalogViewTemplateSelector.cs` | **新文件** | +25 |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | 加 TileTemplate + WrapPanel | +40 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs` | 改 2 + 加 5 | +80 / -20 |
| `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs` | **新文件** | +50 |
| `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs` | **新文件**(或并入 CatalogCacheStoreTests) | +30 |

**总计**:12 个文件(2 新 + 10 改),约 +500 / -100 行

---

## 8. 不在本 spec 范围(后续可做)

- 磁贴视图虚拟化(`VirtualizingWrapPanel`)
- 分页跳页(直接跳到第 N 页)
- 磁贴视图自定义排序/筛选
- catalog JSON 原始文件缓存(目前只在 SQLite)
- 多 query source 并行合并拉取
- 离线/在线状态自动检测 + 跳过 fetch

---

## 9. 实施 plan

按依赖顺序拆 task(给 writing-plans 阶段用):

1. **T1: Settings 字段 + Defaults** — `CatalogViewMode` 枚举 + 2 字段 + 默认值 + 3 个 test
2. **T2: CatalogCacheStore + SqliteConnectionFactory 拆分** — 新类 + 改名 + InitSchemaIfMissing + 4 个 test
3. **T3: CatalogRepository 改造** — 用 CatalogCacheStore + 1 个集成 test
4. **T4: CatalogViewModel 分页 + 自动 refresh** — PageSize / CurrentPage / TotalPages / PagedEntries / InitialRefreshAsync / EmptyStateText + 改 2 + 加 4 个 test
5. **T5: CatalogViewModel 视图模式** — ViewMode / SetListViewCommand / SetTileViewCommand / IsListMode / IsTileMode + 2 个 test
6. **T6: CatalogView.xaml 大改** — toggle / DataTemplateSelector / 分页 / 空状态
7. **T7: Theme.xaml TileTemplate** — WrapPanel + 详细卡片 DataTemplate
8. **T8: 全量 test 跑 + UI 手动验证 + bump v0.6.4 + release**

---

## 10. 验收

- `dotnet build src-wpf/ComfyUI.Manager/` → 0 警告 0 错误
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/` → 80+ tests 全绿(旧 74 不掉,新 6-8 个)
- `pytest tests/test_version_consistency.py` → 3/3 PASS(版本号 0.6.3 → 0.6.4)
- 手动启动 v0.6.4 release exe:
  1. 打开 Catalog 页 → 看到 "暂无数据,正在自动加载..." → 5-15s 后看到 20 个条目
  2. 底部 `1 / N` 分页 → `Next >` 翻页
  3. 点 `磁贴` → 切到卡片视图,关 WPF 重开仍是 `磁贴`
  4. 断网重启 → 旧 cache 仍渲染,ErrorMessage 提示
  5. `%APPDATA%/state.db` 存在 + `<AppBaseDir>/data/catalog-cache.db` 存在
  6. 旧 `%APPDATA%/catalog.db` 自动改名为 `state.db`
- `release/ComfyUI-Manager-v0.6.4-win-x64.zip` build 成功 + GitHub release 标记 Latest
