using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// LogTailer:按 pollInterval 轮询一个 log 文件,把新追加的行通过 NewLine
/// 事件推送出去。从文件"当前末尾"开始(不 replay 历史),每次只读新增部分。
///
/// 替代了 M5.1 中 WsClient 的 log push channel —— 现在 WPF 直接 tail
/// ProcessLauncher 写入的 logs/&lt;env-id&gt;.log 文件。
/// </summary>
public sealed class LogTailer : IDisposable
{
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
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LogTailer));
        if (_cts is not null) return;

        // 从当前末尾开始 —— 不 replay 历史。
        try
        {
            if (File.Exists(_logFilePath))
            {
                _offset = new FileInfo(_logFilePath).Length;
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
