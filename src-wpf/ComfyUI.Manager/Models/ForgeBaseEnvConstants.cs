using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x Forge BED constants — 镜像 lllyasviel/stable-diffusion-webui-forge
/// <c>modules/launch_utils.py:prepare_environment()</c> 在「安装基础环境」阶段
/// 提前跑的步骤,让 <c>launch.py</c> 启动时 step 全部 idempotent 跳过。
///
/// 用户原话 2026-08-29:
/// "pip install torch==2.4.0 torchvision==0.19.0 torchaudio==2.4.0 forge 在安装基础环境
/// 记得是这个版本的torch"
/// → Forge 跑 launch_utils 默认 torch==2.3.1 不够(SDXL 等新优化要 2.4+),显式锁 2.4.0
/// 系列 + 跟 ComfyUI BED 一致的 cu121 CUDA wheel(国内 pypi 镜像不镜像
/// download.pytorch.org/whl/,需要 --extra-index-url 指向原站)。
///
/// 步骤:
///   0. <c>pip install torch==2.4.0 torchvision==0.19.0 torchaudio==2.4.0
///        --extra-index-url https://download.pytorch.org/whl/cu121</c>
///   1. <c>pip install openai/CLIP/archive/{hash}.zip --no-build-isolation</c>
///   2. <c>pip install mlfoundations/open_clip/archive/{hash}.zip --no-build-isolation</c>
///   3. <c>pip install xformers==0.0.27 --no-deps</c>(Forge fork 默认 xformers=True)
///   4. <c>pip install -r requirements_versions.txt --no-deps</c>(过滤裸 torch 行)
///   5. <c>git clone</c> 3 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>
///      (assets / huggingface_guess / BLIP;Stability-AI 2 个 sd core 已被
///      Forge 注释掉 — Stability-AI/stablediffusion 仓库已从 github 移除)
///
/// 复用 <see cref="ForgePreFlightConstants"/> 的 Zips + Repos + RepositoriesDirName,
/// 不重复定义(单源原则)。Marker 文件单独用 .forge_base_env_installed,跟 pre-flight
/// 分开 — pre-flight 是「装依赖」按钮阶段(假设 BED 已装),BED 是「安装基础环境」阶段。
/// </summary>
public static class ForgeBaseEnvConstants
{
    /// <summary>
    /// Forge BED 完成 marker(空文件,只用于 IsInstalled 检测)。跟 pre-flight marker
    /// (.forge_preflight_installed) 分开 — 各自阶段独立判定。
    /// </summary>
    public const string MarkerFileName = ".forge_base_env_installed";

    /// <summary>
    /// 锁定的 torch 版本三元组(用户 2026-08-29 明确指定)。Forge 跑 launch_utils 默认
    /// torch==2.3.1 不够(SDXL 等新优化要 torch>=2.4),显式覆盖。CUDA wheel 走
    /// download.pytorch.org/whl/cu121(国内 PyPI 镜像不镜像 download.pytorch.org)。
    /// </summary>
    public const string TorchVersion = "2.4.0";
    public const string TorchVisionVersion = "0.19.0";
    public const string TorchAudioVersion = "2.4.0";

    /// <summary>
    /// PyTorch CUDA wheel index(launch_utils.py:364 TORCH_INDEX_URL 默认值同步)。
    /// </summary>
    public const string TorchIndexUrl = "https://download.pytorch.org/whl/cu121";

    /// <summary>
    /// launch_utils.py:389 XFORMERS_PACKAGE 默认值(Forge fork xformers=True 走默认)。
    /// </summary>
    public const string XformersPackage = "xformers==0.0.27";
}