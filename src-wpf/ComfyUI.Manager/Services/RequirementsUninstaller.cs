using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// RequirementsUninstaller:v0.6.5.22 — 跑
/// <c>pip uninstall -y -r &lt;filtered&gt; --disable-pip-version-check</c>
/// 卸掉 <see cref="RequirementsInstaller"/> 装过的 ComfyUI 运行时依赖,然后删
/// <c>.requirements_installed</c> marker。
///
/// 跟 <see cref="RequirementsInstaller"/> 的对偶关系:
/// <list type="bullet">
/// <item>同一份 requirements.txt 候选解析(<c>ResolveRequirementsCandidates</c>);</item>
/// <item>同一套 torch 行过滤(<c>FilterTorchLines</c>)— torch 系列由 BED 管,
///   卸依赖不能把 BED 装的 torch 一起卸掉;</item>
/// <item>同一套 venv python 解析(<c>ResolveVenvPython</c>)。</item>
/// </list>
///
/// **临时 filtered 文件用独立文件名** <c>.requirements_uninstall_temp.txt</c>,
/// 避免跟 install flow 的 <c>.requirements_filtered.txt</c> 互相踩(两边并发时
/// 一方清理会把另一方的输入文件删掉)。T4 的 per-env mutex 兜底,但文件名隔离
/// 是更便宜的第二道防线。
///
/// **失败 / 取消都不删 marker** — 依赖还在 venv 里,marker 必须如实反映状态,
/// 否则用户下次点"装依赖"会被 v0.6.5.19 的 IsInstalled 短路挡住却其实没装全。
///
/// 类不是 <c>sealed</c>:测试通过继承覆盖 <see cref="RunPipAsync"/> 做 test seam
/// (跟 <see cref="RequirementsInstaller"/> 一致)。
/// </summary>
public class RequirementsUninstaller
{
    /// <summary>跟 <see cref="RequirementsInstaller.MarkerFileName"/> 同步的 re-export。</summary>
    public const string MarkerFileName = RequirementsInstaller.MarkerFileName;

    /// <summary>卸载专用的临时过滤文件名(跟 install 的隔开,避免互相清理)。</summary>
    public const string FilteredRequirementsFileName = ".requirements_uninstall_temp.txt";

    private readonly AppLogger? _logger;

    public RequirementsUninstaller(AppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 委托 <see cref="RequirementsInstaller.IsInstalled"/> — 两边判定必须一致。
    /// </summary>
    public static bool IsInstalled(Environment env)
        => RequirementsInstaller.IsInstalled(env);

    /// <summary>
    /// 卸载 ComfyUI requirements.txt 里的依赖(过滤 torch 行)。
    /// 成功 → 删 marker + 返 <c>Success=true</c>;
    /// 没装过 → 返 <c>AlreadyUninstalled=true, Success=true</c>(幂等,不跑 pip);
    /// 取消 / pip 非 0 退出 → 返 <c>Success=false</c>,**marker 保留**。
    /// </summary>
    public virtual async Task<RequirementsUninstallResult> UninstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null)
        {
            return Fail("env 为空", envName: "(null)");
        }

        if (!IsInstalled(env))
        {
            _logger?.Info("requirements-uninstall",
                $"env='{env.Name}' marker 不存在,视为已卸载(不跑 pip)");
            return new RequirementsUninstallResult(
                Success: true,
                AlreadyUninstalled: true,
                Cancelled: false,
                Reason: null,
                UninstalledCount: 0);
        }

        _logger?.Info("requirements-uninstall", $"env='{env.Name}' 开始卸载 requirements 依赖");

