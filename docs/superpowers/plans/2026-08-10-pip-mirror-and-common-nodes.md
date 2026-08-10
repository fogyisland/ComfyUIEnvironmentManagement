# v0.6.11++ 「Settings 加 pip 镜像源 + 常规节点配置」Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Settings 加两个独立但相关的配置项 — **Pip 镜像源**(PyPI 官方 / 清华 TUNA / 阿里云 / USTC / 自定义 URL,所有 ComfyUI / ComfyUI Manager 依赖安装都走这个镜像;BED 仍走 pytorch.org)+ **常规节点**(内置勾选列表 + 自由添加,env-create 末尾 + 装依赖末尾自动 clone 到 `<env>/ComfyUI/custom_nodes/`)。

**Architecture:**
- **Settings 单一来源** — `PipMirror` (string) + `PipMirrorCustomUrl` (string) + `CommonNodes` (List<CommonNodeEntry>) + `PipMirrorKind` enum + `CommonNodeEntry` model;JSON 持久化。
- **Pip 镜像作用面** — `RequirementsFileInstaller.InstallAsync` 末尾拼 `--index-url <url>` 到 pipArgs。`BaseEnvInstaller.BuildPipArgs()` 不动(继续发 `--index-url https://download.pytorch.org/whl/{cuda}`)。Lazy `Func<string?>?` 注入(每次调用重新求值 → Settings 改值后下次 pip 调用立即生效)。
- **CommonNodeInstaller** — `Func<string, IReadOnlyList<string>, Task<NodeOperationResult>>` 注入 git clone,遍历 enabled 节点 → 已装跳过 → 失败 WARN(不阻断 caller)。Idempotent(`--depth=1`)。
- **Hooks** — `EnvCreatorService.CreateAsync` 末尾(在 `envRepo.Upsert(env)` 之后)step 5.7 + `RequirementsInstaller.InstallAsync` 末尾(在 `AutoInstallComfyUiManagerAsync` 之后)都 best-effort 调 `CommonNodeInstaller.InstallEnabledAsync`。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · System.Text.Json (已有)· GitRunner (已有)· 手写 MVVM (ViewModelBase / RelayCommand)· 现有 Settings 持久化模式 + PipResult/NodeOperationResult 工厂

**base SHA:** `8b536b6`(v0.6.11+ ComfyUI Manager toggle T5 commit,test baseline 821 PASS / 2 FAIL flake / 1 SKIP)
**spec:** `docs/superpowers/specs/2026-08-10-pip-mirror-and-common-nodes-design.md`

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | **Settings 走 JSON 持久化** — 新字段加在 `Settings.cs`,现有 `SettingsRepository.Load/Save` 自动覆盖。无需迁移代码(读时 `Enum.TryParse` 失败回退官方;读 `CommonNodes` 走 `?? new()`)。 | 现有约定 |
| **G2** | **WPF Setter + DynamicResource hard rule** — 所有新 `Setter` 用 property-element + `DynamicResource` 形式(不 `Setter Property="..." Value="{StaticResource ...}"`)。v0.6.9.2 教训。 | MEMORY |
| **G3** | **Lazy mirror 解析** — `RequirementsFileInstaller` 接 `Func<string?>?`,不接 Settings 直接引用。Settings 改值后下次调用立即生效(不用重启)。 | 用户期望 |
| **G4** | **Pip 镜像不影响 BED** — `BaseEnvInstaller.BuildPipArgs()` 不动(继续发 pytorch.org CUDA wheels)。`RequirementsFileInstaller` 接受 `--index-url` 参数但 BED 不经过 `RequirementsFileInstaller`。 | 用户决策 |
| **G5** | **CommonNodeInstaller 是 best-effort** — 不阻断 caller(env-create / 装依赖)。失败逐节点 WARN + status panel `warn:` 行,caller 仍返回 success。 | 跟 T5 AutoInstall ComfyUI Manager 同模式 |
| **G6** | **Idempotent 安装** — dir 已存在则跳过,不 `git pull`。clone 只走 `--depth=1`(浅克隆,省时间)。 | 用户决策 |
| **G7** | **不引入新依赖** — 复用 `GitRunner` / `RequirementsFileInstaller` / `NodeOperationResult` 等现有基建。 | 项目惯例 |
| **G8** | **测试覆盖** — PipMirrorResolver 8 单元测试 + RequirementsFileInstaller mirror passthrough 3 测试 + CommonNodeInstaller 5 测试 + Settings UI 集成(走现有 SettingsViewModel 测试 pattern)+ EnvCreatorService / RequirementsInstaller ctor 适配 + 全套 baseline 不退化。 | 项目惯例 |
| **G9** | **每 task 单独 commit + 单独 SDD subagent dispatch + task reviewer**,严格匹配 `progress.md` ledger。 | SDD 流程 |
| **G10** | **不做无关重构** — 不重命名公开 API;不调整既有 Settings 字段顺序。 | 项目惯例 |
| **G11** | **Built-in 节点不可删** — `IsBuiltIn=true` 的条目 UI 上删除按钮禁用(避免用户破坏 curated 列表)。仍可取消勾选 Enabled=false(等价于"不装")。 | 用户期望 |
| **G12** | **User-added 节点 Id 必须含 `/`** — UI 添加表单校验,不通过则红字提示 + 不加入列表。 | 防错 |
| **G13** | **SettingsDefaults.Apply 首次启动种 curated 列表** — 只在 `CommonNodes.Count == 0` 时 seed,保护用户的清空操作。 | 防覆盖 |

---

## File Structure

**Modified (8 production + 3 test):**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ PipMirrorKind enum + PipMirror + PipMirrorCustomUrl + CommonNodeEntry + CommonNodes)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ PipMirror/IsCustomPipMirrorSelected/CommonNodes ObservableCollection/AddCommonNode/RemoveCommonNode + form fields)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ Section "Pip 镜像" + Section "常规节点")
- `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs` (ctor 加 Func<string?>? + InstallAsync 末尾拼接 --index-url)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` (ctor 加 CommonNodeInstaller + CreateAsync 末尾 step 5.7 best-effort hook)
- `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (ctor 加 CommonNodeInstaller + InstallAsync 末尾 best-effort hook)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` (构造 CommonNodeInstaller + 注入 EnvCreatorService/RequirementsInstaller ctor + 改 RequirementsFileInstaller 注入 resolveIndexUrl lambda)
- `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` (Apply 末尾种 CommonNodes 10 个 curated)
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs` (+ 3 mirror passthrough tests)
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs` (FakeRequirementsInstaller ctor + override AutoInstallCommonNodes)
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs` (传 CommonNodeInstaller null ctor arg 或 opt-in fake)

**Created (4 files):**
- `src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs` (静态 helper)
- `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs` (sealed service)
- `tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs` (8 tests)
- `tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs` (5 tests)

---

## Task 1: Pip 镜像 (Settings 字段 + UI + 服务 + DI)

**Files (T1):**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ `PipMirrorKind` enum,`PipMirror` string 默认 `"official"`,`PipMirrorCustomUrl` string 默认 `""`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ `PipMirror` / `PipMirrorCustomUrl` properties + `PipMirrorKinds` List + `IsCustomPipMirrorSelected` computed + `RaiseAllPropertiesChanged` 加新)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ Section "Pip 镜像" + ComboBox 5 选项 + TextBox Custom URL(Visible only when `IsCustomPipMirrorSelected`)+ 灰色说明)
- Create: `src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs` (静态 helper,`ResolveIndexUrl(Settings) → string?` + `BuildPipArgs(Settings) → IReadOnlyList<string>`)
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs` (ctor 加 `Func<string?>? resolveIndexUrl = null` 参;InstallAsync 末尾把 `--index-url <url>` 拼到 pipArgs,只在 func 返回非 null 时)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (reqFileInstaller 构造改成 `new RequirementsFileInstaller(() => PipMirrorResolver.ResolveIndexUrl(settings))`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs` (8 测试:Official→null,TUNA/Aliyun/USTC→对应 URL,Custom+URL→trimmed,Custom+empty→null,Garbage→null,BuildPipArgs 空 vs 2 元素)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs` (3 新测试:null Func 不传 --index-url,Func 返回 URL 时拼接,Func 多次调用 live 重新求值)

**Interfaces produced (T1):**
- `enum PipMirrorKind { Official, TsinghuaTuna, Aliyun, USTC, Custom }` (in `Models/Settings.cs`)
- `class Settings { string PipMirror; string PipMirrorCustomUrl; }` (new fields)
- `static class PipMirrorResolver { string? ResolveIndexUrl(Settings s); IReadOnlyList<string> BuildPipArgs(Settings s); }`
- `sealed class RequirementsFileInstaller { ctor(Func<string?>? resolveIndexUrl = null); }`

### Task 1 Steps

- [ ] **Step 1: Write failing PipMirrorResolver tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class PipMirrorResolverTests
{
    private static Settings S(string mirror, string customUrl = "")
        => new Settings { PipMirror = mirror, PipMirrorCustomUrl = customUrl };

    [Fact]
    public void ResolveIndexUrl_Official_ReturnsNull()
    {
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("official")));
    }

    [Fact]
    public void ResolveIndexUrl_TsinghuaTuna_ReturnsTunaUrl()
    {
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple",
            PipMirrorResolver.ResolveIndexUrl(S("tsinghua_tuna")));
    }

    [Fact]
    public void ResolveIndexUrl_Aliyun_ReturnsAliyunUrl()
    {
        Assert.Equal("https://mirrors.aliyun.com/pypi/simple/",
            PipMirrorResolver.ResolveIndexUrl(S("aliyun")));
    }

    [Fact]
    public void ResolveIndexUrl_USTC_ReturnsUstcUrl()
    {
        Assert.Equal("https://pypi.mirrors.ustc.edu.cn/simple/",
            PipMirrorResolver.ResolveIndexUrl(S("ustc")));
    }

    [Fact]
    public void ResolveIndexUrl_CustomWithUrl_ReturnsTrimmedUrl()
    {
        Assert.Equal("https://pypi.doubanio.com/simple",
            PipMirrorResolver.ResolveIndexUrl(S("custom", "  https://pypi.doubanio.com/simple  ")));
    }

    [Fact]
    public void ResolveIndexUrl_CustomWithEmptyUrl_ReturnsNull()
    {
        // 选了 custom 但 URL 没填 → 视为未设,走官方(不传 --index-url)
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("custom", "")));
    }

    [Fact]
    public void ResolveIndexUrl_GarbageValue_ReturnsNull()
    {
        // 未来加新 enum 值前用户可能手改 settings.json 写成无效字符串 → 回退官方
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("not_a_real_mirror")));
    }

    [Fact]
    public void BuildPipArgs_Official_IsEmpty()
    {
        var args = PipMirrorResolver.BuildPipArgs(S("official"));
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_TsinghuaTuna_IsTwoElements()
    {
        var args = PipMirrorResolver.BuildPipArgs(S("tsinghua_tuna"));
        Assert.Equal(2, args.Count);
        Assert.Equal("--index-url", args[0]);
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple", args[1]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (build fails: PipMirrorKind / PipMirror / PipMirrorCustomUrl / PipMirrorResolver don't exist)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PipMirrorResolverTests" -v minimal`
Expected: BUILD FAILS with "type or namespace PipMirrorResolver could not be found" or "Settings does not contain a definition for PipMirror".

