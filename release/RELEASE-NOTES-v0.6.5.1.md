## v0.6.5.1 — 基础环境部署 profile 实时拉取 PyTorch 版本

**v0.6.5.1 是 v0.6.5 的 hotfix,把"基础环境"菜单页 5 个内置 profile
的 PyTorch `TorchVersion` 从硬编码 `"2.1.0"`(2023 早期版本)改为
运行时从 pytorch.org 拉取当下 stable;同时 CUDA 变体从 4 个增加到 5 个
(加 cu126,RTX 50 / Blackwell 起步),nightly 从 cu121 升到 cu126。**

---

### 1) Profile `TorchVersion` 从硬编码 → 运行时拉取

**用户痛点:** v0.6.5 把"基础环境"菜单页内置的 5 个 profile 的
`TorchVersion` 全部写死为 `"2.1.0"`。2026 当下 PyTorch stable 已经在
2.13.0,用户启动 GUI 看到的是 3 年前的版本号,直接降低了 v0.6.5 release
的可用价值 — 用户原话:"当前的列表中的 pytorch 比较低,实际获取对应的
版本来进行列出"。

**改动:**

- **新增 `Data/PyTorchVersionFetcher.cs`** (`154bc74` + `ecf220c`) —
  HTTP GET `https://pytorch.org/get-started/locally/`,regex 抽取
  `"latest_stable":"X.Y.Z"` 字段 + 验证 `"nightly":{"cuda.x":...}`
  存在性。失败统一返回 `null`,不抛异常(4 类异常 catch 后回退)。
  `Parse(html)` 是 `internal static`,test 直接喂字符串、不打 HTTP。

  *(`ecf220c` post-merge fix:初次实现假设的 HTML 结构跟真实 pytorch.org
  有出入 — 真实 HTML 是 flat(独立 `latest_stable` 字段 + flat `cuda.x`
  key),不是 spec 想象的 nested;smoke 跑真 pytorch.org 时发现 → 改 regex
  + test fixture)*

- **新增 `Data/PyTorchVersionCache.cs`** (`b6a9bf2` + `38f977f`) —
  1 小时 TTL,缓存文件路径
  `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`。
  `JsonSerializerOptions.PropertyNameCaseInsensitive = true`(跟
  `BaseEnvProfileLoader` 一致,`38f977f`)。corrupt JSON / 写盘失败
  静默回退到重新拉取。

- **`BaseEnvProfileLoader` refactor** (`615036c`) —
  ctor 新增 `HttpClient? http = null` + `string? cacheDir = null`
  可选参数。`LoadAsync` 文件缺失分支改调
  `GetLiveDefaultsAsync(ct)`,内部走 cache → fetcher →
  `GetHardcodedDefaults()` 三段 fallback。`GetDefaults()` 重命名
  为 `GetHardcodedDefaults()`(v0.6.5 字面量保留)。

- **`App.xaml.cs` wiring** (`d06b997`) —
  `appDataDir = %APPDATA%/ComfyUI-Manager`,直接传给
  `BaseEnvProfileLoader(projectRoot, appDataDir, http)`(复用共享
  15s 超时的 `HttpClient`)。`PyTorchVersionFetcher` /
  `PyTorchVersionCache` 实例由 loader 内部创建,不暴露到 ctor。

---

### 2) CUDA 变体从 4 个扩到 5 个,nightly 升 cu126

**用户痛点:** 2025+ RTX 50 / Blackwell 起 CUDA 12.6 为起步版本;
v0.6.5 的 4 stable + 1 nightly(cu121)组合里没有 cu126 选项,装机
列表里也看不到最新稳定 CUDA。

**改动:**

- **6 个内置 profile**(`615036c`):
  1. `pytorch-{stable}-cu118-stable` — PyTorch stable + cu118
  2. `pytorch-{stable}-cu121-stable` — PyTorch stable + cu121
  3. `pytorch-{stable}-cu124-stable` — PyTorch stable + cu124
  4. `pytorch-{stable}-cu126-stable` — PyTorch stable + cu126 ← 新增
  5. `pytorch-nightly-cu126` — PyTorch nightly + cu126 ← 升级 cu121 → cu126
  6. `pytorch-{stable}-cpu` — CPU only

  `{stable}` 段是运行时拉的 `latest_stable` 版本号(2026-07-25
  抓到 `"2.13.0"`)。所有 stable profile 共用同一 `versions.Stable`
  字符串。nightly profile 的 `TorchVersion` 永远是字面量 `"nightly"`
  (HTML 无 `latest_nightly` 字段)。

