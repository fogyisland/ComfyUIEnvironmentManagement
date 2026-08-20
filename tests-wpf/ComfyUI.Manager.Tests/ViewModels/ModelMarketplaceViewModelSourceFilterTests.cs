using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.ModelSources;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.22 T6:ActiveSource 单选 radio — 切换 radio 自动重跑当前 query
/// (走 service.LoadAllAsync sourceFilter 参),不再走 v0.6.21 view-time ICollectionView.Filter。
///
/// 验证:
/// 1) 默认 ActiveSource = CivitAi(用户确认)
/// 2) RefreshAsync 把当前 ActiveSource 透传给 service
/// 3) 设置 ActiveSource 不直接调 service(setter 只 fire-and-forget RefreshAsync,
///    测试覆盖 by SearchCommandTests / RefreshAsyncTests);Query setter 不再 auto-filter。
///
/// 注意:service.sourceFilter 把 service._sources 按 sourceKind 过滤 — 测试不需要真 source,
/// MockModelMarketplaceService 直接 override LoadAllAsync 4 参版记录入参。
/// </summary>
public class ModelMarketplaceViewModelSourceFilterTests
{
    private static RecordingMockMarketplace MakeRecordingMock()
        => new();

    [Fact]
    public void ActiveSource_Default_IsCivitAi()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(ModelSourceKind.CivitAi, vm.ActiveSource);
    }

    [Fact]
    public async Task RefreshAsync_PassesCurrentActiveSource_AsSourceFilter()
    {
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        // 先 await 一次让默认状态 settle,再改 ActiveSource(setter fire-and-forget 也会触发一次)
        await vm.RefreshAsync();
        var baseline = mock.CallCount;
        mock.LastSourceFilter = null;
        vm.ActiveSource = ModelSourceKind.HuggingFace;
        // fire-and-forget 必触发一次 RefreshAsync — 等它跑完
        for (var i = 0; i < 100 && mock.CallCount <= baseline; i++) await Task.Delay(10);
        Assert.True(mock.CallCount > baseline);
        Assert.Equal(ModelSourceKind.HuggingFace, mock.LastSourceFilter);
    }

    [Fact]
    public async Task RefreshAsync_CivitAiActiveSource_PassesCivitAiFilter()
    {
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        // 默认 CivitAi
        await vm.RefreshAsync();
        Assert.Equal(1, mock.CallCount);
        Assert.Equal(ModelSourceKind.CivitAi, mock.LastSourceFilter);
    }

    [Fact]
    public async Task RefreshAsync_AfterQueryChange_PassesLatestQuery()
    {
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        vm.Query = "controlnet";
        await vm.RefreshAsync();
        Assert.Equal("controlnet", mock.LastQuery);
        Assert.Equal(ModelSourceKind.CivitAi, mock.LastSourceFilter);
    }

    [Fact]
    public void Query_Change_DoesNotAutoFilter()
    {
        // v0.6.22 T6:UI 改 Enter 键 / 按钮显式触发;Query setter 不再 auto-filter on type。
        // 模型测试:Query 改变后,Models 集合应保持不变(直到用户按 Enter / 点 搜索)。
        var vm = new ModelMarketplaceViewModel(MakeRecordingMock(), null!, null!, null!, null);
        vm.Models.Add(new ModelEntry { Title = "Foo", Source = ModelSourceKind.CivitAi, SourceId = "1" });
        var before = vm.Models.Count;
        vm.Query = "new search text";
        Assert.Equal(before, vm.Models.Count);
    }

    // —— v0.6.22+:CivitAI sort + period 过滤 ——

    [Fact]
    public void ActiveSort_Default_IsNewest()
    {
        // 默认 Newest(API 默认值),UI 首次显示高亮 "Newest" chip。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(CivitAiSort.Newest, vm.ActiveSort);
    }

    [Fact]
    public void ActivePeriod_Default_IsAllTime()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(CivitAiPeriod.AllTime, vm.ActivePeriod);
    }

    [Fact]
    public void SortOptions_ContainsAllEnumValues()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(5, vm.SortOptions.Count);
        Assert.Contains(CivitAiSort.Newest, vm.SortOptions);
        Assert.Contains(CivitAiSort.MostDownloaded, vm.SortOptions);
        Assert.Contains(CivitAiSort.TopRated, vm.SortOptions);
        Assert.Contains(CivitAiSort.MostLiked, vm.SortOptions);
        Assert.Contains(CivitAiSort.MostDiscussed, vm.SortOptions);
    }

    [Fact]
    public void PeriodOptions_ContainsAllEnumValues()
    {
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(5, vm.PeriodOptions.Count);
        Assert.Contains(CivitAiPeriod.AllTime, vm.PeriodOptions);
        Assert.Contains(CivitAiPeriod.Year, vm.PeriodOptions);
        Assert.Contains(CivitAiPeriod.Month, vm.PeriodOptions);
        Assert.Contains(CivitAiPeriod.Week, vm.PeriodOptions);
        Assert.Contains(CivitAiPeriod.Day, vm.PeriodOptions);
    }

    [Fact]
    public async Task RefreshAsync_PassesDefaultSortAndPeriod()
    {
        // 第一次 refresh(未改 ActiveSort/ActivePeriod)应透传 Newest/AllTime。
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Equal(CivitAiSort.Newest, mock.LastSort);
        Assert.Equal(CivitAiPeriod.AllTime, mock.LastPeriod);
    }

    [Fact]
    public async Task ActiveSort_Set_TriggersRefreshWithNewSort()
    {
        // v0.6.22+:切 sort chip → setter 自动 fire-and-forget RefreshAsync,新 sort 必须透传。
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        await vm.RefreshAsync();
        var baseline = mock.CallCount;
        mock.LastSort = CivitAiSort.Newest;  // 重置 baseline
        vm.ActiveSort = CivitAiSort.MostDownloaded;
        // fire-and-forget 必触发一次 RefreshAsync — 等它跑完
        for (var i = 0; i < 100 && mock.CallCount <= baseline; i++) await Task.Delay(10);
        Assert.True(mock.CallCount > baseline);
        Assert.Equal(CivitAiSort.MostDownloaded, mock.LastSort);
    }

    [Fact]
    public async Task ActivePeriod_Set_TriggersRefreshWithNewPeriod()
    {
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        await vm.RefreshAsync();
        var baseline = mock.CallCount;
        vm.ActivePeriod = CivitAiPeriod.Week;
        for (var i = 0; i < 100 && mock.CallCount <= baseline; i++) await Task.Delay(10);
        Assert.Equal(CivitAiPeriod.Week, mock.LastPeriod);
    }

    // —— v0.6.22+:CivitAI baseModel 过滤 ——

    [Fact]
    public void ActiveBaseModel_Default_IsAll()
    {
        // 默认 All = 不过滤(不附加 baseModels= URL 参数),chip 首次显示 "All" 高亮。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.Equal(CivitAiBaseModel.All, vm.ActiveBaseModel);
    }

    [Fact]
    public void BaseModelOptions_ContainsCommonBaseModelValues()
    {
        // 至少要覆盖常用 baseModel:SD 1.5/SDXL/Flux/Pony/SD 3.5/Wan Video 等(用户 2026-08-20 反馈
        // "常规可用的 Model 类型")。All 作为 chip 列表第 1 项 = 默认不过滤入口。
        var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
        Assert.True(vm.BaseModelOptions.Count >= 10,
            $"chip 数至少 10,实际 {vm.BaseModelOptions.Count}");
        Assert.Contains(CivitAiBaseModel.All, vm.BaseModelOptions);
        Assert.Contains(CivitAiBaseModel.SD_1_5, vm.BaseModelOptions);
        Assert.Contains(CivitAiBaseModel.SDXL_1_0, vm.BaseModelOptions);
        Assert.Contains(CivitAiBaseModel.Flux_1_D, vm.BaseModelOptions);
        Assert.Contains(CivitAiBaseModel.Pony, vm.BaseModelOptions);
    }

    [Fact]
    public async Task RefreshAsync_DefaultBaseModel_PassesNull()
    {
        // 默认 All = 不过滤,VM 透传 baseModel=null(服务层不附加 baseModels= 参数)。
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        await vm.RefreshAsync();
        Assert.Null(mock.LastBaseModel);
    }

    [Fact]
    public async Task ActiveBaseModel_Set_TriggersRefreshWithApiValue()
    {
        // v0.6.22+:切 baseModel chip → setter 自动 fire-and-forget RefreshAsync,
        // 新值通过 ApiValue() 扩展方法转成 CivitAI 字符串透传。SDXL_1_0 → "SDXL 1.0"。
        var mock = MakeRecordingMock();
        var vm = new ModelMarketplaceViewModel(mock, null!, null!, null!, null);
        await vm.RefreshAsync();
        var baseline = mock.CallCount;
        mock.LastBaseModel = null;
        vm.ActiveBaseModel = CivitAiBaseModel.SDXL_1_0;
        for (var i = 0; i < 100 && mock.CallCount <= baseline; i++) await Task.Delay(10);
        Assert.True(mock.CallCount > baseline);
        Assert.Equal("SDXL 1.0", mock.LastBaseModel);
    }

    [Fact]
    public void CivitAiBaseModelApiValue_RoundTripsForAllValues()
    {
        // ApiValue + FromApi 互逆(All 例外,因为 All → null,FromApi(null) → All)。
        foreach (var m in Enum.GetValues<CivitAiBaseModel>())
        {
            var apiValue = m.ApiValue();
            if (m == CivitAiBaseModel.All)
            {
                Assert.Null(apiValue);
                Assert.Equal(CivitAiBaseModel.All, CivitAiBaseModelExtensions.FromApi(null));
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(apiValue), $"枚举 {m} 应有非空 ApiValue");
                Assert.Equal(m, CivitAiBaseModelExtensions.FromApi(apiValue));
            }
        }
    }

    /// <summary>Recording mock — 记录每次 LoadPageAsync / LoadAllAsync 的入参(query / sourceFilter / call count)。
