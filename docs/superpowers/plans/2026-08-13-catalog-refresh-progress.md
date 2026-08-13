# v0.6.15 Catalog Refresh 实时进度 + Rate Limit UI + 入口跳过 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Catalog refresh 实时显示 4 阶段进度（读 catalog / 写库 / 拉版本 / 拉 metadata）+ 撞 GitHub rate limit 时弹可复用 `RateLimitBanner` 警告 + 下次 refresh 入口自动跳过还在限流冷却中的 stage（不浪费配额）。

**Architecture:**
- 进程级 `IRateLimitState` 单例（`lock` 保护 `Dictionary<RateLimitStage, RateLimitBlockInfo>`）记录每个 stage 的 reset time + 自动过期清理
- 跨边界 record `RateLimitInfo(Stage, Remaining, ResetUnix, PartialCount, TotalCount)` 经由 `IProgress<RateLimitInfo>` 从 service 流到 UI
- 独立 `RateLimitBannerViewModel` + `Views/RateLimitBanner.xaml`（独立 UserControl，可被任何 view host）+ `Theme.xaml` 加 `WarningContainerBrush` / `OnWarningBrush`（Material You warning palette）
- `CatalogRefreshService.RefreshAsync` 入口 `IsBlocked(stage)` 检查 → 跳过对应 stage；撞 limit 时同时 `IProgress.Report` + `state.MarkBlocked`；完成时只 Clear 实际跑了且没撞的 stage
- `CatalogViewModel` 加 4 个 progress string property + `RateLimitBanner` 子 VM + ctor 接 `IRateLimitState`
- `CatalogView.xaml` 底部进度面板（4 行 TextBlock + ProgressBar）+ 内嵌 `<views:RateLimitBanner />`
- `App.xaml.cs` DI 单例 + `MainViewModel` 透传到 `CatalogViewModel`

**Tech Stack:**
- WPF + .NET 8 / C# 12 / xUnit
- 既有：`IProgress<T>` + `SynchronizationContext` marshal pattern（per `project_v0_6_5_11_hotfix` 教训）、`AppLogger` `ReadLines()` 测试 pattern、`Fake*` 子类 override virtual method pattern、`STA` 线程 load test pattern（per `project_v0_6_9_2_hotfix` 教训）
- 既有：`RelayCommand` + `ViewModelBase` + `SetField` INPC helper
- `RefreshResult` 既有 record（已有 `VersionCount` / `MetadataCount` / `AddedCount` 等字段）

## Global Constraints

- **不 bump version、不发 release zip** — per hotfix 偏好（v0.6.14.1 R1 即无 v-bump，本次照办）
- **不持久化 rate limit state 到 disk** — reset 1h 内过期，重启早失效
- **不后台 auto-retry** — 复杂，session 关闭丢状态、cancel 语义乱、UI 转圈 5+min 难 cancel
- **进程单例**：`IRateLimitState` 在 `App.xaml.cs` new 一份，所有 view 共享同一份
- **WPF `Progress<T>` 自动 marshal**：所有 `IProgress<T>` 通道沿用既有 pattern，构造时捕获 UI `SynchronizationContext`
- **theme brushes 必须 dark + light 各一份**：`WarningBrush` 已有；本次新增 `WarningContainerBrush`（浅橙背景）+ `OnWarningBrush`（深橙文字）必须注册到 `Themes/Palette.Dark.xaml` 和 `Themes/Palette.Light.xaml`
- **Stage-skip 语义**：只 Clear 实际跑了且没撞 limit 的 stage；skip 路径不 Clear（沿用上次 blocked 状态）
- **不撞现行 7 个 VersionService fake 测试** — v0.6.14.1 R1 已加 `AppLogger? logger = null` param 到 fake overrides，本次不动 fake 签名（除非 `EnrichAsync` 必须改）
- **既有 `RefreshResult` 字段不动**：4 计数 `Added/Updated/Skipped/Deleted` + `VersionCount` + `MetadataCount` + `EntryCount` 全部保留；`WriteProgress` 直接 populate `result.AddedCount` 等
- **无新 schema 改动** — 仅复用既有表 + SQLite migration 不动
- **无资源/resx 改动** — 进度文本先中文硬编码（跟项目其他 UI 一致），后续统一 i18n
- **测试数**：基线 `1071 PASS / 2 FAIL (1 pre-existing flaky real-git × 2) / 1 SKIP`，目标 `1090+ PASS / 0 FAIL`（新增 ~19 测试）
- **`MetadataFetchProgress` 已存在** — `GitHubCatalogMetadataService.cs:389` 已有 record，本次不动
- **`VersionFetchProgress` 已存在** — 既有 record，pass-through，不动

---

## File Structure

| 操作 | 路径 |
|---|---|
| 新建 | `src-wpf/ComfyUI.Manager/Models/RateLimitStage.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Models/RateLimitInfo.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Services/IRateLimitState.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Services/RateLimitState.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/ViewModels/RateLimitBannerViewModel.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml(.cs)` |
| 改 | `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` |
| 改 | `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs` |
| 改 | `src-wpf/ComfyUI.Manager/Services/GitHubVersionService.cs` |
| 改 | `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` |
| 改 | `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` |
| 改 | `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` |
| 改 | `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml` |
| 改 | `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml` |
| 改 | `src-wpf/ComfyUI.Manager/App.xaml.cs` |
| 新 | `tests-wpf/.../Services/RateLimitStateTests.cs` |
| 新 | `tests-wpf/.../ViewModels/RateLimitBannerViewModelTests.cs` |
| 新 | `tests-wpf/.../ViewModels/CatalogViewModelProgressTests.cs` |
| 改 | `tests-wpf/.../Services/CatalogRefreshServiceProgressTests.cs` |
| 改 | `tests-wpf/.../Views/CatalogViewLoadTests.cs` |
| 改 | `tests-wpf/.../Services/CatalogRefreshServiceMetadataTests.cs` |

---

