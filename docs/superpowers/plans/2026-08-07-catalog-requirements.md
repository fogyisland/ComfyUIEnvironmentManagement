# v0.6.7.4 Catalog 节点内容完整入库 + Requirements 列表化 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 catalog 节点内容(`author` / `description` / `install_type` / `reference` / `last_update` / `pip` requirements)从 raw_metadata JSON 字典抽成 typed 列 + typed property,为 `PipRequirement` 提供 `IsSatisfiedBy` 版本比较 helper,顺便给 catalog 路径加 AppLogger 诊断 "staging DB 0 行" bug。

**Architecture:**
- `CatalogCacheStore` 加 6 个 `EnsureColumn` 幂等迁移(author / description / install_type / reference / last_update / pip_json)
- `CatalogEntry` 加 6 个 `[JsonIgnore]` typed property,`PipRequirement` record + `ParseList` 静态方法
- `PipRequirementMatcher.IsSatisfiedBy(req, installedVersion)` 用 `System.Version` 做 semver 比较,支持 `>= / <= / > / < / == / != / ~=` + 多 specifier `AND`
- `CatalogRepository.UpsertBatch` 调 `ExtractTypedFields` 解析 raw_metadata + 写 typed 列;`Read(reader)` 读 typed 列回 `CatalogEntry`
- `CatalogFetcher` / `CatalogRefreshService` ctor 末尾加 `AppLogger? logger = null`,在 fetch start/complete/failed + refresh start/complete/failed/no-source 写日志
- `CatalogViewModel` 改 typed property(`SelectedAuthor` / `SelectedDescription` / `SelectedInstallType` / `SelectedReference` / `SelectedLastUpdate` / `SelectedPipRequirements` / `HasPipRequirements`)
- `CatalogView.xaml` 详情面板加 1 个 `LastUpdate` TextBlock + 1 个 `Requirements` `Expander` 列出 pip requirements

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · `System.Version`(内置,无 NuGet 依赖)· `System.Text.Json` · `AppLogger` (v0.6.5.13 集中日志) · hand-rolled MVVM (`RelayCommand`)

## Context

用户在桌面验 v0.6.7.3 后反馈两个新需求:

1. **catalog 节点内容要完整入库**——目前 raw_metadata 存完整 JSON 字典,但 UI 每次都 `RawMetadata["author"]` 查找,既慢又脆弱。希望 author / description / install_type / reference / last_update 抽成 typed 列。`pip` requirements 字段("安装 requirements")解析为 `[{name, spec}]` 列表,提供 `IsSatisfiedBy(installedVersion)` helper 做包名+版本比较。
2. **staging DB 是 0 行,但 debug DB 是 5846 行**——用户实测 staging exe `data/catalog-cache.db` 完全是空 schema,启发怀疑 catalog 刷新路径有 silent failure。需要加 AppLogger 诊断。

**用户原话**:
> "节点内容刷新之后入库"
>
> "catalog 节点写入本地的 sqlite 数据库，包名、作者、版本、安装 requirements、发布日期 等等"
>
> "刷新后不入库，重启后又要重新拉一边"
>
> "点了刷新后，重启 app 目录列表为空"
>
> "Requirements 能够最好变成列表，能够较为简单的实现包名和版本比较"

设计验证(G1-G12 in spec):
- **`PipRequirement` 简化 PEP 440**:spec 字段 `>= / <= / > / < / == / != / ~=`,丢弃 prerelease / epoch / url / extras。`System.Version` 覆盖 99% 场景。后续真要严格 PEP 440 再引 `NuGet.Versioning`。
- **`PipRequirementMatcher` 不抛**:对 null / 空 / 不可解析版本返回 `false`,defensive 不阻塞调用方。
- **日志沿用 v0.6.5.13 模式**:`AppLogger` 末尾 optional 参数,跟 `BaseEnvInstaller` / `EnvStartStatusViewModel` 同款,既有 6 处调用 0 改。
- **不引入新 DI 框架**(项目一直用 composition root 手动 new)。
- **不 bump version / 不发 release zip**(per memory `feedback_no_zip.md`)。

**base SHA:** `e9f5d1d` (v0.6.7.4 spec commit)