- [ ] **Step 3: Add `PipMirrorKind` enum + fields to `Settings.cs`**

Edit `src-wpf/ComfyUI.Manager/Models/Settings.cs` — add enum + fields after the `CatalogViewMode` enum block and before the `class Settings { ... }` line. Insert the enum and add fields to the end of the class. Insert near other enums (line 7-11) after `CatalogViewMode`:

```csharp
// v0.6.11++ pip mirror:用户选 global pip 镜像(影响 ComfyUI/Manager 依赖安装,
// BED 不受影响 — 走 pytorch.org)。string 持久化以便老 settings.json 容错:
// 读时若枚举值不认识 → 回退 "official"(G3)。
public enum PipMirrorKind
{
    Official,
    TsinghuaTuna,
    Aliyun,
    USTC,
    Custom,
}
```

Append to the end of `class Settings` (before the closing `}` at line 92):

```csharp
    // v0.6.11++ pip mirror
    [JsonPropertyName("pip_mirror")] public string PipMirror { get; set; } = "official";
    [JsonPropertyName("pip_mirror_custom_url")] public string PipMirrorCustomUrl { get; set; } = "";
```

(2 字段 — JSON 字段名 snake_case 跟 project 现有约定对齐;默认值保证老 settings.json 缺字段时直接走官方。)

- [ ] **Step 4: Re-run PipMirrorResolver tests to confirm partial pass / still failing on PipMirrorResolver**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PipMirrorResolverTests" -v minimal`
Expected: BUILD FAILS (PipMirrorResolver missing),但 9 个 test 文件能编译 Settings 部分;接下来 Step 5 加 PipMirrorResolver 后再 build 通过。

- [ ] **Step 5: Create `PipMirrorResolver.cs` static helper**

Create `src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs`:

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11++:把 <see cref="Settings.PipMirror"/> (string) + <see cref="Settings.PipMirrorCustomUrl"/>
/// 解析成实际 pip 参数。string → enum 走 <c>Enum.TryParse</c> 失败回退 <see cref="PipMirrorKind.Official"/>,
/// 让未来 enum 加新值前已存在的 settings.json 不会崩(G3)。
/// </summary>
public static class PipMirrorResolver
{
    public const string TsinghuaTunaUrl = "https://pypi.tuna.tsinghua.edu.cn/simple";
    public const string AliyunUrl = "https://mirrors.aliyun.com/pypi/simple/";
    public const string USTCUrl = "https://pypi.mirrors.ustc.edu.cn/simple/";

    /// <summary>
    /// 根据 <see cref="Settings.PipMirror"/> 解析出 PyPI index URL。
    /// 返回 <c>null</c> 表示"走官方"(不传 <c>--index-url</c>)。
    /// </summary>
    public static string? ResolveIndexUrl(Settings settings)
    {
        if (settings is null) return null;

        // 解析枚举值(容错:无效字符串 → 官方)
        if (!System.Enum.TryParse<PipMirrorKind>(settings.PipMirror, ignoreCase: true, out var kind))
        {
            return null;
        }

        return kind switch
        {
            PipMirrorKind.Official => null,
            PipMirrorKind.TsinghuaTuna => TsinghuaTunaUrl,
            PipMirrorKind.Aliyun => AliyunUrl,
            PipMirrorKind.USTC => USTCUrl,
            PipMirrorKind.Custom => string.IsNullOrWhiteSpace(settings.PipMirrorCustomUrl)
                ? null
                : settings.PipMirrorCustomUrl.Trim(),
            _ => null,
        };
    }

    /// <summary>
    /// 把 mirror URL 包装成 pip 参数列表(<c>--index-url &lt;url&gt;</c>)。
    /// <see cref="ResolveIndexUrl"/> 返 <c>null</c> → 返空列表(caller 直接 append)。
    /// </summary>
    public static IReadOnlyList<string> BuildPipArgs(Settings settings)
    {
        var url = ResolveIndexUrl(settings);
        if (url is null) return System.Array.Empty<string>();
        return new[] { "--index-url", url };
    }
}
```

- [ ] **Step 6: Run PipMirrorResolver tests to verify all 9 pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PipMirrorResolverTests" -v minimal`
Expected: 9 PASS / 0 FAIL / 0 SKIP.

- [ ] **Step 7: Write failing RequirementsFileInstaller mirror passthrough tests**

Append to `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs` (end of class, before final `private static string? FindPython()`):

```csharp
    // ===== v0.6.11++ pip mirror passthrough =====

    [Fact]
    public async Task InstallAsync_NullResolveFunc_DoesNotAppendIndexUrl()
    {
        // Func=null → 走默认(无 --index-url)
        var reqPath = Path.Combine(_tempRoot, "requirements-mirror-null.txt");
        File.WriteAllLines(reqPath, new[] { "SQLAlchemy" });
        var filteredPath = Path.Combine(_tempRoot, "req-mirror-null-" + RequirementsFileInstaller.FilteredRequirementsFileName);

        // 用 FakeProcessRunner-style 探针做不到(RunPipAsync 是 private static),
        // 这里直接调 InstallAsync,跑假 python(机器上没 python 会被 Process.Start catch
        // 吞掉抛 InvalidOperationException,我们的镜像参数检查点放在 args list)。
        // 简单方法:解析 RunPipAsync 的实现不能,我们用 Expose Pips 模式:
        // 改用 Public TestSurface(PipArgTrace)→ RequirementsFileInstaller 接受
        // Action<string> arg-trace;这里验证:Func 返 URL → 出现在 pip args。
        var installer = new RequirementsFileInstaller(resolveIndexUrl: null);
        // 用一个根本不存在的 python 触发异常,异常信息含完整命令行 → 我们验证
        // 异常信息里没有 --index-url。
        try
        {
            await installer.InstallAsync(
                reqPath, filteredPath, "definitely-no-such-python.exe", null, CancellationToken.None);
        }
        catch (Exception)
        {
            // 期待:真跑失败但异常或 result 不含 --index-url
        }
        // 简化验证:不依赖异常信息,改通过 install result 是"pip 启动失败"路径而非"pip 用了 --index-url"
        // 这里不强断言(下两个 test 用更直接的方法验证)
    }

    [Fact]
    public async Task InstallAsync_ResolveFuncReturnsUrl_PassesItToPip_AsIndexUrl()
    {
        // 验证镜像 URL 真的被拼到 pip 命令行
        var reqPath = Path.Combine(_tempRoot, "requirements-mirror-pass.txt");
        File.WriteAllLines(reqPath, new[] { "SQLAlchemy" });
        var filteredPath = Path.Combine(_tempRoot, "req-mirror-pass-" + RequirementsFileInstaller.FilteredRequirementsFileName);

        // 用 `where python` 之类的方式找一个真 python,跑 InstallAsync
        var pyExe = FindPython();
        if (pyExe is null) return;  // 机器没 python 跳过

        var installer = new RequirementsFileInstaller(
            resolveIndexUrl: () => "https://pypi.tuna.tsinghua.edu.cn/simple");
        var result = await installer.InstallAsync(
            reqPath, filteredPath, pyExe, line => { }, CancellationToken.None);

        // result 是 success / fail 都行(没网会 fail);重要的是 not be cancelled 且没"参数无效"错误。
        // 我们不强验证 pip 真的发了 --index-url(那要 mock process);改验不抛 ArgEx。
        Assert.NotEqual("参数无效", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_ResolveFuncReturnsNull_DoesNotAppendIndexUrl()
    {
        // Func 返 null → 走官方(不传 --index-url)
        var reqPath = Path.Combine(_tempRoot, "requirements-mirror-official.txt");
        File.WriteAllLines(reqPath, new[] { "SQLAlchemy" });
        var filteredPath = Path.Combine(_tempRoot, "req-mirror-official-" + RequirementsFileInstaller.FilteredRequirementsFileName);

        var pyExe = FindPython();
        if (pyExe is null) return;

        var installer = new RequirementsFileInstaller(
            resolveIndexUrl: () => null);
        var result = await installer.InstallAsync(
            reqPath, filteredPath, pyExe, line => { }, CancellationToken.None);

        Assert.NotEqual("参数无效", result.Reason);
    }
```

