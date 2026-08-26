using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0:Console panel 行为测试 — 用户反馈 "本地模型点击后是否能提供日志看下何时完成
/// civital 信息查询"。验证三件事:
/// ① ConsoleLog 在 ReloadAsync 期间接收 scanner [hash]/[match]/[preview] 行
/// (链路 VM._consoleSink → ObservableCollection<string>)
/// ② IsConsoleVisible 三态可见性:!_userHiddenConsole && (IsBusy || Count > 0)
/// ③ 用户点 ✕ 调 ClearConsoleLog 后 IsConsoleVisible = false,即使 IsBusy 也藏;
/// 下次 Reload 重置 _userHiddenConsole 自动重新打开。
/// </summary>
public sealed class LocalModelsViewModelConsoleTests : IDisposable
{
    private readonly SynchronizationContext? _originalCtx;

    public LocalModelsViewModelConsoleTests()
    {
        // v1.0.0.x #571 flaky fix:xUnit runner 线程无 SyncContext →
        // Progress<T> ctor 捕获 null → Report 走 ThreadPool 异步执行。
        // Scan 完成后 await vm.ReloadAsync 返回时,_consoleSink / ctxProgress 包装的
        // Progress<T> 回调可能仍在 ThreadPool 队列里未跑,断言 vm.ConsoleLog.Count /
        // outerReceived.Count 时偶发为 0(并发跑其他 test class 抢占 threadpool 时 100% 出现)。
        // 装个 inline SyncContext:Post 同步执行回调,VM ctor / ReloadAsync 里所有
        // Progress<string>.ctor 捕获它 → Report 后回调 inline,断言时一定到齐。
        _originalCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(_originalCtx);
    }

    /// <summary>Post 同步执行 — 等同于 InlineDispatcher。VM 内部 Progress<T> 捕获它之后
    /// Report 立刻触发 ConsoleLog.Add 等回调,移除 threadpool 调度延迟。</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static Settings SettingsWith(string modelsDir) => new() { DefaultModelsDirectory = modelsDir };

    /// <summary>Synchronous IProgress&lt;T&gt; wrapper — 调用方传入的 outer progress 走它,
    /// 保证 outer.Report 同步 add 到 outerReceived(跟 _consoleSink 是否走 SyncContext 无关,
    /// 这条链路是单 hop 的 IProgress.Report,没 Progress&lt;T&gt; 包装)。</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public SyncProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }

    /// <summary>Test-only scanner:模拟 scanner 在 Scan() 内部 emit progress 行(代替真实 hash/match 流水线)。
    /// 用户期望的 "[hash]/[match]/[preview]" 行 — 链路验证 = ctx.Progress.Report 能否进 ConsoleLog。</summary>
    private sealed class ProgressEmittingScanner : ModelFilesystemScanner
    {
        public List<string> Emitted = new();
        public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
        {
            ctx?.Progress?.Report("[hash] cache hit: foo.safetensors");
            Emitted.Add("hit");
            ctx?.Progress?.Report("[match] 1/1 foo bar → CivitAi/Hash");
            Emitted.Add("matched");
            ctx?.Progress?.Report("[preview] saved: foo.png");
            Emitted.Add("preview");
            return new List<DownloadedModel>
            {
                new()
                {
                    Title = "foo", Kind = ModelKind.LORA, Source = "Local",
                    SourceId = "local:foo", SourceVersionId = "v1", DownloadedAt = DateTime.Now,
                },
            };
        }
    }

    // ===== ConsoleLog 基础行为 =====

