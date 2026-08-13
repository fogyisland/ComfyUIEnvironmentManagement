# v0.6.15 Spec: Catalog Refresh 实时进度 + Rate Limit UI 提示 + 入口自动跳过

> **For agentic workers:** This is a design spec. Read once before writing the implementation plan; do not modify without user approval.

## 1. Goal

解决 v0.6.14.1 hotfix 之后 Catalog refresh 仍有 2 个用户痛点:

1. **refresh 进度不可见**:UI 只显示 "拉取 catalog..." + 一个 `RefreshPercent` 进度条。catalog fetch 阶段没具体数字、version fetch 只显示 "X/Y"、metadata 进度只在 Logs/ 文件。**用户完全不知道 refresh 当前走到哪一步、还要多久、撞 rate limit 没撞**。
2. **rate limit 撞了没 UI 提示**:GitHub 返回 403 + `X-RateLimit-Remaining=0` 时,v0.6.14.1 让 `GitHubVersionService.FetchVersionsAsync` 不再 throw,改成 return partial + log `Warn("version-rate-limit")`。但 **UI 完全感知不到**:用户看到的 "刷新成功" 摘要里 `VersionCount=0` 没人会注意到 → 撞 limit 实际无感 → 下次 refresh 还会再撞同样 5883 entries 浪费配额。

**用户原话**:
> "获取github 的数据和写入数据的过程全部都在程序的界面中显示,让我们知道读取多少出现ratelimit,数据库写入了多少数据了"

**Non-goals(本 spec 不做)**:
- **不做后台 auto-retry task**(spawn `Task.Delay` 等 reset 后自动跑剩余) — 复杂:session 关闭丢失、cancel 语义混乱、UI 转圈 5+ 分钟难以 cancel、retry 失败没 surface
- **不持久化 rate limit state 到 disk** — 1 小时后重启,limit 早 reset,持久化无意义
- **不动 env-start / requirements-install / node-install 状态面板的 rate limit 提示** — 这些子系统不调 GitHub API,撞不到
- **不动 node install 的"diff" 警告(降级/冲突检测)**
- **不改 catalog refresh 的内部逻辑**(仍 v0.6.14 的 3 步流水线:fetch → version → metadata,hash-diff 短路,backfill)
- **不 bump version / 不发 release zip**(per hotfix 偏好)

## 2. Background

### 2.1 v0.6.14.1 hotfix 现状(直接复用的基础)

- **`GitHubVersionService.FetchVersionsAsync`**:撞 rate limit 时**不抛** `RateLimitException`,改回 `(empty, RateLimitHit=true)` tuple 形式。`FetchVersionsAsync` 写共享 `Volatile` flag + log `Warn("version-rate-limit", ...)` + return 当前 partial result。partial data 落库后下次 refresh hash-diff 短路,自然恢复。
- **`GitHubVersionService.GetLatestVersionAsync`**:单条 API,**仍 fail-fast 抛** `RateLimitException`(单条调用没 partial concerns)。
- **`RateLimitHeaderInfo` record**:抓 `X-RateLimit-Remaining` + `X-RateLimit-Reset`(unix seconds)。`FetchVersionsAsync` log 用 `resetHint` 算 "X 分钟后重置" — 已实现,直接复用给 UI。
- **`CatalogRefreshService.RefreshAsync`**:现有 `IProgress<CatalogEntry>?` (per-entry upsert) + `IProgress<VersionFetchProgress>?` 通道。**缺** catalog fetch 阶段 progress、metadata fetch 阶段 progress、rate limit 事件通道。

### 2.2 UI 现状(CatalogView)

- 底部已有 `ProgressMessage` + `RefreshPercent`(只反映 version fetch 阶段)
- 完成后摘要 `InfoMessage = $"+{A} ~{U} ⟳{S} -{D}"`(已用,但 fetch/metadata 阶段无)
- **没有 rate limit banner**(rate limit 信息只在 Logs/ 文件)
- **没有独立的 progress 区域**(只在主 grid 下方贴一条 `ProgressMessage` TextBlock)

### 2.3 已有可复用组件

