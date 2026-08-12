using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
///
/// v0.6.12 SDD:加 per-env OperationLogPath + WriteOperation(envName, message);
/// 日志路径 `Logs/operation-{envName}-{yyyy-MM-dd}.log`,envName 经文件名净化。
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _logsDir;
    private readonly object _writeLock = new();
    private readonly object _fileLock = new();
    private StreamWriter? _writer;
    private DateTime _currentDay;

    /// <summary>
    /// v0.6.12:baseDir 可注入,默认 = projectRoot。Settings.LogDirectory 改了之后
    /// AppLogger / ProcessLauncher / 各 subsystem 都从 Settings 拿这个目录。
    /// Logs/ 子目录固定在 baseDir 下(跟原 projectRoot/Logs 行为一致)。
    /// </summary>
    public AppLogger(string projectRoot, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));
        _projectRoot = projectRoot.TrimEnd('\\', '/');
        var baseDirNormalized = (baseDir ?? _projectRoot).TrimEnd('\\', '/');
        _logsDir = Path.Combine(baseDirNormalized, "Logs");
        Directory.CreateDirectory(_logsDir);
    }

    public string LogDirectory => _logsDir;

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
        var path = Path.Combine(_logsDir, $"{DateTime.Now:yyyy-MM-dd}.log");
        if (!File.Exists(path)) return Array.Empty<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        // split on \n and \r, then strip any trailing \r left from \r\n
        return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public IEnumerable<string> ReadRecentLines(int daysBack = 2, int maxLines = 5)
    {
        if (daysBack <= 0 || maxLines <= 0) yield break;

        var today = DateTime.Now.Date;
        for (var dayOffset = 0; dayOffset < daysBack && maxLines > 0; dayOffset++)
        {
            var date = today.AddDays(-dayOffset);
            var path = Path.Combine(_logsDir, $"{date:yyyy-MM-dd}.log");
            if (!File.Exists(path)) continue;

            string[] lines;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                lines = sr.ReadToEnd().Split(
                    new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch
            {
                continue;
            }

            for (var i = lines.Length - 1; i >= 0 && maxLines > 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                yield return lines[i];
                maxLines--;
            }
        }
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
                var path = Path.Combine(_logsDir, $"{now:yyyy-MM-dd}.log");
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

    /// <summary>
    /// v0.6.12:per-env operation log 文件路径。envName 净化非法字符 + 截断 100 字符 +
    /// 空 fallback "unknown"。baseDir 是 Logs 的父目录(默认 = 调用方 AppLogger 的 _projectRoot);
    /// 静态调用可显式传。
    /// </summary>
    public static string OperationLogPath(string envName, DateTime date, string? baseDir = null)
    {
        var dir = baseDir ?? throw new ArgumentNullException(nameof(baseDir),
            "OperationLogPath 静态调用必须显式传 baseDir");
        var sanitized = SanitizeFileName(envName);
        var fileName = $"operation-{sanitized}-{date:yyyy-MM-dd}.log";
        return Path.Combine(dir, "Logs", fileName);
    }

    /// <summary>
    /// v0.6.12:追加一行到 per-env 当日 operation log。
    /// </summary>
    public void WriteOperation(string envName, string message)
    {
        // _logsDir = baseDir/Logs, 所以父目录就是 baseDir
        var baseDir = Path.GetDirectoryName(_logsDir) ?? _projectRoot;
        var path = OperationLogPath(envName, DateTime.Now, baseDir);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        lock (_fileLock)
        {
            // FileShare.ReadWrite|Delete 兼容 LogTailer 同步读
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{envName}] {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    private static string SanitizeFileName(string envName)
    {
        if (string.IsNullOrWhiteSpace(envName)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(envName.Length);
        foreach (var c in envName)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        var sanitized = sb.ToString().Trim();
        if (sanitized.Length > 100) sanitized = sanitized.Substring(0, 100);
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }
}