using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-09-01) T26:锁 <see cref="ProcessLauncher.BuildStartCommand"/>
/// 对 Fooocus kind 自动追加 <c>--share</c> flag ——
/// gradio launch 在 localhost 不可访问(settings.inf 设了 http_proxy 时常见)
/// 会抛 ValueError("shareable link required"),Fooocus python 子进程 exit,
/// WPF 把 env.Status 回写 stopped。--share 让 gradio 创建临时公网 tunnel 绕开限制。
///
/// Fooocus webui.py:1124 读 `args_manager.args.share` 已支持该 flag。
/// 镜像 <see cref="ProcessLauncherWhisperTests"/> pattern + Forge kind-special
/// 分支(ProcessLauncher.cs:1016-1031)的 args 注入风格。
/// </summary>
public sealed class ProcessLauncherFooocusShareTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _envsDir;

    public ProcessLauncherFooocusShareTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "proc-launch-fooocus-share-" + Guid.NewGuid().ToString("N")[..8]);
        _envsDir = Path.Combine(_projectRoot, "envs");
        Directory.CreateDirectory(_envsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private Environment MakeFooocusEnv(
        string name = "FocusAll",
        string entryScript = "entry_with_update.py",
        string entryArgs = "--port {port} --listen",
        string userExtraArgs = "",
        FooocusEntryMode mode = FooocusEntryMode.AutoUpdate,
        int? port = 7860)
    {
        var envDir = Path.Combine(_envsDir, name);
        Directory.CreateDirectory(envDir);
        // 创建 entry script stub(让 BuildStartCommand File.Exists check 过)
        File.WriteAllText(Path.Combine(envDir, entryScript), "# stub");

        return new Environment
        {
            Id = "fooocus-test",
            Name = name,
            Status = "stopped",
            TemplateKind = "Fooocus",
            Port = port,
            RootPath = envDir,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = entryScript,
                EntryArgs = entryArgs,
                UserExtraArgs = userExtraArgs,
                FooocusEntryMode = mode,
            },
        };
    }

    [Fact]
    public void BuildStartCommand_FooocusKind_AppendsShareFlag_WhenEntryArgsEmpty()
    {
        // T26:Fooocus + 默认 entry_args(不含 --share)→ 自动追加
        var env = MakeFooocusEnv(entryArgs: "");
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        Assert.Contains("--share", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_FooocusKind_DoesNotDoubleShare_WhenUserProvided()
    {
        // T26:用户在 Settings 或 env-create dialog 已填 --share → 不重复追加
        // (命令行双 --share 触发 gradio argparse 警告但一般无害;防重复让 start args 干净)
        var env = MakeFooocusEnv(entryArgs: "--port {port} --share");
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        var occurrences = CountOccurrences(args.ArgsString, "--share");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildStartCommand_FooocusKind_DoesNotDoubleShare_WhenUserExtraArgsProvides()
    {
        // T26:EntryArgs 不含 --share 但 UserExtraArgs 含 → 不重复追加
        var env = MakeFooocusEnv(
            entryArgs: "--port {port} --listen",
            userExtraArgs: "--share");
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        var occurrences = CountOccurrences(args.ArgsString, "--share");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildStartCommand_NonFooocusKind_DoesNotAddShare()
    {
        // 回归保护:T26 只对 Fooocus kind 加 --share,其它 9 个 non-ComfyUI/Forge kind 不动
        var env = MakeFooocusEnv(name: "WhisperTest");
        env.TemplateKind = "Whisper";
        env.TemplateConfigSnapshot.Kind = "Whisper";
        env.TemplateConfigSnapshot.EntryScript = "whisper";  // Whisper short-circuit
        env.TemplateConfigSnapshot.FooocusEntryMode = FooocusEntryMode.AutoUpdate;
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        Assert.DoesNotContain("--share", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_FooocusStable_StillAppendsShare()
    {
        // T26:FooocusEntryMode.Stable 走 entry.py(替 entry_with_update.py),
        // --share 注入逻辑不受 mode 切换影响,稳定 + AutoUpdate 两种模式都该注入
        var env = MakeFooocusEnv(
            entryScript: "entry.py",
            entryArgs: "--port {port}",
            mode: FooocusEntryMode.Stable);
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        Assert.Contains("entry.py", args.File);
        Assert.Contains("--share", args.ArgsString);
    }

    [Fact]
    public void BuildStartCommand_FooocusKind_ShareAppendedAfterUserExtraArgs()
    {
        // T26:--share 拼在 UserExtraArgs 之后(否则命令行 argparse 可能解析错)
        // 顺序:"<EntryArgs> <UserExtraArgs> --share"
        var env = MakeFooocusEnv(
            entryArgs: "--port {port}",
            userExtraArgs: "--theme dark");
        var settings = new Settings();

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        Assert.Contains("--theme dark", args.ArgsString);
        Assert.Contains("--share", args.ArgsString);
        Assert.True(args.ArgsString.IndexOf("--theme dark", StringComparison.Ordinal)
            < args.ArgsString.IndexOf("--share", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}