using System;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x (2026-08-29):Forge env 创建成功后弹的提示框 VM。
///
/// 用户原话:"在创建forge的时候有个提示框,请在设置中设置forge的lora vae路径"。
/// Forge env 实际依赖 <see cref="Settings.ForgePaths"/> 6 个 per-type 字段
/// (checkpoints / loras / vae / embeddings / hypernetworks / controlnet),
/// env-create 时用户不知道 → 默认走 <see cref="Settings.DefaultModelsDirectory"/>
/// 派生 (env-create step 7.5 立即写 yaml,见 EnvCreatorService.CreateAsync)。
/// 此 VM 让用户显式选择"现在去设置"或"之后再说"。
///
/// 复用 <see cref="NodeInstallDiffWarningViewModel"/> 的 CloseRequested + Choice 模式:
/// caller 在 <c>ShowDialog()</c> 关闭后读 <see cref="Choice"/>(<c>null</c> = 用户关窗)。
/// </summary>
public class ForgePostCreatePromptViewModel : ViewModelBase
{
    /// <summary>
    /// 用户最终选择。<c>"settings"</c> = 「去设置」,<c>"skip"</c> = 「跳过」,
    /// <c>null</c> = 初始状态 / 用户关窗(等同 skip,不强制弹)。
    /// </summary>
    public string? Choice { get; private set; }

    public event Action? CloseRequested;

    /// <summary>
    /// 被创建的 Forge env 名(给 UI 文案用,e.g. "Forge env 'forge-foo' 已成功创建")。
    /// </summary>
    public string EnvName { get; }

    /// <summary>
    /// 主文案。包含 env 名 + 提示用户去 Settings → 模型 → Forge 模型目录 配置
    /// LoRA/VAE 等路径(留空则用 DefaultModelsDirectory 派生,启动时
    /// ProcessLauncher.StartEnvAsync 会再写一次 yaml,幂等)。
    /// </summary>
    public string Message { get; }

    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand SkipCommand { get; }

    public ForgePostCreatePromptViewModel(string envName)
    {
        EnvName = envName ?? throw new ArgumentNullException(nameof(envName));
        Message = $"Forge env '{envName}' 已成功创建。建议在「设置 → 模型 → Forge 模型目录」" +
                  "设置 LoRA、VAE 等模型路径,否则将使用默认派生路径(DefaultModelsDirectory 子目录)。";
        GoToSettingsCommand = new RelayCommand(() =>
        {
            Choice = "settings";
            CloseRequested?.Invoke();
        });
        SkipCommand = new RelayCommand(() =>
        {
            Choice = "skip";
            CloseRequested?.Invoke();
        });
    }
}
