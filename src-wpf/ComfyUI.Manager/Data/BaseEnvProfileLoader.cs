using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// BaseEnvProfileLoader:从 &lt;appDataDir&gt;/base_env_profiles.json 读取 profile 列表。
/// 文件缺失 / 解析失败 / 空内容 → 返回 5 个内置默认 profile。
/// 设计上宁可回退到默认值也不要因为 JSON 损坏就让 UI 空掉(用户可在 UI 里
/// 再手动选回默认或者编辑 profile 文件后重启)。
/// </summary>
public sealed class BaseEnvProfileLoader
{
    public const string FileName = "base_env_profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _appDataDir;

    public BaseEnvProfileLoader(string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(appDataDir))
        {
            throw new ArgumentException("appDataDir must be non-empty", nameof(appDataDir));
        }
        _appDataDir = appDataDir;
    }

    /// <summary>
    /// 从 <c>&lt;appDataDir&gt;/base_env_profiles.json</c> 加载 profiles。
    /// 文件缺失 / 解析失败 / 空字符串 → 回退 <see cref="GetDefaults"/>。
    /// 有效空数组 "[]" 视为用户明确选择空列表,直接返回(不回退)。
    /// </summary>
    public async Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_appDataDir, FileName);
        if (!File.Exists(path))
        {
            return GetDefaults();
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return GetDefaults();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<BaseEnvProfile>>(json, JsonOptions);
            return parsed ?? GetDefaults();
        }
        catch (JsonException)
        {
            // 损坏 JSON → 静默回退到默认值;调用方拿不到错误,UI 仍能展示。
            return GetDefaults();
        }
    }

    /// <summary>
    /// 内置 5 个默认 profile。先后顺序即 UI 展示顺序。
    /// </summary>
    public IReadOnlyList<BaseEnvProfile> GetDefaults()
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
}