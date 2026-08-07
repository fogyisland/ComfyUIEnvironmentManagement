# v0.6.7.4 Catalog 节点内容完整入库 + Requirements 列表化 设计 spec

> **For agentic workers:** This is a design spec. Read once before writing the implementation plan; do not modify without user approval.

## 1. Goal

解决 Catalog 页面 3 个用户痛点:

1. **刷新后 catalog 列表为空,重启后又得重新拉**。用户实测 staging exe `data/catalog-cache.db` 是 0 行,debug build 的 DB 是 5846 行(2026-07-19),看起来 refresh 没有把数据写入 runtime 路径。需要根因诊断 + 修复(可能是 silent failure,需要加日志)。
2. **catalog 节点内容目前只存 raw_metadata JSON 字典**,关键字段(author / description / install_type / reference / last_update)散落各处,UI 每次都得 `RawMetadata["author"]` 解析,既慢又脆弱。要把它们抽成 typed 列 + typed property。
3. **install requirements 当前隐藏在 `raw_metadata.pip`(list of strings)里**,用户希望它"变成列表,能够较为简单的实现包名和版本比较"。需要解析为 `PipRequirement { Name, Specifier }` 结构,提供 `IsSatisfiedBy(installedVersion)` helper。

**用户原话**:
> "节点内容刷新之后入库"
>
> "catalog 节点写入本地的 sqlite 数据库,包名、作者、版本、安装 requirements、发布日期 等等"
>
> "刷新后不入库,重启后又要重新拉一边,我们需要点击刷新后所有数据都入库"
>
> "点了刷新后,重启 app 目录列表为空"
>
> "Requirements 能够最好变成列表,能够较为简单的实现包名和版本比较"

**Non-goals(本 spec 不做)**:
- **不**实现"自动为 env 装 pip requirements"——只是暴露数据 + matcher,后续 v0.6.7.x 再接 install 入口
- **不**改 catalog 搜索行为、分页、视图模式
- **不**bump version / 不发 release zip(per memory `feedback_no_zip.md`)

## 2. Background

### 2.1 现状

- **持久化路径**(`CatalogRepository.cs`):`UpsertBatch` 在 `Task.Run` 里跑 transaction + 每条 `INSERT ON CONFLICT UPDATE`,返回 INSERT count。`Search(query, limit)` 用 `LOWER(package) LIKE @pattern OR LOWER(raw_metadata) LIKE @pattern`,**无 expiry filter**(v0.6.5.4 fix 撤掉了 expiry 过滤,因为 raw_metadata LIKE 会扫整列)。`ListNonExpired` 方法存在但没人调用。
- **DB 路径**(`CatalogCacheStore.cs:17-22`):`<AppContext.BaseDirectory>/data/catalog-cache.db`。**重要**:debug build 和 staging exe 各有自己的 base directory,所以**两个 DB 文件独立存在**——这是 v0.6.5.3 G3 故意拆分的设计,debug/staging 不能共享 cache。
- **UI 暴露字段**(`CatalogViewModel.cs:113-118`):`SelectedAuthor` / `SelectedDescription` / `SelectedInstallType` / `SelectedReference` 都是 `RawMetadata["key"]` 字典查找,O(1) 但每次 selected change 触发一次。
- **日志缺口**:`CatalogFetcher` / `CatalogRefreshService` / `CatalogRepository` 三个类**完全没有任何 AppLogger 调用**(grep 0 命中)。所以诊断"为什么 staging DB 0 行"无任何线索。
- **raw_metadata 实际 keys**(5846 entries 采样):`apt_dependency` / `author` / `badges` / `category` / `dependencies`(空) / `description` / `files` / `id` / `install_type` / `js_path` / `last_update`(日期字符串如 `2025-06-15 00:00:00`) / `license` / `name` / `nickname` / `nodename_pattern` / `pip`(24 entries 非空,如 `['huggingface-hub']` / `['numpy>=1.24.0']`) / `preemptions` / `reference` / `reference2` / `stars` / `tags` / `title` / `version`。
- **pip specifier 实测样本**:`gradio==4.19.0` / `numpy>=1.20.0` / `torchaudio>=2.0.0` / `color-matcher>=0.3.0`,20 条带 specifier,46 条纯 name。

