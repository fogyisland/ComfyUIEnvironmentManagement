using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.21: Per-source factory — reads Settings, picks base URL (mirror or
/// official) and API token, instantiates IModelSource. Returns null for disabled
/// sources; aggregator's internal IsEnabled filter never sees them.</summary>
public static class ModelSourceFactory
{
    public const string CivitAiOfficial = "https://civitai.com";
    public const string HuggingFaceOfficial = "https://huggingface.co";

    public static CivitAiModelSource? CreateCivitAi(Settings settings, HttpClient http)
    {
        if (!settings.ModelSourceCivitAiEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceCivitAiUseMirror,
                                     settings.ModelSourceCivitAiMirrorUrl,
                                     CivitAiOfficial);
        return new CivitAiModelSource(http, baseUrl);
    }

    public static HuggingFaceModelSource? CreateHuggingFace(Settings settings, HttpClient http)
    {
        if (!settings.ModelSourceHuggingFaceEnabled) return null;
        var baseUrl = ResolveBaseUrl(settings.ModelSourceHuggingFaceUseMirror,
                                     settings.ModelSourceHuggingFaceMirrorUrl,
                                     HuggingFaceOfficial);
        return new HuggingFaceModelSource(http, baseUrl, settings.HuggingFaceApiToken);
    }

    public static IEnumerable<IModelSource> CreateAll(Settings settings, HttpClient http)
    {
        var sources = new List<IModelSource>();
        var civitai = CreateCivitAi(settings, http);
        if (civitai is not null) sources.Add(civitai);
        var hf = CreateHuggingFace(settings, http);
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
}