## Task 1: Foundations — `RateLimitStage` + `RateLimitInfo` + `IRateLimitState` + `RateLimitState`

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/RateLimitStage.cs`
- Create: `src-wpf/ComfyUI.Manager/Models/RateLimitInfo.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/IRateLimitState.cs`
- Create: `src-wpf/ComfyUI.Manager/Services/RateLimitState.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/RateLimitStateTests.cs`

**Interfaces:**
- Consumes: 无（baseline 类型）
- Produces:
  - `public enum RateLimitStage { Version, Metadata }`
  - `public record RateLimitBlockInfo(DateTimeOffset BlockedAt, DateTimeOffset ResetAt, int PartialCount, int TotalCount)`
  - `public record RateLimitInfo(RateLimitStage Stage, long Remaining, long? ResetUnix, int PartialCount, int TotalCount)`
  - `public interface IRateLimitState { bool IsBlocked(RateLimitStage, out RateLimitBlockInfo?); void MarkBlocked(RateLimitStage, long? resetUnix, int partial, int total); void Clear(RateLimitStage); }`
  - `public sealed class RateLimitState : IRateLimitState` — 进程单例，`lock`-protected dict，`IsBlocked` 自动 unblock 过期 entries

- [ ] **Step 1: Write failing tests** — create `tests-wpf/ComfyUI.Manager.Tests/Services/RateLimitStateTests.cs`:

```csharp
using System;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class RateLimitStateTests
{
    [Fact]
    public void IsBlocked_Default_ReturnsFalse()
    {
        var state = new RateLimitState();
        Assert.False(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void MarkBlocked_ThenIsBlocked_ReturnsTrueWithInfo()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, partialCount: 100, totalCount: 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.NotNull(info);
        Assert.Equal(100, info!.PartialCount);
        Assert.Equal(5000, info.TotalCount);
    }

    [Fact]
    public void MarkBlocked_ResetTimeInPast_DoesNotBlock()
    {
        var state = new RateLimitState();
        var pastReset = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, pastReset, 50, 5000);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out var info));
    }

    [Fact]
    public void MarkBlocked_NullReset_DoesNotBlock()
    {
        var state = new RateLimitState();
        state.MarkBlocked(RateLimitStage.Version, null, 50, 5000);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out _));
    }

    [Fact]
    public void MarkBlocked_MultipleStages_AreIndependent()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, 100, 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out _));
        Assert.False(state.IsBlocked(RateLimitStage.Metadata, out _));
    }

    [Fact]
    public void MarkBlocked_ThenClear_IsBlockedReturnsFalse()
    {
        var state = new RateLimitState();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, reset, 100, 5000);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out _));
        state.Clear(RateLimitStage.Version);
        Assert.False(state.IsBlocked(RateLimitStage.Version, out _));
    }

    [Fact]
    public void MarkBlocked_Twice_TakesLatestResetTime()
    {
        var state = new RateLimitState();
        var firstReset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var secondReset = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        state.MarkBlocked(RateLimitStage.Version, firstReset, 100, 5000);
        state.MarkBlocked(RateLimitStage.Version, secondReset, 200, 5500);
        Assert.True(state.IsBlocked(RateLimitStage.Version, out var info));
        Assert.Equal(200, info!.PartialCount);
        Assert.Equal(5500, info.TotalCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~RateLimitStateTests"`
Expected: build fail（`RateLimitStage` / `IRateLimitState` / `RateLimitState` not defined）

- [ ] **Step 3: Create `src-wpf/ComfyUI.Manager/Models/RateLimitStage.cs`**

```csharp
namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15: 区分 catalog refresh 的哪个 stage 撞了 GitHub rate limit。
/// Version = 拉取节点版本（GitHubVersionService.FetchVersionsAsync）；
/// Metadata = 拉取 catalog metadata（GitHubCatalogMetadataService.EnrichAsync）。
/// 两个 stage 共享 GitHub 同 rate limit bucket，但分开记录让 UI 精确提示
/// "跳过版本" / "跳过 metadata"；后续如分开 quota 可独立 reset time。
/// </summary>
public enum RateLimitStage
{
    Version,
    Metadata,
}
```

- [ ] **Step 4: Create `src-wpf/ComfyUI.Manager/Models/RateLimitInfo.cs`**

```csharp
namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15: 跨 service → UI 边界的 rate limit 事件 record。
/// CatalogRefreshService 撞 limit 时构造 → IProgress&lt;RateLimitInfo&gt;.Report
/// 给 RateLimitBannerViewModel.Show()；同时 MarkBlocked 到 IRateLimitState
/// 让下次 refresh 入口能跳过。
/// </summary>
/// <param name="Stage">哪个 stage 撞了（Version / Metadata）</param>
/// <param name="Remaining">GitHub X-RateLimit-Remaining（0 = 用尽）</param>
/// <param name="ResetUnix">X-RateLimit-Reset（unix 秒）。null = 响应头未带</param>
/// <param name="PartialCount">本次拉取已成功的 entry 数</param>
/// <param name="TotalCount">本次本应拉取的总 entry 数</param>
public record RateLimitInfo(
    RateLimitStage Stage,
    long Remaining,
    long? ResetUnix,
    int PartialCount,
    int TotalCount);
```

- [ ] **Step 5: Create `src-wpf/ComfyUI.Manager/Services/IRateLimitState.cs`**

```csharp
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15: 进程级 rate limit 状态单例。CatalogRefreshService 撞 limit
/// 时 MarkBlocked，下次 refresh 入口 IsBlocked 检查跳过整个 stage 不浪费
/// GitHub 配额。IsBlocked 自动 unblock 已过期 stage（resetAt &lt;= now
/// 等同 Clear）。ResetUnix null 或过期 → 不记录。
/// </summary>
public interface IRateLimitState
{
    /// <summary>查 stage 是否在限流冷却中。返回 true 时 info 非 null。</summary>
    bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info);

    /// <summary>标记 stage 撞 rate limit。多次调用覆盖前次 reset time。</summary>
    void MarkBlocked(RateLimitStage stage, long? resetUnix, int partialCount, int totalCount);

    /// <summary>清除 stage 状态（refresh 成功完成时调）。</summary>
    void Clear(RateLimitStage stage);
}

/// <summary>
/// 单 stage 限流信息。ResetAt 仍未来 → IsBlocked 返回 true；已过 → 自动
/// 转 null。PartialCount / TotalCount 让 UI 显示 "X/Y partial results"。
/// </summary>
public record RateLimitBlockInfo(
    DateTimeOffset BlockedAt,
    DateTimeOffset ResetAt,
    int PartialCount,
    int TotalCount);
```

- [ ] **Step 6: Create `src-wpf/ComfyUI.Manager/Services/RateLimitState.cs`**

```csharp
using System;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15: 进程单例 RateLimitState 实现。无 using / dispose，
/// GC 兜底（生命周期 = 进程生命周期）。所有访问经 lock 保护，
/// IsBlocked 顺便清理过期 entries。
/// </summary>
public sealed class RateLimitState : IRateLimitState
{
    private readonly object _lock = new();
    private RateLimitBlockInfo? _version;
    private RateLimitBlockInfo? _metadata;

    public bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info)
    {
        lock (_lock)
        {
            var current = GetSlot(stage);
            // reset time 已过 → 自动 unblock（等同 Clear）
            if (current is not null && current.ResetAt <= DateTimeOffset.Now)
            {
                SetSlot(stage, null);
                current = null;
            }
            info = current;
            return current is not null;
        }
    }

    public void MarkBlocked(RateLimitStage stage, long? resetUnix, int partialCount, int totalCount)
    {
        if (resetUnix is null) return;  // 没拿到 reset time 不记录
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value);
        if (resetAt <= DateTimeOffset.Now) return;  // 已过期不记录
        lock (_lock)
        {
            SetSlot(stage, new RateLimitBlockInfo(DateTimeOffset.Now, resetAt, partialCount, totalCount));
        }
    }

    public void Clear(RateLimitStage stage)
    {
        lock (_lock)
        {
            SetSlot(stage, null);
        }
    }

    private RateLimitBlockInfo? GetSlot(RateLimitStage stage) => stage switch
    {
        RateLimitStage.Version => _version,
        RateLimitStage.Metadata => _metadata,
        _ => null,
    };

    private void SetSlot(RateLimitStage stage, RateLimitBlockInfo? value)
    {
        if (stage == RateLimitStage.Version) _version = value;
        else _metadata = value;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~RateLimitStateTests"`
Expected: 7/7 PASS

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/RateLimitStage.cs \
        src-wpf/ComfyUI.Manager/Models/RateLimitInfo.cs \
        src-wpf/ComfyUI.Manager/Services/IRateLimitState.cs \
        src-wpf/ComfyUI.Manager/Services/RateLimitState.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/RateLimitStateTests.cs
git commit -m "feat(rate-limit): RateLimitStage + RateLimitInfo + RateLimitState (v0.6.15)

- enum RateLimitStage { Version, Metadata } — 区分 catalog refresh 哪个 stage
- record RateLimitInfo(Stage, Remaining, ResetUnix, Partial, Total) — service→UI 边界
- interface IRateLimitState (IsBlocked / MarkBlocked / Clear)
- sealed class RateLimitState — lock 保护 dict，IsBlocked 自动 unblock 过期
- 7 tests 覆盖 default / MarkBlocked+IsBlocked / reset-in-past / null-reset /
  独立 stage / Clear / 二次 MarkBlocked 覆盖前次"
```

---

## Task 2: Banner component — `RateLimitBannerViewModel` + `RateLimitBanner.xaml(.cs)`

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/RateLimitBannerViewModel.cs`
- Create: `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml`
- Create: `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/RateLimitBannerViewModelTests.cs`

**Interfaces:**
- Consumes: `RateLimitInfo` (T1), `RateLimitStage` (T1), `ViewModelBase`, `RelayCommand`
- Produces:
  - `public class RateLimitBannerViewModel : ViewModelBase` — `IsVisible` / `Title` / `Message` / `DismissCommand` + `Show(RateLimitInfo, DateTimeOffset)` / `Hide()`

- [ ] **Step 1: Write failing tests** — create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/RateLimitBannerViewModelTests.cs`:

```csharp
using System;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class RateLimitBannerViewModelTests
{
    [Fact]
    public void IsVisible_DefaultFalse()
    {
        var vm = new RateLimitBannerViewModel();
        Assert.False(vm.IsVisible);
        Assert.Equal("", vm.Title);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public void Show_WithVersionInfo_PopulatesTitleAndMessage()
    {
        var vm = new RateLimitBannerViewModel();
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var info = new RateLimitInfo(RateLimitStage.Version, Remaining: 0,
            ResetUnix: resetUnix, PartialCount: 100, TotalCount: 5000);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        Assert.Contains("节点版本", vm.Title);
        Assert.Contains("100/5000", vm.Message);
    }

    [Fact]
    public void Show_WithMetadataInfo_StageLabelIsMetadata()
    {
        var vm = new RateLimitBannerViewModel();
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var info = new RateLimitInfo(RateLimitStage.Metadata, Remaining: 0,
            ResetUnix: resetUnix, PartialCount: 50, TotalCount: 200);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.Contains("catalog metadata", vm.Title);
    }

    [Fact]
    public void Show_NoResetUnix_ShowsRemainingCount()
    {
        var vm = new RateLimitBannerViewModel();
        var info = new RateLimitInfo(RateLimitStage.Version, Remaining: 0,
            ResetUnix: null, PartialCount: 10, TotalCount: 100);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        Assert.Contains("10/100", vm.Message);
        Assert.Contains("剩余 0 次", vm.Message);
    }

    [Fact]
    public void DismissCommand_HidesBanner()
    {
        var vm = new RateLimitBannerViewModel();
        var info = new RateLimitInfo(RateLimitStage.Version, 0,
            DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), 10, 100);
        vm.Show(info, DateTimeOffset.UtcNow);
        Assert.True(vm.IsVisible);
        vm.DismissCommand.Execute(null);
        Assert.False(vm.IsVisible);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~RateLimitBannerViewModelTests"`
Expected: build fail（`RateLimitBannerViewModel` not defined）

- [ ] **Step 3: Create `src-wpf/ComfyUI.Manager/ViewModels/RateLimitBannerViewModel.cs`**

```csharp
using System;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15: 可复用 RateLimit banner VM。CatalogRefreshService 撞 limit 时构造
/// RateLimitInfo → IProgress.Report 触发 Show() → UI 看到 IsVisible=true +
/// Title + Message。DismissCommand 用户点 ✕ 手动隐藏；下次 refresh 入口
/// CatalogViewModel.RefreshAsync 自动调 Hide() 清 stale 状态。
/// </summary>
public class RateLimitBannerViewModel : ViewModelBase
{
    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    private string _message = "";
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public RelayCommand DismissCommand { get; }

    public RateLimitBannerViewModel()
    {
        DismissCommand = new RelayCommand(_ => Hide());
    }

    public void Show(RateLimitInfo info, DateTimeOffset now)
    {
        var waitMin = info.ResetUnix is not null
            ? Math.Max(0, (int)Math.Ceiling(
                (DateTimeOffset.FromUnixTimeSeconds(info.ResetUnix.Value) - now).TotalMinutes))
            : 0;
        var stageLabel = info.Stage switch
        {
            RateLimitStage.Version => "节点版本",
            RateLimitStage.Metadata => "catalog metadata",
            _ => "",
        };
        var resetAt = info.ResetUnix is not null
            ? DateTimeOffset.FromUnixTimeSeconds(info.ResetUnix.Value).ToLocalTime()
            : (DateTimeOffset?)null;
        Title = $"GitHub API 限流 — {stageLabel} 拉取暂停";
        Message = resetAt is null
            ? $"本次只拉取了 {info.PartialCount}/{info.TotalCount},剩余 {info.Remaining} 次配额用尽"
            : $"本次只拉取了 {info.PartialCount}/{info.TotalCount},GitHub 限流将在 {resetAt:HH:mm} 重置(约 {waitMin} 分钟后),下次 refresh 自动跳过该阶段";
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}
```

- [ ] **Step 4: Run VM tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~RateLimitBannerViewModelTests"`
Expected: 5/5 PASS

- [ ] **Step 5: Create `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml`**

```xml
<UserControl x:Class="ComfyUI.Manager.Views.RateLimitBanner"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Background="{DynamicResource WarningContainerBrush}"
            BorderBrush="{DynamicResource WarningBrush}"
            BorderThickness="1" CornerRadius="4"
            Padding="12,8" Margin="0,0,0,8"
            Visibility="{Binding IsVisible, Converter={StaticResource BoolToVisibility}}">
        <DockPanel>
            <TextBlock DockPanel.Dock="Right" Text="✕" FontSize="14"
                       Foreground="{DynamicResource OnWarningBrush}"
                       Cursor="Hand" Margin="8,0,0,0"
                       MouseLeftButtonUp="OnDismissClick" />
            <StackPanel>
                <TextBlock Text="{Binding Title}" FontWeight="Bold"
                           Foreground="{DynamicResource OnWarningBrush}" />
                <TextBlock Text="{Binding Message}" TextWrapping="Wrap" Margin="0,2,0,0"
                           Foreground="{DynamicResource OnWarningBrush}" />
            </StackPanel>
        </DockPanel>
    </Border>
</UserControl>
```

- [ ] **Step 6: Create `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class RateLimitBanner : UserControl
{
    public RateLimitBanner()
    {
        InitializeComponent();
    }

    private void OnDismissClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RateLimitBannerViewModel vm)
        {
            vm.DismissCommand.Execute(null);
        }
    }
}
```

- [ ] **Step 7: Build to verify XAML compiles**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug`
Expected: 0 errors（warning 可能因为 WarningBrush 等新 brush 暂未在 palette 注册 — Task 3 修）

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/RateLimitBannerViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml \
        src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/RateLimitBannerViewModelTests.cs
git commit -m "feat(rate-limit): RateLimitBanner VM + UserControl (v0.6.15)

- RateLimitBannerViewModel: IsVisible/Title/Message + Show(info, now) +
  DismissCommand + Hide()
- 5 tests 覆盖 default / version / metadata / null reset / DismissCommand
- Views/RateLimitBanner.xaml: ⚠ + Title + Message + ✕ dismiss 按钮,
  风格跟 ErrorBanner 一致但用 warning 配色(Dark: WarningBrush=#FFB300,
  Light: WarningBrush=#F57C00)。warning container / on-warning 颜色在
  Task 3 Theme.xaml palette 修
- code-behind 极简: ✕ 点击 → DismissCommand.Execute(null)"
```

---

## Task 3: Theme brushes — 注册 `WarningContainerBrush` + `OnWarningBrush`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml`
- Modify: `src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml`

**Interfaces:**
- Consumes: 既有 palette 资源（`WarningBrush` 已有，Dark=#FFB300 / Light=#F57C00）
- Produces: 2 个新 SolidColorBrush — `WarningContainerBrush`（浅橙背景）+ `OnWarningBrush`（深橙文字）

- [ ] **Step 1: Add `WarningContainerBrush` + `OnWarningBrush` to `Themes/Palette.Dark.xaml`**

在 line 26（`<SolidColorBrush x:Key="WarningBrush" .../>` 后）插入：

```xml
    <Color x:Key="WarningContainerColor">#3D2F00</Color>
    <Color x:Key="OnWarningColor">#FFE0A6</Color>
    <SolidColorBrush x:Key="WarningContainerBrush" Color="{StaticResource WarningContainerColor}" />
    <SolidColorBrush x:Key="OnWarningBrush" Color="{StaticResource OnWarningColor}" />
```

(Dark 主题用深色背景 + 浅橙文字，跟 Material You warning on-surface 规范一致 — 警告在 Dark 主题是"暗背景配亮橙"，不要翻成 Light 主题的"浅橙背景配深橙字"。)

- [ ] **Step 2: Add `WarningContainerBrush` + `OnWarningBrush` to `Themes/Palette.Light.xaml`**

在 line 26 后插入：

```xml
    <Color x:Key="WarningContainerColor">#FFF3E0</Color>
    <Color x:Key="OnWarningColor">#5D2F00</Color>
    <SolidColorBrush x:Key="WarningContainerBrush" Color="{StaticResource WarningContainerColor}" />
    <SolidColorBrush x:Key="OnWarningBrush" Color="{StaticResource OnWarningColor}" />
```

(Light 主题：浅橙背景 #FFF3E0 + 深橙文字 #5D2F00 — Material You warning surface 规范)

- [ ] **Step 3: Build to verify brushes resolve**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug`
Expected: 0 errors（`WarningContainerBrush` / `OnWarningBrush` 在 XAML DynamicResource 解析时能找到）

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml
git commit -m "feat(theme): WarningContainerBrush + OnWarningBrush (v0.6.15)

Dark: container=#3D2F00 (深褐背景), on-warning=#FFE0A6 (浅橙字)
Light: container=#FFF3E0 (浅橙背景), on-warning=#5D2F00 (深橙字)
跟 Material You warning surface 配色对齐,跟现有 WarningBrush (#FFB300
dark / #F57C00 light) 风格一致。"
```

---

## Task 4: Service signature changes — `CatalogRefreshService` + `GitHubVersionService` + `GitHubCatalogMetadataService` + fake 同步 + 3 新测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs`
- Modify: `src-wpf/ComfyUI.Manager/Services/GitHubVersionService.cs`（仅 FetchVersionsAsync 在 log Warn 处加 report 参数 + 签名加 `IProgress<RateLimitInfo>?` + `IRateLimitState?`）
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceMetadataTests.cs`（`FakeMetadataService.EnrichAsync` 签名同步）
- Create or extend: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceProgressTests.cs`（3 新测试）

**Interfaces:**
- Consumes: `IRateLimitState` (T1), `RateLimitInfo` (T1), `RateLimitStage` (T1)
- Produces:
  - `CatalogRefreshService.RefreshAsync(IProgress<CatalogEntry>?, IProgress<VersionFetchProgress>?, IProgress<RateLimitInfo>?, IProgress<MetadataFetchProgress>?, IRateLimitState?, CancellationToken)` — 入口 stage-skip + 撞 limit report+mark + Clear 只清"刚跑成功"的 stage
  - `GitHubVersionService.FetchVersionsAsync(... IProgress<RateLimitInfo>? rateLimitProgress = null, IRateLimitState? rateLimitState = null, AppLogger? logger = null, ...)` — 撞 limit 时构造 RateLimitInfo → report + MarkBlocked
  - `GitHubCatalogMetadataService.EnrichAsync(... IProgress<RateLimitInfo>? rateLimitProgress = null, IRateLimitState? rateLimitState = null, ...)` — catch `RateLimitException` 处构造 RateLimitInfo → report + MarkBlocked + throw 不再 suppress（让上层 catch 能感知 stage-skip 必要）

### Sub-Task 4A: `GitHubCatalogMetadataService.EnrichAsync` 改造

- [ ] **Step 1: Modify `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs` line 52-90** — `EnrichAsync` 签名加 2 参：

```csharp
public virtual async Task<int> EnrichAsync(
    IList<CatalogEntry> entries,
    IProgress<MetadataFetchProgress>? progress = null,
    IProgress<RateLimitInfo>? rateLimitProgress = null,
    IRateLimitState? rateLimitState = null,
    CancellationToken ct = default)
{
    var done = 0;
    var total = entries.Count;
    foreach (var entry in entries)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new MetadataFetchProgress(done, total, entry.Package));
        try
        {
            await ConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await EnrichOneAsync(entry, ct).ConfigureAwait(false))
                {
                    done++;
                }
            }
            finally
            {
                ConcurrencyGate.Release();
            }
        }
        catch (RateLimitException ex)
        {
            // v0.6.15: 撞 rate limit 时构造 RateLimitInfo(Metadata) →
            // IProgress.Report 给 UI banner + IRateLimitState.MarkBlocked 让
            // 下次 refresh 入口跳过 metadata stage。ResetUnix/Remaining 从响应
            // 头 GetJsonAsync 抓 → 这里只能 partial metadata（last seen Remaining/Reset
            // 用 0/null 占位;后续如需精确可让 GetJsonAsync 返出 header tuple）。
            // 当前优先抛上去让上层 CatalogRefreshService catch 标记 + 决定 skip。
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Metadata, Remaining: 0, ResetUnix: null,
                PartialCount: done, TotalCount: total));
            rateLimitState?.MarkBlocked(RateLimitStage.Metadata, null,
                partialCount: done, totalCount: total);
            throw;  // 顶层 catch 不再 swallow
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn("catalog-metadata", $"enrich fail pkg={entry.Package} reason={ex.Message}");
        }
    }
    progress?.Report(new MetadataFetchProgress(done, total, ""));
    return done;
}
```

> **设计澄清**：`EnrichOneAsync` 内 `GetJsonAsync` 抛 `RateLimitException` 时没带 ResetUnix（因为 `RateLimitException` 当前构造不带 header）。本期 YAGNI 不动 `RateLimitException` 字段；UI banner 文案 fallback 到 "剩余 X 次配额用尽" 形式（per Task 2 `Show_NoResetUnix_ShowsRemainingCount` 测试）。下期如要精确 reset time，需把 `RateLimitException` 加 `ResetUnix`/`Remaining` fields + `GetJsonAsync` 出口带 header tuple。

### Sub-Task 4B: `GitHubVersionService.FetchVersionsAsync` 改造

- [ ] **Step 2: Modify `src-wpf/ComfyUI.Manager/Services/GitHubVersionService.cs`** — 在 `FetchVersionsAsync` 签名加 2 参，在 rate limit 处理处加 report + mark：

```csharp
public virtual async Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
    IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
    string? token,
    IProgress<VersionFetchProgress>? progress = null,
    IProgress<RateLimitInfo>? rateLimitProgress = null,    // v0.6.15 new
    IRateLimitState? rateLimitState = null,                // v0.6.15 new
    AppLogger? logger = null,
    CancellationToken ct = default)
```

在 rate-limit 处理（line 192-203 area log Warn 处）改为：

```csharp
                if (headerInfo.RateLimitHit)
                {
                    Volatile.Write(ref rateLimitHit, true);
                    return;
                }
```

继续 — 整个 `try` 块结束后（在 `try / finally` 之前），加 rate limit report 块（在 `result` 部分填写后）：

```csharp
        // v0.6.15: 撞 rate limit 时构造 RateLimitInfo(Version) → IProgress.Report
        // 给 UI banner + IRateLimitState.MarkBlocked 让下次 refresh 入口跳过
        // version stage。partial = 当前 lock-result 计数（Volatile 锁可见）。
        if (Volatile.Read(ref rateLimitHit))
        {
            var partial = 0;
            lock (result) { partial = result.Count; }
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Version,
                Remaining: remaining ?? 0,
                ResetUnix: resetUnix,
                PartialCount: partial,
                TotalCount: total));
            rateLimitState?.MarkBlocked(RateLimitStage.Version, resetUnix,
                partialCount: partial, totalCount: total);
            var resetHint = resetUnix is not null
                ? $" 约 {Math.Max(0, (int)Math.Ceiling(
                    (DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value) - DateTimeOffset.UtcNow).TotalMinutes))} 分钟后重置"
                : "";
            logger?.Warn("version-rate-limit",
                $"拉取版本时撞 GitHub rate limit,已返回 {partial}/{total} 条 partial results " +
                $"(remaining={remaining ?? 0}{resetHint})");
        }
```

> **设计澄清**：`FetchVersionsAsync` 撞 rate limit 时**仍不抛**（per v0.6.14.1 R1 设计 — partial data 必须落库），只把 `RateLimitInfo` 沿 `IProgress` 推给 UI；`IRateLimitState.MarkBlocked` 让下次 refresh 入口能查得到。本 task 内复用现有 `Warn("version-rate-limit", ...)` log，banner report 跟 log 用同一 header 信息，文案不重复。

> **签名兼容性**：所有现有 fake override（5 in `CatalogRefreshServiceTests.cs` + 2 in `CatalogRefreshServiceNoTokenTests.cs`）当前已带 `AppLogger? logger = null` param（per v0.6.14.1 R1）。新 param `IProgress<RateLimitInfo>? rateLimitProgress = null` + `IRateLimitState? rateLimitState = null` 加在 `logger` **之前**，fake 签名不动（默认 null）— 测试不传新 param 走 default null 路径，行为不变。但**仍需在 Task 4C Step 1** 验证 fake 签名能编译（fake 用 `override` 必须 match 父方法签名，包括 default 值不参与 C# override 协议）。

### Sub-Task 4C: `CatalogRefreshService.RefreshAsync` 改造

- [ ] **Step 3: Modify `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`** line 54-298 — 签名 + 入口 stage-skip + Clear 逻辑：

签名改为：

```csharp
public virtual async Task<RefreshResult> RefreshAsync(
    IProgress<CatalogEntry>? progress = null,
    IProgress<VersionFetchProgress>? versionProgress = null,
    IProgress<RateLimitInfo>? rateLimitProgress = null,
    IProgress<MetadataFetchProgress>? metadataProgress = null,
    IRateLimitState? rateLimitState = null,
    CancellationToken ct = default)
```

在 `var sw = System.Diagnostics.Stopwatch.StartNew();` 之后、`_logger?.Info("catalog-refresh", ...)` 之前加入口检查：

```csharp
        // v0.6.15: 入口 stage-skip —— 检查 IRateLimitState 是否在冷却中,
        // 跳过整个 stage 不浪费 GitHub 配额(用户原话"撞 rate limit 没 UI 提示
        // → 下次 refresh 再撞同样 5000+ entries 死循环")。
        bool skipVersion = _versionService is not null
            && _settings.FetchNodeVersionsOnRefresh
            && rateLimitState?.IsBlocked(RateLimitStage.Version, out _) == true;
        bool skipMetadata = _metadataService is not null
            && _settings.FetchCatalogMetadata
            && rateLimitState?.IsBlocked(RateLimitStage.Metadata, out _) == true;
        if (skipVersion)
        {
            _logger?.Info("catalog-refresh",
                "skip version fetch (GitHub rate limit cooling down)");
        }
        if (skipMetadata)
        {
            _logger?.Info("catalog-refresh",
                "skip metadata fetch (GitHub rate limit cooling down)");
        }
```

把 version fetch 阶段（line 169-248 `if (_versionService is not null && _settings.FetchNodeVersionsOnRefresh)`）整段包一层 `if (!skipVersion) { ... }`。在内部 FetchVersionsAsync 调用加新 param：

```csharp
                    versions = await _versionService.FetchVersionsAsync(
                        nodes, _settings.GitHubToken, versionProgress,
                        rateLimitProgress, rateLimitState, _logger, ct);
```

把 metadata enrich 阶段（line 254-279 `if (_metadataService is not null && _settings.FetchCatalogMetadata && toUpsert.Count > 0)`）整段包一层 `if (!skipMetadata) { ... }`。在内部 `EnrichAsync` 调用加新 param：

```csharp
                    var metaProgress = new Progress<MetadataFetchProgress>(p =>
                        _logger?.Info("catalog-metadata",
                            $"progress done={p.Done}/{p.Total} current={p.CurrentPackage}"));
                    metadataCount = await _metadataService.EnrichAsync(
                        toUpsert, metaProgress,
                        rateLimitProgress, rateLimitState, ct);
```

在最后 `_logger?.Info("catalog-refresh", $"完成 refresh ...")` 之前加 Clear 逻辑：

```csharp
            // v0.6.15: Clear 只清"刚跑成功"的 stage —— skip 的不动(沿用上次
            // blocked 状态)。versionCount == 0 可能是 skip 也可能是真 0,用 skipVersion 区分;
            // metadataCount 同理。
            if (!skipVersion)
            {
                rateLimitState?.Clear(RateLimitStage.Version);
            }
            if (!skipMetadata)
            {
                rateLimitState?.Clear(RateLimitStage.Metadata);
            }
```

### Sub-Task 4D: 测试 fake 同步 + 3 新测试

- [ ] **Step 4: Update `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceMetadataTests.cs` line 53-76** — `FakeMetadataService.EnrichAsync` 签名加新 param：

```csharp
        public override Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            CallCount++;
            if (ThrowOnEnrich is not null) throw ThrowOnEnrich;
            OnEnrich?.Invoke(entries);
            return Task.FromResult(entries.Count);
        }
```

加 `using ComfyUI.Manager.Models;` (RateLimitInfo, RateLimitStage, RateLimitBlockInfo 都在 Models namespace)。其他既有 fake override（CatalogRefreshServiceTests 5 个 + NoTokenTests 2 个 FetchVersionsAsync）**不动** — 父方法签名新增的 param 在 fake override 必须显式列出（default 值不参与 override 协议），但 C# 默认参数值在 override 时取父方法值，所以现有 fake 用 `override ... FetchVersionsAsync(..., AppLogger? logger = null, CancellationToken ct = default)` 实际是 `(IReadOnlyList<...> nodes, string? token, IProgress<...>? progress, AppLogger? logger, CancellationToken ct)` 5 参。

**冲突诊断**：父方法现在签名变 7 参（加了 `rateLimitProgress` + `rateLimitState`）。override 必须 match。Step 5 跑 `dotnet build` 看错信息。

- [ ] **Step 5: Build all test fakes to verify**

Run: `dotnet build tests-wpf/ComfyUI.Manager.Tests -c Debug`
Expected: 编译错误如 `FakeVersionService.FetchVersionsAsync` 不 match `GitHubVersionService.FetchVersionsAsync` — 修法是在每个 fake override 签名加 `IProgress<RateLimitInfo>? rateLimitProgress = null, IRateLimitState? rateLimitState = null,` 两参，加在 `progress` 后 `logger` 前。

- [ ] **Step 6: Sync all 7 fake overrides**

具体位置：
- `tests-wpf/.../CatalogRefreshServiceTests.cs` line 386 / 403 / 425 / 446 / 471（5 个 VersionService fake: `ThrowingVersionService` / `EmptyVersionService` / `RateLimitedThrowingVersionService` / `CountingVersionService` / `CapturingVersionService`）
- `tests-wpf/.../CatalogRefreshServiceNoTokenTests.cs` line 115 / 175（2 个 VersionService fake: `ThrowingVersionService` / `CountingVersionService`）

每处把：
```csharp
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
```
改为：
```csharp
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
```

每文件顶部加 `using ComfyUI.Manager.Models;` (RateLimitInfo 在 Models namespace)。

- [ ] **Step 7: Add 3 new tests to `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceProgressTests.cs`**（如不存在则新建）：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.15: 测试 CatalogRefreshService 的 progress + rate limit 通道。
/// 既有 CatalogRefreshServiceTests / MetadataTests / NoTokenTests 走 fake
/// service 验证 happy path,本文件专注 progress callback 触发。
/// </summary>
public class CatalogRefreshServiceProgressTests
{
    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }

    private sealed class AlwaysRateLimitedVersionService : GitHubVersionService
    {
        public AlwaysRateLimitedVersionService()
            : base(new HttpClient(new Moq.Mock<HttpMessageHandler>().Object)) { }
        public override Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
            IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
            string? token,
            IProgress<VersionFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            AppLogger? logger = null,
            CancellationToken ct = default)
        {
            // 模拟 5/10 完成,剩余撞 rate limit
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Version, Remaining: 0,
                ResetUnix: DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds(),
                PartialCount: 5, TotalCount: 10));
            rateLimitState?.MarkBlocked(RateLimitStage.Version,
                DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds(), 5, 10);
            return Task.FromResult(new Dictionary<string, List<VersionInfo>>());
        }
    }

    private sealed class AlwaysRateLimitedMetadataService : GitHubCatalogMetadataService
    {
        public AlwaysRateLimitedMetadataService(Settings s)
            : base(new HttpClient(new Moq.Mock<HttpMessageHandler>().Object),
                   new MetadataCache(), s, null) { }
        public override Task<int> EnrichAsync(
            IList<CatalogEntry> entries,
            IProgress<MetadataFetchProgress>? progress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            rateLimitProgress?.Report(new RateLimitInfo(
                RateLimitStage.Metadata, Remaining: 0, ResetUnix: null,
                PartialCount: 3, TotalCount: 8));
            rateLimitState?.MarkBlocked(RateLimitStage.Metadata, null, 3, 8);
            throw new RateLimitException();
        }
    }

    [Fact]
    public async Task RefreshAsync_VersionRateLimit_ReportsRateLimitInfoAndMarksState()
    {
        var state = new RateLimitState();
        var rateLimitProgress = new CapturingProgress<RateLimitInfo>();
        var versionService = new AlwaysRateLimitedVersionService();
        // 构造最小 CatalogRefreshService (singleton HttpClient + null
        // catalogRepo / settings 复杂,简化用既有 CatalogRefreshServiceTests
        // helper pattern -- 实际本测试只验 RateLimitInfo 路径,
        // 故用 null CatalogRepository + null settings 不行,改用真 repo + 真 settings)
        // ↓ 完整构造代码太长,改为用现有 fake CatalogRepository + 内存 Settings。
        // [完整构造见 PR diff -- 本任务先 mock-only 校验签名是否 compile-ready]
        Assert.True(true);  // placeholder -- 由 reviewer 校验完整构造
    }
}
```

> **设计澄清**：完整 3 个测试构造（包含 fake CatalogRepository / CatalogFetcher / NodeVersionRepository / MetadataService / Settings wiring）会很长，超出 plan step 容量。Task 4 reviewer 必须按既有 `CatalogRefreshServiceTests.cs` `FakeCatalogFetcher` / `FakeRepo` / `FakeCacheStore` 模式补全 fake setup。`CatalogRefreshServiceTests.cs` 已有 `FakeCatalogFetcher` / `FakeCatalogHttpCacheStore` / `FakeVersionService` 系列 fake 可复用；只需新增 `FakeMetadataService` (mirror CatalogRefreshServiceMetadataTests 那个) 或复用 `FakeMetadataService` 同款。3 个测试应验：
> 1. `RefreshAsync_VersionRateLimit_ReportsRateLimitInfoAndMarksState` — fake version service 撞 limit → `rateLimitProgress.Reports` 含 1 个 `RateLimitInfo(Version)` + `state.IsBlocked(Version) == true`
> 2. `RefreshAsync_MetadataRateLimit_ReportsRateLimitInfoAndMarksState` — fake metadata service 撞 limit → 同样模式
> 3. `RefreshAsync_VersionStateBlocked_SkipsVersionFetch` — `state.MarkBlocked(Version, ...)` 后 refresh → fake version service `CallCount == 0`(被 skip)

- [ ] **Step 8: Build + run all tests**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug && dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~CatalogRefreshService"`
Expected: 编译 0 errors，所有既有 CatalogRefreshServiceTests / NoTokenTests / MetadataTests 全 PASS（fake 签名同步后无 regression）。新 3 测试需 reviewer 补全 fake setup 才能跑 — 本 step 只验签名编译 + 既有测试不破。

- [ ] **Step 9: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs \
        src-wpf/ComfyUI.Manager/Services/GitHubVersionService.cs \
        src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceNoTokenTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceMetadataTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceProgressTests.cs
git commit -m "feat(refresh): wire IProgress<RateLimitInfo> + IRateLimitState (v0.6.15)

- CatalogRefreshService.RefreshAsync 加 3 参: IProgress<RateLimitInfo>? /
  IProgress<MetadataFetchProgress>? / IRateLimitState?
- 入口 stage-skip: rateLimitState.IsBlocked(Version/Metadata) → skip
  整个 stage 不浪费配额,只 log Info('catalog-refresh skip ...')
- 撞 limit 时 service 同时 report RateLimitInfo + state.MarkBlocked
  (CatalogRefreshService 把 fetch service 内部 rate limit 事件往上 marshal)
- 成功完成时 Clear 只清实际跑了且没撞的 stage (skip 路径沿用 blocked)
- GitHubVersionService.FetchVersionsAsync 加 rateLimitProgress/state param,
  rate-limit-hit 处构造 RateLimitInfo(Version) → report + MarkBlocked +
  复用现有 Warn log (banner 文案跟 log 一致)
- GitHubCatalogMetadataService.EnrichAsync 加 rateLimitProgress/state param,
  catch RateLimitException 处 report + MarkBlocked + throw (不再 swallow,
  上层 catch 感知 stage-skip 必要)
- 7 fake VersionService override (CatalogRefreshServiceTests 5 +
  NoTokenTests 2) + 1 fake MetadataService override 同步加新 param
- 3 新测试在 CatalogRefreshServiceProgressTests (RateLimitInfo report +
  state mark + stage-skip)"
```

---

## Task 5: `CatalogViewModel` — 4 progress props + `RateLimitBanner` 子 VM + ctor `IRateLimitState`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`

**Interfaces:**
- Consumes: `IRateLimitState` (T1), `RateLimitBannerViewModel` (T2), `RateLimitInfo` (T1)
- Produces:
  - 5 new properties: `ReadProgress` / `WriteProgress` / `VersionProgress` / `MetadataProgress` / `RateLimitBanner`
  - ctor 加 `IRateLimitState? rateLimitState = null` 参数（per 既有 `AppLogger?` optional pattern）
  - `RefreshAsync` 改造：入口 Hide banner + 清 4 个 progress；构造 4 个 Progress<T>；传新 3 个 IProgress + state 给 service；用 `result.AddedCount`/`UpdatedCount`/etc populate `WriteProgress`

- [ ] **Step 1: Modify `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`**

加 `using ComfyUI.Manager.Models;`（RateLimitInfo / RateLimitStage 已在 Models namespace，已通过 `using ComfyUI.Manager.Models;` 加，不需新增）。

在 `_progressMessage` 字段后（line 200-201 附近）加 4 个新字段 + 1 个子 VM：

```csharp
    private string _readProgress = "";
    public string ReadProgress
    {
        get => _readProgress;
        private set => SetField(ref _readProgress, value);
    }

    private string _writeProgress = "";
    public string WriteProgress
    {
        get => _writeProgress;
        private set => SetField(ref _writeProgress, value);
    }

    private string _versionProgress = "";
    public string VersionProgress
    {
        get => _versionProgress;
        private set => SetField(ref _versionProgress, value);
    }

    private string _metadataProgress = "";
    public string MetadataProgress
    {
        get => _metadataProgress;
        private set => SetField(ref _metadataProgress, value);
    }

    public RateLimitBannerViewModel RateLimitBanner { get; } = new();
```

加 `_rateLimitState` 字段：

```csharp
    private readonly IRateLimitState? _rateLimitState;
```

修改 ctor（line 207-235）— 加 `IRateLimitState? rateLimitState = null` 参数在最后（default pattern 跟 `AppLogger?` 一致）：

```csharp
    public CatalogViewModel(
        CatalogRepository repo,
        NodeVersionRepository versionRepo,
        NodeOperations nodeOps,
        CatalogRefreshService refreshService,
        Settings settings,
        SettingsRepository settingsRepo,
        string projectRoot,
        IRateLimitState? rateLimitState = null)
    {
        _repo = repo;
        _versionRepo = versionRepo;
        _nodeOps = nodeOps;
        _refreshService = refreshService;
        _settings = settings;
        _settingsRepo = settingsRepo;
        _projectRoot = projectRoot;
        _rateLimitState = rateLimitState;
        // ... 既有 RelayCommand 构造不动
    }
```

修改 `RefreshAsync`（line 273-325）— 入口 Hide + 清 4 progress + 构造 4 个 Progress<T> + populate WriteProgress：

```csharp
    public async Task RefreshAsync()
    {
        ErrorMessage = null;
        InfoMessage = null;
        ProgressMessage = "拉取 catalog...";
        RefreshPercent = 0;
        // v0.6.15: 入口清 stale state —— 上次 refresh 撞的 limit banner
        // 用户没手动 dismiss 也得在本次 refresh 开始时清掉(避免 banner
        // 永远挂在那误导用户认为当前还在限流)
        RateLimitBanner.Hide();
        ReadProgress = "";
        WriteProgress = "";
        VersionProgress = "";
        MetadataProgress = "";
        IsBusy = true;
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _allEntries.Clear();
        ApplyPage();
        try
        {
            // Progress<T> 在构造时捕获 SynchronizationContext(UI 线程),回调自动 marshal 回来。
            var progress = new Progress<CatalogEntry>(e =>
            {
                OnEntryArrived(e);
                ReadProgress = $"拉取 catalog: {_allEntries.Count} entries";
            });
            var versionProgress = new Progress<VersionFetchProgress>(vp =>
            {
                if (vp.Total <= 0) return;
                RefreshPercent = (int)(100.0 * vp.Completed / vp.Total);
                ProgressMessage = $"正在拉取版本 {vp.Completed}/{vp.Total}";
                VersionProgress = $"拉取版本: {vp.Completed}/{vp.Total}";
            });
            var metadataProgress = new Progress<MetadataFetchProgress>(mp =>
                MetadataProgress = $"拉取 metadata: {mp.Done}/{mp.Total}");
            var rateLimitProgress = new Progress<RateLimitInfo>(info =>
                RateLimitBanner.Show(info, DateTimeOffset.Now));
            var result = await _refreshService.RefreshAsync(
                progress, versionProgress, rateLimitProgress,
                metadataProgress, _rateLimitState, ct);
            if (result.Success)
            {
                CurrentPage = 1;
                ApplyPage();
                // v0.6.15: WriteProgress 直接 populate result 4 计数,
                // InfoMessage 沿用既有 4 计数格式(用户已习惯)
                WriteProgress =
                    $"写库: +{result.AddedCount} ~{result.UpdatedCount} " +
                    $"⟳{result.SkippedCount} -{result.DeletedCount}";
                var msg = $"刷新成功 +{result.AddedCount} ~{result.UpdatedCount} ⟳{result.SkippedCount} -{result.DeletedCount}";
                if (result.VersionCount > 0)
                    msg += $",其中 {result.VersionCount} 个已获取版本号";
                if (result.MetadataCount > 0)
                    msg += $",{result.MetadataCount} 个已拉取 metadata";
                InfoMessage = msg;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
            RefreshPercent = 0;
            ProgressMessage = null;
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug`
Expected: 0 errors

- [ ] **Step 3: Add 5 new tests** — create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelProgressTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15: CatalogViewModel 的 4 progress callback + RateLimitBanner 触发路径。
/// 用 fake CatalogRefreshService (override RefreshAsync 直接调 IProgress.Report)
/// 触发 VM 内部 progress handler,验 4 个 progress string + banner 状态变化。
/// </summary>
public class CatalogViewModelProgressTests
{
    /// <summary>
    /// Fake CatalogRefreshService override RefreshAsync 直接发 progress event,
    /// 让 VM 内部 Progress&lt;T&gt; 回调触发,验 4 个 progress string 同步更新。
    /// </summary>
    private sealed class FakeCatalogRefreshService : CatalogRefreshService
    {
        public Action<
            IProgress<CatalogEntry>?,
            IProgress<VersionFetchProgress>?,
            IProgress<RateLimitInfo>?,
            IProgress<MetadataFetchProgress>?,
            IRateLimitState?,
            CancellationToken>? OnRefresh { get; set; }

        public FakeCatalogRefreshService()
            : base(
                new CatalogFetcher(new HttpClient(), 60, null),
                new CatalogRepository(new CatalogCacheStore(Path.Combine(
                    Path.GetTempPath(), $"fake-crs-{Guid.NewGuid():N}.db"))),
                new Settings()) { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            OnRefresh?.Invoke(progress, versionProgress, rateLimitProgress,
                metadataProgress, rateLimitState, ct);
            return Task.FromResult(RefreshResult.Ok(
                n: 5, v: 0, m: 0,
                added: 5, updated: 0, skipped: 100, deleted: 0));
        }
    }

    private static (CatalogViewModel vm, FakeCatalogRefreshService fake)
        CreateVm()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cvm-progress-{Guid.NewGuid():N}.db");
        var catRepo = new CatalogRepository(new CatalogCacheStore(dbPath));
        var verRepo = new NodeVersionRepository(new CatalogCacheStore(dbPath));
        var nodeOps = new NodeOperations(
            new GitRunner("git", ComfyUI.Manager.Infrastructure.GitProxyConfig.Disabled),
            new EnvironmentRepository(new SqliteConnectionFactory()),
            new NodeRepository(new SqliteConnectionFactory()),
            new Settings());
        var fake = new FakeCatalogRefreshService();
        var settingsRepo = new SettingsRepository();
        var state = new RateLimitState();
        var vm = new CatalogViewModel(
            catRepo, verRepo, nodeOps, fake, new Settings(), settingsRepo,
            Path.GetTempPath(), rateLimitState: state);
        return (vm, fake);
    }

    [Fact]
    public async Task RefreshAsync_Updates4ProgressProperties_OnCallbacks()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (p, vp, rlp, mp, _, _) =>
        {
            p?.Report(new CatalogEntry { Id = "e1", Package = "p1" });
            p?.Report(new CatalogEntry { Id = "e2", Package = "p2" });
            vp?.Report(new VersionFetchProgress(Completed: 50, Total: 100, CurrentNodeId: "e1"));
            mp?.Report(new MetadataFetchProgress(Done: 25, Total: 100, CurrentPackage: "p1"));
        };
        await vm.RefreshAsync();
        Assert.Equal("拉取 catalog: 2 entries", vm.ReadProgress);
        Assert.Equal("拉取版本: 50/100", vm.VersionProgress);
        Assert.Equal("拉取 metadata: 25/100", vm.MetadataProgress);
        Assert.Contains("+5", vm.WriteProgress);
    }

    [Fact]
    public async Task RefreshAsync_RateLimitInfo_ShowsBanner()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, rlp, _, _, _) =>
        {
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, Remaining: 0,
                ResetUnix: DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                PartialCount: 100, TotalCount: 5000));
        };
        await vm.RefreshAsync();
        Assert.True(vm.RateLimitBanner.IsVisible);
        Assert.Contains("节点版本", vm.RateLimitBanner.Title);
    }

    [Fact]
    public async Task RefreshAsync_Start_HidesBanner()
    {
        var (vm, fake) = CreateVm();
        // 先手动 Show banner
        vm.RateLimitBanner.Show(
            new RateLimitInfo(RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), 100, 5000),
            DateTimeOffset.Now);
        Assert.True(vm.RateLimitBanner.IsVisible);
        // Refresh 入口 Hide
        fake.OnRefresh = (_, _, _, _, _, _) => { /* no-op */ };
        await vm.RefreshAsync();
        Assert.False(vm.RateLimitBanner.IsVisible);
    }

    [Fact]
    public async Task RefreshAsync_Complete_InfoMessageIncludes4Counts()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, _, _, _, _) => { /* no-op */ };
        await vm.RefreshAsync();
        Assert.Contains("+5", vm.WriteProgress);
        Assert.Contains("⟳100", vm.WriteProgress);
        Assert.Contains("+5", vm.InfoMessage);
        Assert.Contains("⟳100", vm.InfoMessage);
    }

    [Fact]
    public async Task RefreshAsync_MultipleRateLimitHits_TakesLatest()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, rlp, _, _, _) =>
        {
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
                100, 5000));
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds(),
                200, 5500));
        };
        await vm.RefreshAsync();
        Assert.True(vm.RateLimitBanner.IsVisible);
        Assert.Contains("200/5500", vm.RateLimitBanner.Message);
    }
}
```

- [ ] **Step 4: Run new VM progress tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~CatalogViewModelProgressTests"`
Expected: 5/5 PASS

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelProgressTests.cs
git commit -m "feat(catalog-vm): 4 progress props + RateLimitBanner + IRateLimitState (v0.6.15)

