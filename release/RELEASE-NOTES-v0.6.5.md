## v0.6.5 — 基础环境部署重设计 + Settings 瘦身

**v0.6.5 是 v0.6.4 基础环境部署(BED)功能的二次重构,把"对话框 + 自由表单"
模式换成"profile 驱动 + 顶级菜单页"模式;同时清理 Settings 中已过时的 BED
section 和 Catalog 刷新按钮。**

---

### 1) Profile-driven BED 重设计

**用户痛点:** v0.6.4 的 BED 把 CUDA 版本 / channel / packages / ExtraArgs
全暴露给用户填,实测 80% 用户只想选 "PyTorch stable + CUDA 121" 这种
预设组合,但 UI 没有可发现入口;另一方面 power user 又被局限在 4 个内置
Cuda 字符串,无法加 nightly + cu124 之类的组合。

**改动:**

- **`BaseEnvProfile` POCO 替代 `BaseEnvConfig`** (`2f646dd`) —
  字段:`Id` · `Name` · `Description` · `CudaVersion` · `TorchChannel`
  · `Packages`(默认 `[torch, torchaudio, torchvision, xformers]`)·
  `ExtraArgs`。`BuildPipArgs()` 复用 v0.6.4 的优先级逻辑
  (CustomPipArgs → install pkgs [--pre] [--index-url] {ExtraArgs})。
  `Clone()` 深拷贝避免 dialog 编辑污染。

- **`base_env_profiles.json` bundled asset** (`fc915ce`) —
  `<app_dir>/base_env_profiles.json` 与 exe 同目录发布(csproj `<None Include>`
  + `PreserveNewest`),用户可手动编辑覆盖。csproj 显式声明避免 XAML
  build 把它当 Page 处理。

- **`BaseEnvProfileLoader` + fallback** (`95d1dae`) —
  `LoadProfiles()` 尝试读 bundled JSON;malformed / 文件缺失 → fallback
  到 5 个内置 profile(见下)。抛异常时同样 fallback,日志 warn。

  **5 个内置默认值:**
  1. `pytorch-cu118` — PyTorch 2.1.0 stable + cu118
  2. `pytorch-cu121` — PyTorch 2.1.0 stable + cu121
  3. `pytorch-cu124` — PyTorch 2.1.0 stable + cu124
  4. `pytorch-nightly-cu121` — PyTorch nightly + cu121
  5. `pytorch-cpu` — CPU only

- **`BaseEnvInstaller.InstallAsync(envIds, profiles, IProgress, ct)`** (`16162dc`) —
  接受 `IReadOnlyList<BaseEnvProfile>`,沿用 v0.6.4 的 virtual
  `RunPipAsync` + 串行跨 env + 单 env 失败不中断 + Cancel 立即 kill。

- **`BaseEnvProgressVM` / Dialog 接受 profile** (`025c1eb`) —
  `BaseEnvProgressViewModel.Start(profiles, envIds)`;状态文本与日志
  tail 行为不变,`BaseEnvStatus` 优先级仍是 `Failed > Cancelled > Succeeded`。

---

### 2) 顶级「基础环境」菜单页

**用户痛点:** v0.6.4 的 BED 入口藏在 EnvList 工具栏按钮 +
Settings "基础环境" section,层级深且混在两个完全不同的设置上下文里;
用户经常找不到。

**改动:**

- **侧栏新菜单项** (`9567df9`) — 侧栏顺序现在是 `节点目录 | 基础环境 | 环境 | 设置`,
  `基础环境` 居中,图标用 `Infrastructure` 资源键(若未定义则 fallback
  `Cog`)。

- **`BaseEnvView` XAML + code-behind** (`2cf7834` + `0304bea`) —
  内嵌展示:左侧 profile 多选 CheckedListBox(替代 v0.6.4 的 dialog
  内左栏),右侧 env 多选 ListBox,底部 "开始部署" 按钮 + 实时状态
  textblock(progress VM 直接 bind)。点击按钮 → 复用
  `BaseEnvProgressDialog.ShowAsync`。

- **`BaseEnvViewModel` + `StartCommand`** (`0304bea`) —
  `Profiles`(ObservableCollection) / `Environments` /
  `SelectedProfiles` / `SelectedEnvironments` / `IsBusy` /
  `StatusMessage`。`StartCommand.CanExecute = profiles.Count > 0
  && environments.Count > 0 && !IsBusy`;执行时若选多个 profile
  按 v0.6.5 行为**只启动第一个 selected profile**(注释明确标记为
  "known v0.6.5 behavior, multi-profile batch install 在下个 hotfix 跟进")。

- **EnvList 工具栏快捷入口保留** (`84520d9`) —
  `EnvironmentListViewModel.OpenBaseEnvProgress` 跳过 profile 选择
  对话框,直接用 `profiles[0]`(默认 `pytorch-cu121`)启动 — power user
  路径,点击即跑。

---

### 3) 移除过时配置 UI

**用户痛点:** v0.6.4 留下的 `BaseEnvDialog` / `BaseEnvConfig` /
`Settings.BaseEnv` 已无任何 caller;留着只是 dead code + 用户认知负担。

**改动:**

- **删除 `BaseEnvDialog` + ViewModel + 测试** (`ddeb84c` +
  `4739f53` 反向) — `Views/BaseEnvDialog.xaml(.cs)` +
  `ViewModels/BaseEnvDialogViewModel.cs` + 10 个相关测试全部删除。
