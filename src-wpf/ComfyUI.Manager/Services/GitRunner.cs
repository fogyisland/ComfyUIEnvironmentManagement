using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
/// - 代理:每次 RunAsync 读 live HttpProxyConfig,启用时把 HTTP_PROXY/HTTPS_PROXY
///   写到 psi.EnvironmentVariables(per-process,不污染整个 WPF)
///
/// v0.6.15.5: 加 IProgress<string>? onStderrLine 实时 emit 进度行(Receiving objects 等);
/// 非 sealed 让 FakeGitRunner 在测试里 override。
/// </summary>
public class GitRunner
{
    private readonly string _gitExe;
    private readonly HttpProxyConfig? _proxy;

    public string GitExe => _gitExe;

    /// <summary>
    /// v1.0.0.x: 暴露代理配置给 caller(模板更新 Console log helper 需要读 mode/URL/Port
    /// 写 [src] → host (proxy info) 行)。null 表示从 ctor 未注入(纯直连)。
    /// </summary>
    public HttpProxyConfig? ProxyConfig => _proxy;

    public GitRunner(string gitExe, HttpProxyConfig? proxy = null)
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
    ///
    /// v0.6.15.5:
    /// - onStderrLine == null: 走原 ReadToEndAsync() 路径,完全向后兼容
    /// - onStderrLine != null: OutputDataReceived 流式 emit,只 emit 进度相关行
    ///   (Receiving objects: / Resolving deltas: / remote: / Cloning into),
    ///   仍 capture 全 stderr 到 GitResult.Stderr
    /// </summary>
    public virtual async Task<GitResult> RunAsync(
        string workdir,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        IProgress<string>? onStderrLine = null)
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

        // v0.6.15.5: streaming 模式 vs capture 模式
        var capturedStderr = new StringBuilder();
        var stderrT = onStderrLine is null
            ? process.StandardError.ReadToEndAsync()
            : (Task)Task.CompletedTask;

        if (onStderrLine is not null)
        {
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data is null) return;
                capturedStderr.AppendLine(e.Data);  // 仍 capture 给 GitResult.Stderr
                if (ShouldReportProgress(e.Data))
                {
                    onStderrLine.Report(e.Data);
                }
            };
            process.BeginErrorReadLine();
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linkedCts.CancelAfter(t);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (onStderrLine is not null) { try { process.CancelErrorRead(); } catch { } }
            throw;
        }

        // streaming 模式: flush stderr reader
        if (onStderrLine is not null)
        {
            try { process.WaitForExit(); } catch { } // flush BeginErrorReadLine buffer
        }

        var stdout = "";
        try { stdout = await process.StandardOutput.ReadToEndAsync(); } catch { }

        var stderr = onStderrLine is null
            ? await ((Task<string>)stderrT)
            : capturedStderr.ToString();
        return new GitResult(process.ExitCode, stdout, stderr);
    }

    // v0.6.15.5: 只 emit 进度相关行,过滤 git 自己的 noise(stderr "warning:" / "hint:" 等)
    private static bool ShouldReportProgress(string line)
    {
        return line.StartsWith("Receiving objects:")
            || line.StartsWith("Resolving deltas:")
            || line.StartsWith("remote:")
            || line.StartsWith("Cloning into");
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