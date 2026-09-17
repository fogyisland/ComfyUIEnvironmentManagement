using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views.LocalModels;
using Moq;
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

    // -------- v1.0.0 T-D5:streaming scanner tests --------

    /// <summary>v1.0.0 T-D5:streaming scanner — Phase 1 emit 期间 IsBusy=true,VM 立即 RebuildCardsAndChips
    /// 让用户秒级看到卡(不再等 hash+match 流水线结束)。验证:scan 阻塞期间(Scan() 还没 return),
    /// ctx.ModelUpdated callback 已经把 entry 推进 FilteredModels。
    /// 这是用户反馈 "本地模型一直出在加载中,感觉是不是数据还是没进数据库" 的修复核心:
    /// 之前 scanner 把整段 hash+match 串行化,Phase 1 跟 Phase 2 一起出卡;现在 Phase 1 一完成(秒级)
    /// 就出卡,Phase 2 在背后渐进更新。</summary>
    [Fact]
    public async Task ReloadAsync_StreamsEntriesToCardsBeforeScanCompletes()
    {
        // 自定义 scanner:Phase 1 (ScanCore 模拟) 立即 emit 1 条;Phase 2 (hash match 模拟) emit 1 条 update;
        // 然后 Sleep 让 VM 有时间消费 callback,再 return final list(模拟 await Task.Run 还没完成)。
        var streamed = new List<DownloadedModel>();
        var phase2Gate = new ManualResetEventSlim(false);
        var streaming = new StreamingFakeScanner(
            phase1Entries: new List<DownloadedModel>
            {
                new() { Title = "streamed1", Kind = ModelKind.LORA, Source = "Local",
                        SourceId = "streamed:1", SourceVersionId = "v1", DownloadedAt = DateTime.Now },
            },
            phase2Entry: new DownloadedModel
            {
                Title = "streamed1", Kind = ModelKind.LORA, Source = "Local",
                SourceId = "streamed:1", SourceVersionId = "v1", DownloadedAt = DateTime.Now,
                Hash = "CAFEBABE",
            },
            phase2Gate: phase2Gate);

        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), streaming);
        var task = vm.ReloadAsync();

        // 等 Phase 1 emit 触发 OnModelStreamed → RebuildCardsAndChips → FilteredModels 填好 1 卡
        // (但 scan 还没 return,所以 IsBusy 仍 true)。这是 UX 关键:用户立刻看到 1 张卡,而不是空白。
        SpinWait.SpinUntil(() => vm.FilteredModels.Count == 1, TimeSpan.FromSeconds(1));

        Assert.True(vm.IsBusy, "scan still in flight");
        Assert.Single(vm.FilteredModels);
        Assert.Equal("streamed1", vm.FilteredModels[0].Title);
        Assert.Null(vm.FilteredModels[0].Hash);   // Phase 1 raw — hash 还没 match

        // 释放 Phase 2 emit(gate 让 scanner emit + return final)
        phase2Gate.Set();
        await task;

        // Phase 2 emit 应该就地更新 card.Hash(通过 WithMatchStatus + IndexOf 替换)
        Assert.False(vm.IsBusy);
        Assert.Single(vm.FilteredModels);
        Assert.Equal("CAFEBABE", vm.FilteredModels[0].Hash);
    }

    /// <summary>v1.0.0 T-D5:test seam — 自定义 scanner 控制 Phase 1 emit 时机(立即)+ Phase 2 emit 时机(等 gate)。
    /// 真实 scanner 是 ScanCore() 同步返回所有 raw → emit,然后 HashAndMatch 逐条 match emit。
    /// 这里 gate 让 test 能在 Phase 1 emit 跟 Phase 2 emit 之间插入 assertion。</summary>
    private sealed class StreamingFakeScanner : ModelFilesystemScanner
    {
        private readonly IReadOnlyList<DownloadedModel> _phase1Entries;
        private readonly DownloadedModel _phase2Entry;
        private readonly ManualResetEventSlim _phase2Gate;

        public StreamingFakeScanner(
            IReadOnlyList<DownloadedModel> phase1Entries,
            DownloadedModel phase2Entry,
            ManualResetEventSlim phase2Gate)
        {
            _phase1Entries = phase1Entries;
            _phase2Entry = phase2Entry;
            _phase2Gate = phase2Gate;
        }

        public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
        {
            // Phase 1:模拟 ScanCore 立即返回所有 raw entries,通过 ModelUpdated emit
            foreach (var e in _phase1Entries) ctx?.ModelUpdated?.Report(e);

            // Phase 2:等 gate 再 emit(hash match 完成的模拟)+ return final list
            _phase2Gate.Wait();
            ctx?.ModelUpdated?.Report(_phase2Entry);
            return _phase1Entries
                .Select(e => e.SourceId == _phase2Entry.SourceId ? _phase2Entry : e)
                .ToList();
        }
    }

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
            // v1.0.0 T-D5:扫描"完成"时再通过 ModelUpdated 推一条更新版 entry(hash match 模拟)。
            // 验证 streaming 路径在 scan await 期间 + 完成后都能正确推 card 到 FilteredModels。
            var matched = new DownloadedModel
            {
                Title = "m1", Kind = ModelKind.Checkpoint, Source = "Local",
                SourceId = "1", SourceVersionId = "v1", DownloadedAt = DateTime.Now,
                Hash = "DEADBEEF",
            };
            ctx?.ModelUpdated?.Report(matched);
            return new List<DownloadedModel> { matched };
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

    // --- v1.0.0.x: 默认不自动刷新 ---
    // 用户反馈:"本地模型默认情况刷新操作不自动启动,只有手动启动才去进行刷新操作"。
    // VM 构造完未调 ReloadAsync 时,EmptyMessage 应为 placeholder 提示用户点「🔄 刷新」按钮,
    // IsBusy=false 且 ShowLoadingOverlay/IsRefreshingInBackground 都 false(无任何 spinner)。
    // Test 不调 ReloadAsync → VM 应保持"未扫描"状态,等用户手动触发。

    [Fact]
    public void Constructor_NoReloadCalled_EmptyMessageIsPlaceholder()
    {
        var vm = new LocalModelsViewModel(SettingsWith(@"Z:\fake"), new FakeScanner());

        Assert.False(vm.IsBusy);
        Assert.False(vm.ShowLoadingOverlay);
        Assert.False(vm.IsRefreshingInBackground);
        Assert.Equal("点击「🔄 刷新」加载本地模型", vm.EmptyMessage);
        Assert.Empty(vm.FilteredModels);
    }

    [Fact]
    public void Constructor_NoReloadCalled_ReloadCommandCanExecute()
    {
        // 默认状态 — 「🔄 刷新」按钮可点(canExecute !IsBusy)
        var vm = new LocalModelsViewModel(SettingsWith(@"Z:\fake"), new FakeScanner());

        Assert.True(vm.ReloadCommand.CanExecute(null));
    }

    // -------- v1.0.0.x (2026-09-17) T47:ToggleStar 7 命令 wire + Star 持久化 tests --------

    /// <summary>构造单 card VM,注入 LocalModelOperations(Star repo 走 real TestDb)。
    /// LocalModelOperations 走 no-op hash/match Func,Toast 占位 debug 输出,Progress Moq 跳过。
    /// 重要:repo shared TestDb 走 ModelFactory(TestDb 析构会清表,不影响并行测试隔离)。
    /// </summary>
    private static LocalModelsViewModel CreateVmWithOneCard(
        TestDb db,
        string sourcePath = @"D:\star_test_card.safetensors",
        string sourceId = "star-test:1")
    {
        var stars = new StarredModelsRepository(db.ModelFactory);
        var toast = new ToastNotification();
        var progress = new Mock<IProgressDialogService>();
        progress.Setup(p => p.ShowIndeterminate(It.IsAny<string>()))
                .Returns(new NoopDisposableForTest());
        Func<string, CancellationToken, string> hashFunc = (_, _) => "MOCK_HASH";
        Func<DownloadedModel, CancellationToken, Task<MatchResult?>> matchFunc =
            (_, _) => Task.FromResult<MatchResult?>(null);
        var ops = new LocalModelOperations(toast, stars, hashFunc, matchFunc, progress.Object);

        var fake = new FakeScanner
        {
            Entries = new List<DownloadedModel>
            {
                new()
                {
                    Title = "star-test",
                    Kind = ModelKind.Checkpoint,
                    Source = "Local",
                    SourceId = sourceId,
                    SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now,
                    FullPath = sourcePath,
                },
            }
        };
        // v1.0.0.x T47:LocalModelOperations 跟 StarredModelsRepository 是新增 ctor 参数。
        return new LocalModelsViewModel(
            SettingsWith(@"Z:\fake"), fake, logger: null,
            ops: ops, stars: stars);
    }

    [Fact]
    public void ToggleStarCommand_UpdatesCardInPlace()
    {
        using var db = new TestDb();
        var vm = CreateVmWithOneCard(db);
        vm.ReloadAsync().GetAwaiter().GetResult();

        var card = vm.FilteredModels[0];
        Assert.False(card.IsStarred);
        Assert.False(db.ModelFactory is null);  // db open sanity
        var starsRepo = new StarredModelsRepository(db.ModelFactory);
        Assert.False(starsRepo.IsStarred(card.SourcePath));

        // act:调 ToggleStarCommand
        vm.ToggleStarCommand.Execute(card);

        // assert:FilteredModels[0] 实例被替换,IsStarred=true,DB 有 star
        var updated = vm.FilteredModels[0];
        Assert.True(updated.IsStarred);
        Assert.True(starsRepo.IsStarred(updated.SourcePath));
        Assert.Equal(card.SourcePath, updated.SourcePath);
        Assert.Equal(card.Title, updated.Title);  // 其他字段不变
    }

    [Fact]
    public void ToggleStarCommand_PersistsAcrossReload()
    {
        using var db = new TestDb();
        var vm1 = CreateVmWithOneCard(db);
        vm1.ReloadAsync().GetAwaiter().GetResult();
        vm1.ToggleStarCommand.Execute(vm1.FilteredModels[0]);

        // 新 VM 共享同一 TestDb → 走 LoadFromDb 应读回 IsStarred=true
        var vm2 = CreateVmWithOneCard(db);
        // vm2 没 ReloadAsync,需要手动 LoadFromDb(VM ctor 后不自动 reload)
        // 但 vm2 没注入 _localModelFilesRepo,LoadFromDb() 返回 false。
        // 替代:走 ReloadAsync 让 scanner 出 1 card,VM 内部 GroupToCards 注入 IsStarred
        // (前提:LoadFromDbAsync 路径也注入 IsStarred — T47 task 关键路径)。
        vm2.ReloadAsync().GetAwaiter().GetResult();

        Assert.True(vm2.FilteredModels[0].IsStarred);
    }

    private sealed class NoopDisposableForTest : IDisposable { public void Dispose() { } }

    // -------- v1.0.0.x (2026-09-17) T47:T6 ViewMode + StarsOnlyFilter 持久化 tests --------

    /// <summary>helper — 创建 VM,注入 LocalModelSettingsRepository(走 TestDb.ModelFactory),
    /// 但不 _stops/_stars/ops 让 ctor 不抛,只校验 settings 持久化路径。</summary>
    private static (LocalModelsViewModel vm, LocalModelSettingsRepository settings)
        CreateVmWithSettings(TestDb db)
    {
        var settings = new LocalModelSettingsRepository(db.ModelFactory);
        var vm = new LocalModelsViewModel(
            SettingsWith(@"Z:\fake"), new FakeScanner(),
            ops: null, stars: null, localModelsSettings: settings)
        {
            // SearchText setter 1ms 防抖 — 测试用即时 fake timer 模拟"1ms 到时"。
            // 同 LocalModelsViewModelSearchTests.FakeImmediateTimer pattern。
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };
        return (vm, settings);
    }

    /// <summary>helper — 构造 5 张 DownloadedModel 给 scanner 当 seed(stars repo
    /// 单独 Upsert idx 0 + idx 2)。</summary>
    private static List<DownloadedModel> MakeStarredCards(int total)
    {
        var list = new List<DownloadedModel>();
        for (int i = 0; i < total; i++)
        {
            list.Add(new DownloadedModel
            {
                Title = $"m{i}",
                Kind = ModelKind.Checkpoint,
                Source = "Local",
                SourceId = $"star-task6:{i}",
                SourceVersionId = "v1",
                DownloadedAt = DateTime.Now.AddMinutes(-i),
                FullPath = $@"D:\star_task6_{i}.safetensors",
            });
        }
        return list;
    }

    /// <summary>helper — 构造 mixed 卡片用于 3 维交集测试:
    /// 4 cards,1 个同时满足 starred + "anime" searchable + LORA(idx 0)。</summary>
    private static (LocalModelsViewModel vm, LocalModelSettingsRepository settings)
        CreateVmWithMixedCardsForIntersect(TestDb db)
    {
        var entries = new List<DownloadedModel>
        {
            // idx 0 — starred + "anime" + LORA(满足全部 3 维)
            new() { Title = "anime-lora", Kind = ModelKind.LORA, Source = "Local",
                    SourceId = "x:0", SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now, FullPath = @"D:\x0.safetensors" },
            // idx 1 — starred but not anime, LORA
            new() { Title = "other-lora", Kind = ModelKind.LORA, Source = "Local",
                    SourceId = "x:1", SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now.AddMinutes(-1), FullPath = @"D:\x1.safetensors" },
            // idx 2 — anime but not starred, Checkpoint
            new() { Title = "anime-cp", Kind = ModelKind.Checkpoint, Source = "Local",
                    SourceId = "x:2", SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now.AddMinutes(-2), FullPath = @"D:\x2.safetensors" },
            // idx 3 — LORA but not anime, not starred
            new() { Title = "no-match", Kind = ModelKind.LORA, Source = "Local",
                    SourceId = "x:3", SourceVersionId = "v1",
                    DownloadedAt = DateTime.Now.AddMinutes(-3), FullPath = @"D:\x3.safetensors" },
        };
        var settings = new LocalModelSettingsRepository(db.ModelFactory);
        var stars = new StarredModelsRepository(db.ModelFactory);
        stars.Add(@"D:\x0.safetensors");
        stars.Add(@"D:\x1.safetensors");

        var fake = new FakeScanner { Entries = entries };
        var vm = new LocalModelsViewModel(
            SettingsWith(@"Z:\fake"), fake,
            ops: null, stars: stars, localModelsSettings: settings)
        {
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };
        vm.ReloadAsync().GetAwaiter().GetResult();
        return (vm, settings);
    }

    /// <summary>FakeImmediateTimer — 测试用,模拟 1ms DispatcherTimer 防抖 Tick。
    /// 跟 <see cref="LocalModelsViewModelSearchTests.FakeImmediateTimer"/> 同 pattern,
    /// 这里复制一份(同 namespace 已 internal 化,但跨 test class 用 internal 不便)。</summary>
    private sealed class FakeImmediateTimer : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;
        public FakeImmediateTimer(Action action)
        {
            _action = action;
            _action();
        }
        public void Dispose() => _disposed = true;
    }

    [Fact]
    public void ViewMode_DefaultsToCards()
    {
        // arrange:全新 VM,settings repo 空(no persistence record)
        using var db = new TestDb();
        var (vm, _) = CreateVmWithSettings(db);

        // assert
        Assert.Equal(ViewMode.Cards, vm.ActiveViewMode);
    }

    [Fact]
    public void ViewMode_SetToList_FiresPropertyChanged()
    {
        using var db = new TestDb();
        var (vm, _) = CreateVmWithSettings(db);
        var fired = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.ActiveViewMode)) fired = true;
        };

        vm.ActiveViewMode = ViewMode.List;

        Assert.True(fired);
        Assert.Equal(ViewMode.List, vm.ActiveViewMode);
    }

    [Fact]
    public void ViewMode_Changed_PersistsToSettingsRepo()
    {
        using var db = new TestDb();
        var (vm, settings) = CreateVmWithSettings(db);

        vm.ActiveViewMode = ViewMode.Thumbnails;

        Assert.Equal("Thumbnails", settings.Get("localmodels.view_mode"));
    }

    [Fact]
    public void ViewMode_OnStartup_LoadsFromSettingsRepo()
    {
        using var db = new TestDb();
        var settings = new LocalModelSettingsRepository(db.ModelFactory);
        settings.Set("localmodels.view_mode", "List");

        var vm = new LocalModelsViewModel(
            SettingsWith(@"Z:\fake"), new FakeScanner(),
            ops: null, stars: null, localModelsSettings: settings)
        {
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };

        Assert.Equal(ViewMode.List, vm.ActiveViewMode);
    }

    [Fact]
    public void StarsOnly_ToggledOn_FiltersListToStarred()
    {
        // arrange:5 cards,2 starred(用 FakeScanner 出 5 条,stars repo pre-mark 2 条)
        using var db = new TestDb();
        var entries = MakeStarredCards(5);
        var settings = new LocalModelSettingsRepository(db.ModelFactory);
        var stars = new StarredModelsRepository(db.ModelFactory);
        stars.Add(@"D:\star_task6_0.safetensors");
        stars.Add(@"D:\star_task6_2.safetensors");

        var fake = new FakeScanner { Entries = entries };
        var vm = new LocalModelsViewModel(
            SettingsWith(@"Z:\fake"), fake,
            ops: null, stars: stars, localModelsSettings: settings)
        {
            FilterTimerFactory = (action, _) => new FakeImmediateTimer(action),
        };
        vm.ReloadAsync().GetAwaiter().GetResult();

        // 先 assert:5 卡片都加载,2 张 starred
        Assert.Equal(5, vm.FilteredModels.Count);
        Assert.Equal(2, vm.FilteredModels.Count(c => c.IsStarred));

        // act:开启 StarsOnly
        vm.StarsOnlyFilter = true;

        // assert:只剩 2 张 starred
        Assert.Equal(2, vm.FilteredModels.Count);
        Assert.All(vm.FilteredModels, c => Assert.True(c.IsStarred));
    }

    [Fact]
    public void SearchText_And_StarsOnly_And_Kind_Intersect()
    {
        using var db = new TestDb();
        var (vm, _) = CreateVmWithMixedCardsForIntersect(db);

        // 默认 activeChip = "全部" — 不动 kind,先把 StarsOnly 开启,验证只剩 2 starred
        vm.StarsOnlyFilter = true;
        Assert.Equal(2, vm.FilteredModels.Count);

        // 再加 Search 过滤 "anime" — 只剩 idx 0(同时 starred + anime)
        vm.SearchText = "anime";
        Assert.Single(vm.FilteredModels);

        // 再加 KindFilter = LORA(idx 0 也是 LORA)— 还是 Single
        vm.ActiveChip = vm.KindChips.Single(c => c.Kind == ModelKind.LORA);
        Assert.Single(vm.FilteredModels);
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