**相关已有代码:**
- `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs:86-108` — `EnsureColumn` 迁移工具(已有,直接复用)
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs:87-138` — `UpsertBatch` + `BindUpsertParameters` + `UpsertCommandText`
- `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs:28-32` — `CatalogFetcher` ctor `(HttpClient http, int cacheTtlMinutes = 60)`,加 `AppLogger? logger = null` 末尾参数
- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs:26-37` — `CatalogRefreshService` ctor 同款
- `src-wpf/ComfyUI.Manager/App.xaml.cs:97-104` — `var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes, logger);` 已有 `logger` 变量,需传给 ctor
- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` — 加 6 个 typed property
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs:113-118` — 6 个 `SelectedXxx` getter
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml:150-200` — 详情面板区域(具体行号 XAML 实际查找)
- `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs:67-75` — TestDb 已建 catalog_cache 表(无 typed columns,TestDb 用 `_db.Path` 上的 `CatalogCacheStore` 自动 EnsureColumn 迁移)
- `tests-wpf/ComfyUI.Manager.Tests/Services/AppLoggerTests.cs` — 用 `using var log = new AppLogger(_tempRoot); log.ReadLines();` 验证日志内容

---

## Global Constraints

| # | 约束 | 来源 |
|---|---|---|
| G1 | `CatalogFetcher` / `CatalogRefreshService` 保留原 ctor 签名向后兼容(新增 logger 是末尾 optional 参数) | spec §3.3.1 |
| G2 | `CatalogCacheStore.EnsureColumn` 迁移必须幂等(PRAGMA table_info 检查后再 ALTER TABLE,跟 v0.6.5.7 BedStatus 列同款) | CatalogCacheStore.cs:86 |
| G3 | `CatalogRepository.UpsertBatch` / `Search` 行为不变(只增加 typed 列写入/读出),既有测试 0 改 | spec §3.1.3 |
| G4 | AppLogger 注入沿用 v0.6.5.13 模式(末尾 optional `AppLogger? logger = null`),不引入新 DI 框架 | feedback_no_zip.md + AppLogger usage |
| G5 | 不 bump version / 不发 release zip(per memory `feedback_no_zip.md`) | spec §7 |
| G6 | `[JsonIgnore]` 加在 typed property 上——raw_metadata 仍完整保留,UI 仍可走 fallback | spec §3.1.2 |
| G7 | `PipRequirement.ParseList` 处理 empty / whitespace / 单 name / 带 specifier / 多 specifier(逗号分隔 AND) | spec §3.2.1 |
| G8 | `PipRequirementMatcher.IsSatisfiedBy` 对 `installedVersion == null` 返回 false(不抛),对无法解析的版本号返回 false | spec §3.2.2 |
| G9 | UI 改动只动 XAML 详情面板新增 1 个 Expander + 1 个 TextBlock,既有字段不变 | spec §3.4.2 |
| G10 | `staging/` 重建 self-contained win-x64 per `feedback_staging_self_contained.md` | spec §3.5 |
| G11 | 不引入 semver NuGet 库,用 `System.Version` | spec §3.2.2 |
| G12 | CatalogRefresh / CatalogFetcher 的日志点 category 命名为 `[catalog-fetch]` / `[catalog-refresh]` / `[catalog-upsert]` | spec §3.3.2 |

---

## File Structure

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
| `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` | 6 个 typed property + HasPipRequirements | +15 |
| `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` | 详情面板加 LastUpdate TextBlock + Requirements Expander | +20 |

### Delete
无。

### Keep(unchanged)
- `BaseEnvInstaller` / `BaseEnvProgressDialog` / `EnvStartStatusViewModel` / `ProcessLauncher`(G14 隔离)
- `Settings` JSON 模型(不动 Settings 结构)
- `ListNonExpired` / `BulkUpdateOrchestrator` / `NodeOperations`

---

## Tasks

### Task 1: `PipRequirement` + `PipRequirementMatcher` + 14 测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/PipRequirement.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/PipRequirementTests.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/PipRequirementMatcherTests.cs`

**Interfaces:**
- Consumes: nothing(leaf)
- Produces:
  ```csharp
  // src-wpf/ComfyUI.Manager/Models/PipRequirement.cs
  namespace ComfyUI.Manager.Models;
  public sealed record PipRequirement(string Name, string? Specifier)
  {
      public string NormalizedName => Name.Trim().ToLowerInvariant()
          .Replace('_', '-').Replace('.', '-');
      public static IReadOnlyList<PipRequirement> ParseList(IEnumerable<string?> raw);
  }

  // src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs
  namespace ComfyUI.Manager.Services;
  using ComfyUI.Manager.Models;
  public static class PipRequirementMatcher
  {
      public static bool IsSatisfiedBy(PipRequirement req, string? installedVersion);
  }
  ```

- [ ] **Step 1: Write failing test for `ParseList`**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Models/PipRequirementTests.cs
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class PipRequirementTests
{
    [Fact]
    public void ParseList_Empty_ReturnsEmpty()
    {
        var result = PipRequirement.ParseList(System.Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseList_BareName_NoSpecifier()
    {
        var result = PipRequirement.ParseList(new[] { "huggingface-hub" });
        Assert.Single(result);
        Assert.Equal("huggingface-hub", result[0].Name);
        Assert.Null(result[0].Specifier);
    }

    [Fact]
    public void ParseList_WithSpecifier_SplitsCorrectly()
    {
        var result = PipRequirement.ParseList(new[] { "numpy>=1.24.0" });
        Assert.Single(result);
        Assert.Equal("numpy", result[0].Name);
        Assert.Equal(">=1.24.0", result[0].Specifier);
    }

    [Fact]
    public void ParseList_MultiSpecifier_PreservesComma()
    {
        var result = PipRequirement.ParseList(new[] { "requests>=1.0,<2.0" });
        Assert.Single(result);
        Assert.Equal("requests", result[0].Name);
        Assert.Equal(">=1.0,<2.0", result[0].Specifier);
    }

    [Fact]
    public void ParseList_NormalizesName_LowercaseUnderscoresToDashes()
    {
        var req = PipRequirement.ParseList(new[] { "Some_PKG" }).Single();
        Assert.Equal("some-pkg", req.NormalizedName);
    }

    [Fact]
    public void ParseList_SkipsEmptyAndWhitespace()
    {
        var result = PipRequirement.ParseList(new[] { "", "  ", "torch" });
        Assert.Single(result);
        Assert.Equal("torch", result[0].Name);
    }
}
```

- [ ] **Step 2: Run test, verify 6/6 FAIL**

```bash
cd /d/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~PipRequirementTests"
```
Expected: 6 errors, "PipRequirement not found" / "namespace not found"

- [ ] **Step 3: Implement `PipRequirement.cs`**

```csharp
// src-wpf/ComfyUI.Manager/Models/PipRequirement.cs
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.7.4: 一个 catalog pip requirement 项。
/// 简化 PEP 440:丢弃 prerelease / epoch / url / extras,只支持 spec 字段
/// (`>= / &lt;= / &gt; / &lt; / == / != / ~=`)。
/// Name = 原始(trim),NormalizedName = lowercase + underscore→dash + dot→dash。
/// Specifier = specifier 子串原样(逗号分隔 AND 关系)。
/// </summary>
public sealed record PipRequirement(string Name, string? Specifier)
{
    public string NormalizedName => Name.Trim().ToLowerInvariant()
        .Replace('_', '-').Replace('.', '-');

    public static IReadOnlyList<PipRequirement> ParseList(IEnumerable<string?> raw)
    {
        var list = new List<PipRequirement>();
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var trimmed = s.Trim();
            var specIdx = FindSpecifierIndex(trimmed);
            if (specIdx < 0)
                list.Add(new PipRequirement(trimmed, null));
            else
                list.Add(new PipRequirement(trimmed[..specIdx], trimmed[specIdx..]));
        }
        return list;
    }

    private static int FindSpecifierIndex(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '>' or '<' or '!' or '=' or '~')
            {
                // 三字符 === 优先
                if (c == '=' && i + 2 < s.Length && s[i + 1] == '=' && s[i + 2] == '=')
                    return i;
                // 双字符 >= <= == !=
                if (i + 1 < s.Length && s[i + 1] == '=')
                    return i;
                // 单字符 > < ! ~(无 =
                if (c is '>' or '<')
                    return i;
            }
        }
        return -1;
    }
}
```

- [ ] **Step 4: Run test, verify 6/6 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~PipRequirementTests"
```
Expected: 6 PASS

