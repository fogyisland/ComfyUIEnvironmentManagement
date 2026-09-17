# T47 Local Models — Multi-View + Star + Context Menu — Design

**Date**: 2026-09-17
**Status**: Draft → Implementation
**Author**: Claude Code
**Ticket**: T47 (SwarmUI UX 借鉴 第二期)
**Predecessor**: T46 (SwarmUI UX 借鉴 第一期,已 SHIPPED 13 commits)
**Reference**: `memory/reference_swarmui_model_query_ux.md` §「9 Display Formats」+ §「Star 收藏」+ §「Per-card context menu」

## 1. Context

**用户反馈**(2026-09-17):T46 ship 后用户对 KindChip 3D + Card Title 走 CivitAI 满意,继续推进 SwarmUI UX 第二期。

**当前问题**:`LocalModelsView` 单视图(Cards WrapPanel)+ 无 Star 收藏 + 无 context menu + 无 List/Thumbnails 切换。100+ 模型时高密度信息浏览、收藏常用模型、批量操作均不可用。

**T46 §6 deferred**:Spec §6 「Trade-offs vs SwarmUI」明确 deferred SwarmUI UX 第二期(多视图 + Star + context menu)和第三期(虚拟化 + 懒加载 + 架构 tag)。T47 落地第二期,T48 留给后续 plan。

**SwarmUI 借鉴清单**(`memory/reference_swarmui_model_query_ux.md` §6 优先级 2/3/4):
- 🗂 **多视图切换**(Cards / List / Thumbnails — SwarmUI 9 Display Formats 挑 3 个实用)
- ⭐ **Star 收藏**(持久化 + 排序置顶 + 「只看 Starred」filter)
- 📋 **每张卡的下拉上下文菜单**(Star / Copy Hash / Open Folder / Reload Hash / Refresh Metadata / Copy SourceUrl / Copy FileName / Delete)

## 2. Goal

落地 SwarmUI UX **第二期**,3 项增量能力:

1. **3 视图切换** — Cards(现状)/ List(DataGrid 高密度)/ Thumbnails(纯缩略图墙),toolbar segmented button 互斥切换,选择持久化
2. **⭐ Star 收藏** — 每张卡可星标,持久化到 `model_stars` 表,toolbar 加「只看 Starred」filter,重启 app 状态保留
3. **📋 Context menu 7 命令** — 每张卡右键菜单,7 个命令覆盖常见操作(详情见 §3.4)

**Non-goals**(留给 T48 / 未来):
- 缩略图异步懒加载(T47 同步 Freeze,T48 改 async)
- 架构 tag chips(SDXL/Flux/Wan 等)
- 拖拽排序 / 批量操作
- Star 跨设备同步(纯本地)

## 3. Design

### 3.1 架构总览(3 层)

**View 层**(`src-wpf/ComfyUI.Manager/Views/`)
- `LocalModelsView.xaml` — 主容器(toolbar + ContentControl 主体)
- `LocalModels/CardsView.xaml` — 抽出当前 WrapPanel + 加 Star 角标 + ContextMenu
- `LocalModels/ListView.xaml` — DataGrid 高密度单行
- `LocalModels/ThumbnailsView.xaml` — VirtualizingTilePanel 纯缩略图墙
- `LocalModels/LocalModelContextMenu.xaml` — 抽 ContextMenu 为资源,3 个 UserControl 复用

**ViewModel 层**(`src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs`)
- `ViewMode` enum(Cards=0 / List=1 / Thumbnails=2)+ `ActiveViewMode` property
- `StarsOnlyFilter` bool + `StarredPaths: HashSet<string>`(启动时一次性 load)
- 7 RelayCommand 包装(LocalModelOperations 调用)
- 现有 `FilteredModels` 改:`SearchText ∩ KindFilter ∩ StarsOnly` 三维交集

**Service 层**(新):
- `Services/LocalModelOperations.cs` — 7 命令 + 测试 seam(`ShowConfirmDialogOverride` / `OpenInExplorerOverride` / `SetClipboardTextOverride`)
- `Data/StarredModelsRepository.cs` — model_stars CRUD
- `Data/LocalModelSettingsRepository.cs` — model_settings key-value(K=V)

