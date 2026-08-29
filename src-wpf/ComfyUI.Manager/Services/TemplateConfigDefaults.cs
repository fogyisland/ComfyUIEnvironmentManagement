using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
///
/// v1.0.0.x: 加 6 个 built-in — Forge(本地 shipped,但之前没注册 → #497 用户
/// 看到「只有 2 个模板」)+ OpenVoice/Whisper/CoquiTTS/Bark(GitHub clone,AI 语音服务)。
/// v1.0.0.x (2026-08-29): SwarmUI 已下线 — ProcessLauncher 的 Python 假设对 SwarmUI
/// (.NET app)functional break,venv python 不存在 + Models junction 路径错 +
/// PYTHONPATH 无意义。用户决定去掉 SwarmUI 模板。剩 7 个 built-in 都受 G13 delete
/// 保护(改 <see cref="TemplateConfig.CanDelete"/>)。
/// </summary>
public static class TemplateConfigDefaults
{
    // v1.0.0.x: LocalSourceDir 是相对路径,<see cref="TemplatePathResolver.Resolve"/>
    // 锚定到 Settings.SystemTemplateLibraryDir(= 用户配的 ENVTemplate 之类模板根)。
    // 所以 default 直接写 "<Kind>"(<system_template_library_dir>/ComfyUI),**不**加
    // "envTemplates/" 前缀 — 加了会被 resolve 成 <system_template_library_dir>/envTemplates/ComfyUI
    // 多一层(用户 2026-08-26 反馈 git clone 创建了 nested envTemplate/envtemplate/ 子目录)。
    // 2 个 image templates (ComfyUI/Forge) 老 settings 里就是这个形式
    // (LocalSourceDir = "<Kind>"),4 个 GitHub AI voice (OpenVoice/Whisper/CoquiTTS/Bark)
    // 是新建,统一对齐。v1.0.0.x: A1111 + SwarmUI 模板已下线 — A1111 因
    // Stability-AI/stablediffusion 仓库已从 github 移除,SwarmUI 因 ProcessLauncher
    // Python 假设 functional break(A1111 pre-flight + sdweb 启动都 fail paths.py:34;
    // SwarmUI 是 .NET app,venv python 不存在)。Forge 替代 SD 角色。
    public static TemplateConfig ComfyUi(string projectRoot) => new()
    {
        Name = "ComfyUI",
        Kind = "ComfyUI",
        LocalSourceDir = "ComfyUI",
        EntryScript = "main.py",
        EntryArgs = "--port {port} --listen 0.0.0.0",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
        // v1.0.0.x: 内置 Meta 元数据 — 描述/分类/作者/官方仓库,用户可在
        // EditTemplateDialog 自由修改;只读内置模板(seed 时填,SettingsDefaults
        // 不会重新覆盖 — 用户改了永远跟用户走)。
        Meta = new()
        {
            ["category"] = "图像生成",
            ["description"] = "ComfyUI:节点式 Stable Diffusion 工作流引擎",
            ["author"] = "comfyanonymous",
            ["repo"] = "https://github.com/comfyanonymous/ComfyUI",
        },
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
        LocalSourceDir = "Forge",
        EntryScript = "webui.py",
        EntryArgs = "--port {port} --api",
        ModelsSubdir = "models/Stable-diffusion",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
        Meta = new()
        {
            ["category"] = "图像生成",
            ["description"] = "Stable Diffusion WebUI Forge — A1111 优化 fork",
            ["author"] = "lllyasviel",
            ["repo"] = "https://github.com/lllyasviel/stable-diffusion-webui-forge",
        },
    };

    /// <summary>
    /// v1.0.0.x: AI 语音 — OpenVoice (myshell-ai/OpenVoice)。voice cloning TTS。
    /// GitHub clone source。Python 入口 api.py (FastAPI server);空环境由
    /// EnvCreator 装 venv + pip install -e .。
    /// </summary>
    public static TemplateConfig OpenVoice(string projectRoot) => new()
    {
        Name = "OpenVoice",
        Kind = "OpenVoice",
        LocalSourceDir = "OpenVoice",
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
    /// GitHub clone source。Whisper 是 CLI 工具,entry 用 whisper Python module
    /// (执行 whisper transcribe <args>)。
    /// 注意:Whisper 不是常驻 web server,port 参数无意义但保留 {port} 兼容模板结构。
    /// </summary>
    public static TemplateConfig Whisper(string projectRoot) => new()
    {
        Name = "Whisper",
        Kind = "Whisper",
        LocalSourceDir = "Whisper",
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
    /// GitHub clone source。Entry 用 tts-server(HTTP server,coqui-ai 提供的
    /// 内置服务模式)便于 UI 远程调用。
    /// </summary>
    public static TemplateConfig CoquiTts(string projectRoot) => new()
    {
        Name = "CoquiTTS",
        Kind = "CoquiTTS",
        LocalSourceDir = "CoquiTTS",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/coqui-ai/TTS.git",
        EntryScript = "tts-server",
        EntryArgs = "--port {port}",
        ModelsSubdir = "",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: Bark (suno-ai/bark)。生成式语音 / 音效模型。
    /// GitHub clone source。Bark 是 CLI 工具(无 HTTP server),
    /// entry 用 bark 模块(等同 python -m bark)。
    /// </summary>
    public static TemplateConfig Bark(string projectRoot) => new()
    {
        Name = "Bark",
        Kind = "Bark",
        LocalSourceDir = "Bark",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/suno-ai/bark.git",
        EntryScript = "bark",
        EntryArgs = "",
        ModelsSubdir = "",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}