- [ ] **Step 5: Write failing test for `PipRequirementMatcher`**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/PipRequirementMatcherTests.cs
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class PipRequirementMatcherTests
{
    [Fact]
    public void IsSatisfiedBy_NoSpecifier_AlwaysTrue()
    {
        var req = new PipRequirement("torch", null);
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "2.0.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "99.99.99"));
    }

    [Fact]
    public void IsSatisfiedBy_GEQ_Passes_And_Fails()
    {
        var req = new PipRequirement("numpy", ">=1.20");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.24.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.20.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.19.99"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "0.9.0"));
    }

    [Fact]
    public void IsSatisfiedBy_EQ_Passes_And_Fails()
    {
        var req = new PipRequirement("gradio", "==4.19.0");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "4.19.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "4.19.1"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "4.18.0"));
    }

    [Fact]
    public void IsSatisfiedBy_Range_AndSemantics()
    {
        var req = new PipRequirement("urllib3", ">=1.0,<2.0");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.5.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.99.99"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "2.0.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "0.9.0"));
    }

    [Fact]
    public void IsSatisfiedBy_NullVersion_ReturnsFalse_NoThrow()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, null));
    }

    [Fact]
    public void IsSatisfiedBy_EmptyVersion_ReturnsFalse()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, ""));
    }

    [Fact]
    public void IsSatisfiedBy_UnparseableVersion_ReturnsFalse()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "not-a-version"));
    }

    [Fact]
    public void IsSatisfiedBy_CompatibleRelease_TildeEquals()
    {
        var req = new PipRequirement("numpy", "~=1.4.2");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.2"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.5"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.5.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.1"));
    }
}
```

- [ ] **Step 6: Run test, verify 8/8 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~PipRequirementMatcherTests"
```
Expected: 8 errors, "PipRequirementMatcher not found"

- [ ] **Step 7: Implement `PipRequirementMatcher.cs`**

```csharp
// src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs
using System;
using System.Linq;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.7.4: 让 catalog requirement(pip 字段)可以跟"已安装版本"比对。
/// 简化 PEP 440:支持 `&gt;= / &lt;= / &gt; / &lt; / == / != / ~=` + 逗号分隔 AND。
/// prerelease(a1/b2)丢弃,不解析 epoch / url / extras。
/// 失败模式(G8):null / 空 / 不可解析版本 返回 false,不抛。
/// </summary>
public static class PipRequirementMatcher
{
    public static bool IsSatisfiedBy(PipRequirement req, string? installedVersion)
    {
        if (string.IsNullOrEmpty(installedVersion)) return false;
        if (req.Specifier is null) return true;
        foreach (var single in req.Specifier.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!SingleMatches(installedVersion, single)) return false;
        }
        return true;
    }

    private static bool SingleMatches(string installed, string single)
    {
        string op = "";
        string ver = single;
        for (int i = 0; i < single.Length; i++)
        {
            if (single[i] is '>' or '<' or '!' or '=' or '~')
            {
                int opLen = 1;
                if (i + 1 < single.Length && single[i + 1] == '=') opLen = 2;
                if (i + 2 < single.Length && single[i] == '=' && single[i + 1] == '=' && single[i + 2] == '=')
                    opLen = 3;
                op = single[..(i + opLen)];
                ver = single[(i + opLen)..];
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
            _    => false,
        };
    }

    private static string NormalizeVersion(string v)
    {
        // "1.0" → "1.0.0"; "1.0.0a1" → "1.0.0"(丢 prerelease)
        var dash = v.IndexOfAny(new[] { 'a', 'b', 'r', 'p', '-' });
        var clean = dash >= 0 ? v[..dash] : v;
        var parts = clean.Split('.');
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }
}
```

- [ ] **Step 8: Run test, verify 8/8 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~PipRequirementMatcherTests"
```
Expected: 8 PASS

- [ ] **Step 9: Run full suite, verify no regression**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: ~633 PASS / 0 FAIL / 1 SKIP(619 + 14)

- [ ] **Step 10: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/PipRequirement.cs \
        src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Models/PipRequirementTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/PipRequirementMatcherTests.cs
git commit -m "feat(wpf): PipRequirement + matcher(simplified PEP 440)(v0.6.7.4 T1)"
```

---

