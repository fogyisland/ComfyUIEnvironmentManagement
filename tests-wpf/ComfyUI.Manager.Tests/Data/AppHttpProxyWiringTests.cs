using System.Net;
using System.Net.Http;
using System.Reflection;
using ComfyUI.Manager;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class AppHttpProxyWiringTests
{
    [Fact]
    public void BuildHttpClient_ProxyEnabled_HandlerHasWebProxy()
    {
        var settings = new Settings
        {
            HttpProxyEnabled = true,
            HttpProxyUrl = "127.0.0.1",
            HttpProxyPort = 7890,
        };
        var proxy = HttpProxyConfig.From(settings);

        var http = InvokeBuildHttpClient(settings, proxy);

        Assert.IsType<HttpClientHandler>(handler(http));
        Assert.True(handler(http).UseProxy);
        Assert.NotNull(handler(http).Proxy);
        Assert.IsType<WebProxy>(handler(http).Proxy);
    }

    [Fact]
    public void BuildHttpClient_ProxyDisabled_HandlerHasNullProxy()
    {
        var settings = new Settings();
        var proxy = HttpProxyConfig.Disabled;

        var http = InvokeBuildHttpClient(settings, proxy);

        Assert.False(handler(http).UseProxy);
        Assert.Null(handler(http).Proxy);
    }

    [Fact]
    public void BuildHttpClient_ProxyNull_HandlerHasNullProxy()
    {
        var settings = new Settings();

        var http = InvokeBuildHttpClient(settings, null);

        Assert.False(handler(http).UseProxy);
        Assert.Null(handler(http).Proxy);
    }

    // v0.6.22+: 所有 BuildHttpClient 返回的 HttpClient 都要带 User-Agent + Accept 头 —
    // 避免 CivitAI/HF 的 Cloudflare 反爬把空 User-Agent 的 .NET client 当 bot 拦截返回 HTML。
    [Fact]
    public void BuildHttpClient_SetsUserAgentHeader()
    {
        var http = InvokeBuildHttpClient(new Settings(), HttpProxyConfig.Disabled);

        var ua = http.DefaultRequestHeaders.UserAgent;
        Assert.NotEmpty(ua);
        // ParseAdd("ComfyUI-Manager/0.6.13") 拆成 Product=Name="ComfyUI-Manager" Version="0.6.13"
        Assert.Contains(ua, h => h.Product != null && h.Product.Name == "ComfyUI-Manager" && h.Product.Version == "0.6.13");
    }

    [Fact]
    public void BuildHttpClient_SetsAcceptHeader()
    {
        var http = InvokeBuildHttpClient(new Settings(), HttpProxyConfig.Disabled);

        var accept = http.DefaultRequestHeaders.Accept;
        Assert.NotEmpty(accept);
        Assert.Contains(accept, h => h.MediaType != null && h.MediaType.Contains("application/json"));
    }

    // 校验 DefaultUserAgent 常量 = "ComfyUI-Manager/0.6.13" (跟 v0.6.13-B singleton 用的一致)。
    [Fact]
    public void DefaultUserAgent_Constant_MatchesVersionedIdentifier()
    {
        Assert.Equal("ComfyUI-Manager/0.6.13", App.DefaultUserAgent);
    }

    private static HttpClient InvokeBuildHttpClient(Settings _, HttpProxyConfig? proxy)
    {
        var method = typeof(App).GetMethod(
            "BuildHttpClient",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return (HttpClient)method!.Invoke(null, new object?[] { proxy })!;
    }

    private static HttpClientHandler handler(HttpClient http)
    {
        // .NET 8: HttpClient inherits _handler from HttpMessageInvoker (base class).
        // 旧 .NET Framework / .NET 5 时代 _handler 是 HttpClient 自己的字段; .NET 8
        // 迁到 base class。
        var f = typeof(HttpClient).BaseType!.GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        return (HttpClientHandler)f!.GetValue(http)!;
    }
}
