using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.10 T3 + v0.6.11+ T2:EnvCreatorService 步骤 5.5(链接默认 Models)的测试。
/// v0.6.11+ T2 后唯一一条 DefaultModelsDirectory 链接路径(Shared 字段已删除,
/// 不再有 Shared/Default 二选一逻辑)。
/// </summary>
public sealed class EnvCreatorServiceDefaultModelsDirectoryTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ComfyUI.Manager.Models.Settings _settings;
    private readonly RecordingJunctionLinker _linker;
    private readonly EnvCreatorService _service;

    public EnvCreatorServiceDefaultModelsDirectoryTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "envcreator-default-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);

        _settings = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            // v1.0.0 (T12):TemplateComfyuiDir 字段已移除,ComfyUI 模板源目录走
            // Settings.Templates["ComfyUI"].LocalSourceDir。测试用绝对路径,
            // EnvCreatorService 不读 Settings.Templates,但占位填好避免 Apply 误覆盖。
            Templates =
            {
                ["ComfyUI"] = new ComfyUI.Manager.Models.TemplateConfig
                {
                    Kind = "ComfyUI",
                    Name = "ComfyUI",
                    LocalSourceDir = Path.Combine(_rootDir, "ComfyUITemplate"),
                    EntryScript = "main.py",
                    EntryArgs = "--port {port} --listen 0.0.0.0",
                    ModelsSubdir = "models",
                },
            },
            DefaultModelsDirectory = "",
        };
        _linker = new RecordingJunctionLinker();

        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");
        var comfyDir = Path.Combine(_rootDir, "ComfyUITemplate");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");
        Directory.CreateDirectory(Path.Combine(comfyDir, "models"));

        _service = new EnvCreatorService(
            _factory, new FakeVenvCreator(), _linker, _settings, _rootDir,
            // v1.0.0.x:env-create step 6.6 wheel seed 默认会跑真 `python -m pip
            // install wheel`,FakeVenvCreator 没创建真 venv → Process.Start 失败。
            // 注入 no-op 跳过 step 6.6(本测试不关心 wheel seed 行为)。
            pipInstallWheelAsync: NoOpWheel);
    }

    /// <summary>v1.0.0.x:step 6.6 wheel seed 的 no-op fake — 跟 EnvCreatorServiceTests
    /// 共享模式,只为本测试避免 Process.Start。</summary>
    private static Task NoOpWheel(string venvPython, CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private string BasePython => Path.Combine(_rootDir, "python", "3.10", "python.exe");
    private string ComfyuiSource => Path.Combine(_rootDir, "ComfyUITemplate");

    private string CreateDir(string name)
    {
        var dir = Path.Combine(_rootDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsModelsLink(string linkPath) =>
        // v1.0.0 T4:env-create 现在直接 copy template source 到 rootPath(models 目录也跟着进),
        // 不再有 rootPath/ComfyUI 子目录,所以 models junction 直接建在 rootPath/models。
        linkPath.EndsWith(Path.Combine("envs", "env-1", "models"), StringComparison.OrdinalIgnoreCase);

    private static bool SameDir(string a, string b) =>
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task DefaultModelsDirectorySet_JunctionsModels()
    {
        var defaultModelsDir = CreateDir("default-models");
        _settings.DefaultModelsDirectory = defaultModelsDir;

        await _service.CreateAsync(
            "env-1", MakeComfyUITemplate(), BasePython, port: null);

        Assert.Contains(_linker.CreatedLinks,
            pair => IsModelsLink(pair.Link) && SameDir(pair.Target, defaultModelsDir));
    }

    [Fact]
    public async Task DefaultModelsDirectoryEmpty_DoesNotTouchModels()
    {
        _settings.DefaultModelsDirectory = "";

        await _service.CreateAsync(
            "env-2", MakeComfyUITemplate(), BasePython, port: null);

        Assert.DoesNotContain(_linker.CreatedLinks, pair => IsModelsLink(pair.Link));
    }

    private TemplateConfig MakeComfyUITemplate()
    {
        return new TemplateConfig
        {
            Kind = "ComfyUI",
            Name = "ComfyUI",
            LocalSourceDir = ComfyuiSource,
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };
    }

    private sealed class RecordingJunctionLinker : JunctionLinker
    {
        public System.Collections.Generic.List<(string Link, string Target)> CreatedLinks { get; } = new();

        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            CreatedLinks.Add((linkPath, target));
            if (!Directory.Exists(target))
                throw new JunctionCreationException(
                    $"junction target 不存在: {target}", -1, "");

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            foreach (var sub in Directory.GetDirectories(target))
                Directory.CreateDirectory(Path.Combine(linkPath, Path.GetFileName(sub)));
            return Task.CompletedTask;
        }

        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }

    private sealed class FakeVenvCreator : VenvCreator
    {
        public override async Task CreateAsync(string basePython, string venvPath, CancellationToken ct = default)
        {
            var scriptsDir = Path.Combine(venvPath, "Scripts");
            Directory.CreateDirectory(scriptsDir);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "python.exe"), "");
        }
    }
}
