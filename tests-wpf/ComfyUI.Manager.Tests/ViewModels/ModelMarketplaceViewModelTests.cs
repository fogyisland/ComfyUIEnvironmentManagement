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
/// 镜像 v0.6.19 WorkflowMarketplaceViewModelTests,验证 8 个核心场景:
/// 构造器初始空 / Kind 枚举 8 项 / Query 触发 title filter /ActiveKindFilter 触发 kind filter /
/// SelectedVersions 集合改变通知 / ConsoleLog 触发 IsConsoleVisible / Hide + Clear 命令。
///
/// 注:brief 原稿用 `SearchAsync` —— 实际 API 是 `LoadAllAsync`(T4 已 ship),测试跟随实际签名。
/// 注:brief 原稿用 `MockModelMarketplaceService(ctored HttpClient, List&lt;IModelSource&gt;, logger)` —
/// 实际 ctor 是 `(IEnumerable&lt;IModelSource&gt; sources, AppLogger? logger)`,跟随实际签名。
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
    public async Task Query_Text_FiltersByTitle()
    {
        var marketplace = new MockModelMarketplaceService(
            MakeModel(1, ModelKind.Checkpoint, ("v1", "1.0")),
            MakeModel(2, ModelKind.LORA, ("v1", "1.0")));
        var vm = new ModelMarketplaceViewModel(marketplace, null!, null!, null!, null);
        await vm.RefreshAsync();
        vm.Query = "Model 1";
        Assert.Single(vm.Models);
        Assert.Equal("1", vm.Models[0].SourceId);
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
    /// Override LoadAllAsync(实际 API,brief 写错 SearchAsync)。
    /// </summary>
    private sealed class MockModelMarketplaceService : ModelMarketplaceService
    {
        private readonly List<ModelEntry> _entries;

        public MockModelMarketplaceService(params ModelEntry[] entries)
            : base(Enumerable.Empty<IModelSource>(), null)
        {
            _entries = entries.ToList();
        }

        public override Task<IReadOnlyList<ModelEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelEntry>>(_entries);
    }
}
