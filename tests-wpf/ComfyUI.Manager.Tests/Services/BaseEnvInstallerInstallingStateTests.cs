using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class BaseEnvInstallerInstallingStateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstallerInstallingStateTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private Environment SeedEnv(string id, string root, string? bedStatus = null)
    {
        var venv = Path.Combine(root, "venv");
        Directory.CreateDirectory(venv);
        var fakePy = Path.Combine(venv, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venv,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = "stopped",
            BedStatus = bedStatus,
        };
        _envRepo.Upsert(env);
        return env;
    }

    private static BaseEnvProfile DefaultProfile() => new()
    {
        Id = "pytorch-2.5.0-cu121-stable",
        Name = "PyTorch 2.5.0 + CUDA 12.1 (stable)",
        Description = "test",
        TorchVersion = "2.5.0",
        CudaVersion = "cu121",
        Channel = "stable",
        Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
    };

    [Fact]
    public async Task InstallAsync_WritesInstallingBeforePipRun_AndFlipsToDoneAfter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-installing-{Guid.NewGuid():N}");
        SeedEnv("env-a", root, bedStatus: null);
        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            // 当 pip 第一次被调时:检查 db 里 env.BedStatus 必须是 "installing"
            AssertOnFirstRun = () =>
            {
                var live = _envRepo.Get("env-a");
                Assert.NotNull(live);
                Assert.Equal("installing", live!.BedStatus);
                Assert.Null(live.BedProfileId);  // 还没回写
            },
            NextResult = new PipResult(0, false),
        };
        var progress = new RecordingProgress();

        await partial.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress, CancellationToken.None);

        Assert.True(partial.AssertOnFirstRunCalled);
        // 装完:env.BedStatus = "done", BedProfileId 已设
        var final = _envRepo.Get("env-a");
        Assert.Equal("done", final!.BedStatus);
        Assert.Equal("pytorch-2.5.0-cu121-stable", final.BedProfileId);
    }

    [Fact]
    public async Task InstallAsync_EnvMissing_DoesNotWriteInstalling_GoesStraightToFailed()
    {
        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            NextResult = new PipResult(0, false),
        };
        // 不 seed env:_envRepo.Get("ghost") 返 null → 直接 failed,不写 installing,不调 RunPipAsync
        var result = await partial.InstallAsync(
            new[] { "ghost" }, DefaultProfile(), null, CancellationToken.None);

        Assert.True(result.Cancelled is false);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("ghost", result.Failures.Keys);
        Assert.Equal(0, partial.RunCount);
    }

    [Fact]
    public async Task InstallAsync_PythonPathResolveFails_DoesNotWriteInstalling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-pyrfail-{Guid.NewGuid():N}");
        // seed env 但 VenvPath 指向不存在目录 → GetVenvPythonPath 抛 InvalidOperationException
        var env = SeedEnv("env-pyr", root);
        env.VenvPath = Path.Combine(root, "no-such-venv");
        env.PythonExecutable = null;  // 强制 fallback 到 VenvPath(不存在)
        _envRepo.Upsert(env);

        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            NextResult = new PipResult(0, false),
        };
        var result = await partial.InstallAsync(
            new[] { "env-pyr" }, DefaultProfile(), null, CancellationToken.None);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, partial.RunCount);  // 根本没进 pip
        // 终态:env.BedStatus="failed"
        var final = _envRepo.Get("env-pyr");
        Assert.Equal("failed", final!.BedStatus);
        Assert.NotNull(final.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_EnvRepoUpsertFailsDuringInstalling_DoesNotAbortInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-upsertfail-{Guid.NewGuid():N}");
        SeedEnv("env-u", root, bedStatus: null);
        // 包装 envRepo:调 Upsert 时第一次抛 SqliteException(模拟写 installing 失败),
        // 之后调用正常(让终态回写成功)。
        var flakyRepo = new FlakyEnvironmentRepository(_envRepo, failFirstUpsert: true);
        var partial = new FakeBaseEnvInstallerPartial(flakyRepo)
        {
            NextResult = new PipResult(0, false),
        };

        // 不应抛;装完仍 done
        var result = await partial.InstallAsync(
            new[] { "env-u" }, DefaultProfile(), null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        // 终态仍写成功(因为 upsert 失败只在第一次)
        var final = _envRepo.Get("env-u");
        Assert.Equal("done", final!.BedStatus);
    }

    // ---- helpers ----

    private sealed class FakeBaseEnvInstallerPartial : BaseEnvInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }
        public Action? AssertOnFirstRun { get; set; }
        public bool AssertOnFirstRunCalled { get; private set; }
        private readonly IEnvironmentRepository _repo;

        public FakeBaseEnvInstallerPartial(IEnvironmentRepository repo) : base(repo) { _repo = repo; }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            RunCount++;
            if (!AssertOnFirstRunCalled)
            {
                AssertOnFirstRun?.Invoke();
                AssertOnFirstRunCalled = true;
            }
            onLine("Looking in indexes: ...");
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FlakyEnvironmentRepository : IEnvironmentRepository
    {
        private readonly IEnvironmentRepository _inner;
        private int _upsertCalls;
        private readonly bool _failFirstUpsert;

        public FlakyEnvironmentRepository(IEnvironmentRepository inner, bool failFirstUpsert)
        {
            _inner = inner;
            _failFirstUpsert = failFirstUpsert;
        }

        public void Upsert(Environment env)
        {
            if (_failFirstUpsert && _upsertCalls++ == 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("simulated", 0);
            }
            _inner.Upsert(env);
        }

        public Environment? Get(string id) => _inner.Get(id);
        public System.Collections.Generic.List<Environment> ListAll() => _inner.ListAll();
    }

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        public List<BaseEnvProgress> Events { get; } = new();
        public void Report(BaseEnvProgress value) => Events.Add(value);
    }
}
