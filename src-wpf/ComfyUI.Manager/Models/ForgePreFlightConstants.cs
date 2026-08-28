using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x Forge pre-flight constants — 镜像 lllyasviel/stable-diffusion-webui-forge
/// <c>modules/launch_utils.py:prepare_environment()</c> 用的 commit pin。
/// 升级 Forge 模板时同步检查(launch_utils 改了 hash 我们要跟);不跟会出现 sd 启动时
/// hash 不匹配但 git clone 仍成功 — pin 是 advisory,不是 strict assert。
///
/// 跑点(manager 装依赖时 pre-flight):
///   1. <c>pip install openai/CLIP/archive/{clip}.zip</c>  --no-build-isolation
///   2. <c>pip install mlfoundations/open_clip/archive/{openclip}.zip</c>  --no-build-isolation
///   3. <c>pip install -r requirements_versions.txt</c>      --no-deps
///   4. <c>git clone</c> 3 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>:
///      stable-diffusion-webui-assets / huggingface_guess / BLIP
///      (注:Forge launch_utils.py:461-463 已注释掉 Stability-AI/stablediffusion +
///      Stability-AI/generative-models — Stability-AI/stablediffusion 仓库已从
///      github 移除,所以 A1111 pre-flight 跟 sdweb 启动都会 fail paths.py:34。
///      用户决定去掉 A1111 模板;Forge 改用 huggingface_guess 替代 SD core。)
///
/// launch.py 启动时这些步骤全部 idempotent(is_installed / os.makedirs exist_ok /
/// git_clone skip 已存在),所以 pre-flight 失败也不会"半破坏" — 下次再跑可补救。
/// </summary>
public static class ForgePreFlightConstants
{
    /// <summary>
    /// Forge pre-flight 完成 marker(空文件,只用于 <see cref="Services.RequirementsInstaller.IsInstalled"/> 检测)。
    /// 跟 ComfyUI 的 <see cref="Services.RequirementsInstaller.MarkerFileName"/> (= <c>.requirements_installed</c>)
    /// 分开 — 两套 template 各有自己的 marker,互不干扰。
    ///
    /// v1.0.0.x 重命名:从 <c>.a1111_preflight_installed</c> 改为 <c>.forge_preflight_installed</c>
    /// (A1111 模板已下线)。老 A1111 / Forge env 在 marker 文件存在时会被识别成"已装",删除
    /// 重跑 pre-flight 即可;不兼容老 marker 文件名。
    /// </summary>
    public const string MarkerFileName = ".forge_preflight_installed";

    /// <summary>
    /// repositories 子目录名(跟 launch_utils.py <c>dir_repos = "repositories"</c> 同步)。
    /// </summary>
    public const string RepositoriesDirName = "repositories";

    /// <summary>
    /// 单条 git repo pin(URL + target dir + display name + commit hash)。
    /// </summary>
    public sealed record RepoSpec(string Url, string DirName, string DisplayName, string CommitHash);

    /// <summary>
    /// 单条 pip zip 包 pin(URL + display name)。
    /// </summary>
    public sealed record ZipPackage(string Url, string DisplayName);

    /// <summary>
    /// launch_utils.py:460-465 镜像(Stability-AI 两条已注释掉 — Stability-AI/stablediffusion
    /// 仓库已从 github 移除)。
    /// </summary>
    public static readonly IReadOnlyList<RepoSpec> Repos = new[]
    {
        new RepoSpec(
            "https://github.com/AUTOMATIC1111/stable-diffusion-webui-assets.git",
            "stable-diffusion-webui-assets",
            "assets",
            "6f7db241d2f8ba7457bac5ca9753331f0c266917"),
        new RepoSpec(
            "https://github.com/lllyasviel/huggingface_guess.git",
            "huggingface_guess",
            "huggingface_guess",
            "84826248b49bb7ca754c73293299c4d4e23a548d"),
        new RepoSpec(
            "https://github.com/salesforce/BLIP.git",
            "BLIP",
            "BLIP",
            "48211a1594f1321b00f14c9f7a5b4813144b2fb9"),
    };

    /// <summary>
    /// launch_utils.py:390-391 镜像(clip / open_clip 跟 A1111 同 URL — Forge fork 没改)。
    /// </summary>
    public static readonly IReadOnlyList<ZipPackage> Zips = new[]
    {
        new ZipPackage(
            "https://github.com/openai/CLIP/archive/d50d76daa670286dd6cacf3bcd80b5e4823fc8e1.zip",
            "clip"),
        new ZipPackage(
            "https://github.com/mlfoundations/open_clip/archive/bb6e834e9c70d9c27d0dc3ecedeebeaeb1ffad6b.zip",
            "open_clip"),
    };
}