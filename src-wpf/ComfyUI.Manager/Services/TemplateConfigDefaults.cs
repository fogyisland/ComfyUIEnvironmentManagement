using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
///
/// v1.0.0.x: 加 6 个 built-in — Forge/SwarmUI(本地 shipped,但之前没注册 → #497 用户
/// 看到「只有 2 个模板」)+ OpenVoice/Whisper/CoquiTTS/Bark(GitHub clone,AI 语音服务)。
/// 所有 8 个 kind 都受 G13 delete 保护(改 <see cref="TemplateConfig.CanDelete"/>)。
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

    /// <summary>
    /// v1.0.0.x: 修复 #497 — Forge 在 ENVTemplate/ 已 shipped 但 TemplateConfigDefaults
    /// 漏注册。Forge 是 A1111 的衍生 fork,entry 用 webui.py(同 A1111 模式),默认
    /// 启动多 --api 方便 ComfyUI-Manager / API 消费者调用。
    /// </summary>
    public static TemplateConfig Forge(string projectRoot) => new()
    {
        Name = "Forge",
        Kind = "Forge",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "Forge"),
        EntryScript = "webui.py",
        EntryArgs = "--port {port} --api",
        ModelsSubdir = "models/Stable-diffusion",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: SwarmUI (StableSwarmUI) — Multi-platform launcher 脚本(.bat / .sh)。
    /// Windows 用 Launch-windows.bat;macOS/Linux 用对应 .sh。这里 entry script
    /// 留 Launch-windows.bat,跨平台启动由 EnvCreator / ProcessLauncher 根据 OS 决定
    /// 实际执行哪个脚本(用户也可手动改 entry)。
    /// </summary>
    public static TemplateConfig SwarmUi(string projectRoot) => new()
    {
        Name = "SwarmUI",
        Kind = "SwarmUI",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "SwarmUI"),
        EntryScript = "Launch-windows.bat",
        EntryArgs = "",
        ModelsSubdir = "Models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: AI 语音 — OpenVoice (myshell-ai/OpenVoice)。voice cloning TTS。
    /// GitHub clone source,本地路径 envTemplates/OpenVoice。Python 入口 api.py
    /// (FastAPI server);空环境由 EnvCreator 装 venv + pip install -e .。
    /// </summary>
    public static TemplateConfig OpenVoice(string projectRoot) => new()
    {
        Name = "OpenVoice",
        Kind = "OpenVoice",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "OpenVoice"),
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/myshell-ai/OpenVoice.git",
        EntryScript = "api.py",
        EntryArgs = "--port {port}",
        ModelsSubdir = "outputs",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: AI 语音 — Whisper (openai/whisper)。OpenAI 官方 speech-to-text。
    /// GitHub clone source,本地路径 envTemplates/Whisper。Whisper 是 CLI 工具,
    /// entry 用 whisper Python module(执行 whisper transcribe <args>)。
    /// 注意:Whisper 不是常驻 web server,port 参数无意义但保留 {port} 兼容模板结构。
    /// </summary>
    public static TemplateConfig Whisper(string projectRoot) => new()
    {
        Name = "Whisper",
        Kind = "Whisper",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "Whisper"),
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/openai/whisper.git",
        EntryScript = "whisper",
        EntryArgs = "--model tiny",
        ModelsSubdir = "",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: AI 语音 — Coqui TTS (coqui-ai/TTS)。多语言 TTS 库。
    /// GitHub clone source,本地路径 envTemplates/CoquiTTS。Entry 用 tts-server
    /// (HTTP server,coqui-ai 提供的内置服务模式)便于 UI 远程调用。
    /// </summary>
    public static TemplateConfig CoquiTts(string projectRoot) => new()
    {
        Name = "CoquiTTS",
        Kind = "CoquiTTS",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "CoquiTTS"),
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/coqui-ai/TTS.git",
        EntryScript = "tts-server",
        EntryArgs = "--port {port}",
        ModelsSubdir = "",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: AI 语音 — Bark (suno-ai/bark)。生成式语音 / 音效模型。
    /// GitHub clone source,本地路径 envTemplates/Bark。Bark 是 CLI 工具(无 HTTP server),
    /// entry 用 bark 模块(等同 python -m bark)。
    /// </summary>
    public static TemplateConfig Bark(string projectRoot) => new()
    {
        Name = "Bark",
        Kind = "Bark",
        LocalSourceDir = System.IO.Path.Combine("envTemplates", "Bark"),
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/suno-ai/bark.git",
        EntryScript = "bark",
        EntryArgs = "",
        ModelsSubdir = "",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}