- **`ErrorBanner` + `ErrorBannerViewModel`**:ErrorBanner.xaml + `ErrorBannerViewModel.cs`,同款 ⚠ 风格但语义是 "error"。**不直接复用** — rate limit 是 "warning" 不是 "error",但视觉风格可借鉴(⚠ + 文本 + ✕ dismiss)。
- **`MetadataFetchProgress` record**:`{ Done, Total, CurrentPackage }`,已存在但只在 Logs/ 输出。复用 record 直接 report 给 UI。
- **`VersionFetchProgress` record**:`{ Completed, Total, CurrentNodeId }`,已通过 `IProgress<>` 传到 UI(只显示 percent + X/Y 文本)。

### 2.4 用户预期

- **3 行实时进度** + **可复用 RateLimitBanner**:catalog fetch 显示总数、upsert 显示 +A ~U ⟳S -D 实时、version 显示 X/Y、metadata 显示 X/Y。Banner 撞 rate limit 时出现,显示"X 分钟后重置",**持续到下次 refresh 成功**。
- **入口自动跳过**:下次点 refresh 按钮时,VM 先查 `IRateLimitState.IsBlocked(stage)`。如果 stage(version 或 metadata)还在 reset 之前 → **跳过该 stage** + 提示 user "已跳过版本拉取 — GitHub 限流中"。**不浪费配额**。
- **组件可复用**:`RateLimitBanner` 是独立 UserControl + VM,任何 view 都能 host。后续 v0.6.16+ 如果其他子系统也调 GitHub API,直接 `<vm:RateLimitBannerViewModel />` 嵌进去。

## 3. Design

### 3.1 进程级 `IRateLimitState` 单例(核心)

**接口**(`Services/IRateLimitState.cs`,新):

```csharp
public interface IRateLimitState
{
    /// <summary>查某个 stage 是否在限流冷却中。</summary>
    bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info);

    /// <summary>标记某 stage 撞 rate limit。多次调用取最新一次(覆盖前次 reset time)。</summary>
    void MarkBlocked(RateLimitStage stage, long? resetUnix, int partialCount, int totalCount);

    /// <summary>清除某 stage 状态(refresh 成功完成时调)。</summary>
    void Clear(RateLimitStage stage);
}

public enum RateLimitStage
{
    Version,
    Metadata,
}

public record RateLimitBlockInfo(
    DateTimeOffset BlockedAt,
    DateTimeOffset ResetAt,
    int PartialCount,
    int TotalCount);
```

**实现**(`Services/RateLimitState.cs`,新,进程单例):

```csharp
public sealed class RateLimitState : IRateLimitState
{
    private readonly object _lock = new();
    private RateLimitBlockInfo? _version;
    private RateLimitBlockInfo? _metadata;

    public bool IsBlocked(RateLimitStage stage, out RateLimitBlockInfo? info)
    {
        lock (_lock)
        {
            var current = stage switch
            {
                RateLimitStage.Version => _version,
                RateLimitStage.Metadata => _metadata,
                _ => null,
            };
            // reset time 已过 → 自动 unblock(等同 Clear)
            if (current is not null && current.ResetAt <= DateTimeOffset.Now)
            {
                current = null;
                if (stage == RateLimitStage.Version) _version = null;
                else _metadata = null;
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
            var info = new RateLimitBlockInfo(DateTimeOffset.Now, resetAt, partialCount, totalCount);
            if (stage == RateLimitStage.Version) _version = info;
            else _metadata = info;
        }
    }

    public void Clear(RateLimitStage stage)
    {
        lock (_lock)
        {
            if (stage == RateLimitStage.Version) _version = null;
            else _metadata = null;
        }
    }
}
```

**DI 注入**(`App.xaml.cs`):`IRateLimitState` → new `RateLimitState()` 单例,跟 `_logger` 同款(进程内唯一,所有 VM 共享)。无 `using`/dispose,GC 兜底。

### 3.2 `RateLimitInfo` 跨边界 record(新)

`Models/RateLimitInfo.cs`:

```csharp
public record RateLimitInfo(
    RateLimitStage Stage,
    long Remaining,
    long? ResetUnix,
    int PartialCount,
    int TotalCount);
```

跟 `IRateLimitState` 的 `MarkBlocked` 签名一一对应。`CatalogRefreshService` 撞 limit 时构造 `RateLimitInfo` → **同时** Report 给 `IProgress<RateLimitInfo>` (UI 用) **+** Mark 到 `IRateLimitState`(下次 refresh 入口用)。