### Task 2: `CatalogCacheStore` 6 列迁移 + `CatalogEntry` typed property + `CatalogRepository` 写/读 typed 列 + 4 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` (末尾加 6 个 EnsureColumn)
- Modify: `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` (加 6 个 typed property)
- Modify: `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` (UpsertBatch + Read + ExtractTypedFields)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryTypedFieldsTests.cs`

**Interfaces:**
- Consumes: `PipRequirement.ParseList` (T1)
- Produces:
  ```csharp
  // CatalogEntry.cs 新增
  [JsonIgnore] public string? Author { get; init; }
  [JsonIgnore] public string? Description { get; init; }
  [JsonIgnore] public string? InstallType { get; init; }
  [JsonIgnore] public string? Reference { get; init; }
  [JsonIgnore] public string? LastUpdate { get; init; }
  [JsonIgnore] public IReadOnlyList<PipRequirement> PipRequirements { get; init; }
      = Array.Empty<PipRequirement>();
  ```

- [ ] **Step 1: Write failing test for `CatalogRepository` typed columns**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryTypedFieldsTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogRepositoryTypedFieldsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly CatalogRepository _repo;

    public CatalogRepositoryTypedFieldsTests()
    {
        _db = new TestDb();
        _repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void UpsertBatch_PopulatesAuthor_FromRawMetadata()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-author",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "alice" },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Single(list);
        Assert.Equal("alice", list[0].Author);
    }

    [Fact]
    public void UpsertBatch_PopulatesInstallType_And_Reference()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-types",
            RawMetadata = new Dictionary<string, object?>
            {
                ["install_type"] = "git-clone",
                ["reference"] = "https://github.com/foo/bar",
            },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Equal("git-clone", list[0].InstallType);
        Assert.Equal("https://github.com/foo/bar", list[0].Reference);
    }

    [Fact]
    public void UpsertBatch_ParsesPipList_IntoPipRequirements()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-pip",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "numpy>=1.24.0", "huggingface-hub" },
            },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Equal(2, list[0].PipRequirements.Count);
        Assert.Equal("numpy", list[0].PipRequirements[0].Name);
        Assert.Equal(">=1.24.0", list[0].PipRequirements[0].Specifier);
        Assert.Equal("huggingface-hub", list[0].PipRequirements[1].Name);
        Assert.Null(list[0].PipRequirements[1].Specifier);
    }

    [Fact]
    public void UpsertBatch_OnConflict_UpdatesTypedColumns()
    {
        var entry1 = new CatalogEntry
        {
            Id = "fixed-id",
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-update",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "alice" },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry1 });
        var entry2 = new CatalogEntry
        {
            Id = "fixed-id",
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-update",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "bob" },
            CachedAt = "2026-08-07T00:00:01Z",
            ExpiresAt = "2026-08-08T00:00:01Z",
        };
        _repo.UpsertBatch(new[] { entry2 });

        var list = _repo.Search("", 0);
        Assert.Single(list);
        Assert.Equal("bob", list[0].Author);
    }
}
```

- [ ] **Step 2: Run test, verify 4/4 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogRepositoryTypedFieldsTests"
```
Expected: 4 errors: "Author does not exist" / "InstallType does not exist" / "PipRequirements does not exist" / compilation fails

- [ ] **Step 3: Add 6 typed columns to `CatalogCacheStore.cs`**

Open `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs`, append to `InitSchemaIfMissing` after the existing `EnsureColumn(conn, "catalog_cache", "latest_version", "TEXT")` (line 63):

```csharp
            EnsureColumn(conn, "catalog_cache", "author", "TEXT");
            EnsureColumn(conn, "catalog_cache", "description", "TEXT");
            EnsureColumn(conn, "catalog_cache", "install_type", "TEXT");
            EnsureColumn(conn, "catalog_cache", "reference", "TEXT");
            EnsureColumn(conn, "catalog_cache", "last_update", "TEXT");
            EnsureColumn(conn, "catalog_cache", "pip_json", "TEXT");
```

- [ ] **Step 4: Add typed properties to `CatalogEntry.cs`**

Open `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs`, append after the existing `LatestVersion` property (line 25):

```csharp
    // v0.6.7.4: 从 raw_metadata 抽出的 typed 字段(G6:raw_metadata 仍完整保留作 fallback)
    [JsonIgnore] public string? Author { get; init; }
    [JsonIgnore] public string? Description { get; init; }
    [JsonIgnore] public string? InstallType { get; init; }
    [JsonIgnore] public string? Reference { get; init; }
    [JsonIgnore] public string? LastUpdate { get; init; }

    // 解析后的 pip requirements 列表(从 pip_json 反序列化)
    [JsonIgnore] public IReadOnlyList<PipRequirement> PipRequirements { get; init; }
        = Array.Empty<PipRequirement>();
```

Add `using System;` to top if not already present(it's implicit via `global using`).

- [ ] **Step 5: Update `CatalogRepository.cs` — `UpsertCommandText` + `UpsertBatch` + `ExtractTypedFields` + `Search` + `Read`**

Open `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs`. Modify the following:

**5a.** Replace `UpsertCommandText` constant (line 119-127):

```csharp
    private const string UpsertCommandText = @"
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
            pip_json=excluded.pip_json";