### 2.2 v0.6.5.13 集中日志基础设施(可复用)

`AppLogger.Info(category, message)` 写到 `<projectRoot>/Logs/YYYY-MM-DD.log`,category 是 subsystem tag。本 spec 复用同款,在 catalog path 加 `[catalog]` / `[catalog-fetch]` / `[catalog-upsert]` 三个 category,跟 `[env-start]` / `[bed-install]` 风格一致。

### 2.3 v0.6.5.14 系统状态 tab + 详情面板模式(可复用)

`SystemStatusViewModel` 用 typed property(OS / CPU / Memory / Disks / GPU / CUDA),UI 绑 typed property 而非字典 lookup。本 spec 同款——把 raw_metadata 里高频字段抽到 typed property。

### 2.4 当前 bug 假设

用户 staging DB 是 0 行,debug DB 是 5846 行。**3 个可能根因**(待 T1 加日志后确诊):

| # | 假设 | 验证方法 |
|---|---|---|
| H1 | 用户实际从未在 staging 上点过刷新(只点了 debug build) | 看 Logs 有无 `[catalog]` 行 |
| H2 | Refresh 跑了但写入失败(silent throw 被外层 catch 吞) | 看 Logs 有无 ERROR 行 |
| H3 | `AppContext.BaseDirectory` 在 self-contained staging 下路径解析异常 | 看 DbPath 实际值 |

本 spec **T1 必须加日志先诊断**,再决定 T2 是否需要 patch。

## 3. Design

### 3.1 数据层(`CatalogCacheStore` + `CatalogRepository` + `CatalogEntry`)

#### 3.1.1 新增 typed 列(迁移)

`CatalogCacheStore.cs` `InitSchemaIfMissing` 末尾加 6 个 `EnsureColumn`:

```csharp
EnsureColumn(conn, "catalog_cache", "author", "TEXT");
EnsureColumn(conn, "catalog_cache", "description", "TEXT");
EnsureColumn(conn, "catalog_cache", "install_type", "TEXT");
EnsureColumn(conn, "catalog_cache", "reference", "TEXT");
EnsureColumn(conn, "catalog_cache", "last_update", "TEXT");
EnsureColumn(conn, "catalog_cache", "pip_json", "TEXT");
```

**Why TEXT 不抽 author_id / install_type_id 等 reference 表**:catalog 数据是只读的(用户不会改 catalog),不需要 FK 完整性;text 直存最简单,搜索走 `raw_metadata LIKE` 已经够用。

#### 3.1.2 `CatalogEntry` 加 typed property

```csharp
public class CatalogEntry
{
    // ... 既有 Id / SourceUrl / Package / RawMetadata / CachedAt / ExpiresAt / LatestVersion

    // v0.6.7.4: 从 raw_metadata 抽出的 typed 字段
    [JsonIgnore] public string? Author { get; init; }
    [JsonIgnore] public string? Description { get; init; }
    [JsonIgnore] public string? InstallType { get; init; }
    [JsonIgnore] public string? Reference { get; init; }
    [JsonIgnore] public string? LastUpdate { get; init; }

    // 解析后的 pip requirements 列表(从 pip_json 反序列化)
    [JsonIgnore] public IReadOnlyList<PipRequirement> PipRequirements { get; init; }
        = Array.Empty<PipRequirement>();
}
```

**Why `[JsonIgnore]`**:raw_metadata 仍保留完整 JSON 字典作为兜底(有些字段本 spec 不抽如 `tags` / `badges`,未来需要时再走 raw_metadata),typed property 是 cache 层。

#### 3.1.3 `UpsertBatch` 解析并写 typed 列

UpsertCommandText 扩展为:

