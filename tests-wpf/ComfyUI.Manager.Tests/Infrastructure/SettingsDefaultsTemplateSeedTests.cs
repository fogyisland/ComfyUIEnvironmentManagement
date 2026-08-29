using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTemplateSeedTests
{
    private static readonly string ProjectRoot =
        Path.Combine(Path.GetTempPath(), "cmgr-templates-test");

    [Fact]
    public void Apply_EmptySettings_SeedsAllBuiltInTemplates()
    {
        // v1.0.0.x: 加 5 个 built-in(Forge 是 shipped 但漏注册 → #497 修复;
        // OpenVoice/Whisper/CoquiTTS/Bark 是 GitHub-cloned AI 语音 defaults)。
        // v1.0.0.x:A1111 + SwarmUI 模板已下线,不再 seed(A1111 因 Stability-AI
        // 仓库从 github 移除;SwarmUI 因 ProcessLauncher Python 假设 functional break)。
        // v1.0.0.x (2026-08-29): +HunyuanVideo/LTXVideo/CogVideoX/Fooocus → 6 → 10;
        // +HivisionIDPhotos → 11。
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.False(s.Templates.ContainsKey("A1111"));
        Assert.True(s.Templates.ContainsKey("Forge"));
        Assert.False(s.Templates.ContainsKey("SwarmUI"));
        Assert.True(s.Templates.ContainsKey("OpenVoice"));
        Assert.True(s.Templates.ContainsKey("Whisper"));
        Assert.True(s.Templates.ContainsKey("CoquiTTS"));
        Assert.True(s.Templates.ContainsKey("Bark"));
        Assert.True(s.Templates.ContainsKey("HunyuanVideo"));
        Assert.True(s.Templates.ContainsKey("LTXVideo"));
        Assert.True(s.Templates.ContainsKey("CogVideoX"));
        Assert.True(s.Templates.ContainsKey("Fooocus"));
        Assert.True(s.Templates.ContainsKey("HivisionIDPhotos"));
    }

    [Fact]
    public void Apply_EmptySettings_GitHubVoiceTemplates_HaveRepoUrlsAndSourceKind()
    {
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var ov = s.Templates["OpenVoice"];
        Assert.Equal(TemplateSourceKind.GitHub, ov.SourceKind);
        Assert.Equal("https://github.com/myshell-ai/OpenVoice.git", ov.GitHubRepoUrl);
        Assert.Equal("api.py", ov.EntryScript);

        var wh = s.Templates["Whisper"];
        Assert.Equal(TemplateSourceKind.GitHub, wh.SourceKind);
        Assert.Equal("https://github.com/openai/whisper.git", wh.GitHubRepoUrl);

        var co = s.Templates["CoquiTTS"];
        Assert.Equal(TemplateSourceKind.GitHub, co.SourceKind);
        Assert.Equal("https://github.com/coqui-ai/TTS.git", co.GitHubRepoUrl);

        var bk = s.Templates["Bark"];
        Assert.Equal(TemplateSourceKind.GitHub, bk.SourceKind);
        Assert.Equal("https://github.com/suno-ai/bark.git", bk.GitHubRepoUrl);
    }

    [Fact]
    public void Apply_EmptySettings_LocalImageTemplates_AreLocalSourceKind()
    {
        // v1.0.0.x (2026-08-29): Forge 是本地 shipped,SourceKind = Local,无 GitHubRepoUrl。
        // SwarmUI 已下线,不再 seed。
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var fg = s.Templates["Forge"];
        Assert.Equal(TemplateSourceKind.Local, fg.SourceKind);
        Assert.Equal("", fg.GitHubRepoUrl);
        Assert.Equal("webui.py", fg.EntryScript);
        Assert.Equal("--port {port} --api", fg.EntryArgs);

        Assert.False(s.Templates.ContainsKey("SwarmUI"));
    }

    [Fact]
    public void Apply_EmptySettings_ComfyUITemplateHasCorrectDefaults()
    {
        // G5: ComfyUI defaults
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        var c = s.Templates["ComfyUI"];
        Assert.Equal("ComfyUI", c.Name);
        Assert.Equal("ComfyUI", c.Kind);
        Assert.Equal("main.py", c.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", c.EntryArgs);
        Assert.Equal("models", c.ModelsSubdir);
    }

    [Fact]
    public void Apply_EmptySettings_A1111NotSeeded_TemplateDeprecated()
    {
        // v1.0.0.x: A1111 模板已下线 — Stability-AI/stablediffusion 仓库已从 github 移除。
        // A1111 不再出现在 SettingsDefaults 的 seed 默认列表,即便用户 settings.inf 是
        // 空文件 Apply 后也不会有 A1111 entry。老 settings 里残留的 A1111 entry 由
        // 用户在 Settings 面板手动 remove。
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.False(s.Templates.ContainsKey("A1111"));
    }

    [Fact]
    public void Apply_UserCustomizedTemplate_NotOverwritten()
    {
        // G4: never overwrite user customization
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "MyCustomName",
            Kind = "ComfyUI",
            LocalSourceDir = "D:/my-fork",
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 127.0.0.1",
            ModelsSubdir = "models",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("MyCustomName", s.Templates["ComfyUI"].Name);
        Assert.Equal("D:/my-fork", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("--port {port} --listen 127.0.0.1", s.Templates["ComfyUI"].EntryArgs);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDirField_MigratedToTemplatesDict()
    {
        // G6: migrate from old Settings.TemplateComfyuiDir (JSON property
        // "template_comfyui_dir") -> Settings.Templates["ComfyUI"].LocalSourceDir。
        // T12 移除 Settings.TemplateComfyuiDir 字段 — 改走 rawJson + JsonDocument
        // 读老 JSON property。模拟老 settings.json 整文件 deserialize 后,
        // Apply 用 rawJson 触发迁移。
        var s = new Settings();
        var oldJson = "{\"template_comfyui_dir\":\"D:/old/comfyui-source\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("D:/old/comfyui-source", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("main.py", s.Templates["ComfyUI"].EntryScript);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_NotMigrated_WhenRawJsonNull()
    {
        // T12 后:无 rawJson(纯 in-memory settings,没经过 SettingsRepository.Load)
        // → 不迁移,只 seed 默认 ComfyUI entry。保护 caller 不被"凭空迁移"覆盖。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot, rawJson: null);

        // Templates["ComfyUI"] 由 SeedBuiltInTemplatesIfMissing 填默认,
        // LocalSourceDir 指向 "<Kind>"(v1.0.0.x bug #509 修正:之前是
        // "envTemplates/ComfyUI" 多一层嵌套,跟 SystemTemplateLibraryDir 拼起来是
        // <system_template_library_dir>/envTemplates/ComfyUI 不正确)。
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("ComfyUI",
            s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_NotMigrated_WhenUserAlreadyHasComfyUI()
    {
        // G4:用户已设过 ComfyUI template → 不让老 JSON 字段覆盖当前 entry。
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = "D:/user-picked",
            EntryScript = "main.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models",
        };
        var oldJson = "{\"template_comfyui_dir\":\"D:/old/comfyui-source\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        Assert.Equal("D:/user-picked", s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_EmptyValue_NotMigrated()
    {
        // 老 JSON 字段为空字符串 → 不迁移,跟原 Settings 字段空白的语义一致。
        var s = new Settings();
        var oldJson = "{\"template_comfyui_dir\":\"\"}";

        SettingsDefaults.Apply(s, ProjectRoot, oldJson);

        // seed 默认 ComfyUI entry(LocalSourceDir 指向 "<Kind>" — v1.0.0.x bug #509 修正,
        // 之前是 "envTemplates/ComfyUI" 多一层嵌套)
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("ComfyUI",
            s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_OldTemplateComfyuiDir_MalformedJson_DoesNotThrow()
    {
        // 防御:JSON 解析失败 → 静默不抛,后续 seed 走默认 ComfyUI entry。
        var s = new Settings();
        var badJson = "{this is not valid json";

        var ex = Record.Exception(() => SettingsDefaults.Apply(s, ProjectRoot, badJson));

        Assert.Null(ex);
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
    }

    // --- v1.0.0.x bug #509: LocalSourceDir 嵌套 envTemplates/ 前缀 ---

    [Fact]
    public void Apply_EmptySettings_AllBuiltInTemplates_HaveLocalSourceDirWithoutEnvTemplatesPrefix()
    {
        // v1.0.0.x bug #509: 旧 default 的 LocalSourceDir = "envTemplates\<Kind>",
        // 跟 Settings.SystemTemplateLibraryDir (= 用户配的 ENVTemplate/) 一拼 →
        // <system_template_library_dir>/envTemplates/<Kind> 多一层嵌套。修法:
        // default 直接写 "<Kind>",新装用户走这条路。
        // v1.0.0.x (2026-08-29): SwarmUI 已下线,8 个 built-in 减到 6 个;
        // +4 个 GitHub-clone 视频/图像生成模板(HunyuanVideo/LTXVideo/CogVideoX/Fooocus)
        // 共 10 个;+HivisionIDPhotos → 11 个。
        var s = new Settings();
        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("ComfyUI", s.Templates["ComfyUI"].LocalSourceDir);
        Assert.Equal("Forge", s.Templates["Forge"].LocalSourceDir);
        Assert.Equal("OpenVoice", s.Templates["OpenVoice"].LocalSourceDir);
        Assert.Equal("Whisper", s.Templates["Whisper"].LocalSourceDir);
        Assert.Equal("CoquiTTS", s.Templates["CoquiTTS"].LocalSourceDir);
        Assert.Equal("Bark", s.Templates["Bark"].LocalSourceDir);
        Assert.Equal("HunyuanVideo", s.Templates["HunyuanVideo"].LocalSourceDir);
        Assert.Equal("LTXVideo", s.Templates["LTXVideo"].LocalSourceDir);
        Assert.Equal("CogVideoX", s.Templates["CogVideoX"].LocalSourceDir);
        Assert.Equal("Fooocus", s.Templates["Fooocus"].LocalSourceDir);
        Assert.Equal("HivisionIDPhotos", s.Templates["HivisionIDPhotos"].LocalSourceDir);
    }

    [Fact]
    public void Apply_PreExistingEnvTemplatesPrefix_NormalizedToKind()
    {
        // v1.0.0.x bug #509: 已 shipped 用户的 settings.inf 里 4 个 GitHub AI voice
        // (OpenVoice/Whisper/CoquiTTS/Bark) 已经被种了 "envTemplates\<Kind>" →
        // Apply 时通过 NormalizeBuiltInTemplatePaths 替换成 "<Kind>"。
        // Custom templates(用户手填的 LocalSourceDir)一律不动。
        var s = new Settings();
        s.Templates["OpenVoice"] = new TemplateConfig
        {
            Name = "OpenVoice",
            Kind = "OpenVoice",
            LocalSourceDir = Path.Combine("envTemplates", "OpenVoice"),
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/myshell-ai/OpenVoice.git",
            EntryScript = "api.py",
        };
        s.Templates["MyCustom"] = new TemplateConfig
        {
            Name = "MyCustom",
            Kind = "MyCustom",
            LocalSourceDir = Path.Combine("envTemplates", "MyCustom"),
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "main.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("OpenVoice", s.Templates["OpenVoice"].LocalSourceDir);
        // custom kind 不被 normalize,保持原值
        Assert.Equal(Path.Combine("envTemplates", "MyCustom"),
            s.Templates["MyCustom"].LocalSourceDir);
    }

    [Fact]
    public void Apply_PreExistingAbsolutePath_TrailingKindSegment_NormalizedToKind()
    {
        // v1.0.0.x bug #572: 老 settings.json 时代(<c>template_comfyui_dir</c> 字段)写死了
        // 项目根下的绝对路径(如 <c>D:\ToolDevelop\ComfyUI\ComfyUI</c>)。
        // #569 后所有内置模板统一应落在 ENVTemplate/<Kind> 下,但 shipped 用户的 settings.inf
        // 里 ComfyUI.LocalSourceDir = "D:\ToolDevelop\ComfyUI\ComfyUI" 仍指向老位置,
        // TemplatePathResolver.Resolve 把绝对路径原样返回 → 跑老位置而不是 ENVTemplate/ComfyUI。
        // NormalizeBuiltInTemplatePaths 现在检测「绝对路径末段 == <Kind>」→ 改成相对 "<Kind>",
        // 后续 Resolve 锚定到 SystemTemplateLibraryDir (=ENVTemplate) 拼出正确路径。
        //
        // 8 个 built-in kind 都测一遍,顺带验证非 builtin(custom)不动。
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = @"D:\ToolDevelop\ComfyUI\ComfyUI",  // 老位置
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "main.py",
        };
        // v1.0.0.x: A1111 不再是 built-in,不再进 NormalizeBuiltInTemplatePaths 测试夹具。
        s.Templates["MyCustom"] = new TemplateConfig
        {
            Name = "MyCustom",
            Kind = "MyCustom",
            LocalSourceDir = @"D:\custom\MyCustom",  // custom kind 末段也匹配,但不动
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "x.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("ComfyUI", s.Templates["ComfyUI"].LocalSourceDir);
        // custom 模板不动 — 用户可能故意放在别处
        Assert.Equal(@"D:\custom\MyCustom", s.Templates["MyCustom"].LocalSourceDir);
    }

    [Fact]
    public void Apply_PreExistingAbsolutePath_TrailingNotMatchingKind_LeftAlone()
    {
        // v1.0.0.x bug #572 防御性:绝对路径但末段 ≠ <Kind>(用户故意放到不同名目录里)不归一化。
        // 例如 ComfyUI 模板被放到 <projectRoot>/stable-diffusion 目录 — 不是命名冲突,而是
        // 用户主动选择,不要重写。
        var s = new Settings();
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = @"D:\my-software\stable-diffusion",  // 末段 != Kind
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "main.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\my-software\stable-diffusion",
            s.Templates["ComfyUI"].LocalSourceDir);
    }

    // --- v1.0.0.x (2026-08-29): PruneDeprecatedBuiltInKinds(A1111 + SwarmUI cleanup) ---

    [Fact]
    public void Apply_PreExistingA1111Entry_RemovedFromTemplatesDict()
    {
        // v1.0.0.x (2026-08-29): 用户 settings.inf persist 老 A1111 条目(已下线,
        // Stability-AI/stablediffusion 仓库从 github 移除)。Apply 末尾调
        // PruneDeprecatedBuiltInKinds 把 A1111 从 s.Templates dict 清掉 ——
        // TemplateManagementViewModel 直接遍历 _settings.Templates,不清的话
        // 模板管理 UI 仍会显示这个已下线的模板。
        var s = new Settings();
        s.Templates["A1111"] = new TemplateConfig
        {
            Name = "A1111", Kind = "A1111",
            LocalSourceDir = "A1111",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "webui.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.DoesNotContain(s.Templates, kvp => kvp.Key == "A1111");
    }

    [Fact]
    public void Apply_PreExistingSwarmUIEntry_RemovedFromTemplatesDict()
    {
        // v1.0.0.x (2026-08-29): SwarmUI 已下线(commit 0845067 hard-remove ——
        // ProcessLauncher Python 假设对 SwarmUI .NET app functional break)。
        // 老 settings.inf 残留的 SwarmUI 条目 Apply 时清掉,模板管理 UI 不再显示。
        var s = new Settings();
        s.Templates["SwarmUI"] = new TemplateConfig
        {
            Name = "SwarmUI", Kind = "SwarmUI",
            LocalSourceDir = "SwarmUI",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "Launch-windows.bat",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.DoesNotContain(s.Templates, kvp => kvp.Key == "SwarmUI");
    }

    [Fact]
    public void Apply_PreExistingDeprecatedAndCustomEntries_CustomKeptDeprecatedRemoved()
    {
        // v1.0.0.x (2026-08-29): PruneDeprecatedBuiltInKinds 只清 DeprecatedBuiltInKinds
        // 列出的 kind,custom templates(用户手填的,kind 不在 deprecated 列表)一律不动。
        var s = new Settings();
        s.Templates["A1111"] = new TemplateConfig
        {
            Name = "A1111", Kind = "A1111",
            LocalSourceDir = "A1111",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "webui.py",
        };
        s.Templates["SwarmUI"] = new TemplateConfig
        {
            Name = "SwarmUI", Kind = "SwarmUI",
            LocalSourceDir = "SwarmUI",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "Launch-windows.bat",
        };
        s.Templates["MyCustomA1111Like"] = new TemplateConfig
        {
            Name = "MyCustomA1111Like", Kind = "MyCustomA1111Like",
            LocalSourceDir = "MyCustomA1111Like",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "main.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.DoesNotContain(s.Templates, kvp => kvp.Key == "A1111");
        Assert.DoesNotContain(s.Templates, kvp => kvp.Key == "SwarmUI");
        // user-named kind 即使跟 deprecated 撞名也不算 — 必须 kind 字符串精确等于才 prune
        Assert.Contains(s.Templates, kvp => kvp.Key == "MyCustomA1111Like");
    }

    [Fact]
    public void Apply_RunMultipleTimes_IdempotentDoesNotCorruptState()
    {
        // v1.0.0.x (2026-08-29): PruneDeprecatedBuiltInKinds 是幂等的 ——
        // 多次跑(理论上不会发生,但 SettingsDefaults.Apply 会被同一 session 调多次)
        // 不该触发异常、不该影响其他 built-in / custom 模板。
        var s = new Settings();
        s.Templates["A1111"] = new TemplateConfig
        {
            Name = "A1111", Kind = "A1111",
            LocalSourceDir = "A1111",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "webui.py",
        };
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI", Kind = "ComfyUI",
            LocalSourceDir = "CustomComfyuiDir",
            SourceKind = TemplateSourceKind.Local,
            EntryScript = "main.py",
        };

        SettingsDefaults.Apply(s, ProjectRoot);
        var firstPass = new System.Collections.Generic.Dictionary<string, TemplateConfig>(
            s.Templates, System.StringComparer.Ordinal);

        SettingsDefaults.Apply(s, ProjectRoot);
        var secondPass = new System.Collections.Generic.Dictionary<string, TemplateConfig>(
            s.Templates, System.StringComparer.Ordinal);

        Assert.Equal(firstPass.Count, secondPass.Count);
        foreach (var kvp in firstPass)
        {
            Assert.True(secondPass.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value.Name, secondPass[kvp.Key].Name);
        }
        Assert.DoesNotContain(secondPass, kvp => kvp.Key == "A1111");
        Assert.Contains(secondPass, kvp => kvp.Key == "ComfyUI");
    }
}