> **NOTE to implementer:** 上述 3 个 test 故意只 assert "不抛 / 异常信息不是参数无效"。严格的 pip args capture 需要重构 `RunPipAsync` 为 instance/virtual + 抽 `BuildPipArgs` 公共方法,这是过度设计。Step 9 的实现改用更直接的策略 — 抽 `internal static IReadOnlyList<string> BuildPipArgs(Func<string?>? resolveIndexUrl)` 静态方法,然后用 step 8 的 unit test 覆盖它(覆盖 `ResolveFuncReturnsUrl → ["--index-url", "..."]` / `ResolveFuncReturnsNull → []` / `ResolveFuncNull → []`)。Step 7 这 3 个 test 在 Step 9 之后改写为 unit test 直接调 `BuildPipArgs` — 详 Step 8 替代。

- [ ] **Step 8: Replace Step 7 tests with cleaner `BuildPipArgs` unit test**

Replace the entire Step 7 added code with this:

```csharp
    // ===== v0.6.11++ pip mirror passthrough (G3: lazy via Func<string?>) =====

    [Fact]
    public void BuildPipArgs_ResolveFuncNull_ReturnsEmpty()
    {
        var args = RequirementsFileInstaller.BuildPipArgs(resolveIndexUrl: null);
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncReturnsUrl_AppendsIndexUrlPair()
    {
        var args = RequirementsFileInstaller.BuildPipArgs(
            () => "https://pypi.tuna.tsinghua.edu.cn/simple");
        Assert.Equal(2, args.Count);
        Assert.Equal("--index-url", args[0]);
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple", args[1]);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncReturnsNull_ReturnsEmpty()
    {
        // Func 存在但返 null(走官方 / 选 custom 但 URL 空)→ 不拼 --index-url
        var args = RequirementsFileInstaller.BuildPipArgs(() => null);
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncInvokedEachCall_NotCached()
    {
        // G3 强约束:每次 BuildPipArgs 调用都重求值 Func(不缓存),
        // 所以 Settings 在调用之间改值能立即生效。
        int callCount = 0;
        var args1 = RequirementsFileInstaller.BuildPipArgs(() => { callCount++; return "https://first"; });
        var args2 = RequirementsFileInstaller.BuildPipArgs(() => { callCount++; return "https://second"; });
        Assert.Equal("https://first", args1[1]);
        Assert.Equal("https://second", args2[1]);
        Assert.Equal(2, callCount);
    }
```

(4 test 而非 3 — 加 G3 lazy 不缓存的语义测试。)

- [ ] **Step 9: Implement RequirementsFileInstaller ctor + BuildPipArgs + use in RunPipAsync**

Edit `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`:

a) **Add ctor** after the `FilteredRequirementsFileName` const (line 30) and before the regex:

```csharp
    private readonly Func<string?>? _resolveIndexUrl;

    /// <summary>
    /// v0.6.11++ pip mirror:lazy 解析器,每次 <see cref="InstallAsync"/> 调用时
    /// 重新求值,保证 Settings 改值后下次 pip 调用立即生效(G3)。
    /// 返 <c>null</c> / 选官方 → 不拼 <c>--index-url</c>(走官方 PyPI)。
    /// BED 不走本 ctor,继续发 pytorch.org CUDA wheels(G4)。
    /// </summary>
    public RequirementsFileInstaller(Func<string?>? resolveIndexUrl = null)
    {
        _resolveIndexUrl = resolveIndexUrl;
    }

    /// <summary>
    /// 把镜像 URL 包装成 pip 参数片段。<c>resolveIndexUrl</c> 为 null 或返 null → 返空列表。
    /// 暴露为 internal static 便于 <c>RequirementsFileInstallerTests</c> 单元测;
    /// 生产代码 <see cref="InstallAsync"/> 内部直接调。
    /// </summary>
    internal static IReadOnlyList<string> BuildPipArgs(Func<string?>? resolveIndexUrl)
    {
        if (resolveIndexUrl is null) return System.Array.Empty<string>();
        var url = resolveIndexUrl();
        if (string.IsNullOrWhiteSpace(url)) return System.Array.Empty<string>();
        return new[] { "--index-url", url };
    }
```

b) **Update `InstallAsync`** to use the new BuildPipArgs. Replace line 104-108:

```csharp
        var pipArgs = new List<string> { "install", "-r", filteredOutputPath, "--disable-pip-version-check" };
        var mirrorArgs = BuildPipArgs(_resolveIndexUrl);
        if (mirrorArgs.Count > 0) pipArgs.AddRange(mirrorArgs);

        var pipResult = await RunPipAsync(
            venvPythonPath,
            pipArgs,
            onLine ?? (_ => { }),
            ct);
```

(把原 `new[] {...}` 内联挪走,改成可追加 list。BuildPipArgs 是 internal static → test 可见。)

- [ ] **Step 10: Run RequirementsFileInstaller tests to verify all pass (existing + 4 new)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsFileInstaller" -v minimal`
Expected: ALL PASS(existing 5 tests + 4 new = 9 PASS / 0 FAIL / 0 SKIP — 注意 `InstallAsync_PipSucceeds_WritesFilteredFileThenCleansUp` 这类需要真 python 的 test 在没 python 机器上自身有 `if (pyExe is null) return;` 短路 — 跟 G8 baseline 行为一致。)

- [ ] **Step 11: Add PipMirror + PipMirrorCustomUrl properties + computed IsCustomPipMirrorSelected to `SettingsViewModel`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:

a) **Append** after the `FetchNodeVersionsOnRefresh` property (line 290, before "// —— 路径 ——" comment at line 292):

```csharp
    // v0.6.11++ pip mirror
    public List<string> PipMirrorKinds { get; } = new()
    {
        "official", "tsinghua_tuna", "aliyun", "ustc", "custom",
    };
    public string PipMirror
    {
        get => _settings.PipMirror;
        set
        {
            _settings.PipMirror = value ?? "official";
            _repo.Save(_settings);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsCustomPipMirrorSelected));
        }
    }
    public string PipMirrorCustomUrl
    {
        get => _settings.PipMirrorCustomUrl;
        set
        {
            _settings.PipMirrorCustomUrl = value ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }
    public bool IsCustomPipMirrorSelected
        => string.Equals(_settings.PipMirror, "custom", System.StringComparison.OrdinalIgnoreCase);
```

b) **Append** to `RaiseAllPropertiesChanged` (after line 626 `RaisePropertyChanged(nameof(FetchNodeVersionsOnRefresh));`):

```csharp
        RaisePropertyChanged(nameof(PipMirror));
        RaisePropertyChanged(nameof(PipMirrorCustomUrl));
        RaisePropertyChanged(nameof(IsCustomPipMirrorSelected));
```

- [ ] **Step 12: Add "Pip 镜像" Section to `SettingsView.xaml`**

Edit `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — insert AFTER the `<!-- ============ 路径 ============ -->` line (line 190) and BEFORE the "模板 Python 目录" subsection. The new section goes between "SectionPaths" header and the path content:

```xaml
            <!-- ============ v0.6.11++ Pip 镜像 ============ -->
            <TextBlock x:Name="SectionPipMirror" Text="Pip 镜像" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
            <TextBlock Text="选择 pip 镜像源(影响 ComfyUI / ComfyUI Manager 依赖安装,BED 仍走 pytorch.org)"
                       FontSize="11" Foreground="Gray" Margin="0,0,0,8" TextWrapping="Wrap" MaxWidth="480"
                       HorizontalAlignment="Left" />
            <TextBlock Text="镜像源" Margin="0,8,0,4" />
            <ComboBox ItemsSource="{Binding PipMirrorKinds}"
                      SelectedItem="{Binding PipMirror, Mode=TwoWay}"
                      Width="240" HorizontalAlignment="Left" />
            <Grid Margin="0,8,0,0"
                  Visibility="{Binding IsCustomPipMirrorSelected,
                                Converter={StaticResource BoolToVisibility}}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="160" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="自定义 URL" VerticalAlignment="Center" />
                <TextBox Grid.Column="1" Style="{StaticResource MaterialTextBox}" Margin="8,0,0,0"
                         Text="{Binding PipMirrorCustomUrl, UpdateSourceTrigger=PropertyChanged}" />
            </Grid>
            <TextBlock Text="官方(不传 --index-url)|清华 TUNA|阿里云|USTC|自定义。" Foreground="Gray" FontSize="11"
                       Margin="0,4,0,0" TextWrapping="Wrap" MaxWidth="480" HorizontalAlignment="Left" />
```

(ComboBox DisplayMemberPath 默认用 ItemsSource string 本身的 ToString;ComboBox ItemTemplate 不必显式,跟现有"Python 解释器"区段风格一致。)

- [ ] **Step 13: Update `App.xaml.cs` to inject resolveIndexUrl lambda + add new section scroll target**

a) Edit `App.xaml.cs` line 165 (`var reqFileInstaller = new RequirementsFileInstaller();`) to:

```csharp
        // v0.6.5.x hotfix:Env 删除跑腿 service(stop running + 删目录 + 删 SQLite 行)。
        // 复用 envRepo 跟 _launcher,跟 EnvironmentListView 共一份。
        var envDeleter = new EnvDeleterService(envRepo, _launcher);
        // v0.6.5.12 + v0.6.11+: 装依赖 helper(过滤 torch 行 + 写 filtered + 跑 pip)。
        // 抽出 helper 给 RequirementsInstaller(ComfyUI 依赖)和 ComfyUIManagerInstaller
        // (ComfyUI-Manager 自己的依赖)两边复用,避免 30 行过滤逻辑复制。
        // v0.6.11++:注入 lazy mirror 解析器 → 每次 InstallAsync 调用时重新求值,
        // Settings 改值后下次 pip 调用立即生效(G3)。
        var reqFileInstaller = new RequirementsFileInstaller(
            resolveIndexUrl: () => PipMirrorResolver.ResolveIndexUrl(settings));
        // v0.6.11+ T2: ComfyUI Manager 装/卸 service(env-list toggle 按钮 + 装依赖末尾自动装)。
        // 复用 reqFileInstaller 跑 Manager 自己的 requirements.txt;git 走共享的 gitExe + GitRunner。
        var comfyUiManagerInstaller = new ComfyUIManagerInstaller(reqFileInstaller, gitExe, gitProxy, logger);
        var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller, comfyUiManagerInstaller);
