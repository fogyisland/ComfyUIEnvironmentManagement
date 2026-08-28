using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x Forge BED installer:镜像 lllyasviel/stable-diffusion-webui-forge
/// <c>modules/launch_utils.py:prepare_environment()</c> 在「安装基础环境」阶段
/// 提前跑全套(0-5),让 <c>launch.py</c> 启动时 step 全部 idempotent 跳过。
///
/// 用户原话 2026-08-29:
/// "forge 不会弹框 直接点击按照上面的方式来进行安装,以log方式显示进度"
/// + "pip install torch==2.4.0 torchvision==0.19.0 torchaudio==2.4.0 forge 在安装基础环境
/// 记得是这个版本的torch"
///
/// 6 件事执行顺序(跟 launch_utils.py:prepare_environment() 对齐):
///   0. <c>pip install torch==2.4.0 torchvision==0.19.0 torchaudio==2.4.0
///        --extra-index-url {TORCH_INDEX_URL}</c>
///   1. <c>pip install openai/CLIP/archive/{hash}.zip --no-build-isolation</c>
///   2. <c>pip install mlfoundations/open_clip/archive/{hash}.zip --no-build-isolation</c>
///   3. <c>pip install xformers==0.0.27 --no-deps</c>(Forge fork xformers=True 默认值)
///   4. <c>pip install -r &lt;envRoot&gt;/requirements_versions.txt --no-deps</c>(过滤裸 torch 行)
///   5. <c>git clone</c> 3 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>(已存在 skip)
///
/// 触发入口:EnvironmentListViewModel.OpenBaseEnvProgressForSingleEnvAsync 在
/// <c>env.TemplateKind is "Forge"</c> 时跳过 PickerDialog + BaseEnvProgressDialog,
/// 直接 dispatch 到这里(inline panel 显示进度,镜像 RequirementsStatusViewModel 模式)。
///
/// 成功 marker:<see cref="ForgeBaseEnvConstants.MarkerFileName"/>。
/// </summary>
public class ForgeBaseEnvInstaller
{
    private readonly AppLogger? _logger;
    private readonly HttpProxyConfig? _proxy;
    private readonly string _gitExe;
    private readonly ForgePreFlightInstaller _preFlightInstaller;

    public ForgeBaseEnvInstaller(
        AppLogger? logger = null,
        HttpProxyConfig? proxy = null,
        string? gitExe = null,
        ForgePreFlightInstaller? preFlightInstaller = null)
    {
        _logger = logger;
        _proxy = proxy;
        // null/空 fallback 到 "git"(PATH 查找 — App.xaml.cs:GitExe 在 settings 注入,
        // 测试 ctor 不传也走得通)。
        _gitExe = string.IsNullOrWhiteSpace(gitExe) ? "git" : gitExe;
        _preFlightInstaller = preFlightInstaller ?? new ForgePreFlightInstaller(logger, proxy, gitExe);
    }

    /// <summary>
    /// 检查 Forge BED 是否已完成(marker 文件存在)。
    /// 单一判定源:EnvironmentListViewModel ToggleBaseEnvCommand.CanExecute 也走这里。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName));
    }

    /// <summary>
    /// 跑 0-5 全套。任何一步失败 → 返回 ForgeBedInstallResult 描述失败原因;
    /// 已成功的步骤不会回滚(launch.py 启动时 idempotent 跳过,用户可重新点补跑)。
    ///
    /// 跟 <see cref="ForgePreFlightInstaller.InstallAsync"/> 的差别:
    /// - pre-flight 跑 1-5(假设 BED 已装过 torch + venv);
    /// - BED 跑 0-5 全套(env-create 时只装 venv + pip upgrade,没 torch)。
    /// </summary>
    public virtual async Task<ForgeBedInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        _logger?.Info("forge-bed",
            $"env='{env.Name}' kind='{env.TemplateKind}' 开始 Forge BED (6 步:torch + clip + open_clip + xformers + requirements + 3 repos)");
        logProgress?.Report($"[forge-bed] env='{env.Name}' 开始 BED (torch+clip+open_clip+xformers+requirements+repos)");

        var pythonExe = ResolveVenvPython(env);

        // 0. torch==2.4.0 + torchvision==0.19.0 + torchaudio==2.4.0
        // --extra-index-url 指向 download.pytorch.org/whl/cu121(国内 PyPI 镜像不镜像
        // download.pytorch.org,pip 解析 CUDA wheel 时需要原站 index)。
        var torchArgs = new[]
        {
            "install",
            $"torch=={ForgeBaseEnvConstants.TorchVersion}",
            $"torchvision=={ForgeBaseEnvConstants.TorchVisionVersion}",
            $"torchaudio=={ForgeBaseEnvConstants.TorchAudioVersion}",
            "--disable-pip-version-check",
            "--extra-index-url", ForgeBaseEnvConstants.TorchIndexUrl,
        };
        logProgress?.Report(
            $"[forge-bed] $ pip install torch=={ForgeBaseEnvConstants.TorchVersion} "
            + $"torchvision=={ForgeBaseEnvConstants.TorchVisionVersion} "
            + $"torchaudio=={ForgeBaseEnvConstants.TorchAudioVersion} "
            + $"--extra-index-url {ForgeBaseEnvConstants.TorchIndexUrl}");
        var torchResult = await RunPipAsync(pythonExe, torchArgs,
            line => logProgress?.Report(line), ct);
        if (!IsPipOk(torchResult))
            return FailFrom(torchResult, "torch");

        // 1-5 复用 ForgePreFlightInstaller(clip + open_clip + requirements + 3 repos)
        var preFlightResult = await _preFlightInstaller.InstallAsync(env, logProgress, ct);
        if (!preFlightResult.Success)
        {
            // pre-flight 内部已写 marker 失败的 path;透传其 Reason
            return new ForgeBedInstallResult(
                Success: false,
                Cancelled: preFlightResult.Cancelled,
                Reason: preFlightResult.Reason ?? "pre-flight 失败",
                InstalledCount: 0);
        }

        // 全部成功 → 写 marker
        var markerPath = Path.Combine(env.RootPath, ForgeBaseEnvConstants.MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception ex)
        {
            _logger?.Warn("forge-bed",
                $"env='{env.Name}' marker 写失败(ex={ex.Message});下次装基础环境会被短路");
        }

        _logger?.Info("forge-bed", $"env='{env.Name}' BED 完成(1 torch + 2 zip + 1 xformers + 1 requirements + 3 repos)");
        logProgress?.Report("[forge-bed] ✓ 完成(1 torch + 2 zip + 1 xformers + 1 requirements + 3 repos)");
        return new ForgeBedInstallResult(
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
    /// <see cref="PipResult"/>。镜像 ForgePreFlightInstaller 的 RunPipAsync 内部实现;
    /// 不抽基类(避免过早抽象 — 两个 caller 各自独立,只有 RunPipAsync 重复 ~80 行)。
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

    private ForgeBedInstallResult FailFrom(PipResult p, string stage)
    {
        if (p.WasCancelled)
        {
            return new ForgeBedInstallResult(
                Success: false, Cancelled: true, Reason: "用户取消", InstalledCount: 0);
        }
        var reason = $"pip {stage} 退出码 {p.ExitCode}";
        return new ForgeBedInstallResult(
            Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
    }

    private static bool IsPipOk(PipResult p) => p.ExitCode == 0 && !p.WasCancelled;
}

public record ForgeBedInstallResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int InstalledCount);