**进度对话框**:复用项目已有 `ProgressDialogService`(`MainViewModel` 现有)— verify 后引用;若无,T47 spec 内包含最小 indeterminate progress 新建(独立窗体 ~80 行)。

**Toast 服务**:复用项目已有 `ToastNotification`(`MainViewModel` 注入)— verify 后引用;若无,spec 内包含最小新建。

### 3.2 数据模型(model.db schema)

```sql
-- T47 新增(T45 schema 已含 local_model_files / model_tensor_hashes / matched_model_details)
CREATE TABLE IF NOT EXISTS model_stars (
  source_path TEXT PRIMARY KEY NOT NULL,
  starred_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_model_stars_at ON model_stars(starred_at DESC);

CREATE TABLE IF NOT EXISTS model_settings (
  key        TEXT PRIMARY KEY NOT NULL,
  value      TEXT NOT NULL,
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
```

**3 个 model_settings key**:

| key | default | 说明 |
|-----|---------|------|
| `localmodels.view_mode` | `"Cards"` | ViewMode enum 字面量 |
| `localmodels.stars_only` | `"false"` | bool 字符串 |
| `localmodels.search` | `""` | **从 T46 `Settings.LocalModelsFilter` 迁来**(见 §3.7) |

**SqliteConnectionFactory 改动**:`InitModelSchemaIfMissing` 加 2 个 `CREATE TABLE IF NOT EXISTS`(老 DB 自动跳过,无需 `EnsureColumn` 类 helper)。

**Star 行为约束**:
- `source_path` = `card.SourcePath`(绝对路径,Windows 大小写不敏感但存原始 case)
- 同 path 二次 Star = `INSERT OR IGNORE`(PK 冲突自动跳过),`starred_at` 保留首次时间
- 取消 Star = `DELETE FROM model_stars WHERE source_path = ?`
- VM 端 `StarredPaths: HashSet<string>` 启动一次性 load,toggle 即时增删,DB 同步写

**搜索字段迁移**(§3.7 Task 7 详述):启动时 `if Settings.LocalModelsFilter 非空 → 写 model_settings.localmodels.search + 清 Settings 字段`,一次性迁移不双写。

### 3.3 视图渲染

**Cards(保留 + 加 Star 角标)**

当前 `LocalModelsView.xaml` 的 WrapPanel 段抽出到 `LocalModels/CardsView.xaml`。改动:
- 卡片根 `Grid` 加 `MouseRightButtonUp` → 弹 ContextMenu
- 右上角叠 `<Border Visibility="{Binding IsStarred, Converter=BoolToVis}">⭐</Border>`,15px 字号、`HorizontalAlignment="Right" VerticalAlignment="Top" Margin="4"`、`IsHitTestVisible="False"`(不挡 ContextMenu)

**List(新,DataGrid)**

| 列 | 宽 | 绑定 |
|----|----|------|
| ★ | 32 | `IsStarred` + 点击 toggle(用 `ToggleStarCommand`) |
| Kind | 60 | KindChip(T46 已 ship style) |
| Title | * | `DisplayTitle` |
| Source | 100 | `Source` |
| Size | 80 right-align | `FormatBytes(FileSize)` |
| Hash | 140 truncated | `Hash?.Substring(0,12) + "..."` |
| CivitAI | 70 | "✓" / "—"(`MatchedDetail != null`) |

XAML:`DataGrid AutoGenerateColumns="False" CanUserAddRows="False" IsReadOnly="True" HeadersVisibility="Column" GridLinesVisibility="Horizontal"` + `RowStyle` 含 `MouseRightButtonUp`。

`FileSize` 字段:`local_model_files` T45 表已有 `size_bytes BIGINT`,Repository 读时一并填到 `LocalModelCard.FileSize`(新字段)。

**Thumbnails(新,VirtualizingTilePanel)**

