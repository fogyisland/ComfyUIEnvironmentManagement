using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public sealed record VerifyResult(bool Ok, string? Version, string? ErrorMessage);

/// <summary>
/// VenvVerifier: validates that the bundled Python venv can import comfy_mgr.
/// Replaces PythonLauncher for WPF startup — we no longer launch any control
/// service, just check the venv is usable.
/// </summary>
public sealed class VenvVerifier
{
    private readonly string _projectRoot;

    public VenvVerifier(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public string PythonExe { get; } = "";
    public string ProjectRoot => _projectRoot;

    public VenvVerifier WithProjectRoot(string projectRoot)
        => new(projectRoot);

    public async Task<VerifyResult> VerifyAsync(
        CancellationToken ct = default, int timeoutSeconds = 5)
    {
        var pythonExe = Path.Combine(_projectRoot, "python", "python.exe");
        if (!File.Exists(pythonExe))
        {
            return new VerifyResult(false, null,
                $"python.exe 不存在: {pythonExe}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = _projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import comfy_mgr; print(comfy_mgr.__version__)");
        psi.EnvironmentVariables["PYTHONPATH"] =
            $"{_projectRoot};{Path.Combine(_projectRoot, "src")}";

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null,
                $"启动 python.exe 失败: {ex.Message}");
        }

        if (proc is null)
        {
            return new VerifyResult(false, null, "Process.Start 返回 null");
        }

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var exited = proc.WaitForExit(timeoutSeconds * 1000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new VerifyResult(false, null, "venv 验证超时");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                var errMsg = (stderr ?? "").Trim();
                if (string.IsNullOrEmpty(errMsg))
                {
                    errMsg = $"exit code {proc.ExitCode}";
                }
                return new VerifyResult(false, null,
                    $"import comfy_mgr 失败: {errMsg}");
            }

            var version = (stdout ?? "").Trim();
            if (string.IsNullOrEmpty(version))
            {
                return new VerifyResult(false, null,
                    "import comfy_mgr 成功但 stdout 为空");
            }
            return new VerifyResult(true, version, null);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, $"venv 验证异常: {ex.Message}");
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }
}