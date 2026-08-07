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
/// v0.6.7.3 T3:EnvCreatorService 步骤 5.5(链接共享 Models)的测试。
/// </summary>
public sealed class EnvCreatorServiceSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ComfyUI.Manager.Models.Settings _settings;
    private readonly RecordingJunctionLinker _linker;
    private readonly EnvCreatorService _service;

    public EnvCreatorServiceSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "envcreator-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);

        _settings = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            TemplateComfyuiDir = "ComfyUI",
        };
        _linker = new RecordingJunctionLinker();

        // Prepare base python + ComfyUI template
        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");
        var comfyDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");
        // 模板自带 models/ —— shared 布局下会透过 ComfyUI junction 可见,
        // 让步骤 5.5 走到"先删既有 models 再重链"的分支。
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

    private string CreateSharedModelsDir()
    {
        var shared = Path.Combine(_rootDir, "shared-models");
        Directory.CreateDirectory(shared);
        return shared;
    }

    private static bool IsModelsLink(string linkPath) =>
        linkPath.EndsWith(Path.Combine("ComfyUI", "models"), StringComparison.OrdinalIgnoreCase);

    private static bool SameDir(string a, string b) =>
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task SharedModelsDirectorySet_JunctionsModels()
    {
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        await _service.CreateAsync(
            "env-1", "independent", BasePython, ComfyuiSource, port: null);

        Assert.Contains(_linker.CreatedLinks,
            pair => IsModelsLink(pair.Link) && SameDir(pair.Target, shared));
    }

    [Fact]
    public async Task SharedModelsEmpty_DoesNotTouchModels()
    {
        _settings.SharedModelsDirectory = "";

        await _service.CreateAsync(
            "env-2", "independent", BasePython, ComfyuiSource, port: null);

        Assert.DoesNotContain(_linker.CreatedLinks, pair => IsModelsLink(pair.Link));
    }

    [Fact]
    public async Task SharedModelsDirectoryDoesNotExist_FailsAndRollsBack()
    {
        _settings.SharedModelsDirectory = Path.Combine(_rootDir, "ghost-models");

        var ex = await Assert.ThrowsAsync<EnvCreatorService.CreateEnvException>(() =>
            _service.CreateAsync(
                "env-3", "independent", BasePython, ComfyuiSource, port: null));

        Assert.Equal("CREATE_MODELS_LINK_FAILED", ex.Code);

        // 回滚:env 根目录被删
        Assert.False(Directory.Exists(Path.Combine(_rootDir, "envs", "env-3")));

        // 回滚:库里没有 env-3 行(Upsert 在步骤 8,晚于 5.5)
        var repo = new EnvironmentRepository(_factory);
        Assert.DoesNotContain(repo.ListAll(), e => e.Name == "env-3");
    }

    [Fact]
    public async Task IndependentLayout_AlsoJunctions()
    {
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        await _service.CreateAsync(
            "env-4", "independent", BasePython, ComfyuiSource, port: null);

        Assert.Contains(_linker.CreatedLinks, pair => IsModelsLink(pair.Link));
    }

    [Fact]
    public async Task SharedLayout_ReplacesExistingModelsWithSharedTarget()
    {
        var shared = CreateSharedModelsDir();
        _settings.SharedModelsDirectory = shared;

        // shared 布局:步骤 5 把 <env-root>/ComfyUI junction 到 comfyuiSource,
        // 于是 <env-root>/ComfyUI/models 已经存在(指向 <comfyui-source>/models)。
        // 步骤 5.5 必须先删掉它,再重链到 SharedModelsDirectory。
        await _service.CreateAsync(
            "env-5", "shared", BasePython, ComfyuiSource, port: null);

        var modelsLink = Path.Combine(_rootDir, "envs", "env-5", "ComfyUI", "models");

        // 最终 models 链接的 target 是 SharedModelsDirectory,不是 <comfyui-source>/models
        var modelsLinks = _linker.CreatedLinks.Where(p => IsModelsLink(p.Link)).ToList();
        var last = Assert.Single(modelsLinks);
        Assert.True(SameDir(last.Link, modelsLink),
            $"expected link at {modelsLink}, got {last.Link}");
        Assert.True(SameDir(last.Target, shared),
            $"expected target {shared}, got {last.Target}");
        Assert.False(SameDir(last.Target, Path.Combine(ComfyuiSource, "models")));
    }

    /// <summary>
    /// RecordingJunctionLinker:记录每次 CreateAsync 的 (link, target),并模拟真实
    /// junction 的两个关键行为 —— target 不存在时抛异常;target 的子目录透过 link 可见。
    /// </summary>
    private sealed class RecordingJunctionLinker : JunctionLinker
    {
        public System.Collections.Generic.List<(string Link, string Target)> CreatedLinks { get; } = new();

        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            CreatedLinks.Add((linkPath, target));
            // 若 target 不存在,模拟真实 mklink 失败行为
            if (!Directory.Exists(target))
                throw new JunctionCreationException(
                    $"junction target 不存在: {target}", -1, "");

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            // 模拟 junction:target 的子目录透过 link 可见
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
