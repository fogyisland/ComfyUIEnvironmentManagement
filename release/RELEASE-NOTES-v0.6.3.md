## v0.6.3 — Node source list dropdowns (query + download)

**用户痛点:** v0.6.2 Settings 里没有"下载节点列表"配置项,只能用硬编码的
ComfyUI-Manager 自带 catalog。本次新增两个下拉框:用户可自定义添加/删除
**查询节点列表**(catalog fetch URL)和**下载节点列表**(git clone URL),
默认值仍是 `comfyui manager`,首次启动自动填入,向后兼容。

### 新增功能

- **Settings → 查询节点的源** (ComboBox + ItemsControl) —
  配置 catalog 列表的 JSON URL。默认 `comfyui manager`
  (`https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json`)。
  可添加多个源;切换 active 立即生效,下次 Refresh 用新 URL 拉取。

- **Settings → 下载节点的源** (ComboBox + ItemsControl) —
  配置节点 install 时使用的 git clone URL **模板**。默认 `comfyui manager`
  (`https://github.com/comfyanonymous/{node}`)。
  模板里的 `{node}` 占位符在 install 时被节点 id 替换
  (例:`https://github.com/comfyanonymous/ComfyUI-Impact-Pack`)。

- **`CatalogFetcher`** (`5bdea6b`) — 新 HTTP 拉取层,带 60 分钟可配置 TTL cache。
  解析 catalog JSON,提取 `id`/`name`/`repository`/`url` 等字段,
  完整 `raw_metadata` 反序列化为原生 CLR 类型供 UI 绑定。
  WPF 端**首次直连 HTTP**(不再依赖 Python service)。

- **`NodeUrlResolver`** (`4a16e8d`) — 纯函数,模板里 `{node}` → 节点 id。
  用于 install 时把 download 源 URL 模板展开成 git clone URL。

- **`NodeOperations.InstallAsync` 接入 active download 源** (`b66cc38`) —
  catalog 条目缺 `repository` 时,自动回退到当前 active download 源 URL
  模板(展开 `{node}`),无需用户在 Install 时手动输入 repo。
  若未配置 download 源,提示 `未配置下载源,请在 Settings 添加`。

- **`CatalogViewModel.Refresh` 解 stub** (`94b6e80`) — 把原来显示
  `MessageBox` 的占位 `Refresh()` 换成真异步 `RefreshAsync()`,
  通过 `CatalogFetcher` 拉 catalog 后 upsert 到本地 SQLite,
  网络失败时 ErrorMessage + 仍显示本地缓存。

- **`ExtractRepoUrl` 修掉 SourceUrl 误回退** (`80d8385`) —
  catalog 条目缺 `repository`/`url` 时,以前会回退到 query 源 URL
  (一个 JSON 文件 URL),会被原样传给 `git clone`,产生莫名 git 报错。
  修后回退到 null → `InstallAsync` 显示 `catalog 条目缺 repository url`。

### 包含的 commits since v0.6.2

```
e344aad chore(release): bump to v0.6.3
80d8385 fix(wpf): drop SourceUrl fallback in ExtractRepoUrl (resolve review I1)
b66cc38 feat(wpf): NodeOperations consumes active DownloadSource (template substitution)
94b6e80 feat(wpf): CatalogViewModel.Refresh wires to CatalogFetcher (resolves M5.2-T7)
5bdea6b feat(wpf): CatalogFetcher — HTTP GET + parse catalog JSON
5499a64 feat(wpf): SettingsView — query/download source sections
88c6c8d feat(wpf): SettingsViewModel — QuerySources/DownloadSources + commands
4a16e8d feat(wpf): NodeUrlResolver — substitute {node} in download URLs
9af2d5c feat(wpf): add NodeSource model + 4 Settings fields + Defaults
a8b8a6f docs(spec): v0.6.3 hotfix — query/download node source list dropdowns
```

### 升级注意

- 直接覆盖 v0.6.2 即可,settings.json 自动迁移:`query_sources` /
  `download_sources` 空数组会自动填入默认 `comfyui manager` 条目;
  `active_query_source_name` / `active_download_source_name` 空也会填默认。
- 若用户在 v0.6.2 之前手动改过 settings.json 的这些字段,
  v0.6.3 不会覆盖非空值。
- 第一次刷新 Catalog 页面会触发 HTTP 拉取(15s timeout);
  若离线,ErrorMessage 会显示 `拉取失败: <reason>(本地缓存仍可用)`。

### 测试

- WPF: **74/74 PASS**(新增 5 NodeUrlResolverTests + 6 CatalogFetcherTests
  + 4 CatalogViewModel RefreshAsync tests + 2 NodeOperations fallback tests
  + 5 SettingsDefaults new-field tests + 7 SettingsViewModel add/remove tests)
- pytest: 3/3 version consistency 0.6.3 校验通过

### 已知 carry-over

- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test
  仍 fail (non-blocking, M5.2 已删 Python WS server, 这些 test 现在
  无对应代码, 待 M6+ 清理)
- `CatalogViewModel.ErrorMessage` 属性已加但暂无 UI 绑定(T7 之后
  单独的 follow-up);目前错误在 VM 内部 set,UI 层暂时看不到,
  下个 hotfix 再加 XAML 绑定。