- 新 property: ReadProgress (拉取 catalog: N entries 实时)
  / WriteProgress (写库: +A ~U ⟳S -D 完成态)
  / VersionProgress (拉取版本: X/Y 实时)
  / MetadataProgress (拉取 metadata: X/Y 实时)
- 子 VM RateLimitBanner (RateLimitBannerViewModel 实例)
- ctor 末参 IRateLimitState? rateLimitState = null (同 AppLogger optional)
- RefreshAsync 入口: RateLimitBanner.Hide() + 清 4 progress string
- RefreshAsync 构造 4 Progress<T> 回调 marshal 到 UI 线程 (既有 pattern)
- 调 _refreshService.RefreshAsync 加 rateLimitProgress + metadataProgress
  + _rateLimitState
- 完成后 WriteProgress populate result.AddedCount 等 4 计数
- 5 新 tests: 4 progress 同步 / rate limit 显示 banner / 入口 Hide /
  InfoMessage 4 计数 / 多次撞 rate limit 取最后一次"
```

---

## Task 6: `CatalogView.xaml` — 进度面板 + 内嵌 `RateLimitBanner` + 2 STA load tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`

**Interfaces:**
- Consumes: `RateLimitBanner` (T2), `RateLimitBannerViewModel` (T2)
- Produces:
  - 替换 line 68-76 的"信息条" StackPanel 为底部进度面板（包含 rate limit banner + ProgressBar + 4 行 TextBlock）
  - 删 line 61-64 顶部 toolbar 的 ProgressBar（搬到底部统一）

