using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// Regression tests for v0.6.5.11 hotfix:
/// ProcessLauncher.AttachStdoutReader / AttachStderrReader 跑在 Task.Run 后台
/// 线程,LogLines ObservableCollection 必须被 WPF 在 UI 线程枚举,
/// 否则触发 "某个 itemscontrol 与它的项源不一致"。
///
/// 修法:在 EnvironmentListViewModel.StartEnvAsync 把 status 包成 Progress<string>,
/// 构造时捕获 UI 线程 SynchronizationContext,Report 回调自动 marshal。
/// 本测试用 TestSynchronizationContext 模拟 WPF DispatcherSynchronizationContext,
/// 不依赖真实 WPF dispatcher 也能验证修复。
/// </summary>
public sealed class EnvStartStatusViewModelDispatchTests : IDisposable
{
    private readonly SynchronizationContext? _originalCtx;
    private readonly TestSynchronizationContext _ctx;

    public EnvStartStatusViewModelDispatchTests()
    {
        _originalCtx = SynchronizationContext.Current;
        _ctx = new TestSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ctx);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(_originalCtx);
        _ctx.Dispose();
    }

    private sealed class TestSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback cb, object? state)> _queue = new();
        private int _pending;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            Interlocked.Increment(ref _pending);
        }

        public override SynchronizationContext CreateCopy() => this;

        public int PendingCount => _pending;

        public void Pump()
        {
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _pending);
                item.cb(item.state);
            }
        }

        public void Dispose() { }
    }

    [Fact]
    public void ProgressAdapter_FromBackgroundThread_MarshalsReportToCapturedContext()
    {
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        var status = new EnvStartStatusViewModel();
        var callbackThreadId = -1;

        // 在 UI(测试)线程构造 Progress<T> → 捕获 TestSyncCtx
        IProgress<string> stageProgress = new Progress<string>(s =>
        {
            callbackThreadId = Thread.CurrentThread.ManagedThreadId;
            status.Report(s);
        });

        // 模拟 ProcessLauncher 后台 stdout reader 线程
        var bgThreadId = -1;
        var bgDone = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bgThreadId = Thread.CurrentThread.ManagedThreadId;
            stageProgress.Report("stage:激活本地环境");
            bgDone.Set();
        });

        // 等 Post 落进 queue(最多 1s)
        var startWait = DateTime.UtcNow;
        while (bgThreadId == -1 || _ctx.PendingCount == 0)
        {
            if (DateTime.UtcNow - startWait > TimeSpan.FromSeconds(1)) break;
            Thread.Sleep(10);
        }

        // 后台线程确实跟 UI 线程不同(否则测试无效)
        Assert.NotEqual(uiThreadId, bgThreadId);
        Assert.Equal(1, _ctx.PendingCount);  // 1 个 Report 被 Post 进来
        Assert.Equal(-1, status.CurrentStageIndex);  // 还没 Pump,stage 没动

        // 模拟 dispatcher pump
        _ctx.Pump();

        Assert.Equal(0, status.CurrentStageIndex);
        Assert.Equal(uiThreadId, callbackThreadId);
        bgDone.Wait(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ProgressAdapter_FromBackgroundThread_LogProgressAppendsLogLines_OnUIThread()
    {
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        var status = new EnvStartStatusViewModel();
        var lineThreadIds = new System.Collections.Generic.List<int>();

        IProgress<string> logProgress = new Progress<string>(line =>
        {
            lineThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            status.Report(line);  // 直接传 non-stage 前缀 → Report → LogLines.Add
        });

        status.Begin();  // IsVisible=true,CurrentStageIndex=0

        var done = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            logProgress.Report("loading graph");
            logProgress.Report("loading custom nodes");
            logProgress.Report("server started on port 8188");
            done.Set();
        });

        // 等 3 个 Report 落进 queue(最多 1s)
        var startWait = DateTime.UtcNow;
        while (_ctx.PendingCount < 3)
        {
            if (DateTime.UtcNow - startWait > TimeSpan.FromSeconds(1)) break;
            Thread.Sleep(10);
        }

        Assert.Equal(3, _ctx.PendingCount);
        Assert.Empty(status.LogLines);  // 还没 Pump

        _ctx.Pump();

        Assert.Equal(3, status.LogLines.Count);
        Assert.Equal("loading graph", status.LogLines[0]);
        Assert.Equal("server started on port 8188", status.LogLines[2]);
        Assert.All(lineThreadIds, tid => Assert.Equal(uiThreadId, tid));
        Assert.Equal(0, _ctx.PendingCount);  // Pump 完 queue 清空
        done.Wait(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DirectCall_FromBackgroundThread_LogsAddedOnBackgroundThread()
    {
        // 反向证明:如果 env-list VM 没有 Progress<T> 包装,直接传 status 后台调
        // 会让 LogLines 在后台线程被改 — 测试环境无 WPF dispatcher 不会抛,
        // 但 WPF 生产环境会抛 ItemsControl source inconsistent。
        // 反例保留供对照,印证 Progress<T> 是必要修复。
        //
        // v1.0.0.x #571 flaky fix:原版 ThreadPool.QueueUserWorkItem + ManualResetEventSlim
        // 1 秒 timeout 在 ThreadPool 饱和时(并发跑其他 test class)超时,
        // 断言时 background thread 还没跑完,LogLines 为空。
        // 改 sync + Task.Run + GetAwaiter().GetResult — 关键:
        // ① 改 sync 方法不让 xUnit 调 await 测试方法(那样会捕获 ctor 装的
        //   TestSyncContext,test method 的 continuation 永远不 pump → 死锁);
        // ② Task.Run 替代 QueueUserWorkItem,ThreadPool 饱和也能拿到线程;
        // ③ GetAwaiter().GetResult 隐含等待,避免 timeout-based race。
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        var status = new EnvStartStatusViewModel();
        var bgAddThreadId = -1;
        Task.Run(() =>
        {
            bgAddThreadId = Thread.CurrentThread.ManagedThreadId;
            status.Report("hello");
        }).GetAwaiter().GetResult();
        Assert.Single(status.LogLines);
        Assert.Equal("hello", status.LogLines[0]);
        Assert.NotEqual(uiThreadId, bgAddThreadId);
    }
}
