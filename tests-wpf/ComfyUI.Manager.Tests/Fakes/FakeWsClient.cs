using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Tests.Fakes;

public class FakeWsClient : WsClient
{
    public FakeWsClient() : base("ws://fake:7800/ws/events") { }

    /// <summary>直接触发 OnMessage 事件。</summary>
    public void Emit(string channel, params object[] args)
    {
        var jsonArgs = args.Select(a => JsonSerializer.SerializeToElement(a)).ToArray();
        var msg = new WsMessage(channel, jsonArgs, DateTime.Now);
        RaiseOnMessageAsync(msg).GetAwaiter().GetResult();
    }
}