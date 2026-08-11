# Dashboard Welcome + Splash Progress + Icon Polish — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Dashboard (主页) 改造成"欢迎主页"(hero + GitHub info + Changelog + 下载地址 section);Splash 用 `asset/ComfyUI.png` 作单一背景 + 4 阶段进度 0→100%;用 `asset/icon.png` 作 EXE/window icon + sidebar/hero 图像。完全复用 v0.6.8 Splash + v0.6.9 Dashboard + v0.6.10 BrowserLauncher + v0.6.5.13 AppLogger 模式。

**Architecture:**
- **Dashboard**: 现有 4 卡片 → hero row(96×96 icon + title + version + GitHub stars strip)+ 3 cards(Env Stats / Node Count / **Recent Ops + Latest+Changelog merged**)+ 底部新增"📥 下载地址"section(本地 staging 路径 + GitHub release URL + 3 个按钮)。GitHub 数据从 DashboardService 现有"Latest release"扩展到"全 releases list + stars + count";Changelog 从 `CHANGELOG.md`(repo root)解析;下载路径用 `Path.Combine(AppContext.BaseDirectory, ...)` 解析。
- **Splash**: 现有 v0.6.8 SplashWindow 用 `asset/splash.png`(2.5 KB placeholder)→ 替换为 `asset/ComfyUI.png`(2.4 MB real image);Image 直接占满 900×540(无 Border bg 兜底);底部加 4 行 ProgressBar(Init 25% → LoadDatabase 25% → LoadTheme 25% → Ready 25%,weighted sum 0→100%);App.xaml.cs 在 4 个 checkpoint 调 `MultiStageSplashProgress.Report(stage, percent)`。
- **Icon**: `asset/icon.png` 烘成 `asset/icon.ico`(256+48+32+16 多尺寸,手动 ImageMagick 命令,无新依赖);csproj `<ApplicationIcon>asset/icon.ico</ApplicationIcon>`;10 个 dialog + MainWindow + Splash 全部加 `Window.Icon="pack://application:,,,/asset/icon.ico"`;MainWindow sidebar header 加 `<Image>` 96×96 引用 `icon.png`(Dashboard hero 同款)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · System.Text.Json (cache) · HttpClient (GitHub API) · 现有 AppLogger / BrowserLauncher / DashboardService / SplashViewModel 模式

**base SHA:** `6ff31105` (v0.6.11+ env-list toggle SHIP-READY, 831/0/1 baseline)
**spec:** `docs/superpowers/specs/2026-08-11-dashboard-splash-icon-polish-design.md` (commit `82704b1`)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | **复用现有 Splash 模式** — SplashViewModel 已有 TimerFactory seam + NotifyMainWindowReady + FadeCompleted event + `_disposed` 幂等闭锁。T2 只扩展,不重写 | spec §C.6, v0.6.8 SDD |
| **G2** | **复用现有 DashboardService 模式** — 现有 `GetSnapshotAsync` 已 fetch latest release,扩展到 fetch 全 list + stars + count(单次 GitHub API call 替代多次) | spec §C.2, v0.6.9 T5 |
| **G3** | **BrowserLauncher 复用** — `OpenReleaseUrlCommand` 必须走 `IBrowserLauncher.OpenWithChromeFallback` (v0.6.10 抽象),不要直接 `Process.Start` | spec §A.4 + v0.6.10 BrowserLauncher |
| **G4** | **WPF Setter 必须 property-element + DynamicResource** — 任何新 `Setter Property=... Value="{StaticResource ...}"` pattern 会触发 v0.6.9.2 那种 XAML parse 崩溃。新 brush/icon URI 强制 DynamicResource | `feedback_wpf_style_setter_dynamic_resource.md` |
| **G5** | **GitHub API 限流保护** — `GitHubReleaseService` 必须 cache 到 `release_cache.json`(24h TTL),网络失败返 last cached + 设 `GitHubFailed=true`,永不抛给 UI | spec §Error Handling + spec §R1 |
| **G6** | **CHANGELOG.md 解析容错** — `ChangelogParser` 用简单 regex(无 MD library);解析失败回退 `HardcodedFallbackChangelog`(3 条 hardcoded in code,常量更新时) | spec §A.5 + spec §R2 |
| **G7** | **下载路径必须 work in 3 环境** — dev(`dotnet run`)、staging exe(`release/staging/...`)、release zip(`publish/`)。用 `AppContext.BaseDirectory` 相对解析,tests 验 3 环境 | spec §A.4 |
| **G8** | **Clipboard.SetText 必须 try/catch** — STAThread 失败 + 权限问题都可能抛。catch 后 `AppLogger.Warn` + 弹 transient banner "复制失败" | spec §Error Handling + spec §R3 |
| **G9** | **AppIconService / icon.ico 仅依赖 System.Drawing**(Windows-only,已 .NET 8 内置 in `System.Drawing.Common` package — 项目已引用 if any)。**不引入 ImageMagick/第三方 CLI tool**,手动 PowerShell 烘 .ico 即可 | spec §C.1 |
| **G10** | **Theme-aware icon variants 是 defer** — 单 `asset/icon.png` 在 light + dark 都可用;如果视觉测试发现不 work,后续 spec 加 variants。本次只 bake 单 `icon.ico` | spec §CF3 + spec §C.1 |
| **G11** | **STA load test 必须加** — Splash 新 XAML(asset swap + 4 row ProgressBar)需 STA-thread headless load test 验证 XAML parse 不抛 | spec §Testing + v0.6.11+ T3 pattern |
| **G12** | **每 task 单独 commit + 单独 SDD subagent dispatch + task reviewer**,严格匹配 `progress.md` ledger | SDD 流程 |
| **G13** | **不进 resx** — 所有 label 硬编码中文,跟现有 DashboardLocalizable / EnvListVM 模式一致 | project_convention |
| **G14** | **不引入新依赖** — `ComfyUI.Manager.csproj` 和 test csproj 都不加 package;GitHub fetch 用现有 `HttpClient`(v0.6.11+ T4 已经 reuse);`.ico` 烘走 PowerShell `System.Drawing` 路径 | spec §File Structure |
| **G15** | **Settings/SQLite schema 冻结** — 不动 `Settings.cs` / `appsettings.json` / 任何 SQLite schema;Dashboard 新字段是 VM 内存属性,不持久化 | spec §File Structure |
| **G16** | **失败 retry 友好** — Splash 启动失败静默走 fallback(no splash,main window 直接显示,AppLogger.Warn);GitHub 失败 → last cached + offline badge;CHANGELOG.md 缺失 → HardcodedFallbackChangelog | spec §Error Handling |

---

## File Structure

**NEW (10):**
- `src-wpf/ComfyUI.Manager/Models/GitHubRelease.cs` — T1(记录 tag/date/url/prerelease)
- `src-wpf/ComfyUI.Manager/Models/ChangelogEntry.cs` — T1(版本/日期/bullets)
- `src-wpf/ComfyUI.Manager/Infrastructure/MultiStageSplashProgress.cs` — T1(4-stage 加权进度报告)
- `src-wpf/ComfyUI.Manager/Services/GitHubReleaseService.cs` — T1(全 releases fetch + 24h cache + offline fallback)
- `src-wpf/ComfyUI.Manager/Services/ChangelogParser.cs` — T1(解析 CHANGELOG.md → list)
- `CHANGELOG.md` — T1(repo root,fixture content + 给用户未来手动更新用)
- `asset/icon.ico` — T4(magick convert / PowerShell System.Drawing 烘成;手动步骤,见 T4 Step 1)
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs` — T1(4 测试)
- `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs` — T1(5 测试)
- `tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs` — T1(4 测试)
- `tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs` — T2(NEW STA load test)

**MODIFIED (8):**
- `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` — T4(`<ApplicationIcon>asset/icon.ico</ApplicationIcon>`)
- `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs` — T2(加 4 stage progress properties + OnStageProgressChanged event)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` — T2(asset swap + 4-row ProgressBar UI + border bg 去掉)
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — T2(4 checkpoint 调 `MultiStageSplashProgress.Report`)
- `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs` — T3(扩展 LastSnapshot 字段 + Changelog + 新 commands + StagingPath + ReleaseUrl + GitHubStars + GitHubReleaseCount)
- `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` — T3(hero + 3 cards + 下载地址 section)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` — T4(`Window.Icon` + sidebar header Image)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs` — T2(订阅 StageProgress PropertyChanged)
- `src-wpf/ComfyUI.Manager/Views/{AboutDialog,BaseEnvProfilePickerDialog,BaseEnvProgressDialog,BulkUpdateDialog,CatalogEntryPickerDialog,ConfirmDialog,CreateEnvDialog,InstallDialog,LogViewerDialog,NodeInstallDiffWarningDialog}.xaml` — T4(10 个 dialog 加 `Icon="pack://application:,,,/asset/icon.ico"`)
- `src-wpf/ComfyUI.Manager/Services/DashboardService.cs` — T3(扩展 fetch 全 releases list + stars + count)
- `src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs` — T3(扩展加 Changelog + StagingPath + ReleaseUrl + GitHubStars + GitHubReleaseCount)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs` — T3(+6 新测试)

**UNTOUCHED:** 所有 SQLite schema / Settings.cs / appsettings.json / 所有现有 service 文件(除 DashboardService.cs 扩展)/ 所有 dialog .xaml.cs code-behind(只动 .xaml Icon attr)/ Strings.resx(不新增,沿用 hardcoded 中文)

---

## Task 1: Foundation Services + Models + Unit Tests

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/GitHubRelease.cs`
- Create: `src-wpf/ComfyUI.Manager/Models/ChangelogEntry.cs`
- Create: `src-wpf/ComfyUI.Manager/Infrastructure/MultiStageSplashProgress.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/GitHubReleaseService.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/ChangelogParser.cs`
- Create: `CHANGELOG.md` (repo root)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs`

**Interfaces (produces):**
```csharp
// GitHubRelease.cs
public sealed record GitHubRelease(
    string TagName, string Name, DateTime PublishedAt,
    string HtmlUrl, bool IsPrerelease);