```

(改动只 1 行 — reqFileInstaller 构造加 `resolveIndexUrl:` lambda;T3 会在 `requirementsInstaller` 构造后追加第 4 参 CommonNodeInstaller,本 T1 不动。)

b) 验证 T1 不影响 v0.6.9 T7 Spotlight 引用 SectionPipMirror — 暂不更新 ScrollToSection 列表,T3 可选补。

- [ ] **Step 14: Run full test suite to verify no regression**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: 821+/0/1 baseline + 13 new tests(9 PipMirrorResolver + 4 BuildPipArgs unit)= 834/2/1(2 FAIL 是 pre-existing `ProcessLauncherProgressTests` 已知 flake)。

- [ ] **Step 15: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs \
        src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs
git commit -m "feat(wpf): add pip mirror setting + lazy resolution in RequirementsFileInstaller"
```

(commit message: `feat(wpf): add pip mirror setting + lazy resolution in RequirementsFileInstaller`)

---

## Task 2: 常规节点 (Settings 字段 + UI + 服务)

**Files (T2):**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ `CommonNodeEntry` class [Id/DisplayName/IsBuiltIn/Enabled] + `CommonNodes` List<CommonNodeEntry>)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` (`Apply` 末尾 `s.CommonNodes = SeedCommonNodesIfEmpty(s.CommonNodes)`,10 个 curated entries)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ `CommonNodes` ObservableCollection + `AddCommonNodeCommand` / `RemoveCommonNodeCommand`(gated `!IsBuiltIn`)+ `NewCommonNodeId` / `NewCommonNodeDisplayName` 表单字段 + 校验 Id 含 `/` + `AddCommonNodeError` 错误显示)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ Section "常规节点": ItemsControl(CheckBox + DisplayName + Id + 删除按钮[gated `!IsBuiltIn`])+ "添加节点" inline form + Id/DisplayName TextBox + Id 校验错误显示)
- Create: `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs` (`InstallEnabledAsync(env, progress, ct)` — 遍历 enabled 节点,idempotent 跳过已装,失败逐节点 WARN,aggregate 结果)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs` (5 测试:empty list,无 ComfyuiSource,正常 clone,部分失败 aggregate,取消 throw)

**Curated built-in list (10 entries):**
```
ComfyUI-Manager (ltdrdata/ComfyUI-Manager)
ComfyUI-Impact-Pack (ltdrdata/ComfyUI-Impact-Pack)
ComfyUI-Inspire-Pack (ltdrdata/ComfyUI-Inspire-Pack)
ComfyUI-Custom-Scripts (pythongosssss/ComfyUI-Custom-Scripts)
rgthree-comfy (rgthree/rgthree-comfy)
efficiency-nodes-comfyui (jags111/efficiency-nodes-comfyui)
ComfyUI-VideoHelperSuite (Kosinkadink/ComfyUI-VideoHelperSuite)
ComfyUI-KJNodes (kijai/ComfyUI-KJNodes)
ComfyUI-Florence2 (kijai/ComfyUI-Florence2)
ComfyUI-Advanced-ControlNet (Kosinkadink/ComfyUI-Advanced-ControlNet)
```

**Interfaces produced (T2):**
- `class CommonNodeEntry { string Id; string DisplayName; bool IsBuiltIn; bool Enabled; }`
- `class Settings { List<CommonNodeEntry> CommonNodes; }`
- `sealed class CommonNodeInstaller { ctor(Settings settings, Func<string, IReadOnlyList<string>, Task<NodeOperationResult>> gitClone, AppLogger? logger = null); Task<NodeOperationResult> InstallEnabledAsync(Environment env, IProgress<string>? progress, CancellationToken ct); }`

### Task 2 Steps

- [ ] **Step 1: Add `CommonNodeEntry` class + `CommonNodes` field to `Settings.cs`**

Edit `src-wpf/ComfyUI.Manager/Models/Settings.cs`:

a) **Append** to the end of `class Settings` (after the PipMirror fields added in T1 Step 3, before class closing `}`):

```csharp
    // v0.6.11++ common nodes:env-create / 装依赖末尾自动 clone 的一组非冲突常用节点
    [JsonPropertyName("common_nodes")] public List<CommonNodeEntry> CommonNodes { get; set; } = new();
```

b) **Append** to end of file (after `class ExtraPath` closing `}` at line 104):

```csharp
public class CommonNodeEntry
{
    // GitHub "owner/repo" 形式(e.g. "ltdrdata/ComfyUI-Manager")。
    // User-added 节点 Id 必须含 "/" — UI 表单校验(G12)。
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    // UI 显示用(不参与 git clone)。curated list 给用户友好名;user-added 可空 → fallback Id。
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    // 区分 curated seed(G11 不可删)跟 user-added(可删)。
    [JsonPropertyName("is_built_in")] public bool IsBuiltIn { get; set; }
    // 勾选状态 — 取消勾选 = "不装"(等价 skip)。built-in 也能关 enabled。
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 2: Write 5 failing `CommonNodeInstallerTests`**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class CommonNodeInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public CommonNodeInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"commonnode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static Settings S(params (string id, bool enabled)[] nodes)
    {
        var list = new List<CommonNodeEntry>();
        foreach (var (id, enabled) in nodes)
        {
            list.Add(new CommonNodeEntry { Id = id, DisplayName = id, IsBuiltIn = true, Enabled = enabled });
        }
        return new Settings { CommonNodes = list };
    }

    private static Environment EnvWithComfyui(string root, string comfyuiSource)
    {
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes"));
        return new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = root,
            ComfyuiSource = comfyuiSource,
            CustomNodesPath = Path.Combine(comfyuiSource, "custom_nodes"),
        };
    }

    [Fact]
    public async Task InstallEnabledAsync_EmptyList_ReturnsOkAndCallsNothing()
    {
        var settings = S();  // 空 list
        var invocations = new List<(string, IReadOnlyList<string>)>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add((id, args));
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_NullComfyuiSource_ReturnsFailAndCallsNothing()
    {
        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = _tempRoot,
            ComfyuiSource = null,  // 没 Comfyui 源
            CustomNodesPath = Path.Combine(_tempRoot, "custom_nodes"),
        };
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ComfyuiSource", result.Reason ?? "");
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_DisabledNodesSkipped()
    {
        // enabled=false 视为 "不装",跟"已装"同等待遇 — 直接跳过
        var settings = S(
            ("ltdrdata/ComfyUI-Manager", false),
            ("ltdrdata/ComfyUI-Impact-Pack", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(invocations);
        Assert.Contains("ltdrdata/ComfyUI-Impact-Pack", invocations[0]);
    }

    [Fact]
    public async Task InstallEnabledAsync_AlreadyInstalled_Skipped()
    {
        // dir 已存在 → 跳过(不调 git clone,不 git pull)— G6 idempotent
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));

        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, comfyuiSource);
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_PartialFailure_AggregatesResult()
    {
        // 一个成功 + 一个失败 → 整体 Success=false 但成功的依然装了(G5 best-effort)
        var settings = S(
            ("ltdrdata/ComfyUI-Manager", true),
            ("ltdrdata/ComfyUI-Impact-Pack", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            if (id == "ltdrdata/ComfyUI-Manager")
                return Task.FromResult(NodeOperationResult.Ok("ok"));
            return Task.FromResult(NodeOperationResult.Fail("git clone 失败"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);  // 部分失败 → 整体 fail
        Assert.Equal(2, invocations.Count);  // 两个都尝试
    }
}
```

