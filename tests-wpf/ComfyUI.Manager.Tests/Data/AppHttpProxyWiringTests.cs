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

    private static HttpClient InvokeBuildHttpClient(Settings _, HttpProxyConfig proxy)
    {
        var method = typeof(App).GetMethod(
            "BuildHttpClient",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return (HttpClient)method!.Invoke(null, new object?[] { proxy })!;
    }

    private static HttpClientHandler handler(HttpClient http)
    {
        var f = typeof(HttpClient).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        return (HttpClientHandler)f!.GetValue(http)!;
    }
}