- **删除 `BaseEnvConfig` POCO + `Settings.BaseEnv` section** (`fadccba`) —
  `Models/BaseEnvConfig.cs` + `Models/Settings.cs` 中 `BaseEnv` 属性 +
  `SettingsView.xaml` 中 "基础环境" section + `SettingsViewModel`
  对应字段。`SettingsViewModelTests` 中 3 个相关 assertion 也同步删除。
- **旧 settings JSON 兼容** — `Settings.cs` 用 System.Text.Json
  反序列化时未识别的 `base_env` object 默认忽略(`JsonSerializerOptions`
  默认 `IgnoreReadOnlyProperties` + 未知字段不抛),不会阻塞启动。

---

### 4) Catalog 刷新 UX 清理

**用户痛点:** v0.6.4 在 Settings 放了一个 "刷新节点目录" 按钮,
但其实 Catalog 页面也能触发刷新(progress/cancel/streaming),Settings
那个按钮就是冗余入口;按钮移除后,用户在该页面没有 "现在能刷新"
的视觉提示。

**改动:**

- **删除 Settings 侧 "刷新节点目录" 按钮 + VM 命令** (`fb0ab87`) —
  `SettingsView.xaml` 中的 RefreshButton + `SettingsViewModel`
  中的 `RefreshCatalogCommand` / `IsBusy` / `Status` / `Error`
  + 对应 UI 绑定全部移除。`CatalogRefreshService` 仍保留,只
  Catalog 页面用。
- **Catalog 页面空状态文案** (`428209a`) — `CatalogView.xaml` 中
  "暂无数据,去 Settings 刷新" 改为 **"暂无数据,点右上角 刷新"**
  (实际刷新按钮在 Catalog 页右上角 toolbar)。

---

### 5) 升级注意

- 直接覆盖 v0.6.4 文件即可。
- `base_env_profiles.json` 首次启动从 bundled 资源拷贝到 `<app_dir>`,
  用户可手动编辑覆盖(必须是合法 JSON;malformed → fallback 5 默认)。
- 旧 v0.6.4 settings.json 中 `base_env` object 字段会被忽略,
  不会弹错或阻塞启动。
- v0.6.5 不再支持 CustomPipArgs(随 `BaseEnvConfig` 一起删);需要
  自定义 pip 参数的 power user 直接编辑 `base_env_profiles.json`
  加 ExtraArgs。

---

### 6) Verification(本 task 实际跑出的结果)

- **pytest version consistency:** 3 PASS
- **dotnet test WPF:** 185 PASS + 1 SKIP / 0 FAIL
  - BaseEnvProfile POCO + BuildPipArgs + Clone: 11 tests
  - BaseEnvProfileLoader + fallback: 12 tests
  - BaseEnvInstaller(refactored): 8 tests
  - BaseEnvProgressViewModel(refactored): 6 tests
  - BaseEnvViewModel: 15 tests
  - 其余 134 tests(v0.6.4 carry-over + 已有 catalog/version/test infra)
- **dotnet build Release:** 0 errors;warning 数见下方 task report
- **未在本 worktree 执行:** manual GUI smoke / release zip 重建 /
  tag / push / GitHub release — 这些由 controller 在主工作树跑

---

### 7) Commits since v0.6.4 feature base(`2f646dd` ~ `428209a`)

```
428209a docs(wpf): update CatalogView empty-state hint to point at in-page refresh (G9)
fb0ab87 refactor(wpf): remove Settings '刷新节点目录' button (catalog page has full button) (G8)
84520d9 refactor(wpf): EnvironmentListViewModel OpenBaseEnvProgress skips profile selection
fadccba refactor(wpf): remove BaseEnvConfig + Settings.BaseEnv section (G7)
ddeb84c refactor(wpf): remove BaseEnvDialog (replaced by inline BaseEnvView)
9567df9 feat(wpf): 基础环境 top-level menu + ShowBaseEnvCommand wiring
2cf7834 feat(wpf): BaseEnvView XAML + code-behind
0304bea feat(wpf): BaseEnvViewModel + start command + 15 tests
025c1eb refactor(wpf): BaseEnvProgressVM/Dialog accept BaseEnvProfile
16162dc refactor(wpf): BaseEnvInstaller.InstallAsync takes BaseEnvProfile
fc915ce feat(wpf): bundle base_env_profiles.json + csproj copy rule
95d1dae feat(wpf): BaseEnvProfileLoader + fallback defaults + 12 tests
2f646dd feat(wpf): BaseEnvProfile POCO + BuildPipArgs + Clone + 11 tests
```

(`9341bab` T13 implementation report commit 不列在用户可见 release notes。)

---

### 已知 carry-over / 未做事项

- **BaseEnvView 多 profile 选择 → 当前只启动第一个**(`0304bea` 注释
  明确 "v0.6.5 behavior")。multi-profile batch install 等下个 hotfix。
- **`CatalogViewModel.InfoMessage`** 字段已加但 XAML 尚未绑定,
  v0.6.5 没碰(下个 hotfix)。
- **`BulkUpdateOrchestratorTests.cs:363` xUnit1031 警告** — pre-existing,
  本次 release 未处理。
- **4 个 M4 `_on_push_sync` silent-drop WS integration test 仍 fail**
  — pre-existing,M5.2 已删 Python WS server,这些 test 无对应代码,
  待清理(non-blocking)。
- **LiveGitHubVersionFetchTests** 真实联网测试默认 SKIP,这是设计意图
  (CI 默认无外网,需要手动 `dotnet test --filter` 启用)。