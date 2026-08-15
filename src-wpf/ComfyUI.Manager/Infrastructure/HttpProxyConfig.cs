using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// HttpProxyConfig: 统一代理配置 —— 驱动 HttpClient(HTTP catalog 拉取)+ git 进程
/// (HTTP_PROXY/HTTPS_PROXY env 注入)。
/// v0.6.15.4 替代 GitProxyConfig: 单类两个 ApplyTo 方法, 单一 source of truth
/// (Settings.HttpProxy* 3 字段)。
///
/// 设值口径:
/// - Enabled=false → handler.Proxy=null / UseProxy=false (不走 WinHTTP default system proxy)
/// - Enabled=true 且 URL/Port 合法 → handler.Proxy = WebProxy(http://url:port) / psi.HTTP_PROXY 设
/// - URL 不带 scheme → 默认 http://
/// - Port 越界 (0, >65535) → 显式 Proxy=null/UseProxy=false (避免 WinHTTP default system proxy 走 R2 mitigation)
/// </summary>
public sealed class HttpProxyConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int Port { get; set; }

    public static HttpProxyConfig Disabled { get; } = new();

    public static HttpProxyConfig From(Settings s)
    {
        if (s is null) return Disabled;
        return new HttpProxyConfig
        {
            Enabled = s.HttpProxyEnabled,
            Url = s.HttpProxyUrl,
            Port = s.HttpProxyPort,
        };
    }

    /// <summary>Application HTTP client 代理: Enabled 时设 WebProxy; 否则显式 null + UseProxy=false.</summary>
    public void ApplyTo(HttpClientHandler handler)
    {
        if (handler is null) return;
        if (!Enabled)
        {
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(Url))
        {
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        if (Port <= 0 || Port > 65535)
        {
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        handler.Proxy = new WebProxy(BuildProxyUri());
        handler.UseProxy = true;
    }

    /// <summary>Git 进程代理: 写 HTTP_PROXY/HTTPS_PROXY env 到 ProcessStartInfo。
    /// per-process, 不污染整个 WPF。</summary>
    public void ApplyTo(ProcessStartInfo psi)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Port <= 0 || Port > 65535) return;

        var rawUrl = Url.Trim();
        var withScheme = rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? rawUrl
            : "http://" + rawUrl;

        var proxy = $"{withScheme}:{Port}";
        psi.EnvironmentVariables["HTTP_PROXY"] = proxy;
        psi.EnvironmentVariables["HTTPS_PROXY"] = proxy;
    }

    private Uri BuildProxyUri()
    {
        var rawUrl = Url.Trim();
        var withScheme = rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? rawUrl
            : "http://" + rawUrl;
        return new Uri($"{withScheme}:{Port}");
    }
}