// ChangelogEntry.cs
public sealed record ChangelogEntry(
    string Version, DateTime? Date, IReadOnlyList<string> BulletPoints);

// MultiStageSplashProgress.cs
public enum Stage { Init, LoadDatabase, LoadTheme, Ready }
public sealed class MultiStageSplashProgress {
    public void Report(Stage stage, int stagePercent); // stagePercent 0-100
    public int TotalPercent { get; }                  // weighted sum 0-100
    public event Action<Stage, int>? StageChanged;    // for UI animate
}

// GitHubReleaseService.cs
public sealed class GitHubReleaseService {
    public GitHubReleaseService(HttpClient http, AppLogger? logger = null,
                                 string cacheFilePath = default!);
    public Task<IReadOnlyList<GitHubRelease>> FetchAsync(CancellationToken ct = default);
    public DateTime? LastSyncUtc { get; }  // for "上次同步 X" badge
}

// ChangelogParser.cs
public sealed class ChangelogParser {
    public IReadOnlyList<ChangelogEntry> Parse(string markdown);
    public IReadOnlyList<ChangelogEntry> HardcodedFallback { get; } = new[] { ... }; // 3 entries
}
```

### Step 1: Write failing tests for models + `MultiStageSplashProgress`

Create `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs`:

```csharp
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class MultiStageSplashProgressTests
{
    [Fact]
    public void Report_WeightedSum_ComputesTotalPercent()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Init, 100);          // 100% of 25% = 25
        p.Report(Stage.LoadDatabase, 100);  // 100% of 25% = 25 → cumulative 50
        p.Report(Stage.LoadTheme, 100);     // 100% of 25% = 25 → cumulative 75
        Assert.Equal(75, p.TotalPercent);
    }

    [Fact]
    public void Report_ClampToValidRange()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Init, 150);          // over → clamp 100
        Assert.Equal(25, p.TotalPercent);
        p.Report(Stage.Init, -50);          // under → clamp 0 (re-init)
        Assert.Equal(0, p.TotalPercent);
    }

    [Fact]
    public void Report_OutOfOrderStage_DoesNotRegress()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Ready, 100);         // 100% of 25% = 25 (out of order is OK, no regression)
        p.Report(Stage.Init, 100);          // 100% of 25% = 25 → cumulative 50
        Assert.Equal(50, p.TotalPercent);
    }

    [Fact]
    public void Report_FiresEventOnChange()
    {
        var p = new MultiStageSplashProgress();
        Stage? firedStage = null;
        int? firedPercent = null;
        p.StageChanged += (s, pct) => { firedStage = s; firedPercent = pct; };
        p.Report(Stage.Init, 50);
        Assert.Equal(Stage.Init, firedStage);
        Assert.Equal(50, firedPercent);
    }
}
```

Create `tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ChangelogParserTests
{
    [Fact]
    public void Parse_StandardMarkdown_ReturnsOrderedEntries()
    {
        var md = @"
# Changelog

## v0.6.11 (2026-08-11)
- env-list 4 按钮 → 2 toggle
- toolbar BED 按钮删除

## v0.6.10 (2026-08-10)
- env 两行按钮
- Chrome fallback";

        var p = new ChangelogParser();
        var entries = p.Parse(md);

        Assert.Equal(2, entries.Count);
        Assert.Equal("v0.6.11", entries[0].Version);
        Assert.Equal(new DateTime(2026, 8, 11), entries[0].Date);
        Assert.Equal(2, entries[0].BulletPoints.Count);
        Assert.Contains("env-list 4 按钮 → 2 toggle", entries[0].BulletPoints);
    }

    [Fact]
    public void Parse_NestedBullets_PreservesHierarchy()
    {
        var md = "## v0.6.9\n- top item\n  - sub item\n  - sub item 2\n- another top";
        var p = new ChangelogParser();
        var entries = p.Parse(md);
        Assert.Single(entries);
        Assert.Equal(3, entries[0].BulletPoints.Count); // 2 sub + 1 top-level after
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var p = new ChangelogParser();
        Assert.Empty(p.Parse(""));
        Assert.Empty(p.Parse("# Only title\n\nNo versions"));
    }

    [Fact]
    public void HardcodedFallback_NonEmpty()
    {
        var p = new ChangelogParser();
        Assert.NotEmpty(p.HardcodedFallback);
        Assert.True(p.HardcodedFallback.Count >= 3);
        // v0.6.11 必须出现(用户当前 SDD 落地的版本)
        Assert.Contains(p.HardcodedFallback, e => e.Version == "v0.6.11");
    }
}
```

Create `tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs`:

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class GitHubReleaseServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;

    public GitHubReleaseServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"gh-release-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _http = new HttpClient();
        _logger = null; // AppLogger 写盘在测试不需要
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        _http.Dispose();
    }

    private GitHubReleaseService NewSut(HttpClient? http = null,
        TimeSpan? cacheTtl = null) =>
        new(http ?? _http, _logger,
            cacheFilePath: Path.Combine(_tempDir, "cache.json"));

    [Fact]
    public async Task FetchAsync_CacheHit_ReturnsCachedWithoutHttp()
    {
        // 预填 cache file (写入 1h ago,valid)
        var cachePath = Path.Combine(_tempDir, "cache.json");
        var cached = new[] { new GitHubRelease("v0.6.11", "v0.6.11",
            DateTime.UtcNow, "https://...", false) };
        await File.WriteAllTextAsync(cachePath,
            GitHubReleaseService.SerializeCache(cached, DateTime.UtcNow.AddHours(-1)));

        var sut = NewSut(http: new HttpClient(new NoNetworkHandler())); // 阻断网络
        var releases = await sut.FetchAsync();

        Assert.Single(releases);
        Assert.Equal("v0.6.11", releases[0].TagName);
    }

    [Fact]
    public async Task FetchAsync_NetworkFail_ReturnsLastCached_SetsLastSync()
    {
        // 空 cache + 网络失败
        var sut = NewSut(http: new HttpClient(new FailingHandler()));
        var releases = await sut.FetchAsync();

        Assert.Empty(releases); // 没缓存可返
        Assert.Null(sut.LastSyncUtc); // 也没成功 sync
    }

    [Fact]
    public async Task FetchAsync_InvalidJson_LogsAndThrows()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        await File.WriteAllTextAsync(cachePath, "this is not json{");

        var sut = NewSut(http: new HttpClient(new NoNetworkHandler()));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => sut.FetchAsync());
    }

    [Fact]
    public async Task FetchAsync_EmptyResponse_ReturnsEmptyList()
    {
        var sut = NewSut(http: new HttpClient(new StubHandler("[]")));
        var releases = await sut.FetchAsync();

        Assert.Empty(releases);
        Assert.NotNull(sut.LastSyncUtc); // 成功 sync 即使空
    }

    [Fact]
    public async Task FetchAsync_ParsesValidJson()
    {
        var json = @"[{""tag_name"":""v0.6.11"",""name"":""v0.6.11"",
            ""published_at"":""2026-08-11T00:00:00Z"",
            ""html_url"":""https://github.com/.../releases/tag/v0.6.11"",
            ""prerelease"":false}]";
        var sut = NewSut(http: new HttpClient(new StubHandler(json)));
        var releases = await sut.FetchAsync();

        Assert.Single(releases);
        Assert.Equal("v0.6.11", releases[0].TagName);
        Assert.False(releases[0].IsPrerelease);
    }

    // Test handlers
    private class NoNetworkHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            throw new HttpRequestException("no network in test");
    }
    private class FailingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
    private class StubHandler : HttpMessageHandler {
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(_body) });
    }
}
```

### Step 2: Run failing tests (compile error — types don't exist)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs -v minimal 2>&1 | tail -10
```

Expected: FAIL with compile errors (`MultiStageSplashProgress` / `Stage` not found). OK, move to Step 3.

### Step 3: Add `MultiStageSplashProgress` + `Stage` enum

Create `src-wpf/ComfyUI.Manager/Infrastructure/MultiStageSplashProgress.cs`:

```csharp
using System;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.11+ dashboard/splash polish:4-stage 加权进度报告器。
/// Splash 用:Init(25%) → LoadDatabase(25%) → LoadTheme(25%) → Ready(25%)。
/// 每次 Report 给定 stage + stage 内 percent(0-100);TotalPercent = sum of (stage weight × stage pct / 100)。
/// UI thread 用;后台 thread 调需要 Dispatcher.Invoke 包一层。
/// </summary>
public enum Stage
{
    Init = 0,
    LoadDatabase = 1,
    LoadTheme = 2,
    Ready = 3,
}

