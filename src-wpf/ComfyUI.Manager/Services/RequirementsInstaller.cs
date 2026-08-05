using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// RequirementsInstaller:跑 `pip install -r &lt;env-root&gt;/requirements.txt`
/// 装 ComfyUI 的运行时依赖(SQLAlchemy / einops / transformers / ...)。
///
/// 跟 BED (BaseEnvInstaller) 的区别:
/// - BED 装 torch + cuda(profile 锁版本),由环境创建的 BED 入口触发;
/// - RequirementsInstaller 装 ComfyUI 自带 requirements.txt(过滤 torch* 行避免
///   覆盖 BED profile pin 的 torch 版本)。
///
/// 触发入口:env-list 操作列 6th 按钮"装依赖"(v0.6.5.12 SDD 新加)。
/// 成功 marker:&lt;env-root&gt;/.requirements_installed(空文件,只用于检测是否装过)。
/// </summary>
public class RequirementsInstaller
{
    public const string MarkerFileName = ".requirements_installed";
    public const string FilteredRequirementsFileName = ".requirements_filtered.txt";

    // 过滤:跳过 torch 系列包(由 BED profile 锁版本)
    // 匹配 # 开头(注释行)和直接 torch 系列,允许 "torch==2.1.0" / "torch>=2.0" 这种 pin
    private static readonly Regex TorchLinePattern = new(
        @"^\s*#?\s*(torch|torchvision|torchaudio|torchtext|torchdata)(\s|$|[=<>!~])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppLogger? _logger;

    public RequirementsInstaller(AppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检查 env 是否已经装过 requirements.txt(marker 文件存在)。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, MarkerFileName));
    }

    /// <summary>
    /// 过滤掉 torch 系列行 — 用 static + Testable 让 unit test 直接验。
    /// 保留空行 / 普通注释 / 其他依赖。
    /// </summary>
    public static List<string> FilterTorchLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var raw in rawLines)
        {
            var line = raw ?? "";
            if (TorchLinePattern.IsMatch(line)) continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// 装 ComfyUI requirements.txt(过滤 torch 行)。
    /// 成功 → 写 marker 文件 + 返 Success=true。
    /// 失败 / 取消 → 返 Success=false,Cancelled / Reason 字段描述。
    /// </summary>
    public virtual async Task<RequirementsInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        _logger?.Info("requirements", $"env='{env.Name}' 开始装 requirements.txt");

        var requirementsPath = Path.Combine(env.RootPath, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            var reason = $"找不到 requirements.txt(已尝试:{requirementsPath})";
            LogResult(env.Name, "failed", reason);
            return new RequirementsInstallResult(
                Success: false,
                Cancelled: false,
                Reason: reason,
                InstalledCount: 0);
        }

        List<string> rawLines;
        try
        {
            rawLines = new List<string>(await File.ReadAllLinesAsync(requirementsPath, ct));
        }
        catch (Exception ex)
        {
            LogResult(env.Name, "failed", $"读取 requirements.txt 失败:{ex.Message}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"读取 requirements.txt 失败:{ex.Message}",
                InstalledCount: 0);
        }

        var filtered = FilterTorchLines(rawLines);
        var filteredPath = Path.Combine(env.RootPath, FilteredRequirementsFileName);
        try
        {
            await File.WriteAllLinesAsync(filteredPath, filtered, ct);
        }
        catch (Exception ex)
        {
            LogResult(env.Name, "failed", $"写过滤文件失败:{ex.Message}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"写过滤文件失败:{ex.Message}",
                InstalledCount: 0);
        }

        var pythonExe = ResolveVenvPython(env);
        var pipResult = await RunPipAsync(
            pythonExe,
            new[] { "install", "-r", filteredPath, "--disable-pip-version-check" },
            line => logProgress?.Report(line),
            ct);

        // 清理 filtered 文件(成功失败都清)
        try { File.Delete(filteredPath); } catch { }

        if (pipResult.WasCancelled || ct.IsCancellationRequested)
        {
            LogResult(env.Name, "cancelled", "用户取消");
            return new RequirementsInstallResult(
                Success: false, Cancelled: true,
                Reason: "用户取消",
                InstalledCount: 0);
        }

        if (pipResult.ExitCode != 0)
        {
            var reason = $"pip 退出码 {pipResult.ExitCode}";
            LogResult(env.Name, "failed", reason);
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: reason,
                InstalledCount: 0);
        }

        // 成功 → 写 marker
        var markerPath = Path.Combine(env.RootPath, MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch
        {
            // marker 写失败不致命 — 下次用户再点还是会跳过已经装好的包(pip 自带幂等)
        }

        LogResult(env.Name, "succeeded", null);
        return new RequirementsInstallResult(
            Success: true, Cancelled: false,
            Reason: null,
            InstalledCount: filtered.Count);
    }

    private void LogResult(string envName, string status, string? reason)
    {
        if (_logger is null) return;
        var msg = reason is null
            ? $"env='{envName}' {status}"
            : $"env='{envName}' {status} — {reason}";
        if (status == "succeeded") _logger.Info("requirements", msg);
        else _logger.Error("requirements", msg);
    }

    private static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }

        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            throw new InvalidOperationException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath,无法定位 venv python");
        }

        var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                $"venv python 找不到:{exe}");
        }
        return exe;
    }

    /// <summary>
    /// 跑 `&lt;pythonExe&gt; -m pip &lt;pipArgs&gt;`,每行 stdout/stderr 回调 onLine。
    /// 跟 BaseEnvInstaller.RunPipAsync 类似但不接 percent(pip install -r 没
    /// progress 格式),测试可 override。
    /// </summary>
    protected virtual Task<PipResult> RunPipAsync(
        string pythonExe,
        IReadOnlyList<string> pipArgs,
        Action<string> onLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pythonExe))
        {
            throw new ArgumentException("pythonExe 不能为空", nameof(pythonExe));
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        foreach (var a in pipArgs)
        {
            psi.ArgumentList.Add(a);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动 pip 失败:{ex.Message}", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("Process.Start 返回 null");
        }

        var tcs = new TaskCompletionSource<PipResult>();
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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task);
                using var reg = ct.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });
                await process.WaitForExitAsync(CancellationToken.None);
                tcs.TrySetResult(new PipResult(process.ExitCode, WasCancelled: ct.IsCancellationRequested));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });

        return tcs.Task;
    }
}

public record RequirementsInstallResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int InstalledCount);
