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

    /// <summary>Recording mock — 记录每次 LoadAllAsync 的入参(query / sourceFilter / call count)。</summary>
    private sealed class RecordingMockMarketplace : ModelMarketplaceService
    {
        public int CallCount { get; set; }
        public string? LastQuery { get; set; }
        public ModelSourceKind? LastSourceFilter { get; set; }

        public RecordingMockMarketplace()
            : base(Enumerable.Empty<IModelSource>(), null) { }

        public override Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, ModelSourceKind? sourceFilter = null, CancellationToken ct = default)
        {
            CallCount++;
            LastQuery = query;
            LastSourceFilter = sourceFilter;
            return Task.FromResult<IReadOnlyList<ModelEntry>>(Array.Empty<ModelEntry>());
        }
    }
}