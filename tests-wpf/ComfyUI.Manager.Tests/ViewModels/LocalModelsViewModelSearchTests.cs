using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x T46 Task 3:LocalModelsViewModel.SearchText + LocalModelCard.SearchableText +
/// 1ms DispatcherTimer 防抖 + String.Contains(OrdinalIgnoreCase) 全文搜索过滤。
///
/// v1.0.0.x (2026-09-17) T47:SearchText 持久化目标从 Settings.LocalModelsFilter
/// (POCO JSON)迁到 model_settings.localmodels.search(SQLite 表),由
/// <see cref="LocalModelSettingsRepository"/> 接管。
///
/// 测试要点(per spec §3.1 / §3.2 / plan task 3):
///  - SearchText setter 触发 ApplyFilter(防抖 → 立即 ApplyFilter 测试通过 TimerFactory seam 同步触发)
///  - 空 SearchText → 全卡显示
///  - 大小写不敏感(OrdinalIgnoreCase)
///  - SearchableText 拼接 8 字段(实际可用的 SourceId/Title/MatchedDetail.*/Source/Kind)
///  - SearchableText 全 null/empty 不 crash 也不通过 filter("" 才显示全部)
///  - SearchText 持久化到 model_settings.localmodels.search,新构造 VM 时还原
/// </summary>
public sealed class LocalModelsViewModelSearchTests
{
    private static Settings SettingsWith(string modelsDir) => new() { DefaultModelsDirectory = modelsDir };

    private static DownloadedModel Make(string title, ModelKind kind, string sourceId) => new()
    {
        Title = title,
        Kind = kind,
        Source = ModelSourceKind.CivitAi.ToString(),
        SourceId = sourceId,
        SourceVersionId = "v1",
        DownloadedAt = DateTime.Now,
    };

    /// <summary>构造 3 张卡:anime 风格 lora、realistic 风格 lora、sdxl checkpoint。</summary>
    private static FakeScanner ThreeCards() => new()
    {
        Entries = new List<DownloadedModel>
        {
            Make("Anime Style LoRA", ModelKind.LORA, "1"),
            Make("Realistic Vision", ModelKind.LORA, "2"),
            Make("SDXL Base", ModelKind.Checkpoint, "3"),
        }
    };

    /// <summary>测试场景 helper — 构造 VM 时塞一个 FakeImmediateTimerFactory,
    /// 让 SearchText setter 立刻触发 ApplyFilter(xUnit 跑在非 UI 线程,
    /// 真实 DispatcherTimer 不会自动 Tick → 必须用 fake timer 模拟 1ms 后的 Tick)。</summary>
    private static LocalModelsViewModel NewVm(Settings settings, ModelFilesystemScanner? scanner = null)
    {
        return new LocalModelsViewModel(settings, scanner ?? ThreeCards())
        {
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };
    }

    [Fact]
    public void SearchText_SetAnime_FiltersToAnimeCards()
    {
        var vm = NewVm(SettingsWith("Z:\\fake"));
        vm.ReloadAsync().GetAwaiter().GetResult();
        // 默认 SearchText 空 → 全 3 张卡可见
        Assert.Equal(3, vm.FilteredModels.Count);

        vm.SearchText = "anime";

        Assert.Single(vm.FilteredModels);
        Assert.Equal("Anime Style LoRA", vm.FilteredModels[0].Title);
    }

    [Fact]
    public void SearchText_CaseInsensitive_MatchesUppercaseAndLower()
    {
        var vm = NewVm(SettingsWith("Z:\\fake"));
        vm.ReloadAsync().GetAwaiter().GetResult();

        vm.SearchText = "ANIME";
        Assert.Single(vm.FilteredModels);
        Assert.Equal("Anime Style LoRA", vm.FilteredModels[0].Title);

        vm.SearchText = "anime";
        Assert.Single(vm.FilteredModels);
        Assert.Equal("Anime Style LoRA", vm.FilteredModels[0].Title);

        vm.SearchText = "AnImE";
        Assert.Single(vm.FilteredModels);
    }

    [Fact]
    public void SearchText_Empty_ShowsAllCards()
    {
        var vm = NewVm(SettingsWith("Z:\\fake"));
        vm.ReloadAsync().GetAwaiter().GetResult();

        // 先设非空
        vm.SearchText = "anime";
        Assert.Single(vm.FilteredModels);

        // 清空
        vm.SearchText = "";
        Assert.Equal(3, vm.FilteredModels.Count);
    }

