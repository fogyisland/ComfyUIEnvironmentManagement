using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.21: Per-source factory — reads Settings, picks base URL (mirror or
/// official) and API token, instantiates IModelSource. Returns null for disabled
/// sources; aggregator's internal IsEnabled filter never sees them.
///
/// v0.6.22+: per-source proxy — each source gets its own HttpClient built via the
/// injected <paramref name="httpBuilder"/>. Proxy applied only when BOTH global
/// HttpProxyEnabled AND source's per-source UseProxy flag are true. App.xaml.cs
/// supplies the builder that wraps the existing BuildHttpClient(HttpProxyConfig?) helper.
/// 同 mirror toggle:改 UseProxy 后需重启应用才会让 source 重新创建带 WebProxy 的 HttpClient。</summary>
public static class ModelSourceFactory
{
    public const string CivitAiOfficial = "https://civitai.com";
    public const string HuggingFaceOfficial = "https://huggingface.co";

    public static CivitAiModelSource? CreateCivitAi(
        Settings settings, Func<HttpProxyConfig?, HttpClient> httpBuilder)
    {
        if (!settings.ModelSourceCivitAiEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceCivitAiUseMirror,
                                     settings.ModelSourceCivitAiMirrorUrl,
                                     CivitAiOfficial);
        var proxy = ResolveProxy(settings.HttpProxyEnabled && settings.ModelSourceCivitAiUseProxy, settings);
        var http = httpBuilder(proxy);
        return new CivitAiModelSource(http, baseUrl);
    }

    public static HuggingFaceModelSource? CreateHuggingFace(
        Settings settings, Func<HttpProxyConfig?, HttpClient> httpBuilder)
    {
        if (!settings.ModelSourceHuggingFaceEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceHuggingFaceUseMirror,
                                     settings.ModelSourceHuggingFaceMirrorUrl,
                                     HuggingFaceOfficial);
        var proxy = ResolveProxy(settings.HttpProxyEnabled && settings.ModelSourceHuggingFaceUseProxy, settings);
        var http = httpBuilder(proxy);
        return new HuggingFaceModelSource(http, baseUrl, settings.HuggingFaceApiToken);
    }

    public static IEnumerable<IModelSource> CreateAll(
        Settings settings, Func<HttpProxyConfig?, HttpClient> httpBuilder)
    {
        var sources = new List<IModelSource>();
        var civitai = CreateCivitAi(settings, httpBuilder);
        if (civitai is not null) sources.Add(civitai);
        var hf = CreateHuggingFace(settings, httpBuilder);
        if (hf is not null) sources.Add(hf);
        return sources;
    }

    /// <summary>Lightweight connection probe — GET {baseUrl}/api/whoami-v2 with optional
    /// Authorization: Bearer header. Returns true if 2xx, false if any other status
    /// or exception. Used by Settings UI [测试连接] button.</summary>
    public static async Task<bool> TestConnectionAsync(string baseUrl, string apiToken, int timeoutSeconds = 5)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            if (!string.IsNullOrEmpty(apiToken) && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }
            var resp = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/whoami-v2");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveBaseUrl(bool useMirror, string mirrorUrl, string officialUrl)
        => useMirror && !string.IsNullOrWhiteSpace(mirrorUrl)
            ? mirrorUrl.TrimEnd('/')
            : officialUrl;

    private static HttpProxyConfig? ResolveProxy(bool useProxy, Settings settings)
        => useProxy ? HttpProxyConfig.From(settings) : null;
}