/// v0.6.22+:RefreshAsync 改走 LoadPageAsync(走 cursor),所以 mock 同时 override 两个方法
/// 让 LoadAllAsync-based 老测试还能记录 call count。</summary>
    private sealed class RecordingMockMarketplace : ModelMarketplaceService
    {
        public int CallCount { get; set; }
        public string? LastQuery { get; set; }
        public ModelSourceKind? LastSourceFilter { get; set; }
        public CivitAiSort LastSort { get; set; }
        public CivitAiPeriod LastPeriod { get; set; }
        public bool LastIncludeNsfw { get; set; } = true;
        public string? LastBaseModel { get; set; }

        public RecordingMockMarketplace()
            : base(Enumerable.Empty<IModelSource>(), null) { }

        public override Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, ModelSourceKind? sourceFilter,
            IProgress<string>? progress, bool includeNsfw, string? baseModel, CancellationToken ct = default)
        {
            CallCount++;
            LastQuery = query;
            LastSourceFilter = sourceFilter;
            LastIncludeNsfw = includeNsfw;
            LastBaseModel = baseModel;
            return Task.FromResult<IReadOnlyList<ModelEntry>>(Array.Empty<ModelEntry>());
        }

        public override Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> LoadPageAsync(
            string query, string? cursor, int pageSize, ModelSourceKind? sourceFilter,
            CivitAiSort sort, CivitAiPeriod period,
            IProgress<string>? progress, bool includeNsfw = true, string? baseModel = null, CancellationToken ct = default)
        {
            CallCount++;
            LastQuery = query;
            LastSourceFilter = sourceFilter;
            LastSort = sort;
            LastPeriod = period;
            LastIncludeNsfw = includeNsfw;
            LastBaseModel = baseModel;
            return Task.FromResult<(IReadOnlyList<ModelEntry>, string?)>(
                (Array.Empty<ModelEntry>(), (string?)null));
        }
    }
}