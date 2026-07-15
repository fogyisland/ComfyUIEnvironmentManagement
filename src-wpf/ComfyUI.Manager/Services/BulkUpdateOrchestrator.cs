using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// BulkUpdateOrchestrator:串行在 (env, node) 组合上跑 git pull,逐行 emit
/// 进度事件。这是 M5.2 移除 Python control service 后,替代原
/// bulk_update_service.py 在 WPF 端跑批量的实现。
///
/// - 串行:每次一个 git 进程,避免并发抢占 stdout/stderr pipe。
/// - 跳过 vs 失败:env 缺 custom_nodes_path 或 node 目录不存在 → "skipped";
///   git 返回非 0 / 超时 / 抛异常 → "failed"。
/// - 超时:每个 git pull 上限 30s,超时即 cancel 进程,记为 failed。
/// - 取消:Caller 通过传入的 CancellationToken 或调用本类的 CancelAsync()
///   取消,已发出的 Progress 行保留 terminal 状态,未发出的不再发出。
/// - 日志:每个 bulk run 一个 &lt;projectRoot&gt;/logs/bulk-update-&lt;bulkId&gt;.log。
/// - 代理:每次 git 调用读 live GitProxyConfig,启用时把 HTTP_PROXY/HTTPS_PROXY
///   写到 psi.EnvironmentVariables(per-process,不污染整个 WPF)。
/// </summary>
public sealed class BulkUpdateOrchestrator
{
    private const int PerCallTimeoutMs = 30_000;

    private readonly string _projectRoot;
    private readonly string _gitExe;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly GitProxyConfig? _proxy;

    private CancellationTokenSource? _runCts;
    private readonly object _runLock = new();
    private string _currentBulkId = "";

    /// <summary>每行 (env, node) 状态变更时触发。在 background task 上触发。</summary>
    public event Action<BulkUpdateRow>? Progress;

    /// <summary>整个 run 结束(成功 / 取消 / 失败)时触发一次。</summary>
    public event Action<BulkUpdateSummary>? Completed;

    /// <summary>cancellation 触发且 run 后续不再产生事件时触发。</summary>
    public event Action? Cancelled;

    /// <summary>当前 run 的 bulkId。Start 前为空字符串。</summary>
    public string CurrentBulkId
    {
        get
        {
            lock (_runLock) { return _currentBulkId; }
        }
    }

