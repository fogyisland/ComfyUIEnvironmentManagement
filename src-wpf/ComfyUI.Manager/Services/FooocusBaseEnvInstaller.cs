using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) Fooocus BED installer:镜像 lllyasviel/Fooocus
/// <c>launch.py</c> 默认 <c>TORCH_COMMAND</c>(<c>pip install torch==2.1.0
/// torchvision==0.16.0 --extra-index-url cu121</c>),让 Fooocus launcher 启动时
/// <c>is_installed("torch")</c> 直接返回 True 跳过重装。
///
/// **锁版本 2.1.0+cu121**(用户决策 2026-09-01):Fooocus 上游 LTS 模式不修
/// 破坏,pytorch_lightning 2.3.3 / torchsde 0.2.6 / gradio 3.41.2 都跟 torch 2.1
/// 钉死。装 2.4 / 2.5+ 大概率坏 launcher(per memory "Fooocus README Project
/// Status: Limited LTS with Bug Fixes Only")。跳过 BaseEnvProfilePickerDialog
/// 直接装,跟 Forge 镜像(<see cref="ForgeBaseEnvInstaller"/>)。
///
/// 触发入口:EnvironmentListViewModel.OpenBaseEnvProgressForSingleEnvAsync
/// 在 <c>env.TemplateKind == "Fooocus"</c> 时跳过 PickerDialog +
/// BaseEnvProgressDialog,直接 dispatch 到这里(inline panel 显示进度)。
///
/// 成功 marker:<see cref="MarkerFileName"/>。
/// </summary>
public class FooocusBaseEnvInstaller
{
    /// <summary>
    /// v1.0.0.x (2026-09-01): Fooocus 锁版 torch 配置常量。
    /// 镜像 <see cref="ForgeBaseEnvConstants"/> pattern(锁版本硬编码
    /// 在 source 而不是 settings,避免用户改坏 Fooocus launcher 期望)。
    /// </summary>
    public static class FooocusBaseEnvConstants
    {
        /// <summary>torch 主版本号,Fooocus 上游 launch.py 默认 TORCH_COMMAND 钉死。</summary>
        public const string TorchVersion = "2.1.0";
        public const string TorchVisionVersion = "0.16.0";

        /// <summary>CUDA wheel index URL —— download.pytorch.org/whl/cu121(Python 镜像不镜像 CUDA wheel)。</summary>
        public const string TorchIndexUrl = "https://download.pytorch.org/whl/cu121";

        /// <summary>BED 完成 marker 文件名,跟 .forge_base_env_installed 同 pattern。</summary>
        public const string MarkerFileName = ".fooocus_base_env_installed";
    }

    private readonly AppLogger? _logger;
    private readonly HttpProxyConfig? _proxy;

    public FooocusBaseEnvInstaller(AppLogger? logger = null, HttpProxyConfig? proxy = null)
    {
        _logger = logger;
        _proxy = proxy;
    }

