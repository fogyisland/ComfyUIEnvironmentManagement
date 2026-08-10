using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// RequirementsFileInstaller:对单个 requirements.txt 文件做
/// "过滤 torch 行 + 写 filtered 文件 + 跑 pip install -r + 清理"。
///
/// v0.6.11+ T1 抽出:ComfyUI 自己的 requirements(RequirementsInstaller)和
/// ComfyUI Manager 的 requirements(ComfyUIManagerInstaller)都需要跑同一段
/// 逻辑,避免复制 30 行 pip + 过滤 + 临时文件清理代码。
///
/// 行为:
/// - requirementsFilePath 不存在 → 返 Failure(reason="requirements.txt 不存在:{path}")
/// - 过滤 → 写 filteredOutputPath(覆盖)
/// - 跑 pip,onLine 每行 stdout/stderr
/// - 成功 → 删 filteredOutputPath,返 Success
/// - pip 非零 / 取消 → 删 filteredOutputPath,返 Failure/Cancelled
/// </summary>
public sealed class RequirementsFileInstaller
{
    public const string FilteredRequirementsFileName = ".requirements_filtered.txt";

    private static readonly Regex TorchLinePattern = new(
        @"^\s*#?\s*(torch|torchvision|torchaudio|torchtext|torchdata)(\s|$|[=<>!~])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 过滤掉 torch 系列行(让 BED profile 锁版本不被覆盖)。保留空行 / 普通注释 / 其他依赖。
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
    /// 跑 <c>pip install -r &lt;filteredOutputPath&gt;</c>(文件已 caller 写好),
    /// 每行 stdout/stderr 回调 onLine。失败/取消不抛 — 返 RequirementsInstallResult。
    /// filteredOutputPath 会在末尾清理(成功失败都清)。
    /// </summary>
    public async Task<RequirementsInstallResult> InstallAsync(
        string requirementsFilePath,
        string filteredOutputPath,
        string venvPythonPath,
        Action<string>? onLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requirementsFilePath))
            throw new ArgumentException("requirementsFilePath 不能为空", nameof(requirementsFilePath));
        if (string.IsNullOrWhiteSpace(filteredOutputPath))
            throw new ArgumentException("filteredOutputPath 不能为空", nameof(filteredOutputPath));
        if (string.IsNullOrWhiteSpace(venvPythonPath))
            throw new ArgumentException("venvPythonPath 不能为空", nameof(venvPythonPath));

        if (!File.Exists(requirementsFilePath))
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"requirements.txt 不存在:{requirementsFilePath}",
                InstalledCount: 0);
        }

        // filtered 文件先写(覆盖)
        List<string> rawLines;
        try
        {
            rawLines = new List<string>(await File.ReadAllLinesAsync(requirementsFilePath, ct));
        }
        catch (Exception ex)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"读取 requirements.txt 失败:{ex.Message}",
                InstalledCount: 0);
        }
        var filtered = FilterTorchLines(rawLines);
        try
        {
            await File.WriteAllLinesAsync(filteredOutputPath, filtered, ct);
        }
        catch (Exception ex)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"写过滤文件失败:{ex.Message}",
                InstalledCount: 0);
        }

        var pipResult = await RunPipAsync(
            venvPythonPath,
            new[] { "install", "-r", filteredOutputPath, "--disable-pip-version-check" },
            onLine ?? (_ => { }),
            ct);

        try { File.Delete(filteredOutputPath); } catch { }

        if (pipResult.WasCancelled || ct.IsCancellationRequested)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: true,
                Reason: "用户取消",
                InstalledCount: 0);
        }
        if (pipResult.ExitCode != 0)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: $"pip 退出码 {pipResult.ExitCode}",
                InstalledCount: 0);
        }
        return new RequirementsInstallResult(
            Success: true, Cancelled: false,
            Reason: null,
            InstalledCount: filtered.Count);
    }

    private static async Task<PipResult> RunPipAsync(
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

        return await tcs.Task;
    }
}
