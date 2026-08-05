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
/// 运行时从 PyPI JSON + pytorch.org HTML 拉取 PyTorch stable 版本目录
/// (每个版本的发布时间 + CUDA / CPU 索引 tag)。
///
/// 数据源:
/// <list type="number">
/// <item><c>https://pypi.org/pypi/torch/json</c> — <c>releases</c> 字典;
///   每个 file 含 <c>filename</c> + <c>upload_time</c>。PyPI 提供版本号 +
///   发布时间 + CPU wheel 检测 (<c>filename</c> 含 <c>+cpu</c>)。
///   注:PyPI 的 <c>torch</c> 包不再发布 CUDA 标记的 wheel — 只有 CPU
///   wheel。所以 CUDA 变体必须从 pytorch.org HTML 来。</item>
/// <item><c>https://pytorch.org/get-started/locally/</c> —
///   <c>var pt_version_map = { "release": { "cuda.x": ..., "cuda.y": ..., "cuda.z": ... } }</c>
///   JavaScript 字面量。<c>cuda.x</c> / <c>cuda.y</c> / <c>cuda.z</c> 是
///   扁平 key(不是嵌套 <c>"cuda":{"x":...}</c>),由 letter 映射到
///   <c>cuNNN</c> tag(见 <see cref="CudaLetterToTag"/>)。</item>
/// </list>
///
/// 任何失败(HTTP 错 / 解析失败 / <c>releases</c> 字段缺失)统一返回
/// <c>null</c>;调用方应回退到 v0.6.5 hardcoded 默认值,UI 永不空。
/// </summary>
/// <remarks>
/// 非 sealed:允许 <c>PyTorchVersionDirectoryTests</c> 用
/// <see cref="FetchAsync"/> 的 in-memory 子类来验证编排逻辑,避开
/// 真 <c>HttpClient</c>。<see cref="FetchAsync"/> 是 <c>virtual</c> 的;
/// <see cref="ParseCudaVariantsFromHtml"/> / <see cref="ParsePypiJson"/>
/// 仍保持 <c>internal static</c>(纯函数解析,不需要 override)。
/// </remarks>
public class PyTorchVersionCatalog
{
    /// <summary>
    /// PyPI torch 包的 JSON API URL。响应体大(几十 MB 量级),调用方
    /// 应配合 <c>PyTorchVersionCache</c> 做持久化。
    /// </summary>
    public const string PyPiPageUrl = "https://pypi.org/pypi/torch/json";

    /// <summary>
    /// pytorch.org "Get Started" 页面 URL。页面内嵌
    /// <c>var pt_version_map = {...}</c> JavaScript 字面量,
    /// 用 regex 抽取 <c>release.cuda.x</c> / <c>cuda.y</c> / <c>cuda.z</c>
    /// key 推导可用 CUDA 变体列表。
    /// </summary>
    public const string PytorchOrgPageUrl = "https://pytorch.org/get-started/locally/";

