using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    // -------- T10 PreviewImage 透传 tests --------

    [Fact]
    public void GroupToCards_PropagatesPreviewImagePath()
    {
        // 构造 1 DownloadedModel 带 PreviewImagePath = "/path/preview.png" → LocalModelCard.PreviewImagePath 透传
        var previewPath = Path.Combine("Z:", "loras", "mylora", "mylora.png");
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new()
                {
                    Title = "Mylora",
                    Kind = ModelKind.LORA,
                    Source = "Local",
                    SourceId = "local:lora/mylora",
                    SourceVersionId = "",
                    DownloadedAt = DateTime.Now,
                    PreviewImagePath = previewPath,
                },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Equal(previewPath, vm.FilteredModels[0].PreviewImagePath);
    }

    [Fact]
    public void GroupToCards_AggregatesLatestMtime_PreviewFromLatest()
    {
        // 2 records 同 SourceId 不同 mtime,GroupBy 后 latest mtime record 的 preview path wins
        // (T10:GroupToCards 用 OrderBy(DownloadedAt).Last() 代替 First() — deterministic tie-breaker)
        var oldPreview = Path.Combine("Z:", "loras", "x", "x_old.png");
        var newPreview = Path.Combine("Z:", "loras", "x", "x_new.png");
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new()
                {
                    Title = "x",
                    Kind = ModelKind.LORA,
                    Source = "Local",
                    SourceId = "local:lora/x",
                    SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now.AddDays(-10),
                    PreviewImagePath = oldPreview,
                },
                new()
                {
                    Title = "x",
                    Kind = ModelKind.LORA,
                    Source = "Local",
                    SourceId = "local:lora/x",
                    SourceVersionId = "v2",
                    DownloadedAt = DateTime.Now.AddDays(-1),
                    PreviewImagePath = newPreview,
                },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Equal(newPreview, vm.FilteredModels[0].PreviewImagePath);
    }

    [Fact]
    public void GroupToCards_NoPreviewImagePath_PropagatesNull()
    {
        // meta.json 路径 / 无 preview 的 record → PreviewImagePath = null 透传
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new()
                {
                    Title = "nopreview",
                    Kind = ModelKind.Checkpoint,
                    Source = "civitai",
                    SourceId = "999",
                    SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now,
                },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Null(vm.FilteredModels[0].PreviewImagePath);
    }

    // -------- v1.0.0 T12:Diffusers 透传 test --------

    [Fact]
    public void GroupToCards_DiffusersModel_PassesThroughKind()
    {
        // 构造 1 DownloadedModel(Kind=ModelKind.Diffusers) → LocalModelCard.Kind = ModelKind.Diffusers
        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new()
                {
                    Title = "sdxl-base",
                    Kind = ModelKind.Diffusers,
                    Source = "Local",
                    SourceId = "local:diffusers/sdxl-base",
                    SourceVersionId = "",
                    DownloadedAt = DateTime.Now,
                },
            }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), fake);
        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.Equal(ModelKind.Diffusers, vm.FilteredModels[0].Kind);
        Assert.Equal("sdxl-base", vm.FilteredModels[0].Title);
        Assert.Equal("Local", vm.FilteredModels[0].Source);
        Assert.Equal(1, vm.FilteredModels[0].VersionCount);
        // Kind chip 列表应包含 Diffusers
        Assert.Contains(vm.KindChips, c => c.Kind == ModelKind.Diffusers && c.Display == "Diffusers");
    }

    // -------- 用户反馈 "本地模型一直出在加载中" 修复 tests --------

    /// <summary>Slow scanner — Scan() 阻塞直到 ReleaseGate,模拟慢磁盘场景。
    /// 用来验证 ReloadAsync 的 in-flight 守卫:scan 还没完成时第二次调用必须 skip
    /// (否则 sidebar 反复切会启动多个并发 scan,互踩 FilteredModels)。
    /// AutoResetEvent-style gate:CloseGate 让下次 Scan 阻塞,ReleaseGate 释放阻塞。
    /// 必须成对调用 CloseGate/ReleaseGate 才能精确控制每次 Scan 的阻塞/释放。</summary>
    private sealed class SlowFakeScanner : ModelFilesystemScanner
    {
        private readonly ManualResetEventSlim _gate = new(true);   // initial: open
        public int ScanCallCount;

        public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
        {
            ScanCallCount++;
            _gate.Wait();
            return new List<DownloadedModel>
            {
                new() { Title = "m1", Kind = ModelKind.Checkpoint, Source = "Local",
                        SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now },
            };
        }

        public void CloseGate() => _gate.Reset();
        public void ReleaseGate() => _gate.Set();
    }

    [Fact]
    public void ReloadAsync_WhenAlreadyBusy_SkipsSecondCall()
    {
        // 用户反馈修复:ShowLocalModels 每次进入都 fire ReloadAsync,如果上次 scan 还在跑
        // (sidebar 反复切 + 慢磁盘),第二次必须 no-op 而不是并发跑两个 scan。
        // 验证:SlowFakeScanner.ScanCallCount 在 in-flight 期间第二次 ReloadAsync 后仍 == 1。
        var slow = new SlowFakeScanner();
        slow.CloseGate();   // 让首次 Scan 阻塞
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), slow);

        // fire first reload — Scan() 阻塞在 gate 上,IsBusy=true
        var firstTask = vm.ReloadAsync();
        // 等 task.Run 把 Scan() 调度起来 (否则 ScanCallCount 还是 0)
        SpinWait.SpinUntil(() => slow.ScanCallCount == 1, TimeSpan.FromSeconds(1));

        // 不等第一次完成,直接 fire 第二次 — 应该 skip (in-flight 守卫)
        var secondTask = vm.ReloadAsync();
        secondTask.GetAwaiter().GetResult();   // 同步等(应该立即返回)

        Assert.True(vm.IsBusy, "first scan still in flight");
        Assert.Equal(1, slow.ScanCallCount);    // 第二次 ReloadAsync 没进 Scan

        // 释放 first scan,让它完成
        slow.ReleaseGate();
        firstTask.GetAwaiter().GetResult();

        Assert.False(vm.IsBusy);
        Assert.Single(vm.FilteredModels);   // 第一个 scan 的结果生效
    }

    [Fact]
    public void ShowLoadingOverlay_TrueDuringFirstLoad_FalseAfterLoadComplete()
    {
        // 用户反馈修复:首次扫描时 overlay 应显示;加载完成后 overlay 消失。
        // 这是 XAML 绑 ShowLoadingOverlay 的基础契约。
        var slow = new SlowFakeScanner();
        slow.CloseGate();
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), slow);

        var task = vm.ReloadAsync();
        // 等 Scan 启动
        SpinWait.SpinUntil(() => slow.ScanCallCount == 1, TimeSpan.FromSeconds(1));

        // first load in flight — overlay should be on
        Assert.True(vm.ShowLoadingOverlay);

        slow.ReleaseGate();
        task.GetAwaiter().GetResult();

        // first load done — overlay off
        Assert.False(vm.ShowLoadingOverlay);
    }

    [Fact]
    public void IsRefreshingInBackground_FalseOnFirstLoad_TrueWhenRefreshingExistingData()
    {
        // 用户反馈修复:首次加载时 toolbar 不显示 "刷新中…"(避免误导);
        // 已有数据再触发 reload 时显示 — 跟 ShowLoadingOverlay 互补。
        var slow = new SlowFakeScanner();
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), slow);

        // 首次加载(open gate,scan 立即完成)— 验证 toolbar 指示 OFF
        slow.CloseGate();
        var initTask = vm.ReloadAsync();
        SpinWait.SpinUntil(() => slow.ScanCallCount == 1, TimeSpan.FromSeconds(1));
        Assert.True(vm.IsBusy);
        Assert.True(vm.ShowLoadingOverlay);
        Assert.False(vm.IsRefreshingInBackground);   // 首次:overlay 而非 toolbar
        slow.ReleaseGate();
        initTask.GetAwaiter().GetResult();

        Assert.Single(vm.FilteredModels);
        Assert.False(vm.IsBusy);

        // 现在模拟 background refresh:close gate,开第二次 reload,_allCards 已非空
        slow.CloseGate();
        var refreshTask = vm.ReloadAsync();
        SpinWait.SpinUntil(() => slow.ScanCallCount == 2, TimeSpan.FromSeconds(1));

        Assert.True(vm.IsRefreshingInBackground);
        Assert.False(vm.ShowLoadingOverlay);   // 互补:refresh 中不显示 loading overlay
        slow.ReleaseGate();
        refreshTask.GetAwaiter().GetResult();

        Assert.False(vm.IsRefreshingInBackground);
    }
}

internal sealed class FakeScanner : ModelFilesystemScanner
{
    public IReadOnlyList<DownloadedModel> Entries { get; set; } = Array.Empty<DownloadedModel>();
    public Exception? Throw { get; set; }

    public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
    {
        if (Throw is not null) throw Throw;
        return Entries;
    }
}
