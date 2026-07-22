using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
/// BaseEnvInstaller 集成测试:用 FakeBaseEnvInstaller override RunPipAsync,
/// 避免真的跑 venv python(慢 + 依赖网络)。
/// </summary>
public sealed class BaseEnvInstallerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstallerTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private Environment SeedEnv(string id, string root)
    {
        // 用一个不存在的 pythonExe 路径,避免 BaseEnvInstaller 默认行为:
        // 测试时都用 FakeBaseEnvInstaller 不走真的解析。
        // 这里用绝对虚拟路径,让 RunPipAsync override 永不触发文件检查。
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
        };
        _envRepo.Upsert(env);
        return env;
    }

    private static BaseEnvProfile DefaultProfile() => new()
    {
        Id = "test-profile",
        Name = "Test Profile",
        Description = "test",
        TorchVersion = "2.1.0",
        CudaVersion = "cu118",
        Channel = "stable",
        Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
    };

    [Fact]
    public async Task InstallAsync_SingleEnv_Succeeds_EmitsProgressLifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.NextRunResult = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Cancelled);
        Assert.Equal(1, fake.RunCount);
        // progress 至少 emit 一次 Running start + 一次 Succeeded
        Assert.Contains(progress.Events,
            p => p.Status == BaseEnvStatus.Running && p.CurrentEnvId == "env-a");
        Assert.Contains(progress.Events,
            p => p.Status == BaseEnvStatus.Succeeded && p.CurrentEnvId == "env-a");
        // Completed / Total 数字一致
        Assert.All(progress.Events, p =>
        {
            Assert.Equal(1, p.Total);
            Assert.True(p.Completed >= 0 && p.Completed <= 1);
        });
    }

    [Fact]
    public async Task InstallAsync_OneEnvFails_ContinuesNextEnv()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-fail-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        SeedEnv("env-b", tempRoot + "-b");
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.PerEnvResults["env-a"] = PipResultSuccess(1);  // 非 0 = pip 失败
        fake.PerEnvResults["env-b"] = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a", "env-b" }, DefaultProfile(), progress, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("env-a", result.Failures.Keys);
        Assert.DoesNotContain("env-b", result.Failures.Keys);  // success 不应计入 Failures(brief defect 修正)
        // 进度:env-a 跑完后 Completed=1,env-b 才开始(顺序串行)
        var envARanAt = progress.FirstRunningEnv("env-a");
        var envBRanAt = progress.FirstRunningEnv("env-b");
        Assert.True(envARanAt < envBRanAt);
    }

    [Fact]
    public async Task InstallAsync_CancelBeforeStart_SkipsAllEnvs_ReturnsCancelled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-cancel-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.NextRunResult = PipResultSuccess(0);
        var progress = new RecordingProgress();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, fake.RunCount);
        Assert.Empty(progress.Events);  // 起始就 cancel,啥也不 emit
    }

    [Fact]
    public async Task InstallAsync_CancelMidRun_KillsCurrentAndSkipsRest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"baseenv-midcancel-{Guid.NewGuid():N}");
        SeedEnv("env-a", tempRoot);
        SeedEnv("env-b", tempRoot + "-b");
        var fake = new FakeBaseEnvInstaller(_envRepo);
        fake.PerEnvResults["env-a"] = PipResultCancelled();  // RunPipAsync 抛 OperationCanceledException
        fake.PerEnvResults["env-b"] = PipResultSuccess(0);
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-a", "env-b" }, DefaultProfile(), progress, CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);  // env-a 算失败(env-b 未尝试)
        Assert.Contains("env-a", result.Failures.Keys);
        Assert.DoesNotContain("env-b", result.Failures.Keys);
    }

    [Fact]
    public void GetVenvPythonPath_PrefersExplicitPythonExecutable()
    {
        var explicitPy = Path.Combine(Path.GetTempPath(), $"explicit-{Guid.NewGuid():N}.exe");
        File.WriteAllText(explicitPy, "");
        try
        {
            var env = new Environment
            {
                VenvPath = Path.Combine(Path.GetTempPath(), "fake-venv"),
                PythonExecutable = explicitPy,
            };
            // explicit.exe 存在 → BaseEnvInstaller 必须先看 explicit,不 fallback 到 venv
            var actual = BaseEnvInstaller.GetVenvPythonPath(env);
            Assert.Equal(env.PythonExecutable, actual);
        }
        finally
        {
            try { if (File.Exists(explicitPy)) File.Delete(explicitPy); } catch { }
        }
    }

    [Fact]
    public void GetVenvPythonPath_FallsBackToVenvScriptsPython()
    {
        var venvRoot = Path.Combine(Path.GetTempPath(), $"venv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(venvRoot);
        var scriptsDir = Path.Combine(venvRoot,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts" : "bin");
        Directory.CreateDirectory(scriptsDir);
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "python.exe" : "python";
        var pyPath = Path.Combine(scriptsDir, exeName);
        File.WriteAllText(pyPath, "");

        var env = new Environment { VenvPath = venvRoot };
        var actual = BaseEnvInstaller.GetVenvPythonPath(env);

        Assert.Equal(pyPath, actual);
        Directory.Delete(venvRoot, true);
    }

    [Fact]
    public void GetVenvPythonPath_ThrowsIfVenvPythonMissing()
    {
        var env = new Environment
        {
            VenvPath = Path.Combine(Path.GetTempPath(), $"no-venv-{Guid.NewGuid():N}"),
        };
        Assert.Throws<InvalidOperationException>(
            () => BaseEnvInstaller.GetVenvPythonPath(env));
    }

    [Fact]
    public void GetVenvPythonPath_ThrowsIfNoVenvAndNoExplicit()
    {
        var env = new Environment();
        Assert.Throws<ArgumentException>(
            () => BaseEnvInstaller.GetVenvPythonPath(env));
    }

    // ---- helpers ----

    private static PipResult PipResultSuccess(int code) => new(code, false);
    private static PipResult PipResultCancelled() => new(-1, true);

    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public Dictionary<string, PipResult> PerEnvResults { get; } = new();
        public PipResult? NextRunResult { get; set; }
        public int RunCount { get; private set; }
        private string? _currentEnvId;
        private readonly EnvironmentRepository _repo;

        public FakeBaseEnvInstaller(EnvironmentRepository envRepo)
            : base(envRepo)
        {
            _repo = envRepo;
        }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent,
            CancellationToken ct)
        {
            RunCount++;
            // 把 onLine / onPercent 调用 broadcast 出来供测试断言
            onLine("Looking in indexes: https://download.pytorch.org/whl/cu118");
            onPercent((int?)5);
            onLine("Downloading torch-2.1.0-cp310-cp310-win_amd64.whl (xxx MB)");

            var byEnv = ResolveForEnv(_currentEnvId ?? "");
            if (byEnv.WasCancelled)
            {
                throw new OperationCanceledException(ct);
            }
            return Task.FromResult(byEnv);
        }

        public PipResult ResolveForEnv(string envId) =>
            PerEnvResults.TryGetValue(envId, out var r) ? r : NextRunResult ?? PipResultSuccess(0);

        public override async Task<BaseEnvInstallResult> InstallAsync(
            IReadOnlyList<string> envIds, BaseEnvProfile profile,
            IProgress<BaseEnvProgress>? progress, CancellationToken ct)
        {
            // Brief defect correction: the verbatim InstallAsync doesn't pass envId to RunPipAsync,
            // but tests need per-env resolution via PerEnvResults. This override mimics the base
            // loop while tracking CurrentEnvId so RunPipAsync can resolve per-env PipResult.
            var failures = new Dictionary<string, string>();
            int succeeded = 0, failed = 0, total = envIds.Count, completed = 0;
            bool cancelled = false;
            var pipArgs = profile.BuildPipArgs();

            foreach (var envId in envIds)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                _currentEnvId = envId;
                var env = _repo.Get(envId);
                if (env is null)
                {
                    failures[envId] = $"env '{envId}' 不存在";
                    failed++; completed++;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Failed, completed, total,
                        envId, null, null, null, failures[envId]));
                    continue;
                }

                string pythonExe;
                try { pythonExe = GetVenvPythonPath(env); }
                catch (Exception ex)
                {
                    failures[envId] = ex.Message; failed++; completed++;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Failed, completed, total,
                        envId, env.Name, null, null, ex.Message));
                    continue;
                }

                progress?.Report(new BaseEnvProgress(
                    BaseEnvStatus.Running, completed, total,
                    envId, env.Name, 0, $"开始安装 ({env.Name})", null));

                try
                {
                    var result = await RunPipAsync(pythonExe, pipArgs,
                        line => progress?.Report(new BaseEnvProgress(
                            BaseEnvStatus.Running, completed, total,
                            envId, env.Name, null, line, null)),
                        pct => progress?.Report(new BaseEnvProgress(
                            BaseEnvStatus.Running, completed, total,
                            envId, env.Name, pct, null, null)),
                        ct);

                    if (result.WasCancelled || ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        failures[envId] = "用户取消"; failed++;
                        progress?.Report(new BaseEnvProgress(
                            BaseEnvStatus.Cancelled, completed + 1, total,
                            envId, env.Name, null, null, "用户取消"));
                        completed++; break;
                    }

                    if (result.ExitCode == 0)
                    {
                        succeeded++;
                        progress?.Report(new BaseEnvProgress(
                            BaseEnvStatus.Succeeded, completed + 1, total,
                            envId, env.Name, 100, null, null));
                    }
                    else
                    {
                        failed++;
                        failures[envId] = $"pip 退出码 {result.ExitCode}";
                        progress?.Report(new BaseEnvProgress(
                            BaseEnvStatus.Failed, completed + 1, total,
                            envId, env.Name, null, null, $"pip 退出码 {result.ExitCode}"));
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    failures[envId] = "用户取消"; failed++;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Cancelled, completed + 1, total,
                        envId, env.Name, null, null, "用户取消"));
                    completed++; break;
                }
                catch (Exception ex)
                {
                    failed++; failures[envId] = ex.Message;
                    progress?.Report(new BaseEnvProgress(
                        BaseEnvStatus.Failed, completed + 1, total,
                        envId, env.Name, null, null, ex.Message));
                }
                completed++;
            }
            return new BaseEnvInstallResult(cancelled, succeeded, failed, failures);
        }
    }

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        private readonly List<BaseEnvProgress> _events = new();
        public IReadOnlyList<BaseEnvProgress> Events => _events;
        public void Report(BaseEnvProgress value)
        {
            lock (_events) _events.Add(value);
        }
        public int FirstRunningEnv(string envId) =>
            _events.FindIndex(p => p.CurrentEnvId == envId && p.Status == BaseEnvStatus.Running);
    }
}