- [ ] **Step 3: Run tests to verify they fail (CommonNodeInstaller doesn't exist)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CommonNodeInstallerTests" -v minimal`
Expected: BUILD FAILS with "CommonNodeInstaller could not be found".

- [ ] **Step 4: Create `CommonNodeInstaller.cs`**

Create `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11++:env-create / 装依赖末尾自动装用户在 Settings 勾选的一组「常用节点」。
/// 行为:
/// - 遍历 <see cref="Settings.CommonNodes"/> 里 <c>Enabled=true</c> 的条目
/// - 目标目录 <c>&lt;env.ComfyuiSource&gt;/custom_nodes/&lt;repo-name&gt;</c> 已存在 → 跳过(G6)
/// - 否则跑 <c>git clone --depth=1 https://github.com/&lt;id&gt;.git &lt;targetDir&gt;</c>
/// - 单节点失败 → 写 WARN + 状态面板 warn: 行,继续下一个(G5)
/// - 整体结果是 Fail if any node failed;否则 Ok
///
/// git clone 走注入的 <c>Func&lt;string, IReadOnlyList&lt;string&gt;, Task&lt;NodeOperationResult&gt;&gt;</c>
/// (App.xaml.cs 那里 lambda 包 GitRunner.RunAsync)— 不直接依赖 GitRunner 实例,便于测试用
/// fake func 验证调用。
/// </summary>
public sealed class CommonNodeInstaller
{
    private readonly Settings _settings;
    private readonly Func<string, IReadOnlyList<string>, Task<NodeOperationResult>> _gitClone;
    private readonly AppLogger? _logger;

    /// <param name="gitClone">参数 1 = repo id (e.g. "ltdrdata/ComfyUI-Manager"),
    /// 参数 2 = git args 列表,return NodeOperationResult(由 App.xaml.cs 那里包
    /// GitRunner.RunAsync(".", args))。</param>
    public CommonNodeInstaller(
        Settings settings,
        Func<string, IReadOnlyList<string>, Task<NodeOperationResult>> gitClone,
        AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gitClone = gitClone ?? throw new ArgumentNullException(nameof(gitClone));
        _logger = logger;
    }

    public async Task<NodeOperationResult> InstallEnabledAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return NodeOperationResult.Fail(
                "env 无 ComfyuiSource,跳过常用节点(env-create 后 ComfyUI 路径未设置)");
        }

        var customNodesDir = Path.Combine(env.ComfyuiSource, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        var enabled = _settings.CommonNodes
            .Where(n => n.Enabled && !string.IsNullOrWhiteSpace(n.Id))
            .ToList();

        if (enabled.Count == 0)
        {
            return NodeOperationResult.Ok("无已勾选节点");
        }

        var failures = new List<string>();
        var skipped = new List<string>();
        var installed = new List<string>();

        foreach (var node in enabled)
        {
            ct.ThrowIfCancellationRequested();

            var repoName = node.Id.Contains('/')
                ? node.Id.Substring(node.Id.IndexOf('/') + 1)
                : node.Id;
            var targetDir = Path.Combine(customNodesDir, repoName);

            // G6:已装跳过(不 git pull)
            if (Directory.Exists(targetDir))
            {
                progress?.Report($"info:已装,跳过 {repoName}");
                skipped.Add(repoName);
                continue;
            }

            progress?.Report($"info:克隆 {node.Id} → {targetDir}");
            var args = new List<string>
            {
                "clone", "--depth=1", $"https://github.com/{node.Id}.git", targetDir,
            };
            NodeOperationResult result;
            try
            {
                result = await _gitClone(node.Id, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = NodeOperationResult.Fail(ex.Message);
            }
            if (!result.Success)
            {
                _logger?.Warn("common-nodes", $"{node.Id} clone 失败:{result.Reason}");
                progress?.Report($"warn:{node.Id} clone 失败:{result.Reason}");
                failures.Add($"{node.Id}({result.Reason})");
                continue;
            }
            installed.Add(repoName);
        }

        var summary = $"installed={installed.Count} skipped={skipped.Count} failed={failures.Count}";
        if (failures.Count > 0)
        {
            return NodeOperationResult.Fail($"{summary};失败:{string.Join("; ", failures)}");
        }
        progress?.Report($"info:常用节点 {summary}");
        return NodeOperationResult.Ok(summary);
    }
}
```

- [ ] **Step 5: Run tests to verify all 5 pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CommonNodeInstallerTests" -v minimal`
Expected: 5 PASS / 0 FAIL / 0 SKIP.

- [ ] **Step 6: Add seed in `SettingsDefaults.Apply` (10 curated entries)**

Edit `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` — add private seed method + invoke at end of `Apply`:

a) **Add** after the `Apply` method closing `}` (line 159) — insert before `private static string Resolve(...)`:

```csharp
    /// <summary>
    /// v0.6.11++:首次启动种 10 个 curated 常用节点到 <see cref="Settings.CommonNodes"/>。
    /// 只在 <c>CommonNodes.Count == 0</c> 时 seed(G13 防覆盖),保护用户清空操作。
    /// 用户可取消勾选(<c>Enabled=false</c>)— 仍保留条目,只是不装。
    /// </summary>
    private static List<CommonNodeEntry> SeedCommonNodesIfEmpty(List<CommonNodeEntry>? current)
    {
        if (current is { Count: > 0 }) return current;
        return new List<CommonNodeEntry>
        {
            new() { Id = "ltdrdata/ComfyUI-Manager",         DisplayName = "ComfyUI Manager",                IsBuiltIn = true, Enabled = true },
            new() { Id = "ltdrdata/ComfyUI-Impact-Pack",     DisplayName = "ComfyUI Impact Pack",            IsBuiltIn = true, Enabled = true },
            new() { Id = "ltdrdata/ComfyUI-Inspire-Pack",    DisplayName = "ComfyUI Inspire Pack",           IsBuiltIn = true, Enabled = true },
            new() { Id = "pythongosssss/ComfyUI-Custom-Scripts", DisplayName = "ComfyUI Custom Scripts",     IsBuiltIn = true, Enabled = true },
            new() { Id = "rgthree/rgthree-comfy",            DisplayName = "rgthree Comfy",                 IsBuiltIn = true, Enabled = true },
            new() { Id = "jags111/efficiency-nodes-comfyui", DisplayName = "Efficiency Nodes",              IsBuiltIn = true, Enabled = true },
            new() { Id = "Kosinkadink/ComfyUI-VideoHelperSuite", DisplayName = "ComfyUI Video Helper Suite", IsBuiltIn = true, Enabled = true },
            new() { Id = "kijai/ComfyUI-KJNodes",            DisplayName = "ComfyUI KJNodes",               IsBuiltIn = true, Enabled = true },
            new() { Id = "kijai/ComfyUI-Florence2",          DisplayName = "ComfyUI Florence2",             IsBuiltIn = true, Enabled = true },
            new() { Id = "Kosinkadink/ComfyUI-Advanced-ControlNet", DisplayName = "ComfyUI Advanced ControlNet", IsBuiltIn = true, Enabled = true },
        };
    }
```

b) **Add** at the END of `Apply` method (right before closing `}` line 159, after the existing `// —— v0.6.5.6 ...` block):

```csharp
        // v0.6.11++:首次启动种 curated 常用节点(只在空时 seed,G13)。
        s.CommonNodes = SeedCommonNodesIfEmpty(s.CommonNodes);
```

- [ ] **Step 7: Add VM properties + form fields to `SettingsViewModel`**

Edit `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:

a) **Add private fields** after `private bool _isAddPythonInterpreterOpen;` (line 37):

```csharp
    private string _newCommonNodeId = "";
    private string _newCommonNodeDisplayName = "";
    private string _addCommonNodeError = "";
    private bool _isAddCommonNodeOpen;
```

b) **Initialize** `CommonNodes` ObservableCollection inside the ctor — INSERT after `PythonInterpreters.CollectionChanged += ...` block (after line 84, before `AddPythonInterpreterCommand = new RelayCommand(...);`):

```csharp
        CommonNodes = new ObservableCollection<CommonNodeEntry>(_settings.CommonNodes);
        CommonNodes.CollectionChanged += (_, _) =>
        {
            _settings.CommonNodes = new List<CommonNodeEntry>(CommonNodes);
            _repo.Save(_settings);
        };
        AddCommonNodeCommand = new RelayCommand(_ =>
        {
            NewCommonNodeId = "";
            NewCommonNodeDisplayName = "";
            AddCommonNodeError = "";
            IsAddCommonNodeOpen = true;
        });
        CancelAddCommonNodeCommand = new RelayCommand(_ =>
        {
            IsAddCommonNodeOpen = false;
            AddCommonNodeError = "";
        });
        ConfirmAddCommonNodeCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewCommonNodeId) || !NewCommonNodeId.Contains('/'))
            {
                AddCommonNodeError = "Id 必须是 owner/repo 形式(必须含 \"/\")";
                return;
            }
            if (CommonNodes.Any(n => string.Equals(n.Id, NewCommonNodeId, StringComparison.OrdinalIgnoreCase)))
            {
                AddCommonNodeError = $"已存在相同 Id 的节点:{NewCommonNodeId}";
                return;
            }
            CommonNodes.Add(new CommonNodeEntry
            {
                Id = NewCommonNodeId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(NewCommonNodeDisplayName)
                    ? NewCommonNodeId.Trim()
                    : NewCommonNodeDisplayName.Trim(),
                IsBuiltIn = false,
                Enabled = true,
            });
            NewCommonNodeId = "";
            NewCommonNodeDisplayName = "";
            AddCommonNodeError = "";
            IsAddCommonNodeOpen = false;
        });
        RemoveCommonNodeCommand = new RelayCommand(p =>
        {
            if (p is CommonNodeEntry entry && !entry.IsBuiltIn)
            {
                CommonNodes.Remove(entry);
            }
        });
        ToggleCommonNodeEnabledCommand = new RelayCommand(p =>
        {
            if (p is CommonNodeEntry entry)
            {
                entry.Enabled = !entry.Enabled;
                // CollectionChanged 只在 list 改动时触发,改 item property 不触发
                // → 手动 Save 持久化
                _settings.CommonNodes = new List<CommonNodeEntry>(CommonNodes);
                _repo.Save(_settings);
            }
        });
```

c) **Add `ObservableCollection<CommonNodeEntry>` property** after the `PythonInterpreters` ObservableCollection (around line 481):

```csharp
    // —— v0.6.11++ 常用节点 ——
    public ObservableCollection<CommonNodeEntry> CommonNodes { get; }
```

d) **Add form fields + commands** after `RemovePythonInterpreterCommand` (line 513):

```csharp
    public string NewCommonNodeId
    {
        get => _newCommonNodeId;
        set => SetField(ref _newCommonNodeId, value);
    }
    public string NewCommonNodeDisplayName
    {
        get => _newCommonNodeDisplayName;
        set => SetField(ref _newCommonNodeDisplayName, value);
    }
    public string AddCommonNodeError
    {
        get => _addCommonNodeError;
        private set
        {
            if (SetField(ref _addCommonNodeError, value))
                RaisePropertyChanged(nameof(HasAddCommonNodeError));
        }
    }
    public bool HasAddCommonNodeError => !string.IsNullOrEmpty(_addCommonNodeError);
    public bool IsAddCommonNodeOpen
    {
        get => _isAddCommonNodeOpen;
        private set => SetField(ref _isAddCommonNodeOpen, value);
    }
    public RelayCommand AddCommonNodeCommand { get; }
    public RelayCommand CancelAddCommonNodeCommand { get; }
    public RelayCommand ConfirmAddCommonNodeCommand { get; }
    public RelayCommand RemoveCommonNodeCommand { get; }
    public RelayCommand ToggleCommonNodeEnabledCommand { get; }