    [Fact]
    public void SearchText_MatchesOnKindString()
    {
        // SearchableText 拼接包含 Kind.ToString(),搜 "checkpoint" 应命中 SDXL 卡
        var vm = NewVm(SettingsWith("Z:\\fake"));
        vm.ReloadAsync().GetAwaiter().GetResult();

        vm.SearchText = "Checkpoint";
        Assert.Single(vm.FilteredModels);
        Assert.Equal("SDXL Base", vm.FilteredModels[0].Title);
    }

    [Fact]
    public void SearchText_MatchesOnSourceString()
    {
        // Source = "CivitAi"(enum name),搜 "civitai" 应命中所有卡
        var vm = NewVm(SettingsWith("Z:\\fake"));
        vm.ReloadAsync().GetAwaiter().GetResult();

        vm.SearchText = "civitai";
        Assert.Equal(3, vm.FilteredModels.Count);
    }

    [Fact]
    public void SearchableText_LowercaseContainsAllJoinedFields()
    {
        // 单测 LocalModelCard.SearchableText 直接验证字段拼接 + lowercase
        var card = new LocalModelCard(
            SourceId: "test-id-123",
            Title: "Anime Style LoRA",
            Kind: ModelKind.LORA,
            Source: "CivitAi",
            VersionCount: 1,
            LatestDownloadedAt: DateTime.UtcNow,
            SourceUrl: null,
            PreviewImagePath: null,
            Hash: null,
            MatchedDetail: null,
            MatchSource: null,
            LocalPathOverride: null);

        Assert.Contains("test-id-123", card.SearchableText);
        Assert.Contains("anime style lora", card.SearchableText);
        Assert.Contains("civitai", card.SearchableText);
        Assert.Contains("lora", card.SearchableText);
        // 全 lowercase
        Assert.Equal(card.SearchableText.ToLowerInvariant(), card.SearchableText);
    }

    [Fact]
    public void SearchableText_IncludesMatchedDetailFieldsWhenPresent()
    {
        var card = new LocalModelCard(
            SourceId: "1",
            Title: "TitleOnly",
            Kind: ModelKind.Checkpoint,
            Source: "Local",
            VersionCount: 1,
            LatestDownloadedAt: null,
            SourceUrl: null,
            PreviewImagePath: null,
            Hash: null,
            MatchedDetail: new CivitAiDetailDto(
                Id: 42,
                Title: "CivitTitle",
                Username: "SomeAuthor",
                BaseModel: "SDXL 1.0",
                Description: "hand-drawn anime style",
                Tags: new[] { "anime", "illustration" },
                Versions: Array.Empty<CivitAiVersionDto>(),
                ImageUrls: Array.Empty<string>()),
            MatchSource: MatchSource.UserQuery,
            LocalPathOverride: null);

        Assert.Contains("civittitle", card.SearchableText);
        Assert.Contains("someauthor", card.SearchableText);
        Assert.Contains("sdxl 1.0", card.SearchableText);
        Assert.Contains("hand-drawn anime style", card.SearchableText);
        Assert.Contains("anime", card.SearchableText);
        Assert.Contains("illustration", card.SearchableText);
    }

    [Fact]
    public void SearchText_PersistsToModelSettings()
    {
        // v1.0.0.x (2026-09-17) T47:SearchText setter 现在写 model_settings 表
        // (由 LocalModelSettingsRepository 持久化),不再写 Settings.LocalModelsFilter。
        using var db = new TestDb();
        var settings = SettingsWith("Z:\\fake");
        var modelSettings = new LocalModelSettingsRepository(db.ModelFactory);
        var vm = new LocalModelsViewModel(
            settings, ThreeCards(), localModelsSettings: modelSettings);

        vm.SearchText = "anime";

        Assert.Equal("anime", modelSettings.Get(SearchFilterMigration.Key));
    }

