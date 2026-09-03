using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 multi-template: built-in default TemplateConfig singletons.
/// Used by SettingsDefaults.Apply to seed on first run. Read-only after construction.
///
/// v1.0.0.x: 加 4 个 built-in — Forge(本地 shipped,但之前没注册 → #497 用户
/// 看到「只有 2 个模板」)+ OpenVoice/Whisper(GitHub clone,AI 语音服务)。
/// v1.0.0.x (2026-08-29): SwarmUI 已下线 — ProcessLauncher 的 Python 假设对 SwarmUI
/// (.NET app)functional break,venv python 不存在 + Models junction 路径错 +
/// PYTHONPATH 无意义。用户决定去掉 SwarmUI 模板。
/// v1.0.0.x (2026-09-01) T30: CoquiTTS (coqui-ai/TTS, coqui 公司 2024 关停) +
/// Bark (suno-ai/bark, repo 已 archived) 已下线 (>2y 无更新)。剩 6 个 built-in 都受
/// G13 delete 保护(改 <see cref="TemplateConfig.CanDelete"/>)。
/// v1.0.0.x (2026-09-02) T31: Whisper (openai/whisper 纯 CLI one-shot transcribe,
/// 无 Web UI) + LTXVideo (Lightricks/LTX-Video run-ltx2-distilled.bat 纯 CLI batch,
/// 无 Web UI) 已下线 — 用户决策 "删除所有不带 Web UI 的"。剩 4 个非 ComfyUI/Forge
/// built-in (OpenVoice / HunyuanVideo / CogVideoX / HivisionIDPhotos) + ComfyUI/Forge
/// = 6 个 built-in 都受 G13 delete 保护。
/// </summary>
public static class TemplateConfigDefaults
{
    // v1.0.0.x: LocalSourceDir 是相对路径,<see cref="TemplatePathResolver.Resolve"/>
    // 锚定到 Settings.SystemTemplateLibraryDir(= 用户配的 ENVTemplate 之类模板根)。
    // 所以 default 直接写 "<Kind>"(<system_template_library_dir>/ComfyUI),**不**加
    // "envTemplates/" 前缀 — 加了会被 resolve 成 <system_template_library_dir>/envTemplates/ComfyUI
    // 多一层(用户 2026-08-26 反馈 git clone 创建了 nested envTemplate/envtemplate/ 子目录)。
    // 2 个 image templates (ComfyUI/Forge) 老 settings 里就是这个形式
    // (LocalSourceDir = "<Kind>"),2 个 GitHub AI voice (OpenVoice/Whisper) 是新建,
    // 统一对齐。v1.0.0.x: A1111 + SwarmUI + CoquiTTS + Bark + Whisper + LTXVideo
    // 模板已下线 — A1111 因 Stability-AI/stablediffusion 仓库已从 github 移除;
    // SwarmUI 因 ProcessLauncher Python 假设 functional break(A1111 pre-flight +
    // sdweb 启动都 fail paths.py:34;SwarmUI 是 .NET app,venv python 不存在)。
    // v1.0.0.x (2026-09-01) T30: CoquiTTS (coqui 公司 2024 关停) + Bark
    // (repo archived, >2y 无更新)。
    // v1.0.0.x (2026-09-02) T31: Whisper + LTXVideo 纯 CLI,无 Web UI,已下线。
    // Forge 替代 SD 角色。
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
        // v1.0.0.x (2026-08-31): 项目方已在 dev build 验证 env-create + 启动 + 接口可达,
        // 标记 Verified=true 显示绿色 ✓ 「已验证 可运行」badge。其它 9 个 built-in
        // 验证后逐个 ship 时再加。
        Verified = true,
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
        // v1.0.0.x (2026-08-31): 项目方已在 dev build 验证 env-create + 启动 + 接口可达,
        // 标记 Verified=true 显示绿色 ✓ 「已验证 可运行」badge。
        Verified = true,
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
    /// GitHub clone source。空环境由 EnvCreator 装 venv + pip install -e .。
    /// v1.0.0.x (2026-08-31): entry script 从 api.py 改成 openvoice/openvoice_app.py —
    /// api.py 是 library (BaseSpeakerTTS, ToneColorConverter),不是 server entry。
    /// 真实 entry 是 openvoice_app.py (Gradio UI demo.launch()),argparse 只接受
    /// --share;port 通过 GRADIO_SERVER_PORT env var 注入(ProcessLauncher 设,
    /// 零 upstream 改动)。
    /// </summary>
    public static TemplateConfig OpenVoice(string projectRoot) => new()
    {
        Name = "OpenVoice",
        Kind = "OpenVoice",
        LocalSourceDir = "OpenVoice",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/myshell-ai/OpenVoice.git",
        EntryScript = "openvoice/openvoice_app.py",
        EntryArgs = "--share",
        ModelsSubdir = "outputs",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };

    /// <summary>
    /// v1.0.0.x: HunyuanVideo Native Gradio WebUI (Tencent-Hunyuan/HunyuanVideo)。
    /// 腾讯混元视频生成模型的官方 Gradio WebUI。GitHub-clone source。
    /// Entry: <c>gradio_webui.py</c>(仓库根目录),通过 <c>--port {port} --listen 0.0.0.0</c>
    /// 监听所有接口,默认 Gradio 端口 7860(用户可在 Settings 改)。
    /// </summary>
    public static TemplateConfig HunyuanVideo(string projectRoot) => new()
    {
        Name = "HunyuanVideo",
        Kind = "HunyuanVideo",
        LocalSourceDir = "HunyuanVideo",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/Tencent-Hunyuan/HunyuanVideo.git",
        EntryScript = "gradio_webui.py",
        EntryArgs = "--port {port} --listen 0.0.0.0",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
        // v1.0.0.x (2026-09-01): HunyuanVideo 仓库有 requirements.txt,pyproject.toml 要求 torch ≥2.5.1。
        // BaseEnv 按钮 → BaseEnvProfilePickerDialog 让用户选 ≥2.5.1 live defaults;
        // 依赖按钮 → pip install requirements.txt
        RequirementsFile = "requirements.txt",
    };

    /// <summary>
    /// v1.0.0.x: CogVideoX WebUI (THUDM/CogVideo)。
    /// 智谱 CogVideoX 模型的官方 Gradio WebUI。GitHub-clone source。
    /// Entry: <c>inference/gradio_web_demo.py</c>(在 inference/ 子目录)。
    /// 通过 <c>--server_port {port}</c> 设端口(Gradio demo 标准 CLI)。
    /// 注意 entry script 含子目录 — env-create 复制源码后,env 根目录下
    /// 有 <c>inference/gradio_web_demo.py</c>,ProcessLauncher 应支持路径相对解析。
    /// </summary>
    public static TemplateConfig CogVideoX(string projectRoot) => new()
    {
        Name = "CogVideoX",
        Kind = "CogVideoX",
        LocalSourceDir = "CogVideoX",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/THUDM/CogVideo.git",
        EntryScript = "inference/gradio_web_demo.py",
        EntryArgs = "--server_port {port}",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
        // v1.0.0.x (2026-09-01): CogVideoX 仓库有 requirements.txt + pyproject.toml,
        // pyproject.toml 要求 torch ≥2.5.1。
        // BaseEnv 按钮 → BaseEnvProfilePickerDialog 让用户选 ≥2.5.1 live defaults;
        // 依赖按钮 → pip install requirements.txt
        RequirementsFile = "requirements.txt",
    };

    /// <summary>
    /// v1.0.0.x: HivisionIDPhotos (Zeyi-Lin/HivisionIDPhotos)。
    /// AI 身份证 / 护照照片生成 Gradio app ——
    /// 用 CNN 检测人像 + 生成符合规格的标准证件照(支持自定义背景色 / 尺寸 / 美颜)。
    /// GitHub-clone source。Entry: <c>app.py</c>(仓库根目录的 Gradio app 启动脚本),
    /// 通过 <c>--port {port}</c> 设端口(Gradio 标准 CLI);默认 Gradio 端口 7860
    /// (用户可在 Settings 改 {port} 占位)。
    /// </summary>
    public static TemplateConfig HivisionIdPhotos(string projectRoot) => new()
    {
        Name = "HivisionIDPhotos",
        Kind = "HivisionIDPhotos",
        LocalSourceDir = "HivisionIDPhotos",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/Zeyi-Lin/HivisionIDPhotos.git",
        EntryScript = "app.py",
        EntryArgs = "--port {port}",
        ModelsSubdir = "models",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
}