using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class GitProxyConfigTests
{
    [Fact]
    public void ApplyTo_EnabledAndValid_AddsHttpAndHttpsProxy()
    {
        var proxy = new GitProxyConfig
        {
            Enabled = true,
            Url = "127.0.0.1",
            Port = 7890,
        };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTPS_PROXY"]);
    }

    [Fact]
    public void ApplyTo_Disabled_DoesNotTouchEnv()
    {
        var proxy = new GitProxyConfig
        {
            Enabled = false,
            Url = "127.0.0.1",
            Port = 7890,
        };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTP_PROXY"));
        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTPS_PROXY"));
    }

    [Fact]
    public void ApplyTo_EnabledButEmptyUrl_DoesNotInject()
    {
        var proxy = new GitProxyConfig { Enabled = true, Url = "", Port = 7890 };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTP_PROXY"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void ApplyTo_EnabledButInvalidPort_DoesNotInject(int port)
    {
        var proxy = new GitProxyConfig { Enabled = true, Url = "127.0.0.1", Port = port };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTP_PROXY"));
    }

    [Fact]
    public void ApplyTo_PreservesSchemeIfProvided()
    {
        var proxy = new GitProxyConfig { Enabled = true, Url = "socks5://127.0.0.1", Port = 1080 };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.Equal("socks5://127.0.0.1:1080", psi.EnvironmentVariables["HTTP_PROXY"]);
    }

    [Fact]
    public void From_Settings_CopiesAllFields()
    {
        var s = new Settings
        {
            GitProxyEnabled = true,
            GitProxyUrl = "10.0.0.1",
            GitProxyPort = 8888,
        };

        var cfg = GitProxyConfig.From(s);

        Assert.True(cfg.Enabled);
        Assert.Equal("10.0.0.1", cfg.Url);
        Assert.Equal(8888, cfg.Port);
    }

    [Fact]
    public void From_NullSettings_ReturnsDisabled()
    {
        var cfg = GitProxyConfig.From(null!);

        Assert.False(cfg.Enabled);
    }

    [Fact]
    public void Settings_GitProxyEnabled_RoundTripsThroughJson()
    {
        var original = new Settings { GitProxyEnabled = true, GitProxyUrl = "p.local", GitProxyPort = 3128 };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<Settings>(json)!;

        Assert.True(restored.GitProxyEnabled);
        Assert.Equal("p.local", restored.GitProxyUrl);
        Assert.Equal(3128, restored.GitProxyPort);
    }

    [Fact]
    public void Settings_GitProxyEnabled_DefaultsFalseOnFreshInstance()
    {
        var s = new Settings();

        Assert.False(s.GitProxyEnabled);
    }
}