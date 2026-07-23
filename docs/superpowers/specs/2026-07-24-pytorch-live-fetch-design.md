# v0.6.5.1 Hotfix — 基础环境部署 profile 实时拉取 PyTorch 版本

**里程碑:** v0.6.5.1 hotfix(v0.6.5 已发布,用户测时发现 BED 默认 profile 的 PyTorch 版本硬编码过时)
**日期:** 2026-07-24
**状态:** 待用户审阅
**Base SHA:** v0.6.5 release `8340262`(HEAD = `5de88a0` 含方向性 ledger)

---

## 0. 摘要

v0.6.5 发布的 `BaseEnvProfileLoader.GetDefaults()` 硬编码 5 个 profile,`TorchVersion` 全部写死 `"2.1.0"`(nightly 是字面量 `"nightly"`),与 2026 实际 PyTorch stable(2.5+/2.6+/2.7+ 乃至 2.13)严重脱节。release 后用户启动 GUI 看到 profile 列表立刻指出"当前的列表中的 pytorch 比较低,实际获取对应的版本来进行列出"。

### 关键决策

| 决策 | 选择 | 原因 |
|---|---|---|
| 数据源 | **PyTorch 官网 HTML** `https://pytorch.org/get-started/locally/` | 用户主动选,覆盖更广(torch + torchvision + torchaudio 一站齐);HTML 内嵌 `pt_published_versions` / `pt_version_map` JavaScript 字面量,正则即可抽取 |
| CUDA 变体 | **5 + 加 cu126**(共 5 stable + 1 nightly,共 6 个) | 用户主动选;cu126 = CUDA 12.6,RTX 50 / Blackwell 起步版本,2025+ 趋势 |
| 默认 profile 数量 | 6 个(`cu118 / cu121 / cu124 / cu126 / cpu + nightly cu126`) | 5 个 stable(原 4 + cu126) + 1 nightly |
| Stable 版本号 | 运行时拉 `latest_stable`(示例:2.13.0),所有 stable profile 共用 | PyTorch stable 不区分 CUDA 变体,所有 wheel 共享主版本号 |
| Nightly 版本号 | 字面量 `"nightly"` | PyTorch 官网 HTML 未发布 `latest_nightly` 字段;nightly 只有日期戳(如 `dev20250723`)对 WPF 用户不友好,沿用字面量 |
| Nightly CUDA | 改 cu126(原 cu121) | HTML `cuda.x` = cu126 是当前 nightly 唯一活索引(cu130/cu132 HTML 有但 PyTorch wheel index 还没发布) |
| 缓存策略 | 1 小时 TTL,`%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json` | 启动只打一次 pytorch.org,后续冷启动直接读 cache |
| 失败回退 | **离线 / 解析失败 → 返回 v0.6.5 硬编码默认值** | 用户"宁可回退也不要空"原则;UI 永不空 |
| HTTP 客户端 | 复用 `App.xaml.cs:57` 的共享 `HttpClient`(15s 超时) | 已有现成实例,不新建 |
| 范围 | 仅替换 `BaseEnvProfileLoader.GetDefaults()` 的 5 个默认 + 加 cu126;不动 `LoadAsync` 已有逻辑 | 范围最小,既有用户编辑的 `base_env_profiles.json` 不受影响 |
| 版本号决策 | 待用户决定 v0.6.5.1 vs v0.6.6(hotfix 补丁 → 0.6.5.1,新 milestone → 0.6.6) | 见 §6 |

### 不动的东西

- 现有 `BaseEnvProfile` POCO 字段(`Id / Name / Description / TorchVersion / CudaVersion / Channel / Packages / ExtraArgs`)
- 现有 `BaseEnvProfileLoader.LoadAsync` 文件读取 + JSON 解析 + 失败回退路径
- 现有 `BaseEnvInstaller` / `BaseEnvViewModel` / `BaseEnvView` / `BaseEnvProgressDialog`(仍按 BaseEnvProfile 工作,签名不变)
- 现有 `Settings.GitHubToken` / GitHub proxy / Catalog fetch 等 HTTP 路径

---

## 1. 目标 & 非目标

### 1.1 目标(本次完成时)

