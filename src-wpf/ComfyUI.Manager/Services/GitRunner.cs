using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services;

/// <summary>
/// GitRunner: 包装 git.exe 调用,统一 stdout/stderr/exit code + timeout + cancellation。
///
/// 设计要点:
/// - 复用同一份 ProcessStartInfo 模板,只换 workdir 与 args
/// - timeout / cancellation 由 caller 通过 CancellationToken 传入(由 caller 决定上限)
/// - 返回 GitResult(exitCode / stdout / stderr),不抛异常 —— caller 按 ExitCode 决定怎么走
/// - 不动 PATH / 系统环境变量;git exe 路径由 caller 解析(portable / system git)
/// - 代理:每次 RunAsync 读 live GitProxyConfig,启用时把 HTTP_PROXY/HTTPS_PROXY
///   写到 psi.EnvironmentVariables(per-process,不污染整个 WPF)
/// </summary>
public sealed class GitRunner
{
    private readonly string _gitExe;
    private readonly GitProxyConfig? _proxy;

    public string GitExe => _gitExe;

    public GitRunner(string gitExe, GitProxyConfig? proxy = null)
    {
        if (string.IsNullOrWhiteSpace(gitExe))
        {
            throw new ArgumentException("gitExe 不能为空", nameof(gitExe));
        }
        _gitExe = gitExe;
        _proxy = proxy;
    }

    /// <summary>
    /// 在指定工作目录跑 `git &lt;args&gt;`。
    ///
    /// 返回:
    /// - GitResult { ExitCode, Stdout, Stderr }
    /// - 取消 / 超时:抛出 OperationCanceledException(原 ct 或 caller 提供的 timeout)
    /// - Process.Start 失败:抛出 InvalidOperationException
    /// </summary>
    public async Task<GitResult> RunAsync(
        string workdir,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workdir))
        {
            throw new ArgumentException("workdir 不能为空", nameof(workdir));
        }
        if (args is null) throw new ArgumentNullException(nameof(args));

        var psi = new ProcessStartInfo
        {
            FileName = _gitExe,
            WorkingDirectory = workdir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // 代理:启用时把 HTTP_PROXY/HTTPS_PROXY 注入到这一个 psi(per-process)。
        _proxy?.ApplyTo(psi);
        foreach (var a in args)
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
            throw new InvalidOperationException(
                $"无法启动 git: {ex.Message}", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("Process.Start 返回 null");
        }

        var stdoutT = process.StandardOutput.ReadToEndAsync();
        var stderrT = process.StandardError.ReadToEndAsync();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linkedCts.CancelAfter(t);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await stdoutT; } catch { }
            try { await stderrT; } catch { }
            throw;
        }

        var stdout = "";
        var stderr = "";
        try { stdout = await stdoutT; } catch { }
        try { stderr = await stderrT; } catch { }
        return new GitResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
    }
}

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}