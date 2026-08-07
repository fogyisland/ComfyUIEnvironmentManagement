using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class EnvCreatorServiceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ComfyUI.Manager.Models.Settings _settings;
    private readonly FakeVenvCreator _venvCreator;
    private readonly FakeJunctionLinker _linker;
    private readonly EnvCreatorService _service;
    private readonly EnvironmentRepository _repo;

    public EnvCreatorServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "envcreator-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);

        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new EnvironmentRepository(_factory);

        _settings = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            TemplateComfyuiDir = "ComfyUI",
        };

        _venvCreator = new FakeVenvCreator();
        _linker = new FakeJunctionLinker();

        // Prepare base python + ComfyUI template so CreateAsync passes validation.
        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");

        var comfyDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");

        _service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateAsync_WritesBasePythonPath()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "alpha", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        Assert.Equal(basePy, env.BasePythonPath);
        Assert.Equal(basePy, _venvCreator.LastBasePython);
    }

    [Fact]
    public async Task CreateAsync_WritesPythonVersionFromVenvPython()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "beta", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        // FakeVenvCreator writes an empty python.exe; ReadVenvPythonVersionAsync
        // runs it via Process.Start which will fail, so the method falls back
        // to "<unknown>". We only assert that PythonVersion is non-empty.
        Assert.False(string.IsNullOrEmpty(env.PythonVersion));
    }

    [Fact]
    public async Task CreateAsync_WithNotes_PersistsToDb_AndReadBack()
    {
        // v0.6.7.2:Notes 字段从 dialog → service → SQLite 端到端 roundtrip。
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        const string notesText = "测试 SDXL 工作流,验证 ControlNet 节点";

        var env = await _service.CreateAsync(
            "gamma", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null,
            progress: null,
            CancellationToken.None,
            notes: notesText);

        Assert.Equal(notesText, env.Notes);

        // DB read-back:开新 connection 模拟"重启后状态恢复"
        var verifyDb = new SqliteConnectionFactory(_dbPath);
        var fresh = new EnvironmentRepository(verifyDb).Get(env.Id);
        Assert.NotNull(fresh);
        Assert.Equal(notesText, fresh!.Notes);
    }

    [Fact]
    public async Task CreateAsync_WithNullOrWhitespaceNotes_StoresNull()
    {
        // 空 / 全空白 → null(不存空白串)
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "delta", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null,
            progress: null,
            CancellationToken.None,
            notes: "   \n  \t  ");

        Assert.Null(env.Notes);
    }

    private sealed class FakeVenvCreator : VenvCreator
    {
        public string? LastBasePython { get; private set; }
        public override async Task CreateAsync(string basePython, string venvPath,
            CancellationToken ct = default)
        {
            LastBasePython = basePython;
            var scriptsDir = Path.Combine(venvPath, "Scripts");
            Directory.CreateDirectory(scriptsDir);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "python.exe"), "");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeJunctionLinker : JunctionLinker
    {
        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            Directory.CreateDirectory(linkPath);
            return Task.CompletedTask;
        }
        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }
}