```

**5b.** Replace `UpsertBatch` (line 87-117) — add 6 new parameter pre-add + call ExtractTypedFields:

```csharp
    public int UpsertBatch(IEnumerable<CatalogEntry> entries, Action<CatalogEntry>? onUpserted = null)
    {
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertCommandText;
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@source_url", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@package", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@raw_metadata", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@cached_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@expires_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@author", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@description", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@install_type", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@reference", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@last_update", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@pip_json", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Prepare();
        int count = 0;
        foreach (var entry in entries)
        {
            var typed = ExtractTypedFields(entry);
            cmd.Parameters["@id"].Value = entry.Id;
            cmd.Parameters["@source_url"].Value = entry.SourceUrl;
            cmd.Parameters["@package"].Value = entry.Package;
            cmd.Parameters["@raw_metadata"].Value =
                JsonSerializer.Serialize(entry.RawMetadata, JsonOptions);
            cmd.Parameters["@cached_at"].Value = entry.CachedAt;
            cmd.Parameters["@expires_at"].Value = entry.ExpiresAt;
            cmd.Parameters["@author"].Value = (object?)typed.author ?? DBNull.Value;
            cmd.Parameters["@description"].Value = (object?)typed.description ?? DBNull.Value;
            cmd.Parameters["@install_type"].Value = (object?)typed.installType ?? DBNull.Value;
            cmd.Parameters["@reference"].Value = (object?)typed.reference ?? DBNull.Value;
            cmd.Parameters["@last_update"].Value = (object?)typed.lastUpdate ?? DBNull.Value;
            cmd.Parameters["@pip_json"].Value = typed.pipJson;
            cmd.ExecuteNonQuery();
            count++;
            onUpserted?.Invoke(entry);
        }
        tx.Commit();
        return count;
    }

    private static (string? author, string? description, string? installType,
                    string? reference, string? lastUpdate, string pipJson)
    ExtractTypedFields(CatalogEntry entry)
    {
        var rm = entry.RawMetadata ?? new Dictionary<string, object?>();
        string? Get(string k) => rm.TryGetValue(k, out var v) ? v?.ToString() : null;

        // pip 字段可能已经在 Dictionary 里是 List<object> 形式(JsonElement.Convert 后)
        var pipList = new List<string?>();
        if (rm.TryGetValue("pip", out var p) && p is List<object?> pl)
        {
            foreach (var item in pl)
            {
                if (item is not null) pipList.Add(item.ToString());
            }
        }

        var reqs = PipRequirement.ParseList(pipList);
        var pipJson = JsonSerializer.Serialize(
            reqs.Select(r => new { name = r.Name, spec = r.Specifier }),
            JsonOptions);
        return (Get("author"), Get("description"), Get("install_type"),
                Get("reference"), Get("last_update"), pipJson);
    }
```

**5c.** Replace `Search` (line 26-48) — extend SELECT to include 6 typed columns:

```csharp
    public List<CatalogEntry> Search(string query, int limit)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, source_url, package, raw_metadata, cached_at, expires_at,
                   latest_version, author, description, install_type, reference,
                   last_update, pip_json
            FROM catalog_cache
            WHERE LOWER(package) LIKE @pattern
               OR LOWER(raw_metadata) LIKE @pattern
            ORDER BY package"
            + (limit > 0 ? " LIMIT @limit" : "");
        cmd.Parameters.AddWithValue("@pattern", $"%{query.ToLowerInvariant()}%");
        if (limit > 0) cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<CatalogEntry>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }
```

**5d.** Replace `Read` (line 166-180) — read 6 new columns into typed property:

```csharp
    private static CatalogEntry Read(SqliteDataReader reader)
    {
        var rawJson = reader.GetString(3);
        var pipJson = reader.IsDBNull(12) ? "" : reader.GetString(12);
        var reqs = TryParsePipRequirements(pipJson);
        return new CatalogEntry
        {
            Id = reader.GetString(0),
            SourceUrl = reader.GetString(1),
            Package = reader.GetString(2),
            RawMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                rawJson, JsonOptions) ?? new Dictionary<string, object?>(),
            CachedAt = reader.GetString(4),
            ExpiresAt = reader.GetString(5),
            LatestVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
            Author = reader.IsDBNull(7) ? null : reader.GetString(7),
            Description = reader.IsDBNull(8) ? null : reader.GetString(8),
            InstallType = reader.IsDBNull(9) ? null : reader.GetString(9),
            Reference = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastUpdate = reader.IsDBNull(11) ? null : reader.GetString(11),
            PipRequirements = reqs,
        };
    }

    private static IReadOnlyList<PipRequirement> TryParsePipRequirements(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PipRequirement>();
        try
        {
            var rows = JsonSerializer.Deserialize<List<RawPipRow>>(json, JsonOptions);
            if (rows is null) return Array.Empty<PipRequirement>();
            return rows.Select(r => new PipRequirement(r.name ?? "", r.spec)).ToList();
        }
        catch
        {
            return Array.Empty<PipRequirement>();
        }
    }

    private sealed class RawPipRow
    {
        public string? name { get; set; }
        public string? spec { get; set; }
    }
```

- [ ] **Step 6: Run test, verify 4/4 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogRepositoryTypedFieldsTests"
```
Expected: 4 PASS

- [ ] **Step 7: Run full suite, verify no regression**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: ~637 PASS / 0 FAIL / 1 SKIP(619 + 14 + 4)。CatalogRefreshServiceTests / CatalogRepositoryTests 等既有 catalog 测试必须 0 改动通过(它们用 batched entry,typed column 会被新代码自动处理)。

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs \
        src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs \
        src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogRepositoryTypedFieldsTests.cs
git commit -m "feat(wpf): catalog typed columns + PipRequirements 解析入库 (v0.6.7.4 T2)"
```

---

### Task 3: `CatalogFetcher` + `CatalogRefreshService` 接 AppLogger + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs` (ctor +logger,fetch start/complete/failed)
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` (ctor +logger,refresh start/complete/failed/no-source)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (传 logger 给 catalog service)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceLoggingTests.cs`

**Interfaces:**
- Consumes: `AppLogger?` (v0.6.5.13)
- Produces:
  ```csharp
  // CatalogFetcher.cs
  public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60, AppLogger? logger = null);

  // CatalogRefreshService.cs
  public CatalogRefreshService(
      CatalogFetcher fetcher,
      CatalogRepository repo,
      Settings settings,
      GitHubVersionService? versionService = null,
      NodeVersionRepository? versionRepo = null,
      AppLogger? logger = null);
  ```

