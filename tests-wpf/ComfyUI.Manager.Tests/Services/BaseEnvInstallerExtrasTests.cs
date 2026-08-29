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
/// v1.0.0.x #584 + #584.b:BED pre-install + extras 阶段测试 — 主 pip install 之前
/// 跑 <c>pip install --upgrade pip wheel</c>(pre-install:升级 pip + seed wheel),
/// 成功之后顺手装 gitpython (extras)。两个阶段都「失败只 Warn 不阻塞 BED done」,
/// 分别由 <see cref="BaseEnvInstaller.PreInstallPipArgs"/> 和 <see cref="BaseEnvInstaller.ExtraPackages"/>
/// 控制(测试可 override 返空跳过)。
///
/// <para>
/// 用 FakeBaseEnvInstaller override RunPipAsync + 记录 CallHistory;
/// 用 pipArgs 模式区分 3 个阶段:
/// <list type="bullet">
///   <item>pre-install: args 含 "--upgrade"(默认 DefaultPreInstallPipArgs)</item>
///   <item>extras: args 含 "gitpython"(默认 DefaultExtraPackages)</item>
///   <item>main: 其余(由 profile.BuildPipArgs() 拼出)</item>
/// </list>
/// 3 阶段独立返回 PreInstallResult / MainResult / ExtrasResult。
/// </para>
///
/// <para>
/// v1.0.0.x (2026-08-29) #754:pre-install 现在含 <c>wheel</c>(defense-in-depth)。
/// env-create step 6.6 已经给新建 env seed wheel;老 env(2026-08-29 前创建的)
/// 缺 wheel,Forge pre-flight 装 CLIP/open_clip 会 fail `bdist_wheel`。pre-install
/// 阶段重复 seed wheel,覆盖老 env。`--upgrade wheel` 对已装 venv no-op,新 venv
/// 装上 — 两种 OK。
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

    // ───── 1. Happy path:pre + main + extras 都 success ─────

    [Fact]
    public async Task InstallAsync_AllThreePhasesSucceed_DoneThreePipCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-3phase-ok-{Guid.NewGuid():N}");
        SeedEnv("env-a", root);
        var fake = new FakeBaseEnvInstaller(_envRepo);

        var result = await fake.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        // 3 次 pip 调用:pre-install(upgrade pip)+ main(torch+CUDA)+ extras(gitpython only)
        // v1.0.0.x:triton 已从 DefaultExtraPackages 移除(Windows CUDA wheel 不在 PyPI 上,
        // 装 PyPI mirror 找不到);extras 现只装 gitpython。详见 BaseEnvInstaller.cs。
        Assert.Equal(3, fake.CallHistory.Count);
        // 顺序固定
        Assert.Equal("pre-install", fake.PhaseAt(0));
        Assert.Equal("main", fake.PhaseAt(1));
        Assert.Equal("extras", fake.PhaseAt(2));
        // args 内容断言
        Assert.Contains("--upgrade", fake.CallHistory[0]);
        Assert.Contains("torch==2.5.0", fake.CallHistory[1]);
        Assert.Contains("gitpython", fake.CallHistory[2]);
        Assert.DoesNotContain("triton", fake.CallHistory[2]);
        var final = _envRepo.Get("env-a");
        Assert.Equal("done", final!.BedStatus);
    }

    // ───── 2. Pre-install 失败 → 主 install 仍跑 → BedStatus=done ─────

    [Fact]
    public async Task InstallAsync_PreInstallFails_StillRunsMainAndExtras_BedDone()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-prefail-{Guid.NewGuid():N}");
        SeedEnv("env-b", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            PreInstallResult = new PipResult(1, false),  // pip upgrade 失败
        };
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-b" }, DefaultProfile(), progress, CancellationToken.None);

        // pre 失败不阻塞 → 主 + extras 都跑 → BedStatus=done
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, fake.CallHistory.Count);  // pre + main + extras 都跑了
        var final = _envRepo.Get("env-b");
        Assert.Equal("done", final!.BedStatus);
        Assert.Null(final.BedFailedReason);
        // progress emit 了 pre-install 阶段 + 失败 log
        Assert.Contains(progress.Events,
            p => p.LogLine?.Contains("stage:pre-install") == true);
        Assert.Contains(progress.Events,
            p => p.LogLine?.Contains("pre-install pip 退出码 1") == true);
    }

    // ───── 3. 主失败 → extras 不跑(但 pre 已跑)─────

    [Fact]
    public async Task InstallAsync_MainFail_PreInstalledExtrasNotCalled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-mainfail-{Guid.NewGuid():N}");
        SeedEnv("env-c", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            MainResult = new PipResult(1, false),  // 主 pip 退出码 1
        };

        var result = await fake.InstallAsync(
            new[] { "env-c" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        // pre + main 跑了(2 次),extras 不跑(主失败)
        Assert.Equal(2, fake.CallHistory.Count);
        Assert.Equal("pre-install", fake.PhaseAt(0));
        Assert.Equal("main", fake.PhaseAt(1));
        var final = _envRepo.Get("env-c");
        Assert.Equal("failed", final!.BedStatus);
        Assert.StartsWith("pip 退出码", final.BedFailedReason);
    }

    // ───── 4. 主 cancel → extras 不跑(但 pre 已跑)─────

    [Fact]
    public async Task InstallAsync_MainCancelled_PreInstalledExtrasNotCalled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-maincancel-{Guid.NewGuid():N}");
        SeedEnv("env-d", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            MainResult = new PipResult(-1, true),  // WasCancelled = true
        };

        var result = await fake.InstallAsync(
            new[] { "env-d" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.True(result.Cancelled);
        // pre + main 跑了,extras 不跑
        Assert.Equal(2, fake.CallHistory.Count);
        Assert.Equal("pre-install", fake.PhaseAt(0));
        Assert.Equal("main", fake.PhaseAt(1));
        var final = _envRepo.Get("env-d");
        Assert.Equal("failed", final!.BedStatus);
        Assert.Equal("用户取消", final.BedFailedReason);
    }

    // ───── 5. Extras 失败 → BED 仍 done(pre + main + extras 都跑了)─────

    [Fact]
    public async Task InstallAsync_ExtrasFail_BedStillDone_WarnLogged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-extrasfail-{Guid.NewGuid():N}");
        SeedEnv("env-e", root);
        var fake = new FakeBaseEnvInstaller(_envRepo)
        {
            ExtrasResult = new PipResult(1, false),
        };
        var progress = new RecordingProgress();

        var result = await fake.InstallAsync(
            new[] { "env-e" }, DefaultProfile(), progress, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(3, fake.CallHistory.Count);
        var final = _envRepo.Get("env-e");
        Assert.Equal("done", final!.BedStatus);
        Assert.Null(final.BedFailedReason);
        Assert.Contains(progress.Events,
            p => p.LogLine?.Contains("extras pip 退出码 1") == true);
    }

    // ───── 6. PreInstallPipArgs = [] override → 只跑 main + extras ─────

    [Fact]
    public async Task InstallAsync_EmptyPreInstallOverride_OnlyMainAndExtrasRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-nopre-{Guid.NewGuid():N}");
        SeedEnv("env-f", root);
        var fake = new EmptyPreInstallFake(_envRepo);

        var result = await fake.InstallAsync(
            new[] { "env-f" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        // 2 次 pip:main + extras(pre 列表空,RunOptionalStageAsync 早返)
        Assert.Equal(2, fake.CallHistory.Count);
        Assert.Equal("main", fake.PhaseAt(0));
        Assert.Equal("extras", fake.PhaseAt(1));
        Assert.False(fake.SawPreInstallCall);
    }

    // ───── 7. EmptyPreInstall + EmptyExtras → 只跑 main ─────

    [Fact]
    public async Task InstallAsync_EmptyPreInstallAndEmptyExtras_OnlyMainRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-onlymain-{Guid.NewGuid():N}");
        SeedEnv("env-g", root);
        var fake = new NoOptionalStagesFake(_envRepo);

        var result = await fake.InstallAsync(
            new[] { "env-g" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        // 只跑 main(1 次 pip)
        Assert.Equal(1, fake.CallHistory.Count);
        Assert.Equal("main", fake.PhaseAt(0));
    }

    // ───── 8. Pre-install 阶段中途 cancel → 主 install 不跑? ─────
    // 注:当前实现中,cancel 检测在 RunOptionalStageAsync 末尾(主 install 前还有进),
    // 进了主 install 后 ct.IsCancellationRequested 还会被外层 foreach 用。但
    // 一旦进了 pre-install 阶段,pre 阶段末尾会读到 ct.Cancelled → 早返,然后主 install
    // 仍会被调(外层 foreach 没检查 mid-pre cancel,只检查每 env 开始时)。这是已知行为,
    // 主 install 自身在 ct.IsCancellationRequested 时也会早返(它自己 catch OperationCanceledException)。
    // 本测试锁定当前行为:pre cancel → main 仍被调 → main 看到 ct cancelled → WasCancelled 返回
    // → BedStatus=failed/用户取消。

    [Fact]
    public async Task InstallAsync_CancelDuringPreInstall_MainStillCalled_ReturnsCancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-precancel-{Guid.NewGuid():N}");
        SeedEnv("env-h", root);
        // cts 在 pre-install 阶段 fake 内部触发 → pre 阶段 RunOptionalStageAsync 看到
        // ct.IsCancellationRequested=true 早返,外层 InstallAsync 主 install 仍被调,
        // 主 fake 看到 ct cancelled 返 WasCancelled=true → 外层 break → extras 不跑。
        var cts = new CancellationTokenSource();
        try
        {
            var fake = new CancellingPreInstallFake(_envRepo, cts);

            var result = await fake.InstallAsync(
                new[] { "env-h" }, DefaultProfile(), progress: null, cts.Token);

            // pre + main 都跑了(2 次),extras 不跑(主 cancel → break)
            Assert.Equal(2, fake.CallHistory.Count);
            Assert.Equal("pre-install", fake.PhaseAt(0));
            Assert.Equal("main", fake.PhaseAt(1));
            Assert.True(result.Cancelled);
            var final = _envRepo.Get("env-h");
            Assert.Equal("failed", final!.BedStatus);
            Assert.Equal("用户取消", final.BedFailedReason);
        }
        finally
        {
            cts.Dispose();
        }
    }

    // ───── 9. 契约:DefaultPreInstallPipArgs / DefaultExtraPackages 内容 ─────

    [Fact]
    public void DefaultPreInstallPipArgs_UpgradesPipAndSeedsWheel()
    {
        // v1.0.0.x (2026-08-29):pre-install 不仅升级 pip,还 seed wheel 包 ——
        // defense-in-depth,覆盖 env-create step 6.6 wheel seed 之前创建的老 env
        // (老 venv 缺 wheel,Forge pre-flight stage:clip 装 openai/CLIP 时报
        // `error: invalid command 'bdist_wheel'`,因为 setuptools 63.2.0 不带
        // bdist_wheel,需要 `wheel` 包补上)。新 venv(step 6.6 后)wheel 已装,
        // `--upgrade wheel` no-op;老 venv 补上 wheel — 两种都 OK。
        var fake = new FakeBaseEnvInstaller(_envRepo);
        var pre = fake.GetPreInstallPublic();
        Assert.Contains("--upgrade", pre);
        Assert.Contains("pip", pre);
        Assert.Contains("wheel", pre);
        // 不应该拼 mirror 或 --index-url(主 install / extras 才拼)
        Assert.DoesNotContain("--index-url", pre);
    }

    [Fact]
    public void DefaultExtraPackages_ContainsGitPythonOnly_NoTriton()
    {
        // v1.0.0.x:triton 从默认移除 — Windows CUDA wheels 只走
        // download.pytorch.org/whl/{cu},不在 PyPI mirror 范围。extras 走 PyPI
        // mirror 会报 "Could not find a version that satisfies the requirement
        // triton"(实测 fail)。ComfyUI 启动时 launcher 自己装 triton(走 pytorch 源)。
        var fake = new FakeBaseEnvInstaller(_envRepo);
        var extras = fake.GetExtrasPublic();
        Assert.Contains("gitpython", extras);
        Assert.DoesNotContain("triton", extras);
    }

    // ───── 10. v1.0.0.x:BED extras 拼接 Settings.PipMirror (清华/阿里/USTC) ─────
    // 用户加速:extras 是纯 PyPI 包(默认 gitpython + triton),清华/阿里镜像有效。
    // 主 install 走 profile 自带 --index-url download.pytorch.org/whl/{cu},
    // PyPI 镜像不镜像 download.pytorch.org,所以主 install *不* 拼 mirror。

    [Fact]
    public async Task InstallAsync_ExtrasWithTsinghuaMirror_IncludesIndexUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-mirror-tuna-{Guid.NewGuid():N}");
        SeedEnv("env-mt", root);
        var settings = new Settings { PipMirror = "tsinghua_tuna" };
        var fake = new FakeBaseEnvInstaller(_envRepo, settings: settings);

        await fake.InstallAsync(
            new[] { "env-mt" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(3, fake.CallHistory.Count);
        // pre-install 不拼 mirror(只 `install --upgrade pip wheel`)
        Assert.DoesNotContain("--index-url", fake.CallHistory[0]);
        // pre-install 现在 seed wheel(2026-08-29 #754 defense-in-depth)
        Assert.Contains("wheel", fake.CallHistory[0]);
        // main 不拼 tuna mirror(走 profile 自带 download.pytorch.org)
        Assert.DoesNotContain(fake.CallHistory[1], a => a.Contains("pypi.tuna"));
        Assert.Contains("https://download.pytorch.org/whl/cu121", fake.CallHistory[1]);
        // extras 拼 mirror --index-url + https://pypi.tuna.tsinghua.edu.cn/simple
        Assert.Contains("--index-url", fake.CallHistory[2]);
        Assert.Contains("https://pypi.tuna.tsinghua.edu.cn/simple", fake.CallHistory[2]);
        Assert.Contains("gitpython", fake.CallHistory[2]);
        Assert.DoesNotContain(fake.CallHistory[2], a => a == "triton");
    }

    [Fact]
    public async Task InstallAsync_ExtrasWithOfficialMirror_NoIndexUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-mirror-off-{Guid.NewGuid():N}");
        SeedEnv("env-mo", root);
        // PipMirror=official(默认)→ 不拼 --index-url
        var settings = new Settings { PipMirror = "official" };
        var fake = new FakeBaseEnvInstaller(_envRepo, settings: settings);

        await fake.InstallAsync(
            new[] { "env-mo" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(3, fake.CallHistory.Count);
        // extras 不拼 mirror(走官方 pypi.org)
        Assert.DoesNotContain("--index-url", fake.CallHistory[2]);
        Assert.Contains("gitpython", fake.CallHistory[2]);
    }

    [Fact]
    public async Task InstallAsync_ExtrasWithNullSettings_NoIndexUrl()
    {
        // 兼容性:settings=null 走老路径,不拼 mirror(也无 HTTP_PROXY 注入)
        var root = Path.Combine(Path.GetTempPath(), $"bed-mirror-null-{Guid.NewGuid():N}");
        SeedEnv("env-mn", root);
        var fake = new FakeBaseEnvInstaller(_envRepo, settings: null);

        await fake.InstallAsync(
            new[] { "env-mn" }, DefaultProfile(), progress: null, CancellationToken.None);

        Assert.Equal(3, fake.CallHistory.Count);
        Assert.DoesNotContain("--index-url", fake.CallHistory[2]);
    }

    [Fact]
    public async Task InstallAsync_MainArgsPreservePytorchIndexUrl_WhenMirrorConfigured()
    {
        // 关键不变量:即使配了 PyPI mirror,主 install 的 --index-url 必须保持
        // download.pytorch.org/whl/{cuda} —— 清华/USTC 不镜像 download.pytorch.org,
        // 否则 CUDA wheel 找不到。这条测试锁住"主 install 不被 mirror 污染"。
        var root = Path.Combine(Path.GetTempPath(), $"bed-mirror-main-{Guid.NewGuid():N}");
        SeedEnv("env-mm", root);
        var settings = new Settings { PipMirror = "aliyun" };
        var fake = new FakeBaseEnvInstaller(_envRepo, settings: settings);

        await fake.InstallAsync(
            new[] { "env-mm" }, DefaultProfile(), progress: null, CancellationToken.None);

        var mainArgs = fake.CallHistory[1];
        // 主 install 必含 pytorch CUDA 源(exact element)
        Assert.Contains("--index-url", mainArgs);
        Assert.Contains("https://download.pytorch.org/whl/cu121", mainArgs);
        // 主 install 必不含 aliyun mirror URL —— 避免 mirror 覆盖 pytorch CUDA 源
        Assert.DoesNotContain(mainArgs, a => a.Contains("aliyun"));
    }

    // ───── helpers ─────

    private enum PipPhase { None, PreInstall, Main, Extras }

    private static PipPhase ClassifyPhase(IReadOnlyList<string> pipArgs)
    {
        if (pipArgs.Any(a => a == "--upgrade")) return PipPhase.PreInstall;
        if (pipArgs.Any(a => a == "gitpython")) return PipPhase.Extras;
        return PipPhase.Main;
    }

    /// <summary>
    /// stage 阶段 label(匹配 BaseEnvInstaller.cs 里的 RunOptionalStageAsync
    /// 第一个参数 + log 行格式):pre-install / main / extras(主 install 没有 stage
    /// 前缀,phase label 用 "main")。
    /// </summary>
    private static string PhaseLabel(PipPhase phase) => phase switch
    {
        PipPhase.PreInstall => "pre-install",
        PipPhase.Extras => "extras",
        _ => "main",
    };

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        private readonly List<BaseEnvProgress> _events = new();
        public IReadOnlyList<BaseEnvProgress> Events => _events;
        public void Report(BaseEnvProgress value) => _events.Add(value);
    }

    /// <summary>
    /// 默认 3 阶段 fake — PreInstallResult / MainResult / ExtrasResult 分别设,
    /// CallHistory 记录全部 args 顺序,PhaseAt(i) 返阶段标签。
    /// v1.0.0.x:接受可选 <paramref name="settings"/> 透传给 base ctor,让测试
    /// 验证 PipMirror 拼接逻辑(只在 BaseEnvInstaller.TryInstallExtrasAsync 里拼)。
    /// </summary>
    private class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();
        public PipResult PreInstallResult { get; set; } = new(0, false);
        public PipResult MainResult { get; set; } = new(0, false);
        public PipResult ExtrasResult { get; set; } = new(0, false);

        public FakeBaseEnvInstaller(IEnvironmentRepository repo, Settings? settings = null)
            : base(repo, settings: settings) { }

        public string PhaseAt(int index) => PhaseLabel(ClassifyPhase(CallHistory[index]));
        public IReadOnlyList<string> GetPreInstallPublic() => PreInstallPipArgs;
        public IReadOnlyList<string> GetExtrasPublic() => ExtraPackages;

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallHistory.Add(pipArgs.ToList());
            onLine($"[fake-pip] {ClassifyPhase(pipArgs)}");
            return Task.FromResult(ClassifyPhase(pipArgs) switch
            {
                PipPhase.PreInstall => PreInstallResult,
                PipPhase.Extras => ExtrasResult,
                _ => MainResult,
            });
        }
    }

    /// <summary>
    /// PreInstallPipArgs 返空 — 只跑 main + extras(2 次 pip)。
    /// </summary>
    private sealed class EmptyPreInstallFake : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();
        public bool SawPreInstallCall { get; private set; }

        public EmptyPreInstallFake(IEnvironmentRepository repo) : base(repo) { }

        protected override IReadOnlyList<string> PreInstallPipArgs => Array.Empty<string>();

        public string PhaseAt(int index) => PhaseLabel(ClassifyPhase(CallHistory[index]));

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallHistory.Add(pipArgs.ToList());
            if (ClassifyPhase(pipArgs) == PipPhase.PreInstall) SawPreInstallCall = true;
            return Task.FromResult(new PipResult(0, false));
        }
    }

    /// <summary>
    /// 两个 optional 阶段都返空 — 只跑 main(1 次 pip)。
    /// </summary>
    private sealed class NoOptionalStagesFake : BaseEnvInstaller
    {
        public List<List<string>> CallHistory { get; } = new();
        public NoOptionalStagesFake(IEnvironmentRepository repo) : base(repo) { }

        protected override IReadOnlyList<string> PreInstallPipArgs => Array.Empty<string>();
        protected override IReadOnlyList<string> ExtraPackages => Array.Empty<string>();

        public string PhaseAt(int index) => PhaseLabel(ClassifyPhase(CallHistory[index]));

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallHistory.Add(pipArgs.ToList());
            return Task.FromResult(new PipResult(0, false));
        }
    }

    /// <summary>
    /// Pre-install 阶段立即 cancel — fake 在 RunPipAsync pre-install 阶段主动调
    /// cts.Cancel(),让外层 ct 真正 cancel。RunOptionalStageAsync 看到 ct cancelled
    /// 早返,外层 InstallAsync 主 install 仍被调,主 fake 看到 ct cancelled 返
    /// WasCancelled=true → 外层 break → extras 不跑。
    /// </summary>
    private sealed class CancellingPreInstallFake : BaseEnvInstaller
    {
        private readonly CancellationTokenSource _cts;
        public List<List<string>> CallHistory { get; } = new();

        public CancellingPreInstallFake(IEnvironmentRepository repo, CancellationTokenSource cts)
            : base(repo)
        {
            _cts = cts;
        }

        public string PhaseAt(int index) => PhaseLabel(ClassifyPhase(CallHistory[index]));

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            CallHistory.Add(pipArgs.ToList());
            // 模拟 pre-install 阶段检测到用户取消 → 主动 cancel ct
            if (ClassifyPhase(pipArgs) == PipPhase.PreInstall)
            {
                _cts.Cancel();
                return Task.FromResult(new PipResult(-1, true));
            }
            // main 看到 ct 已 cancelled 也返 cancelled
            if (ct.IsCancellationRequested)
            {
                return Task.FromResult(new PipResult(-1, true));
            }
            return Task.FromResult(new PipResult(0, false));
        }
    }
}