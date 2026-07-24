using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 运行时从 pytorch.org/get-started/locally/ 拉取实际 stable 版本 + 校验
/// nightly cu126 索引是否存在。结果由 <c>PyTorchVersionCache</c> 持久化,
/// <c>BaseEnvProfileLoader</c> 用来生成 6 个默认 profile
/// (cu118/cu121/cu124/cu126/cpu + nightly cu126)。
/// </summary>
/// <remarks>
/// 任何失败(HTTP 错 / 超时 / 解析失败 / HTML 字段缺失)统一返回
/// <c>null</c>;调用方应回退到 v0.6.5 hardcoded 默认值,UI 永不空。
/// </remarks>
public sealed class PyTorchVersionFetcher
{
    /// <summary>
    /// pytorch.org "Get Started" 页面的 HTML URL。
    /// 页面内嵌 <c>var pt_published_versions = {...}</c> /
    /// <c>var pt_version_map = {...}</c> JavaScript 字面量,
    /// 用 regex 抽取 <c>latest_stable</c> 版本号 + <c>nightly.cuda.x</c>
    /// 索引存在标记。
    /// </summary>
    public const string PageUrl = "https://pytorch.org/get-started/locally/";

    // regex A:从 pt_published_versions 内抽出 latest_stable(cuda.x key)
    // 顺序匹配 var pt_published_versions = { 后第一个 stable,pip,linux,cuda.x,python 行。
    // Group 1 = 版本号("2.13.0")
    private static readonly Regex StableRegex = new(
        @"var pt_published_versions = \{[^{}]*?""stable,pip,linux,cuda\.x,python"":\s*""[^""]*?torch==(\d+\.\d+\.\d+)",
        RegexOptions.Compiled);

    // regex B:验证 pt_version_map.nightly.cuda.x 存在(确认 cu126 nightly 索引可装)
    // 命中 = HasNightlyCu126 = true
    private static readonly Regex NightlyCudaXRegex = new(
        @"""nightly"":\s*\{\s*""cpu"":[^{}]*?""cuda"":\s*\{\s*""x"":",
        RegexOptions.Compiled);

    private readonly HttpClient _http;

    public PyTorchVersionFetcher(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 拉取 pytorch.org HTML → 提取 Stable + 验证 nightly cu126。
    /// 任何失败(HTTP 错 / 超时 / 解析失败)→ 返回 <c>null</c>,不抛。
    /// </summary>
    public async Task<PyTorchLiveVersions?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(PageUrl, ct).ConfigureAwait(false);
            return Parse(html);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 pytorch.org HTML 提取 <see cref="PyTorchLiveVersions"/>。
    /// 静态 + internal 便于单测(纯字符串输入,无需 mock HTTP)。
    /// </summary>
    /// <remarks>
    /// 两个 regex 任一不命中 → 返回 <c>null</c>;调用方应回退 hardcoded 默认值。
    /// </remarks>
    internal static PyTorchLiveVersions? Parse(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var stableMatch = StableRegex.Match(html);
        if (!stableMatch.Success)
        {
            return null;
        }

        var stable = stableMatch.Groups[1].Value;
        if (string.IsNullOrEmpty(stable))
        {
            return null;
        }

        var hasNightlyCu126 = NightlyCudaXRegex.IsMatch(html);
        if (!hasNightlyCu126)
        {
            // 理论上 PyTorch 不会撤掉 cu126 nightly;若真没了,保守起见回退 null
            // (上层 GetLiveDefaultsAsync 会 catch → 走 GetHardcodedDefaults)。
            return null;
        }

        return new PyTorchLiveVersions
        {
            Stable = stable,
            HasNightlyCu126 = true,
            FetchedAt = DateTimeOffset.UtcNow,
        };
    }
}