using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 基础环境部署 profile:torch + CUDA + packages 的预设组合。
/// 从 base_env_profiles.json 加载(或内置默认值)。
///
/// 与 BaseEnvConfig 的差别:
/// - 新增 Id / Name / Description / TorchVersion(给预设展示用)
/// - 去掉 CustomPipArgs(不再支持高级 raw 模式)
/// - TorchChannel 重命名为 Channel(更直观)
/// </summary>
public class BaseEnvProfile
{
    public string Id { get; set; } = "";                       // "torch-stable-cu118"
    public string Name { get; set; } = "";                     // "Torch Stable CUDA 11.8"
    public string Description { get; set; } = "";              // 用户可见的简介
    public string TorchVersion { get; set; } = "";             // "2.1.0" / "nightly"
    public string CudaVersion { get; set; } = "cu118";         // cu118 / cu121 / cu124 / cpu
    public string Channel { get; set; } = "stable";            // stable / nightly
    public List<string> Packages { get; set; } = new()
    {
        "torch", "torchaudio", "torchvision", "xformers",
    };
    public string ExtraArgs { get; set; } = "";                // --user / -f / --no-cache ...

    /// <summary>
    /// 构造 pip install 参数数组(argparse 风格,空格 split)。
    /// 永远走结构化路径(无 CustomPipArgs 分支)。
    /// </summary>
    public IReadOnlyList<string> BuildPipArgs()
    {
        var args = new List<string> { "install" };
        args.AddRange(Packages);
        if (Channel == "nightly")
        {
            args.Add("--pre");
        }
        if (!string.IsNullOrWhiteSpace(CudaVersion) && CudaVersion != "cpu")
        {
            args.Add("--index-url");
            args.Add($"https://download.pytorch.org/whl/{CudaVersion}");
        }
        if (!string.IsNullOrWhiteSpace(ExtraArgs))
        {
            args.AddRange(
                ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return args;
    }

    /// <summary>
    /// 浅-深拷贝:Packages 是新 List<string> 实例,字符串本身不可变不需要深拷贝。
    /// ViewModel 改副本不影响原 profile(供 BaseEnvView 多选 + 临时编辑用)。
    /// </summary>
    public BaseEnvProfile Clone()
    {
        return new BaseEnvProfile
        {
            Id = Id,
            Name = Name,
            Description = Description,
            TorchVersion = TorchVersion,
            CudaVersion = CudaVersion,
            Channel = Channel,
            Packages = new List<string>(Packages),
            ExtraArgs = ExtraArgs,
        };
    }
}