- `BaseEnvProfileLoader` 默认 profile 列表的 `TorchVersion` 字段从硬编码 `"2.1.0"` 改为运行时拉取的实际 stable 版本
- 默认 profile 数量从 5 增到 6(加 `cu126`)
- 默认 nightly profile 改用 cu126(原 cu121)
- 拉取流程失败(网络断 / 解析错 / HTML 格式变动)时静默回退到 v0.6.5 硬编码默认值
- 1 小时 cache 命中时跳过网络请求
- 启动时拉取是后台异步,不阻塞 WPF 主窗口显示(profile 列表"loading..."占位由 `BaseEnvViewModel` 处理,等 Task 完成再 refresh 列表)
- 全部 6 个 profile 共用同一个 stable 版本号(避免拉多次)
- `PyTorchVersionFetcher` 是 `protected virtual` / `internal` 可注入的,test 子类用 fake `HttpMessageHandler` 喂 HTML 字符串(不真打 pytorch.org)
- cache 文件路径 = `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`(无 cache 文件视为首次,直接拉)
- cache 文件 corrupt / 过期 → 忽略,重新拉

### 1.2 非目标(本次不做)

- 不解析 HTML 表内每个 cell 的 wheel 路径(只取 `latest_stable` + `cuda.x` 标识)
- 不实现"PyPI 镜像源"(只打 pytorch.org)
- 不实现"用户自定义 mirror URL"(`Settings.PyTorchMirror` 字段留后续)
- 不解析 nightly 实际日期戳(用字面量 `"nightly"` 即可,WPF 用户不需要逐日 nightly)
- 不为 cu118 / cu121 / cu124 / cu126 各自维护独立 stable 版本(都用同一 latest_stable)
- 不做"per-env 不同 profile"(仍是全局共享 loader)
- 不重新设计 cache invalidation UI(用户改 `pytorch_versions_cache.json` 文件路径在 explorer;下次启动 1h 内会过期)
- 不引入第三方 HTML 解析器(纯 regex on `var pt_published_versions = {...}` / `var pt_version_map = {...}` JavaScript literal)

---

## 2. 文件改动表

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Data/PyTorchVersionFetcher.cs` | ~120 | HTTP GET pytorch.org,regex 抽取 `pt_published_versions.latest_stable` + `pt_version_map.nightly.cuda.x` 标记 → `PyTorchLiveVersions { Stable, HasNightlyCu126, FetchedAt }` |
| `src-wpf/ComfyUI.Manager/Data/PyTorchVersionCache.cs` | ~80 | 读/写 `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`,1h TTL,corrupt → 返回 null |
| `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionFetcherTests.cs` | ~150 | `HttpMessageHandler` fake 喂 HTML 字符串;断言:正常解析 / 字段缺失 / 字段损坏 / 404 / timeout |
| `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCacheTests.cs` | ~80 | 写读 round-trip / TTL 内命中 / TTL 外失效 / corrupt JSON |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Data/BaseEnvProfileLoader.cs` | ctor 增 2 参(`HttpClient http`, `string? appDataCacheDir = null`)。`GetDefaults()` → `GetLiveDefaultsAsync(CancellationToken ct = default)`,调用 fetcher + cache 后再生成 6 个 profile。`LoadAsync` 保持不变:文件存在 → 走文件;文件缺失 → 改调 `GetLiveDefaultsAsync`。**新增** `GetHardcodedDefaults()` 方法保留 v0.6.5 字面量(供 fetcher 失败时回退) |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | L70 改:`new BaseEnvProfileLoader(projectRoot, appDataDir, http)`,实例化 `PyTorchVersionCache(appDataDir)` + `PyTorchVersionFetcher(http)` |
| `tests-wpf/ComfyUI.Manager.Tests/Data/BaseEnvProfileLoaderTests.cs` | `GetDefaults_*_HasExpectedFields` 测试改名为 `GetHardcodedDefaults_*_HasExpectedFields`,断言保持 5 个 + 字面量 `"2.1.0"`(这是 fallback 路径,值得测)。**新增** `GetLiveDefaultsAsync_*_UsesFetchedVersion` 系列测试:fake fetcher 返回 `2.13.0` → 默认 profile 的 `TorchVersion` 应为 `"2.13.0"`,且 profile 数量 = 6(加 cu126) |

### Delete

无。

### Keep (unchanged)

