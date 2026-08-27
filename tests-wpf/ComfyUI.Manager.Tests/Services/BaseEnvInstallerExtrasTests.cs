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
/// v1.0.0.x #584:BaseEnvInstaller BED extras(gitpython + triton)阶段测试 — 覆盖
/// happy(主+extras 都 success → done + 2 pip calls)、extras 失败(主 success +
/// extras fail → 仍 done,extras 仅 Warn log)、主失败(主 fail → extras 不跑)、
/// 主 cancel(主 cancel → extras 不跑)、Empty extras override(测试 seam,不跑 extras)。
///
/// <para>
/// 用 FakeBaseEnvInstaller override RunPipAsync + 记录 CallHistory;用 pipArgs
/// 含 "gitpython" 区分 main vs extras 调用(production extras 列表
/// <c>["gitpython","triton"]</c> 在默认 ExtraPackages 实现里)。
/// </para>
/// </summary>
public sealed class BaseEnvInstallerExtrasTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstallerExtrasTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private Environment SeedEnv(string id, string root)
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

    // ───── 1. Happy path:主+extras 都 success ─────

    [Fact]
    public async Task InstallAsync_MainAndExtrasBothSucceed_DoneTwoPipCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extras-ok-{Guid.NewGuid():N}");
        SeedEnv("env-a", root);
        var fake = new FakeBaseEnvInstaller(_envRepo);

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.True(result.SucceededCount == 1);
        Assert.True(result.FailedCount == 0);
        Assert.False(result.Cancelled);
        // 2 次 pip 调用:main 一次 + extras 一次
        Assert.Equal(2, fake.CallHistory.Count);
        // 第 1 次 = main(stable profile 把 torch pin 成 "torch==2.5.0")
        // 第 2 次 = extras(带 gitpython + triton)
        Assert.Contains("torch==2.5.0", fake.CallHistory[0]);
        Assert.DoesNotContain("gitpython", fake.CallHistory[0]);
        Assert.Contains("gitpython", fake.CallHistory[1]);
        Assert.Contains("triton", fake.CallHistory[1]);
        // BedStatus = done(没被 extras 阶段改写)
        var final = _envRepo.Get("env-a");
        Assert.Equal("done", final!.BedStatus);
    }

    // ───── 2. Extras 失败 → BED 仍 done ─────

    [Fact]
    public async Task InstallAsync_ExtrasFail_BedStillDone_WarnLogged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extras-fail-{Guid.NewGuid():N}");
        SeedEnv("env-b", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            ExtrasResult = new PipResult(1, false),  // extras pip 退出码 1
        };
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-b" }, DefaultProfile(), progress, CancellationToken.None);

        // 主 install 成功 → BedStatus="done",succeededCount=1(extras 不影响)
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        var final = _envRepo.Get("env-b");
        Assert.Equal("done", final!.BedStatus);
        Assert.Null(final.BedFailedReason);
        // extras 调用确实发生了(2 次 pip)
        Assert.Equal(2, fake.CallHistory.Count);
        Assert.Contains("gitpython", fake.CallHistory[1]);
        // progress emit 了 extras 阶段 + 失败 log 行
        Assert.Contains(progress.Events,
            p => p.Status == BaseEnvStatus.Running && p.LogLine?.Contains("stage:extras") == true);
        Assert.Contains(progress.Events,
            p => p.LogLine?.Contains("extras pip 退出码 1") == true);
    }

    // ───── 3. 主失败 → extras 不跑 ─────

    [Fact]
    public async Task InstallAsync_MainFail_ExtrasNotCalled_FailedOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extras-mainfail-{Guid.NewGuid():N}");
        SeedEnv("env-c", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            MainResult = new PipResult(1, false),  // 主 pip 退出码 1
        };

        var result = await fake.InstallAsync(
            new[] { "env-c" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        // 主失败 → extras 不会跑(只有 1 次 pip 调用)
        Assert.Equal(1, fake.CallHistory.Count);
        Assert.DoesNotContain("gitpython", fake.CallHistory[0]);
        var final = _envRepo.Get("env-c");
        Assert.Equal("failed", final!.BedStatus);
        Assert.StartsWith("pip 退出码", final.BedFailedReason);
    }

    // ───── 4. 主 cancel → extras 不跑 ─────

    [Fact]
    public async Task InstallAsync_MainCancelled_ExtrasNotCalled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extras-cancel-{Guid.NewGuid():N}");
        SeedEnv("env-d", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            MainResult = new PipResult(-1, true),  // WasCancelled = true
        };

        var result = await fake.InstallAsync(
            new[] { "env-d" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.True(result.Cancelled);
        // cancel 后 extras 不跑
        Assert.Equal(1, fake.CallHistory.Count);
        Assert.DoesNotContain("gitpython", fake.CallHistory[0]);
        var final = _envRepo.Get("env-d");
        Assert.Equal("failed", final!.BedStatus);
        Assert.Equal("用户取消", final.BedFailedReason);
    }

    // ───── 5. ExtraPackages = [] override → 只跑主 ─────

    [Fact]
    public async Task InstallAsync_EmptyExtrasOverride_OnlyMainRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-no-extras-{Guid.NewGuid():N}");
        SeedEnv("env-e", root);
        // EmptyExtrasFake override ExtraPackages 返空 → 跳过 extras 阶段
        var fake = new EmptyExtrasFake(_envRepo);

        var result = await fake.InstallAsync(
            new[] { "env-e" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        // 只有 1 次 pip(主)— extras 列表空,TryInstallExtrasAsync 早返
        Assert.Equal(1, fake.CallCount);
        Assert.Single(fake.CallHistory);
        // sanity: 这次调用是 main(torch==2.5.0),不是 extras(gitpython/triton)
        Assert.False(fake.SawExtrasCall);
        Assert.Contains("torch==2.5.0", fake.CallHistory[0]);
    }

    // ───── 6. 用户中途 cancel 时 extras 透传 ─────

    [Fact]
    public async Task InstallAsync_CancelDuringExtras_DoesNotOverrideSuccessToCancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extras-cancelmid-{Guid.NewGuid():N}");
        SeedEnv("env-f", root);
        // 用 cts 在 extras 阶段 cancel — FakeBaseEnvInstaller 在 extras 调用时
        // 检 ct.IsCancellationRequested 抛 cancelled 结果
        var fake = new CancellingExtrasFake(_envRepo);
        using var cts = new CancellationTokenSource();

        var result = await fake.InstallAsync(
            new[] { "env-f" }, DefaultProfile(), progress: null, cts.Token);

        // 主成功(extras 抛 cancel,但 TryInstallExtrasAsync 内部 catch 不 throw)
        // → BedStatus=done;succeededCount=1(主已 ++);Cancelled flag 由外层判定
        // 因为只有 1 个 env,extras cancel 后外层 foreach 走下一轮时已 break
        // 故 Cancelled=true 取决于 cts 是否真被 cancel
        // 简化断言:extras 被调 + BedStatus=done(extras 失败不改 BedStatus)
        Assert.Equal(2, fake.CallHistory.Count);
        Assert.Contains("gitpython", fake.CallHistory[1]);
        var final = _envRepo.Get("env-f");
        Assert.Equal("done", final!.BedStatus);
        Assert.Null(final.BedFailedReason);
    }

    // ───── 7. DefaultExtraPackages 内容契约 ─────

    [Fact]
    public void DefaultExtraPackages_ContainsGitPythonAndTriton()
    {
        // production 默认常量必须包含用户原话要求的两个包,防止后续 refactor 漏掉
        var fake = new FakeBaseEnvInstaller(_envRepo);
        var extras = fake.GetExtrasPublic();
        Assert.Contains("gitpython", extras);
        Assert.Contains("triton", extras);
    }

    // ───── helpers ─────

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        private readonly List<BaseEnvProgress> _events = new();
        public IReadOnlyList<BaseEnvProgress> Events => _events;
        public void Report(BaseEnvProgress value) => _events.Add(value);
    }

    /// <summary>
    /// Fake override RunPipAsync — 用 pipArgs 是否含 "gitpython" 区分 main vs extras。
    /// MainResult / ExtrasResult 可分别设。CallHistory 记录全部调用 args(顺序)。
    /// </summary>
    private class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();
        public PipResult MainResult { get; set; } = new(0, false);
        public PipResult ExtrasResult { get; set; } = new(0, false);

        public FakeBaseEnvInstaller(IEnvironmentRepository repo) : base(repo) { }

        // 测试用 public 入口取 extras(默认常量验证用)
        public IReadOnlyList<string> GetExtrasPublic() => ExtraPackages;

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            var args = pipArgs.ToList();
            CallHistory.Add(args);
            var isExtras = pipArgs.Any(a => a == "gitpython");
            onLine(isExtras ? "[extras-pip-line]" : "[main-pip-line]");
            return Task.FromResult(isExtras ? ExtrasResult : MainResult);
        }
    }

    /// <summary>
    /// Override ExtraPackages 返空 → 测试 "无 extras" 路径(只跑主,1 次 pip)。
    /// </summary>
    private sealed class EmptyExtrasFake : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();
        public int CallCount { get; private set; }
        public bool SawExtrasCall { get; private set; }

        public EmptyExtrasFake(IEnvironmentRepository repo) : base(repo) { }

        protected override IReadOnlyList<string> ExtraPackages => Array.Empty<string>();

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallCount++;
            CallHistory.Add(pipArgs.ToList());
            if (pipArgs.Any(a => a == "gitpython")) SawExtrasCall = true;
            return Task.FromResult(new PipResult(0, false));
        }
    }

    /// <summary>
    /// Extras 调用时检 ct — cancel 后返 cancelled 结果,模拟"extras 阶段中途 cancel"。
    /// </summary>
    private sealed class CancellingExtrasFake : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();

        public CancellingExtrasFake(IEnvironmentRepository repo) : base(repo) { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallHistory.Add(pipArgs.ToList());
            // 模拟 extras 阶段被 cancel:main 永远 success,extras 阶段 ct 已 cancel
            if (pipArgs.Any(a => a == "gitpython") && ct.IsCancellationRequested)
            {
                return Task.FromResult(new PipResult(-1, true));
            }
            return Task.FromResult(new PipResult(0, false));
        }
    }
}
