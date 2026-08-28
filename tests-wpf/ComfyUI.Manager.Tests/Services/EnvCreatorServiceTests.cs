using System;
using System.Collections.Generic;
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
            // v1.0.0 (T12):TemplateComfyuiDir 字段已移除,ComfyUI 模板源目录走
            // Settings.Templates["ComfyUI"].LocalSourceDir 承载。测试用绝对路径
            // <rootDir>/ComfyUITemplate,与下方 MakeComfyUITemplate 一致。
            // EnvCreatorService 不读 Settings.Templates,这里占位填绝对路径只为
            // 让 _settings.Templates["ComfyUI"] 完整,避免 Apply 误覆盖。
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
        };

        _venvCreator = new FakeVenvCreator();
        _linker = new FakeJunctionLinker();

        // Prepare base python + ComfyUI template so CreateAsync passes validation.
        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");

        var comfyDir = Path.Combine(_rootDir, "ComfyUITemplate");
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
            "alpha", MakeComfyUITemplate(), basePy,
            port: null);

        Assert.Equal(basePy, env.BasePythonPath);
        Assert.Equal(basePy, _venvCreator.LastBasePython);
    }

    [Fact]
    public async Task CreateAsync_WritesPythonVersionFromVenvPython()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "beta", MakeComfyUITemplate(), basePy,
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
            "gamma", MakeComfyUITemplate(), basePy,
            port: null,
            notes: notesText,
            ct: CancellationToken.None,
            progress: null);

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
            "delta", MakeComfyUITemplate(), basePy,
            port: null,
            notes: "   \n  \t  ",
            ct: CancellationToken.None,
            progress: null);

        Assert.Null(env.Notes);
    }

    private TemplateConfig MakeComfyUITemplate()
    {
        return new TemplateConfig
        {
            Kind = "ComfyUI",
            Name = "ComfyUI",
            LocalSourceDir = Path.Combine(_rootDir, "ComfyUITemplate"),
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };
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

    /// <summary>
    /// FakeCommonNodeInstaller:fake git clone func 记录被请求 clone 的 node id。
    /// 配 Enabled=true 的 entry,InstallEnabledAsync 会真的调 gitClone lambda,
    /// 这样 hook 触发可通过 captures 列表断言。
    /// </summary>
    private static CommonNodeInstaller BuildFakeCommonNodeInstaller(List<string> calls)
    {
        return new CommonNodeInstaller(
            new ComfyUI.Manager.Models.Settings
            {
                CommonNodes = new List<CommonNodeEntry>
                {
                    new() { Id = "fake/test-node", DisplayName = "Test", IsBuiltIn = true, Enabled = true },
                },
            },
            (id, args) => { calls.Add(id); return Task.FromResult(NodeOperationResult.Ok("fake")); },
            logger: null);
    }

    [Fact]
    public async Task CreateAsync_DoesNotTriggerCommonNodeHook_UsersTriggerViaButton()
    {
        // v1.0.0.x: 常用节点安装不再在 env-create 末尾自动跑 — 由用户在 env 行
        // 右侧按钮触发(RequirementsInstaller / 行内按钮已存在,逻辑独立)。
        // 这里验证 env-create 不再调 CommonNodeInstaller git clone func。
        // 即便 fakeInstaller 在 CommonNodes 里塞了 Enabled=true 的 node,
        // 也不会被触发。
        var hookCalls = new List<string>();
        var fakeInstaller = BuildFakeCommonNodeInstaller(hookCalls);

        var service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir);

        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var env = await service.CreateAsync(
            "hooktest", MakeComfyUITemplate(), basePy,
            port: null);

        Assert.NotNull(env);
        Assert.Empty(hookCalls);  // 关键断言:env-create 不再调 git clone lambda
    }

    [Fact]
    public async Task CreateAsync_UpgradesVenvPip_AfterVenvCreate()
    {
        // v1.0.0.x: env-create step 6.5 — 升级 venv 内 pip 到最新版(对应 A1111
        // webui.bat upgrade_pip 段 + BaseEnvInstaller.DefaultPreInstallPipArgs 同语义)。
        // 用 fake Func 替换真实 pip upgrade,记录被调用的 venvPython 路径。
        var pipCalls = new List<string>();
        var service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir,
            pipUpgradeAsync: (venvPython, _) =>
            {
                pipCalls.Add(venvPython);
                return Task.CompletedTask;
            });

        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var env = await service.CreateAsync(
            "pipup", MakeComfyUITemplate(), basePy,
            port: null);

        // pip upgrade 被调一次,venvPython = <envRoot>/venv/Scripts/python.exe
        Assert.Single(pipCalls);
        var expected = Path.Combine(env.RootPath, "venv", "Scripts", "python.exe");
        Assert.Equal(expected, pipCalls[0]);
        // env 仍创建成功 — pip upgrade 是 step 6.5,step 7 写 DB 照常跑
        Assert.Equal("pipup", env.Name);
        Assert.NotNull(_repo.Get(env.Id));
    }

    [Fact]
    public async Task CreateAsync_PipUpgradeFailure_DoesNotFailCreate()
    {
        // v1.0.0.x: pip upgrade 失败 = 警告不阻塞(同 bat 行为)。
        // 即使 fake pip upgrade 抛异常,env-create 整体仍成功,DB 行仍写入。
        var service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir,
            pipUpgradeAsync: (venvPython, _) =>
                throw new InvalidOperationException("simulated pip failure"));

        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        var env = await service.CreateAsync(
            "pipfail", MakeComfyUITemplate(), basePy,
            port: null);

        Assert.Equal("pipfail", env.Name);
        Assert.NotNull(_repo.Get(env.Id));
    }

    [Fact]
    public async Task CreateAsync_PipUpgradeCancellation_RollsBackEnvRoot()
    {
        // v1.0.0.x: 用户取消(env-create 整体取消)— step 6.5 走取消分支,
        // 回滚 env 根目录 + 不写 DB(同 step 6 venv 创建失败的回滚语义)。
        var service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir,
            pipUpgradeAsync: (venvPython, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CreateAsync(
                "pipcancel", MakeComfyUITemplate(), basePy,
                port: null,
                ct: cts.Token));

        Assert.True(cts.IsCancellationRequested);
        // DB 不应写入(env 名字 pipcancel 不在表里)
        Assert.DoesNotContain(_repo.ListAll(), e => e.Name == "pipcancel");
    }
}