    [Fact]
    public void ConsoleLog_InitialState_IsEmpty()
    {
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner());
        Assert.Empty(vm.ConsoleLog);
    }

    [Fact]
    public void IsConsoleVisible_InitialState_IsFalse()
    {
        // 新 VM:无 IsBusy,无 ConsoleLog 行 → IsConsoleVisible = false(panel 不显示)
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner());
        Assert.False(vm.IsConsoleVisible);
    }

    // ===== Scanner → ConsoleLog 转发链路 =====

    [Fact]
    public async Task ReloadAsync_ScannerProgress_AppendsToConsoleLog()
    {
        // 链路验证:scanner 通过 ctx.Progress.Report 推 3 行 → ConsoleLog 应该按序收到
        // (绕过真实 hash+match 流水线,用 ProgressEmittingScanner 直接 emit)。
        // ctx.Progress 不为 null 要求 _hashCache + _orchestrator 都注入 — 注入真 CivitaiHashCache
        // (tmp SQLite) + 空 matcher list 的 Orchestrator,让 ctor 接受非 null,scanner 实际不调它们。
        var scan = new ProgressEmittingScanner();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"),
            scan,
            logger: null,
            lookup: null,
            hashCache: cache,
            orchestrator: CreateOrchestratorWithoutMatchers());

        await vm.ReloadAsync();

        Assert.Equal(3, vm.ConsoleLog.Count);
        Assert.Equal("[hash] cache hit: foo.safetensors", vm.ConsoleLog[0]);
        Assert.Equal("[match] 1/1 foo bar → CivitAi/Hash", vm.ConsoleLog[1]);
        Assert.Equal("[preview] saved: foo.png", vm.ConsoleLog[2]);
    }

    [Fact]
    public async Task ReloadAsync_CallerProgress_AlsoReceivesLines()
    {
        // 链式转发:VM 内部 _consoleSink + 调用方传入的 progress 都应收到 scanner 行。
        // (MainVM 拿 progress 是为了写自己的 logger — 必须保留这个行为。)
        // SyncProgress 而非 Progress<T>:Progress<T> 在 xUnit runner 线程构造 → SyncContext=null
        // → ThreadPool 异步执行,assert 时未处理完。用 SyncProgress 让 outer.Report 同步 add。
        var scan = new ProgressEmittingScanner();
        var outerReceived = new List<string>();
        var outer = new SyncProgress<string>(line => outerReceived.Add(line));

        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"),
            scan,
            hashCache: cache,
            orchestrator: CreateOrchestratorWithoutMatchers());

        await vm.ReloadAsync(outer);

        Assert.Equal(3, vm.ConsoleLog.Count);        // 内部 _consoleSink → ConsoleLog
        Assert.Equal(3, outerReceived.Count);        // 调用方 outer 也收到 3 行
        Assert.Equal(vm.ConsoleLog[0], outerReceived[0]);
        Assert.Equal(vm.ConsoleLog[1], outerReceived[1]);
        Assert.Equal(vm.ConsoleLog[2], outerReceived[2]);
    }

    // ===== 三态可见性 =====

    [Fact]
    public void IsConsoleVisible_TrueDuringReload()
    {
        // ReloadAsync 期间 IsBusy=true → IsConsoleVisible = true(panel 显示,即使 log 暂空)。
        var slow = new SlowConsoleScanner();
        slow.CloseGate();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"), slow,
            hashCache: cache, orchestrator: CreateOrchestratorWithoutMatchers());

        var task = vm.ReloadAsync();
        SpinWait.SpinUntil(() => slow.ScanCallCount == 1, TimeSpan.FromSeconds(1));

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsConsoleVisible);

        slow.ReleaseGate();
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void IsConsoleVisible_TrueAfterReloadWhenConsoleNonEmpty()
    {
        // scan 完仍 IsConsoleVisible=true:log 非空 + !userHidden(默认) → 保留 panel
        // 让用户看到完整日志(下一步手动点 ✕ 才隐藏)。
        var scan = new ProgressEmittingScanner();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"), scan,
            hashCache: cache, orchestrator: CreateOrchestratorWithoutMatchers());

        vm.ReloadAsync().GetAwaiter().GetResult();

        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.ConsoleLog);
        Assert.True(vm.IsConsoleVisible);
    }

    // ===== 用户点 ✕ 行为 =====

    [Fact]
    public void ClearConsoleLog_HidesPanelEvenDuringBusy()
    {
        // 用户点 ✕:即使 IsBusy=true,IsConsoleVisible 立即变 false(用户意图优先)。
        var slow = new SlowConsoleScanner();
        slow.CloseGate();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"), slow,
            hashCache: cache, orchestrator: CreateOrchestratorWithoutMatchers());

        var task = vm.ReloadAsync();
        SpinWait.SpinUntil(() => slow.ScanCallCount == 1, TimeSpan.FromSeconds(1));

        Assert.True(vm.IsConsoleVisible);
        slow.ReleaseGate();   // 让 scanner emit 几行再 clear,模拟用户在运行中关 console
        Thread.Sleep(20);

        vm.ClearConsoleLog();

        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);   // ✕ → 隐藏

        // 清完后,如果 scanner 后续 emit 新行,ConsoleLog 又会增长 → _userHiddenConsole 仍是 true,
        // IsConsoleVisible 仍 false(用户主动隐藏意图直到下次 Reload 才解除)。这是 user intent priority。
        // 验证:本测试不依赖这个细节,只确认 clear 立刻隐藏。
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void ReloadAsync_ResetsUserHidden_ConsoleReappears()
    {
        // v1.0.0 Console panel:重置 _userHiddenConsole 让 ✕ 关闭意图在下一次 Reload 解除。
        // 用户流程:① reload → console 出现 → ② 用户点 ✕ 关闭 → ③ 再次 reload → console 重新出现
        // 验证 _userHiddenConsole 在 ReloadAsync 开始时被复位。
        var scan = new ProgressEmittingScanner();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"), scan,
            hashCache: cache, orchestrator: CreateOrchestratorWithoutMatchers());

        // 首次 reload → console 出现 → 用户点 ✕ 关闭
        vm.ReloadAsync().GetAwaiter().GetResult();
        vm.ClearConsoleLog();
        Assert.False(vm.IsConsoleVisible);

        // 第二次 reload → _userHiddenConsole 复位 → 重新可见 + log 重填
        vm.ReloadAsync().GetAwaiter().GetResult();
        Assert.True(vm.IsConsoleVisible);
        Assert.Equal(3, vm.ConsoleLog.Count);   // 新一轮 scanner 行
    }

    [Fact]
    public void ReloadAsync_ClearsConsoleLogOnStart()
    {
        // 第二次 reload 清掉旧 log(避免 stale 行跟新行混在一起)
        var scan = new ProgressEmittingScanner();
        using var cache = CreateTempHashCache();
        var vm = new LocalModelsViewModel(
            SettingsWith("Z:\\fake"), scan,
            hashCache: cache, orchestrator: CreateOrchestratorWithoutMatchers());

        vm.ReloadAsync().GetAwaiter().GetResult();
        Assert.Equal(3, vm.ConsoleLog.Count);

        vm.ReloadAsync().GetAwaiter().GetResult();
        Assert.Equal(3, vm.ConsoleLog.Count);   // 不是 6,是新一轮的 3 行
    }

    // ===== Scanner test fakes =====

    /// <summary>Slow 版的 ProgressEmittingScanner — Scan() 阻塞在 gate 上,让 test 能插 assertion 在 IsBusy=true 期间。
    /// 释放 gate 后 emit progress 行再 return final list。</summary>
    private sealed class SlowConsoleScanner : ModelFilesystemScanner
    {
        private readonly ManualResetEventSlim _gate = new(true);
        public int ScanCallCount;

        public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
        {
            ScanCallCount++;
            _gate.Wait();
            ctx?.Progress?.Report("[hash] computed: x → aabbccdd…");
            ctx?.Progress?.Report("[match] 1/1 x → CivitAi/CompanionJson");
            return new List<DownloadedModel>
            {
                new()
                {
                    Title = "x", Kind = ModelKind.Checkpoint, Source = "Local",
                    SourceId = "local:x", SourceVersionId = "v1", DownloadedAt = DateTime.Now,
                },
            };
        }

        public void CloseGate() => _gate.Reset();
        public void ReleaseGate() => _gate.Set();
    }

    /// <summary>Real CivitaiHashCache with tmp SQLite path — hash cache is sealed,can't fake.
    /// Test scanner 不调 cache.Lookup/Store,只是注入让 LocalModelsViewModel ctor 接受非 null 参数
    /// → ctx.Progress 字段被启用 → scanner 可以 emit progress 行。打开 tmp SQLite 是廉价的。</summary>
    private static CivitaiHashCache CreateTempHashCache()
    {
        var tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-models-console-test-{Guid.NewGuid():N}.sqlite");
        return new CivitaiHashCache(tmpPath);
    }

    /// <summary>Real CivitaiMatcherOrchestrator with empty matcher list — Orchestrator 是 sealed。
    /// Empty matcher list 让 MatchAsync 立即返回 null (no match);test scanner 不调它,
    /// 只是注入让 LocalModelsViewModel ctor 接受非 null。</summary>
    private static CivitaiMatcherOrchestrator CreateOrchestratorWithoutMatchers()
    {
        return new CivitaiMatcherOrchestrator(Array.Empty<IModelMatcher>());
    }
}