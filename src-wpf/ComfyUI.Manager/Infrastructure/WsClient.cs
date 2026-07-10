using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public record WsMessage(string Channel, JsonElement[] Args, DateTime Ts);

public class WsClient : IDisposable
{
    private readonly Uri _uri;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int _retryDelay = 1000;
    private bool _disposed;

    /// <summary>收到 WS 消息(channel → args → ts)。</summary>
    public event Func<WsMessage, Task>? OnMessage;

    public WsClient(string url)
    {
        _uri = new Uri(url);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_uri, ct);
        _retryDelay = 1000;
        _receiveLoop = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && _socket is { State: WebSocketState.Open })
        {
            try
            {
                var result = await _socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ReconnectAsync(ct);
                    continue;
                }
                var json = Encoding.UTF8.GetString(
                    buffer, 0, result.Count);
                var msg = JsonSerializer.Deserialize<JsonElement>(json);
                var channel = msg.GetProperty("channel").GetString() ?? "";
                var args = msg.GetProperty("args");
                var tsStr = msg.TryGetProperty("ts", out var t)
                    ? t.GetString() : null;
                DateTime.TryParse(tsStr, out var ts);
                var wsMsg = new WsMessage(channel,
                    args.EnumerateArray().ToArray(), ts);
                if (OnMessage is { } handler)
                    await handler(wsMsg);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await ReconnectAsync(ct);
            }
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        await Task.Delay(_retryDelay, ct);
        _retryDelay = Math.Min(_retryDelay * 2, 30_000);
        try
        {
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(_uri, ct);
            _retryDelay = 1000;
        }
        catch { /* 下一次 ReceiveLoop 重试 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Dispose(); } catch { }
    }
}
