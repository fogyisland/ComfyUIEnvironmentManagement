using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// LogTailer:按 pollInterval 轮询一个 log 文件,把新追加的行通过 NewLine
/// 事件推送出去。Start 时先把文件末尾最多 MaxHistoryBytes 的历史行 emit 出来
/// (从头打开 dialog 也能看见之前跑过的输出),然后接着 tail 增量内容。
///
/// 替代了 M5.1 中 WsClient 的 log push channel —— 现在 WPF 直接 tail
/// ProcessLauncher 写入的 logs/&lt;env-id&gt;.log 文件。
/// </summary>
public sealed class LogTailer : IDisposable
{
    /// <summary>
    /// 启动时回放的最大字节数(从文件末尾向前)。64KB ≈ 500-700 行,匹配
    /// <see cref="LogViewerViewModel.MaxLines"/> 上限,避免 tailer 输出
    /// 远超 VM 容量被后续 RemoveAt(0) 浪费 CPU。
    /// </summary>
    public const int MaxHistoryBytes = 64 * 1024;

    private readonly string _logFilePath;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _offset;
    private bool _disposed;

    /// <summary>
    /// 每读到一行新日志就触发。At 为读到该行的本地时间。
    /// </summary>
    public event Action<LogLine>? NewLine;

    public LogTailer(string logFilePath, TimeSpan? pollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            throw new ArgumentException("logFilePath 不能为空", nameof(logFilePath));
        }
        _logFilePath = logFilePath;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// 开始 tail。多次调用安全(后续调用 noop)。
    /// 启动时:如果文件存在,先回放末尾 MaxHistoryBytes 之内的历史行(整文件比
    /// MaxHistoryBytes 小就读全部),让 stopped env 也能看到上次运行的尾部输出;
    /// 然后 _offset 设到 file.Length,后续只读新增内容。
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LogTailer));
        if (_cts is not null) return;

        try
        {
            if (File.Exists(_logFilePath))
            {
                var info = new FileInfo(_logFilePath);
                EmitHistory(info);
                _offset = info.Length;
            }
            else
            {
                _offset = 0;
            }
        }
        catch
        {
            _offset = 0;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// 回放文件末尾的历史行(整文件较小时全读)。如果从偏移开始读,首行
    /// 可能被截断 —— 丢弃首 partial 行,避免显示半截内容。
    /// </summary>
    private void EmitHistory(FileInfo info)
    {
        try
        {
            using var fs = info.OpenRead();
            if (fs.Length == 0) return;

            var startFrom = fs.Length > MaxHistoryBytes ? fs.Length - MaxHistoryBytes : 0L;
            var truncatedHead = startFrom > 0;
            fs.Seek(startFrom, SeekOrigin.Begin);

            var len = fs.Length - startFrom;
            var buf = new byte[len];
            int read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return;

            var text = Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (truncatedHead && i == 0) continue;  // 截断处首行丢弃
                if (line.Length == 0) continue;
                EmitLine(line);
            }
        }
        catch
        {
            // 文件读取失败 → 当作无历史,tail 正常继续
        }
    }

    /// <summary>
    /// 停止 tail。可以再次 Start 重新开始。
    /// </summary>
    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { cts.Dispose(); } catch { }
        _loop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    using var fs = new FileStream(
                        _logFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var len = fs.Length;

                    // 文件被截断 / rotate 了 —— 回到开头
                    if (len < _offset) _offset = 0;

                    if (len > _offset)
                    {
                        fs.Seek(_offset, SeekOrigin.Begin);
                        int read;
                        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                            pending.Append(chunk);
                            _offset += read;
                        }

                        // 按行 emit
                        var text = pending.ToString();
                        var newlineIdx = text.IndexOf('\n');
                        while (newlineIdx >= 0)
                        {
                            var line = text.Substring(0, newlineIdx);
                            // strip trailing \r / \n
                            line = line.TrimEnd('\r', '\n');
                            EmitLine(line);
                            text = text.Substring(newlineIdx + 1);
                            newlineIdx = text.IndexOf('\n');
                        }
                        pending.Clear();
                        pending.Append(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 文件暂时被独占 / IO 抖动,下一轮再试
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // flush 残余 partial line(不 emit —— 没换行符不视为完整一行)
    }

    private void EmitLine(string line)
    {
        var ll = new LogLine { Text = line, At = DateTime.Now };
        try
        {
            NewLine?.Invoke(ll);
        }
        catch
        {
            // 单个订阅者抛了不能影响后续订阅者
        }
    }
}