- [ ] **Step 1: Write failing test for `CatalogRefreshService` logging**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceLoggingTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogRefreshServiceLoggingTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _tempRoot;
    private readonly Settings _settings;

    public CatalogRefreshServiceLoggingTests()
    {
        _db = new TestDb();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"catalog-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _settings = new Settings
        {
            QuerySources = new List<NodeSource>
            {
                new() { Name = "src", Url = "https://example.com/catalog.json" },
            },
            ActiveQuerySourceName = "src",
        };
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }
        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }
        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        {
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            return Task.FromResult(EntriesToReturn);
        }
    }

    [Fact]
    public async Task RefreshAsync_Success_Logs_Info_WithCounts()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-a" },
                new() { Id = Guid.NewGuid().ToString(), Package = "pkg-b" },
            },
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("完成") && l.Contains("entry_count=2"));
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_Logs_Warn()
    {
        var settingsNoSource = new Settings
        {
            QuerySources = new(),
            ActiveQuerySourceName = "nonexistent",
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            new FakeCatalogFetcher(),
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            settingsNoSource,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("WARN") && l.Contains("未配置查询源"));
    }

    [Fact]
    public async Task RefreshAsync_FetcherThrows_Logs_Error()
    {
        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };
        using var logger = new AppLogger(_tempRoot);
        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings,
            logger: logger);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        var lines = logger.ReadLines();
        Assert.Contains(lines, l => l.Contains("[catalog-refresh]") && l.Contains("ERROR") && l.Contains("dns fail"));
    }
}
```

- [ ] **Step 2: Run test, verify 3/3 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogRefreshServiceLoggingTests"
```
Expected: 3 errors — `CatalogRefreshService` ctor has no `logger:` named param OR compilation fails because log lines don't exist

- [ ] **Step 3: Update `CatalogFetcher.cs` ctor + add logger field + log fetch start/complete/failed**

Open `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs`. Modify:

**3a.** Add field + update ctor:

```csharp
    private readonly HttpClient _http;
    private readonly int _cacheTtlMinutes;
    private readonly AppLogger? _logger;

    public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60, AppLogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheTtlMinutes = cacheTtlMinutes;
        _logger = logger;
    }
```

**3b.** Wrap `FetchAsync` body with start/complete/failed logs (preserve existing logic):

```csharp
    public virtual async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("catalog-fetch", $"开始 fetch url={url}");
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            var rawArray = ExtractEntriesArray(root);

            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_cacheTtlMinutes);
            var entries = new List<CatalogEntry>();

            foreach (var element in rawArray.EnumerateArray())
            {
                string package = "";
                if (element.TryGetProperty("id", out var idProp))
                {
                    package = idProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("title", out var titleProp))
                {
                    package = titleProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package) &&
                    element.TryGetProperty("name", out var nameProp))
                {
                    package = nameProp.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(package))
                {
                    continue;
                }

                var rawMeta = ParseRawMetadata(element);

                entries.Add(new CatalogEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceUrl = url,
                    Package = package,
                    RawMetadata = rawMeta,
                    CachedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ExpiresAt = expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                });
            }

            _logger?.Info("catalog-fetch", $"完成 fetch count={entries.Count} duration_ms={sw.ElapsedMilliseconds} url={url}");
            return entries;
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-fetch", $"fetch 失败 url={url}", ex);
            throw;
        }
    }
```

- [ ] **Step 4: Update `CatalogRefreshService.cs` ctor + add logger field + log refresh start/complete/failed/no-source**

Open `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`. Modify:

**4a.** Add field + update ctor:

```csharp
    private readonly CatalogFetcher _fetcher;
    private readonly CatalogRepository _repo;
    private readonly NodeVersionRepository? _versionRepo;
    private readonly GitHubVersionService? _versionService;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;

    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings,
        GitHubVersionService? versionService = null,
        NodeVersionRepository? versionRepo = null,
        AppLogger? logger = null)
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
        _versionService = versionService;
        _versionRepo = versionRepo;
        _logger = logger;
    }
```

**4b.** Wrap `RefreshAsync` body with start/complete/failed/no-source logs:

```csharp
    public virtual async Task<RefreshResult> RefreshAsync(
        IProgress<CatalogEntry>? progress = null,
        IProgress<VersionFetchProgress>? versionProgress = null,
        CancellationToken ct = default)
    {
        var src = _settings.QuerySources
            .FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            _logger?.Warn("catalog-refresh",
                $"未配置查询源 active='{_settings.ActiveQuerySourceName}' query_sources_count={_settings.QuerySources.Count}");
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("catalog-refresh", $"开始 refresh url={src.Url} ttl={_settings.CatalogCacheTtlMinutes}min");

        int versionCount = 0;
        try
        {
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            var url = src.Url;
            var count = await Task.Run(() =>
            {
                foreach (var e in entries) e.SourceUrl = url;
                return _repo.UpsertBatch(entries,
                    e => progress?.Report(e));
            }, ct);

            if (_versionService is not null && !string.IsNullOrWhiteSpace(_settings.GitHubToken))
            {
                var nodes = entries
                    .Select(e => (e.Id, ReferenceUrl: ExtractReference(e)))
                    .Where(t => !string.IsNullOrWhiteSpace(t.ReferenceUrl))
                    .ToList();
                var versions = await _versionService.FetchVersionsAsync(
                    nodes, _settings.GitHubToken, versionProgress, ct);
                if (versions.Count > 0)
                {
                    versionCount = await Task.Run(() =>
                    {
                        if (_versionRepo is not null)
                        {
                            _versionRepo.UpsertBatch(
                                versions.SelectMany(kv =>
                                    kv.Value.Select(v => (kv.Key, v))));
                        }
                        return _repo.UpdateLatestVersions(
                            versions.Select(kv => (
                                kv.Key,
                                kv.Value.FirstOrDefault(v => !v.IsPrerelease)?.Tag
                                    ?? kv.Value.FirstOrDefault()?.Tag
                                    ?? "")));
                    }, ct);
                }
            }

            _logger?.Info("catalog-refresh",
                $"完成 refresh upsert_count={count} version_count={versionCount} duration_ms={sw.ElapsedMilliseconds}");
            return RefreshResult.Ok(count, versionCount);
        }
        catch (OperationCanceledException)
        {
            _logger?.Warn("catalog-refresh", "refresh 已取消");
            return RefreshResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            _logger?.Error("catalog-refresh", $"refresh 失败 url={src.Url}", ex);
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }
```