- `BaseEnvProfile` POCO(签名不动)
- `BaseEnvInstaller`(依赖 `BaseEnvProfile.BuildPipArgs`,字段名不变,无影响)
- `BaseEnvViewModel` / `BaseEnvView` / `BaseEnvProgressDialog` / `BaseEnvProgressViewModel`(loader 返回的 profile 数量从 5 → 6,UI 自动展示更多行)
- `base_env_profiles.json` bundled asset(用户编辑路径不受影响;若文件存在 → 走 `LoadAsync` 路径,不走 `GetLiveDefaultsAsync`)
- `SettingsDefaults`(无需新字段)

---

## 3. 架构

### 3.1 数据流(冷启动 / cache miss 路径)

```
BaseEnvViewModel.ctor
  ↓
BaseEnvProfileLoader.LoadAsync(ct)
  ↓
  if (file exists) parse JSON → return (既有路径)
  ↓
  else → GetLiveDefaultsAsync(ct)
       ↓
       PyTorchVersionCache.TryReadAsync(ct) → 命中且 < 1h → return cached
       ↓
       PyTorchVersionFetcher.FetchAsync(ct)
         ↓
         http.GetAsync("https://pytorch.org/get-started/locally/")
         ↓
         regex extract: latest_stable, cuda.x in pt_version_map
         ↓
         return PyTorchLiveVersions { Stable, FetchedAt }
       ↓
       PyTorchVersionCache.WriteAsync(versions)
       ↓
       BuildLiveDefaults(versions) → 6 BaseEnvProfile 实例
```

### 3.2 失败回退语义

```
GetLiveDefaultsAsync(ct):
  try:
    cached = cache.TryRead()
    if cached != null: return BuildLiveDefaults(cached)
    fresh = await fetcher.FetchAsync(ct)        ← HttpRequestException / TaskCanceledException / FormatException → catch
    if fresh == null: return GetHardcodedDefaults()   ← fetcher 内部已 catch,这里只判 null
    cache.Write(fresh)
    return BuildLiveDefaults(fresh)
  catch (Exception):
    return GetHardcodedDefaults()               ← 兜底,UI 永不空
```

### 3.3 HTTP 客户端复用

`App.xaml.cs:57` 的共享 `HttpClient { Timeout = TimeSpan.FromSeconds(15) }` 已存在,直接传引用。

### 3.4 cache 文件路径

`Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData), "ComfyUI-Manager", "pytorch_versions_cache.json")`

`BaseEnvProfileLoader` ctor 第 3 参接受 `appDataCacheDir`(由 `App.xaml.cs` 传入 `%APPDATA%/ComfyUI-Manager`)。默认 `null` → 用 `Environment.GetFolderPath(ApplicationData)` 兜底(便于测试)。

---

## 4. 接口

### 4.1 `PyTorchLiveVersions`(POCO,放 `Data/`)

```csharp
public sealed class PyTorchLiveVersions
{
    public string Stable { get; init; } = "";          // "2.13.0"
    public bool HasNightlyCu126 { get; init; } = true; // 总是 true(HTTP 200 时 cuda.x 必有)
    public DateTimeOffset FetchedAt { get; init; }     // UTC
}
```

JSON 序列化:`System.Text.Json` 默认 camelCase + DateTimeOffset ISO-8601。

### 4.2 `PyTorchVersionFetcher`

```csharp
public sealed class PyTorchVersionFetcher
{
    public const string PageUrl = "https://pytorch.org/get-started/locally/";

    private readonly HttpClient _http;

    public PyTorchVersionFetcher(HttpClient http) { _http = http; }

    /// <summary>
    /// 拉取 pytorch.org 主页 → 提取 Stable 版本号 + 验证 nightly cu126 存在。
    /// 任何失败(HTTP 错 / 超时 / 解析失败) → 返回 null,不抛。
    /// </summary>
    public async Task<PyTorchLiveVersions?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(PageUrl, ct).ConfigureAwait(false);
            return Parse(html);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or InvalidOperationException)
        {
            return null;
        }
    }

    internal static PyTorchLiveVersions? Parse(string html)
    {
        // regex 抽取 var pt_published_versions = {...} 内 latest_stable
        // regex 抽取 var pt_version_map = {...} 内 nightly.cuda.x 存在
        // 任一失败 → null
    }
}
```

### 4.3 `PyTorchVersionCache`

