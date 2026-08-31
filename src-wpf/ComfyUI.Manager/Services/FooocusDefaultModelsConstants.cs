using System.Collections.Generic;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01):Fooocus 默认模型清单 ——
/// 镜像 <c>ENVTemplate/Fooocus/launch.py</c> line 62-67 + line 109-113 的
/// <c>download_models()</c> 启动时自动下载的 4 个文件。
///
/// 文件名 vs URL 后缀不一致(<c>vaeapp_sd15.pth</c> vs URL <c>.pt</c>)是
/// Fooocus 上游 quirk — <c>model_loader.load_file_from_url</c> 第一个参数
/// <c>file_name</c> 决定本地保存名,不是 URL 后缀。本常量严格用 Fooocus
/// launcher 列出的 file_name,跟 T22 plan 决策一致。
///
/// HF 镜像支持镜像 <c>model_loader.py</c> line 18:<c>rawUrl.Replace(
/// "https://huggingface.co", mirrorBase)</c> —— Settings.ModelSourceHuggingFaceUseMirror
/// + ModelSourceHuggingFaceMirrorUrl 配置。
/// </summary>
public static class FooocusDefaultModelsConstants
{
    /// <summary>Target relative path segments(从 env.RootPath 起算)。</summary>
    public const string VaeApproxRelativeDir = "models/vae_approx";
    public const string FooocusExpansionRelativeDir = "models/prompt_expansion/fooocus_expansion";

    /// <summary>
    /// v1.0.0.x (2026-09-01):BED 完成 marker 文件名,跟 .forge_base_env_installed
    /// + .fooocus_base_env_installed 同 pattern。
    /// </summary>
    public const string MarkerFileName = ".fooocus_default_models_installed";

    /// <summary>
    /// 4 个 Fooocus 默认模型清单。**严格镜像 launch.py launch.py 文件名** —
    /// 任何上游变更需同步更新 launcher + 此常量。test 锁文件名 + URL 防 drift。
    /// </summary>
    public static readonly IReadOnlyList<FooocusModelEntry> DefaultModels = new[]
    {
        // launch.py line 63: vae_approx xlvaeapp.pth
        new FooocusModelEntry(
            FileName: "xlvaeapp.pth",
            Url: "https://huggingface.co/lllyasviel/misc/resolve/main/xlvaeapp.pth",
            SubDir: VaeApproxRelativeDir),
        // launch.py line 64: vae_approx vaeapp_sd15.pth — URL .pt,本地 .pth
        // (Fooocus 上游 quirk,model_loader 第 1 参数决定本地保存名)
        new FooocusModelEntry(
            FileName: "vaeapp_sd15.pth",
            Url: "https://huggingface.co/lllyasviel/misc/resolve/main/vaeapp_sd15.pt",
            SubDir: VaeApproxRelativeDir),
        // launch.py line 65-66: vae_approx xl-to-v1_interposer-v4.0.safetensors
        // —— 用户 dev build 触发 WinError 10060 的就是它
        new FooocusModelEntry(
            FileName: "xl-to-v1_interposer-v4.0.safetensors",
            Url: "https://huggingface.co/mashb1t/misc/resolve/main/xl-to-v1_interposer-v4.0.safetensors",
            SubDir: VaeApproxRelativeDir),
        // launch.py line 109-113: fooocus_expansion pytorch_model.bin
        new FooocusModelEntry(
            FileName: "pytorch_model.bin",
            Url: "https://huggingface.co/lllyasviel/misc/resolve/main/fooocus_expansion.bin",
            SubDir: FooocusExpansionRelativeDir),
    };
}

/// <summary>
/// v1.0.0.x (2026-09-01):单条 Fooocus 默认模型元数据 ——
/// <see cref="FileName"/> 是 Fooocus <c>load_file_from_url</c> 决定的本地保存名
/// (launch.py launch.py line 107、line 112 等),<see cref="Url"/> 是 huggingface.co
/// 源 URL,<see cref="SubDir"/> 是 env.RootPath 下的相对目录。
/// </summary>
public record FooocusModelEntry(string FileName, string Url, string SubDir);
