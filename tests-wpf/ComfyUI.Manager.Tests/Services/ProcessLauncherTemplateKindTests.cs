using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ProcessLauncherTemplateKindTests : System.IDisposable
{
    private readonly string _projectRoot;

    public ProcessLauncherTemplateKindTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "proc-launch-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// v1.0.0.x: BuildStartCommand 现在校验入口脚本存在性(Spec §9),所以测试要 pre-create
    /// 假 entry script 文件，否则新逻辑会先抛 FileNotFound 跳过原生 test 断言。
    /// v1.0.0.x: BuildStartCommand 用 env.RootPath 派生 envRoot(env-create 时存的绝对路径,
    /// dev/release 一致),不再从 projectRoot + "envs" 拼 — 测试要 mirror 真实场景。
    /// </summary>
    private void CreateFakeEntryFile(string envName, string entryScript, string? absoluteRootPath = null)
    {
        var envRoot = absoluteRootPath ?? Path.Combine(_projectRoot, "envs", envName);
        Directory.CreateDirectory(envRoot);
        File.WriteAllText(Path.Combine(envRoot, entryScript), "# fake");
    }

    [Fact]
    public void BuildStartCommand_ComfyUI_UsesMainPyArgsAndUserExtras()
    {
        // G7: <venvPython> <EntryScript> <EntryArgs-with-{port}> [UserExtraArgs]
        var env = new Environment
        {
            Id = "e1", Name = "e1", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9000,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port} --listen 0.0.0.0",
                UserExtraArgs = "--preview-method auto",
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("e1", "main.py");

        var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "e1", "venv", "Scripts", "python.exe"), exe);
        Assert.Equal(Path.Combine(_projectRoot, "envs", "e1", "main.py"), args.File);
        Assert.Contains("--port 9000 --listen 0.0.0.0", args.ArgsString);
        Assert.Contains("--preview-method auto", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_Forge_UsesWebuiPy()
    {
        // v1.0.0.x: A1111 已下线,Forge 用 webui.py 作 entry script,沿用相同 entry path。
        var env = new Environment
        {
            Id = "e2", Name = "e2", Status = "stopped",
            TemplateKind = "Forge",
            Port = 9001,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Forge",
                EntryScript = "webui.py",
                EntryArgs = "--port {port}",
                UserExtraArgs = "--xformers",
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("e2", "webui.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "e2", "webui.py"), args.File);
        Assert.Contains("--port 9001", args.ArgsString);
        Assert.Contains("--xformers", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_Custom_UsesSnapshotEntryScript()
    {
        var env = new Environment
        {
            Id = "e3", Name = "e3", Status = "stopped",
            TemplateKind = "MySwarmUI",
            Port = 9002,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "MySwarmUI",
                EntryScript = "swarmui-launcher.sh",
                EntryArgs = "--listen 0.0.0.0",
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("e3", "swarmui-launcher.sh");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "e3", "swarmui-launcher.sh"), args.File);
        Assert.Contains("--listen 0.0.0.0", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_MissingSnapshot_FallsBackToSettingsTemplates()
    {
        // v1.0.0.x: 用 Forge 替代 A1111 测 fallback —— 都是 shipped local 内置,
        // missing snapshot 时回落到 Settings.Templates["Forge"].EntryScript (webui.py)。
        // backward compat: old env rows may not have snapshot — fallback to current Settings.Templates
        var env = new Environment
        {
            Id = "e4", Name = "e4", Status = "stopped",
            TemplateKind = "Forge",
            Port = 9003,
            TemplateConfigSnapshot = null,
        };
        var settings = new Settings();
        settings.Templates["Forge"] = new TemplateConfig
        {
            Kind = "Forge",
            EntryScript = "webui.py",
            EntryArgs = "--port {port}",
        };
        CreateFakeEntryFile("e4", "webui.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "e4", "webui.py"), args.File);
        Assert.Contains("--port 9003", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_MissingEntryScript_ThrowsWithSpecSection9Message()
    {
        // Spec §9: "入口脚本不存在: <envRoot>/<EntryScript>" — fake path 让文件不存在
        // 验证 error message 含 spec 文本 + entry script 路径。
        var env = new Environment
        {
            Id = "e5", Name = "e5", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9004,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
            },
        };
        var settings = new Settings();
        // not creating the entry file — File.Exists will be false

        var ex = Assert.Throws<System.InvalidOperationException>(
            () => ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot));
        Assert.Contains("入口脚本不存在", ex.Message);
        Assert.Contains("main.py", ex.Message);
    }

    [Fact]
    public void BuildStartCommand_EnvsDirEmpty_FallsBackToEnvsSubdir()
    {
        // Settings.EnvsDir 默认 = ""(未配置),BuildStartCommand fallback 必须把它当
        // "用默认子目录 envs",否则 entry script 路径会缺 envs 段(snapshot 没有时)。
        // 锁住 `string.IsNullOrEmpty` 兜底 — `?? "envs"` 那种写法只抓 null 不抓 ""。
        var env = new Environment
        {
            Id = "e6", Name = "e6", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9005,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
            },
        };
        var settings = new Settings { EnvsDir = "" };  // 空串 = 未配置,跟默认一致
        CreateFakeEntryFile("e6", "main.py");          // 放在 <projectRoot>/envs/e6/main.py

        var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        // entry script 必须落在 <projectRoot>/envs/e6/main.py,而不是 <projectRoot>/e6/main.py
        Assert.Equal(Path.Combine(_projectRoot, "envs", "e6", "main.py"), args.File);
        Assert.Equal(Path.Combine(_projectRoot, "envs", "e6", "venv", "Scripts", "python.exe"), exe);
    }

    /// <summary>
    /// v1.0.0.x: dev build 启动按钮路径 bug 回归 — 用户 2026-08-26 反馈「点击环境启动
    /// 还是指向了错误的路径」。原因 <see cref="ProcessLauncher.BuildStartCommand"/> 硬编码
    /// <c>Path.Combine(projectRoot, "envs", env.Name)</c>,但 dev build projectRoot 来自
    /// <c>Environment.ProcessPath</c> = bin/Debug/net8.0-windows,不是真正的项目根。
    /// 修复:envRoot 改用 <see cref="Environment.RootPath"/>(env-create 时 EnvCreatorService
    /// 存的绝对路径,跟 settings.EnvsDir 解析结果一致)。本测试断言 RootPath 存在时,
    /// 拼装走 RootPath,不再受 projectRoot 影响。
    /// </summary>
    [Fact]
    public void BuildStartCommand_RootPathSet_UsesAbsoluteRootPathIgnoringProjectRoot()
    {
        // dev build 场景:projectRoot = bin dir(≠ 真实项目根),env.RootPath = env 真实绝对路径。
        var fakeProjectRoot = Path.Combine(Path.GetTempPath(), "fake-bin-" + System.Guid.NewGuid().ToString("N")[..8]);
        var realEnvRoot = Path.Combine(Path.GetTempPath(), "real-env-" + System.Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(realEnvRoot);
            File.WriteAllText(Path.Combine(realEnvRoot, "main.py"), "# fake");

            var env = new Environment
            {
                Id = "e-dev", Name = "faceswap", Status = "stopped",
                TemplateKind = "ComfyUI",
                Port = 9100,
                RootPath = realEnvRoot,  // 绝对(env-create 时 EnvCreatorService 存的)
                TemplateConfigSnapshot = new TemplateConfig
                {
                    Kind = "ComfyUI",
                    EntryScript = "main.py",
                    EntryArgs = "--port {port}",
                },
            };
            var settings = new Settings();

            var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: fakeProjectRoot);

            // 关键断言:entry file 必须落在 realEnvRoot(从 env.RootPath 派生),
            // 不在 fakeProjectRoot/envs/faceswap/ 里。
            Assert.Equal(Path.Combine(realEnvRoot, "main.py"), args.File);
            Assert.NotEqual(Path.Combine(fakeProjectRoot, "envs", "faceswap", "main.py"), args.File);
            // venv python 也用 envRoot 派生。
            Assert.Equal(Path.Combine(realEnvRoot, "venv", "Scripts", "python.exe"), exe);
        }
        finally
        {
            try { Directory.Delete(realEnvRoot, recursive: true); } catch { }
            try { Directory.Delete(fakeProjectRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// v1.0.0.x: 兜底 — env.RootPath 为空(legacy env 行)时,BuildStartCommand 应该 fallback
    /// 到 <c>Path.Combine(projectRoot, settings.EnvsDir ?? "envs", env.Name)</c> 旧行为,
    /// 不要抛 NRE。
    /// </summary>
    [Fact]
    public void BuildStartCommand_RootPathEmpty_FallsBackToProjectRoot()
    {
        CreateFakeEntryFile("e-fallback", "main.py");

        var env = new Environment
        {
            Id = "e-fallback", Name = "e-fallback", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9101,
            RootPath = null,  // legacy env row
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
            },
        };
        var settings = new Settings();  // EnvsDir = null

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "e-fallback", "main.py"), args.File);
    }

    // ====================================================================
    // v1.0.0.x (2026-08-29):Forge env 显式拼 --ckpt-dir / --vae-dir /
    // --lora-dir / --controlnet-dir 等指向 Settings.DefaultModelsDirectory。
    // 用户原话:"在 forge 中写入启动参数中写入路径到 models --ckpt-dir"
    // 目的:让 webui.py 启动时直接读共享本地模型库,避免 Forge 默认 a1111_home/
    // models/ 跟用户共享盘的实际 ComfyUI 风格子目录(checkpoints/ vae/ loras/
    // controlnet/)命名约定不同导致 "You do not have any model!" 错误。
    //
    // v1.0.0.x (2026-08-29) followup:子目录名用 ComfyUI 风格而非 Forge/A1111 默认
    // (Stable-diffusion/ VAE/ lora/ ControlNet/)— 用户共享盘实际是 ComfyUI 布局,
    // 直接拼 ComfyUI 子目录(webui.py 只看目录里的 .safetensors,不关心目录名)。
    // ====================================================================

    [Fact]
    public void BuildStartCommand_Forge_WithDefaultModelsDirectory_AppendsCkptDirEtcArgs()
    {
        // G10: Forge + DefaultModelsDirectory 非空 → EntryArgs 末尾拼 --ckpt-dir /
        // --vae-dir / --lora-dir / --controlnet-dir 4 个绝对路径(ComfyUI 风格子目录:
        // checkpoints/ vae/ loras/ controlnet/)。浏览器禁自动开走 env var SD_WEBUI_RESTARTING=1
        // (ForgeExtraEnvironmentVariables tests 覆盖),不走 --no-autolaunch CLI flag —
        // 实测 Forge fork 移除了 A1111 的 bool_py2 自定义 argparse action,导致
        // `webui.py: error: unrecognized arguments: --no-autolaunch` 直接 crash。
        var env = new Environment
        {
            Id = "e-forge", Name = "ForgeUI", Status = "stopped",
            TemplateKind = "Forge",
            Port = 9001,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Forge",
                EntryScript = "webui.py",
                EntryArgs = "--port {port} --api",
            },
        };
        var modelsDir = Path.Combine(_projectRoot, "shared-models");
        var settings = new Settings { DefaultModelsDirectory = modelsDir };
        CreateFakeEntryFile("ForgeUI", "webui.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        // 4 个参数 + 4 个 ComfyUI 风格绝对路径子目录
        Assert.Contains("--ckpt-dir", args.ArgsString);
        Assert.Contains("--vae-dir", args.ArgsString);
        Assert.Contains("--lora-dir", args.ArgsString);
        Assert.Contains("--controlnet-dir", args.ArgsString);
        Assert.Contains(Path.Combine(modelsDir, "checkpoints"), args.ArgsString);
        Assert.Contains(Path.Combine(modelsDir, "vae"), args.ArgsString);
        Assert.Contains(Path.Combine(modelsDir, "loras"), args.ArgsString);
        Assert.Contains(Path.Combine(modelsDir, "controlnet"), args.ArgsString);
        // v1.0.0.x (2026-08-29):Forge 禁自动开浏览器 — 用户原话 "他启动后自动打开网页,
        // 在这里我们不推荐"。**实撤回 --no-autolaunch CLI flag**(2026-08-29 followup):
        // Forge fork 移除 A1111 的 bool_py2 自定义 argparse action,导致
        // `webui.py: error: unrecognized arguments: --no-autolaunch` (exit code 2)
        // 直接 fail。改纯靠 SD_WEBUI_RESTARTING=1 env var(ForgeExtraEnvironmentVariables
        // tests 覆盖)。这里只锁住 CLI flag 不要再回归。
        Assert.DoesNotContain("--no-autolaunch", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_Forge_WithEmptyDefaultModelsDirectory_DoesNotAppendCkptArgs()
    {
        // 用户没配 DefaultModelsDirectory → 不要拼 --ckpt-dir 等(env 内 models/ 目录按
        // Forge 默认 a1111_home/models/ 走),也避免 launcher 引用空字符串触发 webui.py
        // 启动错。--no-autolaunch 也不再拼(同上,Forge 报 unrecognized arguments)。
        var env = new Environment
        {
            Id = "e-forge2", Name = "ForgeUI2", Status = "stopped",
            TemplateKind = "Forge",
            Port = 9002,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Forge",
                EntryScript = "webui.py",
                EntryArgs = "--port {port}",
            },
        };
        var settings = new Settings { DefaultModelsDirectory = "" };  // 未配
        CreateFakeEntryFile("ForgeUI2", "webui.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.DoesNotContain("--ckpt-dir", args.ArgsString);
        Assert.DoesNotContain("--vae-dir", args.ArgsString);
        Assert.DoesNotContain("--lora-dir", args.ArgsString);
        Assert.DoesNotContain("--controlnet-dir", args.ArgsString);
        // --no-autolaunch 也不拼(Forge fork 移除 bool_py2 自定义 action,直接 crash webui.py)
        Assert.DoesNotContain("--no-autolaunch", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_NonForge_WithDefaultModelsDirectory_DoesNotAppendCkptArgs()
    {
        // ComfyUI env 不需要 --ckpt-dir(走自己的 models/checkpoints/ 约定);同样的
        // DefaultModelsDirectory 不应触发 Forge-only 注入。锁住这个 boundary —
        // Forge-specific 行为不能 spill 到 ComfyUI env(后者有自己的 args 模板)。
        // --no-autolaunch 也不拼(ComfyUI 启动本来就不弹浏览器,且该 flag 在 Forge 报
        // unrecognized arguments)。
        var env = new Environment
        {
            Id = "e-comfy", Name = "ComfyMain", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9003,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port} --listen 0.0.0.0",
            },
        };
        var modelsDir = Path.Combine(_projectRoot, "shared-models");
        var settings = new Settings { DefaultModelsDirectory = modelsDir };
        CreateFakeEntryFile("ComfyMain", "main.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.DoesNotContain("--ckpt-dir", args.ArgsString);
        Assert.DoesNotContain("--vae-dir", args.ArgsString);
        Assert.DoesNotContain("--lora-dir", args.ArgsString);
        Assert.DoesNotContain("--controlnet-dir", args.ArgsString);
        Assert.DoesNotContain("--no-autolaunch", args.ArgsString);
    }

    // ====================================================================
    // v1.0.0.x (2026-08-29):Forge env 启动禁用 webui.py 自动开浏览器。
    // 用户原话:"他启动后自动打开网页,在这里我们不推荐"。
    // 机制:ForgeExtraEnvironmentVariables(Environment) → IDictionary,StartEnvAsync
    // 把 entries 灌到 ProcessStartInfo.EnvironmentVariables;Forge webui.py 检测
    // `os.getenv('SD_WEBUI_RESTARTING') != '1'`(A1111 PR #11037 官方机制,Foge 扩展
    // 到所有启动场景),env var = "1" → 跳过整段 auto_launch_browser 逻辑 → 不弹浏览器。
    // 用户用我们 app 的 OpenBrowser 按钮手动开。
    // ====================================================================

    [Fact]
    public void ForgeExtraEnvironmentVariables_Forge_SetsSdWebuiRestarting()
    {
        // G11: Forge env → 返回 dict 含 SD_WEBUI_RESTARTING="1",供 StartEnvAsync
        // 灌到 ProcessStartInfo.EnvironmentVariables → webui.py 检测后跳过 auto_launch。
        var env = new Environment
        {
            Id = "e-forge-env", Name = "ForgeUI", Status = "stopped",
            TemplateKind = "Forge",
            Port = 9001,
            TemplateConfigSnapshot = null,
        };

        var extras = ProcessLauncher.ForgeExtraEnvironmentVariables(env);

        Assert.Single(extras);
        Assert.True(extras.ContainsKey("SD_WEBUI_RESTARTING"));
        Assert.Equal("1", extras["SD_WEBUI_RESTARTING"]);
    }

    [Fact]
    public void ForgeExtraEnvironmentVariables_NonForge_DoesNotSetSdWebuiRestarting()
    {
        // ComfyUI 不走 webui.py 的 auto-launch 路径(ComfyUI 是无 GUI 后端,自己不带
        // 默认浏览器启动逻辑),不需要这个 env var。锁住 boundary:Forge-specific
        // 行为不 spill 到其它 template。
        var env = new Environment
        {
            Id = "e-comfy", Name = "ComfyMain", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 9002,
            TemplateConfigSnapshot = null,
        };

        var extras = ProcessLauncher.ForgeExtraEnvironmentVariables(env);

        Assert.Empty(extras);
    }

    [Fact]
    public void ForgeExtraEnvironmentVariables_EmptyTemplateKind_DoesNotSetSdWebuiRestarting()
    {
        // 老 env SQLite template_kind 列可能 null(legacy data),兜底走 "ComfyUI"
        // 默认行为 — 不要凭空 set SD_WEBUI_RESTARTING(避免误伤)。
        var env = new Environment
        {
            Id = "e-legacy", Name = "Legacy", Status = "stopped",
            TemplateKind = null,
            Port = 9003,
            TemplateConfigSnapshot = null,
        };

        var extras = ProcessLauncher.ForgeExtraEnvironmentVariables(env);

        Assert.Empty(extras);
    }
}