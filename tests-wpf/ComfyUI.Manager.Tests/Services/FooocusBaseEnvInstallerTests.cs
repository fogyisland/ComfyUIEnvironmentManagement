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
/// v1.0.0.x (2026-09-01):Fooocus BED installer 测试。覆盖:
/// - step 1 torch==2.1.0 + torchvision==0.16.0 + cu121 index(用户决策锁版)
/// - step 0 pip upgrade 失败 / step 1 失败 → 不写 marker + Reason 含 "pip upgrade" / "torch"
/// - 全套成功 → 写 .fooocus_base_env_installed marker
/// - IsInstalled 静态判定 marker 文件存在
///
/// 不覆盖:真实 pip 调用(走 CapturingFooocusBaseEnvInstaller override RunPipAsync)。
/// 镜像 <see cref="ForgeBaseEnvInstallerTests"/> pattern(简化版 — Fooocus
/// 没有 pre-flight 步骤,只装 torch)。
/// </summary>
public class FooocusBaseEnvInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public FooocusBaseEnvInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"fooocusbed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string name = "fooocus")
    {
        var venvDir = Path.Combine(_envRoot, name, "venv", "Scripts");
        Directory.CreateDirectory(venvDir);
        File.WriteAllBytes(Path.Combine(venvDir, "python.exe"), new byte[] { 0x00 });
        var env = new Environment
        {
            Id = name,
            Name = name,
            RootPath = Path.Combine(_envRoot, name),
            PythonExecutable = Path.Combine(venvDir, "python.exe"),
            TemplateKind = "Fooocus",
        };
        Directory.CreateDirectory(env.RootPath);
        // requirements_versions.txt 留 Fooocus 真实场景用 — 测试不读它
        File.WriteAllLines(Path.Combine(env.RootPath, "requirements_versions.txt"),
            new[] { "torchsde==0.2.6", "pytorch_lightning==2.3.3" });
        return env;
    }

    /// <summary>
    /// 替身 BED:覆盖 RunPipAsync 拦截 pip 调用,记录 args。可设 FailPipStep
    /// 让任意 step 返 exit=1(模拟网络失败 / CUDA wheel 不可用等)。
    /// </summary>
    private class CapturingInstaller : FooocusBaseEnvInstaller
    {
        public List<string> LastPipArgs { get; } = new();
        public int PipCallCount { get; private set; }
        public bool FailPipStep { get; set; }
        public string? PipFailureStage { get; private set; }

        public CapturingInstaller() : base() { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            PipCallCount++;
            LastPipArgs.Clear();
            foreach (var a in pipArgs) LastPipArgs.Add(a);
            // 阶段识别:第 1 次 pip call = pip upgrade,第 2 次 = torch
            if (FailPipStep)
            {
                PipFailureStage = pipArgs.Contains("torch==2.1.0") ? "torch" : "pip upgrade";
                return Task.FromResult(new PipResult(1, WasCancelled: false));
            }
            return Task.FromResult(new PipResult(0, WasCancelled: false));
        }
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenMarkerMissing()
    {
        var env = SeedEnv();
        Assert.False(FooocusBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsTrue_WhenMarkerExists()
    {
        var env = SeedEnv();
        File.WriteAllText(
            Path.Combine(env.RootPath, FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName),
            "2026-09-01T00:00:00Z");
        Assert.True(FooocusBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenRootPathEmpty()
    {
        var env = new Environment { Name = "x", RootPath = "" };
        Assert.False(FooocusBaseEnvInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenEnvNull()
    {
        Assert.False(FooocusBaseEnvInstaller.IsInstalled(null!));
    }

    [Fact]
    public void FooocusBaseEnvConstants_TorchVersion_IsLockedTo210()
    {
        // 用户决策 2026-09-01:锁 torch 2.1.0 + cu121(Fooocus 上游 launcher 默认)。
        // 任何后续"现代化"PR 改这些常量都会触发 test fail → 提醒 review
        // Fooocus LTS 兼容性(pytorch_lightning 2.3.3 / torchsde 0.2.6 / gradio 3.41.2)
        Assert.Equal("2.1.0", FooocusBaseEnvInstaller.FooocusBaseEnvConstants.TorchVersion);
        Assert.Equal("0.16.0", FooocusBaseEnvInstaller.FooocusBaseEnvConstants.TorchVisionVersion);
        Assert.Equal("https://download.pytorch.org/whl/cu121",
            FooocusBaseEnvInstaller.FooocusBaseEnvConstants.TorchIndexUrl);
        Assert.Equal(".fooocus_base_env_installed",
            FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName);
    }

    [Fact]
    public async Task InstallAsync_RunsTwoPipCalls_UpgradeThenTorch()
    {
        // Fooocus BED 镜像 Forge 模式:先 pip upgrade 再装 torch
        // (避免 torch 装到一半碰到老 pip wheel 解析 bug)
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        Assert.Equal(2, installer.PipCallCount);
    }

    [Fact]
    public async Task InstallAsync_TorchStep_LocksTo210WithCu121Index()
    {
        // 关键锁版验证:torch==2.1.0 + torchvision==0.16.0 + --extra-index-url cu121
        // 完全匹配 Fooocus launch.py 默认 TORCH_COMMAND
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        // 第 2 次 pip call 是 torch
        Assert.Contains("install", installer.LastPipArgs);
        Assert.Contains("torch==2.1.0", installer.LastPipArgs);
        Assert.Contains("torchvision==0.16.0", installer.LastPipArgs);
        Assert.Contains("--extra-index-url", installer.LastPipArgs);
        Assert.Contains("https://download.pytorch.org/whl/cu121", installer.LastPipArgs);
    }

    [Fact]
    public async Task InstallAsync_AllStepsSucceed_WritesMarker()
    {
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"BED fail: {result.Reason}");
        Assert.True(File.Exists(
            Path.Combine(env.RootPath,
                FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName)),
            "marker 文件未写入");
    }

    [Fact]
    public async Task InstallAsync_PipUpgradeFails_DoesNotWriteMarker_ReasonMentionsPipUpgrade()
    {
        // step 0 (pip upgrade) 失败 → 不继续 torch step + 不写 marker
        var env = SeedEnv();
        var installer = new CapturingInstaller { FailPipStep = true };

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.True(result.Cancelled == false);
        Assert.Equal("pip upgrade", installer.PipFailureStage);
        Assert.Contains("pip upgrade", result.Reason ?? "");
        Assert.False(File.Exists(
            Path.Combine(env.RootPath,
                FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName)),
            "失败路径不应写 marker");
    }

    [Fact]
    public async Task InstallAsync_TorchStepFails_DoesNotWriteMarker_ReasonMentionsTorch()
    {
        // 第 2 次 pip (torch) 失败 — 第 1 次 pip upgrade 已成功
        // (用更细粒度的 fake,只让第 2 次失败)
        var env = SeedEnv();
        var installer = new Step2FailInstaller();

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.Contains("torch", result.Reason ?? "");
        Assert.False(File.Exists(
            Path.Combine(env.RootPath,
                FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName)));
    }

    /// <summary>
    /// 第 1 次 pip (upgrade) 成功,第 2 次 (torch) 失败。
    /// </summary>
    private class Step2FailInstaller : FooocusBaseEnvInstaller
    {
        private int _callCount;

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            _callCount++;
            // 第 2 次调用 = torch step
            return Task.FromResult(_callCount == 2
                ? new PipResult(1, WasCancelled: false)
                : new PipResult(0, WasCancelled: false));
        }
    }
}
