using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 运行时从 PyPI JSON 拉取 PyTorch stable 版本目录(每个版本的发布时间 +
/// 可用 CUDA / CPU 索引 tag)。
///
/// 数据源 <c>https://pypi.org/pypi/torch/json</c> 的 <c>releases</c> 字段
/// 是一个 <c>version → [file, ...]</c> 的字典,每个 file 含
/// <c>filename</c> + <c>upload_time</c>。CUDA / CPU tag 出现在
/// <c>filename</c> 的 <c>+cu118</c> / <c>+cu126</c> / <c>+cpu</c> 片段里。
///
/// 任何失败(HTTP 错 / 解析失败 / <c>releases</c> 字段缺失)统一返回
/// <c>null</c>;调用方应回退到 v0.6.5 hardcoded 默认值,UI 永不空。
/// </summary>
public sealed class PyTorchVersionCatalog
{
    /// <summary>
    /// PyPI torch 包的 JSON API URL。
    /// 响应体大(几十 MB 量级),调用方应配合 <c>PyTorchVersionCache</c>
    /// 做持久化。
    /// </summary>
    public const string PageUrl = "https://pypi.org/pypi/torch/json";

    // 匹配 filename 里的 +cuNNN tag(capture group 1 = "118" / "126")。
    // 严格限制 NNN 是 3 位数字,避免误匹配 +custom / +cublas 等非 CUDA tag。
    private static readonly Regex CudaTagRegex = new(
        @"\+cu(\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex CpuTagRegex = new(
        @"\+cpu",
        RegexOptions.Compiled);

    private readonly HttpClient _http;

    public PyTorchVersionCatalog(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 拉取 PyPI JSON → 解析 → 返回 stable 版本目录。
    /// 任何失败(HTTP 错 / 超时 / JSON 损坏 / <c>releases</c> 缺失)→
    /// 返回 <c>null</c>,不抛。
    /// </summary>
    public async Task<IReadOnlyList<PyTorchVersion>?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(PageUrl, ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or InvalidOperationException
            or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 PyPI JSON 字符串解析 PyTorch 版本目录。
    /// 静态 + internal 便于单测(纯字符串输入,无需 mock HTTP)。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>JSON 解析失败 / 顶层缺失 <c>releases</c> → 返回 <c>null</c>。</item>
    /// <item>PEP 440 pre-release(<c>rc</c> / <c>a</c> / <c>b</c> /
    ///   <c>alpha</c> / <c>beta</c> / <c>pre</c>)、post-release(<c>.postN</c>)、
    ///   dev(<c>.devN</c>)版本号一律过滤。</item>
    /// <item>file 列表为空的版本号跳过。</item>
    /// <item><c>upload_time</c> 解析失败的 file 跳过(不影响同版本其他 file)。</item>
    /// <item><c>CudaVariants</c> 按数字升序、<c>HasCpu</c> = 是否出现 <c>+cpu</c>。</item>
    /// <item>结果按 <c>ReleaseDate</c> 降序(最新在前);同日期再按版本号
    ///   字典序降序(稳定排序)。</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<PyTorchVersion>? Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("releases", out var releasesElement)
                || releasesElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var versions = new List<PyTorchVersion>();

            foreach (var releaseProp in releasesElement.EnumerateObject())
            {
                var versionStr = releaseProp.Name;
                if (string.IsNullOrEmpty(versionStr) || !IsStableRelease(versionStr))
                {
                    continue;
                }

                if (releaseProp.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var fileElements = releaseProp.Value.EnumerateArray().ToList();
                if (fileElements.Count == 0)
                {
                    continue;
                }

                var cudaSet = new SortedSet<int>();
                var hasCpu = false;
                DateTimeOffset? latestUpload = null;

                foreach (var fileEl in fileElements)
                {
                    if (fileEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!fileEl.TryGetProperty("filename", out var filenameEl)
                        || filenameEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var filename = filenameEl.GetString();
                    if (string.IsNullOrEmpty(filename))
                    {
                        continue;
                    }

                    var cudaMatch = CudaTagRegex.Match(filename);
                    if (cudaMatch.Success
                        && int.TryParse(cudaMatch.Groups[1].Value, out var cudaMinor))
                    {
                        cudaSet.Add(cudaMinor);
                    }

                    if (!hasCpu && CpuTagRegex.IsMatch(filename))
                    {
                        hasCpu = true;
                    }

                    if (fileEl.TryGetProperty("upload_time", out var uploadEl)
                        && uploadEl.ValueKind == JsonValueKind.String)
                    {
                        var uploadStr = uploadEl.GetString();
                        if (!string.IsNullOrEmpty(uploadStr)
                            && DateTimeOffset.TryParse(
                                uploadStr,
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal
                                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                out var parsed))
                        {
                            if (latestUpload is null || parsed > latestUpload.Value)
                            {
                                latestUpload = parsed;
                            }
                        }
                    }
                }

                if (latestUpload is null)
                {
                    // 没有任何可解析的 upload_time → 跳过整个版本。
                    continue;
                }

                versions.Add(new PyTorchVersion
                {
                    Version = versionStr,
                    ReleaseDate = latestUpload.Value,
                    CudaVariants = cudaSet.Select(c => "cu" + c).ToArray(),
                    HasCpu = hasCpu,
                });
            }

            return versions
                .OrderByDescending(v => v.ReleaseDate)
                .ThenByDescending(v => v.Version, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// 按 PEP 440 语义判断一个版本号是否为 stable 正式版。
    /// 排除:<c>2.6.0rc1</c>(pre-release)、<c>2.6.0.post1</c>(post)、
    /// <c>2.7.0.dev0</c>(dev)、<c>2.7.0a1</c>(alpha)、<c>2.7.0b2</c>(beta)、
    /// <c>2.7.0pre1</c>(pre)。
    /// </summary>
    /// <remarks>
    /// 实现方式:遍历字符,要求整串只含 ASCII 数字 + 最多两个
    /// <c>.</c>(2 段或 3 段:PEP 440 stable 必是 <c>X.Y</c> 或
    /// <c>X.Y.Z</c>)。任何字母 / 4 段 / 末尾孤立的 <c>.</c> 一律拒掉。
    /// </remarks>
    private static bool IsStableRelease(string version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        var dotCount = 0;
        for (var i = 0; i < version.Length; i++)
        {
            var c = version[i];
            if (c == '.')
            {
                if (i == 0 || i == version.Length - 1 || version[i - 1] == '.')
                {
                    // 开头 / 结尾 / 连续 '.':(".", "2.", ".5", "2..0")。
                    return false;
                }
                dotCount++;
                if (dotCount > 2)
                {
                    return false;
                }
                continue;
            }
            if (c < '0' || c > '9')
            {
                // 任意非数字非点(字母 → rc/post/dev/alpha/beta/pre 等)→ 拒掉。
                return false;
            }
        }

        // 至少 2 段("2.7" 或 "2.7.0")。
        return dotCount >= 1;
    }
}