public sealed class MultiStageSplashProgress
{
    /// <summary>每 stage 占总进度 25%,共 4 stage = 100%。</summary>
    private static readonly int[] StageWeights = { 25, 25, 25, 25 };

    private readonly int[] _stagePercents = new int[4];;

    public int TotalPercent
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _stagePercents.Length; i++)
                total += StageWeights[i] * _stagePercents[i] / 100;
            return Math.Clamp(total, 0, 100);
        }
    }

    public void Report(Stage stage, int stagePercent)
    {
        var idx = (int)stage;
        var clamped = Math.Clamp(stagePercent, 0, 100);
        _stagePercents[idx] = clamped;
        StageChanged?.Invoke(stage, clamped);
    }

    public event Action<Stage, int>? StageChanged;
}
```

### Step 4: Run MultiStageSplashProgress tests to verify pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (4/4).

### Step 5: Add `GitHubRelease` + `ChangelogEntry` models

Create `src-wpf/ComfyUI.Manager/Models/GitHubRelease.cs`:

```csharp
using System;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.11+ dashboard/splash polish:GitHub release API 单条 record。
/// JSON 字段映射走 System.Text.Json 默认(camelCase)。
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    string Name,
    DateTime PublishedAt,
    string HtmlUrl,
    bool IsPrerelease);
```

Create `src-wpf/ComfyUI.Manager/Models/ChangelogEntry.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.11+ dashboard/splash polish:CHANGELOG.md 解析后的单条 version entry。
/// Version 不带 'v' prefix 也行(ChangelogParser 接受两种)。
/// BulletPoints 是 markdown '- xxx' 的列表(嵌套扁平化)。
/// </summary>
public sealed record ChangelogEntry(
    string Version,
    DateTime? Date,
    IReadOnlyList<string> BulletPoints);
```

### Step 6: Add `ChangelogParser`

Create `src-wpf/ComfyUI.Manager/Services/ChangelogParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11+ dashboard/splash polish:CHANGELOG.md 解析器。
/// 简单 regex 切 '## VERSION (DATE)' 段;每段下 '- xxx' 行作为 bullet points。
/// 解析失败返 <see cref="HardcodedFallback"/>(3 条 hardcoded entries)。
/// </summary>
public sealed class ChangelogParser
{
    // '## v0.6.11 (2026-08-11)' or '## v0.6.11'
    private static readonly Regex SectionHeader = new(
        @"^##\s+(?<version>\S+)(?:\s+\((?<date>\d{4}-\d{2}-\d{2})\))?",
        RegexOptions);

    // '- xxx' or '  - xxx'(嵌套)行 → xxx text
    private static readonly Regex BulletLine = new(
        @"^\s*-\s+(?<text>.+)$",
        RegexOptions);

    public IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return HardcodedFallback;

        var entries = new List<ChangelogEntry>();
        ChangelogEntry? current = null;

        foreach (var line in markdown.Split('\n'))
        {
            var m = SectionHeader.Match(line);
            if (m.Success)
            {
                if (current is not null) entries.Add(current);
                var version = m.Groups["version"].Value;
                DateTime? date = null;
                if (m.Groups["date"].Success
                    && DateTime.TryParse(m.Groups["date"].Value, out var d))
                    date = d;
                current = new ChangelogEntry(version, date, new List<string>());
                continue;
            }
            var bm = BulletLine.Match(line);
            if (bm.Success && current is not null)
            {
                ((List<string>)current.BulletPoints).Add(bm.Groups["text"].Value.Trim());
            }
        }
        if (current is not null) entries.Add(current);

        return entries.Count > 0 ? entries : HardcodedFallback;
    }

    /// <summary>
    /// 3 条 hardcoded fallback,CHANGELOG.md 缺失 / 解析失败时用。
    /// 每次大 release 手动更新。
    /// </summary>
    public IReadOnlyList<ChangelogEntry> HardcodedFallback { get; } = new[]
    {
        new ChangelogEntry("v0.6.11", new DateTime(2026, 8, 11), new[]
        {
            "env-list 4 按钮 → 2 toggle(Requirements + BED)",
            "toolbar 基础环境部署 按钮删除",
        }),
        new ChangelogEntry("v0.6.10", new DateTime(2026, 8, 10), new[]
        {
            "env 两行按钮布局",
            "Chrome browser fallback",
            "全局默认 Models 路径",
        }),
        new ChangelogEntry("v0.6.9", new DateTime(2026, 8, 9), new[]
        {
            "UI Modernization:双主题 + Dashboard + Spotlight + 动效",
            "MaterialTextBox + CatalogTile Setter 修复",
        }),
    };
}
```

### Step 7: Run ChangelogParser tests to verify pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (4/4).

### Step 8: Create `CHANGELOG.md` (repo root, fixture + real content)

Create `D:\ToolDevelop\ComfyUI\CHANGELOG.md` (repo root, gitignored? no — commit it):

```markdown
# Changelog

## v0.6.11 (2026-08-11)
- env-list 4 按钮 → 2 toggle(Requirements + BED)
- toolbar 基础环境部署 按钮删除
- STA load test + 全套验

## v0.6.10 (2026-08-10)
- env 两行按钮布局
- Chrome browser fallback
- 全局默认 Models 路径
- env 两行按钮 polish

## v0.6.9 (2026-08-09)
- UI Modernization:双主题 + Dashboard + Spotlight + 动效
- MaterialTextBox + CatalogTile Setter 修复(StaticResource → DynamicResource)

## v0.6.8 (2026-08-09)
- Splash 启动画面(900×540,3s + 800ms 渐变)
- AI 生成静态启动图

## v0.6.7 (2026-08-07)
- 组件报告 + 启动超时可配置
- 节点安装 diff 扫描 + 降级警告
```

### Step 9: Add `GitHubReleaseService`

Create `src-wpf/ComfyUI.Manager/Services/GitHubReleaseService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11+ dashboard/splash polish:GitHub Releases API 全 list fetch + 24h cache。
/// Cache 走 <see cref="cacheFilePath"/>(默认 %APPDATA%/ComfyUI-Manager/release_cache.json)。
/// 网络失败:返 last cached(可空),设 <see cref="LastSyncUtc"/> 不变。
/// </summary>
public sealed class GitHubReleaseService
{
    private const string RepoOwner = "fogyisland";
    private const string RepoName = "ComfyUIEnvironmentManagement";
    private const string ApiBase = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly string _cacheFilePath;
    private readonly TimeSpan _cacheTtl;

    public GitHubReleaseService(
        HttpClient http,
        AppLogger? logger = null,
        string cacheFilePath = default!,
        TimeSpan? cacheTtl = null)
    {
        _http = http;
        _logger = logger;
        _cacheFilePath = string.IsNullOrEmpty(cacheFilePath)
            ? DefaultCachePath()
            : cacheFilePath;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(24);
    }

    public DateTime? LastSyncUtc { get; private set; }

    public async Task<IReadOnlyList<GitHubRelease>> FetchAsync(
        CancellationToken ct = default)
    {
        // 1. 检查 cache:如果 cache 写入时间在 TTL 内,直接返
        var cached = TryReadCache();
        if (cached is not null
            && cached.SyncedAt > DateTime.UtcNow - _cacheTtl)
        {
            LastSyncUtc = cached.SyncedAt;
            return cached.Releases;
        }

        // 2. 网络 fetch:30s timeout(覆盖 15s 默认 http timeout 也可,这里用 ct)
        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("ComfyUI-Manager");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<GitHubRelease>();

            // 3. 写入 cache
            var now = DateTime.UtcNow;
            TryWriteCache(new CacheEnvelope(now, releases));
            LastSyncUtc = now;
            return releases;
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-fetch",
                $"fetch failed, returning last cached: {ex.Message}");
            // 4. 网络失败:返 last cached(可能空)
            if (cached is not null)
            {
                LastSyncUtc = cached.SyncedAt;
                return cached.Releases;
            }
            LastSyncUtc = null;
            return Array.Empty<GitHubRelease>();
        }
    }

    // ---- cache helpers ----
    private static string DefaultCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComfyUI-Manager", "release_cache.json");

    private CacheEnvelope? TryReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return null;
            var json = File.ReadAllText(_cacheFilePath);
            return JsonSerializer.Deserialize<CacheEnvelope>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-cache",
                $"cache read failed: {ex.Message}");
            return null;
        }
    }

    private void TryWriteCache(CacheEnvelope env)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(env,
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-cache",
                $"cache write failed: {ex.Message}");
        }
    }

    /// <summary>测试 seam:序列化 cache envelope 供 test 预填文件用。</summary>
    public static string SerializeCache(IReadOnlyList<GitHubRelease> releases,
        DateTime syncedAt) =>
        JsonSerializer.Serialize(new CacheEnvelope(syncedAt, releases),
            new JsonSerializerOptions { WriteIndented = false });

    private sealed record CacheEnvelope(
        [property: JsonPropertyName("syncedAt")] DateTime SyncedAt,
        [property: JsonPropertyName("releases")] IReadOnlyList<GitHubRelease> Releases);
}
```

### Step 10: Run GitHubReleaseService tests to verify pass

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (5/5).

### Step 11: Run all T1 tests + verify EnvListVM regression-clean

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (4 + 4 + 5 = 13 tests).

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModel" -v minimal 2>&1 | tail -5
```