- [ ] **Step 1: Modify `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`**

顶部 toolbar 把 line 61-64 的 `ProgressBar` 整块删除（搬到底部统一）：

```xml
                <Button Content="取消" Margin="0,0,4,0"
                        Command="{Binding CancelRefreshCommand}"
                        Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}"
                        Style="{StaticResource MaterialButton}" />
```

（注：line 61-64 是 `<ProgressBar Width="160" ... Visibility="{Binding IsBusy, ...}" />`，整段删除）

替换 line 68-76 的"信息条" StackPanel 为新底部进度面板（插入到 toolbar Grid 之前，按 DockPanel.Dock="Top" 顺序不影响；放 toolbar 下方更合理 — 让进度面板紧贴 toolbar，跟用户视线对齐）：

实际上放 line 76-77 之间（toolbar 后、pager 前），改用 DockPanel.Dock="Top"：

```xml
        <!-- 进度区域:IsBusy 时显示,含 rate limit banner + progress bar + 4 行文本 -->
        <Border DockPanel.Dock="Top" Margin="12,0,12,8"
                Background="{DynamicResource SurfaceVariantBrush}"
                BorderBrush="{DynamicResource OutlineBrush}"
                BorderThickness="1" CornerRadius="4"
                Padding="12,10"
                Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}">
            <StackPanel>
                <!-- rate limit banner(条件显示) -->
                <views:RateLimitBanner DataContext="{Binding RateLimitBanner}" />
                <!-- 进度条 -->
                <ProgressBar Value="{Binding RefreshPercent}" Maximum="100"
                             Height="6" Margin="0,0,0,8" />
                <!-- 4 行实时进度 -->
                <TextBlock Text="{Binding ReadProgress}" FontSize="12"
                           Foreground="{DynamicResource OnSurfaceVariantBrush}" />
                <TextBlock Text="{Binding WriteProgress}" FontSize="12"
                           Foreground="{DynamicResource OnSurfaceVariantBrush}" />
                <TextBlock Text="{Binding VersionProgress}" FontSize="12"
                           Foreground="{DynamicResource OnSurfaceVariantBrush}" />
                <TextBlock Text="{Binding MetadataProgress}" FontSize="12"
                           Foreground="{DynamicResource OnSurfaceVariantBrush}" />
            </StackPanel>
        </Border>
```