### 3.3 `RateLimitBannerViewModel` + `RateLimitBanner.xaml`(新,独立组件)

**`ViewModels/RateLimitBannerViewModel.cs`**:

```csharp
public class RateLimitBannerViewModel : ViewModelBase
{
    public bool IsVisible { get; private set; }   // default false
    public string Title { get; private set; } = "";
    public string Message { get; private set; } = "";
    public RelayCommand DismissCommand { get; }

    public RateLimitBannerViewModel()
    {
        DismissCommand = new RelayCommand(_ => Hide());
    }

    public void Show(RateLimitInfo info, DateTimeOffset now)
    {
        var waitMin = Math.Max(0, (int)Math.Ceiling(
            (DateTimeOffset.FromUnixTimeSeconds(info.ResetUnix ?? 0) - now).TotalMinutes));
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
        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Message));
    }

    public void Hide()
    {
        IsVisible = false;
        RaisePropertyChanged(nameof(IsVisible));
    }
}
```

**`Views/RateLimitBanner.xaml`**:跟 `ErrorBanner` 同款风格(⚠ + 文本 + ✕ 按钮),但**永远不是 error 红色**,用 warning 黄色 / 橙色。从 `Theme.xaml` 借 `WarningBrush`(新建)+ `OnWarningBrush` 文字色。

```xml
<UserControl ...>
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

code-behind 极简:`OnDismissClick` → `((RateLimitBannerViewModel)DataContext).DismissCommand.Execute(null)`。

### 3.4 `CatalogRefreshService` 改造

**签名**:
```csharp
public virtual async Task<RefreshResult> RefreshAsync(
    IProgress<CatalogEntry>? progress = null,
    IProgress<VersionFetchProgress>? versionProgress = null,
    IProgress<RateLimitInfo>? rateLimitProgress = null,    // v0.6.15 新
    IProgress<MetadataFetchProgress>? metadataProgress = null,  // v0.6.15 新(已有 type)
    IRateLimitState? rateLimitState = null,                // v0.6.15 新
    CancellationToken ct = default)