```csharp
public sealed class PyTorchVersionCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    public const string FileName = "pytorch_versions_cache.json";

    private readonly string _dir;

    public PyTorchVersionCache(string appDataDir) { _dir = appDataDir; }

    public string FilePath => Path.Combine(_dir, FileName);

    public async Task<PyTorchLiveVersions?> TryReadAsync(CancellationToken ct = default)
    {
        // 1. 文件不存在 → null
        // 2. 读 JSON → 失败(corrupt) → null
        // 3. FetchedAt 距今 > 1h → null
        // 4. 返回 cached
    }

    public async Task WriteAsync(PyTorchLiveVersions versions, CancellationToken ct = default)
    {
        // Directory.CreateDirectory(dir)
        // JsonSerializer.Serialize + WriteAllText
        // 不抛(写失败时静默,下次启动再试)
    }
}
```

### 4.4 `BaseEnvProfileLoader` 改动

```csharp
public sealed class BaseEnvProfileLoader
{
    public const string FileName = "base_env_profiles.json";

    private readonly string _appDataDir;
    private readonly string? _appDataCacheDir;     // ← 新增(可为 null → 走 Environment.GetFolderPath)
    private readonly HttpClient? _http;            // ← 新增(可为 null → 不拉,只走 hardcoded)

    public BaseEnvProfileLoader(
        string appDataDir,
        string? appDataCacheDir = null,
        HttpClient? http = null)
    {
        _appDataDir = appDataDir;
        _appDataCacheDir = appDataCacheDir;
        _http = http;
    }

    public async Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_appDataDir, FileName);
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<BaseEnvProfile>>(json, JsonOptions);
                    if (parsed != null) return parsed;     // 用户文件优先,即使空数组
                }
                catch (JsonException) { /* fall through to live defaults */ }
            }
        }
        return await GetLiveDefaultsAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BaseEnvProfile>> GetLiveDefaultsAsync(CancellationToken ct = default)
    {
        if (_http == null) return GetHardcodedDefaults();   // 无 HTTP → fallback

        var cacheDir = _appDataCacheDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cache = new PyTorchVersionCache(Path.Combine(cacheDir, "ComfyUI-Manager"));

        var cached = await cache.TryReadAsync(ct).ConfigureAwait(false);
        if (cached != null) return BuildLiveDefaults(cached);

        var fetcher = new PyTorchVersionFetcher(_http);
        var fresh = await fetcher.FetchAsync(ct).ConfigureAwait(false);
        if (fresh == null) return GetHardcodedDefaults();

        await cache.WriteAsync(fresh, ct).ConfigureAwait(false);
        return BuildLiveDefaults(fresh);
    }

    public IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults() { /* v0.6.5 5 个 + 加 cu126 stable */ }

    private static IReadOnlyList<BaseEnvProfile> BuildLiveDefaults(PyTorchLiveVersions v) { /* 6 个 */ }
}
```

### 4.5 `BuildLiveDefaults(versions)` 输出 6 个 profile

| # | Id | Name | TorchVersion | CudaVersion | Channel | Packages |
|---|---|---|---|---|---|---|
| 1 | `pytorch-{stable}-cu118-stable` | `PyTorch {stable} + CUDA 11.8 (stable)` | `versions.Stable` (e.g. `2.13.0`) | `cu118` | `stable` | `[torch, torchaudio, torchvision, xformers]` |
| 2 | `pytorch-{stable}-cu121-stable` | `PyTorch {stable} + CUDA 12.1 (stable)` | `versions.Stable` | `cu121` | `stable` | 同上 |
| 3 | `pytorch-{stable}-cu124-stable` | `PyTorch {stable} + CUDA 12.4 (stable)` | `versions.Stable` | `cu124` | `stable` | 同上 |
| 4 | `pytorch-{stable}-cu126-stable` | `PyTorch {stable} + CUDA 12.6 (stable)` | `versions.Stable` | `cu126` | `stable` | 同上 |
| 5 | `pytorch-nightly-cu126` | `PyTorch Nightly + CUDA 12.6` | `nightly` | `cu126` | `nightly` | `[torch, torchaudio, torchvision]` |
| 6 | `pytorch-{stable}-cpu` | `PyTorch {stable} (CPU only)` | `versions.Stable` | `cpu` | `stable` | `[torch, torchaudio, torchvision]` |

