using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
public sealed class BaseEnvProfileLoader
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
    public async Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
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
    /// v0.6.5 硬编码 5 个默认 profile(fetcher 失败 / 无 HTTP 时回退)。
    /// 先后顺序即 UI 展示顺序。字面量 "2.1.0" / "nightly" 刻意保留。
    /// </summary>
    public IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults()
    {
        return new List<BaseEnvProfile>
        {
            new()
            {
                Id = "pytorch-2.1-cu118-stable",
                Name = "PyTorch 2.1 + CUDA 11.8 (stable)",
                Description = "稳定版 PyTorch 2.1.0,搭配 CUDA 11.8,带 xformers",
                TorchVersion = "2.1.0",
                CudaVersion = "cu118",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.1-cu121-stable",
                Name = "PyTorch 2.1 + CUDA 12.1 (stable)",
                Description = "稳定版 PyTorch 2.1.0,搭配 CUDA 12.1,带 xformers",
                TorchVersion = "2.1.0",
                CudaVersion = "cu121",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
            },
            new()
            {
                Id = "pytorch-2.1-cu124-stable",
                Name = "PyTorch 2.1 + CUDA 12.4 (stable)",
                Description = "稳定版 PyTorch 2.1.0,搭配 CUDA 12.4,带 xformers",
                TorchVersion = "2.1.0",
                CudaVersion = "cu124",
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
                Id = "pytorch-2.1-cpu",
                Name = "PyTorch 2.1 (CPU only)",
                Description = "仅 CPU 的 PyTorch 2.1.0,适合无 NVIDIA 显卡环境",
                TorchVersion = "2.1.0",
                CudaVersion = "cpu",
                Channel = "stable",
                Packages = new List<string> { "torch", "torchaudio", "torchvision" },
            },
        };
    }

    /// <summary>
    /// 用运行时拉取的 stable 版本生成 6 个默认 profile(spec §4.5):
    /// 4 个 stable CUDA(cu118/cu121/cu124/cu126)+ 1 个 nightly cu126 + 1 个 CPU。
    /// 所有 stable profile 共用 <paramref name="v"/>.Stable;nightly 保留字面量 "nightly"。
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

        return new List<BaseEnvProfile>
        {
            stableProfile("cu118", "11.8"),
            stableProfile("cu121", "12.1"),
            stableProfile("cu124", "12.4"),
            stableProfile("cu126", "12.6"),
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
}
