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
    /// </summary>
    private void CreateFakeEntryFile(string envName, string entryScript)
    {
        var envRoot = Path.Combine(_projectRoot, "envs", envName);
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
}