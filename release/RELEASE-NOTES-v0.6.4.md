## v0.6.4 — Catalog 分页 + 节点版本侧面板 + 安装按钮跟版本 + 基础环境部署

**v0.6.4 是 hotfix 大集合,4 个独立子功能:**

1. **Catalog:Settings 手动刷新 + 分页 + 磁贴/列表 + cache 拆分**(主菜)
2. **节点详情侧面板 + GitHub `/releases` 版本下拉 + 安装按钮反映选中版本**(follow-up)
3. **基础环境部署按钮** — 给 env 装 torch / torchaudio / torchvision / xformers(BED feature)
4. **node_versions 表 + seed_versions.py** — 持久化 GitHub releases 列表

---

### 1) Catalog 分页 + 视图切换 + cache 拆分

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

---

### 2) 节点版本侧面板 + GitHub 版本下拉 + 安装按钮反映版本

**用户痛点:** v0.6.3 的 catalog 详情只显示 title / author / description,
用户没法选具体 GitHub release 版本,装的是 master 最新 commit,无法 rollback。

**新增功能:**

- **GitHubToken 持久化** (`4ffb085`) — Settings 加 GitHubToken 字段
  (password-style input),触发时附加 `Authorization` header 突破 60 次/h
  匿名 rate limit(已登录用户)。

- **GitHubVersionService** (`4ffb085`) — 批量拉 `/repos/{owner}/{repo}/releases`
  (10 release / node),并行调用。空 response 视作 "无 release" 而非 error。

- **node_versions 表 + VersionInfo model** (`4ffb085`) — `node_versions`
  schema(node_id, tag_name, published_at, is_prerelease, fetched_at),
  替代临时 dict。`CatalogRefreshService.RefreshAsync` 现在持久化完整 release 列表。

- **CatalogEntry.LatestVersion** (`4ffb085`) — DESC by published_at,
  pre-release 排后(用户看 stable 优先)。

- **CatalogView 详情侧面板 + 版本 ComboBox** (`4ffb085`) — 选中节点后
  右侧显示 title / author / description / reference / install_type,
  底部 ComboBox 列所有 tag + 发布日期。

- **安装按钮跟版本** (`f18493b`) — `InstallButtonLabel = "安装 {SelectedVersion}"`
  (默认 "安装 latest_tag");`NodeOperations.InstallAsync(targetTag)` clone
  后 `git checkout <target>`(无 `--`,避免变 pathspec)。

- **seed_versions.py** (`f18493b`) — 已有 catalog cache 的用户可手动
  backfill node_versions(否则只能等下次 catalog refresh)。

---

### 3) 基础环境部署 (Base Environment Deployment)

**用户痛点:** 新装 env 时 torch / torchaudio / torchvision / xformers
得手动跑 `pip install`(还得挑 CUDA 版本 + channel),容易装错版本
(CPU vs CUDA)或忘装 xformers。本次加一个工具栏按钮,
多选 env → 选配置 → 批量安装。

**新增功能:**

- **BaseEnvConfig POCO + BuildPipArgs** (`f0103b5`) —
  字段:`CudaVersion` (cu118/cu121/cu124/cpu) · `TorchChannel` (stable/nightly)
  · `Packages` (默认 `[torch, torchaudio, torchvision, xformers]`)
  · `ExtraArgs` · `CustomPipArgs`(高级,整段覆盖)。
  `BuildPipArgs()` 优先级:non-empty `CustomPipArgs` → split verbatim;
  否则 `install {pkgs} [--pre if nightly] [--index-url https://download.pytorch.org/whl/{cuda} if cu != cpu] {ExtraArgs}`。
  `Clone()` 深拷贝,避免 dialog 编辑污染 Settings。

- **Settings.BaseEnv 字段** (`5fe3d57`) — JSON 持久化,
  老 settings.json 无此字段 → 反序列化兜底 `new()`。

- **BaseEnvInstaller + virtual RunPipAsync** (`88078a0` + `f4f8099`) —
  `GetVenvPythonPath(env)` 优先级:`env.PythonExecutable` 非空 +
  `File.Exists` → `<VenvPath>/Scripts/python.exe`(Windows)/ `bin/python`(其他)。
  `InstallAsync(envIds, config, IProgress<BaseEnvProgress>, ct)` 串行跨 env:
  单 env 失败不中断(G7),Cancel 立即 kill 当前 pip 进程(G8)。
  `protected virtual RunPipAsync` 让测试 override 模拟 pip 输出 / exit code / cancel。