        var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);
        var requirementsPath = candidates.FirstOrDefault(File.Exists);
        if (requirementsPath is null)
        {
            return Fail(
                $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})",
                env.Name);
        }

        List<string> rawLines;
        try
        {
            rawLines = new List<string>(await File.ReadAllLinesAsync(requirementsPath, ct));
        }
        catch (Exception ex)
        {
            return Fail($"读取 requirements.txt 失败:{ex.Message}", env.Name);
        }

        var filtered = RequirementsInstaller.FilterTorchLines(rawLines);
        var filteredPath = Path.Combine(env.RootPath, FilteredRequirementsFileName);
        try
        {
            await File.WriteAllLinesAsync(filteredPath, filtered, ct);
        }
        catch (Exception ex)
        {
            return Fail($"写过滤文件失败:{ex.Message}", env.Name);
        }

        string pythonExe;
        try
        {
            pythonExe = RequirementsInstaller.ResolveVenvPython(env);
        }
        catch (Exception ex)
        {
            try { File.Delete(filteredPath); } catch { }
            return Fail(ex.Message, env.Name);
        }

        PipResult pipResult;
        try
        {
            pipResult = await RunPipAsync(
                pythonExe,
                new[] { "uninstall", "-y", "-r", filteredPath, "--disable-pip-version-check" },
                line => logProgress?.Report(line),
                ct);
        }
        catch (Exception ex)
        {
            try { File.Delete(filteredPath); } catch { }
            return Fail($"跑 pip uninstall 失败:{ex.Message}", env.Name);
        }

        // 清理临时 filtered 文件(成功失败都清)
        try { File.Delete(filteredPath); } catch { }

        if (pipResult.WasCancelled || ct.IsCancellationRequested)
        {
            LogResult(env.Name, "cancelled", "用户取消");
            return new RequirementsUninstallResult(
                Success: false,
                AlreadyUninstalled: false,
                Cancelled: true,
                Reason: "用户取消",
                UninstalledCount: 0);
        }

        if (pipResult.ExitCode != 0)
        {
            // marker 保留 — 依赖可能只卸了一部分,状态仍是"装过"。
            return Fail($"pip 退出码 {pipResult.ExitCode}", env.Name);
        }

        // 成功 → 删 marker(v1.0.0.x:A1111 / Forge 用 A1111PreFlightConstants.MarkerFileName;
        // 其他 ComfyUI 用老 MarkerFileName。两套 marker 各自独立,跟 IsInstalled 一致)。
        var markerPath = Path.Combine(
            env.RootPath,
            env.TemplateKind is "A1111" or "Forge"
                ? A1111PreFlightConstants.MarkerFileName
                : MarkerFileName);
        try
        {
            File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            // marker 删不掉是硬失败:UI 会继续显示"已装",用户下次点装依赖被短路。
            return Fail($"删除 marker 失败:{ex.Message}", env.Name);
        }

        LogResult(env.Name, "succeeded", null);
        return new RequirementsUninstallResult(
            Success: true,
            AlreadyUninstalled: false,
            Cancelled: false,
            Reason: null,
            UninstalledCount: filtered.Count);
    }

    private RequirementsUninstallResult Fail(string reason, string envName)
    {
        LogResult(envName, "failed", reason);
        return new RequirementsUninstallResult(
            Success: false,
            AlreadyUninstalled: false,
            Cancelled: false,
            Reason: reason,
            UninstalledCount: 0);
    }

    private void LogResult(string envName, string status, string? reason)
    {
        if (_logger is null) return;
        var msg = reason is null
            ? $"env='{envName}' {status}"
            : $"env='{envName}' {status} — {reason}";
        if (status == "succeeded") _logger.Info("requirements-uninstall", msg);
        else _logger.Error("requirements-uninstall", msg);
    }

    /// <summary>
    /// 跑 <c>&lt;pythonExe&gt; -m pip &lt;pipArgs&gt;</c>,每行 stdout/stderr 回调 onLine。
    /// 跟 <see cref="RequirementsInstaller.RunPipAsync"/> 同结构(无法复用 — 那边是
    /// 另一个类的 protected 成员),测试可 override 做 seam。
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

public record RequirementsUninstallResult(
    bool Success,
    bool AlreadyUninstalled,
    bool Cancelled,
    string? Reason,
    int UninstalledCount);
