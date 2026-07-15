using System;
using System.Diagnostics;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// GitProxyConfig:git 代理的 live 配置。Settings 持久层和 GitRunner/BulkUpdateOrchestrator
/// 运行期之间共用一份实例 —— 改 SettingsViewModel 里的勾选 / URL / port 立即影响下一次 git 调用。
///
/// 写入方式:只写到 ProcessStartInfo.EnvironmentVariables(per-process),不调
/// Environment.SetEnvironmentVariable。这样代理只作用于这一个 git 子进程,不会污染
/// 整个 WPF 进程的浏览器、其它 HTTP 调用等。
/// </summary>
public sealed class GitProxyConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int Port { get; set; }

    /// <summary>代理关闭的默认值。静态单例,避免重复分配。</summary>
    public static GitProxyConfig Disabled { get; } = new();

    /// <summary>从持久化的 Settings 构造(只读快照,后续 settings 改了不会回写到这里)。</summary>
    public static GitProxyConfig From(Settings s)
    {
        if (s is null) return Disabled;
        return new GitProxyConfig
        {
            Enabled = s.GitProxyEnabled,
            Url = s.GitProxyUrl,
            Port = s.GitProxyPort,
        };
    }

    /// <summary>
    /// 如果启用了代理,把 HTTP_PROXY / HTTPS_PROXY 写入 psi.EnvironmentVariables。
    /// 否则 noop(psi 自身不带这些 var,git 走直连)。
    ///
    /// 不调用 Environment.SetEnvironmentVariable —— 只影响这一个 ProcessStartInfo
    /// 启动的 git 进程,不会污染整个 WPF。
    /// </summary>
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
        // ProcessStartInfo.EnvironmentVariables 在 Windows 上 key 大小写不敏感;
        // 设大写(libcurl 约定)。
        psi.EnvironmentVariables["HTTP_PROXY"] = proxy;
        psi.EnvironmentVariables["HTTPS_PROXY"] = proxy;
    }
}