using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T24:测试 <see cref="FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync"/>。
/// 精确判定 4+4 = 8 个默认模型是否全部下载,merged 「下载默认模型」按钮的 disabled 依据。
///
/// 测试策略:
/// <list type="bullet">
///   <item>Step 1(4 T22 fixed file in/out/缺):env 目录可纯文件操作,测 4 个 in/out 组合</item>
///   <item>Step 1 全过 + Step 2 probe 真 spawn Python:测集成行为;CI 无 venv → probe 返 null → 按钮 enabled
///     (这是预期 fallback,我们只验证"Step 1 全过 ≠ 永远 true")</item>
///   <item>Null / empty root path 防御:防止 NPE</item>
/// </list>
///
/// **重要**:本测试**不**依赖真实 venv Python —— 因为 CI 环境没装 Fooocus env。
///   Step 1 早退(任意 T22 文件缺失)→ 直接返 false,不调 probe;这是核心可测路径。
///   Step 1 全过但 probe 失败 → 返 false(probe 在测试环境返 null);这是 fallback 行为。
/// </summary>
public class FooocusAllModelsCheckerTests : IDisposable
{
    private readonly string _envRoot;

    public FooocusAllModelsCheckerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"fooocus-all-checker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv()
    {
        return new Environment
        {
            Id = "fooocus-env",
            Name = "fooocus-env",
            RootPath = _envRoot,
            TemplateKind = "Fooocus",
        };
    }

    /// <summary>
    /// 创建全部 T22 4 文件 + 让 Step 1 通过(Step 2 probe 在 CI 没 venv 会失败,
    /// 这是 fallback 行为,我们测的是"probe 失败 → false")。
    /// </summary>
    private void CreateAllT22Files()
    {
        var vaeApproxDir = Path.Combine(_envRoot, "models", "vae_approx");
        Directory.CreateDirectory(vaeApproxDir);
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xlvaeapp.pth"), new byte[] { 0x01 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "vaeapp_sd15.pth"), new byte[] { 0x02 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xl-to-v1_interposer-v4.0.safetensors"), new byte[] { 0x03 });

        var expansionDir = Path.Combine(_envRoot, "models", "prompt_expansion", "fooocus_expansion");
        Directory.CreateDirectory(expansionDir);
        File.WriteAllBytes(Path.Combine(expansionDir, "pytorch_model.bin"), new byte[] { 0x04 });
    }

    // ----- Null / empty 防御 -----

    [Fact]
    public async Task Check_ReturnsFalse_WhenEnvNull()
    {
        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(null!);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_ReturnsFalse_WhenRootPathEmpty()
    {
        var env = new Environment { Name = "x", RootPath = "" };
        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_ReturnsFalse_WhenRootPathNull()
    {
        var env = new Environment { Name = "x", RootPath = null! };
        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    // ----- Step 1: T22 4 file 缺失场景(早退,不调 probe)-----

    [Fact]
    public async Task Check_ReturnsFalse_WhenAllFourT22FilesMissing()
    {
        // env 目录完全空 → Step 1 第一个文件 xlvaeapp.pth 不存在 → 早退 false
        var env = SeedEnv();
        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_ReturnsFalse_WhenOnlyXlvaeappMissing()
    {
        // 只缺第 1 个 → Step 1 早退 false
        var env = SeedEnv();
        var vaeApproxDir = Path.Combine(_envRoot, "models", "vae_approx");
        Directory.CreateDirectory(vaeApproxDir);
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "vaeapp_sd15.pth"), new byte[] { 0x02 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xl-to-v1_interposer-v4.0.safetensors"), new byte[] { 0x03 });

        var expansionDir = Path.Combine(_envRoot, "models", "prompt_expansion", "fooocus_expansion");
        Directory.CreateDirectory(expansionDir);
        File.WriteAllBytes(Path.Combine(expansionDir, "pytorch_model.bin"), new byte[] { 0x04 });

        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_ReturnsFalse_WhenInterposerMissing()
    {
        // 缺第 3 个(interposer) → 早退 false
        var env = SeedEnv();
        var vaeApproxDir = Path.Combine(_envRoot, "models", "vae_approx");
        Directory.CreateDirectory(vaeApproxDir);
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xlvaeapp.pth"), new byte[] { 0x01 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "vaeapp_sd15.pth"), new byte[] { 0x02 });

        var expansionDir = Path.Combine(_envRoot, "models", "prompt_expansion", "fooocus_expansion");
        Directory.CreateDirectory(expansionDir);
        File.WriteAllBytes(Path.Combine(expansionDir, "pytorch_model.bin"), new byte[] { 0x04 });

        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_ReturnsFalse_WhenPytorchModelBinMissing()
    {
        // 3 vae_approx 在,但缺 pytorch_model.bin(fooocus_expansion 4th file) → 早退 false
        var env = SeedEnv();
        var vaeApproxDir = Path.Combine(_envRoot, "models", "vae_approx");
        Directory.CreateDirectory(vaeApproxDir);
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xlvaeapp.pth"), new byte[] { 0x01 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "vaeapp_sd15.pth"), new byte[] { 0x02 });
        File.WriteAllBytes(Path.Combine(vaeApproxDir, "xl-to-v1_interposer-v4.0.safetensors"), new byte[] { 0x03 });

        // 注意:故意不创建 prompt_expansion 目录 + pytorch_model.bin

        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);
        Assert.False(result);
    }

    // ----- Step 1 全过 + Step 2 probe 失败 fallback (CI 没 venv) -----

    [Fact]
    public async Task Check_ReturnsFalse_WhenT22AllPresentButProbeFails()
    {
        // Step 1 全部 T22 文件在 → 走到 probe → CI 无 venv python → probe 返 null → 返 false
        // 这是 fallback 行为:用户能看到 progress log 写 "venv python 不存在",按钮保持 enabled
        var env = SeedEnv();
        CreateAllT22Files();

        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(env);

        // 不管 probe 返 null 还是抛异常,只要 Step 1 全过但 probe 失败 → 都应该返 false
        Assert.False(result);
    }

    [Fact]
    public async Task Check_AcceptsCancellationToken_DoesNotThrow()
    {
        // 验证 CancellationToken 入参不会引起异常
        // (Step 1 早退不消费 ct;Step 2 probe 内部会消费)
        var env = SeedEnv();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 空目录 → Step 1 早退 false,不调 probe,不消费 ct → 不会因 cancellation 抛
        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(
            env, logProgress: null, ct: cts.Token);
        Assert.False(result);
    }

    [Fact]
    public async Task Check_AcceptsLogProgress_DoesNotThrow()
    {
        // 验证 IProgress<string> 入参接收 log,但不抛(防御 null 入参)
        var env = SeedEnv();
        var progress = new Progress<string>(_ => { });

        var result = await FooocusDefaultModelsInstaller.CheckAllDefaultModelsDownloadedAsync(
            env, logProgress: progress, ct: default);
        Assert.False(result);
    }

    // ----- Lock 行为:防止 Step 1 全过却仍然 false 时的退化 -----

    [Fact]
    public void MarkerFileName_RemainsUnchanged_ForBackwardCompat()
    {
        // T22 + T23b 共用同一个 marker(.fooocus_default_models_installed)。
        // T24 CheckAllDefaultModelsDownloadedAsync 不依赖 marker(更精确的文件存在判定),
        // 但要确保 marker 常量没被改(否则旧 env 的 marker 失效)。
        Assert.Equal(".fooocus_default_models_installed",
            FooocusDefaultModelsConstants.MarkerFileName);
    }
}