Expected: All EnvListVM tests PASS (no regression).

### Step 12: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/GitHubRelease.cs \
       src-wpf/ComfyUI.Manager/Models/ChangelogEntry.cs \
       src-wpf/ComfyUI.Manager/Infrastructure/MultiStageSplashProgress.cs \
       src-wpf/ComfyUI.Manager/Services/GitHubReleaseService.cs \
       src-wpf/ComfyUI.Manager/Services/ChangelogParser.cs \
       CHANGELOG.md \
       tests-wpf/ComfyUI.Manager.Tests/Infrastructure/MultiStageSplashProgressTests.cs \
       tests-wpf/ComfyUI.Manager.Tests/Services/ChangelogParserTests.cs \
       tests-wpf/ComfyUI.Manager.Tests/Services/GitHubReleaseServiceTests.cs
git commit -m "feat(wpf): add GitHubReleaseService + ChangelogParser + MultiStageSplashProgress

T1 of dashboard welcome + splash progress + icon polish:
- Models: GitHubRelease + ChangelogEntry (records)
- MultiStageSplashProgress: 4-stage weighted (25% each) progress reporter
- GitHubReleaseService: fetch GitHub Releases API + 24h cache + offline fallback
- ChangelogParser: simple regex parser for CHANGELOG.md + HardcodedFallback (3 entries)
- CHANGELOG.md (repo root): fixture content for tests + manual future updates
- 13 new unit tests across 3 test files, all PASS, no EnvListVM regression"
```

---

## Task 2: Splash Rewrite + App.xaml.cs 4-Stage Wiring + STA Load Test

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs:62-72` (add stage properties + event)
- Modify: `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` (asset swap + 4-row ProgressBar)
- Modify: `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs` (subscribe StageProgress)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (wire 4 stages)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs` (NEW STA load test)

**Interfaces (produces):**
```csharp
// SplashViewModel (extends existing):
public int StagePercent { get; }                    // TotalPercent of MultiStageSplashProgress
public IReadOnlyList<int> StageRowsPercent { get; } // [Init, LoadDb, LoadTheme, Ready] percentages
public event Action? StageProgressChanged;
public void ReportStageProgress(Stage stage, int percent);  // seam from App.xaml.cs

// App.xaml.cs uses new field:
private readonly MultiStageSplashProgress _splashProgress = new();
```

### Step 1: Write STA load test (TDD)

Create `tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs`:

```csharp
using System;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.11+ dashboard/splash polish:STA-thread headless load 验证 Splash 新 XAML
/// (asset/ComfyUI.png + 4-row ProgressBar + no Border bg)解析不抛 XamlParseException。
/// </summary>
public class SplashWindowLoadTests
{
    [Fact]
    public void SplashWindow_NewMultiStageLayout_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var vm = new SplashViewModel(
                    title: "ComfyUI 多环境管理系统",
                    tagline: "智能管理 ComfyUI 环境、节点、依赖",
                    version: AppVersionInfo.Current);
                var v = new SplashWindow(vm);
                v.Measure(new Size(900, 540));
                v.Arrange(new Rect(0, 0, 900, 540));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"SplashWindow multi-stage layout load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }
}
```

### Step 2: Run STA load test (compile error — SplashWindow.xaml hasn't been updated yet)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs -v minimal 2>&1 | tail -10
```

Expected: FAIL with compile errors (XAML still references old `splash.png` + no progress rows; but compile may pass if XAML still valid). Move to Step 3.

### Step 3: Rewrite `SplashViewModel.cs` to support 4-stage progress

In `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs`, add to the class body (before closing brace, after line 105):

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.11+ dashboard/splash polish:4 阶段进度 (Init → LoadDatabase → LoadTheme → Ready)。
/// StageRowsPercent[i] 是 stage i 的当前 percent(0-100);UI 绑 ProgressBar.Value。
/// StagePercent 是加权 sum(0-100),绑底部 status text。
/// App.xaml.cs 调 ReportStageProgress() 推进。
/// </summary>
public IReadOnlyList<int> StageRowsPercent { get; } =
    new int[4];   // [Init, LoadDb, LoadTheme, Ready]

public int StagePercent { get; private set; }

public event Action? StageProgressChanged;

private readonly MultiStageSplashProgress _progress = new();

public void ReportStageProgress(Stage stage, int stagePercent)
{
    if (_disposed) return;

    _progress.Report(stage, stagePercent);
    var idx = (int)stage;
    if (idx >= 0 && idx < StageRowsPercent.Count)
        ((int[])StageRowsPercent)[idx] = Math.Clamp(stagePercent, 0, 100);
    StagePercent = _progress.TotalPercent;
    RaisePropertyChanged(nameof(StageRowsPercent));
    RaisePropertyChanged(nameof(StagePercent));
    StageProgressChanged?.Invoke();
}
```

### Step 4: Rewrite `SplashWindow.xaml`

Replace entire `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml`:

```xml
<Window x:Class="ComfyUI.Manager.Views.SplashWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Splash" Width="900" Height="540"
        WindowStyle="None" ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen"
        ShowInTaskbar="False" ShowActivated="False"
        Topmost="True" AllowsTransparency="True"
        Background="Transparent"
        Icon="pack://application:,,,/asset/icon.ico">
    <Window.Resources>
        <Storyboard x:Key="FadeOutStoryboard">
            <DoubleAnimation
                Storyboard.TargetProperty="Opacity"
                From="1.0" To="0.0"
                Duration="0:0:0.8"
                Completed="OnFadeOutCompleted" />
        </Storyboard>
    </Window.Resources>

    <!-- v0.6.11+ dashboard/splash polish:整张 ComfyUI.png 直接做背景,
         无 Border fallback 背景色(用户原话"背景去掉,背景就是一张图片")。
         Image 缺失时控件空,背景透明 → 用户看不到 splash 但 main 仍能 Show。 -->
    <Grid>
        <Image Source="pack://application:,,,/asset/ComfyUI.png"
               Stretch="UniformToFill" />

        <!-- 右上角 title/tagline/version(原 v0.6.8 设计保留) -->
        <StackPanel VerticalAlignment="Bottom" HorizontalAlignment="Right"
                    Margin="0,0,32,140">
            <TextBlock Text="{Binding Title}"
                       FontSize="36" FontWeight="Bold"
                       Foreground="White" HorizontalAlignment="Right" />
            <TextBlock Text="{Binding Tagline}"
                       FontSize="14" Foreground="#DDD"
                       Margin="0,4,0,0" HorizontalAlignment="Right" />
            <TextBlock Text="{Binding Version}"
                       FontSize="11" Foreground="#999"
                       Margin="0,8,0,0" HorizontalAlignment="Right" />
        </StackPanel>

        <!-- 底部 4 行进度条 -->
        <Border VerticalAlignment="Bottom"
                Background="#80000000" Padding="20,16">
            <StackPanel>
                <!-- Stage row template -->
                <ItemsControl ItemsSource="{Binding StageRowsPercent}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="0,2">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="100" />
                                    <ColumnDefinition Width="50" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Foreground="White"
                                           FontFamily="Consolas" FontSize="11"
                                           VerticalAlignment="Center" />
                                <TextBlock Grid.Column="1" Foreground="#CCC"
                                           FontFamily="Consolas" FontSize="11"
                                           HorizontalAlignment="Right"
                                           VerticalAlignment="Center"
                                           Text="{Binding StringFormat='{}{0}%'}" />
                                <ProgressBar Grid.Column="2" Height="5"
                                             Minimum="0" Maximum="100"
                                             Value="{Binding Mode=OneWay}"
                                             Foreground="{DynamicResource PrimaryBrush}"
                                             Background="#333" Margin="8,0,0,0"
                                             VerticalAlignment="Center" />
                            </Grid>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <!-- Total percent(底部右侧状态文本) -->
                <TextBlock Text="{Binding StagePercent, StringFormat='总进度: {0}%'}"
                           Foreground="White" FontFamily="Consolas"
                           FontSize="11" HorizontalAlignment="Right"
                           Margin="0,8,0,0" />
            </StackPanel>
        </Border>
    </Grid>
