using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x:Forge BED installer 测试。覆盖:
/// - step 0 torch==2.4.0 + torchvision==0.19.0 + torchaudio==2.4.0 + cu121 index
///   (用户 2026-08-29 明确锁定 — Forge 默认 torch==2.3.1 不够,SDXL 等新优化要 2.4+)
/// - step 0 失败 → 不写 marker + Reason 含 "torch"
/// - 复用 <see cref="ForgePreFlightInstaller"/> 跑 1-5 失败 → 不写 marker + 透传 pre-flight reason
/// - 全套成功 → 写 .forge_base_env_installed marker
/// - IsInstalled 静态判定 marker 文件存在
///
/// 不覆盖:真实 pip / git 调用(走 CapturingForgeBaseEnvInstaller override + CapturingPreFlight
/// 替身)。
/// </summary>
public class ForgeBaseEnvInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public ForgeBaseEnvInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"forgebed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string name = "forge")
    {
        var venvDir = Path.Combine(_envRoot, name, "venv", "Scripts");
        Directory.CreateDirectory(venvDir);
        // fake python.exe(空文件 — 测试不真调 pip,只验 args 序列)
        File.WriteAllBytes(Path.Combine(venvDir, "python.exe"), new byte[] { 0x00 });
        var env = new Environment
        {
            Id = name,
            Name = name,
            RootPath = Path.Combine(_envRoot, name),
            PythonExecutable = Path.Combine(venvDir, "python.exe"),
            TemplateKind = "Forge",
        };
        Directory.CreateDirectory(env.RootPath);
        // requirements_versions.txt 留 forge 真实场景用 — 测试 mock pre-flight 不读它
        File.WriteAllLines(Path.Combine(env.RootPath, "requirements_versions.txt"),
            new[] { "torch==2.1.2", "gradio==3.41.2" });
        // pre-create repositories/<repoName>/.git/ 让 git clone 步骤 skip(就算真跑也不 clone)
        var reposDir = Path.Combine(env.RootPath, "repositories");
        foreach (var spec in ForgePreFlightConstants.Repos)
            Directory.CreateDirectory(Path.Combine(reposDir, spec.DirName, ".git"));
        return env;
    }

    /// <summary>
    /// 替身 pre-flight:不调 pip / git,直接返回 Success。每次调用记录 invocations,
    /// 供 step-1..5 是否真的被 invoke 验证用。
    /// </summary>
    private class CapturingPreFlight : ForgePreFlightInstaller
    {
        public int CallCount { get; private set; }
        public bool FailNextCall { get; set; }

        public CapturingPreFlight() : base() { }

        public override Task<RequirementsInstallResult> InstallAsync(
            Environment env,
            IProgress<string>? logProgress,
            CancellationToken ct)
        {
            CallCount++;
            if (FailNextCall)
            {
                return Task.FromResult(new RequirementsInstallResult(
                    Success: false, Cancelled: false,
                    Reason: "pre-flight forced fail", InstalledCount: 0));
            }
            // 模拟成功 — 直接写 marker(pre-flight 真实成功路径会写,我们替身也写,
            // 让 ForgeBaseEnvInstaller 末尾 marker 写入对得上)
            var markerPath = Path.Combine(env.RootPath, ForgePreFlightConstants.MarkerFileName);
            try { File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")); }
            catch { }
            return Task.FromResult(new RequirementsInstallResult(
                Success: true, Cancelled: false, Reason: null, InstalledCount: 0));
        }
    }

    /// <summary>
    /// 替身 BED:覆盖 RunPipAsync 拦截 step 0 的 pip 调用,记录 args。可设
    /// FailTorchStep=true 让 step 0 返 exit=1。
    /// </summary>
    private class CapturingInstaller : ForgeBaseEnvInstaller
    {
        public List<string> TorchPipArgs { get; } = new();
        public bool FailTorchStep { get; set; }

        public CapturingInstaller(ForgePreFlightInstaller preFlight) : base(preFlightInstaller: preFlight) { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            // step 0 = 第一个 pip 调用(每次 InstallAsync 只跑一次 pip step 0,pre-flight
            // 内部跑 3 次 pip 但被 CapturingPreFlight 替身绕过)。简单判别:第一个
            // argument 是 "torch==2.4.0" 就是 step 0。
            if (pipArgs.Count > 0 && pipArgs[0] == "install"
                && pipArgs.Any(a => a.StartsWith("torch==2.4.0")))
            {
                TorchPipArgs.Clear();
                foreach (var a in pipArgs) TorchPipArgs.Add(a);
                return Task.FromResult(FailTorchStep
                    ? new PipResult(1, WasCancelled: false)
                    : new PipResult(0, WasCancelled: false));
            }
            // 其它调用(理论上不会到这里 — pre-flight 已被替身绕过)
            return Task.FromResult(new PipResult(0, WasCancelled: false));
        }
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenMarkerMissing()
    {
        var env = SeedEnv();
        Assert.False(ForgeBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsTrue_WhenMarkerExists()
    {
        var env = SeedEnv();
        File.WriteAllText(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName),
            "2026-08-29T00:00:00Z");
        Assert.True(ForgeBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenRootPathEmpty()
    {
        var env = new Environment { Name = "x", RootPath = "" };
        Assert.False(ForgeBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenEnvNull()
    {
        Assert.False(ForgeBaseEnvInstaller.IsInstalled(null!));
    }

    [Fact]
    public async Task InstallAsync_RunsTorchPip_WithLockedVersionsAndCu121Index()
    {
        // 用户 2026-08-29 明确:"pip install torch==2.4.0 torchvision==0.19.0
        // torchaudio==2.4.0 forge 在安装基础环境 记得是这个版本的torch"
        // → 必须锁这三个版本 + 走 cu121 CUDA wheel index(国内 PyPI 镜像不镜像
        // download.pytorch.org/whl/,pip 解析 CUDA wheel 时需要原站)。
        var env = SeedEnv();
        var preFlight = new CapturingPreFlight();
        var installer = new CapturingInstaller(preFlight);

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        Assert.Contains("torch==2.4.0", installer.TorchPipArgs);
        Assert.Contains("torchvision==0.19.0", installer.TorchPipArgs);
        Assert.Contains("torchaudio==2.4.0", installer.TorchPipArgs);
        Assert.Contains("--extra-index-url", installer.TorchPipArgs);
        Assert.Contains("https://download.pytorch.org/whl/cu121", installer.TorchPipArgs);
        // pre-flight 替身被调 1 次(clip + open_clip + requirements + 3 repos 都被 mock 跳过)
        Assert.Equal(1, preFlight.CallCount);
    }

    [Fact]
    public async Task InstallAsync_AllStepsSucceed_WritesMarker()
    {
        var env = SeedEnv();
        var installer = new CapturingInstaller(new CapturingPreFlight());

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        Assert.True(File.Exists(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName)),
            "marker 文件未写入");
    }

    [Fact]
    public async Task InstallAsync_TorchStepFails_DoesNotInvokePreFlight_DoesNotWriteMarker()
    {
        var env = SeedEnv();
        var preFlight = new CapturingPreFlight();
        var installer = new CapturingInstaller(preFlight) { FailTorchStep = true };

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("torch", result.Reason ?? "");
        // torch 失败 → 直接返,不调 pre-flight(避免重复跑 pip 浪费时间)
        Assert.Equal(0, preFlight.CallCount);
        // marker 不应写
        Assert.False(File.Exists(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName)));
    }

    [Fact]
    public async Task InstallAsync_PreFlightFails_PropagatesReason_DoesNotWriteMarker()
    {
        var env = SeedEnv();
        var preFlight = new CapturingPreFlight { FailNextCall = true };
        var installer = new CapturingInstaller(preFlight);

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        // pre-flight 失败 reason 应透传(用户能看出是哪一步挂的)
        Assert.Contains("pre-flight forced fail", result.Reason ?? "");
        // BED marker 不应写(只跑完 0-5 全部才写)
        Assert.False(File.Exists(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName)));
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_NotAutoCalled_FromThisLayer()
    {
        // IsInstalled 是判定源 — 但 InstallAsync 本身不短路(由 caller
        // EnvironmentListViewModel.ToggleBaseEnvCommand.CanExecute 短路)。
        // 这里只验:已装 marker 存在时,InstallAsync 仍跑全部步骤(模拟用户点
        // 「重新安装基础环境」的语义 — marker 是 advisory,不是 strict assert)。
        var env = SeedEnv();
        File.WriteAllText(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName),
            "old-timestamp");
        var preFlight = new CapturingPreFlight();
        var installer = new CapturingInstaller(preFlight);

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        // marker 被重写为新时间戳
        var newContent = File.ReadAllText(
            Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName));
        Assert.NotEqual("old-timestamp", newContent);
        Assert.Equal(1, preFlight.CallCount);
    }

    [Fact]
    public void ForgeBaseEnvConstants_TorchVersion_IsLockedTo240()
    {
        // 用户原话:"forge 安装的torch 版本只能是2.4 不能高于这个版本"
        // → 常量锁住 2.4.0 系列,任何修改都得改 Constants + 重新审 review。
        Assert.Equal("2.4.0", ForgeBaseEnvConstants.TorchVersion);
        Assert.Equal("0.19.0", ForgeBaseEnvConstants.TorchVisionVersion);
        Assert.Equal("2.4.0", ForgeBaseEnvConstants.TorchAudioVersion);
    }

    [Fact]
    public void ForgeBaseEnvConstants_TorchIndexUrl_PointsToCu121PyTorchOrg()
    {
        // 国内 PyPI 镜像不镜像 download.pytorch.org/whl/,必须走原站解析 CUDA wheel。
        Assert.Equal("https://download.pytorch.org/whl/cu121", ForgeBaseEnvConstants.TorchIndexUrl);
    }

    [Fact]
    public void ForgeBaseEnvConstants_MarkerFileName_DistinctFromPreFlight()
    {
        // BED marker (.forge_base_env_installed) 跟 pre-flight marker
        // (.forge_preflight_installed) 必须分开,各自阶段独立判定。
        Assert.NotEqual(ForgeBaseEnvConstants.MarkerFileName,
            ForgePreFlightConstants.MarkerFileName);
    }
}