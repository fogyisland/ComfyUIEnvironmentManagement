using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons for ComfyUI + A1111.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
/// </summary>
public static class TemplateConfigDefaults
{
    // v1.0.0.x: 目录名 "Templates" → "envTemplates"(用户 2026-08-24 反馈)。
    // 用相对路径 "envTemplates/<kind>" 让 settings 可移植(任意 cwd 启动都能 resolve),
    // 不依赖 projectRoot 绝对路径。ProcessLauncher / EnvCreator 走 Directory.Exists
    // 检查,Path.GetFullPath 转绝对路径做实际 git clone。
    public static TemplateConfig ComfyUi(string projectRoot) => new()
    {
        Name = "ComfyUI",
        Kind = "ComfyUI",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "ComfyUI"),
        EntryScript = "main.py",
        EntryArgs = "--port {port} --listen 0.0.0.0",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    public static TemplateConfig A1111(string projectRoot) => new()
    {
        Name = "A1111",
        Kind = "A1111",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "A1111"),
        EntryScript = "webui.py",
        EntryArgs = "--port {port}",
        ModelsSubdir = "models/Stable-diffusion",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}
