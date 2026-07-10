using System;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public class WsClient : IDisposable
{
    public WsClient(string url) { }
    public Task ConnectAsync() => Task.CompletedTask;
    public void Dispose() { }
}
