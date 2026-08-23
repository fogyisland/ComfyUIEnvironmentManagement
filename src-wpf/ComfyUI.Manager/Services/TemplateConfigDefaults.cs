using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons for ComfyUI + A1111.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
/// </summary>
public static class TemplateConfigDefaults
{
    public static TemplateConfig ComfyUi(string projectRoot) => new()
    {
        Name = "ComfyUI",
        Kind = "ComfyUI",
        LocalSourceDir = System.IO.Path.Combine(projectRoot, "Templates", "ComfyUI"),
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
        LocalSourceDir = System.IO.Path.Combine(projectRoot, "Templates", "A1111"),
        EntryScript = "webui.py",
        EntryArgs = "--port {port}",
        ModelsSubdir = "models/Stable-diffusion",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}