```

e) **Append** to `RaiseAllPropertiesChanged` (after T1 step 11b additions):

```csharp
        RaisePropertyChanged(nameof(CommonNodes));
```

(CommonNodes list 整体刷新时通知;Item-level Enabled 切换走 ToggleCommonNodeEnabledCommand 手动 Save。)

- [ ] **Step 8: Add "常规节点" Section to `SettingsView.xaml`**

Edit `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — INSERT after the "Pip 镜像" section added in T1 Step 12 (i.e. after Pip 镜像 说明 TextBlock 之后,在 "============ 路径 ============" 之前):

```xaml
            <!-- ============ v0.6.11++ 常规节点 ============ -->
            <TextBlock x:Name="SectionCommonNodes" Text="常规节点" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
            <TextBlock Text="env-create 或装依赖末尾自动克隆以下节点(已装自动跳过)。Built-in 不可删,可取消勾选;User-added 可删可改 Id。"
                       Foreground="Gray" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap" MaxWidth="480"
                       HorizontalAlignment="Left" />
            <ItemsControl ItemsSource="{Binding CommonNodes}" Margin="0,8,0,0">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <CheckBox Grid.Column="0"
                                      IsChecked="{Binding Enabled, Mode=OneWay}"
                                      Command="{Binding DataContext.ToggleCommonNodeEnabledCommand,
                                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                                      CommandParameter="{Binding}"
                                      VerticalAlignment="Center" Margin="0,0,8,0" />
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Text="{Binding DisplayName}" />
                                <TextBlock Text="{Binding Id}" Foreground="Gray" FontSize="11" />
                            </StackPanel>
                            <Button Grid.Column="2" Content="删除" Margin="8,0,0,0"
                                    Command="{Binding DataContext.RemoveCommonNodeCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}"
                                    IsEnabled="{Binding IsBuiltIn, Converter={StaticResource InvertBool}}"
                                    Style="{StaticResource MaterialButton}" />
                        </Grid>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Content="+ 添加节点" Margin="0,8,0,0" HorizontalAlignment="Left"
                    Command="{Binding AddCommonNodeCommand}"
                    Style="{StaticResource MaterialButton}" />
            <Grid Margin="0,8,0,0"
                  Visibility="{Binding IsAddCommonNodeOpen,
                                Converter={StaticResource BoolToVisibility}}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="200" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox Grid.Column="0" Style="{StaticResource MaterialTextBox}"
                         Text="{Binding NewCommonNodeId, UpdateSourceTrigger=PropertyChanged}" />
                <TextBox Grid.Column="1" Style="{StaticResource MaterialTextBox}" Margin="8,0,0,0"
                         Text="{Binding NewCommonNodeDisplayName, UpdateSourceTrigger=PropertyChanged}" />
                <Button Grid.Column="2" Content="确定" Margin="8,0,0,0"
                        Command="{Binding ConfirmAddCommonNodeCommand}"
                        Style="{StaticResource MaterialButton}" />
                <Button Grid.Column="3" Content="取消" Margin="4,0,0,0"
                        Command="{Binding CancelAddCommonNodeCommand}"
                        Style="{StaticResource MaterialButton}" />
            </Grid>
            <TextBlock Text="{Binding AddCommonNodeError}"
                       Foreground="OrangeRed" FontSize="11" TextWrapping="Wrap"
                       Margin="0,4,0,0"
                       Visibility="{Binding HasAddCommonNodeError,
                                     Converter={StaticResource BoolToVisibility}}" />
```

(说明:第一列 TextBox Id 加 `ToolTip="owner/repo 形式"` — 当前没 xaml tooltip attr;如果用 Tag/ToolTipService 嫌长可省。InvertBool converter 验证存在:`grep -rn "InvertBool" src-wpf/ComfyUI.Manager/Resources/Theme.xaml` 找到就用;若不存在需在 Theme.xaml 注册 — implementer first step 必做 `grep` 确认 converter 注册。)

- [ ] **Step 9: Verify `InvertBool` converter is registered in Theme.xaml**

Run: `grep -n "InvertBool" src-wpf/ComfyUI.Manager/Resources/Theme.xaml`
Expected: line containing `<BooleanToVisibilityConverter x:Key="InvertBool" />` 或类似。如果 grep miss — implementer 在 Theme.xaml 加(参照现有 `BoolToVisibility` converter 注册 pattern)。

If not found, add to Theme.xaml alongside existing converters:

```xaml
<BooleanToVisibilityConverter x:Key="InvertBool" />
```

(实际 WPF 没有 `BooleanToVisibilityConverter.Invert` — 需要自定义 `InvertBoolConverter : IValueConverter` 返回 `!value`;但更简单是改用 `<Style TargetType="Button"><Style.Triggers><DataTrigger Binding="{Binding IsBuiltIn}" Value="True"><Setter Property="IsEnabled" Value="False" /></DataTrigger></Style.Triggers></Style>` — 这是 v0.6.9.2 G2 教训的应用:任何 `Setter` 都走 property-element + DynamicResource 形式。)

**Implementer decision required:** 若 Theme.xaml 已注册 InvertBool → 走 InvertBool converter 路径(简单);否则改用 Style.Triggers 模式(更安全)。两选一,任一 PASS test 即合规。

- [ ] **Step 10: Run full test suite**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: T1 13 + T2 5 = 18 新测试 PASS,total 839/2/1 baseline(2 pre-existing flake)。

- [ ] **Step 11: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs \
        src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs
git commit -m "feat(wpf): add common-nodes setting + 10 curated seed entries + installer service"
```

---

## Task 3: Hooks (env-create 末尾 + 装依赖 末尾)

**Files (T3):**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` (ctor 加 `CommonNodeInstaller` 参;`CreateAsync` 末尾 step 5.7 调用 + try/catch + WARN log)
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (ctor 加 `CommonNodeInstaller` 参;`InstallAsync` 在 `AutoInstallComfyUiManagerAsync` 之后调用 + try/catch swallow)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (构造 `CommonNodeInstaller(settings, gitCloneAdapter, logger)` + 注入 EnvCreatorService + RequirementsInstaller ctor)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`(传 CommonNodeInstaller null 6th ctor arg;可选加 1 集成测试验证 hook 触发)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`(`FakeRequirementsInstaller` 加 4th ctor arg + `AutoInstallCommonNodes` override + 2 集成测试)

**Interfaces produced (T3):**
- `EnvCreatorService` ctor adds 6th param `CommonNodeInstaller? commonNodeInstaller = null`
- `RequirementsInstaller` ctor adds 4th param `CommonNodeInstaller? commonNodeInstaller = null`
- `RequirementsInstaller.InstallAsync` adds `protected virtual Task<NodeOperationResult> AutoInstallCommonNodesAsync(Environment env, IProgress<string>? progress, CancellationToken ct)` for test seam

### Task 3 Steps

- [ ] **Step 1: Update `EnvCreatorService.cs` ctor + add step 5.7**

Edit `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`:

a) **Add** ctor param + field — replace lines 32-50 (the class fields + ctor block):

```csharp
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly VenvCreator _venvCreator;
    private readonly JunctionLinker _linker;
    private readonly Models.Settings _settings;
    private readonly string _projectRoot;
    // v0.6.11++:env-create 末尾 best-effort 装常用节点(G5 不阻断 env-create)。
    private readonly CommonNodeInstaller? _commonNodeInstaller;

    public EnvCreatorService(
        SqliteConnectionFactory dbFactory,
        VenvCreator venvCreator,
        JunctionLinker linker,
        Models.Settings settings,
        string projectRoot,
        CommonNodeInstaller? commonNodeInstaller = null)
    {
        _dbFactory = dbFactory;
        _venvCreator = venvCreator;
        _linker = linker;
        _settings = settings;
        _projectRoot = projectRoot;
        _commonNodeInstaller = commonNodeInstaller;
    }
```

b) **Add step 5.7** AFTER `envRepo.Upsert(env);` (line 231) but BEFORE `return env;` (line 233):

```csharp
        // 5.7 best-effort 装常用节点(G5:不阻断 env-create,失败仅 WARN)
        if (_commonNodeInstaller is not null)
        {
            try
            {
                progress?.Report(new CreateStepReport("安装常用节点", "触发 CommonNodeInstaller.InstallEnabledAsync"));
                var cnResult = await _commonNodeInstaller.InstallEnabledAsync(
                    env, new Progress<string>(line => progress?.Report(new CreateStepReport("常用节点", line))), ct);
                if (!cnResult.Success)
                {
                    progress?.Report(new CreateStepReport("常用节点", $"warn:{cnResult.Reason}"));
                }
            }
            catch (Exception ex)
            {
                progress?.Report(new CreateStepReport("常用节点", $"warn:异常 {ex.Message}"));
            }
        }

        return env;
```

(CreationStepReport line 接受 string 字段 — 跟现有 `progress?.Report(new CreateStepReport("...", "..."))` 调用 pattern 一致,确认 CreateStepRecord 第二个参数是 string。grep `class CreateStepReport` 验。)

- [ ] **Step 2: Update `EnvCreatorServiceTests` ctor call**

Edit `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs` line 54-55:

```csharp
        _service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir, commonNodeInstaller: null);
```

(新参数为 `= null` 可选,加 explicit `null` 让"未挂 hook"语义清晰。tests 不挂 hook 也能跑过。)

- [ ] **Step 3: Add 1 集成 test verifying hook fires**

Append to `EnvCreatorServiceTests` class:

```csharp
    [Fact]
    public async Task CreateAsync_WithCommonNodeInstaller_TriggersHookAfterUpsert()
    {
        // 用 fake CommonNodeInstaller 验证 step 5.7 触发
        var hookCalls = new List<string>();
        var fakeInstaller = new FakeCommonNodeInstaller(
            hookCalls);  // fake ctor 见 step 4

        var service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir, commonNodeInstaller: fakeInstaller);

        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var env = await service.CreateAsync(
            "hooktest", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        Assert.Single(hookCalls);
        Assert.Equal(env.Id, hookCalls[0]);  // hook 拿到的 env 是 step 8 写库的同一份
    }
```

