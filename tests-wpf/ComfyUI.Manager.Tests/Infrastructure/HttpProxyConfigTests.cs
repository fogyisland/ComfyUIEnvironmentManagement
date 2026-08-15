using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class HttpProxyConfigTests
{
    [Fact]
    public void ApplyTo_HttpClientHandler_Disabled_SetsProxyNullAndUseProxyFalse()
    {
        var proxy = HttpProxyConfig.Disabled;
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_Enabled_SetsWebProxyAndUseProxyTrue()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 7890 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.NotNull(handler.Proxy);
        Assert.True(handler.UseProxy);
        Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), ((WebProxy)handler.Proxy!).Address);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_UrlWithoutScheme_PrependsHttp()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "proxy.local", Port = 8080 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Equal(new Uri("http://proxy.local:8080"), ((WebProxy)handler.Proxy!).Address);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_InvalidPort_DisablesProxy()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 0 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ApplyTo_ProcessStartInfo_Enabled_WritesHttpAndHttpsProxyEnv()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 7890 };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTPS_PROXY"]);
    }

    [Fact]
    public void ApplyTo_ProcessStartInfo_Disabled_NoEnvWritten()
    {
        var proxy = HttpProxyConfig.Disabled;
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTP_PROXY"));
        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTPS_PROXY"));
    }

    [Fact]
    public void From_Settings_MapsFields()
    {
        var s = new Settings
        {
            HttpProxyEnabled = true,
            HttpProxyUrl = "10.0.0.1",
            HttpProxyPort = 8888,
        };

        var cfg = HttpProxyConfig.From(s);

        Assert.True(cfg.Enabled);
        Assert.Equal("10.0.0.1", cfg.Url);
        Assert.Equal(8888, cfg.Port);
    }

    [Fact]
    public void From_NullSettings_ReturnsDisabled()
    {
        var cfg = HttpProxyConfig.From(null!);

        Assert.False(cfg.Enabled);
    }
}