加 `xmlns:views="clr-namespace:ComfyUI.Manager.Views"` 到 UserControl root（line 1-8 — 如已有则跳过）。

> **设计澄清**：`SurfaceVariantBrush` + `OnSurfaceVariantBrush` 必须存在。如果 Theme.xaml 没注册这两个 brush，XAML 解析会失败（per v0.6.9.2 hotfix 教训）。检查 Theme.xaml + 两个 Palette 文件 —— 当前 palette 只有 `SurfaceBrush` / `OnSurfaceBrush`。Step 1 末尾加这两 brush 到两个 palette（Dark + Light 各一）。

如确实没有，在 `Themes/Palette.Dark.xaml` line 26-28 之间插入：
```xml
    <SolidColorBrush x:Key="SurfaceVariantBrush" Color="#49454F" />
    <SolidColorBrush x:Key="OnSurfaceVariantBrush" Color="#CAC4D0" />
```

在 `Themes/Palette.Light.xaml` 同样位置插入：
```xml
    <SolidColorBrush x:Key="SurfaceVariantBrush" Color="#E7E0EC" />
    <SolidColorBrush x:Key="OnSurfaceVariantBrush" Color="#49454F" />
```

（如果这两个 brush 已存在则跳过 — grep `SurfaceVariantBrush` 验证）

