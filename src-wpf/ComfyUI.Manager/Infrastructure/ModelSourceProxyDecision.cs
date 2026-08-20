using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>v0.6.22++:决策助手 — 综合全局 HttpProxyMode + per-source ModelSourceProxyMode
/// → 最终传给 source builder 的 HttpProxyConfig?(null = 这个 source 不用 proxy,
/// 让 builder 内 handler.Proxy=null + UseProxy=false 显式不走 WinHTTP default)。</summary>
public static class ModelSourceProxyDecision
{
    public static HttpProxyConfig? Resolve(
        HttpProxyMode globalMode,
        ModelSourceProxyMode sourceMode,
        Settings settings)
    {
        switch (sourceMode)
        {
            case ModelSourceProxyMode.Off:
                return null;  // this source 显式不走 proxy
            case ModelSourceProxyMode.AlwaysOn:
                // 总是启用:用全局 config(若全局 InheritSystem → UseProxy=true;若 Custom → 用 URL/Port)
                return HttpProxyConfig.From(settings);
            case ModelSourceProxyMode.InheritGlobal:
                // 跟随全局
                if (globalMode == HttpProxyMode.Off) return null;
                return HttpProxyConfig.From(settings);
            default:
                return null;
        }
    }
}