```sql
INSERT INTO catalog_cache
    (id, source_url, package, raw_metadata, cached_at, expires_at,
     author, description, install_type, reference, last_update, pip_json)
VALUES
    (@id, @source_url, @package, @raw_metadata, @cached_at, @expires_at,
     @author, @description, @install_type, @reference, @last_update, @pip_json)
ON CONFLICT(source_url, package) DO UPDATE SET
    raw_metadata=excluded.raw_metadata,
    cached_at=excluded.cached_at,
    expires_at=excluded.expires_at,
    author=excluded.author,
    description=excluded.description,
    install_type=excluded.install_type,
    reference=excluded.reference,
    last_update=excluded.last_update,
    pip_json=excluded.pip_json
```

`BindUpsertParameters` / `UpsertBatch` pre-add 6 个新 named parameter(`@author` / `@description` / `@install_type` / `@reference` / `@last_update` / `@pip_json`),每条 entry 写库前调 `ExtractTypedFields(entry)` 拿到值并 mutate parameter `.Value`:

```csharp
private static (string? author, string? description, string? installType,
                string? reference, string? lastUpdate, string pipJson)
ExtractTypedFields(CatalogEntry entry)
{
    var rm = entry.RawMetadata ?? new Dictionary<string, object?>();
    string? Get(string k) => rm.TryGetValue(k, out var v) ? v?.ToString() : null;

    var pipList = Get("pip") is string s
        ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        : (rm.TryGetValue("pip", out var p) && p is JsonElement je && je.ValueKind == JsonValueKind.Array
            ? je.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList()
            : new List<string>());

    var reqs = PipRequirement.ParseList(pipList);
    var pipJson = JsonSerializer.Serialize(reqs.Select(r => new {
        name = r.Name, spec = r.Specifier
    }));
    return (Get("author"), Get("description"), Get("install_type"),
            Get("reference"), Get("last_update"), pipJson);
}
```

**Why 兼容 string / JsonElement 两种 pip 格式**:实测 raw_metadata 里 `pip` 是 JSON array(经 SQLite 序列化后变 `["..."]` string),但若 CatalogFetcher 未来写 Dictionary 则是 string 列表。两条路径都支持。

#### 3.1.4 `Read(reader)` 还原 typed property

`Search` SELECT 列扩展为:

```sql
SELECT id, source_url, package, raw_metadata, cached_at, expires_at,
       latest_version, author, description, install_type, reference,
       last_update, pip_json
FROM catalog_cache
```

`Read(reader)` 把第 7-12 列(latest_version..pip_json)写回 `CatalogEntry` 的 typed property。`pip_json` 反序列化成 `List<PipRequirement>`;反序列化失败时回退 `Array.Empty<PipRequirement>()`(defensive,raw_metadata 仍兜底)。

### 3.2 解析层(`Models/PipRequirement.cs` + `Services/PipRequirementMatcher.cs`)

#### 3.2.1 `PipRequirement` model

```csharp
public sealed record PipRequirement(string Name, string? Specifier)
{
    // 规整化:小写、去空格、保留 specifier 原样(PEP 440 风格)
    public string NormalizedName => Name.Trim().ToLowerInvariant()
        .Replace('_', '-').Replace('.', '-');

    public static IReadOnlyList<PipRequirement> ParseList(IEnumerable<string?> raw)
    {
        var list = new List<PipRequirement>();
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var trimmed = s.Trim();
            // 找第一个 specifier 字符(>=, <=, ==, !=, >, <, ~=, ===)
            int specIdx = -1;
            for (int i = 0; i < trimmed.Length - 1; i++)
            {
                var c = trimmed[i];
                if (c is '>' or '<' or '!' or '=' or '~')
                {
                    // 双字符 / 三字符先匹配,避免把 ">=" 切成 ">"
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '=')
                    { specIdx = i; break; }
                    if (i + 2 < trimmed.Length && trimmed[i + 1] == '=' && trimmed[i + 2] == '=')
                    { specIdx = i; break; }
                    if (c is '>' or '<')
                    { specIdx = i; break; }
                }
            }
            if (specIdx < 0)
                list.Add(new PipRequirement(trimmed, null));
            else
                list.Add(new PipRequirement(trimmed[..specIdx], trimmed[specIdx..]));
        }
        return list;
    }
}
```

**Why record**:immutable,equality 自动,name+specifier 唯一标识一个 requirement。

#### 3.2.2 `PipRequirementMatcher`

