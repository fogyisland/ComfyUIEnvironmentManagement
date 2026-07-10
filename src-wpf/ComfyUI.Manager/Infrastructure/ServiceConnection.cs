using System;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// ServiceConnection:封装 Launcher + Api + Ws 三件套的容器。
/// App 持有此对象,关闭时整体 Dispose。
/// </summary>
public class ServiceConnection : IDisposable
{
    public PythonLauncher Launcher { get; }
    public ApiClient Api { get; }
    public WsClient Ws { get; }

    public ServiceConnection(PythonLauncher launcher,
        ApiClient api, WsClient ws)
    {
        Launcher = launcher;
        Api = api;
        Ws = ws;
    }

    public void Dispose()
    {
        Ws.Dispose();
        Launcher.Dispose();
    }
}