```xml
<ItemsControl ItemsSource="{Binding FilteredModels}">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate>
      <VirtualizingTilePanel TileWidth="170" TileHeight="200" />
    </ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
  <ItemsControl.Template>
    <ControlTemplate>
      <VirtualizingStackPanel IsItemsHost="True" />
    </ControlTemplate>
  </ItemsControl.Template>
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Grid Width="160" Height="190" Margin="4">
        <Border>
          <Image Source="{Binding PreviewImagePath, Converter=LocalPathToBitmap}"
                 Stretch="UniformToFill" />
        </Border>
        <TextBlock Text="{Binding DisplayTitle}" MaxHeight="32" TextTrimming="CharacterEllipsis" />
        <Border .../> <!-- star overlay 同样模式 -->
      </Grid>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

缩略图源:`LocalModelCard.PreviewImagePath` 已有(T45 schema `local_model_files.preview_image_path TEXT NULL`),用 `LocalPathToBitmapConverter`(~30 行):
- 路径 null/不存在 → 返回 placeholder 灰块
- 存在 → `new BitmapImage { UriSource = ..., DecodePixelWidth = 150, CacheOption = BitmapCacheOption.OnLoad }.Freeze()`
- **同步加载**(T47 简化,T48 改异步)

虚拟化:`VirtualizingTilePanel` 需 `Win8` SDK 引用 — verify NuGet;若缺,降级 `ListBox + VirtualizingPanel.ScrollUnit=Item` 仍享虚拟化。

**视图切换机制**

```xml
<ContentControl Content="{Binding ActiveViewMode}">
  <ContentControl.ContentTemplateSelector>
    <local:ViewModeTemplateSelector>
      <local:ViewModeTemplateSelector.CardsTemplate>...</local:ViewModeTemplateSelector.CardsTemplate>
      <local:ViewModeTemplateSelector.ListTemplate>...</local:ViewModeTemplateSelector.ListTemplate>
      <local:ViewModeTemplateSelector.ThumbnailsTemplate>...</local:ViewModeTemplateSelector.ThumbnailsTemplate>
    </local:ViewModeTemplateSelector>
  </ContentControl.ContentTemplateSelector>
</ContentControl>
```

`ViewModeTemplateSelector : DataTemplateSelector`(3 template property + `SelectTemplate`):
```csharp
public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
    item is ViewMode vm switch {
        ViewMode.Cards => CardsTemplate,
        ViewMode.List => ListTemplate,
        ViewMode.Thumbnails => ThumbnailsTemplate,
        _ => CardsTemplate,
    };
```

### 3.4 Context menu(7 命令)

**XAML 抽到 `Views/LocalModels/LocalModelContextMenu.xaml`**(3 UserControl 复用):

```xml
<ContextMenu x:Key="LocalModelCardContextMenu"
            DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}">
  <MenuItem Header="⭐ 收藏 / 取消收藏"
            Command="{Binding DataContext.ToggleStarCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <MenuItem Header="⧉ 复制 Hash"
            Command="{Binding DataContext.CopyHashCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <MenuItem Header="📂 打开所在文件夹"
            Command="{Binding DataContext.OpenFolderCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <MenuItem Header="🔄 重新计算 Hash"
            Command="{Binding DataContext.ReloadHashCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <MenuItem Header="📥 从 CivitAI 刷新元数据"
            Command="{Binding DataContext.RefreshMetadataCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <Separator />
  <MenuItem Header="📋 复制 CivitAI 页面 URL"
            Command="{Binding DataContext.CopySourceUrlCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}"
            IsEnabled="{Binding MatchedDetail, Converter=NotNullToBool}" />
  <MenuItem Header="🔍 复制文件名"
            Command="{Binding DataContext.CopyFileNameCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
  <Separator />
  <MenuItem Header="🗑 删除(到回收站)"
            Command="{Binding DataContext.DeleteCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
            CommandParameter="{Binding}" />
