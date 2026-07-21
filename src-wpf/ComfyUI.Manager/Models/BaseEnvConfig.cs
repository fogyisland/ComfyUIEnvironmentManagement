using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 基础环境部署配置:torch/torchaudio/torchvision/xformers 等 Python 包
/// 在 env venv 里 pip install 的参数模板。Settings 持久化字段。
///
/// 字段全部 JSON-friendly,缺省值让老 settings.json 无 base_env 也能正常反序列化。
/// </summary>
public class BaseEnvConfig
{
    public string CudaVersion { get; set; } = "cu118";        // cu118 / cu121 / cu124 / cpu
    public string TorchChannel { get; set; } = "stable";      // stable / nightly
    public List<string> Packages { get; set; } = new()
    {
        "torch", "torchaudio", "torchvision", "xformers",
    };
    public string ExtraArgs { get; set; } = "";               // --user / -f / --no-cache ...
    public string CustomPipArgs { get; set; } = "";           // 高级:整段覆盖,优先于结构化字段

    /// <summary>
    /// 构造 pip install 参数数组(argparse 风格,空格 split)。
    /// CustomPipArgs 非空 → 直接 split 返回,优先级最高。
    /// </summary>
    public IReadOnlyList<string> BuildPipArgs()
    {
        if (!string.IsNullOrWhiteSpace(CustomPipArgs))
        {
            return CustomPipArgs
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        var args = new List<string> { "install" };
        args.AddRange(Packages);
        if (TorchChannel == "nightly")
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
    /// SettingsViewModel / BaseEnvDialogViewModel 改副本不影响原 Settings。
    /// </summary>
    public BaseEnvConfig Clone()
    {
        return new BaseEnvConfig
        {
            CudaVersion = CudaVersion,
            TorchChannel = TorchChannel,
            Packages = new List<string>(Packages),
            ExtraArgs = ExtraArgs,
            CustomPipArgs = CustomPipArgs,
        };
    }
}