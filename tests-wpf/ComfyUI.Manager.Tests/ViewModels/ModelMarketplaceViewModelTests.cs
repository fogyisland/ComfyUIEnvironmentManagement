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

    /// <summary>
    /// v0.6.20 T8:Mock marketplace — 返回固定模型列表。
    /// v0.6.22 T6 加 4 参 override(VM 改走 sourceFilter 入参),记录 CallCount 跟 LastSourceFilter。
    /// 3 参版保留作为旧测试兜底(实际不会被 VM 触发)。
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
        public int DelayMs { get; set; }

        public override async Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, ModelSourceKind? sourceFilter = null, CancellationToken ct = default)
        {
            CallCount++;
            LastSourceFilter = sourceFilter;
            if (DelayMs > 0) await Task.Delay(DelayMs);
            return _entries;
        }
    }
}