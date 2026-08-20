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
/// (Settings.HttpProxy* 4 字段)。
///
/// 设值口径:
/// - Enabled=false → handler.Proxy=null / UseProxy=false (不走 WinHTTP default system proxy)
/// - Enabled=true + UseSystemProxy=true → handler.UseProxy=true (跟 OS 默认 proxy; 不设 Proxy 让 WinHTTP
///   自动检测 IE settings / WinHTTP service config / WPAD / PAC)
/// - Enabled=true + UseSystemProxy=false + URL/Port 合法 → handler.Proxy = WebProxy(http://url:port)
/// - URL 不带 scheme → 默认 http://
/// - Port 越界 (0, >65535) → 显式 Proxy=null/UseProxy=false (避免 WinHTTP default system proxy 走 R2 mitigation)
/// </summary>
public sealed class HttpProxyConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int Port { get; set; }
    /// <summary>v0.6.22+:继承系统代理 — 启用代理但由 OS 决定具体 URL/Port(WPAD/PAC/IE settings)。
    /// 此时 Url/Port 字段被忽略,UI 灰显。UseSystemProxy=true 时 git env 注入也跳过(由 OS 处理)。</summary>
    public bool UseSystemProxy { get; set; }

    public static HttpProxyConfig Disabled { get; } = new();

    public static HttpProxyConfig From(Settings s)
    {
        if (s is null) return Disabled;
        if (s.HttpProxyMode == HttpProxyMode.Off) return Disabled;
        return new HttpProxyConfig
        {
            Enabled = true,
            UseSystemProxy = s.HttpProxyMode == HttpProxyMode.InheritSystem,
            Url = s.HttpProxyUrl,
            Port = s.HttpProxyPort,
        };
    }

    /// <summary>Application HTTP client 代理: 三种 mode — disabled / system / custom.</summary>
    public void ApplyTo(HttpClientHandler handler)
    {
        if (handler is null) return;
        if (!Enabled)
        {
            // Disabled 默认: 显式不走 system proxy (避免 WinHTTP 静默走 OS 设置)
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        if (UseSystemProxy)
        {
            // 继承系统代理: 设 UseProxy=true 但 *不* 覆盖 handler.Proxy — 让 WinHTTP
            // 走 IE settings / WPAD / PAC 自动检测。不设 Proxy = 不覆盖 default = 跟系统走。
            handler.Proxy = null;
            handler.UseProxy = true;
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
    /// per-process, 不污染整个 WPF。
    /// v0.6.22+:UseSystemProxy=true 时跳过 env 注入 — 让 git 进程通过 OS-level proxy 设置自己解析
    /// (git 自身会读 HTTP_PROXY env; 但用户既然选了"继承系统",通常意味着 IE/PAC/WPAD 配置,
    /// 这套配置 OS 一般会自动给进程设置环境变量,但跨进程不一定可靠;此处干脆 unset 我们自己的 env,
    /// 让 git 进程用 OS 默认)。</summary>
    public void ApplyTo(ProcessStartInfo psi)
    {
        if (!Enabled) return;
        if (UseSystemProxy) return;  // 跳过 env 注入,OS 决定
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