Id / Name 用 `$"pytorch-{v.Stable}-cu118-stable"` 模板(避免硬编码 `"2.1.0"`);**所有 stable profile 共用 `v.Stable`**。

---

## 5. HTML 解析规则

### 5.1 `pt_published_versions` 字面量

```js
var pt_published_versions = {
  "stable,pip,linux,accnone,python": "pip3 install torch==2.13.0 torchvision==0.22.0 torchaudio==2.6.0 --index-url https://download.pytorch.org/whl/cpu",
  "stable,pip,linux,cuda.x,python": "pip3 install torch==2.13.0 ... --index-url https://download.pytorch.org/whl/cu126",
  ...
};
```

抽取 regex:
```regex
var pt_published_versions = \{[^{}]*?"stable,pip,linux,cuda\.x,python":\s*"[^"]*?torch==(\d+\.\d+\.\d+)
```

**Group 1** = `latest_stable`(示例:`2.13.0`)。

### 5.2 `pt_version_map` 字面量

```js
var pt_version_map = {
  ...
  "nightly": {
    "cpu": ["cpu"],
    "cuda": {
      "x": ["12.6", ...],
      "y": ["13.0", ...],
      "z": ["13.2", ...]
    }
  }
};
```

只验证 `nightly.cuda.x` 存在即可(确认 cu126 nightly 索引可装)。

抽取 regex:
```regex
"nightly":\s*\{\s*"cpu":[^{}]*?"cuda":\s*\{\s*"x":
```

**Group 命中** = `HasNightlyCu126 = true`。

### 5.3 解析失败场景

- regex 不匹配 → return null
- `latest_stable` 是空字符串 / 非 `x.y.z` 格式 → return null
- `cuda.x` 不存在 → return null(理论上 PyTorch 不会撤掉 cu126 nightly)

---

## 6. 版本号决策(待用户)

`v0.6.5.1` vs `v0.6.6` 二选一:

| 选项 | 含义 | 用户之前选过的类似情况 |
|---|---|---|
| **v0.6.5.1** | 紧跟 v0.6.5 的 hotfix 补丁,语义化版本 4 位 | 之前用过 v0.6.5.1 这个占位 |
| **v0.6.6** | 新的 minor 升版 | 用户在 v0.6.0 vs v0.6.1 时选过 "拆新 version" |

**默认推荐 v0.6.5.1**:
- 是对 v0.6.5 release bug 的即时修补(用户看到的就是硬编码版本过时)
- 不引入新 feature,纯修补
- rebuild zip 不影响后续 hotfix chain

> 这条决策**不在本 spec 范围内**,plan 写完后由用户通过 ExitPlanMode / AskUserQuestion 决定。

---

## 7. 测试策略

### 7.1 `PyTorchVersionFetcherTests`

- ✅ `Parse_ExtractsStableFromPytorchOrgHtml` — 喂固定 HTML,断言 `Stable == "2.13.0"`
- ✅ `Parse_ReturnsNullWhenLatestStableMissing` — HTML 内无 `"stable,pip,linux,cuda.x,python"`
- ✅ `Parse_ReturnsNullWhenNightlyCudaXMissing` — 验证字段缺失
- ✅ `Parse_ReturnsNullOnCorruptHtml` — 喂 `<html></html>`
- ✅ `FetchAsync_ReturnsNullOnHttp404` — fake handler 返回 404
- ✅ `FetchAsync_ReturnsNullOnTimeout` — fake handler delay > 15s(或注入 `CancellationToken`)
- ✅ `FetchAsync_ReturnsNullOnNetworkError` — fake handler 抛 `HttpRequestException`
- ✅ `FetchAsync_DoesNotThrow_OnAnyFailure` — 即使 cache 文件路径无效也不抛

### 7.2 `PyTorchVersionCacheTests`

- ✅ `TryRead_ReturnsNullWhenFileMissing`
- ✅ `TryRead_ReturnsParsedWhenWithinTtl`
- ✅ `TryRead_ReturnsNullWhenTtlExpired`(写 `FetchedAt = UtcNow - 2h` 后再读)
- ✅ `TryRead_ReturnsNullOnCorruptJson`(写 `{"Stable":` 后读)
- ✅ `Write_CreatesDirectoryIfMissing`
- ✅ `Write_RoundTrip`(写 → 读 → 内容一致)
- ✅ `Write_DoesNotThrow_OnReadOnlyDir`(尽量模拟,可能 skip)