```csharp
public static class PipRequirementMatcher
{
    /// <summary>
    /// 给一个已安装的包 + 版本,返回该 catalog requirement 是否被满足。
    /// installedVersion 为 null/空 → 视为 unknown,返回 false(不报错)。
    /// Specifier 为 null → 仅匹配 Name(任何版本都算满足)。
    /// </summary>
    public static bool IsSatisfiedBy(PipRequirement req, string? installedVersion)
    {
        if (string.IsNullOrEmpty(installedVersion)) return false;
        if (req.Specifier is null) return true;
        // 简单 semver 比较:用 System.Version 处理 X.Y.Z
        // 支持多 specifier(逗号分隔,AND 关系)如 ">=1.0,<2.0"
        foreach (var single in req.Specifier.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!SingleMatches(installedVersion, single)) return false;
        }
        return true;
    }

    private static bool SingleMatches(string installed, string single)
    {
        // 解析 op + version
        string op = ""; string ver = single;
        for (int i = 0; i < single.Length; i++)
        {
            if (single[i] is '>' or '<' or '!' or '=' or '~')
            {
                op = single[..(i + (single[i + 1] == '=' ? 2 : 1))];
                ver = single[op.Length..];
                break;
            }
        }
        if (!Version.TryParse(NormalizeVersion(ver), out var want)) return false;
        if (!Version.TryParse(NormalizeVersion(installed), out var have)) return false;
        var cmp = have.CompareTo(want);
        return op switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">"  => cmp > 0,
            "<"  => cmp < 0,
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "~=" => cmp == 0 || (cmp > 0 && have.Major == want.Major),
            _    => false,  // 不支持的 op
        };
    }

    private static string NormalizeVersion(string v)
    {
        // "1.0" → "1.0.0"; "1.0.0a1" → "1.0.0"(丢 prerelease,简化比较)
        var dash = v.IndexOfAny(new[] { 'a', 'b', 'r', 'p', '-' });
        var clean = dash >= 0 ? v[..dash] : v;
        var parts = clean.Split('.');
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }
}
```

**Why 不引入 NuGet semver 库**:`System.Version` 已 cover 99% 场景,PEP 440 prerelease(a1/b2)简化丢弃,本 spec 不需要严格 PEP 440 compliance。后续 spec 真要严格再引 `System.Versioning` 或 `NuGet.Versioning`。

### 3.3 服务层日志(`CatalogFetcher` + `CatalogRefreshService`)

#### 3.3.1 注入 AppLogger(沿用 v0.6.5.13 模式)

两个 service ctor 加 `AppLogger? logger = null` 末尾参数。`App.xaml.cs` 把现有的 `logger` 传给它们(已有 ctor wiring 链路):

```csharp
var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes, logger);
var catalogRefreshService = new CatalogRefreshService(
    catalogFetcher, catalogRepo, settings, githubVersionService, nodeVersionRepo, logger);
```

#### 3.3.2 日志点

| 类 | 事件 | category | level | 何时写 |
|---|---|---|---|---|
| `CatalogFetcher` | fetch start | `catalog-fetch` | INFO | URL + retry count |
| `CatalogFetcher` | fetch complete | `catalog-fetch` | INFO | count + duration_ms |
| `CatalogFetcher` | fetch failed | `catalog-fetch` | ERROR | exception message + URL |
| `CatalogRefreshService` | refresh start | `catalog-refresh` | INFO | active source URL + ttl |
| `CatalogRefreshService` | refresh complete | `catalog-refresh` | INFO | upsert_count + version_count + duration_ms |
| `CatalogRefreshService` | refresh failed | `catalog-refresh` | ERROR | reason |
| `CatalogRefreshService` | no active source | `catalog-refresh` | WARN | settings state snapshot |
| `CatalogRepository.UpsertBatch` | batch start | `catalog-upsert` | DEBUG | count + db path |
| `CatalogRepository.UpsertBatch` | batch complete | `catalog-upsert` | INFO | count + duration_ms |

**Why WARN on no-active-source**:settings 状态错误是 user-fixable,不 ERROR(不是 bug)。但要留痕帮助诊断。