    /// <summary>
    /// 检查 Fooocus BED 是否已完成(marker 文件存在)。
    /// 单一判定源:EnvironmentListViewModel ToggleBaseEnvCommand.CanExecute 也走这里。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, FooocusBaseEnvConstants.MarkerFileName));
    }

    /// <summary>
    /// 跑全套(只装 torch 2.1.0+cu121 + 写 marker)。失败 → 返回
    /// <see cref="FooocusBedInstallResult"/> 描述失败原因;不会回滚已成功的步骤
    /// (launch.py 启动时 idempotent 跳过,用户可重新点补跑)。
    /// </summary>
    public virtual async Task<FooocusBedInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        _logger?.Info("fooocus-bed",
            $"env='{env.Name}' kind='{env.TemplateKind}' 开始 Fooocus BED (1 步:torch=={FooocusBaseEnvConstants.TorchVersion}+cu121)");
        logProgress?.Report($"[fooocus-bed] env='{env.Name}' 开始 BED (torch=={FooocusBaseEnvConstants.TorchVersion}+cu121)");

        var pythonExe = ResolveVenvPython(env);

        // 0. pip install --upgrade pip wheel (ForgeBaseEnvInstaller 模式)
        var preArgs = new[] { "install", "--upgrade", "pip", "wheel" };
        logProgress?.Report("[fooocus-bed] $ pip install --upgrade pip wheel");
        var preResult = await RunPipAsync(pythonExe, preArgs,
            line => logProgress?.Report(line), ct);
        if (!IsPipOk(preResult))
            return FailFrom(preResult, "pip upgrade");

        // 1. torch==2.1.0 + torchvision==0.16.0
        // --extra-index-url 指向 download.pytorch.org/whl/cu121(国内 PyPI 镜像
        // 不镜像 download.pytorch.org,pip 解析 CUDA wheel 时需要原站 index)。
        var torchArgs = new[]
        {
            "install",
            $"torch=={FooocusBaseEnvConstants.TorchVersion}",
            $"torchvision=={FooocusBaseEnvConstants.TorchVisionVersion}",
            "--disable-pip-version-check",
            "--extra-index-url", FooocusBaseEnvConstants.TorchIndexUrl,
        };
        logProgress?.Report(
            $"[fooocus-bed] $ pip install torch=={FooocusBaseEnvConstants.TorchVersion} "
            + $"torchvision=={FooocusBaseEnvConstants.TorchVisionVersion} "
            + $"--extra-index-url {FooocusBaseEnvConstants.TorchIndexUrl}");
        var torchResult = await RunPipAsync(pythonExe, torchArgs,
            line => logProgress?.Report(line), ct);
        if (!IsPipOk(torchResult))
            return FailFrom(torchResult, "torch");

        // 全部成功 → 写 marker
        var markerPath = Path.Combine(env.RootPath, FooocusBaseEnvConstants.MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception ex)
        {
            _logger?.Warn("fooocus-bed",
                $"env='{env.Name}' marker 写失败(ex={ex.Message});下次装基础环境会被短路");
        }

        _logger?.Info("fooocus-bed",
            $"env='{env.Name}' BED 完成(torch=={FooocusBaseEnvConstants.TorchVersion}+cu121)");
        logProgress?.Report($"[fooocus-bed] ✓ 完成(torch=={FooocusBaseEnvConstants.TorchVersion}+cu121)");
        return new FooocusBedInstallResult(
            Success: true, Cancelled: false, Reason: null, InstalledCount: 0);
    }

    private static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
            return env.PythonExecutable;
        var defaultPath = Path.Combine(env.RootPath, "venv", "Scripts", "python.exe");
        if (!File.Exists(defaultPath))
            throw new InvalidOperationException(
                $"venv python 不存在:{defaultPath}(env-create 时应已装 venv,异常说明 venv 被破坏)");
        return defaultPath;
    }

    /// <summary>
    /// Run pip with stderr/stdout 实时通过 <paramref name="onLine"/> 报告,返
    /// <see cref="PipResult"/>。镜像 <see cref="ForgeBaseEnvInstaller.RunPipAsync"/>
    /// 实现(不抽基类 — 跟 Forge caller 各自独立,重复 ~80 行可接受)。
    /// 测试 seam:virtual 方法,子类可 override 拦截 pip 调用。
    /// </summary>
    protected virtual async Task<PipResult> RunPipAsync(
        string pythonExe,
        IReadOnlyList<string> pipArgs,
        Action<string> onLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        _proxy?.ApplyTo(psi);
        // v1.0.0.x (2026-09-01): PYTHONUTF8=1 — 见 PipProcessHelpers doc-comment。
        PipProcessHelpers.ApplyUtf8Mode(psi);
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        foreach (var a in pipArgs) psi.ArgumentList.Add(a);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动 pip 失败:{ex.Message}", ex);
        }
        if (process is null) throw new InvalidOperationException("Process.Start 返回 null");

        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine(line);
                }
            }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (ct.IsCancellationRequested) break;
                    onLine(line);
                }
            }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new PipResult(-1, WasCancelled: true);
        }

        await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

        return new PipResult(process.ExitCode, WasCancelled: false);
    }

    private FooocusBedInstallResult FailFrom(PipResult p, string stage)
    {
        if (p.WasCancelled)
        {
            return new FooocusBedInstallResult(
                Success: false, Cancelled: true, Reason: "用户取消", InstalledCount: 0);
        }
        var reason = $"pip {stage} 退出码 {p.ExitCode}";
        return new FooocusBedInstallResult(
            Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
    }

    private static bool IsPipOk(PipResult p) => p.ExitCode == 0 && !p.WasCancelled;
}

public record FooocusBedInstallResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int InstalledCount) : IBedInstallResult;