</ContextMenu>
```

**7 命令实现(`Services/LocalModelOperations.cs` ~200 行)**

| 命令 | 实现 |
|------|------|
| `ToggleStar` | `_stars.IsStarred(path) ? _stars.Remove : _stars.Add`;返回新 LocalModelCard `with { IsStarred = ... }`;VM 收到后更新 `FilteredModels` 对应 item |
| `CopyHash` | `Clipboard.SetText(card.Hash ?? "")`;空 hash → toast「无 Hash 可复制」 |
| `OpenFolder` | `Process.Start("explorer.exe", $"/select,\"{path}\")`;path 不存在 → toast + log |
| `CopySourceUrl` | 仅 `MatchedDetail != null` enabled(XAML 拦);URL = `https://civitai.com/models/{MatchedDetail.Id}` |
| `CopyFileName` | `Path.GetFileName(card.SourcePath)` |
| `ReloadHash` | progress dialog → `await _hasher.ComputeTensorOnlySha256Async(path, ct)` → 写 DB → 关 + toast |
| `RefreshMetadata` | progress dialog → `await _orchestrator.MatchByHashAsync(hash, ct)` → 写 DB → 关 + toast + VM Refresh card |
| `Delete` | `MessageBox.Show` confirm → `FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`(Microsoft.VisualBasic 引用)→ 删 DB entry → VM 移除 + toast |

**Progress dialog 集成**:用 `IProgressDialogService` 接口 — verify 项目现有;若无,spec §3.5 含最小新建。

```csharp
public interface IProgressDialogService
{
    IDisposable ShowIndeterminate(string message);
}
```

VM 端命令 wrapper(`LocalModelsViewModel.cs`):
```csharp
public IRelayCommand<LocalModelCard> ToggleStarCommand { get; }
public IRelayCommand<LocalModelCard> CopyHashCommand { get; }
public IRelayCommand<LocalModelCard> OpenFolderCommand { get; }
public IAsyncRelayCommand<LocalModelCard> ReloadHashCommand { get; }
public IAsyncRelayCommand<LocalModelCard> RefreshMetadataCommand { get; }
public IRelayCommand<LocalModelCard> CopySourceUrlCommand { get; }
public IRelayCommand<LocalModelCard> CopyFileNameCommand { get; }
public IRelayCommand<LocalModelCard> DeleteCommand { get; }
```

**CanExecute 逻辑**:`CopySourceUrl` 走 XAML `IsEnabled="{Binding MatchedDetail, Converter=NotNullToBool}"`,CanExecute 同步。

**测试 seam(避免真调 OS API)**:

```csharp
public sealed class LocalModelOperations
{
    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowConfirmDialogOverride { get; set; }
        = (msg, title, btn, icon) => MessageBox.Show(msg, title, btn, icon);
    public Action<string> OpenInExplorerOverride { get; set; }
        = path => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    public Action<string> SetClipboardTextOverride { get; set; }
        = text => Clipboard.SetText(text);

    private readonly ToastNotification _toast;
    private readonly StarredModelsRepository _stars;
    private readonly ModelHasher _hasher;
    private readonly CivitaiMatcherOrchestrator _orchestrator;
    private readonly IProgressDialogService _progress;
    private readonly Func<SqliteConnection> _modelDbConnectionFactory;
}
```

### 3.5 错误处理

**失败矩阵(7 命令 × 主要 fail path)**

| 命令 | 失败 | 处理 | Toast | 阻塞 list? |
|------|------|------|-------|-----------|
| ⭐ ToggleStar | DB 写失败 | toast「收藏失败」+ log,卡状态不变回滚 | ⚠ | ❌ |
| ⧉ CopyHash | Hash null | toast「无 Hash 可复制」+ log warn | ⚠ | ❌ |
| ⧉ CopyHash | Clipboard COMException | toast「剪贴板不可用」+ log | ❌ Error | ❌ |
| 📂 OpenFolder | path 不存在 | toast「路径不存在」+ log warn | ⚠ | ❌ |
| 📂 OpenFolder | Win32Exception | toast「无法打开文件夹」+ log | ❌ | ❌ |
| 🔄 ReloadHash | IO 失败 | progress 关 + toast「Hash 计算失败:reason」 | ❌ | ❌ |
| 📥 RefreshMetadata | API 失败 | progress 关 + toast「CivitAI 失败:status」 | ❌ | ❌ |
| 📥 RefreshMetadata | 未匹配 | progress 关 + toast「未在 CivitAI 找到匹配」 | ℹ Info | ❌ |
| 🗑 Delete | 用户点 No | noop | — | ❌ |
| 🗑 Delete | 文件被占用 | toast「删除失败:reason」+ DB 不变 | ❌ | ❌ |
| 🗑 Delete | FileNotFoundException | toast「文件已不存在,仅清理数据库」+ DB 删 + VM 移除 | ℹ | ❌ |

**Toast 服务统一入口**:走项目已有 `ToastNotification`(verify 后引用路径;若无 spec 含最小新建 ~30 行)。

**API 形态**:
```csharp
public sealed class ToastNotification
{
    public void Success(string message);   // 绿色 ✓ 3 秒
    public void Warning(string message);   // 黄色 ⚠ 5 秒
    public void Error(string message);     // 红色 ✕ 7 秒
    public void Info(string message);      // 灰 ℹ 3 秒
}
```

**统一前缀**:`LocalModel:` 标识来源 + 简短 action + 主体(e.g. `LocalModel: ⭐ 已收藏`、`LocalModel: Hash 计算失败 - 权限不足`)。

**Log 路径**:`AppLogger.Log<T>(level, message, exception?)`,T47 新增:
- `LocalModelOperations`(Service)
- `StarredModelsRepository`(Data)
- `LocalModelSettingsRepository`(Data)

log 内容必须含 `card.SourcePath`(Windows 中文路径需 UTF-8,AppLogger encoding verify)+ exception type + message(不 stack trace 全 dump,遵循 `feedback_wpf_release_stack_release_unreliable` 教训)+ 上下文 action 名。

debug log 文件走项目已有 `<exe>/xxx_debug.log` 路径。

**不抛异常原则**:`LocalModelOperations` 7 公共方法全部 `try { ... } catch { toast + log }`,**不抛** — 上下文菜单触发,异常冒泡会让 ContextMenu 残留 + DispatcherUnhandledException。**唯一例外**:`ctor` 抛(依赖缺失 = 启动失败)。

**并发安全**:
- `StarredPaths: HashSet<string>` VM 唯一实例,7 命令同步修改(UI 线程)— OK
- model.db 写:每命令走独立 connection(`ModelRepositoryFactory` per-call pattern,T45 已 verify)
- progress dialog 同时只 1 个:`IProgressDialogService` 内部 lazy 创建,复用同一窗体直到关闭(`SemaphoreSlim(1,1)` 串行化)

**DB migration 失败**:启动 `InitModelSchemaIfMissing` 失败 → toast「数据库初始化失败,LocalModels 功能受限」+ log error + VM 走 in-memory fallback(只读 list,Star/ContextMenu 写操作变 noop + toast「当前不可用」)。

### 3.6 测试策略

**测试栈**:xUnit + NSubstitute(项目已有)+ Microsoft.Data.Sqlite in-memory DB。

**测试覆盖矩阵**

| 测试类 | 数量 | 覆盖点 |
|--------|------|--------|
| `ModelSchemaTests`(Data,新) | 2 | 新 DB 2 表存在 / 老 DB 自动建 |
| `StarredModelsRepositoryTests`(新) | 7 | Add/Remove/IsStarred/GetAll + DB migration + 排序 |
| `LocalModelSettingsRepositoryTests`(新) | 5 | Get/Set/缺 key 默认值/UPDATE 路径/空 value |
| `LocalModelOperationsTests`(新) | 17 | 7 命令 happy + 主要 fail path + 测试 seam override |
| `LocalModelsViewModelTests`(扩) | +10 | ViewMode 4 + StarsOnly 2 + Search/Kind/Stars 交集 1 + ToggleStar 2 + 启动持久化 1 |
| `ViewModeTemplateSelectorTests`(新) | 4 | 3 mode → 3 template + unknown fallback |
| **合计** | **~45** | |

**Focused tests filter**:
```bash
dotnet test --filter "FullyQualifiedName~T47|FullyQualifiedName~StarredModels|FullyQualifiedName~LocalModelSettings|FullyQualifiedName~LocalModelOperations|FullyQualifiedName~LocalModelsViewModel|FullyQualifiedName~ViewModeTemplateSelector|FullyQualifiedName~ModelSchema"
```

**不测的部分**:
- XAML binding 路径(运行时检查,dev verify)
- 3 UserControl 视觉布局(dev verify)
- `VirtualizingTilePanel` 真实虚拟化生效(T48 spec 专治)
- `Clipboard.SetText` 实际写 OS 剪贴板(Substitute 拦截)
- `Process.Start` 实际启动(Substitute 拦截)
- `FileSystem.DeleteFile` 回收站实际行为(Substitute 拦截 + dev verify 1 步:真删 1 个 test.txt → 检查回收站)

**Dev verify 11 步**(启动真 app):
1. 切 List 视图 → 重启 app → 仍 List
2. Star 3 个 card → 重启 → 仍 Star
3. 右键 → Copy Hash → 粘贴到 notepad 验证
4. 右键 → Open Folder → Explorer 弹出并定位
5. 右键 → Reload Hash → progress dialog + 完成后 toast
6. 右键 → Refresh Metadata → 拉回 CivitAI 数据
7. 右键 → Copy SourceUrl → 浏览器开 URL 可达
8. 右键 → Copy FileName → 粘贴验证
9. 右键 → Delete → confirm Yes → 文件进回收站 + list 移除
10. ⭐ toggle 过滤:off → 全显,on → 只 3 个 starred
11. 切 Thumbnails → 缩略图墙 + 滚动 100+ 卡(本地塞 50 个 fixture 即可)

### 3.7 任务分解(7 task,3-4 天 ship)

| # | Task | 估时 | 关键产出 | Commit |
|---|------|------|----------|--------|
| 1 | model.db 加 2 表 + SqliteConnectionFactory ATTACH 流程 | 1h | DB schema + 2 测试 | `feat(state): T47 model.db model_stars + model_settings tables` |
| 2 | StarredModelsRepository + LocalModelSettingsRepository + 12 测试 | 2h | 2 repo + 焦点测试 | `feat(state): T47 StarredModelsRepository + LocalModelSettingsRepository` |
| 3 | LocalModelOperations 7 命令 + 17 测试 + DI 注入 | 3h | service + Substitute mock | `feat(svc): T47 LocalModelOperations 7 commands` |
| 4 | ContextMenu XAML + 7 命令 wire VM + IsStarred 派生属性 | 2h | 交互层 + 2 VM 测试 | `feat(view): T47 ContextMenu 7 commands + IsStarred derived property` |
| 5 | 3 视图 UserControl + DataTemplateSelector + LocalPathToBitmapConverter | 4h | UI 切换 + 4 selector 测试 | `feat(view): T47 3-view mode (Cards/List/Thumbnails)` |
| 6 | ViewMode + StarsOnly 持久化 + toolbar segmented button + FilteredModels 3 维交集 | 2h | 状态层 + 6 VM 测试 | `feat(vm): T47 ViewMode + StarsOnly filter with model_settings persistence` |
| 7 | Settings.LocalModelsFilter → model_settings 迁移 + dev verify 11 步 + release | 2h | 迁移 + 1 测试 | `chore(state): T47 migrate Settings.LocalModelsFilter to model_settings` |

**总估时**:~16h(2-3 天),7 commit,7 文件新 + ~10 文件改,~1500 行,~45 测试。

**任务依赖**:
```
T1 → T2 → T3 → T4 → T6 → T7
        ↘ T5 ↗
```

T4 / T5 不共享文件,可并行(SDD 默认串行保险)。

### 3.8 Star 派生属性的循环依赖解决

**问题**:record `LocalModelCard` 是 positional record 不可变,`IsStarred` 是 VM 状态(`StarredPaths.Contains(path)`),不能放 record getter(VM 依赖 record,反向会循环)。

**解法**:VM `LoadFromDb` 后:
```csharp
var cards = await _repo.GetAllAsync();
var starred = _stars.GetAll().ToHashSet(StringComparer.OrdinalIgnoreCase);
Models = cards.Select(c => c.WithIsStarred(starred.Contains(c.SourcePath))).ToList();
```

`LocalModelCard.WithIsStarred(bool)` 是 record `with` 工厂方法(同已有 `WithLocalPathOverride` 模式)。

**Star 切换**:`ToggleStarCommand` 收到旧 card → 调 `LocalModelOperations.ToggleStarAsync` → 收到新 card `with { IsStarred = ... }` → VM 更新 `FilteredModels` 对应 item + `StarredPaths` 增删。

**FilteredModels 重算**:`StarsOnlyFilter` toggle 时,`ApplyFilter()` 复用 T46 防抖逻辑,加 StarsOnly 维度交集。

### 3.9 Global Constraints

**继承自 CLAUDE.md + 项目 memory**:
- 全量输出,无省略号/TODO
- 多层错误处理,无静默失败
- 单一职责,文件 < 500 行
- 现代 C# 12(type hints,nullable,`record` / `with`)
- 命名 `is/has/can` 布尔前缀
- WPF 必须 `DynamicResource`(palette 类)
- WPF HttpClient ctor 期一次性构造
- Dialog `vm.CloseRequested → Close()` wire
- DB schema migration 向后兼容(`CREATE IF NOT EXISTS` / `EnsureColumn`)
- focused tests PASS 才算 task complete
- dev verify(真启动 app + 11 步)后才 push
- **不要 publish**(只用户明示才 publish)
- 不下载/解压/重压 zip,直跑 staging publish

## 4. Verification

### 4.1 编译 + focused tests
```bash
cd D:\ToolDevelop\ComfyUI
dotnet build src-wpf\ComfyUI.Manager\ComfyUI.Manager.csproj -c Debug
dotnet test tests-wpf\ComfyUI.Manager.Tests\ComfyUI.Manager.Tests.csproj \
  --filter "FullyQualifiedName~T47|FullyQualifiedName~StarredModels|FullyQualifiedName~LocalModelSettings|FullyQualifiedName~LocalModelOperations|FullyQualifiedName~LocalModelsViewModel|FullyQualifiedName~ViewModeTemplateSelector|FullyQualifiedName~ModelSchema" \
  --no-build
```
**预期**:0 编译错,**~45 tests PASS**,0 跳过。

### 4.2 老 DB migration verify
启动 WPF 应用时 `CREATE TABLE IF NOT EXISTS` 触发,**0 错**:
- `model_stars` 表新建
- `model_settings` 表新建
- 老 `local_model_files` / `model_tensor_hashes` / `matched_model_details` 数据完整

### 4.3 Settings.LocalModelsFilter 迁移 verify
全新用户:`localmodels.search = ""`,`Settings.LocalModelsFilter = null` → 无迁移。
老 T46 用户:`Settings.LocalModelsFilter = "anime"`,`model_settings.localmodels.search = ""` → 启动后迁移完成,`Settings.LocalModelsFilter` 清空,`model_settings.localmodels.search = "anime"`。

### 4.4 XAML compile verify
启动 WPF 应用 + 打开 LocalModelsView:
- toolbar segmented button(3 选 1)显示正常
- ⭐ checkbox 显示正常
- 切 Cards → List → Thumbnails 无 binding error
- 右键弹 ContextMenu(8 项含 2 separator),CopySourceUrl 在 MatchedDetail=null 时灰显
- ⭐ toggle 立即更新 card 角标 + toolbar 过滤

### 4.5 Dev verify 11 步(详见 §3.6 末尾)

### 4.6 Release build + push
```bash
dotnet build -c Release  # 0 errors
# 不 publish;push 到 fork remote
git push fork main
```

## 5. Risk Assessment

| 风险 | 概率 | 缓解 |
|------|------|------|
| `VirtualizingTilePanel` NuGet 引用缺失 | 中 | spec §3.3 写明降级路径 → `ListBox + VirtualizingPanel.ScrollUnit=Item` |
| Delete 双删(实际文件 + DB)失败回滚复杂 | 中 | 3 个 catch 分支独立处理,不抛跨级异常(§3.4 测试 seam + §3.5 失败矩阵) |
| IsStarred 派生属性循环依赖 | 低 | §3.8 显式解法 — VM 在 LoadFromDb 后 `with` 注入 |
| `Settings.LocalModelsFilter` 迁移漏 | 低 | spec §3.7 Task 7 显式迁移 + 1 测试覆盖 |
| 老 DB 用户 Star PK 大小写冲突 | 极低 | `StringComparer.OrdinalIgnoreCase` HashSet + INSERT OR IGNORE |
| progress dialog 重入(用户连点) | 低 | `SemaphoreSlim(1,1)` 串行化,同窗复用 |
| `Microsoft.VisualBasic` 引用警告 | 低 | 项目已引用 Microsoft.VisualBasic(T33 BulkUpdate precedent);若无,csporj 加 Reference |

## 6. Trade-offs vs SwarmUI

| SwarmUI 做法 | 我们做法 | 差异原因 |
|--------------|----------|----------|
| 9 Display Formats | 3(Cards/List/Thumbnails) | 80% 用户用不到 6 个;3 个够用 |
| Star 持久化 LiteDB + 排序置顶 | SQLite + 仅 filter(默认 Name 升序) | 简化 UX,Star 不抢排序;用户选 yes |
| Context menu 7 命令含 Load/SetRefiner | 简化为本地文件操作 | Load/SetRefiner 需 ComfyUI 进程集成,T47 scope 外 |
| 缩略图 lazy async | 同步 Freeze | T47 简化,T48 改 async |
| LiteDB per-folder `.ldb` | 单 `model.db` 多表 | T45 已 ship 单 db 架构 |

## 7. Open Questions

无。设计已确定,所有歧义点均通过 AskUserQuestion 与用户确认。

## 8. References

- **SwarmUI 调研**:`memory/reference_swarmui_model_query_ux.md`(9 Display Formats + Star + ContextMenu)
- **T46 父 spec**:`docs/superpowers/specs/2026-09-17-t46-local-model-search-and-tensor-hash-design.md`
- **T45 schema 基础**:`docs/superpowers/specs/2026-09-16-t45-split-model-db-design.md`
- **现有 ModelHasher**:`src-wpf/ComfyUI.Manager/Services/Civitai/ModelHasher.cs:14-43`
- **现有 CivitaiMatcherOrchestrator**:`src-wpf/ComfyUI.Manager/Services/Civitai/CivitaiMatcherOrchestrator.cs`(Reload/Refresh 调用)
- **现有 LocalModelsViewModel**:`src-wpf/ComfyUI.Manager/ViewModels/LocalModelsViewModel.cs`(T46 + DisplayTitle + SearchText)
- **现有 DisplayTitle 派生属性模式**:`src-wpf/ComfyUI.Manager/Models/ModelEntry.cs:313`(T46i.2 — non-INPC 模式)
- **现有 WithLocalPathOverride record factory**:`src-wpf/ComfyUI.Manager/Models/ModelEntry.cs`(IsStarred 沿用同模式)
- **T43 Nodelist precedent for DB migration**:T43i.2.1-fix `EnsureColumnDropped` pattern
- **教训引用**:
  - `feedback_wpf_xaml_datacontext_visibility` — ContextMenu PlacementTarget DataContext 绑定
  - `feedback_wpf_release_stack_release_unreliable` — log 用 message 不 stack
  - `feedback_db_single_source_of_truth` — Star 走 SQLite 不走 Settings
  - `feedback_wpf_observablecollection_progress` — UI 线程约束
  - `feedback_configureawait_false_placement` — VM/View await 不 ConfigureAwait(false)