```

**改动点**:

1. **version fetch 阶段**:`GitHubVersionService.FetchVersionsAsync` 当前 log `Warn("version-rate-limit", ...)` → **同时**构造 `RateLimitInfo(Version, header.Remaining, header.Reset, partial, total)` → 调 `rateLimitProgress?.Report(info)` **+** `rateLimitState?.MarkBlocked(Version, reset, partial, total)`。
2. **metadata fetch 阶段**:`GitHubCatalogMetadataService.EnrichAsync` 当前 catch `RateLimitException` → **同时**构造 `RateLimitInfo(Metadata, ...)` → report + mark。metadata 服务的 `EnrichAsync` 签名需要加 `IProgress<RateLimitInfo>?` 参数(类似 version service)。
3. **入口 stage-skip 逻辑**(在 `RefreshAsync` 顶部):
   ```csharp
   // 入口检查 rate limit 冷却 — 跳过整个 stage 不浪费 GitHub 配额
   bool skipVersion = _versionService is not null
       && _settings.FetchNodeVersionsOnRefresh
       && rateLimitState?.IsBlocked(RateLimitStage.Version, out _) == true;
   bool skipMetadata = _metadataService is not null
       && _settings.FetchCatalogMetadata
       && rateLimitState?.IsBlocked(RateLimitStage.Metadata, out _) == true;

   if (skipVersion)
   {
       _logger?.Info("catalog-refresh", "skip version fetch (GitHub rate limit cooling down)");
       versionCount = 0;
   }
   if (skipMetadata) { ... 同 ... }

   // 然后正常走 5 步流水线,跳过对应 stage
   ```
4. **完成时清状态**:`RefreshAsync` 成功 return 前调 `rateLimitState?.Clear(Version)` + `Clear(Metadata)`(如果该 stage 实际跑了且没撞 limit)。**只清"刚跑成功"的 stage** — 跳过的不动(还是 blocked)。

**progress 通道**:跟现有 `IProgress<>` 模式一致,WPF `Progress<T>` 构造时自动捕获 UI `SynchronizationContext`,Report 自动 marshal 回去。

### 3.5 `CatalogViewModel` 改造

**新 property**:
```csharp
public string ReadProgress { get; private set; } = "";   // "拉取 catalog: 5883 entries"
public string WriteProgress { get; private set; } = "";  // "写库: +5 ~12 ⟳5866 -0" (实时)
public string VersionProgress { get; private set; } = ""; // "拉取版本: 4521/5883 (76%)" (已有 RefreshPercent 同步)
public string MetadataProgress { get; private set; } = ""; // "拉取 metadata: 1234/5883 (20%)"
public RateLimitBannerViewModel RateLimitBanner { get; }  // 子 VM,XAML 嵌 UserControl 用
```

**构造**:`RateLimitBanner = new RateLimitBannerViewModel()`。

**`RefreshAsync` 改造**:
- 入口:`RateLimitBanner.Hide()`(clear 上次 stale state) + 清 4 个 progress string
- 构造 4 个 `Progress<T>`:
  ```csharp
  var stageProgress = new Progress<CatalogEntry>(e => { _allEntries.Add(e); ReadProgress = $"拉取 catalog: {_allEntries.Count} entries"; });
  var versionProgress = new Progress<VersionFetchProgress>(vp => { RefreshPercent = (int)(100.0 * vp.Completed / vp.Total); VersionProgress = $"拉取版本: {vp.Completed}/{vp.Total}"; });
  var metadataProgress = new Progress<MetadataFetchProgress>(mp => { MetadataProgress = $"拉取 metadata: {mp.Done}/{mp.Total}"; });
  var rateLimitProgress = new Progress<RateLimitInfo>(info => { RateLimitBanner.Show(info, DateTimeOffset.Now); });
  ```
- 调 `_refreshService.RefreshAsync(stageProgress, versionProgress, rateLimitProgress, metadataProgress, _rateLimitState, ct)`
- `WriteProgress` 在 `RefreshResult` 返回后用 `+A ~U ⟳S -D` populate(已有 `result.AddedCount` 等字段,直接拿来)
- 完成后:`ReadProgress` / `WriteProgress` 保留(不 reset)→ 用户看完后下次 refresh 入口清

**`_rateLimitState` 字段**:`CatalogViewModel` ctor 加 `IRateLimitState? rateLimitState = null` 参数(同 `AppLogger?` pattern)。

### 3.6 `CatalogView.xaml` 改造

**进度区域扩展**(替换现有 `ProgressMessage` 单行):

```xml
<!-- 底部进度面板(只在 IsBusy 时显示) -->
<Border DockPanel.Dock="Bottom" Margin="12,0,12,12"
        Background="{DynamicResource SurfaceVariantBrush}"
        BorderBrush="{DynamicResource OutlineBrush}"
        BorderThickness="1" CornerRadius="4"
        Padding="12,10"
        Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}">
    <StackPanel>
        <!-- rate limit banner(条件显示) -->
        <views:RateLimitBanner DataContext="{Binding RateLimitBanner}" />
        <!-- 进度条 -->
        <ProgressBar Value="{Binding RefreshPercent}" Maximum="100" Height="6" Margin="0,0,0,8" />
        <!-- 4 行实时进度 -->
        <TextBlock Text="{Binding ReadProgress}" FontSize="12" Foreground="{DynamicResource OnSurfaceVariantBrush}" />
        <TextBlock Text="{Binding WriteProgress}" FontSize="12" Foreground="{DynamicResource OnSurfaceVariantBrush}" />
        <TextBlock Text="{Binding VersionProgress}" FontSize="12" Foreground="{DynamicResource OnSurfaceVariantBrush}" />
        <TextBlock Text="{Binding MetadataProgress}" FontSize="12" Foreground="{DynamicResource OnSurfaceVariantBrush}" />
    </StackPanel>
</Border>
```

**Theme.xaml 注册** 新增资源(2 个):
- `WarningBrush` — 警告橙色(`#F57C00` 之类)
- `WarningContainerBrush` — 警告容器背景(浅橙 `#FFF3E0` 之类)
- `OnWarningBrush` — 警告文字色(`#5D2F00` 之类)

`BoolToVisibility` converter 已有(注册过),直接用。