    [Fact]
    public void Constructor_RestoresLastSearchFromModelSettings()
    {
        // v1.0.0.x (2026-09-17) T47:VM ctor 从 model_settings 还原 SearchText(非
        // Settings.LocalModelsFilter)。新构造 VM → 启动还原路径 → SearchText 立刻可见。
        using var db = new TestDb();
        var settings = SettingsWith("Z:\\fake");
        var modelSettings = new LocalModelSettingsRepository(db.ModelFactory);
        modelSettings.Set(SearchFilterMigration.Key, "anime");
        var vm = new LocalModelsViewModel(
            settings, ThreeCards(), localModelsSettings: modelSettings)
        {
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Equal("anime", vm.SearchText);
        // 还原后立刻过滤 → 只剩 anime 卡
        Assert.Single(vm.FilteredModels);
        Assert.Equal("Anime Style LoRA", vm.FilteredModels[0].Title);
    }

    [Fact]
    public void SearchText_RapidChanges_OnlyTriggersOneFilterApply()
    {
        // 1ms DispatcherTimer 防抖:连续 10 次 SearchText 赋值只触发 1 次 ApplyFilter。
        // 测试 seam:FilterTimerFactory 传 DeferredFakeTimerHost — Start 时不立即 Tick,
        // 由 host.Drain() 模拟"1ms 后"统一 Tick。前 N-1 个 setter 把 PendingAction 替换 + 旧 handle Dispose,
        // Drain() 时只有最后那个 handle 没被 Dispose → 只 Tick 1 次。
        var settings = SettingsWith("Z:\\fake");
        var host = new DeferredFakeTimerHost();
        var vm = new LocalModelsViewModel(settings, ThreeCards())
        {
            FilterTimerFactory = host.CreateTimer,
        };
        vm.ReloadAsync().GetAwaiter().GetResult();

        int refreshCount = 0;
        vm.FilterAppliedCallback = () => Interlocked.Increment(ref refreshCount);

        int initialRefresh = refreshCount;   // ReloadAsync 内已 ApplyFilter 一次
        // 连续 10 次赋值 — 每 setter 都 Start 新 timer,前 9 次被前 handle Dispose 取消,
        // 只有最后一次留到 Drain() 时 Tick → ApplyFilter 只触发 1 次。
        for (int i = 0; i < 10; i++)
        {
            vm.SearchText = $"q{i}";
        }

        // 模拟 1ms 防抖 timer 到时 → 只 1 次 ApplyFilter
        host.Drain();
        Assert.Equal(initialRefresh + 1, refreshCount);
    }

    /// <summary>v1.0.0.x T46 测试用 — 模拟 DispatcherTimer 1ms 防抖:构造时立刻同步 Tick
    /// (模拟"1ms 防抖到时"的瞬时态)。Dispose 后再 Tick 不触发 — 模拟"上 timer 被新 timer 取消"。
    /// 非 UI 线程(xUnit 默认)DispatcherTimer 不会自动 Tick,所以测试需要这个 fake。
    /// </summary>
    private sealed class FakeImmediateTimer : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;

        public FakeImmediateTimer(Action action)
        {
            _action = action;
            // 立即 Tick — 模拟 1ms 防抖 timer 到时
            _action();
        }

        public void Dispose() => _disposed = true;

        /// <summary>已 disposed 后再 Tick 不触发(模拟"上 timer 被新 timer 取消")。</summary>
        public void Tick()
        {
            if (_disposed) return;
            _action();
        }
    }

    /// <summary>v1.0.0.x T46 测试用 — SearchText setter 调 FilterTimerFactory 时
    /// 返回一个 pending timer,defer 到 Drain() 时统一 Tick 一次(模拟 1ms 后)。
    /// factory 调多次会替换前一个 = 跟真 DispatcherTimer.Dispose + 新 timer 一致。</summary>
    internal sealed class DeferredFakeTimerHost
    {
        public Action? PendingAction { get; private set; }
        public bool LastHandleDisposed { get; private set; }

        public IDisposable CreateTimer(Action action, TimeSpan _)
        {
            // 替换前一个 timer(setter 内部会 Dispose 旧的,这里把 PendingAction 替换成新的)
            PendingAction = action;
            LastHandleDisposed = false;
            return new Handle(() => { LastHandleDisposed = true; });
        }

        public void Drain()
        {
            if (PendingAction is not null && !LastHandleDisposed)
            {
                PendingAction();
            }
            PendingAction = null;
            LastHandleDisposed = false;
        }

        private sealed class Handle : IDisposable
        {
            private readonly Action _onDispose;
            public Handle(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }
}