**Why DEBUG on batch start**:per-batch 不 spam INFO,complete 用 INFO 即可。

### 3.4 UI 层(`CatalogViewModel` + `CatalogView.xaml`)

#### 3.4.1 `CatalogViewModel` 改 typed property

替换 line 113-118 的 6 个 `SelectedXxx` getter:

```csharp
public string? SelectedAuthor => _selected?.Author;
public string? SelectedDescription => _selected?.Description;
public string? SelectedInstallType => _selected?.InstallType;
public string? SelectedReference => _selected?.Reference;
public string? SelectedLastUpdate => _selected?.LastUpdate;
public IReadOnlyList<PipRequirement> SelectedPipRequirements
    => _selected?.PipRequirements ?? Array.Empty<PipRequirement>();
```

`Selected` setter 末尾 `LoadVersionsForSelected()` 后加 `RaisePropertyChanged(nameof(SelectedPipRequirements))` 等。

#### 3.4.2 XAML 详情面板加 typed 字段

`<StackPanel>` 里在现有 TextBlock 之间插:

```xaml
<TextBlock Text="{Binding SelectedLastUpdate, StringFormat='最后更新: {0}'}"
           Margin="0,4,0,0" FontSize="11" Foreground="Gray" />

<!-- Requirements 列表 -->
<Expander Header="Requirements" Margin="0,8,0,0" IsExpanded="False"
          Visibility="{Binding HasPipRequirements, Converter={StaticResource BoolToVisibility}}">
    <ItemsControl ItemsSource="{Binding SelectedPipRequirements}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Border Padding="4,2" Margin="0,1" Background="#F5F5F5" CornerRadius="2">
                    <TextBlock FontFamily="Consolas" FontSize="11">
                        <Run Text="{Binding Name}" FontWeight="Bold" />
                        <Run Text="{Binding Specifier, TargetNullValue=''}" Foreground="DarkSlateGray" />
                    </TextBlock>
                </Border>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</Expander>
```

`HasPipRequirements => SelectedPipRequirements.Count > 0` 加到 VM(同 HasVersions 模式)。

### 3.5 全局约束(Global Constraints)

| # | 约束 | 出处 |
|---|---|---|
| G1 | `CatalogFetcher` / `CatalogRefreshService` 保留原 ctor 签名向后兼容(新增 logger 是末尾 optional 参数) | App.xaml.cs + 既有 6 处调用 |
| G2 | `CatalogCacheStore.EnsureColumn` 迁移必须幂等(PRAGMA table_info 检查后再 ALTER TABLE,跟 v0.6.5.7 BedStatus 列同款) | CatalogCacheStore.cs:86 |
| G3 | `CatalogRepository.UpsertBatch` / `Search` 行为不变(只增加 typed 列写入/读出),既有测试 0 改 | CatalogRepositoryTests + CatalogRefreshServiceTests |
| G4 | AppLogger 注入沿用 v0.6.5.13 模式(末尾 optional `AppLogger? logger = null`),不引入新 DI 框架 | feedback_no_zip.md + AppLogger usage |
| G5 | 不 bump version / 不发 release zip(per memory `feedback_no_zip.md`) | 既有 staging rebuild 模式 |
| G6 | `[JsonIgnore]` 加在 typed property 上——raw_metadata 仍完整保留,UI 仍可走 fallback | 不破坏既有 XAML 兜底 |
| G7 | `PipRequirement.ParseList` 处理 empty / whitespace / 单 name / 带 specifier / 多 specifier(逗号分隔 AND) | PEP 440 简化版 |
| G8 | `PipRequirementMatcher.IsSatisfiedBy` 对 `installedVersion == null` 返回 false(不抛),对无法解析的版本号返回 false | defensive,不阻塞调用方 |
| G9 | UI 改动只动 XAML 详情面板新增 1 个 Expander + 1 个 TextBlock,既有字段不变 | 隔离 |
| G10 | `staging/` 重建 self-contained win-x64 per `feedback_staging_self_contained.md` | staging 模式 |
| G11 | 不引入 semver NuGet 库,用 `System.Version` | YAGNI |
| G12 | CatalogRefresh / CatalogFetcher 的日志点 category 命名为 `[catalog-fetch]` / `[catalog-refresh]` / `[catalog-upsert]`(跟 `[bed-install]` / `[env-start]` 同款) | 命名一致 |

