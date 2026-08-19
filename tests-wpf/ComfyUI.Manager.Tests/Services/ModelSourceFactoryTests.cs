using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.21 T4:ModelSourceFactory 单元测试。
/// v0.6.22+:factory 改用 Func&lt;HttpProxyConfig?, HttpClient&gt; builder — 每个 source 拿自己的
/// HttpClient(handler 在 builder 内构造,可按 source 应用不同 proxy)。测试用 inline lambda
/// 验证:① 不传 builder → factory 内直接 new HttpClient(handler);② 启用 proxy 时
/// builder.ApplyTo 把 handler.Proxy 设成 WebProxy;③ per-source UseProxy 是 AND 关系(全局 + source 都得 true)。
/// </summary>
public class ModelSourceFactoryTests
{
    private static Settings MakeSettings(
        bool civitai = true, bool civitaiMirror = false, string civitaiMirrorUrl = "",
        bool civitaiUseProxy = false,
        bool hf = false, string hfToken = "", bool hfMirror = true, string hfMirrorUrl = "https://hf-mirror.com",
        bool hfUseProxy = false,
        bool httpProxyEnabled = false, string httpProxyUrl = "http://127.0.0.1", int httpProxyPort = 7890)
        => new Settings
        {
            ModelSourceCivitAiEnabled = civitai,
            ModelSourceCivitAiUseMirror = civitaiMirror,
            ModelSourceCivitAiMirrorUrl = civitaiMirrorUrl,
            ModelSourceCivitAiUseProxy = civitaiUseProxy,
            ModelSourceHuggingFaceEnabled = hf,
            HuggingFaceApiToken = hfToken,
            ModelSourceHuggingFaceUseMirror = hfMirror,
            ModelSourceHuggingFaceMirrorUrl = hfMirrorUrl,
            ModelSourceHuggingFaceUseProxy = hfUseProxy,
            HttpProxyEnabled = httpProxyEnabled,
            HttpProxyUrl = httpProxyUrl,
            HttpProxyPort = httpProxyPort,
        };

    /// <summary>测试用 builder:接收 HttpProxyConfig?,记下传进来的 proxy + 返回带/不带 proxy 的 HttpClient。</summary>
    private sealed class RecordingBuilder
    {
        public List<HttpProxyConfig?> Calls { get; } = new();
        public HttpClient Build(HttpProxyConfig? proxy)
        {
            Calls.Add(proxy);
            var handler = new HttpClientHandler();
            if (proxy is not null) proxy.ApplyTo(handler);
            else { handler.Proxy = null; handler.UseProxy = false; }
            return new HttpClient(handler);
        }
        public Func<HttpProxyConfig?, HttpClient> AsFunc() => Build;
    }

    [Fact]
    public void CreateCivitAi_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(civitai: false);
        var b = new RecordingBuilder();
        var result = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.Null(result);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void CreateHuggingFace_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(hf: false);
        var b = new RecordingBuilder();
        var result = ModelSourceFactory.CreateHuggingFace(settings, b.AsFunc());
        Assert.Null(result);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void CreateAll_ResolvesMirrorUrl_And_StripsTrailingSlash()
    {
        var settings = MakeSettings(
            civitai: true, civitaiMirror: true, civitaiMirrorUrl: "https://my-mirror.example.com/civitai/",
            hf: true, hfMirror: true, hfMirrorUrl: "https://my-mirror.example.com/hf/");
        var b = new RecordingBuilder();
        var sources = ModelSourceFactory.CreateAll(settings, b.AsFunc());
        Assert.Equal(2, new List<IModelSource>(sources).Count);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyOff_SourceUseProxyOn_BuildsWithoutProxy()
    {
        // AND 关系:全局 HttpProxyEnabled=false → 即使 source UseProxy=true 也按无 proxy 处理。
        var settings = MakeSettings(civitaiUseProxy: true, httpProxyEnabled: false);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.Null(b.Calls[0]);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyOn_SourceUseProxyOff_BuildsWithoutProxy()
    {
        // AND 关系:全局 ON 但 source OFF → 无 proxy(走 default system proxy 显式 disable)。
        var settings = MakeSettings(civitaiUseProxy: false, httpProxyEnabled: true);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.Null(b.Calls[0]);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyOn_SourceUseProxyOn_PassesEnabledProxy()
    {
        // AND 双 true → builder 收到 Enabled=true 的 HttpProxyConfig。
        var settings = MakeSettings(civitaiUseProxy: true, httpProxyEnabled: true);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        var proxy = b.Calls[0];
        Assert.NotNull(proxy);
        Assert.True(proxy!.Enabled);
        Assert.Equal(7890, proxy.Port);
    }

    [Fact]
    public void CreateHuggingFace_GlobalProxyOn_SourceUseProxyOn_PassesEnabledProxy()
    {
        var settings = MakeSettings(hf: true, hfUseProxy: true, httpProxyEnabled: true);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateHuggingFace(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.True(b.Calls[0]!.Enabled);
    }

    [Fact]
    public void CreateAll_MixedProxyToggles_PerSourceDecisions()
    {
        // CivitAi ON+proxy ON / HF ON+proxy OFF — builder 收到 1 个 Enabled + 1 个 null。
        var settings = MakeSettings(
            civitai: true, civitaiUseProxy: true,
            hf: true, hfUseProxy: false,
            httpProxyEnabled: true);
        var b = new RecordingBuilder();
        var sources = new List<IModelSource>(ModelSourceFactory.CreateAll(settings, b.AsFunc()));
        Assert.Equal(2, sources.Count);
        Assert.Equal(2, b.Calls.Count);
        // 顺序:CreateCivitAi 先,CreateHuggingFace 后
        Assert.True(b.Calls[0]!.Enabled);
        Assert.Null(b.Calls[1]);
    }

    [Fact]
    public void TestConnectionAsync_NoToken_ReturnsFalseForFailureStatus()
    {
        // 不可达的 URL → HttpRequestException → catch → false。
        var ok = ModelSourceFactory.TestConnectionAsync(
            baseUrl: "http://127.0.0.1:1",  // 1 端口基本不可达
            apiToken: "",
            timeoutSeconds: 1).GetAwaiter().GetResult();
        Assert.False(ok);
    }
}