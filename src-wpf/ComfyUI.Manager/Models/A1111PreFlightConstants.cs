using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x A1111 / Forge pre-flight constants — 镜像 AUTOMATIC1111 stable-diffusion-webui
/// <c>modules/launch_utils.py:prepare_environment()</c> 用的 commit pin。
/// 升级 A1111 时同步检查(launch_utils 改了 hash 我们要跟);不跟会出现 sd 启动时 hash 不匹配
/// 但 git clone 仍成功 — pin 是 advisory,不是 strict assert。
///
/// 跑点(manager 装依赖时 pre-flight):
///   1. <c>pip install openai/CLIP/archive/{clip}.zip</c>
///   2. <c>pip install mlfoundations/open_clip/archive/{openclip}.zip</c>
///   3. <c>pip install -r requirements_versions.txt</c>
///   4. <c>git clone</c> 5 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>:
///      stable-diffusion-webui-assets / stable-diffusion-stability-ai /
///      generative-models / k-diffusion / BLIP
///
/// launch.py 启动时这些步骤全部 idempotent(is_installed / os.makedirs exist_ok /
/// git_clone skip 已存在),所以 pre-flight 失败也不会"半破坏" — 下次再跑可补救。
/// </summary>
public static class A1111PreFlightConstants
{
    /// <summary>
    /// A1111 / Forge pre-flight 完成 marker(空文件,只用于 <see cref="RequirementsInstaller.IsInstalled"/> 检测)。
    /// 跟 ComfyUI 的 <see cref="Services.RequirementsInstaller.MarkerFileName"/> (= <c>.requirements_installed</c>)
    /// 分开 — 两套 template 各有自己的 marker,互不干扰。
    /// </summary>
    public const string MarkerFileName = ".a1111_preflight_installed";

    /// <summary>
    /// repositories 子目录名(跟 launch_utils.py:26 <c>dir_repos = "repositories"</c> 同步)。
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
    /// launch_utils.py:344-358 镜像。
    /// </summary>
    public static readonly IReadOnlyList<RepoSpec> Repos = new[]
    {
        new RepoSpec(
            "https://github.com/AUTOMATIC1111/stable-diffusion-webui-assets.git",
            "stable-diffusion-webui-assets",
            "assets",
            "6f7db241d2f8ba7457bac5ca9753331f0c266917"),
        new RepoSpec(
            "https://github.com/Stability-AI/stablediffusion.git",
            "stable-diffusion-stability-ai",
            "Stable Diffusion",
            "cf1d67a6fd5ea1aa600c4df58e5b47da45f6bdbf"),
        new RepoSpec(
            "https://github.com/Stability-AI/generative-models.git",
            "generative-models",
            "Stable Diffusion XL",
            "45c443b316737a4ab6e40413d7794a7f5657c19f"),
        new RepoSpec(
            "https://github.com/crowsonkb/k-diffusion.git",
            "k-diffusion",
            "K-diffusion",
            "ab527a9a6d347f364e3d185ba6d714e22d80cb3c"),
        new RepoSpec(
            "https://github.com/salesforce/BLIP.git",
            "BLIP",
            "BLIP",
            "48211a1594f1321b00f14c9f7a5b4813144b2fb9"),
    };

    /// <summary>
    /// launch_utils.py:345-346 镜像。
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