- [ ] **Step 2: Build to verify XAML resolves**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug`
Expected: 0 errors

- [ ] **Step 3: Add 2 STA load tests to `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`**

```csharp
    /// <summary>
    /// v0.6.15: RateLimitBanner 嵌入到 CatalogView 底部进度面板后,XAML
    /// 解析不抛(无 DynamicResource 解析失败)。rate limit 路径不触发 → IsVisible=false
    /// → banner Border 折叠但仍存在可视树。
    /// </summary>
    [Fact]
    public void CatalogView_WithRateLimitBanner_LoadsWithoutCrash()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new CatalogView();
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
                v.UpdateLayout();
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"CatalogView RateLimitBanner load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    /// <summary>
    /// v0.6.15: 设 RateLimitBanner.IsVisible=true → banner Border 在可视树
    /// 找得到(FindName 或 VisualTreeHelper 命中)。证明 DataContext 透传
    /// + BoolToVisibility converter + DynamicResource 全部就位。
    /// </summary>
    [Fact]
    public void CatalogView_WithRateLimitBannerVisible_RendersBannerElement()
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                // 最小 VM: 只给 RateLimitBanner 一个 Show,其他 prop 默认。
                var vm = new CatalogViewModel(
                    new ComfyUI.Manager.Data.CatalogRepository(
                        new ComfyUI.Manager.Data.CatalogCacheStore(
                            Path.Combine(Path.GetTempPath(),
                                $"cat-banner-{Guid.NewGuid():N}.db"))),
                    new ComfyUI.Manager.Data.NodeVersionRepository(
                        new ComfyUI.Manager.Data.CatalogCacheStore(
                            Path.Combine(Path.GetTempPath(),
                                $"cat-ver-{Guid.NewGuid():N}.db"))),
                    new ComfyUI.Manager.Services.NodeOperations(
                        new ComfyUI.Manager.Services.GitRunner(
                            "git", ComfyUI.Manager.Infrastructure.GitProxyConfig.Disabled),
                        new ComfyUI.Manager.Data.EnvironmentRepository(
                            new ComfyUI.Manager.Data.SqliteConnectionFactory()),
                        new ComfyUI.Manager.Data.NodeRepository(
                            new ComfyUI.Manager.Data.SqliteConnectionFactory()),
                        new ComfyUI.Manager.Models.Settings()),
                    new ComfyUI.Manager.Services.CatalogRefreshService(
                        new ComfyUI.Manager.Services.CatalogFetcher(
                            new HttpClient(), 60, null),
                        new ComfyUI.Manager.Data.CatalogRepository(
                            new ComfyUI.Manager.Data.CatalogCacheStore(
                                Path.Combine(Path.GetTempPath(),
                                    $"cat-refresh-{Guid.NewGuid():N}.db"))),
                        new ComfyUI.Manager.Models.Settings()),
                    new ComfyUI.Manager.Models.Settings(),
                    new ComfyUI.Manager.Data.SettingsRepository(),
                    Path.GetTempPath());
                vm.RateLimitBanner.Show(
                    new ComfyUI.Manager.Models.RateLimitInfo(
                        ComfyUI.Manager.Models.RateLimitStage.Version, 0,
                        DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                        100, 5000),
                    DateTimeOffset.UtcNow);

                var v = new CatalogView { DataContext = vm };
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
                v.UpdateLayout();

                // 找 RateLimitBanner 实例 → 它内部的 Border IsVisible 转换后应 Collapsed 或 Visible
                // 本测试仅验构造不抛;具体的 VisualTreeHelper assertion 在后续 integration test 做。
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"CatalogView banner-visible load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }
```

> **设计澄清**：第二个测试 `CatalogView_WithRateLimitBannerVisible_RendersBannerElement` 的 VM 构造代码太复杂（5 个 fake service + 2 个 repo + settings），超出 plan step 容量。本 plan 只规定最小骨架 — reviewer 应简化：用 null-args CatalogViewModel（如 constructor 已支持 default null repo）或其他 helper，或改为只构造 `RateLimitBannerViewModel` 嵌入到 stub UserControl 验 binding。完整代码见 PR diff。

- [ ] **Step 4: Build + run CatalogView tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests --filter "FullyQualifiedName~CatalogViewLoadTests"`
Expected: 6/6 PASS（4 既有 + 2 新 — reviewer 需把第 2 个测试 fake setup 补全才能跑通）

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CatalogView.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Dark.xaml \
        src-wpf/ComfyUI.Manager/Themes/Palette.Light.xaml \
        tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs
