using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-30):EnvCreatorService 集成 LTX-2 模板替换的 3 个新 step —
/// step 6.7 uv installer、step 7.5 uv sync(替 pip install -r)、step 7.6 wrapper generator。
///
/// Brief 备注(对 plan 源 task-6-brief.md 的修正):
/// <list type="bullet">
///   <item>原 brief <c>settings.LocalDataDir = _root</c> 是 typo,Settings 没这个字段;
///         实际应填 <c>SystemTemplateLibraryDir</c> 作 anchor。</item>
///   <item>原 brief 测试调 CreateAsync 没传 <c>pythonExe</c>;CreateAsync 加 <c>sourceOverride</c>
///         参数后,默认行为下 pythonExe 校验跳过(python 真存在性由调用方负责),其它路径
///         仍需 pythonExe 显式传。</item>
///   <item>原 brief RecordingUvInstaller 的 LastEnvRoot 写死 <c>"&lt;recorded&gt;"</c> 但断言
///         等于真实 envRoot;这里改为 factory 接收 envRoot 后构造 RecordingUvInstaller,
///         RecordingUvInstaller 把 envRoot 存为 ctor field。</item>
/// </list>
/// </summary>
public sealed class EnvCreatorServiceLtx2StepsTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvCreatorServiceLtx2StepsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "envcreator-ltx2-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "state.db"));
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ComfyUI.Manager.Models.Settings MakeSettings(string projectRoot)
    {
        var s = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            DefaultModelsDirectory = Path.Combine(projectRoot, "Models"),
            // 让 TemplatePathResolver 把 "LTX-Video" 解析到 <_root>/LTX-Video
            SystemTemplateLibraryDir = projectRoot,
        };
        return s;
    }

    private TemplateConfig LtxVideoTemplate(string projectRoot) => new()
    {
        Kind = "LTXVideo",
        Name = "LTXVideo",
        LocalSourceDir = "LTX-Video",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/Lightricks/LTX-2.git",
        EntryScript = "run-ltx2-distilled.bat",
        EntryArgs = "--output-path {env}/out.mp4",
        ModelsSubdir = "Models/ltx-2.5",
    };

    [Fact]
    public async Task CreateAsync_LTXVideo_CallsUvInstaller()
    {
        // fake 源 — SystemTemplateLibraryDir = _root + LocalSourceDir = "LTX-Video"
        // → TemplatePathResolver.Resolve → _root/LTX-Video
        var srcDir = Path.Combine(_root, "LTX-Video");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["LTXVideo"] = LtxVideoTemplate(_root);

        // 通过 factory 闭包捕获:传 envRoot 进 recorder,断言用同一实例。
        RecordingUvInstaller? capturedInstaller = null;
        var wrapper = new RecordingWrapperGenerator();
        var svc = new EnvCreatorService(
            _factory,
            new FakeVenvCreator(),
            new FakeJunctionLinker(),
            settings,
            projectRoot: _root,
            // step 6.5 / 6.6: FakeVenvCreator 写空 python.exe,真 pip 跑会 Process.Start 失败,
            // 注入 no-op 跳过(wheel seed 不在本测试范围)。
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            uvInstallerFactory: envRoot => capturedInstaller = new RecordingUvInstaller(envRoot),
            wrapperGeneratorFactory: envRoot => wrapper,
            // step 7.5 uv sync:测试机一般没装 uv.exe,注入 no-op 跳过 Process.Start。
            uvSyncAsync: (_, _) => Task.CompletedTask);

        await svc.CreateAsync(
            "ltx-env", LtxVideoTemplate(_root),
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.NotNull(capturedInstaller);
        Assert.Equal(1, capturedInstaller!.CallCount);
        Assert.Equal(Path.Combine(_root, "envs", "ltx-env"), capturedInstaller.LastEnvRoot);
    }

    [Fact]
    public async Task CreateAsync_LTXVideo_CallsWrapperGenerator()
    {
        var srcDir = Path.Combine(_root, "LTX-Video");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["LTXVideo"] = LtxVideoTemplate(_root);

        var uv = new RecordingUvInstaller();
        var wrapper = new RecordingWrapperGenerator();
        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            uvInstallerFactory: envRoot => uv,
            wrapperGeneratorFactory: envRoot => wrapper,
            uvSyncAsync: (_, _) => Task.CompletedTask);

        await svc.CreateAsync(
            "ltx-env", LtxVideoTemplate(_root),
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.Equal(1, wrapper.CallCount);
    }

    [Fact]
    public async Task CreateAsync_ComfyUI_SkipsUvInstaller()
    {
        var srcDir = Path.Combine(_root, "ComfyUI");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "main.py"), "# fake");
        File.WriteAllText(Path.Combine(srcDir, "requirements.txt"), "# empty");
        var settings = MakeSettings(_root);
        settings.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI", Name = "ComfyUI",
            LocalSourceDir = "ComfyUI",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git",
            EntryScript = "main.py", EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };

        var uv = new RecordingUvInstaller();
        var wrapper = new RecordingWrapperGenerator();
        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            uvInstallerFactory: envRoot => uv,
            wrapperGeneratorFactory: envRoot => wrapper,
            uvSyncAsync: (_, _) => Task.CompletedTask);

        await svc.CreateAsync(
            "cui-env", settings.Templates["ComfyUI"],
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.Equal(0, uv.CallCount);
        Assert.Equal(0, wrapper.CallCount);
    }
}

/// <summary>
/// 测试用 IUvInstaller fake — 记录调用次数 + 入参 envRoot(来自 factory 闭包)。
/// </summary>
internal sealed class RecordingUvInstaller : EnvCreatorService.IUvInstaller
{
    public int CallCount { get; private set; }
    public string? LastEnvRoot { get; }
    public string? LastReturnedExePath { get; private set; }

    public RecordingUvInstaller() : this(null) { }
    public RecordingUvInstaller(string? envRoot)
    {
        LastEnvRoot = envRoot;
    }

    public Task<string> InstallAsync(CancellationToken ct = default)
    {
        CallCount++;
        LastReturnedExePath = Path.Combine(LastEnvRoot ?? "<recorded>", "tools", "uv", "uv.exe");
        return Task.FromResult(LastReturnedExePath);
    }
}

/// <summary>
/// 测试用 ILtx2WrapperGenerator fake — 记录调用次数。
/// </summary>
internal sealed class RecordingWrapperGenerator : EnvCreatorService.ILtx2WrapperGenerator
{
    public int CallCount { get; private set; }
    public string? LastEnvRoot { get; private set; }

    public RecordingWrapperGenerator() { }

    public Task GenerateAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// FakeVenvCreator:跳过真 <c>python -m venv</c>,写空 Scripts/python.exe 让后续 step
/// 不会因为 venv python 不存在而抛 VenvCreationException。
/// </summary>
internal sealed class FakeVenvCreator : ComfyUI.Manager.Infrastructure.VenvCreator
{
    public override async Task CreateAsync(string basePython, string venvPath,
        CancellationToken ct = default)
    {
        var scriptsDir = Path.Combine(venvPath, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "python.exe"), "", ct);
    }
}

/// <summary>
/// FakeJunctionLinker:CopyDirectory 写成 no-op 目录创建;CreateAsync 创建目标占位目录。
/// </summary>
internal sealed class FakeJunctionLinker : ComfyUI.Manager.Infrastructure.JunctionLinker
{
    public override void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
    }

    public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Directory.CreateDirectory(linkPath);
        return Task.CompletedTask;
    }
}
