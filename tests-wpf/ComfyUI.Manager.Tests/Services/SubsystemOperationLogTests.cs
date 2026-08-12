using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.12:subsystem operations hook into AppLogger.WriteOperation,写一行到
/// per-env operation log(Logs/operation-{envName}-{date}.log)。这里验证
/// BaseEnvInstaller / RequirementsInstaller 的安装起点都各自写一行带正确 tag 的事件。
///
/// ProcessLauncher 同样接,但 StartEnvAsync 需要真 python + port,不在这里测 —
/// 见 ProcessLauncherOperationLogTests。
/// </summary>
public sealed class SubsystemOperationLogTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly TestDb _db;
    private readonly EnvironmentRepository _envRepo;

    public SubsystemOperationLogTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"oplog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _db = new TestDb();
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id)
    {
        // venv python 用绝对虚拟路径,BaseEnvInstaller.FakeBaseEnvInstaller 不会真跑。
        var venv = Path.Combine(_tmpDir, id, "venv");
        Directory.CreateDirectory(venv);
        var fakePy = Path.Combine(venv, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tmpDir, id),
            VenvPath = venv,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(_tmpDir, id, "nodes"),
            // shared layout:requirements.txt 写在 RootPath(由 ResolveRequirementsCandidates 第一项匹配)。
            ComfyuiSource = Path.Combine(_tmpDir, id),
            Port = 8188,
            Status = "stopped",
        };
        Directory.CreateDirectory(env.CustomNodesPath);
        Directory.CreateDirectory(env.RootPath);
        _envRepo.Upsert(env);
        return env;
    }

    [Fact]
    public async Task BaseEnvInstaller_InstallAsync_WritesOperationLogBedInstallStart()
    {
        var env = SeedEnv("env-bed-op");
        var logger = new AppLogger(_tmpDir);
        var fake = new FakeBaseEnvInstaller(_envRepo, logger);
        fake.NextRunResult = new PipResult(0, false);

        var profile = new BaseEnvProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Description = "test",
            TorchVersion = "2.1.0",
            CudaVersion = "cu118",
            Channel = "stable",
            Packages = new List<string> { "torch", "torchaudio" },
        };

        var result = await fake.InstallAsync(
            new[] { env.Id }, profile, progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);

        // 日志路径 = <_tmpDir>/Logs/operation-{envName}-{today}.log
        var logPath = AppLogger.OperationLogPath(env.Name, DateTime.Now, _tmpDir);
        Assert.True(File.Exists(logPath), $"expected operation log at {logPath}");
        var lines = File.ReadAllLines(logPath);
        Assert.Contains(lines, l => l.Contains("[bed-install]") && l.Contains("profile=test-profile"));
    }

    [Fact]
    public async Task RequirementsInstaller_InstallAsync_WritesOperationLogRequirementsInstallStart()
    {
        var env = SeedEnv("env-req-op");
        File.WriteAllLines(Path.Combine(env.RootPath, "requirements.txt"),
            new[] { "SQLAlchemy", "transformers" });

        var logger = new AppLogger(_tmpDir);
        // 用真 RequirementsInstaller — 它在 InstallAsync 第 81 行写 WriteOperation,
        // 早于 pip 跑。pip 会失败(fake-python.exe 不是真 python),但 WriteOperation 已写入。
        var installer = new RequirementsInstaller(logger);

        // 不在乎 pip 结果 — WriteOperation 在 InstallAsync 入口立即写,后面 pip
        // 抛 InvalidOperationException 也已写过。catch 是为了不让测试失败。
        try
        {
            await installer.InstallAsync(env, logProgress: null, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // fake-python.exe 无法启动,符合预期
        }

        var logPath = AppLogger.OperationLogPath(env.Name, DateTime.Now, _tmpDir);
        Assert.True(File.Exists(logPath), $"expected operation log at {logPath}");
        var lines = File.ReadAllLines(logPath);
        Assert.Contains(lines, l => l.Contains("[requirements-install]") && l.Contains("start"));
    }

    [Fact]
    public async Task SubsystemEvents_DifferentEnvs_WriteToDifferentOperationLogFiles()
    {
        // 验证多 env 不会互相污染 — operation-{envName}.log 按 envName 拆。
        var envA = SeedEnv("env-a");
        var envB = SeedEnv("env-b");
        File.WriteAllLines(Path.Combine(envA.RootPath, "requirements.txt"), new[] { "x" });
        File.WriteAllLines(Path.Combine(envB.RootPath, "requirements.txt"), new[] { "y" });

        var logger = new AppLogger(_tmpDir);

        // envA: requirements install(用真 RequirementsInstaller,吞掉 pip 启动失败)
        var installerA = new RequirementsInstaller(logger);
        try
        {
            await installerA.InstallAsync(envA, logProgress: null, CancellationToken.None);
        }
        catch (InvalidOperationException) { }

        // envB: BED install(FakeBaseEnvInstaller 走 InstallAsync 真逻辑,只 override RunPipAsync)
        var fakeBedB = new FakeBaseEnvInstaller(_envRepo, logger);
        fakeBedB.NextRunResult = new PipResult(0, false);
        var profile = new BaseEnvProfile
        {
            Id = "p", Name = "p", Description = "p",
            TorchVersion = "2.1.0", CudaVersion = "cu118", Channel = "stable",
            Packages = new List<string> { "torch" },
        };
        await fakeBedB.InstallAsync(new[] { envB.Id }, profile, progress: null, CancellationToken.None);

        var pathA = AppLogger.OperationLogPath(envA.Name, DateTime.Now, _tmpDir);
        var pathB = AppLogger.OperationLogPath(envB.Name, DateTime.Now, _tmpDir);
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        var linesA = File.ReadAllLines(pathA);
        var linesB = File.ReadAllLines(pathB);
        Assert.Contains(linesA, l => l.Contains("[requirements-install]"));
        Assert.DoesNotContain(linesA, l => l.Contains("[bed-install]"));
        Assert.Contains(linesB, l => l.Contains("[bed-install]"));
        Assert.DoesNotContain(linesB, l => l.Contains("[requirements-install]"));
    }

    // ---- helpers / fakes ----

    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        private readonly EnvironmentRepository _repo;
        public PipResult NextRunResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }

        public FakeBaseEnvInstaller(EnvironmentRepository envRepo, AppLogger? logger = null)
            : base(envRepo, logger)
        {
            _repo = envRepo;
        }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent,
            CancellationToken ct)
        {
            RunCount++;
            onLine("fake-pip-line");
            return Task.FromResult(NextRunResult);
        }
    }
}