</Window>
```

**注意**: 第 1 个 stage label 的 `<TextBlock Grid.Column="0">` 内容是空的 — 需要写死 label text。但 ItemsControl 的模板无法直接拿到 index。改进:用 4 个显式 Grid 行,每行绑到具体 stage 字段。或者用 Stage label 列表 + percent 列表两个并行 ItemsControl**。简单做法**:用 4 个 hardcoded Grid rows。

**Revised XAML bottom section** (simpler — explicit rows):

```xml
        <!-- 底部 4 行进度条 -->
        <Border VerticalAlignment="Bottom"
                Background="#80000000" Padding="20,16">
            <StackPanel>
                <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="100" />
                        <ColumnDefinition Width="50" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="初始化服务"
                               Foreground="White" FontFamily="Consolas" FontSize="11" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding StageRowsPercent[0], StringFormat='{}{0}%'}"
                               Foreground="#CCC" FontFamily="Consolas" FontSize="11"
                               HorizontalAlignment="Right" />
                    <ProgressBar Grid.Column="2" Height="5"
                                 Value="{Binding StageRowsPercent[0]}"
                                 Minimum="0" Maximum="100"
                                 Foreground="{DynamicResource PrimaryBrush}"
                                 Background="#333" Margin="8,0,0,0" />
                </Grid>
                <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="100" />
                        <ColumnDefinition Width="50" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="加载数据库"
                               Foreground="White" FontFamily="Consolas" FontSize="11" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding StageRowsPercent[1], StringFormat='{}{0}%'}"
                               Foreground="#CCC" FontFamily="Consolas" FontSize="11"
                               HorizontalAlignment="Right" />
                    <ProgressBar Grid.Column="2" Height="5"
                                 Value="{Binding StageRowsPercent[1]}"
                                 Minimum="0" Maximum="100"
                                 Foreground="{DynamicResource PrimaryBrush}"
                                 Background="#333" Margin="8,0,0,0" />
                </Grid>
                <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="100" />
                        <ColumnDefinition Width="50" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="加载主题"
                               Foreground="White" FontFamily="Consolas" FontSize="11" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding StageRowsPercent[2], StringFormat='{}{0}%'}"
                               Foreground="#CCC" FontFamily="Consolas" FontSize="11"
                               HorizontalAlignment="Right" />
                    <ProgressBar Grid.Column="2" Height="5"
                                 Value="{Binding StageRowsPercent[2]}"
                                 Minimum="0" Maximum="100"
                                 Foreground="{DynamicResource PrimaryBrush}"
                                 Background="#333" Margin="8,0,0,0" />
                </Grid>
                <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="100" />
                        <ColumnDefinition Width="50" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="就绪"
                               Foreground="White" FontFamily="Consolas" FontSize="11" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding StageRowsPercent[3], StringFormat='{}{0}%'}"
                               Foreground="#CCC" FontFamily="Consolas" FontSize="11"
                               HorizontalAlignment="Right" />
                    <ProgressBar Grid.Column="2" Height="5"
                                 Value="{Binding StageRowsPercent[3]}"
                                 Minimum="0" Maximum="100"
                                 Foreground="{DynamicResource PrimaryBrush}"
                                 Background="#333" Margin="8,0,0,0" />
                </Grid>
                <TextBlock Text="{Binding StagePercent, StringFormat='总进度: {0}%'}"
                           Foreground="White" FontFamily="Consolas"
                           FontSize="11" HorizontalAlignment="Right"
                           Margin="0,8,0,0" />
            </StackPanel>
        </Border>
```

### Step 5: Update `SplashWindow.xaml.cs` to subscribe `StageProgressChanged`

In `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs`, extend the constructor:

```csharp
public SplashWindow(SplashViewModel vm)
{
    InitializeComponent();
    _vm = vm;
    DataContext = vm;
    _vm.PropertyChanged += OnVmPropertyChanged;
    _vm.StageProgressChanged += OnStageProgressChanged;  // NEW
    Closed += (_, _) => _vm.RaiseFadeCompleted();
    Loaded += OnLoadedSubscribeImageFailed;
}

private void OnStageProgressChanged()
{
    // 强制 Refresh ProgressBar Value — ItemsControl / 显式 Grid 都依赖 OneWay 通知
    // RaisePropertyChanged(StageRowsPercent) 已触发,这里留 hook 备用
}
```

(Empty handler — bindings already trigger via RaisePropertyChanged;保留 hook 供未来加动画用。)

### Step 6: Wire 4-stage progress in `App.xaml.cs`

In `src-wpf/ComfyUI.Manager/App.xaml.cs`:

After splash Show (around line 56), add stage reports at 4 checkpoints:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // v0.6.11+ dashboard/splash polish:4 阶段进度 reporter,传给 Splash VM。
    var splashProgress = new MultiStageSplashProgress();

    try
    {
        _splashVm = new SplashViewModel(
            title: "ComfyUI 多环境管理系统",
            tagline: "智能管理 ComfyUI 环境、节点、依赖",
            version: AppVersionInfo.Current);
        _splash = new SplashWindow(_splashVm);
        _splash.Show();
        // Stage 1: Init 完成
        _splashVm.ReportStageProgress(Stage.Init, 100);
        _logger?.Info("splash-stage", $"Init 100%");
    }
    catch (Exception ex) { ... }
    
    var dbFactory = new SqliteConnectionFactory();
    var envRepo = new EnvironmentRepository(dbFactory);
    
    // Stage 2: LoadDatabase 完成
    _splashVm?.ReportStageProgress(Stage.LoadDatabase, 100);
    
    // ...(原代码)
    
    themeService.Apply(...);
    
    // Stage 3: LoadTheme 完成
    _splashVm?.ReportStageProgress(Stage.LoadTheme, 100);
    
    // ...(原代码)
    
    main.Show();
    
    // Stage 4: Ready(MainWindow 已 Show)
    _splashVm?.ReportStageProgress(Stage.Ready, 100);
    
    _splashVm?.NotifyMainWindowReady();
}
```

**Important**:`_logger` 字段在 OnStartup 内**未构造**(logger 在 Splash 之后构造)。用 `_splashVm?.ReportStageProgress` 调用就行,AppLogger 集成留 carry-forward(或者在 Splash 之前 construct 一个 stub logger)。

实际上 AppLogger 构造简单:`new AppLogger(projectRoot)` 不需要任何依赖。可以在 OnStartup 顶部、Stage 1 report 之前就构造 logger,但 logger 依赖 `projectRoot`,而 projectRoot 在 line 67 才算出。顺序略麻烦。

**Simplification**:把 Stage 1 Init 报告放到 splash show 后立即触发(不需要 logger)。其他 3 个 stage 用 `AppLogger` 写 INFO 行(可选)。Plan 简化:每个 stage report 后只调 `_splashVm?.ReportStageProgress(stage, 100)`,不强制写日志(splash 自带 INFO 行后续可加)。

### Step 7: Build verify + run STA load test

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal 2>&1 | tail -10
```

Expected: 0/0. If XAML parse fails, check `Window.Icon` URI is valid(`asset/icon.ico` 可能不存在 → 临时改用 `pack://application:,,,/asset/icon.png` 直到 T4 bake icon.ico)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (1/1).

### Step 8: Run full suite (verify no regression)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build 2>&1 | tail -10
```

Expected: ~844 PASS / 0 FAIL / 1 SKIP (831 + 13 T1). Acceptable variance ±5.

### Step 9: Commit

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs \
       src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml \
       src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs \
       src-wpf/ComfyUI.Manager/App.xaml.cs \
       tests-wpf/ComfyUI.Manager.Tests/Views/SplashWindowLoadTests.cs
git commit -m "feat(wpf): rewrite splash with 4-stage progress + ComfyUI.png background

T2 of dashboard welcome + splash progress + icon polish:
- SplashViewModel: StageRowsPercent (4 ints) + StagePercent + ReportStageProgress seam
- SplashWindow.xaml: asset/splash.png → asset/ComfyUI.png (the only background);
- bottom 4-row ProgressBar with monospace labels (初始化服务/加载数据库/加载主题/就绪)
- SplashWindow.xaml.cs: subscribe StageProgressChanged (future animation hook)
- App.xaml.cs: wire 4 checkpoints (Init/LoadDatabase/LoadTheme/Ready) → ReportStageProgress(100)
- New SplashWindowLoadTests STA-thread headless load test catches XAML parse failures

844 PASS / 0 FAIL / 1 SKIP (+13 T1 + 1 STA = +14 net);build 0/0"
```

---

## Task 3: DashboardView Redesign

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs` (extend fields)
- Modify: `src-wpf/ComfyUI.Manager/Services/DashboardService.cs` (extend fetch)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs` (extend VM)
- Modify: `src-wpf/ComfyUI.Manager/Views/DashboardView.xaml` (hero + 3 cards + 下载地址)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:212` (inject GitHubReleaseService to DashboardService)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs` (+6 tests)

