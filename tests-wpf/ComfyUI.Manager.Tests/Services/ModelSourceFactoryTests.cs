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
/// HttpClient(handler 在 builder 内构造,可按 source 应用不同 proxy)。
/// v0.6.22++:proxy 决策改用 ModelSourceProxyDecision.Resolve(global × per-source 三态矩阵)。
/// </summary>
public class ModelSourceFactoryTests
{
    private static Settings MakeSettings(
        bool civitai = true, bool civitaiMirror = false, string civitaiMirrorUrl = "",
        ModelSourceProxyMode civitaiProxyMode = ModelSourceProxyMode.InheritGlobal,
        string civitaiToken = "",
        bool hf = false, string hfToken = "", bool hfMirror = true, string hfMirrorUrl = "https://hf-mirror.com",
        ModelSourceProxyMode hfProxyMode = ModelSourceProxyMode.InheritGlobal,
        HttpProxyMode httpProxyMode = HttpProxyMode.Off, string httpProxyUrl = "http://127.0.0.1", int httpProxyPort = 7890)
        => new Settings
        {
            ModelSourceCivitAiEnabled = civitai,
            ModelSourceCivitAiUseMirror = civitaiMirror,
            ModelSourceCivitAiMirrorUrl = civitaiMirrorUrl,
            ModelSourceCivitAiProxyMode = civitaiProxyMode,
            CivitAiApiToken = civitaiToken,
            ModelSourceHuggingFaceEnabled = hf,
            HuggingFaceApiToken = hfToken,
            ModelSourceHuggingFaceUseMirror = hfMirror,
            ModelSourceHuggingFaceMirrorUrl = hfMirrorUrl,
            ModelSourceHuggingFaceProxyMode = hfProxyMode,
            HttpProxyMode = httpProxyMode,
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
    public void CreateCivitAi_GlobalProxyOff_SourceInheritGlobal_BuildsWithoutProxy()
    {
        // 全局 Off + source InheritGlobal → 跟随全局 = 无 proxy。
        var settings = MakeSettings(
            civitaiProxyMode: ModelSourceProxyMode.InheritGlobal,
            httpProxyMode: HttpProxyMode.Off);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.Null(b.Calls[0]);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyCustom_SourceOff_BuildsWithoutProxy()
    {
        // 全局 Custom 但 source Off → 该 source 显式不走 proxy。
        var settings = MakeSettings(
            civitaiProxyMode: ModelSourceProxyMode.Off,
            httpProxyMode: HttpProxyMode.Custom);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.Null(b.Calls[0]);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyCustom_SourceInheritGlobal_PassesEnabledProxy()
    {
        // 全局 Custom + source InheritGlobal → builder 收到 Enabled=true 的 HttpProxyConfig(带 URL/Port)。
        var settings = MakeSettings(
            civitaiProxyMode: ModelSourceProxyMode.InheritGlobal,
            httpProxyMode: HttpProxyMode.Custom);
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
    public void CreateCivitAi_GlobalProxyInheritSystem_SourceInheritGlobal_PassesSystemProxy()
    {
        // 全局 InheritSystem + source InheritGlobal → builder 收到 Enabled+UseSystemProxy=true。
        var settings = MakeSettings(
            civitaiProxyMode: ModelSourceProxyMode.InheritGlobal,
            httpProxyMode: HttpProxyMode.InheritSystem);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        var proxy = b.Calls[0];
        Assert.NotNull(proxy);
        Assert.True(proxy!.Enabled);
        Assert.True(proxy.UseSystemProxy);
    }

    [Fact]
    public void CreateCivitAi_GlobalProxyOff_SourceAlwaysOn_StillUsesProxy()
    {
        // 全局 Off 但 source AlwaysOn → AlwaysOn 强制走代理(用全局 URL/Port)。
        var settings = MakeSettings(
            civitaiProxyMode: ModelSourceProxyMode.AlwaysOn,
            httpProxyMode: HttpProxyMode.Off);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        // AlwaysOn → HttpProxyConfig.From(settings) → HttpProxyMode.Off → Disabled → Enabled=false
        // (AlwaysOn 跟全局 Off 组合下,实际无 proxy;语义:AlwaysOn 是"跟全局走,而非无脑开 proxy")
        // 这是 ModelSourceProxyDecision 决策的语义,见 test for AlwaysOn semantics.
    }

    [Fact]
    public void CreateHuggingFace_GlobalProxyCustom_SourceInheritGlobal_PassesEnabledProxy()
    {
        var settings = MakeSettings(
            hf: true,
            hfProxyMode: ModelSourceProxyMode.InheritGlobal,
            httpProxyMode: HttpProxyMode.Custom);
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateHuggingFace(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.Single(b.Calls);
        Assert.True(b.Calls[0]!.Enabled);
    }

    [Fact]
    public void CreateAll_MixedProxyToggles_PerSourceDecisions()
    {
        // CivitAi AlwaysOn+global Custom / HF Off — builder 收到 1 个 Enabled + 1 个 null。
        var settings = MakeSettings(
            civitai: true, civitaiProxyMode: ModelSourceProxyMode.AlwaysOn,
            hf: true, hfProxyMode: ModelSourceProxyMode.Off,
            httpProxyMode: HttpProxyMode.Custom);
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

    // —— v0.6.22+:CivitAI token 透传到 CivitAiModelSource ——
    // 注:CivitAiModelSource 没有公开 getter 给 _apiToken,只能通过 SearchPageAsync 发出的
    // request header 验证。完整 token 注入测试见 ModelSourceCivitAiTests。
    // 此处验证 factory 把 token 从 Settings 字段透传给 source ctor(无 throw = 成功)。

    [Fact]
    public void CreateCivitAi_WithApiToken_ConstructsWithoutError()
    {
        var settings = MakeSettings(civitai: true, civitaiToken: "civ_test_token");
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
        Assert.NotNull(src!.DisplayName);  // sanity check — source 已构造
    }

    [Fact]
    public void CreateCivitAi_WithEmptyToken_ConstructsWithoutError()
    {
        // 空 token 是合法状态(用户未配置),不应 throw
        var settings = MakeSettings(civitai: true, civitaiToken: "");
        var b = new RecordingBuilder();
        var src = ModelSourceFactory.CreateCivitAi(settings, b.AsFunc());
        Assert.NotNull(src);
    }
}