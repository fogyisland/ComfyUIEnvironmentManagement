using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x (2026-09-01) T23b: <see cref="Services.FooocusConfigProbe"/> 探查结果 ——
/// 镜像 <c>ENVTemplate/Fooocus/modules/config.py</c> line 454-477 的 4 个下载 dict +
/// line 191-204 的目标 path 字段。WPF 端拿这份数据预下 Fooocus launcher 启动时
/// 自动下载的 5GB SDXL checkpoint + loras + embeddings + vaes,避免 launch.py
/// line 131-140 网络超时 crash env。
/// </summary>
public sealed record FooocusLauncherConfig(
    /// <summary>checkpoint_downloads —— SDXL/SD checkpoint(.safetensors ~GB)。</summary>
    IReadOnlyDictionary<string, string> CheckpointDownloads,
    /// <summary>lora_downloads —— LoRA(.safetensors ~MB-GB)。</summary>
    IReadOnlyDictionary<string, string> LoraDownloads,
    /// <summary>embeddings_downloads —— textual inversion(.pt/.bin)。</summary>
    IReadOnlyDictionary<string, string> EmbeddingsDownloads,
    /// <summary>vae_downloads —— VAE(.pt/.safetensors)。</summary>
    IReadOnlyDictionary<string, string> VaeDownloads,
    /// <summary>
    /// 5 个目标目录 (file_name → path),全部相对 env.RootPath(FOOOCUS NAMING QUIRK:
    /// paths_checkpoints / paths_loras 是 list,其它是 singular path)。WPF 端
    /// 用 <c>Path.Combine(env.RootPath, relPath)</c> 解析。
    /// </summary>
    IReadOnlyDictionary<string, string> Paths);