- **BaseEnvDialog** (`4739f53` + `f8e01b9`) — 静态 `Show(envs, settings)`
  返回 `BaseEnvDialogResult?`。左:env 多选 ListBox;右:CUDA 下拉 / channel 下拉 /
  package 列表(CRUD 按钮)/ ExtraArgs textbox / "预览 pip 命令" 按钮。

- **BaseEnvProgressDialog** (`9f5680f` + `bc61a5b`) — 整体 N/M env
  + 当前 env 进度(从 pip stdout 抓 percent)+ 滚动日志 tail + Cancel 按钮。
  `BaseEnvStatus` enum 优先级:`Failed > Cancelled > Succeeded`。

- **EnvList 工具栏按钮** (`102dd1d`) — "+ 新建环境" 旁加 "基础环境部署" 按钮,
  `EnvironmentListViewModel.BaseEnvCommand` 弹 dialog。

- **Settings '基础环境' section** (`3e196d4`) — 简单 form(CUDA / channel /
  packages / ExtraArgs)+ 可折叠 "高级" raw mode(CustomPipArgs textbox)。

**G1-G20 spec 全数实现**,opus 整支 review **APPROVED FOR MERGE**,
0 critical / 0 important / 6 minor(全为非阻塞 polish)。

---

### 包含的 commits since v0.6.3

```
e344aad (v0.6.3 ref)…
1031c97 docs(sdd): close out BED feature — 12 tasks, 168 PASS, opus APPROVED FOR MERGE
3e196d4 feat(wpf): Settings '基础环境' section (CUDA / channel / packages / ExtraArgs + advanced raw)
102dd1d feat(wpf): wire BaseEnvInstaller through MainViewModel → EnvList toolbar
bc61a5b fix(wpf): BaseEnvProgressDialog — separate 整体进度 label from ProgressBar (Grid.Row layout)
9f5680f feat(wpf): BaseEnvProgressDialog XAML + static Show (progress bar + log tail + cancel)
97afea4 feat(wpf): BaseEnvProgressViewModel + 6 unit tests (log tail, status, cancel CTS)
f8e01b9 feat(wpf): BaseEnvDialog XAML + static Show (env multi-select + config form + preview)
4739f53 feat(wpf): BaseEnvDialogViewModel + 10 unit tests (env multi-select + package CRUD + preview)
f4f8099 fix(wpf): BaseEnvInstaller — restore File.Exists check in GetVenvPythonPath (G11)
88078a0 feat(wpf): BaseEnvInstaller + virtual RunPipAsync + 8 unit tests
4b13ccd feat(wpf): BaseEnvProgress records (BaseEnvProgress + BaseEnvInstallResult + PipResult + BaseEnvStatus)
5fe3d57 feat(wpf): Settings.BaseEnv field + JSON round-trip tests (3 tests)
f0103b5 feat(wpf): BaseEnvConfig POCO + BuildPipArgs() + Clone + 7 unit tests
fcb7a4e docs(plan): 基础环境部署 implementation plan
f18493b feat(scripts): seed node_versions from GitHub /releases list
4ffb085 feat(wpf): v0.6.4 follow-up — version side panel + GitHub version dropdown + install button reflects selected version
fd0891c docs(plan): v0.6.4 hotfix — Catalog pagination + view mode + cache split
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
- **新基础环境部署按钮**:首次使用去 Settings → "基础环境" 确认配置
  (默认 cu118 + stable + [torch, torchaudio, torchvision, xformers]),
  然后在 env list 工具栏点 "基础环境部署" 选 envs → 启动。

### 测试

- WPF: **168/169 PASS**(1 skipped = `LiveGitHubVersionFetchTests` 真实联网,默认 skip)
  - catalog 分页 / 视图切换 / cache 拆分: +30 tests
  - 版本侧面板 + GitHub 版本下拉 + 安装按钮跟版本: +15 tests
  - 基础环境部署 (12 task): +35 tests
- pytest: **3/3 version consistency 0.6.4 校验通过**

### 已知 carry-over

- `BulkUpdateOrchestratorTests.cs:363` xUnit1031 警告
  (blocking task) — pre-existing,不在本次范围
- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test
  仍 fail (non-blocking, M5.2 已删 Python WS server, 这些 test 现在
  无对应代码, 待清理)
- `CatalogViewModel.InfoMessage` 属性已加但暂无 UI 绑定(在 VM 内部
  set,下个 hotfix 再加 XAML 绑定)
- BED feature 有 6 个 minor review findings(全为 polish,非阻塞):
  `RunPipAsync` 异常路径 `process` 可能 leak / `LogTail` 200 行 hardcoded /
  ListBox vs CheckedListBox UX / `CustomPipArgs` split 未 trim / `--index-url`
  空格 quote / cancel 按钮 enable UX