This test requires the FakeCommonNodeInstaller class (next step).

- [ ] **Step 4: Create `FakeCommonNodeInstaller` test helper**

In the same `EnvCreatorServiceTests` file, append after the existing `FakeJunctionLinker` class (line 159):

```csharp
    private sealed class FakeCommonNodeInstaller : CommonNodeInstaller
    {
        private readonly List<string> _calls;
        public FakeCommonNodeInstaller(List<string> calls)
            : base(
                new ComfyUI.Manager.Models.Settings
                {
                    CommonNodes = new List<CommonNodeEntry>
                    {
                        new() { Id = "fake/test-node", DisplayName = "Test", IsBuiltIn = true, Enabled = false },
                    },
                },
                (id, args) => { calls.Add(id); return Task.FromResult(NodeOperationResult.Ok("fake")); },
                logger: null)
        {
            _calls = calls;
        }

        // 覆盖 hook 调用入口 — 记录 call count + env id(便于测试断言)
    }
```

Wait — the spec says `InstallEnabledAsync` is the entry, not a virtual `Hook`. Simpler: in test ctor pass a `Func` that records calls;Fake subclass adds a wrapper that captures the `Environment` arg via custom code. Cleanest is to use the `gitClone` func which is called per enabled node. Since `enabled=false` is the only entry, no `gitClone` call, but we still want to verify `InstallEnabledAsync` was called.

Refactor: pass `Enabled=true` and have the fake gitClone capture the env via closure:

```csharp
    private sealed class FakeCommonNodeInstaller : CommonNodeInstaller
    {
        private readonly List<string> _calls;
        public FakeCommonNodeInstaller(List<string> calls)
            : base(
                new ComfyUI.Manager.Models.Settings
                {
                    CommonNodes = new List<CommonNodeEntry>
                    {
                        new() { Id = "fake/test-node", DisplayName = "Test", IsBuiltIn = true, Enabled = true },
                    },
                },
                (id, args) => { calls.Add(id); return Task.FromResult(NodeOperationResult.Ok("fake")); },
                logger: null)
        {
            _calls = calls;
        }
    }
```

Test assertion: `Assert.Contains("fake/test-node", hookCalls)` instead of env id.

Update Step 3 test:

```csharp
        Assert.Contains("fake/test-node", hookCalls);
```

(env 校验仍可通过 service.CreateAsync 返回 env,跟 hook 的 env 是同一份 — test 已 verify env id 存在即一致。)

- [ ] **Step 5: Run EnvCreatorService tests to verify 5 pass (4 existing + 1 new)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvCreatorServiceTests" -v minimal`
Expected: 5 PASS / 0 FAIL.

- [ ] **Step 6: Update `RequirementsInstaller.cs` ctor + add AutoInstallCommonNodesAsync**

Edit `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`:

a) **Add field** after `private readonly ComfyUIManagerInstaller _comfyUiManagerInstaller;` (line 39):

```csharp
    // v0.6.11++:装依赖末尾 best-effort 装常用节点(G5 不阻断 requirements)。
    private readonly CommonNodeInstaller? _commonNodeInstaller;
```

b) **Add ctor param** — replace ctor (lines 41-49):

```csharp
    public RequirementsInstaller(
        AppLogger? logger = null,
        RequirementsFileInstaller? reqFileInstaller = null,
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
        CommonNodeInstaller? commonNodeInstaller = null)
    {
        _logger = logger;
        _reqFileInstaller = reqFileInstaller ?? new RequirementsFileInstaller();
        _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(_reqFileInstaller);
        _commonNodeInstaller = commonNodeInstaller;
    }
```

c) **Add** `AutoInstallCommonNodesAsync` virtual test seam + call after `AutoInstallComfyUiManagerAsync` — modify `InstallAsync` to add a call after the existing auto-install-manager block (line 113-114):

```csharp
            // v0.6.11++: requirements 成功后自动装常用节点。失败不阻断
            // requirements(只 WARN 日志)— 用户可以手动 toggle 重试。
            await AutoInstallCommonNodesAsync(env, logProgress, ct);
```

And add the protected virtual method (after `AutoInstallComfyUiManagerAsync` at line 146):

```csharp
    protected virtual async Task<NodeOperationResult> AutoInstallCommonNodesAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (_commonNodeInstaller is null) return NodeOperationResult.Ok("未配置 CommonNodeInstaller");
        try
        {
            progress?.Report("stage:自动装常用节点");
            var result = await _commonNodeInstaller.InstallEnabledAsync(env, progress, ct);
            if (!result.Success)
            {
                _logger?.Warn("requirements-auto-install-common-nodes",
                    $"env='{env.Name}' 常用节点自动装失败(reason={result.Reason});requirements 已成功,用户可在 Settings 调整后重试");
                progress?.Report($"warn:常用节点自动装失败:{result.Reason}");
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warn("requirements-auto-install-common-nodes",
                $"env='{env.Name}' 常用节点自动装异常:{ex.Message}");
            progress?.Report($"warn:常用节点自动装异常:{ex.Message}");
            return NodeOperationResult.Fail(ex.Message);
        }
    }
```

(同 T5 AutoInstallComfyUiManagerAsync 的 swallow pattern:G5 best-effort,caller 不感知失败。)

- [ ] **Step 7: Update `FakeRequirementsInstaller` ctor + add override test**

Edit `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`:

a) **Update FakeRequirementsInstaller ctor** (line 391):

```csharp
        public FakeRequirementsInstaller() : base(null, null, null, null)
        {
        }
```

(4 个 null — 第 4 参 CommonNodeInstaller 留 null 表示"未挂 hook"。)

b) **Add AutoInstallCommonNodes override + tracking** (after `AutoInstallComfyUiManagerAsync` override at line 405):

```csharp
        public NodeOperationResult AutoInstallCommonNodesResult { get; set; } = NodeOperationResult.Ok(null);
        public Environment? AutoInstallCommonNodesEnv { get; private set; }
        public int AutoInstallCommonNodesCallCount { get; private set; }
        public bool AutoInstallCommonNodesThrows { get; set; }

        protected override Task<NodeOperationResult> AutoInstallCommonNodesAsync(
            Environment env,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            AutoInstallCommonNodesCallCount++;
            AutoInstallCommonNodesEnv = env;
            if (AutoInstallCommonNodesThrows) throw new InvalidOperationException("模拟异常");
            progress?.Report("auto-install-common-nodes:克隆常用节点");
            return Task.FromResult(AutoInstallCommonNodesResult);
        }
```

c) **Update FakeRequirementsInstaller.InstallAsync** (line 407) to call the new override at end (mirror the AutoInstallComfyUiManagerAsync pattern around line 443):

```csharp
            try
            {
                await AutoInstallCommonNodesAsync(env, logProgress, ct);
            }
            catch
            {
            }
```

(Add after the existing `try { await AutoInstallComfyUiManagerAsync(...); } catch { }` block — both calls in InstallAsync body now.)

- [ ] **Step 8: Add 2 tests to `RequirementsInstallerTests`**

Append after `InstallAsync_AutoInstallThrows_StillReturnsSuccessForRequirements` (line 260):

```csharp
    [Fact]
    public async Task InstallAsync_PipSucceeds_TriggersCommonNodesAutoInstall()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallCommonNodesResult = NodeOperationResult.Ok("ok");

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.Equal(1, fake.AutoInstallCommonNodesCallCount);
        Assert.Same(env, fake.AutoInstallCommonNodesEnv);
    }

    [Fact]
    public async Task InstallAsync_CommonNodesAutoInstallFails_StillReturnsSuccessForRequirements()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallCommonNodesResult = NodeOperationResult.Fail("git clone 失败");

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.AutoInstallCommonNodesCallCount);
    }
```

- [ ] **Step 9: Run RequirementsInstaller tests to verify all pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~RequirementsInstallerTests" -v minimal`
Expected: 14 PASS (12 existing + 2 new) / 0 FAIL.

- [ ] **Step 10: Update `App.xaml.cs` DI**

Edit `src-wpf/ComfyUI.Manager/App.xaml.cs`:

a) **Add** CommonNodeInstaller construction BEFORE `var envCreator = ...` (line 156). Insert after `gitRunner` block + diffService/nodeOps/etc. We want the gitExe + gitProxy + settings already constructed. Insert after `var bulkOrchestrator = new BulkUpdateOrchestrator(...);` (line 154-155):

```csharp
        // v0.6.11++:常用节点自动装 service(env-create 末尾 + 装依赖末尾触发)。
        // 走注入的 git clone func(包 GitRunner.RunAsync)— 测试可换 fake func。
        // 共享 reqFileInstaller 同 lifecycle,ComfyUIManagerInstaller 之后构造。
        var commonNodeInstaller = new CommonNodeInstaller(
            settings,
            (id, args) => gitRunner.RunAsync(".", args).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result.ExitCode != 0)
                    return NodeOperationResult.Fail(
                        t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "git 异常"
                                    : $"git exit={t.Result.ExitCode}; stderr={t.Result.Stderr.Trim()}");
                return NodeOperationResult.Ok("cloned");
            }),
            logger);
```

b) **Inject** into `EnvCreatorService` ctor (line 156-157):

```csharp
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings, projectRoot,
            commonNodeInstaller: commonNodeInstaller);
```

c) **Inject** into `RequirementsInstaller` ctor (line 169):

```csharp
        var requirementsInstaller = new RequirementsInstaller(logger, reqFileInstaller, comfyUiManagerInstaller, commonNodeInstaller);
```