### 7.3 `BaseEnvProfileLoaderTests`(扩展)

保留原有测试:
- ✅ `GetHardcodedDefaults_*_HasExpectedFields` × 5(改名 + 字面量断言保留)
- ✅ `LoadAsync_FallsBackWhenFileMissing`
- ✅ `LoadAsync_ReturnsParsedWhenFileExists`
- ✅ `LoadAsync_FallsBackOnCorruptJson`

新增测试(`GetLiveDefaults_*` 系列):
- ✅ `GetLiveDefaults_UsesFetchedStableVersion`(fake fetcher 返回 `2.13.0` → 6 个 profile 的 `TorchVersion` 都对,除了 nightly)
- ✅ `GetLiveDefaults_GeneratesSixProfiles`(确认有 cu118/cu121/cu124/cu126/cpu/nightly)
- ✅ `GetLiveDefaults_NightlyProfileKeepsLiteralNightly`
- ✅ `GetLiveDefaults_FallsBackOnFetcherReturnsNull`
- ✅ `GetLiveDefaults_UsesCacheWhenFresh`(cache 1h 内 → 不调 fetcher;用一个计数 fake fetcher 验证)
- ✅ `GetLiveDefaults_RefetchesWhenCacheExpired`
- ✅ `GetLiveDefaults_WithoutHttp_UsesHardcoded`(ctor 不传 HttpClient → 直接 hardcoded)

### 7.4 端到端手动 verify

- WPF 启动 → 基础环境页 → 看到 6 个 profile,稳定版 5 个名称含 `PyTorch 2.13.0`(或当下实际值)
- 离线启动 → 6 个 profile 显示 v0.6.5 字面量 `PyTorch 2.1`
- 删 cache 文件 → 重启 → 重新打 pytorch.org
- 编辑 `base_env_profiles.json` → 重启 → 显示自定义内容(不拉 live)

---

## 8. 风险与权衡

| 风险 | 缓解 |
|---|---|
| pytorch.org HTML 改版 → regex 失效 → 用户看不到最新版本 | (1) `FetchAsync` 失败静默回退,UI 仍可用 (2) cache 1h TTL 减少对站点的请求频次 (3) 失败可在 ledger 留 observe,后续加 fallback 到 PyPI JSON |
| 启动时拉取 pytorch.org 阻塞 15s(超时) | (1) `App.xaml.cs` 已有 15s 超时,不延长 (2) cache 命中路径 < 50ms (3) UI 显示"loading..."而非空白 |
| 用户编辑的 `base_env_profiles.json` 仍含 `"2.1.0"` → 不拉 live | 设计如此:文件存在则文件优先(fallback 时才拉 live);用户升级到 v0.6.5.1 后删文件即可恢复 live |
| `HasNightlyCu126 = false` 罕见场景(nightly 撤掉 cu126) | 返回 null → fallback hardcoded 5 个 + hardcoded nightly cu121 仍能用 |
| 多用户机器共享 `%APPDATA%` 不存在 | `Directory.CreateDirectory` 兜底(`PyTorchVersionCache.Write` 第一行) |
| `PyTorchLiveVersions.FetchedAt` 用本地时区序列化导致 TTL 误判 | 用 `DateTimeOffset.UtcNow`,序列化是 ISO-8601 with `Z`,反序列化 `DateTimeOffset` 自动识别 UTC |
| Nightly 用户想装 `cu121` 而不是 cu126 | 当前 nightly cu121 HTML 不再列(虽然 `cuda.x`/`y`/`z` 都是 12.6/13.0/13.2);用户可手动编辑 `base_env_profiles.json` 加自定义 profile |

---

## 9. 执行选择

**Recommended: Subagent-Driven Development**
- 任务拆解见 plan:`docs/superpowers/plans/2026-07-23-pytorch-live-fetch.md`
- 估 4-5 task × implementer + reviewer = ~8-10 subagent dispatch
- 估 4-5 commits on main

执行前需要用户:
1. 审阅本 spec → 确认设计无误
2. 决定版本号 v0.6.5.1 vs v0.6.6
3. ExitPlanMode 批准 plan