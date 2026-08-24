using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class LocalModelsViewModelTests
{
    private static Settings SettingsWith(string modelsDir) => new() { DefaultModelsDirectory = modelsDir };

    [Fact]
    public void Initialize_EmptySettings_ShowsUnconfiguredMessage()
    {
        var vm = new LocalModelsViewModel(new Settings(), new FakeScanner());
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Equal("未配置 Models 目录 — 请在设置中配置", vm.EmptyMessage);
        Assert.Empty(vm.FilteredModels);
    }

    [Fact]
    public void Initialize_EmptyDirectory_ShowsNoModelsMessage()
    {
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\nonexistent"), new FakeScanner());
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Equal("暂无已下载模型", vm.EmptyMessage);
        Assert.Empty(vm.FilteredModels);
    }

    [Fact]
    public void Initialize_ThreeModels_BuildsThreeCardsAndFourKindChips()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "m1", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now.AddDays(-3) },
                new() { Title = "m2", Kind = ModelKind.LORA, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "2", SourceVersionId = "v2", DownloadedAt = DateTime.Now.AddDays(-2) },
                new() { Title = "m3", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.HuggingFace.ToString(), SourceId = "3", SourceVersionId = "v3", DownloadedAt = DateTime.Now.AddDays(-1) },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Equal(3, vm.FilteredModels.Count);
        Assert.Null(vm.EmptyMessage);
        Assert.Equal(3, vm.KindChips.Count);   // 全部 + Checkpoint(2) + LORA(1)
        Assert.Equal("全部", vm.KindChips[0].Display);
        Assert.Null(vm.KindChips[0].Kind);
        Assert.Equal(2, vm.KindChips[1].Count);  // Checkpoint
        Assert.Equal(1, vm.KindChips[2].Count);  // LORA
    }

    [Fact]
    public void Initialize_TwoVersionsSameSourceId_GroupsIntoOneCard()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "shared", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "42", SourceVersionId = "v1", DownloadedAt = DateTime.Now.AddDays(-5) },
                new() { Title = "shared", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "42", SourceVersionId = "v2", DownloadedAt = DateTime.Now.AddDays(-1) },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Equal(2, vm.FilteredModels[0].VersionCount);
        Assert.Equal(vm.FilteredModels[0].LatestDownloadedAt, fake.Entries[1].DownloadedAt);  // 最新 = 最新 version
    }

    [Fact]
    public void ActiveChip_ChangedToLora_FiltersToLoraOnly()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "m1", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now },
                new() { Title = "m2", Kind = ModelKind.LORA, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "2", SourceVersionId = "v2", DownloadedAt = DateTime.Now },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        var loraChip = vm.KindChips.Single(c => c.Kind == ModelKind.LORA);
        vm.ActiveChip = loraChip;

        Assert.Single(vm.FilteredModels);
        Assert.Equal(ModelKind.LORA, vm.FilteredModels[0].Kind);
    }

    [Fact]
    public void ActiveChip_BackToAll_RestoresFullList()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "m1", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now },
                new() { Title = "m2", Kind = ModelKind.LORA, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "2", SourceVersionId = "v2", DownloadedAt = DateTime.Now },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();
        vm.ActiveChip = vm.KindChips.Single(c => c.Kind == ModelKind.LORA);
        vm.ActiveChip = vm.KindChips[0];   // 全部

        Assert.Equal(2, vm.FilteredModels.Count);
    }

    [Fact]
    public void ReloadAsync_RerunsScanAndResetsActiveChipToAll()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "m1", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();
        vm.ActiveChip = vm.KindChips[0];   // 全部
        fake.Entries = new List<DownloadedModel>
        {
            new() { Title = "m2", Kind = ModelKind.LORA, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "2", SourceVersionId = "v2", DownloadedAt = DateTime.Now },
        };

        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Equal(ModelKind.LORA, vm.FilteredModels[0].Kind);
        Assert.Equal("全部", vm.ActiveChip!.Display);
    }

    [Fact]
    public void ReloadAsync_ScannerThrows_EmptyStateAndNoCrash()
    {
        var fake = new FakeScanner { Throw = new InvalidOperationException("disk fail") };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Empty(vm.FilteredModels);
        Assert.Equal("暂无已下载模型", vm.EmptyMessage);
    }

    [Fact]
    public void Initialize_OrdersByLatestDownloadedAtDescending()
    {
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new() { Title = "old", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now.AddDays(-30) },
                new() { Title = "newest", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "2", SourceVersionId = "v2", DownloadedAt = DateTime.Now.AddDays(-1) },
                new() { Title = "mid", Kind = ModelKind.Checkpoint, Source = ModelSourceKind.CivitAi.ToString(), SourceId = "3", SourceVersionId = "v3", DownloadedAt = DateTime.Now.AddDays(-7) },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Equal("newest", vm.FilteredModels[0].Title);
        Assert.Equal("mid", vm.FilteredModels[1].Title);
        Assert.Equal("old", vm.FilteredModels[2].Title);
    }
}

internal sealed class FakeScanner : ModelFilesystemScanner
{
    public IReadOnlyList<DownloadedModel> Entries { get; set; } = Array.Empty<DownloadedModel>();
    public Exception? Throw { get; set; }

    public override IReadOnlyList<DownloadedModel> Scan(string modelsDir)
    {
        if (Throw is not null) throw Throw;
        return Entries;
    }
}
