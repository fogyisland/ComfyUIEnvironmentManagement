## v0.6.4 — Catalog: Settings 手动刷新 + 分页 + 磁贴/列表 + cache 拆分

**用户痛点:** v0.6.3 的节点目录页自动展示 10 条 catalog 条目(硬编码),
用户没法改、也没法刷新;也无法切换视图/分页。本次重构让 Settings 成为
唯一刷新入口,目录页支持分页 + 列表/磁贴视图切换 + 空状态。

### 新增功能

- **Settings → "刷新节点目录" 按钮** (`e291b59`) —
  Catalog 页面不再自动加载,改为手动触发。按钮文本随状态切换
  ("刷新节点目录" / "刷新中..."),busy 时 disabled;下方实时显示
  成功状态或错误信息(用 NullToVisibility 自动隐藏空文本)。

- **目录页 → 列表 / 磁贴 切换** (`76573e2` + `5af56d3`) —
  顶部 toggle 按钮可在 List(原 DataGrid)和 Tile(WrapPanel 卡片)间切换,
  用 `CatalogViewTemplateSelector` 在 ContentControl 渲染时挑 template。
  切换立即写 settings.json;重启后保持上次选择。

- **目录页 → 分页** (`4d88a6f`) —
  列表默认 20 条/页(`CatalogPageSize` setting 可改);底栏显示
  `< Prev | 1 / 12 | Next >`。无数据时显示空状态
  "暂无数据,去 Settings 刷新"。

- **CatalogCacheStore 拆分** (`1c546dd`) — v0.6.3 之前 catalog_cache
  跟用户数据混在同一个 `catalog.db` 里。本次把 catalog_cache 拆到
  `<AppBaseDir>/data/catalog-cache.db`,用户数据 db 改名为 `state.db`。
  启动时自动 rename 旧 `catalog.db → state.db`(try/catch 容错)。

- **`CatalogRefreshService`** (`94dea70`) —
  把 v0.6.3 散在 `SettingsViewModel` 和 `CatalogViewModel.Refresh` 的
  拉取逻辑抽出,统一为 `RefreshAsync(CancellationToken)` 返
  `RefreshResult(bool Success, int EntryCount, string? Error)`,共享
  给两个 ViewModel 使用;失败返 Result,不抛。

- **ComfyUI 源模板打包** (`219cbcf`) —
  新脚本 `scripts/fetch_comfyui_template.ps1` 把
  `comfyanonymous/ComfyUI` shallow-clone 到 `<repo>/ComfyUI/`(用
  bundled portable git,depth=1,默认 master)。`build_release.ps1`
  自动调用,把整个 `ComfyUI/` 目录(几十 MB)复制到 zip 里。
  用户首次创建 env 不再需要手动 git clone。

### 包含的 commits since v0.6.3

```
e344aad (v0.6.3 ref)…
219cbcf feat(scripts): bundle ComfyUI source template via auto-fetch on release build
e291b59 feat(wpf): SettingsView — refresh node catalog button + status/error display
76573e2 feat(wpf): CatalogView — view mode toggle + paging + empty state + raw_metadata bindings
5af56d3 feat(wpf): Theme.xaml — CatalogTileTemplate + CatalogTileWrapPanel + converters
78c8c21 feat(wpf): SettingsViewModel — RefreshCatalogCommand + IsBusy/Status/Error
4d88a6f feat(wpf): CatalogViewModel — paging + view mode + refresh via shared service (no auto-refresh)
94dea70 feat(wpf): CatalogRefreshService — shared refresh logic for Settings + Catalog
86dfb6f fix(wpf): T3 leftover — CatalogViewModelTests still used db.Factory for CatalogRepository
194b5e8 refactor(wpf): CatalogRepository consumes CatalogCacheStore (not user db factory)
a659777 fix(wpf): add trailing newline to SqliteConnectionFactory.cs
1c546dd refactor(wpf): split catalog_cache to <AppBaseDir>/data/catalog-cache.db + rename legacy catalog.db → state.db
3b43025 feat(wpf): add CatalogViewMode enum + CatalogPageSize field + Defaults
903d9bb docs(spec): remove auto refresh; Settings is the only refresh entry point
2b504c8 docs(spec): self-review fixes — test count + XAML structure
54d8ad4 docs(spec): v0.6.4 hotfix — Catalog pagination + view mode toggle + cache store split
```

### 升级注意

- 直接覆盖 v0.6.3 即可。
- 首次启动会自动把旧 `catalog.db` 改名为 `state.db`(用户数据 db);
  catalog_cache 改存到 `<AppBaseDir>/data/catalog-cache.db`。
- Catalog 页面首次打开是**空的**(v0.6.3 的 10 条硬编码入口已移除)。
  去 Settings → "刷新节点目录" 触发第一次拉取(15s timeout)。
- bundled zip 里多了 `ComfyUI/` 目录(原 ComfyUI 源码 shallow-clone)。
  离线用户的 env 第一次跑需要联网一次让 build_release fetcher 拉源。

### 测试

- WPF: **93/93 PASS**(新增 5 SettingsDefaults + 3 CatalogCacheStore
  + 2 SqliteConnectionFactory + 4 CatalogRefreshService + 10 CatalogViewModel
  + 2 SettingsViewModel Refresh tests)
- pytest: **3/3 version consistency 0.6.4 校验通过**

### 已知 carry-over

- `BulkUpdateOrchestratorTests.cs:363` xUnit1031 警告
  (blocking task) — pre-existing,不在本次范围
- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test
  仍 fail (non-blocking, M5.2 已删 Python WS server, 这些 test 现在
  无对应代码, 待清理)
- `CatalogViewModel.InfoMessage` 属性已加但暂无 UI 绑定(在 VM 内部
  set,下个 hotfix 再加 XAML 绑定)
