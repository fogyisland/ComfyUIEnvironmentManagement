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

public class EnvCreatorServiceMultiTemplateTests : IDisposable
{
    private readonly string _workRoot;
    private readonly string _srcDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Settings _settings;
    private readonly FakeJunctionLinker _linker;
    private readonly FakeVenvCreator _venvCreator;
    private readonly string _dbPath;

    public EnvCreatorServiceMultiTemplateTests()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "cmgr-envcreate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workRoot);

        // Build a fake template source (a single file inside)
        _srcDir = Path.Combine(_workRoot, "fake-template");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "main.py"), "print('hello')");
        File.WriteAllText(Path.Combine(_srcDir, ".gitkeep"), "");

        // Build a fake python.exe so EnvCreatorService validation passes
        // (File.Exists(pythonExe) check is required before venv creation).
        var pyDir = Path.Combine(_workRoot, "python");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");

        // Use the (string dbPath) overload so we control the temp path directly;
        // SqliteConnectionFactory.Open() ensures all needed columns via EnsureColumn.
        _dbPath = Path.Combine(_workRoot, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        // Force schema init by opening once.
        using (var conn = _factory.Open())
        {
            // Open() already runs InitSchemaIfMissing + EnsureColumn for template_kind
            // and template_config_snapshot. No-op SELECT just keeps the connection open.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
        }

        // Ensure settings have EnvsDir set so EnvCreatorService validation passes.
        _settings = new Settings
        {
            EnvsDir = "envs",
        };
        _linker = new FakeJunctionLinker();
        _venvCreator = new FakeVenvCreator();
    }

    /// <summary>Fake python.exe absolute path — service validates File.Exists on it.</summary>
    private string PythonExe => Path.Combine(_workRoot, "python", "python.exe");

    /// <summary>
    /// FakeVenvCreator:跳过真实 <c>python -m venv</c> 进程调用,直接写空 Scripts/python.exe
    /// 文件让 <see cref="EnvCreatorService"/> 的 ReadVenvPythonVersionAsync fallback 到 "&lt;unknown&gt;"。
    /// </summary>
    private sealed class FakeVenvCreator : VenvCreator
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
    /// FakeJunctionLinker:CopyDirectory 用真实 junction 做不到(需要 admin / mklink),
    /// 这里做"copy 文件 + 目录"语义,让 <see cref="EnvCreatorService"/> 觉得 copy 已完成。
    /// </summary>
    private sealed class FakeJunctionLinker : JunctionLinker
    {
        public override void CopyDirectory(string sourceDir, string destDir)
        {
            // 跟 Infrastructure/JunctionLinker.cs 的真实实现一样的递归 copy
            CopyRecursive(new DirectoryInfo(sourceDir), new DirectoryInfo(destDir));
        }

        private static void CopyRecursive(DirectoryInfo source, DirectoryInfo dest)
        {
            Directory.CreateDirectory(dest.FullName);
            foreach (var f in source.EnumerateFiles())
            {
                f.CopyTo(Path.Combine(dest.FullName, f.Name), overwrite: false);
            }
            foreach (var sub in source.EnumerateDirectories())
            {
                CopyRecursive(sub, new DirectoryInfo(Path.Combine(dest.FullName, sub.Name)));
            }
        }

        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            // 测试不覆盖 junction 路径(只覆盖 copy);DefaultModelsDirectory 走 junction 时
            // 这里简单地 create 一个空目录占位。DefaultModelsDirectory 测试不在本 fixture 范围。
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void CreateAsync_ComfyUIEnv_AlwaysCopiesSourceFiles()
    {
        // G3: always copy, no junction
        var envRepo = new EnvironmentRepository(_factory);
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models",
        };

        var env = svc.CreateAsync(
            name: "comfyEnv",
            templateConfig: template,
            pythonExe: PythonExe,
            port: 9000,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.NotNull(env);
        Assert.Equal("ComfyUI", env.TemplateKind);
        Assert.Equal("main.py", env.TemplateConfigSnapshot!.EntryScript);
        // File copied (not junctioned — verify by checking the file exists and is not a junction)
        Assert.True(File.Exists(Path.Combine(_workRoot, "envs", "comfyEnv", "main.py")));
    }

    [Fact]
    public void CreateAsync_A1111Env_SnapshotIncludesWebuiPy()
    {
        // G5: A1111 snapshot uses webui.py entry
        File.WriteAllText(Path.Combine(_srcDir, "webui.py"), "print('a1111')");
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "A1111",
            LocalSourceDir = _srcDir,
            EntryScript = "webui.py",
            EntryArgs = "--port {port}",
            ModelsSubdir = "models/Stable-diffusion",
        };

        var env = svc.CreateAsync(
            name: "a1111Env",
            templateConfig: template,
            pythonExe: PythonExe,
            port: 9001,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.Equal("A1111", env.TemplateKind);
        Assert.Equal("webui.py", env.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("models/Stable-diffusion", env.TemplateConfigSnapshot.ModelsSubdir);
        Assert.True(File.Exists(Path.Combine(_workRoot, "envs", "a1111Env", "webui.py")));
    }

    [Fact]
    public void CreateAsync_CustomEnv_AcceptsUserEntryScript()
    {
        // G12: Custom kind uses user-defined entry script
        File.WriteAllText(Path.Combine(_srcDir, "my-entry.sh"), "echo custom");
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "MySwarmUI",
            LocalSourceDir = _srcDir,
            EntryScript = "my-entry.sh",
            EntryArgs = "--listen 0.0.0.0",
            ModelsSubdir = "models",
        };

        var env = svc.CreateAsync(
            name: "customEnv",
            templateConfig: template,
            pythonExe: PythonExe,
            port: 9002,
            notes: "",
            ct: default).GetAwaiter().GetResult();

        Assert.Equal("MySwarmUI", env.TemplateKind);
        Assert.Equal("my-entry.sh", env.TemplateConfigSnapshot!.EntryScript);
    }

    [Fact]
    public void CreateAsync_SnapshotIsFrozen_NotAffectedBySettingsChanges()
    {
        // G2: snapshot is frozen at creation
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
        };
        var env = svc.CreateAsync("env1", template, PythonExe, 9000, "", default).GetAwaiter().GetResult();
        var snapshotBefore = env.TemplateConfigSnapshot!;

        // User edits template defaults AFTER env creation
        _settings.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI",
            EntryScript = "DIFFERENT.py",
            EntryArgs = "--totally-different",
        };

        // Reload env from DB — snapshot should be unchanged
        var repo = new EnvironmentRepository(_factory);
        var reloaded = repo.Get(env.Id)!;
        Assert.NotNull(reloaded);
        Assert.Equal("main.py", reloaded!.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("--port {port} --listen 0.0.0.0", reloaded.TemplateConfigSnapshot.EntryArgs);
    }

    [Fact]
    public void CreateAsync_DoesNotJunction_ComfyUISourceEvenWhenCallerExpectedShared()
    {
        // G3: even if caller passes shared-layout-style params, no junction
        var svc = new EnvCreatorService(_factory, _venvCreator, _linker, _settings, _workRoot);
        var template = new TemplateConfig
        {
            Kind = "ComfyUI",
            LocalSourceDir = _srcDir,
            EntryScript = "main.py",
        };
        var env = svc.CreateAsync("sharedTest", template, PythonExe, 9000, "", default).GetAwaiter().GetResult();

        var envComfyDir = Path.Combine(_workRoot, "envs", "sharedTest");
        // env dir exists with main.py file (copy), not a junction
        Assert.True(Directory.Exists(envComfyDir));
        Assert.True(File.Exists(Path.Combine(envComfyDir, "main.py")));
        // Sanity: source still has the same file (copy semantics)
        Assert.True(File.Exists(Path.Combine(_srcDir, "main.py")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }
}
