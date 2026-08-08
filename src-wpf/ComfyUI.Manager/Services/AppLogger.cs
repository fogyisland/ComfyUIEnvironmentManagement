using System;
using System.Globalization;
using System.IO;

namespace ComfyUI.Manager.Services;

/// <summary>
/// AppLogger:集中日志 — 把所有 subsystem 的 INFO/ERROR metadata 写到
/// &lt;projectRoot&gt;/Logs/YYYY-MM-DD.log(本地日期切)。ComfyUI 进程 stdout/stderr
/// 仍走各自 <c>logs/&lt;env-id&gt;.log</c>(LogViewer 用),跟这个 AppLogger 不同用途。
///
/// 文件模式:append + FileShare.ReadWrite|Delete + AutoFlush=true。
/// 写入路径:单 lock + 单 StreamWriter;写入线程可以是任意 — Process.Start 的
/// stdout reader 也能直接 LogInfo,不需要 Progress&lt;T&gt; 包装(因为这个写入
/// 是 sync I/O,不会改 ObservableCollection)。
///
/// v0.6.5.13 SDD,用户原话:
///   "另外是否可以为当前的文件添加一个日志目录,所有的启动执行都在日志中,
///    本地文件夹命名为 Logs,每天形成一个文件"
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _logDir;
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private DateTime _currentDay;

    public AppLogger(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));
        _logDir = Path.Combine(projectRoot, "Logs");
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// 读今天的日志行(测试用 + 调试用)。开新 FileStream 用 FileShare.ReadWrite|Delete,
    /// 跟写锁内部的 writer 兼容。返回去掉末尾换行的纯文本行。
    /// </summary>
    public string[] ReadLines()
    {
        var path = Path.Combine(_logDir, $"{DateTime.Now:yyyy-MM-dd}.log");
        if (!File.Exists(path)) return Array.Empty<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        // split on \n and \r, then strip any trailing \r left from \r\n
        return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public void Info(string subsystem, string message)
        => Write("INFO ", subsystem, message);

    public void Warn(string subsystem, string message)
        => Write("WARN ", subsystem, message);

    public void Error(string subsystem, string message, Exception? ex = null)
        => Write("ERROR", subsystem,
            ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string subsystem, string message)
    {
        if (string.IsNullOrEmpty(subsystem)) subsystem = "unknown";
        if (message is null) message = "";

        lock (_writeLock)
        {
            var now = DateTime.Now;
            if (_writer is null || now.Date != _currentDay)
            {
                _writer?.Dispose();
                _currentDay = now.Date;
                var path = Path.Combine(_logDir, $"{now:yyyy-MM-dd}.log");
                _writer = new StreamWriter(new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    AutoFlush = true,
                };
            }
            var line = $"[{now:HH:mm:ss.fff}] [{level}] [{subsystem}] {message}";
            _writer.WriteLine(line);
        }
    }

    /// <summary>
    /// 启动时清理:删 &gt;days 天的 *.log 文件。文件名 = yyyy-MM-dd.log。
    /// 文件锁住 / 解析不了日期 / 不存在的都跳过。
    /// </summary>
    public static int CleanupOlderThan(string projectRoot, int days)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || days < 0) return 0;
        var logDir = Path.Combine(projectRoot, "Logs");
        if (!Directory.Exists(logDir)) return 0;
        var cutoff = DateTime.Now.Date.AddDays(-days);
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date.Date < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch
            {
                // 文件锁住 / IO 错误 → 跳过
            }
        }
        return deleted;
    }
}
