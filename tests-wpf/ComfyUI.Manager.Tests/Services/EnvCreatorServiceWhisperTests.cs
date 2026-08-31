using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31):锁 <see cref="EnvCreatorService"/> step 7.7(Whisper 分支)
/// 触发 <c>pip install openai-whisper</c> — 镜像 LTX-2 step 7.5 uv sync factory pattern
/// (<c>Func&lt;venvPython, ct, Task&gt;?</c> ctor 注入)。其它 kind(ComfyUI / Forge /
/// LTXVideo / CoquiTTS / Bark / etc)完全不触发(回归保护 — 防 Whisper 分支污染)。
///
/// Whisper 是 PyPI 包,没有 monorepo uv sync(LTXVideo 模式)。不装包直接
/// <c>python -m whisper</c> → ImportError → WaitForCliCompletionAsync 抛
/// ServiceLaunchException("Whisper CLI 退出失败")。env-create 阶段装包解决。
/// </summary>
public sealed class EnvCreatorServiceWhisperTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;

    public EnvCreatorServiceWhisperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "envcreator-whisper-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "state.db"));
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
            // 让 TemplatePathResolver 把 "Whisper" 解析到 <_root>/Whisper
            SystemTemplateLibraryDir = projectRoot,
        };
        return s;
    }

    private TemplateConfig WhisperTemplate() => new()
    {
        Kind = "Whisper",
        Name = "Whisper",
        LocalSourceDir = "Whisper",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/openai/whisper.git",
        EntryScript = "whisper",
        EntryArgs = "",  // v1.0.0.x (2026-08-31):CLI 必填 audio + --model 在 UserExtraArgs
        ModelsSubdir = "",
        UserExtraArgs = "",
    };

    [Fact]
    public async Task CreateAsync_Whisper_CallsWhisperInstallAsync()
    {
        var srcDir = Path.Combine(_root, "Whisper");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["Whisper"] = WhisperTemplate();

        var captured = new RecordingWhisperInstall();
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
            // step 7.7:Whisper install — 注入 recorder 验证触发 + 入参。
            whisperInstallAsync: (venvPython, _) => captured.Invoke(venvPython));

        await svc.CreateAsync(
            "whisper-env", WhisperTemplate(),
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.Equal(1, captured.CallCount);
    }

    [Fact]
    public async Task CreateAsync_Whisper_WhisperInstallReceivesVenvPythonPath()
    {
        var srcDir = Path.Combine(_root, "Whisper");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["Whisper"] = WhisperTemplate();

        var captured = new RecordingWhisperInstall();
        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            whisperInstallAsync: (venvPython, _) => captured.Invoke(venvPython));

        await svc.CreateAsync(
            "whisper-env", WhisperTemplate(),
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "envs", "whisper-env", "venv", "Scripts", "python.exe"),
            captured.LastVenvPython);
    }

    [Fact]
    public async Task CreateAsync_ComfyUI_SkipsWhisperInstall()
    {
        // 回归保护:ComfyUI kind 不触发 Whisper install step(防 short-circuit 漏判)
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

        var captured = new RecordingWhisperInstall();
        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            whisperInstallAsync: (venvPython, _) => captured.Invoke(venvPython));

        await svc.CreateAsync(
            "cui-env", settings.Templates["ComfyUI"],
            pythonExe: null,
            port: null,
            notes: null,
            sourceOverride: srcDir,
            ct: CancellationToken.None);

        Assert.Equal(0, captured.CallCount);
    }

    [Fact]
    public async Task CreateAsync_WhisperInstallThrows_RollsBackEnvDirectory()
    {
        // 镜像 step 7.5 uv sync 失败语义:env-create 整体失败 + 回滚 env 根目录
        var srcDir = Path.Combine(_root, "Whisper");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["Whisper"] = WhisperTemplate();

        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            pipUpgradeAsync: (_, _) => Task.CompletedTask,
            pipInstallWheelAsync: (_, _) => Task.CompletedTask,
            // 注入 throwing fake — 模拟网络失败 / pip install 报 exit code != 0
            whisperInstallAsync: (_, _) => throw new InvalidOperationException("pip install failed (fake)"));

        var ex = await Assert.ThrowsAsync<EnvCreatorService.CreateEnvException>(() =>
            svc.CreateAsync(
                "whisper-fail", WhisperTemplate(),
                pythonExe: null,
                port: null,
                notes: null,
                sourceOverride: srcDir,
                ct: CancellationToken.None));

        Assert.Equal("WHISPER_INSTALL_FAILED", ex.Code);
        // 验证 env 根目录被回滚 — Directory.Exists(<envRoot>) 应空
        var envRoot = Path.Combine(_root, "envs", "whisper-fail");
        Assert.False(Directory.Exists(envRoot));
    }
}

/// <summary>
/// 测试用 Whisper install recorder — 捕获 venvPython 入参 + 调用次数。
/// FakeVenvCreator / FakeJunctionLinker 复用 EnvCreatorServiceLtx2StepsTests 的定义
/// (同 namespace ComfyUI.Manager.Tests.Services,同 assembly)。
/// </summary>
internal sealed class RecordingWhisperInstall
{
    public int CallCount { get; private set; }
    public string? LastVenvPython { get; private set; }

    public Task Invoke(string venvPython)
    {
        CallCount++;
        LastVenvPython = venvPython;
        return Task.CompletedTask;
    }
}