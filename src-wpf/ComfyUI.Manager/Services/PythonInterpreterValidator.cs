using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

public sealed record ValidationResult(bool IsValid, string Version = "", string? Error = null);

public sealed class PythonInterpreterValidator
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex VersionRegex =
        new(@"Python\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public async Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ValidationResult(false, Error: "路径不存在");

        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new ValidationResult(false, Error: "无法启动进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new ValidationResult(false, Error: "超时");
            }

            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = string.IsNullOrEmpty(stdout)
                ? (await stderrTask.ConfigureAwait(false)).Trim()
                : "";

            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            if (string.IsNullOrEmpty(output))
                return new ValidationResult(false, Error: "无输出");

            var m = VersionRegex.Match(output);
            if (!m.Success)
                return new ValidationResult(false, Error: "不是合法 Python 解释器");

            return new ValidationResult(true, Version: m.Groups[1].Value);
        }
        catch (OperationCanceledException)
        {
            return new ValidationResult(false, Error: "超时");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            return new ValidationResult(false, Error: $"启动失败:{ex.Message}");
        }
    }
}
