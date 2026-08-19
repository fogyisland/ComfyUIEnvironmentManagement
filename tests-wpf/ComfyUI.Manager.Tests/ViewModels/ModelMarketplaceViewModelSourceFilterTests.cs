using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.21 T4:Source filter chips — ShowOnlyCivitai / ShowOnlyHuggingFace 两个 bool 闸
/// Models view 的可见性。仅切 view-time ICollectionView.Filter,不重 query source,
/// 也不改 Models 集合本身。
///
/// 验证 ApplySourceFilter() 是否对 Sources 正确分流(经由 GetDefaultView().Cast&lt;ModelEntry&gt;()
/// 取经过 filter 的可见条目):
/// 1) 关 CivitAI → view 中 CivitAI 条目不可见
/// 2) 关 HuggingFace → view 中 HuggingFace 条目不可见
/// 3) 全关 → view 渲染空(view 实际显示 empty state hint)
/// </summary>
public class ModelMarketplaceViewModelSourceFilterTests
{
    private static ModelEntry MakeEntry(ModelSourceKind source, int id) => new()
    {
        Source = source,
        SourceId = id.ToString(),
        SourceUrl = $"https://example.com/{source}/{id}",
        Title = $"{source} {id}",
        Kind = ModelKind.Checkpoint,
        NsfwKind = ModelNsfwKind.SFW,
        Versions = new List<ModelVersionEntry>().AsReadOnly(),
    };

    private static ModelMarketplaceViewModel MakeVmWithEntries(params ModelEntry[] entries)
    {
        var vm = new ModelMarketplaceViewModel(
            marketplace: null!,
            downloader: null!,
            scanner: null!,
            settings: null!,
            logger: null);
        foreach (var e in entries) vm.Models.Add(e);
        return vm;
    }

    private static List<ModelEntry> Visible(ModelMarketplaceViewModel vm)
    {
        var view = CollectionViewSource.GetDefaultView(vm.Models);
        return view.Cast<ModelEntry>().ToList();
    }

    [Fact]
    public void ShowOnlyCivitai_False_HidesCivitAiEntries()
    {
        var vm = MakeVmWithEntries(
            MakeEntry(ModelSourceKind.CivitAi, 1),
            MakeEntry(ModelSourceKind.HuggingFace, 2));
        vm.ShowOnlyCivitai = false;
        var visible = Visible(vm);
        Assert.Single(visible);
        Assert.Equal(ModelSourceKind.HuggingFace, visible[0].Source);
    }

    [Fact]
    public void ShowOnlyHuggingFace_False_HidesHuggingFaceEntries()
    {
        var vm = MakeVmWithEntries(
            MakeEntry(ModelSourceKind.CivitAi, 1),
            MakeEntry(ModelSourceKind.HuggingFace, 2));
        vm.ShowOnlyHuggingFace = false;
        var visible = Visible(vm);
        Assert.Single(visible);
        Assert.Equal(ModelSourceKind.CivitAi, visible[0].Source);
    }

    [Fact]
    public void BothFalse_RendersEmptyHint()
    {
        var vm = MakeVmWithEntries(MakeEntry(ModelSourceKind.CivitAi, 1));
        vm.ShowOnlyCivitai = false;
        vm.ShowOnlyHuggingFace = false;
        Assert.Empty(Visible(vm));
    }
}