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
        // 10 enum values (T12 added Diffusers), 1 (Unknown) excluded = 9 visible filters
        Assert.Equal(9, vm.KindFilters.Count);
        Assert.Contains(ModelKind.Checkpoint, vm.KindFilters);
        Assert.Contains(ModelKind.LORA, vm.KindFilters);
        Assert.Contains(ModelKind.VAE, vm.KindFilters);
        Assert.Contains(ModelKind.Diffusers, vm.KindFilters);   // v1.0.0 T-D4:T12 added Diffusers
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

    // —— v0.6.22++ Console 反向滚动 + 时间戳 ——

    [Fact]
    public void ConsoleLog_AppendConsole_PrependsTimestampAndInsertsAtIndex0()
    {
        // 用户 2026-08-21 反馈"模型市场 console 中使用反向滚动,也就是最上面的为最新,
        // 另外为日志加上时间戳"。AppendConsole(line) 必须:
        //   1. 在行首加 [HH:mm:ss] 时间戳(同秒多次调也即时反映)
        //   2. 插入到 index 0(最新在最上面)
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.ConsoleLog.Add("first");          // 旧调用 OK,不破坏现有 contract
        vm.AppendConsole("second");

        Assert.Equal(2, vm.ConsoleLog.Count);
        Assert.Equal("first", vm.ConsoleLog[1]);  // 旧行保持在 index 1
        var newest = vm.ConsoleLog[0];
        // 时间戳格式 [HH:mm:ss] + 空格 + 原文;小时可能在 0-23,分钟秒 00-59
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] second$", newest);
    }

    [Fact]
    public void ConsoleLog_AppendConsole_NewerLineAppearsAboveOlder()
    {
        // 关键 UX 校验:连续 AppendConsole 3 行后,ConsoleLog[0] 是最后一行。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        vm.AppendConsole("alpha");
        vm.AppendConsole("beta");
        vm.AppendConsole("gamma");

        Assert.Equal(3, vm.ConsoleLog.Count);
        Assert.Contains("gamma", vm.ConsoleLog[0]);
        Assert.Contains("beta", vm.ConsoleLog[1]);
        Assert.Contains("alpha", vm.ConsoleLog[2]);
    }

    [Fact]
    public void ConsoleLog_ProgressSinkUsesAppendConsole()
    {
        // Progress<string>(line => AppendConsole(line)) 模式被 RefreshAsync / LoadMoreAsync
        // / DownloadSelectedAsync 三处共享。模拟 source 透过 IProgress<string> 回调时,
        // 所有行必须带时间戳 + 插入头部。
        // 注:Progress<T>.Report 是 IProgress<T> 的显式接口实现,只能通过接口类型调;
        // 这里直接调 lambda 等价(生产代码 RefreshAsync:397/437/498 就是这个 lambda)。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Action<string> sink = line => vm.AppendConsole(line);
        sink("[URL] https://example.com/x");
        sink("plain line");

        Assert.Equal(2, vm.ConsoleLog.Count);
        // 新 -> 上:plain line (最后调用) 在 index 0;[URL] (先调用) 在 index 1。
        Assert.Contains("plain line", vm.ConsoleLog[0]);
        Assert.Contains("[URL] https://example.com/x", vm.ConsoleLog[1]);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] ", vm.ConsoleLog[0]);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] ", vm.ConsoleLog[1]);
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
        // v0.6.22+:NSFW 透传 API(setter 触发 refresh,fire-and-forget)— 用户
        // 2026-08-20 "因为我们就需要完整的非NSFW数据"。Mock 模拟 source 端过滤逻辑。
        var marketplace = new MockModelMarketplaceService(
            MakeNsfwModel(1, ModelNsfwKind.SFW),
            MakeNsfwModel(2, ModelNsfwKind.Mature),
            MakeNsfwModel(3, ModelNsfwKind.NSFW));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        await marketplacetempToggle(vm, marketplace);
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
        await marketplacetempToggle(vm, marketplace);
        Assert.Single(vm.Models);
        await marketplacetempToggle(vm, marketplace, includeNsfw: true);
        Assert.Equal(2, vm.Models.Count);
    }

    [Fact]
    public async Task IncludeNsfw_SetFalse_TriggersRefreshWithIncludeNsfwFalse()
    {
        // v0.6.22+:NSFW 在 API 层透传 — 服务调用应带 includeNsfw=false。
        // 旧行为只调 ApplyFilter 不重 fetch,source 没机会返回正确的全量 SFW。
        var marketplace = new MockModelMarketplaceService { DelayMs = 20 };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.True(marketplace.LastIncludeNsfw);  // 默认 true
        vm.IncludeNsfw = false;
        // 等 fire-and-forget 的 refresh 跑完
        for (var i = 0; i < 100 && marketplace.LastIncludeNsfw; i++) await Task.Delay(10);
        Assert.False(marketplace.LastIncludeNsfw);
    }

    /// <summary>v0.6.22+:NSFW 切换是 fire-and-forget 触发 refresh — 等待 mock 收到新 includeNsfw 才断言。</summary>
    private static async Task marketplacetempToggle(ModelMarketplaceViewModel vm, MockModelMarketplaceService mock, bool includeNsfw = false)
    {
        mock.LastIncludeNsfw = !includeNsfw;  // sentinel,会在 refresh 后变成新值
        vm.IncludeNsfw = includeNsfw;
        for (var i = 0; i < 100 && mock.LastIncludeNsfw == !includeNsfw; i++) await Task.Delay(10);
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
    public void IsEmpty_DefaultsTrueWhenNoModelsLoaded()
    {
        // v0.6.22+:empty state overlay IsEmpty 计算属性 — 初始 Models.Count==0 → true,
        // 配合 loading overlay IsBusy=true 时 empty 自动隐藏(IsBusy setter 同步 fire)。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Empty(vm.Models);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task IsEmpty_BecomesFalseAfterRefreshWithResults()
    {
        // refresh 后 Models 有数据 → IsEmpty 应转 false(Models.CollectionChanged hook 触发)。
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        Assert.True(vm.IsEmpty);
        await vm.RefreshAsync();
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void NotIsBusy_DefaultsTrue()
    {
        // v0.6.22+:loading overlay ScrollViewer.IsEnabled 绑 NotIsBusy — 初始空闲时应为 true。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.False(vm.IsBusy);
        Assert.True(vm.NotIsBusy);
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

    // —— v0.6.22+ 页码式分页 ——
    // 用户 2026-08-20 提问:"如果进行 checkpoint 筛选了是不是会重新计算页数" —— 会,
    // 页数分母是筛选后的 _filtered,筛选变化同时把页码归零(见 KindFilter_* 测试)。

    private static ModelEntry[] MakeModels(int count, ModelKind kind, int startId = 1)
        => Enumerable.Range(startId, count)
            .Select(i => MakeModel(i, kind, ("v1", "1.0")))
            .ToArray();

    [Fact]
    public async Task Refresh_SplitsResultsIntoPagesOfPageSize()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(25, ModelKind.Checkpoint));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Equal(ModelMarketplaceViewModel.PageSize, vm.Models.Count);
        Assert.Equal(25, vm.TotalFilteredCount);
        Assert.Equal(2, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.False(vm.CanGoPrev);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public async Task NextPage_WithCachedResults_DoesNotRefetch()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(25, ModelKind.Checkpoint));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        var callsAfterRefresh = marketplace.CallCount;

        await vm.NextPageAsync();

        Assert.Equal(2, vm.CurrentPageNumber);
        Assert.Equal(5, vm.Models.Count);
        Assert.Equal(callsAfterRefresh, marketplace.CallCount);
        Assert.True(vm.CanGoPrev);
    }

    [Fact]
    public async Task PrevPage_ReturnsToPreviousPageFromCache()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(25, ModelKind.Checkpoint));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        await vm.NextPageAsync();

        vm.PrevPage();

        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.Equal(ModelMarketplaceViewModel.PageSize, vm.Models.Count);
        Assert.False(vm.CanGoPrev);
    }

    [Fact]
    public async Task NextPage_WhenCacheExhausted_FetchesAnotherPage()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(10, ModelKind.Checkpoint))
        {
            NextPageResults = MakeModels(15, ModelKind.Checkpoint, startId: 100),
        };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Equal(1, vm.TotalPages);

        await vm.NextPageAsync();

        Assert.Equal(25, vm.TotalFilteredCount);
        Assert.Equal(2, vm.TotalPages);
        Assert.Equal(2, vm.CurrentPageNumber);
        Assert.Equal(5, vm.Models.Count);
    }

    [Fact]
    public async Task KindFilter_RecalculatesTotalPagesAndResetsToFirstPage()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModels(25, ModelKind.Checkpoint)
                .Concat(MakeModels(5, ModelKind.LORA, startId: 100))
                .ToArray());
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        await vm.NextPageAsync();
        Assert.Equal(2, vm.CurrentPageNumber);

        vm.ActiveKindFilter = ModelKind.LORA;

        // 筛选后只剩 5 条 → 1 页,页码归零(否则停在已不存在的第 2 页 = 空白)
        Assert.Equal(5, vm.TotalFilteredCount);
        Assert.Equal(1, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.Equal(5, vm.Models.Count);
        Assert.False(vm.CanGoPrev);
    }

    [Fact]
    public async Task LoadMore_KeepsCurrentPage()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(25, ModelKind.Checkpoint))
        {
            NextPageResults = MakeModels(20, ModelKind.Checkpoint, startId: 100),
        };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        await vm.NextPageAsync();
        Assert.Equal(2, vm.CurrentPageNumber);

        await vm.LoadMoreAsync();

        // 追加数据不该把用户弹回第 1 页
        Assert.Equal(2, vm.CurrentPageNumber);
        Assert.Equal(45, vm.TotalFilteredCount);
        Assert.Equal(3, vm.TotalPages);
    }

    [Fact]
    public async Task CanGoNext_FalseOnLastPageWithoutCursor()
    {
        var marketplace = new MockModelMarketplaceService(MakeModels(5, ModelKind.Checkpoint))
        {
            NextCursorResult = null,
        };
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Equal(1, vm.TotalPages);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoPrev);
    }

    private static ModelEntry MakeNsfwModel(int id, ModelNsfwKind nsfwKind)    {
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

        public MockModelMarketplaceService(params ModelEntry[] entries)
            : base(Enumerable.Empty<IModelSource>(), null)
        {
            _entries = entries.ToList();
        }

        public int CallCount { get; private set; }
        public ModelSourceKind? LastSourceFilter { get; private set; }
        public CivitAiSort LastSort { get; private set; }
        public CivitAiPeriod LastPeriod { get; private set; }
        public bool LastIncludeNsfw { get; set; } = true;
        public string? LastBaseModel { get; set; }
        public int DelayMs { get; set; }
        public List<string> ProgressLines { get; } = new();

        // v0.6.22+:分页 mock — 默认首返 cursor="next", 二次返 cursor=null 模拟耗尽。
        // 调用方可设 NextPageResults 自定义第二页内容 + NextCursorResult=null 测试"无更多"场景。
        public ModelEntry[]? NextPageResults { get; set; }
        public string? NextCursorResult { get; set; } = "next";

        public override async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
            IProgress<string>? progress, bool includeNsfw, string? baseModel, CancellationToken ct = default)
        {
            CallCount++;
            LastSourceFilter = sourceFilter;
            LastIncludeNsfw = includeNsfw;
            LastBaseModel = baseModel;
            progress?.Report($"[mock] 开始 query='{query}' filter={sourceFilter} nsfw={includeNsfw} bm={baseModel ?? "(无)"}");
            if (DelayMs > 0) await Task.Delay(DelayMs);
            progress?.Report($"[mock] 完成 {_entries.Count} 条");
            return _entries;
        }

        public override async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> LoadPageAsync(
            string query, string? cursor, int pageSize, ModelSourceKind? sourceFilter,
            CivitAiSort sort, CivitAiPeriod period,
            IProgress<string>? progress, bool includeNsfw = true, string? baseModel = null, CancellationToken ct = default)
        {
            CallCount++;
            LastSort = sort;
            LastPeriod = period;
            LastIncludeNsfw = includeNsfw;
            LastBaseModel = baseModel;
            if (DelayMs > 0) await Task.Delay(DelayMs);

            // v0.6.22+:mock 模拟 source 端 NSFW 过滤 — includeNsfw=false 时
            // 只返 SFW 条目(对标 CivitAI `?nsfw=false` / HF post-filter)。
            IReadOnlyList<ModelEntry> entries = includeNsfw
                ? _entries
                : _entries.Where(e => e.NsfwKind == ModelNsfwKind.SFW).ToList();

            // cursor == null → 第一页(Refresh 触发,不管调用几次都返 _entries 因为这是
            // "新查询的第一页")。
            // cursor != null → 第二页(LoadMore 触发,返 NextPageResults)。
            if (cursor is null)
            {
                progress?.Report($"[mock] page 1: {entries.Count} 条, next={NextCursorResult} sort={sort} period={period} nsfw={includeNsfw} bm={baseModel ?? "(无)"}");
                return (entries, NextCursorResult);
            }
            var nextEntries = NextPageResults ?? Array.Empty<ModelEntry>();
            progress?.Report($"[mock] page 2+: {nextEntries.Length} 条");
            return (nextEntries, null);
        }
    }
}