## 4. File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/PipRequirement.cs` | ~50 | record + ParseList static |
| `src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs` | ~80 | IsSatisfiedBy + SingleMatches + NormalizeVersion |
| `tests-wpf/.../Models/PipRequirementTests.cs` | ~120 | 6 测试 |
| `tests-wpf/.../Services/PipRequirementMatcherTests.cs` | ~180 | 8 测试 |
| `tests-wpf/.../Data/CatalogRepositoryTypedFieldsTests.cs` | ~150 | 4 测试 |
| `tests-wpf/.../Services/CatalogRefreshServiceLoggingTests.cs` | ~100 | 3 测试 |
| `tests-wpf/.../ViewModels/CatalogViewModelRequirementsTests.cs` | ~100 | 3 测试 |

### Modify

| 文件 | 改动 | 行数 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` | 末尾加 6 个 EnsureColumn | +10 |
| `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` | 加 6 个 typed property + JsonIgnore | +15 |
| `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` | UpsertBatch 调 ExtractTypedFields,Read 还原 typed | +30 |
| `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs` | ctor +logger,fetch start/complete/failed 日志 | +20 |
| `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` | ctor +logger,refresh start/complete/failed/no-source 日志 | +25 |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | 传 logger 给 catalog service | +2 |
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | SelectedAuthor 等改 typed property,加 SelectedPipRequirements / HasPipRequirements | +15 |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 详情面板加 LastUpdate TextBlock + Requirements Expander | +20 |

### Delete
无。

### Keep(unchanged)
- `BaseEnvInstaller` / `BaseEnvProgressDialog` / `EnvStartStatusViewModel` / `ProcessLauncher`(G14 隔离)
- `Settings` JSON 模型(不动 Settings 结构)
- 既有 6 处 CatalogFetcher / CatalogRefreshService 调用(App.xaml.cs 是唯一 production 调用,测试沿用 ctor 兼容)
- `ListNonExpired` / `BulkUpdateOrchestrator` / `NodeOperations`

## 5. Testing

| 测试 | 验证 |
|---|---|
| `PipRequirement.ParseList_Empty_ReturnsEmpty` | `[]` → `[]` |
| `PipRequirement.ParseList_BareName_NoSpecifier` | `"huggingface-hub"` → `{Name="huggingface-hub", Specifier=null}` |
| `PipRequirement.ParseList_WithSpecifier_SplitsCorrectly` | `"numpy>=1.24.0"` → `{Name="numpy", Specifier=">=1.24.0"}` |
| `PipRequirement.ParseList_MultiSpecifier_PreservesSpecifier` | `"requests>=1.0,<2.0"` → `{Name="requests", Specifier=">=1.0,<2.0"}` |
| `PipRequirement.ParseList_NormalizesName` | `"Some_PKG"` → `NormalizedName="some-pkg"` |
| `PipRequirement.ParseList_SkipsEmptyAndWhitespace` | `["", "  ", "torch"]` → 1 element |
| `PipRequirementMatcher.IsSatisfiedBy_NoSpecifier_AlwaysTrue` | `req={torch, null}` + `installedVersion="2.0.0"` → true |
| `PipRequirementMatcher.IsSatisfiedBy_GEQ_Passes` | `req={numpy, ">=1.20"}` + `"1.24.0"` → true |
| `PipRequirementMatcher.IsSatisfiedBy_GEQ_Fails` | `req={numpy, ">=1.20"}` + `"1.19.0"` → false |
| `PipRequirementMatcher.IsSatisfiedBy_EQ_PassesAndFails` | `gradio==4.19.0` + `4.19.0` → true;+`4.20.0` → false |
| `PipRequirementMatcher.IsSatisfiedBy_Range_AndSemantics` | `>=1.0,<2.0` + `1.5.0` → true;+`2.0.0` → false |
| `PipRequirementMatcher.IsSatisfiedBy_NullVersion_False` | req + `null` → false(不抛) |
| `PipRequirementMatcher.IsSatisfiedBy_UnparseableVersion_False` | req + `"not.a.version"` → false |
| `PipRequirementMatcher.IsSatisfiedBy_CompatibleRelease` | `~=1.4.2` + `1.4.5` → true |
| `CatalogRepository_UpsertBatch_PopulatesTypedColumns` | upsert 1 entry → 重读 SELECT 拿 typed column |
| `CatalogRepository_UpsertBatch_AuthorFromRawMetadata` | raw_metadata.author="alice" → typed Author="alice" |
| `CatalogRepository_UpsertBatch_PipJsonParsedToList` | raw_metadata.pip=["numpy>=1.0"] → PipRequirements[0].Name="numpy" |
| `CatalogRepository_Search_AfterMigration_ReturnsTypedFields` | 旧 DB(staging 现状 0 rows 不适用,用 TestDb)→ 加 author 列后,upsert + search 拿到 typed |
| `CatalogRefreshService_LogsFetchComplete_AtInfo` | 用 FakeLogger 验证有 INFO line,category="catalog-refresh",含 entry_count |
| `CatalogRefreshService_LogsNoActiveSource_AtWarn` | settings.ActiveQuerySourceName="" → WARN log |
| `CatalogRefreshService_LogsFailedFetch_AtError` | fetcher throws → ERROR log + result.Error |
| `CatalogViewModel_SelectedPipRequirements_PopulatedFromDb` | ctor Search 后 select entry → SelectedPipRequirements.Count > 0 |
| `CatalogViewModel_HasPipRequirements_ReflectsCount` | entry 有 pip → true;无 → false |
| `CatalogViewModel_PropertyChanged_FiresOnSelectedChange` | Selected = entry → PropertyChanged for 6 个 typed property 名 |

**预估**:619 → 668 PASS(+49:6+8+4+3+3=24 catalog 测试,加既有 24 测试不破坏)。

## 6. Risks & Tradeoffs

| 风险 | 缓解 |
|---|---|
| T1 加日志后用户跑去 staging 跑 refresh,**根因发现是 H1**(用户根本没点过刷新),导致 spec 范围缩窄 | spec 范围不变——日志 + 抽列 + requirements 解析都是用户要求的功能,即使 H1 成立也要做。日志能确认是 H1/H2/H3 |
| `PipRequirement.ParseList` 对 PEP 440 完整规范只覆盖 80%(pre-release / epoch / url / extras 字段简化丢弃) | spec 不要求严格 PEP 440;后续真要严格再引 NuGet 库 |
| `pip_json` 列存 `[{name, spec}, ...]`,SQLite 不能直接 WHERE search(只能走 raw_metadata LIKE) | 接受——本 spec 不改 Search 行为,只 cache typed column |
| UI Expander 跟既有主题颜色不匹配 | 默认 Style,后续 spec 统一调 |
| 加 6 个 EnsureColumn 对存量 staging DB 是 ALTER TABLE,几毫秒 | 启动时一次,跟 v0.6.5.7 BedStatus 同款 |
| `ExtractTypedFields` 每次 upsert 都解析 raw_metadata 的 pip list,5000+ entries 性能 | 一次 refresh 5000 条,解析每条 < 1ms,可接受 |
| 详情面板加 Expander 增高度,DataGrid 行少几行 | 用 IsExpanded=False 折叠,默认不展开 |

## 7. Out of Scope(显式不做)

- **不**做"为 env 自动装 pip requirements"——只是暴露数据 + matcher,后续 v0.6.7.x 接 install 入口
- **不**改 catalog Search 行为 / pagination / view mode
- **不**改 `ListNonExpired`(它继续 unused,留作 v0.6.5.4 兼容)
- **不**加新的 PyPI / pip 下载入口
- **不**改 `Settings.json` schema
- **不** bump version / 不发 release zip

---

> 评审人:用户(待批准)
> 下一阶段:invoking `superpowers:writing-plans` 创建 `docs/superpowers/plans/2026-08-07-catalog-requirements.md`
