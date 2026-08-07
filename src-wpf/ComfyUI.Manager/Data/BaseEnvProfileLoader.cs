using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// BaseEnvProfileLoader:从 &lt;appDataDir&gt;/base_env_profiles.json 读取 profile 列表。
/// 文件缺失 / 解析失败 / 空内容 → 走 <see cref="GetLiveDefaultsAsync"/>
/// (运行时拉取 PyTorch stable 版本生成 6 个默认 profile);拉取失败再回退
/// <see cref="GetHardcodedDefaults"/>(v0.6.5 硬编码 5 个)。
/// 设计上宁可回退到默认值也不要因为 JSON 损坏 / 网络断就让 UI 空掉。
/// </summary>
public class BaseEnvProfileLoader
{
    public const string FileName = "base_env_profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _appDataDir;
    private readonly string? _cacheDir;
    private readonly HttpClient? _http;

    /// <summary>
    /// </summary>
    /// <param name="appDataDir">存放 base_env_profiles.json 的目录。</param>
    /// <param name="cacheDir">PyTorch 版本缓存目录;null → 不拉 live,只走 hardcoded。</param>
    /// <param name="http">共享 HttpClient;null → 不拉 live,只走 hardcoded。</param>
    public BaseEnvProfileLoader(
        string appDataDir,
        string? cacheDir = null,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(appDataDir))
        {
            throw new ArgumentException("appDataDir must be non-empty", nameof(appDataDir));
        }
        _appDataDir = appDataDir;
        _cacheDir = cacheDir;
        _http = http;
    }

    /// <summary>
    /// 从 <c>&lt;appDataDir&gt;/base_env_profiles.json</c> 加载 profiles。
    /// 文件缺失 / 解析失败 / 空字符串 → 走 <see cref="GetLiveDefaultsAsync"/>。
    /// 有效空数组 "[]" 视为用户明确选择空列表,直接返回(不回退)。
    /// </summary>
    public virtual async Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_appDataDir, FileName);
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<BaseEnvProfile>>(json, JsonOptions);
                    if (parsed != null) return parsed;
                }
                catch (JsonException)
                {
                    // 损坏 JSON → 静默回退到 live/hardcoded 默认值。
                }
            }
        }

        return await GetLiveDefaultsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 运行时拉取 PyTorch stable 版本生成 6 个默认 profile。
    /// 无 HttpClient / 无 cache 目录 → 直接回退 <see cref="GetHardcodedDefaults"/>。
    /// cache 命中(1h TTL 内)→ 用 cached 生成;否则拉 HTTP,失败 → hardcoded。
    /// </summary>
    public async Task<IReadOnlyList<BaseEnvProfile>> GetLiveDefaultsAsync(CancellationToken ct = default)
    {
        if (_http == null || _cacheDir == null)
        {
            return GetHardcodedDefaults();
        }

        var cache = new PyTorchVersionCache(_cacheDir);
        var cached = await cache.TryReadAsync(ct).ConfigureAwait(false);
        if (cached != null)
        {
            return BuildLiveDefaults(cached);
        }

        var fetcher = new PyTorchVersionFetcher(_http);
        var fresh = await fetcher.FetchAsync(ct).ConfigureAwait(false);
        if (fresh == null)
        {
            return GetHardcodedDefaults();
        }

        await cache.WriteAsync(fresh, ct).ConfigureAwait(false);
        return BuildLiveDefaults(fresh);
    }

    /// <summary>
    /// v0.6.5 硬编码 7 个默认 profile(fetcher 失败 / 无 HTTP 时回退)。
    /// 先后顺序即 UI 展示顺序;cu118 第一个是历史默认(v0.6.5 之前一直
    /// 是这个,保持兼容性 — 已有 env 的 BED 列还能显示)。
    /// v0.6.5.22 (Fix Round 1):<c>TorchVersion</c> 从 "2.1.0" 升到 "2.4.1"
    /// —— comfy_kitchen 用了 <c>@torch.library.custom_op</c>(PyTorch 2.4 引入),
    /// 装 2.1.x 后启动 ComfyUI 抛 <c>AttributeError: module 'torch.library'
    /// has no attribute 'custom_op'</c>。nightly 字面量 "nightly" 不变;
    /// CPU profile 也跟着升 2.4.1。
    /// v0.6.5.22: 返回前过 <see cref="MarkIncompatibleOlderVersions"/>
    /// 标 <c>torch &lt; 2.4</c> 的 profile 不推荐(comfy_kitchen 不兼容)。
    /// </summary>
    public virtual IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults()
    {
        return MarkIncompatibleOlderVersions(new List<BaseEnvProfile>
        {
            new()
            {
                Id = "pytorch-2.4.1-cu118-stable",
                Name = "PyTorch 2.4.1 + CUDA 11.8 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 11.8,带 xformers",
                TorchVersion = "2.4.1",
                CudaVersion = "cu118",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.4.1-cu121-stable",
                Name = "PyTorch 2.4.1 + CUDA 12.1 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 12.1,带 xformers",
                TorchVersion = "2.4.1",
                CudaVersion = "cu121",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.4.1-cu124-stable",
                Name = "PyTorch 2.4.1 + CUDA 12.4 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 12.4,带 xformers",
                TorchVersion = "2.4.1",
                CudaVersion = "cu124",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.4.1-cu126-stable",
                Name = "PyTorch 2.4.1 + CUDA 12.6 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 12.6,带 xformers",
                TorchVersion = "2.4.1",
                CudaVersion = "cu126",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.4.1-cu128-stable",
                Name = "PyTorch 2.4.1 + CUDA 12.8 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 12.8,带 xformers",
                TorchVersion = "2.4.1",
                CudaVersion = "cu128",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-nightly-cu121",
                Name = "PyTorch Nightly + CUDA 12.1",
                Description = "PyTorch nightly,搭配 CUDA 12.1(不带 xformers)",
                TorchVersion = "nightly",
                CudaVersion = "cu121",
                Channel = "nightly",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            },
            new()
            {
                Id = "pytorch-2.4.1-cpu",
                Name = "PyTorch 2.4.1 (CPU only)",
                Description = "仅 CPU 的 PyTorch 2.4.1,适合无 NVIDIA 显卡环境",
                TorchVersion = "2.4.1",
                CudaVersion = "cpu",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            },
        });
    }

    /// <summary>
    /// 用运行时拉取的 stable 版本生成 7 个默认 profile(spec §4.5):
    /// 5 个 stable CUDA(cu118/cu121/cu124/cu126/cu128)+ 1 个 nightly cu126 + 1 个 CPU。
    /// 所有 stable profile 共用 <paramref name="v"/>.Stable;nightly 保留字面量 "nightly"。
    /// v0.6.5.18 加 cu128(pytorch.org wheel 路径有,Get Started 页 release 块不列,但属于
    /// 当前 PyTorch 2.1 wheel 实际可用 CUDA 范围)。
    /// v0.6.5.22: 返回前过 <see cref="MarkIncompatibleOlderVersions"/>
    /// 标 <c>torch &lt; 2.4</c> 的 profile 不推荐(comfy_kitchen 不兼容)。
    /// v0.6.5.22 (Fix Round 1):如果 <paramref name="v"/>.Stable &lt; 2.4
    /// (pytorch.org 的 <c>latest_stable</c> 字段 stale 或异常),在返回的
    /// list 顶部 prepend 一个 hardcoded <c>torch==2.4.1+cu118</c> profile —
    /// 确保用户点"新建环境"时 dropdown 第一项是 comfy_kitchen 兼容的版本,
    /// 不会因为网络 stale 兜底到 2.1.0 然后启动炸。
    /// </summary>
    private static IReadOnlyList<BaseEnvProfile> BuildLiveDefaults(PyTorchLiveVersions v)
    {
        var stableProfile = new Func<string, string, BaseEnvProfile>((cuda, cudaLabel) => new BaseEnvProfile
        {
            Id = $"pytorch-{v.Stable}-{cuda}-stable",
            Name = $"PyTorch {v.Stable} + CUDA {cudaLabel} (stable)",
            Description = $"稳定版 PyTorch {v.Stable},搭配 CUDA {cudaLabel},带 xformers",
            TorchVersion = v.Stable,
            CudaVersion = cuda,
            Channel = "stable",
            Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
        });

        var profiles = new List<BaseEnvProfile>
        {
            stableProfile("cu118", "11.8"),
            stableProfile("cu121", "12.1"),
            stableProfile("cu124", "12.4"),
            stableProfile("cu126", "12.6"),
            stableProfile("cu128", "12.8"),
            new()
            {
                Id = "pytorch-nightly-cu126",
                Name = "PyTorch Nightly + CUDA 12.6",
                Description = "PyTorch nightly,搭配 CUDA 12.6(不带 xformers)",
                TorchVersion = "nightly",
                CudaVersion = "cu126",
                Channel = "nightly",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            },
            new()
            {
                Id = $"pytorch-{v.Stable}-cpu",
                Name = $"PyTorch {v.Stable} (CPU only)",
                Description = $"仅 CPU 的 PyTorch {v.Stable},适合无 NVIDIA 显卡环境",
                TorchVersion = v.Stable,
                CudaVersion = "cpu",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            },
        };

        // 防御:pytorch.org 的 latest_stable 可能 stale(比如它缓存旧版本
        // HTML),如果返回的 stable < 2.4,dropdown 第一项就会是不兼容版本,
        // 用户不读文字直接 Enter 会触发 comfy_kitchen 错误。prepend 一个
        // hardcoded 2.4.1+cu118 顶在首位让默认一定兼容。
        if (IsStableIncompatible(v.Stable))
        {
            profiles.Insert(0, new BaseEnvProfile
            {
                Id = "pytorch-2.4.1-cu118-stable",
                Name = "PyTorch 2.4.1 + CUDA 11.8 (stable)",
                Description = "稳定版 PyTorch 2.4.1,搭配 CUDA 11.8,带 xformers(comfy_kitchen 兼容)",
                TorchVersion = "2.4.1",
                CudaVersion = "cu118",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            });
        }

        return MarkIncompatibleOlderVersions(profiles);
    }

    /// <summary>
    /// 判定 <paramref name="stable"/> 字符串是不是 torch &lt; 2.4
    /// (用 regex 抓 MAJOR.MINOR)。nightly / 空 / 无法解析 → false
    /// (不前置,nightly 永远新,空无法判定)。
    /// </summary>
    private static bool IsStableIncompatible(string? stable)
    {
        if (string.IsNullOrWhiteSpace(stable)) return false;
        var match = Regex.Match(stable, @"(\d+)\.(\d+)");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        return major < 2 || (major == 2 && minor < 4);
    }

    /// <summary>
    /// 根据指定 PyTorch 版本生成 BED profile 列表(Task 4:多版本 BED)。
    /// 无 metadata → 走 <see cref="GetLiveDefaultsAsync"/> 拉 stable defaults 再 filter;
    /// 有 metadata → 按 CudaVariants + HasCpu 直接生成。
    /// nightly 永远是单个 cu126 profile。
    /// </summary>
    public Task<IReadOnlyList<BaseEnvProfile>> LoadProfilesForVersionAsync(
        string version,
        CancellationToken ct = default)
    {
        return LoadProfilesForVersionAsync(version, metadata: null, ct);
    }

    /// <summary>
    /// metadata-aware overload:<paramref name="metadata"/> 非空时按其
    /// <c>CudaVariants</c> + <c>HasCpu</c> 精确生成;为 null 时回退到 live
    /// defaults + filter。
    /// </summary>
    public async Task<IReadOnlyList<BaseEnvProfile>> LoadProfilesForVersionAsync(
        string version,
        PyTorchVersion? metadata,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("version 必须非空", nameof(version));
        }

        // nightly 永远是单个 cu126 profile,无视 metadata。
        if (version == PyTorchVersionDirectory.NightlyVersion)
        {
            return new List<BaseEnvProfile>
            {
                new()
                {
                    Id = "pytorch-nightly-cu126",
                    Name = "PyTorch Nightly + CUDA 12.6",
                    Description = "PyTorch nightly,搭配 CUDA 12.6(不带 xformers)",
                    TorchVersion = PyTorchVersionDirectory.NightlyVersion,
                    CudaVersion = "cu126",
                    Channel = PyTorchVersionDirectory.NightlyVersion,
                    Packages = new List<string> { "torch", "torchaudio", "torchvision" },
                },
            };
        }

        if (metadata != null)
        {
            return BuildStableProfilesForVersion(version, metadata);
        }

        // 无 metadata:走 GetLiveDefaultsAsync(filter 到目标 version)。
        var defaults = await GetLiveDefaultsAsync(ct).ConfigureAwait(false);
        var filtered = defaults
            .Where(p => string.Equals(p.TorchVersion, version, StringComparison.Ordinal))
            .ToList();

        // 如果 live defaults 都没匹配到 version(例如 hardcoded 写死 2.1.0,但用户选了
        // 2.5.1,cache 也没命中 → 全 stale 默认值),建一个最简 stable 兜底:
        // 4 CUDA(cu118/cu121/cu124/cu126)+ 1 CPU,都带这个 version。
        if (filtered.Count == 0)
        {
            filtered = BuildStableFallbackProfiles(version).ToList();
        }

        return filtered;
    }

    /// <summary>
    /// 用 metadata 里的 CudaVariants + HasCpu 生成 stable profile 列表。
    /// 每个 CUDA tag 一个 profile;HasCpu=true 时追加一个 CPU profile。
    /// </summary>
    private static IReadOnlyList<BaseEnvProfile> BuildStableProfilesForVersion(
        string version, PyTorchVersion metadata)
    {
        var profiles = new List<BaseEnvProfile>();

        foreach (var cuda in metadata.CudaVariants)
        {
            var label = CudaTagToLabel(cuda);
            profiles.Add(new BaseEnvProfile
            {
                Id = $"pytorch-{version}-{cuda}-stable",
                Name = $"PyTorch {version} + CUDA {label} (stable)",
                Description = $"稳定版 PyTorch {version},搭配 CUDA {label},带 xformers",
                TorchVersion = version,
                CudaVersion = cuda,
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            });
        }

        if (metadata.HasCpu)
        {
            profiles.Add(new BaseEnvProfile
            {
                Id = $"pytorch-{version}-cpu",
                Name = $"PyTorch {version} (CPU only)",
                Description = $"仅 CPU 的 PyTorch {version},适合无 NVIDIA 显卡环境",
                TorchVersion = version,
                CudaVersion = "cpu",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            });
        }

        return profiles;
    }

    /// <summary>
    /// 当 live defaults 没有匹配版本时的兜底:固定 4 个 CUDA + 1 个 CPU,
    /// 都标 <paramref name="version"/>。例如选了 2.5.1 但 hardcoded 是 2.1.0。
    /// </summary>
    private static IEnumerable<BaseEnvProfile> BuildStableFallbackProfiles(string version)
    {
        var cudaTags = new[] { ("cu118", "11.8"), ("cu121", "12.1"), ("cu124", "12.4"), ("cu126", "12.6") };
        foreach (var (cuda, label) in cudaTags)
        {
            yield return new BaseEnvProfile
            {
                Id = $"pytorch-{version}-{cuda}-stable",
                Name = $"PyTorch {version} + CUDA {label} (stable)",
                Description = $"稳定版 PyTorch {version},搭配 CUDA {label},带 xformers",
                TorchVersion = version,
                CudaVersion = cuda,
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            };
        }

        yield return new BaseEnvProfile
        {
            Id = $"pytorch-{version}-cpu",
            Name = $"PyTorch {version} (CPU only)",
            Description = $"仅 CPU 的 PyTorch {version},适合无 NVIDIA 显卡环境",
            TorchVersion = version,
            CudaVersion = "cpu",
            Channel = "stable",
            Packages = new List<string> { "torch", "torchaudio", "torchvision" },
        };
    }

    /// <summary>
    /// cu-tag → CUDA label("cu118" → "11.8","cu126" → "12.6")。
    /// 解析失败时回退原 tag(避免抛)。
    /// </summary>
    private static string CudaTagToLabel(string cuda)
    {
        if (cuda.StartsWith("cu", StringComparison.Ordinal) && cuda.Length >= 4)
        {
            var digits = cuda.Substring(2);
            if (digits.Length == 3
                && char.IsDigit(digits[0])
                && char.IsDigit(digits[1])
                && char.IsDigit(digits[2]))
            {
                return $"{digits[0]}{digits[1]}.{digits[2]}";
            }
        }
        return cuda;
    }

    /// <summary>
    /// 给 <paramref name="profiles"/> 中 <c>TorchVersion &lt; 2.4</c> 的
    /// stable profile 加 <c>(不推荐 — comfy_kitchen 不兼容)</c> 后缀到 Name。
    /// 纯函数:不修改输入 list,生成新 list。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>comfy_kitchen 用了 <c>@torch.library.custom_op</c>,该 decorator
    ///   在 PyTorch 2.4 引入(参见 v0.6.5.22 plan G5)。装 torch 2.1/2.2/2.3
    ///   后启动 ComfyUI 会抛 <c>AttributeError: module 'torch.library' has no
    ///   attribute 'custom_op'</c>。</item>
    /// <item>判定依据:regex 抓 <c>TorchVersion</c> 前两段作为 <c>major</c>/
    ///   <c>minor</c>;<c>major &lt; 2</c> 或 <c>major == 2 &amp;&amp; minor &lt; 4</c>
    ///   视为不兼容。</item>
    /// <item>无法解析(<c>null</c> / regex miss / nightly 字面量)→ 原样保留
    ///   (nightly 永远新于 2.4,无需标记)。</item>
    /// <item>v0.6.5.22 (Fix Round 1):只修改 <c>Name</c> 字段,<c>Id</c> 不动。
    ///   原因:<c>Id</c> 会在安装时被持久化到 SQLite
    ///   (<c>Environment.BedProfileId</c>),改了 Id 之后,老 env 行里存的
    ///   是 "pytorch-2.1-cu118-stable",但新生成的 profile Id 是
    ///   "pytorch-2.1-cu118-stable (不推荐 — ...)" — 这不影响功能
    ///   (BedProfileId 只是显示用,不做 Id 比较),但 BED 列会显示带后缀
    ///   的旧 Id,看起来很怪。所以保持 Id 干净,只在 Name(下拉框显示
    ///   用,<c>BaseEnvView.xaml:48 Text="{Binding Name}"</c>)上做警告。</item>
    /// <item>不修改 <c>TorchVersion</c> / <c>CudaVersion</c> / <c>Channel</c>
    ///   / <c>Packages</c>,所以 <see cref="BaseEnvProfile.BuildPipArgs"/>
    ///   不会受影响 — pip install 命令还是 pin 实际 torch 版本。</item>
    /// <item>user override JSON 文件(<c>base_env_profiles.json</c>)由
    ///   <see cref="LoadAsync"/> 直接反序列化,不经此方法 — 用户在 JSON 里
    ///   明知 2.1 不可用还写,UI 应忠实显示用户选择,不强行加后缀。</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<BaseEnvProfile> MarkIncompatibleOlderVersions(
        IReadOnlyList<BaseEnvProfile> profiles)
    {
        const string Suffix = " (不推荐 — comfy_kitchen 不兼容)";

        var result = new List<BaseEnvProfile>(profiles.Count);
        foreach (var p in profiles)
        {
            if (string.IsNullOrWhiteSpace(p.TorchVersion))
            {
                // nightly / null → 跳过(no regex parse possible)
                result.Add(p);
                continue;
            }

            var match = Regex.Match(p.TorchVersion, @"(\d+)\.(\d+)");
            if (!match.Success)
            {
                // nightly 字面量等非 MAJOR.MINOR 形式 → 原样保留
                result.Add(p);
                continue;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);

            if (major < 2 || (major == 2 && minor < 4))
            {
                // 重建 profile 副本,只改 Name(Id 保持不变,见 remarks)
                result.Add(new BaseEnvProfile
                {
                    Id = p.Id,
                    Name = p.Name + Suffix,
                    Description = p.Description,
                    TorchVersion = p.TorchVersion,
                    CudaVersion = p.CudaVersion,
                    Channel = p.Channel,
                    Packages = new List<string>(p.Packages),
                    ExtraArgs = p.ExtraArgs,
                });
            }
            else
            {
                result.Add(p);
            }
        }
        return result;
    }
}
