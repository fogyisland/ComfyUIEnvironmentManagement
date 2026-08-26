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
    public void BuildStartCommand_A1111_UsesWebuiPy()
    {
        var env = new Environment
        {
            Id = "e2", Name = "e2", Status = "stopped",
            TemplateKind = "A1111",
            Port = 9001,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "A1111",
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
        // backward compat: old env rows may not have snapshot — fallback to current Settings.Templates
        var env = new Environment
        {
            Id = "e4", Name = "e4", Status = "stopped",
            TemplateKind = "A1111",
            Port = 9003,
            TemplateConfigSnapshot = null,
        };
        var settings = new Settings();
        settings.Templates["A1111"] = new TemplateConfig
        {
            Kind = "A1111",
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
}