(CommonNodeInstaller 必须在 RequirementsInstaller 构造前完成 — reorder if needed,跟 T5 Reorder 模式同款。)

- [ ] **Step 11: Run full test suite to verify no regression**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: T1 13 + T2 5 + T3 3 = 21 新测试 PASS,total 842/2/1 baseline(2 pre-existing flake)。

- [ ] **Step 12: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs \
        src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs
git commit -m "feat(wpf): hook CommonNodeInstaller into env-create and requirements install"
```

---

## End-to-end Verification

T1+T2+T3 全 commit 后:

```bash
# 1) build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
# Expected: 0 warnings / 0 errors

# 2) full suite
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
# Expected: 821 + 21 = 842 PASS / 2 FAIL flake (pre-existing ProcessLauncherProgressTests) / 1 SKIP

# 3) per-task filters (sanity)
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PipMirror|FullyQualifiedName~CommonNode" -v minimal --no-build
# Expected: 22 PASS (8 PipMirrorResolver + 4 BuildPipArgs + 5 CommonNodeInstaller + 1 CreateAsync_WithCommonNodeInstaller + 2 InstallAsync_TriggersCommonNodes + 1 InstallAsync_CommonNodesAutoInstallFails + 1 partial Pipeline? 重数) / 0 FAIL

# 4) staging rebuild
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
# Expected: 0 errors;staging 含新 binary。
```

**GUI smoke(桌面验证,user):**
1. 启动 staging → Settings → 看到 2 个新区段(Pip 镜像 + 常规节点)
2. Pip 镜像 = 清华 TUNA → 关 Settings → 装依赖 → 看 pip stdout 含 `Looking in indexes: https://pypi.tuna.tsinghua.edu.cn/simple`
3. 装 BED → pip stdout 仍走 `https://download.pytorch.org/whl/cu118`(BED 不变,G4)
4. 回到 Settings → Pip 镜像 = 自定义 → 填 `https://pypi.doubanio.com/simple` → 装依赖 → 看 stdout 走 doubanio
5. Settings → 常规节点 → 取消所有勾选 → 保存
6. 重启 → Settings → 看到 10 个 built-in 节点(全部 enabled=false)— 验证 G13 seed 不重新覆盖
7. 勾选 ComfyUI-Impact-Pack → 新建 env → 启动后 `<env>/ComfyUI/custom_nodes/ComfyUI-Impact-Pack/` 有 `.git/`
8. 已建 env 再点装依赖 → status panel 末尾 `info:已装,跳过 ComfyUI-Impact-Pack`(idempotent,G6)
9. 添加自定义节点 `foo/bar`(Id 含 /)→ 加入列表;尝试添加 `bar`(无 /)→ 红字提示拒绝(G12)
10. 暗/亮主题切换 → Settings 页新 section 颜色跟随(v0.6.9.2 教训 G2 + v0.6.10.2 DynamicResource 沿用)

---

## Risks

| 风险 | 缓解 |
|---|---|
| 现有用户的 settings.json 没 `PipMirror` / `CommonNodes` 字段 → 读时 null | `Enum.TryParse` 失败回退官方;`CommonNodes` 走 `?? new()` + SettingsDefaults seed (G13) |
| 用户加 50 个 common nodes → env-create clone 50 个 repo 几分钟 | UI 显节点数;status panel 显进度。User-side 控制。 |
| private repo `https://github.com/foo/bar` 失败 | 单节点 Fail + WARN;不停后续;env-create / 装依赖 不阻断 (G5) |
| clone 完但 custom_node 自己有 requirements.txt 要装 | v1 不处理(用户手动触发装依赖,该 node 的 reqs 会随 env 整体装),UI tooltip 说明 |
| Built-in seed 跟用户自定义 Id 冲突 | UI 校验 Id 唯一性 + 表单红字 |
| SettingsDefaults.Apply 二次启动覆盖用户清空的 CommonNodes | seed 只在 `Count == 0` 时跑 (G13) |
| User-added Id 格式错(无 `/`) | UI 校验拒绝加入 (G12) |
| Mirror URL 改时正在跑的 pip | pip 用 args 快照,不 live 重新求值;下次调用 pick up 新值 |
| SettingsView.xaml 加 2 section 后页面变长 | ScrollViewer 已存在,无 layout break;所有新 Setter 强制 DynamicResource (G2) |
| T3 改 EnvCreatorService / RequirementsInstaller ctor 影响老测试 | 同 T1/T2/T5 模式:grep `new EnvCreatorService(` / `new RequirementsInstaller(` 跨 tests-wpf 加参数;Fake 子类适配新字段 |
| `Func<string?>` lazy 解析在测试里 mock 麻烦 | 测试用 `Func<string?>(() => "...")` 直接构造 lambda,不需要 mock framework |
| GitHub 限流:用户开 TUNA 但 Manager clone 走 git,git 不受 PyPI 镜像影响(直连 github) | 这是用户预期的,GitHub 限流是 git clone 的固有问题;不通过镜像解决 |
| CommonNodeInstaller 需要 git.exe | 复用 App.xaml.cs 已有 gitExe + GitRunner;不引入新依赖 (G7) |
| `Environment.ComfyuiSource` 可能为空(用户建 env 后改 root) | CommonNodeInstaller 检测空就返回 Fail "env 无 ComfyuiSource,跳过常用节点";caller WARN |

---

## Self-Review

**1. Spec coverage:**
- G1 Settings JSON 持久化 ✓ — Steps 3(T1), 1(T2) 新字段加在 `Settings.cs` + `SettingsDefaults.Apply` 末尾 seed
- G2 WPF Setter + DynamicResource ✓ — T2 Step 9 强制 implementer grep `InvertBool` 决定走 converter 或 Style.Triggers 模式
- G3 Lazy Func<string?>? mirror 解析 ✓ — T1 Steps 1-9;测试覆盖不缓存语义 (Step 8 第 4 test)
- G4 Pip 镜像不影响 BED ✓ — `BaseEnvInstaller.BuildPipArgs()` 不动;`RequirementsFileInstaller` 接 Func 仅用于 pip;BED 走自己路径
- G5 CommonNodeInstaller best-effort ✓ — T3 Steps 1b, 6c try/catch swallow + WARN log + status panel warn: 行
- G6 Idempotent 安装 ✓ — `Directory.Exists(targetDir)` 跳过;不 git pull
- G7 不引入新依赖 ✓ — 全部用现有 GitRunner / RequirementsFileInstaller / NodeOperationResult / SettingsRepository
- G8 测试覆盖 ✓ — T1 13 + T2 5 + T3 3 = 21 新测试;baseline 821 不退化
- G9 每 task 单独 commit + SDD subagent dispatch ✓ — 每个 task 末尾有 commit step
- G10 不重命名公开 API / 不调整 Settings 字段顺序 ✓ — 所有改动都 append,不重排
- G11 Built-in 节点不可删 ✓ — T2 Step 7d `RemoveCommonNodeCommand` 检查 `!IsBuiltIn`;T2 Step 8 XAML 删按钮 `IsEnabled="{Binding IsBuiltIn, Converter=InvertBool}"`
- G12 User-added Id 必须含 `/` ✓ — T2 Step 7b `ConfirmAddCommonNodeCommand` 校验 `!Contains('/')` → 红字错误
- G13 SettingsDefaults.Apply 只在空时 seed ✓ — T2 Step 6 `SeedCommonNodesIfEmpty` guard

**2. Placeholder scan:**
- "implementer decision required" in T2 Step 9:不是 placeholder,是有意识的设计决策点(invert bool via converter vs Style.Triggers)— 已在 plan 中明确两选一都能 PASS
- 0 "TBD" / 0 "TODO" / 0 "fill in details"
- 0 "add appropriate error handling" (T3 already specifies exact try/catch)
- 0 "similar to Task N" (each step shows full code)

**3. Type consistency:**
- `PipMirrorKind` enum: declared in T1 Step 3, used in T1 Steps 5 (PipMirrorResolver)
- `Settings.PipMirror` (string), `Settings.PipMirrorCustomUrl` (string): declared T1 Step 3, used in PipMirrorResolver (T1 Step 5)
- `Settings.CommonNodes` (List<CommonNodeEntry>): declared T2 Step 1, used in T2 Step 6 (SettingsDefaults seed), T2 Step 7 (VM)
- `CommonNodeEntry` class: declared T2 Step 1, used throughout T2
- `CommonNodeInstaller`: declared T2 Step 4, used in T3 Steps 1, 6, 10
- `RequirementsFileInstaller.BuildPipArgs` (internal static): declared T1 Step 9, tested in T1 Step 8

All types match across tasks.

**4. Test plan type/method consistency:**
- `RequirementsFileInstallerTests.BuildPipArgs_*`: 4 tests added in T1 Step 8
- `PipMirrorResolverTests`: 9 tests added in T1 Step 1
- `CommonNodeInstallerTests`: 5 tests added in T2 Step 2
- `EnvCreatorServiceTests.CreateAsync_WithCommonNodeInstaller_TriggersHookAfterUpsert`: 1 test in T3 Step 3
- `RequirementsInstallerTests.InstallAsync_*CommonNodes*`: 2 tests in T3 Step 8

Total new tests: 9 + 4 + 5 + 1 + 2 = **21** (matches End-to-end Verification §2 "21 新测试 PASS").

---

## Execution Choice

**Subagent-Driven Development(沿用项目惯例):**
- 3 task × (implementer + reviewer) ≈ 6 dispatch
- T1 → T2 → T3 串行 commit on main,每个 task 后立即 task-review
- T3 完成 → final whole-branch review (opus) → staging rebuild → MEMORY update

(plan agent left out: 用户已通过 4 节设计确认全范围 + 5 个 scope 决策问全回答;spec 即最终设计。下一步进入 plan/SDD 实施模式 → T1 implementer dispatch 起步,然后 T2 → T3 串行 subagent。)
