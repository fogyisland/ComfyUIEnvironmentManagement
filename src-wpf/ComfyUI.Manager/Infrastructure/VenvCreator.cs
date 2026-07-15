using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// VenvCreator:在指定目录创建 Python venv,通过 <c>python.exe -m venv</c>。
///
/// M5.2 替代了 Python <c>VenvManager</c>(T9 删)。WPF 端不解析 venv 结构,
/// 只负责触发创建 + 失败 throw 清晰错误信息。
/// </summary>
public sealed class VenvCreator
{
    public sealed class VenvCreationException : Exception
    {
        public VenvCreationException(string message, int exitCode, string stderr)
            : base($"{message} (exit={exitCode}): {stderr}") { }
    }

    /// <summary>
    /// CreateAsync:调 <c>pythonExe -m venv &lt;venvPath&gt;</c> 同步等待。
    /// </summary>
    /// <exception cref="VenvCreationException">python 启动失败 / venv 创建失败</exception>
    public async Task CreateAsync(
        string pythonExe,
        string venvPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(pythonExe))
            throw new VenvCreationException(
                $"python 解释器不存在: {pythonExe}", -1, "");

        // venv 已存在但目录非空 → 失败
        if (Directory.Exists(venvPath) &&
            Directory.EnumerateFileSystemEntries(venvPath).Any())
        {
            throw new VenvCreationException(
                $"venv 目录已存在且非空: {venvPath}", -1, "");
        }

        Directory.CreateDirectory(venvPath);

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"-m venv \"{venvPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new VenvCreationException("python 启动失败", -1, "");

        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
        {
            // 失败回滚:删空 venv 目录
            try { Directory.Delete(venvPath, recursive: true); } catch { }
            throw new VenvCreationException(
                "venv 创建失败", p.ExitCode, stderr);
        }

        // 验证:Windows 上 venv 创建后必须有 python.exe
        var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            try { Directory.Delete(venvPath, recursive: true); } catch { }
            throw new VenvCreationException(
                $"venv 创建后缺少 python.exe: {venvPython}", -1, "");
        }
    }
}