    // regex:从整个 pt_version_map 字符串里抽出所有 cuda.x / cuda.y /
    // cuda.z entry(扁平结构,值是 ["cuda","<version>"] 二元组)。
    // pt_version_map.nightly 也是同样 flat 结构,所以先抽 release 块边界,
    // 再在块内找 cuda entry。
    // 块边界:从 "release":{ 到 下一个顶层 },(用非贪婪找最近的 })。
    // 因为 release 块里没有嵌套 { (key 都是数组值如 [...]),
    // 所以 "[^}]*" 可以安全吃到块尾。
    // 块内 cuda entry regex 抓整个 ["cuda","12.6"]:
    //   Group "letter" = "x" / "y" / "z"(目前 pytorch.org 固定这三个代号)
    //   Group "ver" = "12.6" / "13.0" 等真实 CUDA 数字版本
    // 拼装 cuNNN tag 时把 "12.6" 去点 → "126" → "cu126"。
    private static readonly Regex ReleaseBlockRegex = new(
        @"""release""\s*:\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex ReleaseCudaEntryRegex = new(
        @"""cuda\.(?<letter>[xyz])""\s*:\s*\[\s*""cuda""\s*,\s*""(?<ver>\d+\.\d+)""\s*\]",
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
    /// 并行拉取 PyPI JSON + pytorch.org HTML → 合并解析 → 返回 stable
    /// 版本目录(每个版本带 CudaVariants + HasCpu)。
    /// 任何失败(HTTP 错 / 超时 / JSON 损坏 / HTML regex miss /
    /// <c>releases</c> 缺失)→ 返回 <c>null</c>,不抛。
    /// </summary>
    /// <remarks>
    /// 标记 <c>virtual</c> 是为了允许测试用 in-memory 子类替换,避开
    /// 真 <c>HttpClient</c>。生产代码调用方(<c>PyTorchVersionDirectory</c>)
    /// 期望的契约不变:成功返回 non-null stable 列表,失败返回 null。
    /// </remarks>
    public virtual async Task<IReadOnlyList<PyTorchVersion>?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            // 并行发起两个 GET。任一失败 → 整体回退 null。
            var pypiTask = _http.GetStringAsync(PyPiPageUrl, ct);
            var pytorchOrgTask = _http.GetStringAsync(PytorchOrgPageUrl, ct);

            await Task.WhenAll(pypiTask, pytorchOrgTask).ConfigureAwait(false);

            var json = await pypiTask.ConfigureAwait(false);
            var html = await pytorchOrgTask.ConfigureAwait(false);

            var cudaVariants = ParseCudaVariantsFromHtml(html);
            return ParsePypiJson(json, cudaVariants);
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
    /// 从 pytorch.org HTML 提取 <c>pt_version_map.release</c> 块里的
    /// <c>cuda.x</c> / <c>cuda.y</c> / <c>cuda.z</c> entry,从
    /// <c>["cuda","12.6"]</c> 二元组里的 version 字段拼出 <c>cuNNN</c> tag。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>HTML 空 → 空列表(不抛)。</item>
    /// <item>没有 <c>"release"</c> 块 → 空列表(nightly 单独存在不算)。</item>
    /// <item>只 release 块里有 cuda.{letter} entry 才计入;nightly 块不算。</item>
    /// <item>cuNNN tag 从 entry 第二字段(如 <c>"12.6"</c>)去点拼装
    ///   (<c>"cu"</c> + <c>"126"</c>),所以 pytorch.org 改 CUDA 数字
    ///   自动反映(无需硬编码 letter → tag 映射)。</item>
    /// <item>entry 不含第二字段 / version 不是 <c>MAJOR.MINOR</c> 数字 →
    ///   静默跳过(防御 pytorch.org 改格式)。</item>
    /// <item>结果按 pytorch.org HTML 出现顺序排列(cuda.x → cuda.y → cuda.z),
    ///   与 stable CUDA 推荐顺序一致。</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<string> ParseCudaVariantsFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return Array.Empty<string>();
        }

        // 第一步:抽出 release:{...} 块内容。如果 release 块不存在 → 空。
        var blockMatch = ReleaseBlockRegex.Match(html);
        if (!blockMatch.Success)
        {
            return Array.Empty<string>();
        }

        var releaseBody = blockMatch.Groups["body"].Value;

        // 第二步:在 release 块内容里找所有 cuda.x/y/z entry,正则抓
        // 整个 ["cuda","12.6"] 二元组。因为 release 块里值都是数组,
        // 没有嵌套 {} ,所以 [^}]* 把 release 块切到下一个 } 是安全的
        // (nightly 块 cuda key 在 sibling 对象里,不会被误抓)。
        var matches = ReleaseCudaEntryRegex.Matches(releaseBody);
        if (matches.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (Match m in matches)
        {
            var ver = m.Groups["ver"].Value;
            if (string.IsNullOrEmpty(ver))
            {
                // 防御:pytorch.org 改了 entry 结构(没 version 字段) → 跳过。
                continue;
            }
            var tag = "cu" + ver.Replace(".", "");
            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }
        return result;
    }

    /// <summary>
    /// 从 PyPI JSON 字符串解析 PyTorch 版本目录,把外部传入的
    /// <paramref name="cudaVariants"/> 列表(来自 pytorch.org HTML)
    /// 赋给每个稳定版本。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>JSON 解析失败 / 顶层缺失 <c>releases</c> → 返回 <c>null</c>。</item>
    /// <item>PEP 440 pre-release(<c>rc</c> / <c>a</c> / <c>b</c> /
    ///   <c>alpha</c> / <c>beta</c> / <c>pre</c>)、post-release(<c>.postN</c>)、
    ///   dev(<c>.devN</c>)版本号一律过滤。</item>
    /// <item>file 列表为空的版本号跳过。</item>
    /// <item><c>upload_time</c> 解析失败的 file 跳过(不影响同版本其他 file)。</item>
    /// <item>没有任何可解析 <c>upload_time</c> 的版本号跳过。</item>
    /// <item><see cref="PyTorchVersion.CudaVariants"/> 直接取传入的
    ///   <paramref name="cudaVariants"/>(已排序);不来自 filename。
    ///   <see cref="PyTorchVersion.HasCpu"/> = 是否有 wheel 含 <c>+cpu</c>。</item>
    /// <item>结果按 <see cref="PyTorchVersion.ReleaseDate"/> 降序
    ///   (最新在前);同日期再按版本号字典序降序(稳定排序)。</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<PyTorchVersion>? ParsePypiJson(
        string json,
        IReadOnlyList<string> cudaVariants)
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

            // Defensive copy so callers can mutate their input without
            // affecting the result.
            var cudaSnapshot = cudaVariants ?? Array.Empty<string>();

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
                    CudaVariants = cudaSnapshot,
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