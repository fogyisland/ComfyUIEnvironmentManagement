using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.ModelSources;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.20 T8:Model市场 VM 单元测试。
/// 镜像 v0.6.19 WorkflowMarketplaceViewModelTests,验证核心场景:
/// 构造器初始空 / Kind 枚举 8 项 / Query 在 Refresh 后过滤 / ActiveKindFilter 触发 kind filter /
/// SelectedVersions 集合改变通知 / ConsoleLog 触发 IsConsoleVisible / Hide + Clear 命令。
///
/// v0.6.22 T6 加 SearchCommand 测试(Enter / 按钮触发刷新)— Query setter 不再 auto-filter on type。
///
/// 注:brief 原稿用 `SearchAsync` —— 实际 API 是 `LoadAllAsync`(T4 已 ship),测试跟随实际签名。
/// 注:brief 原稿用 `MockModelMarketplaceService(ctored HttpClient, List&lt;IModelSource&gt;, logger)` —
/// 实际 ctor 是 `(IEnumerable&lt;IModelSource&gt; sources, AppLogger? logger)`,跟随实际签名。
/// 注:T6 MockModelMarketplaceService override 4 参版 LoadAllAsync(VM 改走 sourceFilter 入参),
/// 旧 3 参版保留为向后兼容。
/// </summary>
public class ModelMarketplaceViewModelTests
{
    private static ModelEntry MakeModel(int id, ModelKind kind, params (string vid, string name)[] versions)
    {
        return new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = id.ToString(),
            SourceUrl = $"https://civitai.com/models/{id}",
            Title = $"Model {id}",
            Kind = kind,
            NsfwKind = ModelNsfwKind.SFW,
            Versions = versions.Select(v => new ModelVersionEntry
            {
                SourceVersionId = v.vid,
                Name = v.name,
                PrimaryDownloadUrl = $"https://civitai.com/api/download/models/{v.vid}",
                SizeBytes = 1024,
                Files = new List<ModelFile>
                {
                    new() { Name = "m.safetensors", SizeBytes = 1024, IsPrimary = true },
                }.AsReadOnly(),
                Parent = null!,
            }).ToList().AsReadOnly(),
        };
    }

    [Fact]
    public void Constructor_StartsEmpty()
    {
        var vm = new ModelMarketplaceViewModel(
            marketplace: null!,
            downloader: null!,
            scanner: null!,
            settings: null!,
            logger: null);
        Assert.Empty(vm.Models);
        Assert.Empty(vm.SelectedVersions);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsConsoleVisible);
        Assert.Equal(ModelSourceKind.CivitAi, vm.ActiveSource);
    }

    [Fact]
    public void KindFilters_ContainsAllModelKindValues()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        // 9 enum values, 1 (Unknown) excluded = 8 visible filters
        Assert.Equal(8, vm.KindFilters.Count);
        Assert.Contains(ModelKind.Checkpoint, vm.KindFilters);
        Assert.Contains(ModelKind.LORA, vm.KindFilters);
        Assert.Contains(ModelKind.VAE, vm.KindFilters);
        Assert.DoesNotContain(ModelKind.Unknown, vm.KindFilters);
    }

    [Fact]
    public async Task Query_SetBeforeRefresh_FiltersByTitleAfterRefresh()
    {
        // v0.6.22 T6:Query setter 不再 auto-filter on type — 改 Enter/按钮显式触发。
        // Query 必须在 RefreshAsync 前 set,refresh 时 ApplyFilter 用 _query 过滤 results。
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.Query = "Model 1";
        await vm.RefreshAsync();
        Assert.Single(vm.Models);
        Assert.Equal("1", vm.Models[0].SourceId);
    }

    [Fact]
    public void Query_SetAfterRefresh_DoesNotFilter()
    {
        // v0.6.22 T6:Query setter 不再触发 ApplyFilter — 改 Enter/按钮显式触发。
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.RefreshAsync().GetAwaiter().GetResult();
        var before = vm.Models.Count;
        vm.Query = "Model 1";  // set AFTER refresh — no-op
        Assert.Equal(before, vm.Models.Count);
    }

    [Fact]
    public async Task ActiveKindFilter_Set_FiltersByKind()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        vm.ActiveKindFilter = ModelKind.LORA;
        Assert.Single(vm.Models);
        Assert.Equal(ModelKind.LORA, vm.Models[0].Kind);
    }

    [Fact]
    public async Task SearchCommand_FiresRefresh()
    {
        // v0.6.22 T6:SearchCommand = 输入框 Enter 键 + 工具栏 搜索 按钮同一命令 — 触发 RefreshAsync。
        var marketplace = new MockModelMarketplaceService { DelayMs = 50 };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.SearchCommand.Execute(null);
        // fire-and-forget on UI thread — allow completion
        for (var i = 0; i < 100 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.True(marketplace.CallCount >= 1);
    }

    [Fact]
    public async Task SearchCommand_DisabledWhileBusy()
    {
        // 注入 delay 让 RefreshAsync 在 IsBusy=true 期间有可观察窗口 — 否则 mock 同步返回导致 IsBusy 立即回落。
        var marketplace = new MockModelMarketplaceService { DelayMs = 100 };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        // 第 1 次 refresh 启动
        vm.SearchCommand.Execute(null);
        await Task.Delay(20);  // 让 fire-and-forget 进 IsBusy=true 状态
        Assert.True(vm.IsBusy, "Refresh should be in flight");
        // IsBusy=true 期间再点 — SearchCommand 不能并发(由 IsBusy setter RaiseCanExecuteChanged 触发)
        Assert.False(vm.SearchCommand.CanExecute(null));
        // 让 in-flight refresh 收尾
        for (var i = 0; i < 50 && vm.IsBusy; i++) await Task.Delay(20);
        // 收尾后 CanExecute 恢复
        Assert.True(vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedVersions_AddingVersion_FiresCollectionChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        var version = MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")).Versions[0];
        var changed = false;
        vm.SelectedVersions.CollectionChanged += (_, _) => changed = true;
        vm.SelectedVersions.Add(version);
        Assert.True(changed);
    }

    [Fact]
    public void ConsoleLog_AddLine_FiresIsConsoleVisibleChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsConsoleVisible)) changed = true;
        };
        vm.ConsoleLog.Add("hello");
        Assert.True(changed);
        Assert.True(vm.IsConsoleVisible);
    }

    [Fact]
    public void HideConsoleCommand_FiresPropertyChanged()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("hello");
        Assert.True(vm.IsConsoleVisible);
        vm.HideConsoleCommand.Execute(null);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public void ClearConsoleCommand_RemovesAllLines()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("line 1");
        vm.ConsoleLog.Add("line 2");
        vm.ClearConsoleLogCommand.Execute(null);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);
    }

    // v0.6.22+:Per-source proxy CheckBox 从 model marketplace view 移除(用户 2026-08-20
    // 反馈 "勾选代理直接在设置中勾选就好了,就不要在界面中选择是否使用代理")。
    // 对应 VM 属性 CivitAiUseProxy / HuggingFaceUseProxy / IsGlobalProxyEnabled 已删除。
    // Proxy 配置仍走 SettingsViewModel.ModelSourceCivitAiUseProxy / ModelSourceHuggingFaceUseProxy。

    // —— v0.6.22+ 新增功能测试 ——

    [Fact]
    public void IncludeNsfw_DefaultsTrue_ShowsAllModels()
    {
        // 默认 IncludeNsfw=true 时 Models 包含 SFW + Mature + NSFW。
        var marketplace = new MockModelMarketplaceService(
            MakeNsfwModel(1, ModelNsfwKind.SFW),
            MakeNsfwModel(2, ModelNsfwKind.Mature),
            MakeNsfwModel(3, ModelNsfwKind.NSFW));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        vm.RefreshAsync().GetAwaiter().GetResult();
        Assert.True(vm.IncludeNsfw);
        Assert.Equal(3, vm.Models.Count);
    }

    [Fact]
    public async Task IncludeNsfw_SetFalse_HidesNonSfwModels()
    {
        // 用户 2026-08-20 反馈"NSFW 是否可以有一个复选框,用来过滤"。
        var marketplace = new MockModelMarketplaceService(
            MakeNsfwModel(1, ModelNsfwKind.SFW),
            MakeNsfwModel(2, ModelNsfwKind.Mature),
            MakeNsfwModel(3, ModelNsfwKind.NSFW));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        vm.IncludeNsfw = false;
        Assert.Single(vm.Models);
        Assert.Equal(ModelNsfwKind.SFW, vm.Models[0].NsfwKind);
    }

    [Fact]
    public async Task IncludeNsfw_ToggleRoundTrip_RestoresAll()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeNsfwModel(1, ModelNsfwKind.SFW),
            MakeNsfwModel(2, ModelNsfwKind.NSFW));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        vm.IncludeNsfw = false;
        Assert.Single(vm.Models);
        vm.IncludeNsfw = true;
        Assert.Equal(2, vm.Models.Count);
    }

    [Fact]
    public async Task LoadMoreCommand_AppendsPageResults()
    {
        // Mock 同时支持 LoadPageAsync — 第一页 2 条 + cursor "next",
        // 第二页 1 条 + cursor null。点 LoadMore 后 _allModels 累计。
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")))
        { NextPageResults = new[] { MakeModel(3, ModelKind.Checkpoint, ("v1", "1.0")) } };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.LoadedCount);
        Assert.True(vm.HasNextPage);  // mock 第一页返 cursor "next"
        Assert.True(vm.LoadMoreCommand.CanExecute(null));
        await vm.LoadMoreAsync();
        Assert.Equal(3, vm.LoadedCount);
        Assert.False(vm.HasNextPage);  // 第二页 cursor=null
        Assert.False(vm.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadMoreCommand_DisabledWhenNoMore()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")))
        { NextCursorResult = null };  // 第一页就耗尽
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.False(vm.HasNextPage);
        Assert.False(vm.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleConsoleVisibilityCommand_FlipsVisibility()
    {
        // v0.6.22+:toolbar "Console" 按钮 — 可见时点 → 隐藏;隐藏时点 → 显示。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("hello");  // 让 IsConsoleVisible=true
        Assert.True(vm.IsConsoleVisible);
        vm.ToggleConsoleVisibilityCommand.Execute(null);
        Assert.False(vm.IsConsoleVisible);  // → 隐藏
        vm.ToggleConsoleVisibilityCommand.Execute(null);
        Assert.True(vm.IsConsoleVisible);  // → 再显示
    }

    private static ModelEntry MakeNsfwModel(int id, ModelNsfwKind nsfwKind)
    {
        var entry = MakeModel(id, ModelKind.Checkpoint, ("v1", "1.0"));
        return new ModelEntry
        {
            Source = entry.Source,
            SourceId = entry.SourceId,
            SourceUrl = entry.SourceUrl,
            Title = entry.Title,
            Description = entry.Description,
            Kind = entry.Kind,
            NsfwKind = nsfwKind,
            PreviewImageUrl = entry.PreviewImageUrl,
            Tags = entry.Tags,
            Versions = entry.Versions,
        };
    }

    /// <summary>
    /// v0.6.20 T8:Mock marketplace — 返回固定模型列表。
    /// v0.6.22 T6+ override 5 参版 LoadAllAsync(VM 走 sourceFilter + IProgress 入参),
    /// 记录 CallCount / LastSourceFilter / ProgressLines。
    /// v0.6.22+ override LoadPageAsync — 首返 _entries + cursor "next",
    /// 二次返 NextPageResults + cursor null(模拟分页耗尽)。
    /// DelayMs 属性让调用方在 IsBusy=true 期间留出观察窗口(否则同步 mock 立即回落)。
    /// </summary>
    private sealed class MockModelMarketplaceService : ModelMarketplaceService
    {
        private readonly List<ModelEntry> _entries;
        private int _pageCallCount;

        public MockModelMarketplaceService(params ModelEntry[] entries)
            : base(Enumerable.Empty<IModelSource>(), null)
        {
            _entries = entries.ToList();
        }

        public int CallCount { get; private set; }
        public ModelSourceKind? LastSourceFilter { get; private set; }
        public CivitAiSort LastSort { get; private set; }
        public CivitAiPeriod LastPeriod { get; private set; }
        public int DelayMs { get; set; }
        public List<string> ProgressLines { get; } = new();

        // v0.6.22+:分页 mock — 默认首返 cursor="next", 二次返 cursor=null 模拟耗尽。
        // 调用方可设 NextPageResults 自定义第二页内容 + NextCursorResult=null 测试"无更多"场景。
        public ModelEntry[]? NextPageResults { get; set; }
        public string? NextCursorResult { get; set; } = "next";

        public override async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
            IProgress<string>? progress, CancellationToken ct = default)
        {
            CallCount++;
            LastSourceFilter = sourceFilter;
            progress?.Report($"[mock] 开始 query='{query}' filter={sourceFilter}");
            if (DelayMs > 0) await Task.Delay(DelayMs);
            progress?.Report($"[mock] 完成 {_entries.Count} 条");
            return _entries;
        }

        public override async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> LoadPageAsync(
            string query, string? cursor, int pageSize, ModelSourceKind? sourceFilter,
            CivitAiSort sort, CivitAiPeriod period,
            IProgress<string>? progress, CancellationToken ct = default)
        {
            _pageCallCount++;
            CallCount++;
            LastSort = sort;
            LastPeriod = period;
            if (DelayMs > 0) await Task.Delay(DelayMs);

            // cursor=null 第一页 → 返 _entries + "next"
            // cursor="next" 第二页 → 返 NextPageResults + null(耗尽)
            // 之后 cursor="next" 也按第二页返(测试不需要第三次)
            if (cursor is null && _pageCallCount == 1)
            {
                progress?.Report($"[mock] page 1: {_entries.Count} 条, next={NextCursorResult} sort={sort} period={period}");
                return (_entries, NextCursorResult);
            }
            var nextEntries = NextPageResults ?? Array.Empty<ModelEntry>();
            progress?.Report($"[mock] page 2: {nextEntries.Length} 条");
            return (nextEntries, null);
        }
    }
}