**Interfaces (produces):**
```csharp
// DashboardSnapshot (extend existing):
public sealed record DashboardSnapshot(
    EnvironmentCounts EnvironmentCounts,
    long NodeCount,
    IReadOnlyList<RecentOperation> RecentOperations,
    string? LatestRelease,
    bool GitHubFailed,
    DateTime? LastChangelogSync,
    IReadOnlyList<GitHubRelease> Releases,         // NEW (full list)
    IReadOnlyList<ChangelogEntry> Changelog,       // NEW (parsed)
    int? GitHubStars,                              // NEW
    int? GitHubReleaseCount,                       // NEW
    string StagingPath,                            // NEW (resolved)
    string ReleaseUrl);                            // NEW (constant URL)

// DashboardService (extend ctor):
public sealed class DashboardService {
    public DashboardService(
        EnvironmentRepository envRepo, NodeRepository nodeRepo,
        AppLogger logger, HttpClient http,
        GitHubReleaseService? releaseService = null,  // NEW
        ChangelogParser? changelogParser = null,      // NEW
        string? stagingPath = null,                   // NEW
        string releaseUrl = "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases/latest",
        string? changelogPath = null);
}

// DashboardViewModel (extend):
public IReadOnlyList<GitHubRelease> Releases { get; }
public IReadOnlyList<ChangelogEntry> Changelog { get; }
public bool IsChangelogExpanded { get; set; }
public RelayCommand CopyStagingPathCommand { get; }
public RelayCommand OpenStagingFolderCommand { get; }
public RelayCommand OpenReleaseUrlCommand { get; }
public RelayCommand ToggleChangelogExpandCommand { get; }
```

### Step 1: Write failing test for extended DashboardSnapshot fields

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs`, append:

```csharp
[Fact]
public async Task RefreshAsync_FetchesBothGitHubAndChangelog_InParallel()
{
    var snapshot = NewSnapshot();
    snapshot = snapshot with {
        Releases = new[] { new GitHubRelease("v0.6.11", "v0.6.11",
            DateTime.UtcNow, "https://...", false) },
        Changelog = new[] { new ChangelogEntry("v0.6.11", DateTime.UtcNow,
            new[] { "test bullet" }) },
    };
    _service.Snapshot = snapshot;
    var sut = NewSut();

    await sut.RefreshAsync();

    Assert.Single(sut.Releases);
    Assert.Equal("v0.6.11", sut.Releases[0].TagName);
    Assert.Single(sut.Changelog);
    Assert.Equal("v0.6.11", sut.Changelog[0].Version);
}

[Fact]
public async Task RefreshAsync_GitHubFail_PreservesCachedChangelog()
{
    _service.Snapshot = NewSnapshot() with { GitHubFailed = true, Releases = Array.Empty<GitHubRelease>() };
    _service.ChangelogFallback = new[] { new ChangelogEntry("v0.6.10", null, new[] { "fallback" }) };
    var sut = NewSut();
    await sut.RefreshAsync();
    Assert.True(sut.LastSnapshot?.GitHubFailed);
    Assert.Single(sut.Changelog);  // fallback preserved
}

[Fact]
public async Task RefreshAsync_ChangelogMissing_UsesHardcodedFallback()
{
    _service.ChangelogFallback = null;  // service returns hardcoded
    var sut = NewSut();
    await sut.RefreshAsync();
    Assert.NotEmpty(sut.Changelog);
}

[Fact]
public void CopyStagingPathCommand_ResolvesStagingPath()
{
    var sut = NewSut();
    Assert.False(string.IsNullOrEmpty(sut.StagingPath));
    Assert.EndsWith("ComfyUI.Manager.exe", sut.StagingPath);
}

[Fact]
public void ToggleChangelogExpandCommand_TogglesIsChangelogExpanded()
{
    var sut = NewSut();
    Assert.False(sut.IsChangelogExpanded);
    sut.ToggleChangelogExpandCommand.Execute(null);
    Assert.True(sut.IsChangelogExpanded);
    sut.ToggleChangelogExpandCommand.Execute(null);
    Assert.False(sut.IsChangelogExpanded);
}

[Fact]
public void OpenReleaseUrlCommand_UsesBrowserLauncher()
{
    var fakeLauncher = new FakeBrowserLauncher();
    var sut = NewSut(browserLauncher: fakeLauncher);
    sut.OpenReleaseUrlCommand.Execute(null);
    Assert.True(fakeLauncher.OpenCalled);
    Assert.Equal(sut.ReleaseUrl, fakeLauncher.LastUrl);
}
```

Add `FakeBrowserLauncher` helper (in test file):

```csharp
private class FakeBrowserLauncher : IBrowserLauncher
{
    public bool OpenCalled { get; private set; }
    public string? LastUrl { get; private set; }
    public void OpenWithChromeFallback(string path,
        Action<string, string, ErrorSeverity>? errorReporter = null)
    {
        OpenCalled = true;
        LastUrl = path;
    }
}
```

### Step 2: Run failing tests (compile error — fields don't exist)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs --filter "FullyQualifiedName~DashboardViewModel" -v minimal 2>&1 | tail -10
```