- **nightly 不含 xformers** —
  `packages = [torch, torchaudio, torchvision]`(stable 含 xformers)。
  xformers nightly wheel 偶尔断更,保守起见不放进 nightly。

---

### 3) 升级注意

- 直接覆盖 v0.6.5 文件即可。
- 首次启动走 pytorch.org 拉取 → 写 cache 到 `%APPDATA%/ComfyUI-Manager/`;
  之后 1 小时内重启不重复请求。1 小时后自动重新拉取。
- 离线 / 拉取失败 → 静默 fallback 到 v0.6.5 5 个硬编码默认值
  (Nightly + CUDA 12.1 + Stable 2.1.0),UI 永不空。
- 用户编辑过的 `<app_dir>/base_env_profiles.json` 仍优先于 live fetch
  (沿用 v0.6.5 行为);用户升级后想恢复 live,删该文件即可。
- cache 文件 = `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`,
  手动删可强制下次启动重新拉取。

---

### 4) Verification(本 release 实际跑出的结果)

- **pytest version consistency:** 3 PASS
- **dotnet test WPF:** 210 PASS + 1 SKIP / 0 FAIL(v0.6.5 baseline = 185)
  - 增量:`PyTorchLiveVersionsTests` 4 +
    `PyTorchVersionFetcherTests` 7(2.5 fix 后所有 fixture 反映真实
    pytorch.org HTML 格式)+
    `PyTorchVersionCacheTests` 7 +
    `BaseEnvProfileLoaderTests` 新增 8 = 26 新
- **smoke against real pytorch.org**(_T5SmokeIntegration_,已删):
  6 profiles returned, `Stable="2.13.0"`, `HasNightlyCu126=true`,
  cache 文件写到 `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`
- **dotnet build Release:** 0 errors / 0 warnings
- **未执行:** manual GUI smoke / release zip 重建 / tag / push /
  GitHub release — 由 controller 在主工作树跑

---

### 5) Commits since v0.6.5(`5de88a0`)

```
d06b997 feat(wpf): wire PyTorchVersionFetcher into App startup
ecf220c fix(wpf): PyTorchVersionFetcher regex for real pytorch.org HTML
615036c refactor(wpf): BaseEnvProfileLoader live defaults + 6 profiles + cache integration
38f977f refactor(wpf): PyTorchVersionCache JsonSerializerOptions case-insensitive
b6a9bf2 feat(wpf): PyTorchVersionCache 1h TTL + file IO
154bc74 feat(wpf): PyTorchVersionFetcher HTTP + regex + null-on-failure
73d774e feat(wpf): PyTorchLiveVersions POCO + JSON round-trip
1b4f837 docs(spec): v0.6.5.1 hotfix — PyTorch live version fetch
```

---

### 已知 carry-over / 未做事项

- **`HasNightlyCu126 = false` 罕见场景** — 当前 regex 验证 nightly
  cu126 存在才返回 non-null;若 pytorch 哪天撤掉 nightly cu126 会
  fallback 到 hardcoded 5 个 + nightly cu121(向后兼容)。
- **nightly 用户想装 cu121** — 当前 nightly profile 只含 cu126;
  若需要 cu121 nightly,可手动编辑 `base_env_profiles.json` 加自定义
  profile(走 v0.6.5 引入的用户文件优先机制)。
- **`BulkUpdateOrchestratorTests.cs:363` xUnit1031 警告** —
  pre-existing,本 release 未处理。
- **LiveGitHubVersionFetchTests 真实联网测试** — 默认 SKIP,
  这是设计意图,需要手动启用。
- **`_T5SmokeIntegration.cs` smoke 文件已删** — 功能已被
  `PyTorchVersionFetcherTests` / `BaseEnvProfileLoaderTests` 的真实
  HTML fixture 覆盖。