### 3.7 `App.xaml.cs` DI 注入

`App.xaml.cs` 加 `IRateLimitState` 单例(跟 `_logger` / `_mainVm` 同 lifecycle):

```csharp
public IRateLimitState RateLimitState { get; } = new RateLimitState();
```

传给 `MainViewModel` 构造,再传给 `CatalogViewModel`(如果 `MainViewModel` 直接 new CatalogViewModel)。如果有 service locator pattern,加一行 `RateLimitState` 分发。

### 3.8 数据流总结

```
user 点 Refresh
    │
    ▼
CatalogViewModel.RefreshAsync
    │  入口:RateLimitBanner.Hide() + 清 4 个 progress string
    │  构造 4 个 Progress<T>
    ▼
CatalogRefreshService.RefreshAsync
    │  Step 0:rateLimitState.IsBlocked 检查 → skipVersion / skipMetadata
    │  Step 1:fetch catalog JSON → stageProgress.Report(每条 entry) → ReadProgress 实时
    │  Step 2:per-entry hash diff + UpsertBatch → WriteProgress 实时计算
    │  Step 3:version fetch (如果 !skipVersion) → versionProgress.Report
    │         if 撞 rate limit → rateLimitProgress.Report(RateLimitInfo Version)
    │                          → rateLimitState.MarkBlocked(Version, ...)
    │         if 正常完成 → rateLimitState.Clear(Version)
    │  Step 4:metadata enrich (如果 !skipMetadata) → metadataProgress.Report
    │         if 撞 rate limit → report + MarkBlocked(Metadata, ...)
    │         if 正常完成 → Clear(Metadata)
    ▼
RefreshResult return → InfoMessage 摘要 + 4 行 progress string 保留
```

> **设计澄清**:Section 3.4 Step 4 的 "Clear(Version/Metadata)" 只在该 stage 实际跑了且没撞 limit 时调;`skipVersion=true` 时 stage 完全没跑,state 仍保持 blocked(等同"沿用上次的 rate limit 状态")。这避免了"上次撞了,这次跳过,然后被错误 Clear"的语义错乱。

## 4. Tests

### 4.1 `RateLimitStateTests`(新,5 测试)

- `IsBlocked_Default_ReturnsFalse`
- `MarkBlocked_ThenIsBlocked_ReturnsTrueWithInfo`
- `MarkBlocked_ResetTimeInPast_DoesNotBlock`
- `MarkBlocked_MultipleStages_AreIndependent`
- `MarkBlocked_ThenClear_IsBlockedReturnsFalse`
- `MarkBlocked_Twice_TakesLatestResetTime`(覆盖)

### 4.2 `RateLimitBannerViewModelTests`(新,4 测试)

- `IsVisible_DefaultFalse`
- `Show_WithVersionInfo_PopulatesTitleAndMessage`
- `Show_WithMetadataInfo_StageLabelIsMetadata`
- `DismissCommand_HidesBanner`
- `Show_NoResetUnix_ShowsRemainingCount`

### 4.3 `CatalogRefreshServiceProgressTests`(扩展现有,3 新测试)

需要先把 `FakeProgress<T>` helper 抽出来(目前测试用 `new Progress<T>` 然后断言 side effect,改成 capture list):

- `RefreshAsync_VersionRateLimit_ReportsRateLimitInfoAndMarksState`
- `RefreshAsync_MetadataRateLimit_ReportsRateLimitInfoAndMarksState`
- `RefreshAsync_VersionStateBlocked_SkipsVersionFetch`

### 4.4 `CatalogViewModelProgressTests`(新,5 测试)

- `RefreshAsync_Updates4ProgressProperties_OnCallbacks`(4 个 progress callback 各自正确更新 property)
- `RefreshAsync_RateLimitInfo_ShowsBanner`(撞 rate limit → RateLimitBanner.IsVisible=true + Title/Message populated)
- `RefreshAsync_Start_HidesBanner`(每次 refresh 入口 Hide,清上次 stale state)
- `RefreshAsync_Complete_InfoMessageIncludes4Counts`(扩展现有,verify WriteProgress 同步 result.AddedCount 等)
- `RefreshAsync_MultipleRateLimitHits_TakesLatest`(多次撞 limit,banner 反映最后一次 info)