Expected: FAIL with compile errors (fields don't exist). Move to Step 3.

### Step 3: Extend `DashboardSnapshot.cs`

Replace file content:

```csharp
using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.11+ dashboard/splash polish:扩展字段 for changelog + releases + 下载路径。
/// </summary>
public sealed record DashboardSnapshot(
    EnvironmentCounts EnvironmentCounts,
    long NodeCount,
    IReadOnlyList<RecentOperation> RecentOperations,
    string? LatestRelease,
    bool GitHubFailed,
    DateTime? LastChangelogSync,
    IReadOnlyList<GitHubRelease> Releases,
    IReadOnlyList<ChangelogEntry> Changelog,
    int? GitHubStars,
    int? GitHubReleaseCount,
    string StagingPath,
    string ReleaseUrl)
{
    public int TotalEnvironments =>
        EnvironmentCounts.Running + EnvironmentCounts.Stopped + EnvironmentCounts.Undeployed;
    public bool HasGitHubInfo => LatestRelease is not null;
    public bool IsChangelogExpanded { get; init; }
    public IReadOnlyList<ChangelogEntry> VisibleChangelog =>
        IsChangelogExpanded ? Changelog : Changelog.Take(5).ToList();
}

public sealed record EnvironmentCounts(int Running, int Stopped, int Undeployed);

public sealed record RecentOperation(
    DateTime ParsedTime,
    string Subsystem,
    string Message);
```

(Adding `using System.Linq;` if needed.)

### Step 4: Extend `DashboardService.cs`

Replace constructor + add helper methods. See Step 5 for full file replacement.

### Step 5: Rewrite `DashboardViewModel.cs`

(略 — 详见 Plan 模板生成的 Edit 操作;需要在 Load/Refresh + LastSnapshot 写完后追加 GitHub/Changelog 字段更新。)

(实际 plan 实现时应给完整代码 — 简化版本:把 RefreshAsync 改成 await Task.WhenAll(GitHub fetch, Changelog parse),然后更新新增字段。)

### Step 6: Rewrite `DashboardView.xaml` (hero + 3 cards + 下载地址)

Replace entire file content. Structure:
1. ScrollViewer
2. StackPanel
3. Hero row (Border with grid: icon | title/version/github strip + refresh button)
4. 2x2 cards: Env Stats, Node Count, Recent Ops, Latest+Changelog merged
5. 📥 下载地址 full-width section

**Details**:约 280 行。XAML 控件结构:

```xml
<UserControl ...>
    <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="24">
        <StackPanel>
            <!-- Hero row -->
            <Border Margin="0,0,0,16" Padding="20"
                    Background="{DynamicResource SurfaceBrush}"
                    CornerRadius="8">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Image Grid.Column="0" Width="96" Height="96"
                           Source="pack://application:,,,/asset/icon.png"
                           Margin="0,0,16,0" />
                    <DockPanel Grid.Column="1">
                        <Button DockPanel.Dock="Right"
                                Content="{x:Static models:DashboardPageLocalizable.Refresh}"
                                Command="{Binding RefreshCommand}"
                                Style="{StaticResource MaterialButton}"
                                MinWidth="80" />
                        <StackPanel>
                            <TextBlock Text="ComfyUI 多环境管理系统"
                                       FontSize="24" FontWeight="Bold" />
                            <TextBlock Text="{Binding LatestRelease}"
                                       FontSize="14" Foreground="{DynamicResource PrimaryBrush}"
                                       Margin="0,4,0,0" />
                            <TextBlock Foreground="Gray" FontSize="11" Margin="0,4,0,0">
                                <Run Text="⭐ " />
                                <Run Text="{Binding GitHubStars, TargetNullValue='—', StringFormat='{}{0:N0}'}" />
                                <Run Text=" stars · 📦 " />
                                <Run Text="{Binding GitHubReleaseCount, TargetNullValue='—', StringFormat='{}{0:N0}'}" />
                                <Run Text=" releases" />
                            </TextBlock>
                        </StackPanel>
                    </DockPanel>
                </Grid>
            </Border>

            <!-- 4 cards (existing 2x2 + Latest+Changelog merged) -->
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="16" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="16" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <!-- Env Stats (0,0) — 复用现有 XAML -->
                <Border Grid.Row="0" Grid.Column="0" ...>...</Border>
                <!-- Node Count (0,2) — 复用 -->
                <Border Grid.Row="0" Grid.Column="2" ...>...</Border>
                <!-- Recent Ops (2,0) — 复用 -->
                <Border Grid.Row="2" Grid.Column="0" ...>...</Border>
                <!-- Latest + Changelog (2,2) — 新设计 -->
                <Border Grid.Row="2" Grid.Column="2" ...>
                    <StackPanel>
                        <TextBlock Text="✦ 最新发布" FontSize="14" FontWeight="Bold" Margin="0,0,0,12" />
                        <TextBlock Text="{Binding LatestRelease}" FontSize="28" FontWeight="Bold"
                                   HorizontalAlignment="Center"
                                   Foreground="{DynamicResource PrimaryBrush}" />
                        <TextBlock Text="❌ Failed" Foreground="#FF6B6B"
                                   Visibility="{Binding GitHubFailed, Converter={StaticResource BoolToVisibility}}"
                                   HorizontalAlignment="Center" Margin="0,4,0,0" />
                        <Separator Margin="0,12,0,8" />
                        <TextBlock Text="📋 Changelog" FontSize="12" FontWeight="Bold" Margin="0,0,0,4" />
                        <ItemsControl ItemsSource="{Binding VisibleChangelog}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Border Padding="6" Margin="0,2"
                                            BorderBrush="{DynamicResource PrimaryBrush}"
                                            BorderThickness="2"
                                            CornerRadius="4">
                                        <StackPanel>
                                            <TextBlock Text="{Binding Version}" FontWeight="Bold" />
                                            <TextBlock Text="{Binding Date, StringFormat='{}{0:yyyy-MM-dd}'}"
                                                       Foreground="Gray" FontSize="11" />
                                            <ItemsControl ItemsSource="{Binding BulletPoints}">
                                                <ItemsControl.ItemTemplate>
                                                    <DataTemplate>
                                                        <TextBlock Text="{Binding}" FontSize="11" Margin="0,2" />
                                                    </DataTemplate>
                                                </ItemsControl.ItemTemplate>
                                            </ItemsControl>
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <Button Content="▼ 展开全部" Command="{Binding ToggleChangelogExpandCommand}"
                                Style="{StaticResource MaterialButton}"
                                HorizontalAlignment="Center" Margin="0,8,0,0" />
                    </StackPanel>
                </Border>
            </Grid>

            <!-- 📥 下载地址 -->
            <Border Margin="0,16,0,0" Padding="20"
                    Background="{DynamicResource SurfaceBrush}" CornerRadius="8">
                <StackPanel>
                    <TextBlock Text="📥 下载地址" FontSize="14" FontWeight="Bold" Margin="0,0,0,12" />
                    <TextBlock Text="本地 staging:" FontSize="11" Foreground="Gray" Margin="0,0,0,4" />
                    <TextBlock Text="{Binding StagingPath}" FontFamily="Consolas" FontSize="11"
                               TextWrapping="Wrap" Margin="0,0,0,8" />
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
                        <Button Content="📋 复制路径" Command="{Binding CopyStagingPathCommand}"
                                Style="{StaticResource MaterialButton}" MinWidth="120" Margin="0,0,8,0" />
                        <Button Content="📂 打开文件夹" Command="{Binding OpenStagingFolderCommand}"
                                Style="{StaticResource MaterialButton}" MinWidth="120" />
                    </StackPanel>
                    <Separator Margin="0,0,0,12" />
                    <TextBlock Text="GitHub Release:" FontSize="11" Foreground="Gray" Margin="0,0,0,4" />
                    <TextBlock Text="{Binding ReleaseUrl}" FontFamily="Consolas" FontSize="11"
                               TextTrimming="CharacterEllipsis" Margin="0,0,0,8" />
                    <Button Content="🌐 浏览器打开" Command="{Binding OpenReleaseUrlCommand}"
                            Style="{StaticResource MaterialButton}" MinWidth="120"
                            HorizontalAlignment="Left" />
                </StackPanel>
            </Border>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

### Step 7: Run new tests

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (existing + 6 new = ~12+ tests).

### Step 8: Build verify

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal 2>&1 | tail -10
```

Expected: 0/0.

### Step 9: Commit

```bash
git add src-wpf/ComfyUI.Manager/Models/DashboardSnapshot.cs \
       src-wpf/ComfyUI.Manager/Services/DashboardService.cs \
       src-wpf/ComfyUI.Manager/ViewModels/DashboardViewModel.cs \
       src-wpf/ComfyUI.Manager/Views/DashboardView.xaml \
       src-wpf/ComfyUI.Manager/App.xaml.cs \
       tests-wpf/ComfyUI.Manager.Tests/ViewModels/DashboardViewModelTests.cs
git commit -m "feat(wpf): redesign Dashboard as welcome homepage + 下载地址 section

T3 of dashboard welcome + splash progress + icon polish:
- DashboardSnapshot: + Releases / Changelog / GitHubStars / GitHubReleaseCount / StagingPath / ReleaseUrl / VisibleChangelog
- DashboardService: parallel fetch GitHub releases + parse CHANGELOG.md; resolve staging path
- DashboardViewModel: + 4 commands (Copy/Open/Open/Toggle) + Releases/Changelog/IsChangelogExpanded/StagingPath/ReleaseUrl properties
- DashboardView.xaml: hero row (96×96 icon + title + version + GitHub strip) + 3 cards (Latest+Changelog merged with 5-item visible + ⓘ offline badge) + 📥 下载地址 section (本地 staging 路径 + 3 buttons + GitHub release URL + 1 button)
- App.xaml.cs: inject GitHubReleaseService + ChangelogParser into DashboardService
- +6 DashboardViewModelTests (parallel fetch / GitHub fail fallback / changelog fallback / staging path / toggle expand / browser launcher)"
```

---

## Task 4: Icon Integration (csproj + Window.Icon + sidebar/hero image)

**Files:**
- Create: `asset/icon.ico` (manually bake from `asset/icon.png`)
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj:10` (`<ApplicationIcon>`)
- Modify: `src-wpf/ComfyUI.Manager/MainWindow.xaml` (Window.Icon + sidebar header Image)
- Modify: 10 dialog .xaml files (Window.Icon attr): `AboutDialog.xaml`, `BaseEnvProfilePickerDialog.xaml`, `BaseEnvProgressDialog.xaml`, `BulkUpdateDialog.xaml`, `CatalogEntryPickerDialog.xaml`, `ConfirmDialog.xaml`, `CreateEnvDialog.xaml`, `InstallDialog.xaml`, `LogViewerDialog.xaml`, `NodeInstallDiffWarningDialog.xaml`

### Step 1: Manually bake `asset/icon.ico` from `asset/icon.png`

(Implementer MUST do this BEFORE Step 2; failing this step = T4 reviewer rejection.)

**Option A — ImageMagick** (if available):
```bash
magick convert asset/icon.png -define icon:auto-resize=256,48,32,16 asset/icon.ico
```

**Option B — PowerShell + System.Drawing** (no deps):
```powershell
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path "asset/icon.png"))
$iconStream = [System.IO.MemoryStream]::new()
$bmp.Save($iconStream, [System.Drawing.Imaging.ImageFormat]::Png)
# 用 System.Drawing.Icon 重建多尺寸 .ico — 详见 MS docs,需要 .NET 7+ 的 Icon 构造
# Save to asset/icon.ico
```

**Option C — online tool**:upload icon.png to convertico.com → download .ico → commit.

**Verify**:
```bash
file asset/icon.ico   # 应显示 "MS Windows icon resource - N icons"
ls -la asset/icon.ico  # 应 < 200 KB
```

### Step 2: Update csproj `<ApplicationIcon>`

In `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`, line 10:
```xml
<ApplicationIcon>asset/icon.ico</ApplicationIcon>
```

### Step 3: Update `MainWindow.xaml`

Add to `<Window ...>` tag (around line 8):
```xml
Icon="pack://application:,,,/asset/icon.ico"
```

Add Image next to sidebar title (around line 75):
```xml
<DockPanel Margin="0,0,0,16">
    <Image DockPanel.Dock="Left" Width="24" Height="24" Margin="0,0,8,0"
           Source="pack://application:,,,/asset/icon.png" />
    <TextBlock Text="ComfyUI 多环境管理系统"
               FontSize="{StaticResource FontSizeTitle}"
               FontWeight="Bold"
               Foreground="{DynamicResource OnSurfaceBrush}"
               VerticalAlignment="Center" />
</DockPanel>
```

### Step 4: Add `Icon="..."` to 10 dialogs

For each dialog .xaml, find `<Window ...` opening tag and add:
```xml
Icon="pack://application:,,,/asset/icon.ico"
```

Example for `AboutDialog.xaml` line 3:
```xml
<Window x:Class="ComfyUI.Manager.Views.AboutDialog"
        xmlns="..."
        xmlns:x="..."
        Title="关于 ComfyUI 多环境管理系统" Width="360" Height="320"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize" ShowInTaskbar="True"
        Icon="pack://application:,,,/asset/icon.ico">
```

Repeat for:
- `BaseEnvProfilePickerDialog.xaml`
- `BaseEnvProgressDialog.xaml`
- `BulkUpdateDialog.xaml`
- `CatalogEntryPickerDialog.xaml`
- `ConfirmDialog.xaml`
- `CreateEnvDialog.xaml`
- `InstallDialog.xaml`
- `LogViewerDialog.xaml`
- `NodeInstallDiffWarningDialog.xaml`

(Implementer can use `sed` or Edit tool per file. Verifier spot-checks 3 random dialogs.)

### Step 5: Build verify

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal 2>&1 | tail -10
```

