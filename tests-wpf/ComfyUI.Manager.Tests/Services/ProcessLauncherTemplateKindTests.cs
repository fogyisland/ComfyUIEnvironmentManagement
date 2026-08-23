using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ProcessLauncherTemplateKindTests
{
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

        var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e1\venv\Scripts\python.exe", exe);
        Assert.Equal(@"D:\fake\envs\e1\main.py", args.File);
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

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e2\webui.py", args.File);
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

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e3\swarmui-launcher.sh", args.File);
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

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: @"D:\fake");

        Assert.Equal(@"D:\fake\envs\e4\webui.py", args.File);
        Assert.Contains("--port 9003", args.ArgsString);
    }
}