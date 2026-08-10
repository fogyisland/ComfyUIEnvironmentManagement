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
/// v0.6.10 T3:EnvCreatorService 步骤 5.6(链接默认 Models)的测试。
/// SharedModelsDirectory 非空时 5.6 不执行(Shared 优先,Default 兜底)。
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
            TemplateComfyuiDir = "ComfyUI",
            SharedModelsDirectory = "",
            DefaultModelsDirectory = "",
        };
        _linker = new RecordingJunctionLinker();

        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");
        var comfyDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");
        Directory.CreateDirectory(Path.Combine(comfyDir, "models"));

        _service = new EnvCreatorService(
            _factory, new FakeVenvCreator(), _linker, _settings, _rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private string BasePython => Path.Combine(_rootDir, "python", "3.10", "python.exe");
    private string ComfyuiSource => Path.Combine(_rootDir, "ComfyUI");

    private string CreateDir(string name)
    {
        var dir = Path.Combine(_rootDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsModelsLink(string linkPath) =>
        linkPath.EndsWith(Path.Combine("ComfyUI", "models"), StringComparison.OrdinalIgnoreCase);

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
            "env-1", "independent", BasePython, ComfyuiSource, port: null);

        Assert.Contains(_linker.CreatedLinks,
            pair => IsModelsLink(pair.Link) && SameDir(pair.Target, defaultModelsDir));
    }

    [Fact]
    public async Task DefaultModelsDirectoryEmpty_DoesNotTouchModels()
    {
        _settings.DefaultModelsDirectory = "";

        await _service.CreateAsync(
            "env-2", "independent", BasePython, ComfyuiSource, port: null);

        Assert.DoesNotContain(_linker.CreatedLinks, pair => IsModelsLink(pair.Link));
    }

    [Fact]
    public async Task DefaultModelsDirectoryAndSharedModelsDirectory_BothSet_SharedModelsWins()
    {
        var shared = CreateDir("shared-models");
        var defaultDir = CreateDir("default-models");
        _settings.SharedModelsDirectory = shared;
        _settings.DefaultModelsDirectory = defaultDir;

        await _service.CreateAsync(
            "env-3", "independent", BasePython, ComfyuiSource, port: null);

        // 步骤 5.6 因 SharedModelsDirectory 非空被跳过 —— 只有 5.5 建的那一条 models 链
        var modelsLinks = _linker.CreatedLinks.Where(p => IsModelsLink(p.Link)).ToList();
        var only = Assert.Single(modelsLinks);
        Assert.True(SameDir(only.Target, shared),
            $"expected target {shared}, got {only.Target}");
        Assert.False(SameDir(only.Target, defaultDir),
            "DefaultModelsDirectory 不应覆盖 SharedModelsDirectory");
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
