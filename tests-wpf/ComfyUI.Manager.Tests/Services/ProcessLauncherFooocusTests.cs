using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31):Fooocus entry mode 切换 — BuildStartCommand 在
/// Kind=="Fooocus" 且 FooocusEntryMode==Stable 时改用 entry.py(替 snapshot.EntryScript 的
/// entry_with_update.py),其它 kind 跟其它 mode 完全不受影响。
/// </summary>
public sealed class ProcessLauncherFooocusTests : IDisposable
{
    private readonly string _projectRoot;

    public ProcessLauncherFooocusTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "proc-launch-fooocus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// v1.0.0.x: BuildStartCommand 校验入口脚本存在性(Spec §9),测试 pre-create 假 entry script
    /// 否则新逻辑会先抛 FileNotFound。BuildStartCommand 用 env.RootPath 派生 envRoot。
    /// </summary>
    private void CreateFakeEntryFile(string envName, string entryScript, string? absoluteRootPath = null)
    {
        var envRoot = absoluteRootPath ?? Path.Combine(_projectRoot, "envs", envName);
        Directory.CreateDirectory(envRoot);
        File.WriteAllText(Path.Combine(envRoot, entryScript), "# fake");
    }

    [Fact]
    public void BuildStartCommand_Fooocus_AutoUpdate_UsesEntryWithUpdate()
    {
        // 默认:EntryScript = entry_with_update.py + FooocusEntryMode = AutoUpdate
        // → 走 snapshot.EntryScript,跟 v1.0.0 行为 100% 一致
        var env = new Environment
        {
            Id = "fooocus-au", Name = "FooocusAU", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.AutoUpdate,
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusAU", "entry_with_update.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusAU", "entry_with_update.py"), args.File);
        Assert.DoesNotContain("entry.py", Path.GetFileName(args.File));   // 不是 entry.py
    }

    [Fact]
    public void BuildStartCommand_Fooocus_Stable_UsesEntryPy()
    {
        // Stable 模式:EntryScript 仍是 entry_with_update.py(快照冻结),但 mode override 用 entry.py
        var env = new Environment
        {
            Id = "fooocus-st", Name = "FooocusStable", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",   // 快照仍记 entry_with_update.py
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.Stable,   // 但 mode = Stable → 替 entry.py
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusStable", "entry.py");   // 实际磁盘上只有 entry.py

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusStable", "entry.py"), args.File);
        Assert.DoesNotContain("entry_with_update.py", args.File);
    }

    [Fact]
    public void BuildStartCommand_Fooocus_EntryModeMissing_FallsBackToAutoUpdate()
    {
        // 老 settings 缺 fooocus_entry_mode 字段 → JsonStringEnumConverter 数字 fallback → 0 → AutoUpdate
        // 行为跟 v1.0.0完全一致
        var env = new Environment
        {
            Id = "fooocus-fb", Name = "FooocusFallback", Status = "stopped",
            TemplateKind = "Fooocus",
            Port = 7865,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "Fooocus",
                EntryScript = "entry_with_update.py",
                EntryArgs = "--port {port} --listen",
                FooocusEntryMode = FooocusEntryMode.AutoUpdate,   // 默认值
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("FooocusFallback", "entry_with_update.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "FooocusFallback", "entry_with_update.py"), args.File);
    }

    [Fact]
    public void BuildStartCommand_NonFooocusKind_StableModeSet_Unaffected()
    {
        // 其它 kind 误打 FooocusEntryMode = Stable(用户手抖) → kind check 短路,完全不影响
        var env = new Environment
        {
            Id = "comfy-st", Name = "ComfyStable", Status = "stopped",
            TemplateKind = "ComfyUI",
            Port = 8000,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
                FooocusEntryMode = FooocusEntryMode.Stable,   // 误打,应该被 kind check 短路掉
            },
        };
        var settings = new Settings();
        CreateFakeEntryFile("ComfyStable", "main.py");

        var (_, args) = ProcessLauncher.BuildStartCommand(env, settings, projectRoot: _projectRoot);

        Assert.Equal(Path.Combine(_projectRoot, "envs", "ComfyStable", "main.py"), args.File);
        Assert.DoesNotContain("entry.py", args.File);   // 不能替成 entry.py
    }
}