    public BulkUpdateOrchestrator(
        string projectRoot,
        string gitExe,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        GitProxyConfig? proxy = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));
        }
        if (string.IsNullOrWhiteSpace(gitExe))
        {
            throw new ArgumentException("gitExe 不能为空", nameof(gitExe));
        }
        _projectRoot = projectRoot;
        _gitExe = gitExe;
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _proxy = proxy;
    }

    /// <summary>
    /// 通知 orchestrator 取消当前 run(若已结束则 noop)。
    /// 与传入 StartAsync 的 CancellationToken 互不影响,因为内部已经通过
    /// CreateLinkedTokenSource 把两者绑在一起 —— 任一触发都取消。
    /// </summary>
    public void CancelAsync()
    {
        CancellationTokenSource? cts;
        lock (_runLock)
        {
            cts = _runCts;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
    }

    /// <summary>
    /// 跑一次批量更新。返回的 Task 在 run 完成(success / cancelled / fail)后结束。
    /// 内部用 Task.Run 包装以避免阻塞调用线程。事件从 background task 触发。
    /// </summary>
    public Task<BulkUpdateSummary> StartAsync(
        IReadOnlyList<string> envIds,
        IReadOnlyList<string> nodeIds,
        CancellationToken ct = default)
    {
        if (envIds is null) throw new ArgumentNullException(nameof(envIds));
        if (nodeIds is null) throw new ArgumentNullException(nameof(nodeIds));

        var bulkId = Guid.NewGuid().ToString("N");
        CancellationTokenSource linked;
        lock (_runLock)
        {
            // 重新开始:如果上一个 CTS 还在,先干掉,避免前 run 的取消影响新 run。
            try { _runCts?.Cancel(); } catch { }
            try { _runCts?.Dispose(); } catch { }
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts = linked;
            _currentBulkId = bulkId;
        }

        // 用 Task.Run 包装异步 method body,把整个 run 丢到后台。
        return Task.Run(() => RunAsync(bulkId, envIds, nodeIds, linked.Token, linked));
    }

    private async Task<BulkUpdateSummary> RunAsync(
        string bulkId,
        IReadOnlyList<string> envIds,
        IReadOnlyList<string> nodeIds,
        CancellationToken ct,
        CancellationTokenSource linkedCts)
    {
        var logPath = Path.Combine(_projectRoot, "logs", $"bulk-update-{bulkId}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // 全 run 共享一个流,而不是每次 (env,node) 重新打开 ——
        // 保持与 ProcessLauncher 的 log 格式风格一致,便于 tail。
        await using var logStream = new FileStream(
            logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        var rows = new List<BulkUpdateRow>();
        int succeeded = 0, skipped = 0, failed = 0;
        bool cancelledByUser = false;

        foreach (var envId in envIds)
        {
            if (ct.IsCancellationRequested)
            {
                cancelledByUser = true;
                break;
            }

            var env = _envRepo.Get(envId);
            if (env is null)
            {
                // env row 不存在 —— 视为 env 跳过,所有 node 跳过
                foreach (var nodeId in nodeIds)
                {
                    var tsRow = Stopwatch.StartNew();
                    var row = Emit(rows, logWriter, bulkId, envId, nodeId, "skipped", "env 不存在", 0);
                    tsRow.Stop();
                    skipped++;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
            {
                foreach (var nodeId in nodeIds)
                {
                    var tsRow = Stopwatch.StartNew();
                    var row = Emit(rows, logWriter, bulkId, envId, nodeId, "skipped", "env 缺 custom_nodes_path", 0);
                    tsRow.Stop();
                    skipped++;
                }
                continue;
            }

            foreach (var nodeId in nodeIds)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelledByUser = true;
                    break;
                }

                var nodeDir = Path.Combine(env.CustomNodesPath, nodeId);
                var sw = Stopwatch.StartNew();

                // emit "running"
                var runningRow = new BulkUpdateRow(envId, nodeId, "running", null, 0);
                rows.Add(runningRow);
                EmitLog(logWriter, bulkId, envId, nodeId, "START");

                var pStart = Progress;
                pStart?.Invoke(runningRow);

                // 节点目录不存在 → skip
                if (!Directory.Exists(nodeDir))
                {
                    sw.Stop();
                    var skippedRow = new BulkUpdateRow(
                        envId, nodeId, "skipped", "目录不存在", (int)sw.ElapsedMilliseconds);
                    ReplaceLast(rows, runningRow, skippedRow);
                    EmitLog(logWriter, bulkId, envId, nodeId,
                        $"END status=skipped reason=目录不存在 ms={sw.ElapsedMilliseconds}");
                    var pSkip = Progress;
                    pSkip?.Invoke(skippedRow);
                    skipped++;
                    continue;
                }

                // 跑 git pull --ff-only
                var (status, reason, stdout, stderr) = await RunGitPullAsync(nodeDir, ct);
                sw.Stop();

                EmitLog(logWriter, bulkId, envId, nodeId,
                    $"END status={status} reason={reason ?? "-"} ms={sw.ElapsedMilliseconds}");
                foreach (var line in EnumerateLines(stdout))
                {
                    EmitLog(logWriter, bulkId, envId, nodeId, $"OUT: {line}");
                }
                foreach (var line in EnumerateLines(stderr))
                {
                    EmitLog(logWriter, bulkId, envId, nodeId, $"ERR: {line}");
                }

                var terminalRow = new BulkUpdateRow(
                    envId, nodeId, status, reason, (int)sw.ElapsedMilliseconds);
                ReplaceLast(rows, runningRow, terminalRow);
                var pDone = Progress;
                pDone?.Invoke(terminalRow);

                if (status == "succeeded") succeeded++;
                else failed++;
            }

            if (cancelledByUser) break;
        }

        var summary = new BulkUpdateSummary(
            Total: rows.Count,
            Succeeded: succeeded,
            Skipped: skipped,
            Failed: failed,
            Rows: rows);

        // 顺序:Completed 先(订阅者拿 summary),然后 Cancelled(如果真的是取消)。
        // 订阅者的 Completed 处理通常把 Mode 切到 Summary。
        try
        {
            var pDone = Completed;
            pDone?.Invoke(summary);
        }
        catch
        {
            // 单个订阅者抛了不能阻断 Cancelled / 资源清理
        }

        if (cancelledByUser)
        {
            try
            {
                var pCancel = Cancelled;
                pCancel?.Invoke();
            }
            catch { }
        }

        // 清理:释放本次 run 的 CTS,以便 CancelAsync 后再 Start 能干净跑。
        lock (_runLock)
        {
            if (ReferenceEquals(_runCts, linkedCts))
            {
                try { _runCts?.Dispose(); } catch { }
                _runCts = null;
                _currentBulkId = "";
            }
        }

        return summary;
    }

    /// <summary>
    /// 跑 `git -C &lt;dir&gt; pull --ff-only`,30s 超时。
    /// 返回 status: "succeeded" | "failed"; reason: null / "timeout" / stderr 头 / 异常信息。
    /// </summary>
    private async Task<(string Status, string? Reason, string Stdout, string Stderr)>
        RunGitPullAsync(string nodeDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = nodeDir,
        };
        // 代理:启用时把 HTTP_PROXY/HTTPS_PROXY 注入到这一个 psi(per-process)。
        _proxy?.ApplyTo(psi);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(nodeDir);
        psi.ArgumentList.Add("pull");
        psi.ArgumentList.Add("--ff-only");

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ("failed", $"启动 git 失败: {ex.Message}", "", "");
        }

        if (process is null)
        {
            return ("failed", "Process.Start 返回 null", "", "");
        }

        // 异步收集 stdout / stderr;并自己处理超时(而不是依赖 WaitForExit
        // 单调阻塞)。
        var stdoutT = process.StandardOutput.ReadToEndAsync();
        var stderrT = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerCallTimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // 等 reader 自然结束(进程已 kill,pipes 已关)。
            try { await stdoutT; } catch { }
            try { await stderrT; } catch { }
            string reason;
            if (ct.IsCancellationRequested) reason = "用户取消";
            else reason = "timeout";
            return ("failed", reason, "", "");
        }

        var stdout = "";
        var stderr = "";
        try { stdout = await stdoutT; } catch { }
        try { stderr = await stderrT; } catch { }

        if (process.ExitCode == 0)
        {
            return ("succeeded", null, stdout, stderr);
        }

        // 失败:取 stderr 第一行作 reason,截断到 200 字
        var firstLine = "";
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var nlIdx = stderr.IndexOf('\n');
            firstLine = nlIdx >= 0 ? stderr[..nlIdx] : stderr;
        }
        if (string.IsNullOrWhiteSpace(firstLine) && !string.IsNullOrWhiteSpace(stdout))
        {
            var nlIdx = stdout.IndexOf('\n');
            firstLine = nlIdx >= 0 ? stdout[..nlIdx] : stdout;
        }
        firstLine = firstLine.Trim();
        if (firstLine.Length > 200)
        {
            firstLine = firstLine[..200] + "…";
        }
        var exitReason = string.IsNullOrWhiteSpace(firstLine)
            ? $"git 退出码 {process.ExitCode}"
            : firstLine;
        return ("failed", exitReason, stdout, stderr);
    }

    private BulkUpdateRow Emit(
        List<BulkUpdateRow> rows,
        StreamWriter logWriter,
        string bulkId,
        string envId,
        string nodeId,
        string status,
        string? reason,
        int latencyMs)
    {
        var row = new BulkUpdateRow(envId, nodeId, status, reason, latencyMs);
        rows.Add(row);
        EmitLog(logWriter, bulkId, envId, nodeId,
            $"END status={status} reason={reason ?? "-"} ms={latencyMs}");
        var pEmit = Progress;
        pEmit?.Invoke(row);
        return new BulkUpdateRow(envId, nodeId, status, reason, latencyMs);
    }

    private static void ReplaceLast(
        List<BulkUpdateRow> rows,
        BulkUpdateRow oldRow,
        BulkUpdateRow newRow)
    {
        // rows 末条一定是 oldRow(running 直接 push 进来的)。替换它的字段,
        // 因为 BulkUpdateRow 是 record,这里用 Add + Remove 重新替换条目,
        // 否则订阅者通过引用比对 oldRow 找不到。
        var idx = rows.IndexOf(oldRow);
        if (idx < 0) return;
        rows[idx] = newRow;
    }

    private static void EmitLog(
        StreamWriter w,
        string bulkId,
        string envId,
        string nodeId,
        string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        w.WriteLine($"[{ts}] [bulk {bulkId[..8]}] env={envId} node={nodeId} {message}");
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed)) yield return trimmed;
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }
}
