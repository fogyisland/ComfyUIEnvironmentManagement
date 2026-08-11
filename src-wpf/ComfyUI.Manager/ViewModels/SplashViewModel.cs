using System;
using System.Collections.Generic;
using System.Windows.Threading;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.8 Splash 画面 view model — 持有标题 / 副标语 / 版本号,管理最少显示时间
/// 计时器,触发 IsFading 让 Window 跑 Storyboard 渐变。
///
/// TimerFactory seam(G13):生产路径默认 null,首次 NotifyMainWindowReady() lazy-new
/// 一个 DispatcherTimer;测试路径 set fake 直接同步触发 Tick。
/// </summary>
public class SplashViewModel : ViewModelBase
{
    public const double MinDisplaySeconds = 3.0;
    public const double FadeOutSeconds = 0.8;

    private readonly DateTime _shownAt = DateTime.UtcNow;
    private IDisposable? _timerHandle;
    private bool _disposed;

    public SplashViewModel(string title, string tagline, string version)
    {
        Title = title;
        Tagline = tagline;
        Version = version;
    }

    public string Title { get; }
    public string Tagline { get; }
    public string Version { get; }
    public bool IsFading { get; private set; }

    /// <summary>
    /// 测试 seam(G13):Action=Timer tick callback,TimeSpan=interval,返回 IDisposable
    /// 调 Dispose 停 timer。生产路径默认 null → lazy-new 一个 DispatcherTimer。
    /// </summary>
    internal Func<Action, TimeSpan, IDisposable>? TimerFactory { get; set; }

    /// <summary>
    /// App 在 MainWindow.Show() 后调这个。启动 DispatcherTimer 每 100ms 检查
    /// elapsed,elapsed ≥ MinDisplaySeconds(3s) 才允许 fade。
    /// </summary>
    public void NotifyMainWindowReady()
    {
        if (_disposed) return;

        Action onTick = () =>
        {
            var elapsed = DateTime.UtcNow - _shownAt;
            if (elapsed.TotalSeconds >= MinDisplaySeconds)
            {
                _timerHandle?.Dispose();
                StartFadeOut();
            }
        };

        if (TimerFactory is not null)
        {
            _timerHandle = TimerFactory(onTick, TimeSpan.FromMilliseconds(100));
        }
        else
        {
            var dispatcherTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            dispatcherTimer.Tick += (_, _) => onTick();
            dispatcherTimer.Start();
            _timerHandle = new DispatcherTimerHandle(dispatcherTimer);
        }
    }

    /// <summary>
    /// 内部逻辑:把 IsFading 设为 true。Window PropertyChanged 监听触发 Storyboard。
    /// Storyboard.Completed → Window.OnFadeOutCompleted → RaiseFadeCompleted() → Close。
    /// </summary>
    internal void StartFadeOut()
    {
        if (IsFading) return;
        IsFading = true;
        RaisePropertyChanged(nameof(IsFading));
    }

    /// <summary>
    /// 幂等闭锁(G5+G14 防双 invoke):Storyboard 完成 → Window.Close → Window.Closed
    /// 事件 handler 也会 raise,所以两次调只 fire 一次 FadeCompleted。
    /// </summary>
    internal void RaiseFadeCompleted()
    {
        if (_disposed) return;
        _disposed = true;
        _timerHandle?.Dispose();
        FadeCompleted?.Invoke();
    }

    public event Action? FadeCompleted;

    /// <summary>
    /// v0.6.11+ dashboard/splash polish:4 阶段进度 (Init → LoadDatabase → LoadTheme → Ready)。
    /// StageRowsPercent[i] 是 stage i 的当前 percent(0-100);UI 绑 ProgressBar.Value。
    /// StagePercent 是加权 sum(0-100),绑底部 status text。
    /// App.xaml.cs 调 <see cref="ReportStageProgress"/> 推进。
    /// </summary>
    /// <remarks>
    /// backing field 是 int[],暴露成 IReadOnlyList&lt;int&gt; 只是防外部改;
    /// 内部推进走 _stageRows 而不是把 IReadOnlyList 再 cast 回 int[]。
    /// WPF 的 {Binding StageRowsPercent[0]} 对 IReadOnlyList 索引器可用。
    /// </remarks>
    private readonly int[] _stageRows = new int[4];

    public IReadOnlyList<int> StageRowsPercent => _stageRows;

    public int StagePercent { get; private set; }

    public event Action? StageProgressChanged;

    private readonly MultiStageSplashProgress _progress = new();

    /// <summary>
    /// App.xaml.cs 的 seam:某个启动阶段推进到 stagePercent(通常直接 100)。
    /// _disposed 后(splash 已 fade 完 / 已 Close)静默 no-op,避免启动尾段
    /// 的 late report 打到已关窗口的 binding 上。
    /// </summary>
    public void ReportStageProgress(Stage stage, int stagePercent)
    {
        if (_disposed) return;

        _progress.Report(stage, stagePercent);
        var idx = (int)stage;
        if (idx >= 0 && idx < _stageRows.Length)
        {
            _stageRows[idx] = Math.Clamp(stagePercent, 0, 100);
        }
        StagePercent = _progress.TotalPercent;
        RaisePropertyChanged(nameof(StageRowsPercent));
        RaisePropertyChanged(nameof(StagePercent));
        StageProgressChanged?.Invoke();
    }

    /// <summary>DispatcherTimer 包装成 IDisposable 让 seam 接口统一。</summary>
    private sealed class DispatcherTimerHandle : IDisposable
    {
        private readonly DispatcherTimer _timer;
        public DispatcherTimerHandle(DispatcherTimer timer) => _timer = timer;
        public void Dispose() => _timer.Stop();
    }
}