- [ ] **Step 5: Update `App.xaml.cs` — pass logger to catalog services**

Open `src-wpf/ComfyUI.Manager/App.xaml.cs`. Modify lines 98 and 103-104:

```csharp
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes, logger);
        var catalogCacheStore = new CatalogCacheStore();
        var catalogRepo = new CatalogRepository(catalogCacheStore);
        var githubVersionService = new GitHubVersionService(http);
        var nodeVersionRepo = new NodeVersionRepository(catalogCacheStore);
        var catalogRefreshService = new CatalogRefreshService(
            catalogFetcher, catalogRepo, settings, githubVersionService, nodeVersionRepo, logger);
```

- [ ] **Step 6: Run test, verify 3/3 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogRefreshServiceLoggingTests"
```
Expected: 3 PASS

- [ ] **Step 7: Run full suite, verify no regression**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: ~640 PASS / 0 FAIL / 1 SKIP(619 + 14 + 4 + 3)。既有 CatalogRefreshServiceTests / CatalogRefreshServiceNoTokenTests 必须 0 改动通过(它们的 ctor 调用仍兼容,因为 `logger` 是末尾 optional 参数)。

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs \
        src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceLoggingTests.cs
git commit -m "feat(wpf): CatalogFetcher/Refresh 接 AppLogger 诊断 (v0.6.7.4 T3)"
```

---

### Task 4: `CatalogViewModel` typed property + `CatalogView.xaml` 详情面板加 LastUpdate + Requirements Expander + 3 测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` (改 typed property,加 HasPipRequirements)
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` (加 LastUpdate TextBlock + Requirements Expander)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelRequirementsTests.cs`

**Interfaces:**
- Consumes: `CatalogEntry.PipRequirements` / `Author` / `Description` / `InstallType` / `Reference` / `LastUpdate` (T2)
- Produces:
  ```csharp
  // CatalogViewModel.cs
  public string? SelectedAuthor => _selected?.Author;
  public string? SelectedDescription => _selected?.Description;
  public string? SelectedInstallType => _selected?.InstallType;
  public string? SelectedReference => _selected?.Reference;
  public string? SelectedLastUpdate => _selected?.LastUpdate;
  public IReadOnlyList<PipRequirement> SelectedPipRequirements
      => _selected?.PipRequirements ?? Array.Empty<PipRequirement>();
  public bool HasPipRequirements => SelectedPipRequirements.Count > 0;
  ```