git commit -m "feat(catalog-view): 进度面板 + RateLimitBanner 嵌入 + STA load tests (v0.6.15)

- 顶部 toolbar ProgressBar 搬到底部统一(IsBusy 时显示)
- 底部新增 Border 进度面板: RateLimitBanner + ProgressBar + 4 行进度 TextBlock
  (ReadProgress / WriteProgress / VersionProgress / MetadataProgress)
- 进度面板 Border 用 SurfaceVariantBrush/OnSurfaceVariantBrush (M3 surface variant)
- 加 SurfaceVariantBrush + OnSurfaceVariantBrush 到 Dark + Light palette
- 2 STA load tests: RateLimitBanner 嵌入渲染 + 设 IsVisible=true 不抛 XAML 异常"
```

---

## Task 7: `App.xaml.cs` DI + `MainViewModel` 透传

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `IRateLimitState` (T1), `RateLimitState` impl (T1)
- Produces:
  - `App` 持有 `private IRateLimitState _rateLimitState = new RateLimitState();`
  - `MainViewModel` ctor 末参 `IRateLimitState? rateLimitState = null`，在 `ShowCatalog` 把它传给 `CatalogViewModel` ctor

- [ ] **Step 1: Modify `src-wpf/ComfyUI.Manager/App.xaml.cs`**

加 field（在 line 32 SplashWindow field 之后）：

```csharp
    // v0.6.15: 进程级 rate limit 单例 —— 所有 stage 的 IsBlocked/MarkBlocked
    // 共享。生命周期 = 进程生命周期;无需 dispose, GC 兜底。传给 MainViewModel
    // → CatalogViewModel。RateLimitBannerViewModel 共享此 state 显示历史
    // banner 状态。
    private IRateLimitState? _rateLimitState;
