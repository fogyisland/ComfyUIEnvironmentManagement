using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

public enum ErrorSeverity { Info, Warn, Error, Critical }

public record ErrorBannerEntry(
    string Code, string Message, ErrorSeverity Severity, DateTime At,
    TimeSpan? DismissAfter = null);

public class ErrorBannerViewModel : ViewModelBase
{
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Dictionary<ErrorBannerEntry, IDisposable> _scheduled = new();

    /// <summary>默认 TTL:Info 5s,Warn 8s,Error 12s。Critical=null(手动关)。</summary>
    public static TimeSpan? DefaultDismissAfter(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.Info => TimeSpan.FromSeconds(5),
        ErrorSeverity.Warn => TimeSpan.FromSeconds(8),
        ErrorSeverity.Error => TimeSpan.FromSeconds(12),
        ErrorSeverity.Critical => null,
        _ => TimeSpan.FromSeconds(5),
    };

    /// <summary>默认调度:DispatcherTimer 跑在 UI 线程。测试上下文(无 Dispatcher)no-op。</summary>
    public static readonly Func<TimeSpan, Action, IDisposable> DefaultSchedule =
        (delay, action) =>
        {
            // DispatcherTimer.Start() 在没有 Dispatcher 的线程(单元测试)上会抛
            // InvalidOperationException。检测一下,无 Dispatcher 就返回 no-op,
            // 避免打破现有 9 个 new ErrorBannerViewModel() 的测试。
            if (Dispatcher.FromThread(System.Threading.Thread.CurrentThread) is null)
            {
                return new NoopDisposable();
            }
            var timer = new DispatcherTimer { Interval = delay };
            EventHandler? handler = null;
            handler = (s, e) =>
            {
                timer.Tick -= handler;
                timer.Stop();
                action();
            };
            timer.Tick += handler;
            timer.Start();
            // DispatcherTimer 没实现 IDisposable,包一层让我们的 IDisposable 契约一致。
            return new TimerHandle(timer);
        };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>包 DispatcherTimer 让它满足 IDisposable(Stop + 切断 Tick handler)。</summary>
    private sealed class TimerHandle : IDisposable
    {
        private DispatcherTimer? _timer;
        public TimerHandle(DispatcherTimer timer) { _timer = timer; }
        public void Dispose()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer = null;
        }
    }

    public ObservableCollection<ErrorBannerEntry> Entries { get; } = new();

    /// <summary>v0.6.15.8:测试用 — 是否有 Error/Critical 级别条目。</summary>
    public bool HasErrors => Entries.Any(e =>
        e.Severity == ErrorSeverity.Error || e.Severity == ErrorSeverity.Critical);

    /// <summary>v0.6.16+:XAML Close 按钮绑这个,参数=ErrorBannerEntry。</summary>
    public RelayCommand DismissCommand { get; }

    public ErrorBannerViewModel()
        : this(DefaultSchedule)
    {
    }

    /// <summary>测试 seam:注入 scheduler 让测试同步验证 dismiss 逻辑。</summary>
    public ErrorBannerViewModel(Func<TimeSpan, Action, IDisposable> schedule)
    {
        _schedule = schedule;
        DismissCommand = new RelayCommand(p => Dismiss((ErrorBannerEntry)p!));
    }

    public void Add(string code, string message, ErrorSeverity severity)
        => Add(code, message, severity, DefaultDismissAfter(severity));

    public void Add(string code, string message, ErrorSeverity severity, TimeSpan? dismissAfter)
    {
        var entry = new ErrorBannerEntry(
            code, message, severity, DateTime.Now, dismissAfter);
        Entries.Insert(0, entry);
        // 限制最多 20 条
        while (Entries.Count > 20)
            Entries.RemoveAt(Entries.Count - 1);

        if (dismissAfter is { } ttl)
        {
            _scheduled[entry] = _schedule(ttl, () => Dismiss(entry));
        }
    }

    /// <summary>手动关单条。Close 按钮 / 自动 dismiss 都走这里。</summary>
    public void Dismiss(ErrorBannerEntry entry)
    {
        if (_scheduled.TryGetValue(entry, out var timer))
        {
            timer.Dispose();
            _scheduled.Remove(entry);
        }
        Entries.Remove(entry);
    }
}