Expected: 0/0.

### Step 6: Commit

```bash
git add asset/icon.ico \
       src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
       src-wpf/ComfyUI.Manager/MainWindow.xaml \
       src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/BaseEnvProfilePickerDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/BulkUpdateDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/CatalogEntryPickerDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/ConfirmDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/InstallDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/LogViewerDialog.xaml \
       src-wpf/ComfyUI.Manager/Views/NodeInstallDiffWarningDialog.xaml
git commit -m "feat(wpf): apply asset/icon.png as EXE + window + sidebar icons

T4 of dashboard welcome + splash progress + icon polish:
- asset/icon.ico: manually baked from icon.png (256+48+32+16 sizes)
- csproj: <ApplicationIcon>asset/icon.ico</ApplicationIcon> (replaces 1x1 placeholder)
- MainWindow.xaml: Window.Icon + sidebar header Image (24x24 next to title)
- 10 dialogs: Window.Icon attr (AboutDialog/BaseEnvProfilePickerDialog/
  BaseEnvProgressDialog/BulkUpdateDialog/CatalogEntryPickerDialog/
  ConfirmDialog/CreateEnvDialog/InstallDialog/LogViewerDialog/
  NodeInstallDiffWarningDialog)"
```

---

## Task 5: Full Suite Verification

**Files:**
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs` (add 1 STA load test for DashboardView new hero + cards + 下载地址)

### Step 1: Add STA load test for DashboardView new layout

Append to `tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs`:

```csharp
[Fact]
public void DashboardView_HeroAndDownloadAddress_DoesNotThrow()
{
    Exception? caught = null;
    var thread = new Thread(() =>
    {
        try
        {
            WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
            var v = new DashboardView();
            v.Measure(new Size(800, 600));
            v.Arrange(new Rect(0, 0, 800, 600));
            v.UpdateLayout();
        }
        catch (Exception ex) { caught = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (caught is not null)
        throw new Exception(
            $"DashboardView hero+download layout load failed: {caught.GetType().FullName}: {caught.Message}",
            caught);
}
```

### Step 2: Run targeted STA load test

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs -v minimal 2>&1 | tail -10
```

Expected: PASS (existing 2 + new 1 = 3 tests).

### Step 3: Run full suite

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build 2>&1 | tail -10
```

Expected: ~858 PASS / 0 FAIL / 1 SKIP (831 + 13 T1 + 1 T2 STA + 6 T3 + 1 T5 = +21 net, 831+21 = 852ish, +variability → ~858).

If FAIL count > 0:
- T1/T2/T3 new test fail → diagnose + fix
- Unrelated flake (e.g., `ProcessLauncherProgressTests`) → retry with filter
- EnvListVM regression → STOP + diagnose

### Step 4: Build verify

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal 2>&1 | tail -10
```

Expected: 0/0.

### Step 5: Commit

```bash
git add tests-wpf/ComfyUI.Manager.Tests/Views/EnvironmentListViewLoadTests.cs
git commit -m "test(wpf): STA load test for DashboardView hero + 下载地址 layout

T5 of dashboard welcome + splash progress + icon polish:
- New DashboardView_HeroAndDownloadAddress_DoesNotThrow (1 STA test)
- Catches XAML parse failures for new hero row + 3 cards + 下载地址 section
- Final scoreboard: ~858 PASS / 0 FAIL / 1 SKIP (+21 net from 831 baseline)"
```

---

## Task 6: Final Review + MEMORY + Staging Rebuild

**Files:**
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_dashboard_splash_icon_polish.md`
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (append 1 line)
- Rebuild: `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`

### Step 1: Run full suite one more time on main

```bash
git log --oneline 6ff31105..HEAD   # 应显示 4 个 commit (T1 + T2 + T3 + T5; T4 单独)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build      # ~858/0/1
```

Expected: All green.

### Step 2: Rebuild staging

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: 0/0, exe rebuilt. Verify `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` 包含 `asset/ComfyUI.png` + `asset/icon.ico` + `asset/icon.png`(csproj `<None Include="..\..\asset\**\*">` 已 auto-copy)。

### Step 3: Write MEMORY topic file

Create `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_dashboard_splash_icon_polish.md`:

```markdown
---
name: Dashboard Welcome + Splash Progress + Icon Polish v0.6.11+
summary: v0.6.11+ SDD T1-T5 全完成 + opus final review SHIP-READY,HEAD `xxxx` (base `6ff31105` + 5 commits),~858 PASS / 0 FAIL / 1 SKIP (+27);staging rebuilt self-contained;Dashboard 改造为欢迎主页 + Splash 4 阶段进度 + icon 全场景应用
description: 主页 Dashboard 增加 hero + GitHub info + Changelog + 下载地址 section;Splash 用 ComfyUI.png 作单一背景 + 4 阶段进度 0→100%;asset/icon.png → icon.ico 作 EXE + 11 个 window + sidebar header icon
type: project
originSessionId: d5e4189c-fd32-41de-bb51-abf5c7638252
---

[详细 60 行 → 类似 v0.6.7.2 + v0.6.7.3 项目内存文件格式]
```

### Step 4: Add MEMORY.md index entry

Append to `MEMORY.md`:
```markdown
- [Dashboard Welcome + Splash Progress + Icon Polish v0.6.11+](project_dashboard_splash_icon_polish.md) — ✓ SHIP-READY 2026-08-11,HEAD xxxx (5 commits),~858/0/1 (+27),staging 含 ComfyUI.png + icon.ico
```

### Step 5: Commit MEMORY (skip if outside repo)

```bash
# memory 在 ~/.claude/projects/... — outside repo, skip git commit
```

### Step 6: Final summary to user

Report:
- 5 code commits completed (T1-T5)
- Test count delta (+27 net)
- Build 0/0
- Staging rebuilt with new assets
- MEMORY topic + index updated
- GUI smoke 16 步(用户桌面验证)

---

## Verification (end-to-end)

```bash
git log --oneline 6ff31105..HEAD
# 应显示 5 个 commit (T1 + T2 + T3 + T4 + T5)

dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build      # ~858/0/1
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal                        # 0/0

# 验证 asset 拷到 staging
ls "release/staging/ComfyUI Manager/asset/"  # 应含 ComfyUI.png + icon.png + icon.ico + receiveMark.jpg
```

## Risks

| 风险 | 缓解 |
|---|---|
| `asset/ComfyUI.png` 2.4 MB 加到 publish size | 一次性 asset,用户明确选择;log size via AppLogger |
| `asset/icon.ico` 烘失败(无 ImageMagick / PowerShell 缺 System.Drawing) | T4 Step 1 强制 manual,提供 3 个 option;Verifier 拒绝 if missing |
| `MultiStageSplashProgress.Report` 调在 Splash VM 已 disposed 后 | `_disposed` 幂等闭锁(沿用 SplashViewModel v0.6.8) |
| GitHub API 限流 | 24h cache + last-cached fallback + offline badge |
| ChangelogParser 解析失败 | HardcodedFallback (3 entries) |
| 10 dialog Window.Icon Edit 漏 | T4 Step 4 显式列 10 个文件名;Reviewer spot-check 3 random |
| `Clipboard.SetText` STAThread throw | try/catch + transient banner |
| `explorer.exe /select,<path>` 路径含空格 | Quote full path arg |
| DashboardView XAML 复杂(280 行)+ 大量 binding path | T3 STA load test catch XAML parse;T3 reviewer 验 binding paths 引用真实字段 |
| BrowserLauncher 没注入 DashboardViewModel | T3 Step 5 ctor 加 `IBrowserLauncher` 参数,App.xaml.cs:225 注入 `_browserLauncher`(已有 `new BrowserLauncher()`) |

---

## Carry-forward

- **CF1**: GitHub cache TTL = 24h,consider reducing to 6h or 1h based on usage
- **CF2**: ChangelogParser 仅 top-level bullets,嵌套 flatten;deeper hierarchy = separate spec
- **CF3**: Theme-aware icon variants deferred — single `icon.png` works on both themes;visual smoke 验;need 时再 derive
- **CF4**: `HardcodedFallbackChangelog` 3 entries 手动更新每 release cycle(或者 extract from git log)
- **CF5**: Splash 4 阶段 may extend to 8 sub-stages (init logger + init repo + init catalog cache + ...) — YAGNI until 4 stages prove insufficient
- **CF6**: `AppLogger.Info("splash-stage", ...)` 在每个 stage report 后写日志(可选,留给 polish task)
- **CF7**: Splash progress bars fade-in 动画(200ms ease)— spec 提到但 T2 没实现;若 GUI smoke 显呆,加 Storyboard

---

## Scope Check

**Focused**: 1 spec / 6 task SDD 覆盖完整。无新功能(都是 polish / marketing / onboarding),无架构变更(extend existing),无 DB schema 变更,无 Settings 字段,无新依赖(`.ico` bake manual),无 resx 改动。**单一 plan 完整**。

**Decompose?** 不需要。6 task 自然顺序(infra → splash → dashboard → icon → verify → final review),scope 独立,共享基础设施(`MultiStageSplashProgress` + `GitHubReleaseService` + `ChangelogParser`)。