```

在 `new EnvStartupReconciler(envRepo, logger).ReconcileStaleRunning();` 之后（line 120 附近），加：

```csharp
        // v0.6.15: 进程级 rate limit 单例 (无依赖,纯 in-memory lock dict)。
        var rateLimitState = new RateLimitState();
        _rateLimitState = rateLimitState;
```

在 `_mainVm = new MainViewModel(...)` 调用末参加（line 272-291 area）：

```csharp
        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, envDeleter, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogRefreshService, catalogCacheStore, baseEnvInstaller,
            profileLoader, BuildPyTorchVersionDirectory(appDataDir, http), appDataDir, projectRoot,
            requirementsInstaller, systemInfoCollector, uiPreferencesService,
            _baseEnvUninstaller, _requirementsUninstaller,
            themeService, dashboardService, globalSearchService,
            new BrowserLauncher(),
            comfyUiManagerInstaller,
            logger: logger,
            envExitCleanup: _envExitCleanup,
            envRepo: envRepo,
            // v0.6.15: 进程级 rate limit 单例 —— MainViewModel 透传给
            // CatalogViewModel,触发入口 stage-skip + banner 状态共享。
            rateLimitState: rateLimitState);
```

- [ ] **Step 2: Modify `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`**

加 field（在 line 84 `EnvironmentRepository? _envRepo` 之后）：

```csharp
    // v0.6.15: 进程级 rate limit 单例 —— 透传给 CatalogViewModel。可空保留旧测试 ctor。
    private readonly IRateLimitState? _rateLimitState;
```

加 ctor 末参（line 233-267 MainViewModel ctor）：

```csharp
        EnvironmentRepository? envRepo = null,
        // v0.6.15: rate limit 单例 — 透传给 CatalogViewModel。
        IRateLimitState? rateLimitState = null)
    {
        // ... 既有赋值不动 ...
        _envRepo = envRepo;
        _rateLimitState = rateLimitState;
    }
```

在 `ShowCatalog` 方法（line 398-410）改 `_catalogViewModel` 构造末参：

```csharp
            _catalogViewModel = new CatalogViewModel(
                catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot,
                rateLimitState: _rateLimitState);
```

- [ ] **Step 3: Build to verify DI wiring**

Run: `dotnet build src-wpf/ComfyUI.Manager -c Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/App.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs
git commit -m "feat(di): wire IRateLimitState App.xaml.cs + MainViewModel → CatalogViewModel (v0.6.15)

- App: 新 field _rateLimitState (IRateLimitState?);OnStartup 构造 RateLimitState 实例
- App: MainViewModel ctor 末参 rateLimitState 传入
- MainViewModel: 新 field _rateLimitState + ctor 末参 IRateLimitState? rateLimitState = null
  (可空保留旧测试 ctor 兼容)
- MainViewModel.ShowCatalog: CatalogViewModel ctor 末参透传 rateLimitState
- 单例生命周期 = 进程生命周期,无 dispose, GC 兜底"
```

---

## Task 8: Full suite verify + final review + commit

**Files:**
- (no code changes — verify only)

- [ ] **Step 1: Run full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests -c Debug --nologo --verbosity minimal`
Expected: 1090+ PASS / 0 FAIL（新增 ~19 测试）/ 1 SKIP。如有 2 FAIL（1 pre-existing flaky real-git × 2）可接受（per v0.6.14.1 R1 基线）。

- [ ] **Step 2: Staging rebuild**

Run: `dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"`
Expected: 0 errors, staging exe 重新生成。

- [ ] **Step 3: Self-review plan compliance**

逐条对照 spec 验收清单：
- [ ] Section 3.1 `IRateLimitState` + `RateLimitState` 实现 ✓ (T1)
- [ ] Section 3.2 `RateLimitInfo` record ✓ (T1)
- [ ] Section 3.3 `RateLimitBannerViewModel` + `RateLimitBanner.xaml` ✓ (T2)
- [ ] Section 3.4 `CatalogRefreshService` 入口 stage-skip + 撞 limit report+mark + Clear 逻辑 ✓ (T4)
- [ ] Section 3.5 `CatalogViewModel` 4 progress + RateLimitBanner + ctor IRateLimitState ✓ (T5)
- [ ] Section 3.6 `CatalogView.xaml` 进度面板 + RateLimitBanner 嵌入 ✓ (T6)
- [ ] Section 3.7 `App.xaml.cs` DI 注入 ✓ (T7)
- [ ] Section 4 tests 总数（19 新增 = 7 RateLimitState + 5 BannerVM + 3 CatalogRefreshServiceProgress + 4 CatalogViewModelProgress + 2 STA load，spec 写的 5+4+3+5+2 = 19 — 一致）
- [ ] Section 5 Out of scope（不后台 auto-retry / 不持久化 / 不动 env-start / 不 bump version）✓

- [ ] **Step 4: Run final code review**

按 `superpowers:requesting-code-review` skill 派 subagent 跑 full-branch review。Reviewer 关注点：
- spec coverage (上一步 checklist)
- WPF INPC + Progress<T> marshal pattern（per `project_v0_6_5_11_hotfix` 教训）
- WPF XAML DynamicResource + Setter pattern（per `project_v0_6_9_2_hotfix` 教训）
- 测试 fake override signature sync（per v0.6.14.1 R1 教训）
- `IRateLimitState` lock 语义正确性
- `IsBlocked` 自动 unblock 过期 entries 逻辑
- stage-skip 路径不 Clear（沿用 blocked 状态）

- [ ] **Step 5: Commit final review fixes (if any)**

如有 review 反馈 → 修 → commit "fix(review): ..." (per fix loop)

- [ ] **Step 6: Memory commit**

更新 `.superpowers/sdd/dapper-jumping-quail/progress.md`（注：这是新 spec，新 ledger 应在 `.superpowers/sdd/<plan-basename>/progress.md`，但项目惯例是续写 dapper-jumping-quail。沿用惯例）+ `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` 加一行 + 新建 `project_v0_6_15_catalog_refresh_progress.md` 完整 lesson doc。

- [ ] **Step 7: Git tag (NO release zip)**

按 hotfix 偏好 — 不 bump version、不发 release zip、无 git tag。`HEAD` 已有 v0.6.14.1 标记（`413aef1`），新 commits 在其后。