(早期草稿里写 "RefreshAsync_EntryBlocked_ResetsBanner" 是自相矛盾 — 设计是 banner 持续到下次 refresh 成功,所以同一次 refresh 内 banner 不会自己消失。已删)

### 4.5 `CatalogViewLoadTests`(扩展现有,2 新测试)

- `CatalogView_WithRateLimitBanner_LoadsWithoutCrash`(STA 加载,RateLimitBanner 嵌入渲染不抛)
- `CatalogView_WithRateLimitBannerVisible_RendersBannerElement`(STA + 设 RateLimitBanner.IsVisible=true → FindName 找到 banner Border 元素)

### 4.6 基线

**当前**: 1071 PASS / 2 FAIL (1 pre-existing flaky real-git × 2) / 1 SKIP (v0.6.14.1)

**目标**: 1090+ PASS / 0 FAIL (新 ~19 测试,可能跑 2 次 real-git flake) / 1 SKIP

## 5. Out of Scope(明确不做)

- **后台 auto-retry task**(spawn `Task.Delay` 等 reset 后自动跑剩余) — 复杂,session 关闭丢失
- **持久化 rate limit state 到 disk** — 1 小时后重启,limit 早 reset
- **node install / env-start / requirements 的 rate limit 提示** — 这些子系统不调 GitHub API,撞不到
- **rate limit 历史的累计统计**(每次 refresh 跨 session 的命中率) — YAGNI
- **force full refresh 跳过 rate limit 状态** — 后续如果用户有需求再单独 spec
- **多 stage 独立 reset 时间计算**(version / metadata 分别算 reset) — 当前都是同一 GitHub rate limit bucket,没意义分开
- **rate limit banner 的国际化**(i18n) — 跟项目其他 UI 一致先用中文,后续统一 i18n 时一起做
- **catalog refresh 内部逻辑改动** — 仍 v0.6.14 的 3 步流水线

## 6. Files

| 操作 | 路径 |
|---|---|
| 新建 | `src-wpf/ComfyUI.Manager/Services/IRateLimitState.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Services/RateLimitState.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Models/RateLimitInfo.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Models/RateLimitStage.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/ViewModels/RateLimitBannerViewModel.cs` |
| 新建 | `src-wpf/ComfyUI.Manager/Views/RateLimitBanner.xaml(.cs)` |
| 改 | `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`(加 3 个 progress 参数 + state 检查 + state 调) |
| 改 | `src-wpf/ComfyUI.Manager/Services/GitHubCatalogMetadataService.cs`(EnrichAsync 签名加 `IProgress<RateLimitInfo>?` + catch RateLimitException 处 report) |
| 改 | `src-wpf/ComfyUI.Manager/Services/GitHubVersionService.cs`(FetchVersionsAsync 在 log Warn 处加 rateLimitProgress.Report;reset hint 复用) |
| 改 | `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`(4 progress property + RateLimitBanner 子 VM + ctor 加 IRateLimitState) |
| 改 | `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(ctor 加 IRateLimitState 透传) |
| 改 | `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml`(进度区域扩展 + RateLimitBanner 嵌入) |
| 改 | `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`(注册 WarningBrush + WarningContainerBrush + OnWarningBrush) |
| 改 | `src-wpf/ComfyUI.Manager/App.xaml.cs`(IRateLimitState 单例注入) |
| 新 | `tests-wpf/.../Services/RateLimitStateTests.cs`(5 测试) |
| 新 | `tests-wpf/.../ViewModels/RateLimitBannerViewModelTests.cs`(4 测试) |
| 新 | `tests-wpf/.../ViewModels/CatalogViewModelProgressTests.cs`(5 测试) |
| 改 | `tests-wpf/.../Services/CatalogRefreshServiceProgressTests.cs`(3 新测试) |
| 改 | `tests-wpf/.../Views/CatalogViewLoadTests.cs`(2 新测试) |
| 改 | `tests-wpf/.../Services/CatalogRefreshServiceMetadataTests.cs`(FakeMetadataService 签名同步) |
| 改 | `tests-wpf/.../Services/CatalogRefreshServiceTests.cs`(FakeMetadataService 签名同步 — 跟 v0.6.14.1 R1 同样的 7 个 fake 同步) |