- [ ] **Step 1: Write failing test for `CatalogViewModel` typed properties**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelRequirementsTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelRequirementsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _projectRoot;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly FakeRefreshService _refreshService;
    private readonly NoopNodeOps _nodeOps;

    public CatalogViewModelRequirementsTests()
    {
        _db = new TestDb();
        _projectRoot = Path.Combine(Path.GetTempPath(), $"cat-vm-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _settings = new Settings();
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, _projectRoot);
        _settingsRepo = new SettingsRepository(
            Path.Combine(_projectRoot, "settings.json"));
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
        _refreshService = new FakeRefreshService();
        _nodeOps = new NoopNodeOps(
            new EnvironmentRepository(_db.Factory),
            new NodeRepository(_db.Factory),
            _settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(
            _catRepo, _versionRepo, _nodeOps, _refreshService,
            _settings, _settingsRepo, _projectRoot);

    private void SeedEntry(string package, Dictionary<string, object?> rawMetadata)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            RawMetadata = rawMetadata,
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        });
    }

    [Fact]
    public void SelectedPipRequirements_PopulatedFromDb()
    {
        SeedEntry("pkg-vm", new Dictionary<string, object?>
        {
            ["author"] = "alice",
            ["pip"] = new List<object?> { "numpy>=1.24.0", "huggingface-hub" },
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First();

        Assert.Equal(2, vm.SelectedPipRequirements.Count);
        Assert.Equal("numpy", vm.SelectedPipRequirements[0].Name);
        Assert.Equal(">=1.24.0", vm.SelectedPipRequirements[0].Specifier);
        Assert.Equal("huggingface-hub", vm.SelectedPipRequirements[1].Name);
        Assert.Null(vm.SelectedPipRequirements[1].Specifier);
    }

    [Fact]
    public void HasPipRequirements_True_WhenAny()
    {
        SeedEntry("pkg-pip", new Dictionary<string, object?>
        {
            ["pip"] = new List<object?> { "torch" },
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-pip");

        Assert.True(vm.HasPipRequirements);
    }

    [Fact]
    public void HasPipRequirements_False_WhenNoPipField()
    {
        SeedEntry("pkg-no-pip", new Dictionary<string, object?>
        {
            ["author"] = "bob",
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-no-pip");

        Assert.False(vm.HasPipRequirements);
        Assert.Empty(vm.SelectedPipRequirements);
    }

    /// <summary>
    /// Fake refresh service — 同 CatalogViewModelTests.FakeRefreshService pattern,
    /// 不真跑 fetch。继承 CatalogRefreshService 调 base(null fetcher, default settings)。
    /// </summary>
    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }
    }

    private sealed class NullCatalogFetcher : CatalogFetcher
    {
        public NullCatalogFetcher() : base(new System.Net.Http.HttpClient(), 60) { }
        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings) { }
    }
}
```

- [ ] **Step 2: Run test, verify 3/3 FAIL**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogViewModelRequirementsTests"
```
Expected: 3 errors — `HasPipRequirements` does not exist / `SelectedPipRequirements` does not exist

- [ ] **Step 3: Update `CatalogViewModel.cs` typed properties**

Open `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`. Replace lines 113-118 (the 6 `SelectedXxx` getters):

```csharp
    public string? SelectedTitle => _selected?.RawMetadata?.TryGetValue("title", out var t) == true ? t?.ToString() : _selected?.Package;
    public string? SelectedAuthor => _selected?.Author;
    public string? SelectedDescription => _selected?.Description;
    public string? SelectedInstallType => _selected?.InstallType;
    public string? SelectedReference => _selected?.Reference;
    public string? SelectedLastUpdate => _selected?.LastUpdate;
    public IReadOnlyList<PipRequirement> SelectedPipRequirements
        => _selected?.PipRequirements ?? Array.Empty<PipRequirement>();
    public bool HasPipRequirements => SelectedPipRequirements.Count > 0;
```

Add `using ComfyUI.Manager.Models;` if not already present(filename is `using ComfyUI.Manager.Models;` in line 9).

**3b.** Update `Selected` setter (line 73-92) — add 5 PropertyChanged notifications:

```csharp
    private CatalogEntry? _selected;
    public CatalogEntry? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RaisePropertyChanged(nameof(HasSelected));
                RaisePropertyChanged(nameof(SelectedReference));
                RaisePropertyChanged(nameof(SelectedReferenceUrl));
                RaisePropertyChanged(nameof(SelectedLatestVersion));
                RaisePropertyChanged(nameof(SelectedInstallType));
                RaisePropertyChanged(nameof(SelectedDescription));
                RaisePropertyChanged(nameof(SelectedAuthor));
                RaisePropertyChanged(nameof(SelectedTitle));
                RaisePropertyChanged(nameof(SelectedLastUpdate));
                RaisePropertyChanged(nameof(SelectedPipRequirements));
                RaisePropertyChanged(nameof(HasPipRequirements));
                LoadVersionsForSelected();
            }
        }
    }
```

- [ ] **Step 4: Update `CatalogView.xaml` — add LastUpdate TextBlock + Requirements Expander**

Open `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`. Find the right detail panel section (it's the `<StackPanel>` after the version ComboBox in the right `Grid.Column="2"` of the main Grid). Insert before the existing `Install type:` TextBlock (or after if `InstallType` is shown first):

```xaml
        <TextBlock Text="{Binding SelectedInstallType, StringFormat='安装类型: {0}'}"
                   Margin="0,4,0,0" FontSize="11" Foreground="Gray" />
        <TextBlock Text="{Binding SelectedLastUpdate, StringFormat='最后更新: {0}'}"
                   Margin="0,4,0,0" FontSize="11" Foreground="Gray" />

        <!-- Requirements 列表(G9:IsExpanded=False 默认折叠,避免详情面板过高) -->
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

- [ ] **Step 5: Run test, verify 3/3 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CatalogViewModelRequirementsTests"
```
Expected: 3 PASS

- [ ] **Step 6: Run full suite, verify no regression**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```
Expected: ~643 PASS / 0 FAIL / 1 SKIP(619 + 14 + 4 + 3 + 3)。既有 CatalogViewModelTests / CatalogViewModelDownloadTests 必须 0 改动通过(因为 Selected setter 扩展了 RaisePropertyChanged 列表,既有检验 SelectedReference 触发的测试不受影响;XAML 改动不在 VM 单元测试范围)。

- [ ] **Step 7: Build verify (sanity check XAML)**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal --nologo
```
Expected: 0 errors / 0 warnings

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/CatalogView.xaml \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelRequirementsTests.cs
git commit -m "feat(wpf): CatalogView typed fields + Requirements UI (v0.6.7.4 T4)"
```

---

### Task 5: 全量 verify + 重建 staging

**Files:** none

- [ ] **Step 1:** `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal --nologo` → 0 errors / 0 warnings
- [ ] **Step 2:** `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --nologo` → ~643 PASS / 0 FAIL / 1 SKIP(619 + 24)
- [ ] **Step 3:** 重建 staging per `feedback_staging_self_contained.md`:
  ```bash
  dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
  ```
- [ ] **Step 4:** `git status --short` → working tree clean(staging exe 时间戳变动 gitignored)
- [ ] **Step 5:** 无 v-bump / 无 zip(G5)
- [ ] **Step 6:** 写 `project_v0_6_7_4_catalog_requirements.md` 项目 memory + MEMORY.md entry(不动 long-term pattern)
- [ ] **Step 7:** Commit(若 memory 改动 + 任何 stale file)

---

## Risks

| 风险 | 缓解 |
|---|---|
| `EnsureColumn` 在用户首启时 ALTER TABLE 6 列,5000+ 行的 DB 几毫秒 | 启动时一次,跟 v0.6.5.7 BedStatus 同款 |
| `ExtractTypedFields` 每次 upsert 解析 pip list,5846 entries × 24 有 pip ≈ total 解析 < 1s | 一次 refresh,UX 可接受 |
| `pip_json` SQLite 不能直接 WHERE search | 不改 Search 行为,只 cache typed column |
| UI Expander 默认折叠,用户不会察觉 | GUI smoke 验证:点 entry → 展开 Requirements Expander |
| 测试 `FakeCatalogFetcher` derives CatalogFetcher,override `FetchAsync`(T3 跟 T2 重叠) | 既有 CatalogRefreshServiceTests 已有 FakeCatalogFetcher,直接复用同款 pattern |
| 测试 `HasPipRequirements_False_WhenEmpty` 走 reflection 拿私有 `_repo` | 不优雅但局部测试,后续 spec 加 VM test ctor helper 可消除 |

## Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/PipRequirement.cs` (new)
- `src-wpf/ComfyUI.Manager/Services/PipRequirementMatcher.cs` (new)
- `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs` (+10 lines)
- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` (+15 lines)
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` (+30 lines)
- `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs` (+20 lines)
- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` (+25 lines)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` (+2 lines)
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` (+15 lines)
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` (+20 lines)
- 7 new test files (~750 lines total)

---

## Execution choice

Plan complete and saved to `docs/superpowers/plans/2026-08-07-catalog-requirements.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
