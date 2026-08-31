using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-08-31):锁 <see cref="ProcessLauncher.BuildStartCommand"/>
/// 对 Whisper kind 的 CLI 分支 — 用 <c>python -m whisper &lt;args&gt;</c> 调起,
/// skip <c>File.Exists</c> check + skip {port}/{models}/{env} 占位符替换。
///
/// Whisper 是 one-shot CLI 工具,不 bind port,<see cref="BuildStartCommand"/>
/// 在 Whisper 分支 short-circuit 返回 <c>("-m", "whisper ...")</c> 而非真实文件路径 —
/// <c>EntryScript="whisper"</c> 是 console-script 名(<c>Pip install openai-whisper</c>
/// 装到 <c>&lt;envRoot&gt;/venv/Scripts/whisper.exe</c>,不是 <c>&lt;envRoot&gt;/whisper</c>
/// 文件),File.Exists 直接 fail。
/// </summary>
public sealed class ProcessLauncherWhisperTests : IDisposable
{
    private readonly string _projectRoot;

    public ProcessLauncherWhisperTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "proc-launch-whisper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private Environment MakeEnv(TemplateConfig snapshot, string name = "WhisperEnv", int? port = 8000)
    {
        return new Environment
        {
            Id = "whisper-test",
            Name = name,
            Status = "stopped",
            TemplateKind = snapshot.Kind,
            Port = port,
            RootPath = Path.Combine(_projectRoot, "envs", name),
            TemplateConfigSnapshot = snapshot,
        };
    }

    // ===== Whisper kind: short-circuit 分支 =====

    [Fact]
    public void BuildStartCommand_WhisperKind_ReturnsDashMWhisper()
    {
        // EntryScript="whisper"(console-script 名,不是文件)— BuildStartCommand
        // Whisper 分支应 ignore EntryScript,返回 ("-m", "whisper ...")
        var env = MakeEnv(new TemplateConfig
        {
            Kind = "Whisper",
            EntryScript = "whisper",
            EntryArgs = "",
            UserExtraArgs = "",
        });
        var settings = new Settings();

        var (exe, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal("-m", args.File);
        Assert.Equal("whisper", args.ArgsString);
        // exe 是 venv python(不验证绝对路径,只验证非空)
        Assert.False(string.IsNullOrEmpty(exe));
        Assert.EndsWith("python.exe", exe);
    }

    [Fact]
    public void BuildStartCommand_WhisperKind_DoesNotThrowOnMissingEntryFile()
    {
        // 关键回归:不 pre-create <envRoot>/whisper 文件 —
        // Whisper 分支必须 short-circuit 跳过 File.Exists check。
        // 老逻辑会抛 InvalidOperationException("入口脚本不存在: <envRoot>/whisper")。
        var env = MakeEnv(new TemplateConfig
        {
            Kind = "Whisper",
            EntryScript = "whisper",
            EntryArgs = "",
        });
        var settings = new Settings();

        // 不抛即 PASS
        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal("-m", args.File);
    }

    [Fact]
    public void BuildStartCommand_WhisperKind_AppendsUserExtraArgs()
    {
        // UserExtraArgs 拼到 "whisper" 后(env-create dialog 用户填 audio file + --model)
        var env = MakeEnv(new TemplateConfig
        {
            Kind = "Whisper",
            EntryScript = "whisper",
            EntryArgs = "",
            UserExtraArgs = "--model tiny C:/audio/sample.wav",
        });
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal("-m", args.File);
        Assert.Contains("whisper", args.ArgsString);
        Assert.Contains("--model tiny C:/audio/sample.wav", args.ArgsString);
        // 顺序:"whisper" 在前,UserExtraArgs 在后
        Assert.True(args.ArgsString.IndexOf("whisper", StringComparison.Ordinal)
            < args.ArgsString.IndexOf("--model", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStartCommand_WhisperKind_DoesNotReplacePortPlaceholder()
    {
        // Whisper 分支 short-circuit 在 {port} 替换前 — 即使 EntryArgs 含 {port},
        // 也不替换为 port 数字(Whisper CLI 不需要 port)
        var env = MakeEnv(new TemplateConfig
        {
            Kind = "Whisper",
            EntryScript = "whisper",
            EntryArgs = "{port} should remain literal",
        }, port: 12345);
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Contains("{port}", args.ArgsString);
        Assert.DoesNotContain("12345", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_WhisperKind_SkipsForgeExtraArgs()
    {
        // Whisper 分支 short-circuit 在 Forge CLI args 拼接前 —
        // 即使用户误在 settings.ForgePaths 配路径,也不污染 Whisper 命令行
        var env = MakeEnv(new TemplateConfig
        {
            Kind = "Whisper",
            EntryScript = "whisper",
            EntryArgs = "",
        });
        var settings = new Settings
        {
            ForgePaths = new ForgePaths
            {
                CheckpointsDir = "C:/forge/checkpoints",
                LorasDir = "C:/forge/loras",
            },
        };

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.DoesNotContain("--ckpt-dir", args.ArgsString);
        Assert.DoesNotContain("--lora-dir", args.ArgsString);
        Assert.DoesNotContain("C:/forge", args.ArgsString);
    }

    // ===== 非 Whisper kind: 回归保护 =====

    [Fact]
    public void BuildStartCommand_NonWhisperKind_FallsBackToEntryFile()
    {
        // ComfyUI kind 走原逻辑:EntryScript 文件存在 → 返回 file path。
        // 防 Whisper 分支过度蔓延污染其它 kind。
        var envRoot = Path.Combine(_projectRoot, "envs", "ComfyEnv");
        Directory.CreateDirectory(envRoot);
        File.WriteAllText(Path.Combine(envRoot, "main.py"), "# fake");
        var env = new Environment
        {
            Id = "comfy-regress",
            Name = "ComfyEnv",
            Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 8188,
            RootPath = envRoot,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
            },
        };
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(envRoot, "main.py"), args.File);
        Assert.Contains("--port 8188", args.ArgsString);
        Assert.DoesNotContain("-m", args.File);
    }
}
