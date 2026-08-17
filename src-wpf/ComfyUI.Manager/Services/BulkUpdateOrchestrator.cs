using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// BulkUpdateOrchestrator:串行在 (env, target) 组合上跑 git pull,逐行 emit
/// 进度事件。v0.6.11 T8:target 不再是 per-node,而是 ComfyUI 源或
/// ComfyUI-Manager 子目录。v0.6.18.1 再加 <see cref="BulkUpdateTargetKind.Node"/>
/// target — 给定 (envId, nodeId) 在 node.PackagePath 上 git pull。
///
/// - 串行:每次一个 git 进程,避免并发抢占 stdout/stderr pipe。
/// - 跳过 vs 失败:env 不存在 / env 缺 ComfyuiSource / target 目录不存在 /
///   Node 节点未注册 / Node 节点目录不存在 → "skipped";git 返回非 0 / 超时 /
///   抛异常 → "failed"。注:BED 状态不参与判断(git pull 跟 torch 部署无关)。
/// - 超时:每个 git pull 上限 30s,超时即 cancel 进程,记为 failed。
/// - 取消:Caller 通过传入的 CancellationToken 或调用本类的 CancelAsync()
///   取消,已发出的 Progress 行保留 terminal 状态,未发出的不再发出。
/// - 日志:每个 bulk run 一个 &lt;projectRoot&gt;/logs/bulk-update-&lt;bulkId&gt;.log。
/// - 代理:每次 git 调用读 live HttpProxyConfig,启用时把 HTTP_PROXY/HTTPS_PROXY
///   写到 psi.EnvironmentVariables(per-process,不污染整个 WPF)。
/// </summary>
public sealed class BulkUpdateOrchestrator
{
    private const int PerCallTimeoutMs = 30_000;

    private readonly string _projectRoot;
    private readonly string _gitExe;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly HttpProxyConfig? _proxy;
    private readonly AppLogger? _logger;

    private CancellationTokenSource? _runCts;
    private readonly object _runLock = new();
    private string _currentBulkId = "";

    /// <summary>每行 (env, target) 状态变更时触发。在 background task 上触发。</summary>
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
        HttpProxyConfig? proxy = null,
        AppLogger? logger = null)
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
        _logger = logger;
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
    ///
    /// v0.6.18.1:job 是 (EnvId, TargetKind, NodeId?) 三元组 ——
    /// - <c>NodeId</c> 仅在 <see cref="BulkUpdateTargetKind.Node"/> 类型上有意义,
    ///   其余类型传 null。
    /// - env-level target(<c>ComfyUi</c> / <c>ComfyUiManager</c>)和 node-level
    ///   target(<c>Node</c>)可以在同一个 job 列表里混排,orchestrator 串行处理,
    ///   共享 Progress / Completed / Cancelled 事件接口。
    ///
    /// v0.6.18.4:<paramref name="log"/> 可选 — 每个 job 的 git pull stdout/stderr
    /// 实时通过 <c>log.Report("[envId · itemName] line")</c> 派发,VM 用这个串推
    /// Console 面板 ObservableCollection(同 <see cref="EnvStartStatusViewModel"/>
    /// 的 IProgress&lt;string&gt; 模式)。为 null 时 orchestrator 不发 console 行。
    /// </summary>
    public Task<BulkUpdateSummary> StartAsync(
        IReadOnlyList<(string EnvId, BulkUpdateTargetKind TargetKind, string? NodeId)> jobs,
        CancellationToken ct = default,
        IProgress<string>? log = null)
    {
        if (jobs is null) throw new ArgumentNullException(nameof(jobs));

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
        return Task.Run(() => RunAsync(bulkId, jobs, linked.Token, linked, log));
    }

    private async Task<BulkUpdateSummary> RunAsync(
        string bulkId,
        IReadOnlyList<(string EnvId, BulkUpdateTargetKind TargetKind, string? NodeId)> jobs,
        CancellationToken ct,
        CancellationTokenSource linkedCts,
        IProgress<string>? log)
    {
        // v0.6.18.4:所有 console 行都加 [envId · itemName] 前缀方便用户在面板里
        // 区分多 job 混排输出;itemName 跟 <see cref="BulkUpdateRow.ItemName"/>
        // 走同样规则(Node → nodeId,env-level → EnvId + 后缀)。
        string Label(string envId, BulkUpdateTargetKind t, string? nodeId) =>
            $"[{envId} · {(t == BulkUpdateTargetKind.Node ? (nodeId ?? "?") : t == BulkUpdateTargetKind.ComfyUi ? "基础环境" : "ComfyUI-Manager")}]";

        _logger?.Info("bulk-update", $"开始 bulkId={bulkId[..8]} jobs={jobs.Count}");

        var logPath = Path.Combine(_projectRoot, "logs", $"bulk-update-{bulkId}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // 全 run 共享一个流,而不是每次 (env,target) 重新打开 ——
        // 保持与 ProcessLauncher 的 log 格式风格一致,便于 tail。
        await using var logStream = new FileStream(
            logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        var rows = new List<BulkUpdateRow>();
        int succeeded = 0, skipped = 0, failed = 0;
        bool cancelledByUser = false;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested)
            {
                cancelledByUser = true;
                break;
            }

            var (envId, target, nodeId) = job;

            // 1. env 必须存在 —— 没 env 一切免谈
            var env = _envRepo.Get(envId);
            if (env is null)
            {
                var tsRow = Stopwatch.StartNew();
                var row = Emit(rows, logWriter, bulkId, envId, target, nodeId, "skipped", "env 不存在", 0);
                tsRow.Stop();
                skipped++;
                continue;
            }

            // 2. Node 目标额外查 node 行;非 Node 目标查 env.ComfyuiSource
            ScannedNode? node = null;
            if (target == BulkUpdateTargetKind.Node)
            {
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    var tsRow = Stopwatch.StartNew();
                    var row = Emit(rows, logWriter, bulkId, envId, target, nodeId, "skipped", "缺少 nodeId", 0);
                    tsRow.Stop();
                    skipped++;
                    continue;
                }
                node = _nodeRepo.Get(nodeId);
                if (node is null)
                {
                    var tsRow = Stopwatch.StartNew();
                    var row = Emit(rows, logWriter, bulkId, envId, target, nodeId, "skipped", "节点未注册", 0);
                    tsRow.Stop();
                    skipped++;
                    continue;
                }
            }
            else if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
            {
                var tsRow = Stopwatch.StartNew();
                var row = Emit(rows, logWriter, bulkId, envId, target, nodeId, "skipped", "env 缺 ComfyUI 源", 0);
                tsRow.Stop();
                skipped++;
                continue;
            }

            // 3. 实际跑
            if (ct.IsCancellationRequested)
            {
                cancelledByUser = true;
                break;
            }

            var targetDir = ResolveTargetDir(env, node, target);
            var sw = Stopwatch.StartNew();

            // emit "running"
            var runningRow = new BulkUpdateRow(envId, target, "running", null, 0, 0, nodeId);
            rows.Add(runningRow);
            EmitLog(logWriter, bulkId, envId, target, nodeId, "START");

            var pStart = Progress;
            pStart?.Invoke(runningRow);

            // v0.6.18.4:每个 job 开始时 emit 一条 console 行,显示当前在跑的命令
            // (用户能在面板里看到 "激活虚拟环境 → cd dir → git pull" 的入口)。
            log?.Report($"{Label(envId, target, nodeId)} 开始:git pull --ff-only --progress");

            // target 目录不存在 → skip,reason 按 target 类型不同:
            // - ComfyUi → "目录不存在"(ComfyUI 源都不在)
            // - ComfyUiManager → "ComfyUI-Manager 未安装"(只 manager 子目录缺失)
            // - Node → "节点目录不存在"(scanned_nodes.PackagePath 不在 fs)
            if (targetDir is null || !Directory.Exists(targetDir))
            {
                sw.Stop();
                string skipReason = target switch
                {
                    BulkUpdateTargetKind.ComfyUiManager => "ComfyUI-Manager 未安装",
                    BulkUpdateTargetKind.Node => "节点目录不存在",
                    _ => "目录不存在",
                };
                var skippedRow = new BulkUpdateRow(
                    envId, target, "skipped", skipReason, (int)sw.ElapsedMilliseconds, 0, nodeId);
                ReplaceLast(rows, runningRow, skippedRow);
                EmitLog(logWriter, bulkId, envId, target, nodeId,
                    $"END status=skipped reason={skipReason} ms={sw.ElapsedMilliseconds}");
                log?.Report($"{Label(envId, target, nodeId)} 跳过:{skipReason}");
                var pSkip = Progress;
                pSkip?.Invoke(skippedRow);
                skipped++;
                continue;
            }

            // 跑 git pull --ff-only
            var (status, reason, stdout, stderr) = await RunGitPullAsync(envId, target, nodeId, targetDir, ct);
            sw.Stop();

            EmitLog(logWriter, bulkId, envId, target, nodeId,
                $"END status={status} reason={reason ?? "-"} ms={sw.ElapsedMilliseconds}");
            // v0.6.18.4:把 stdout/stderr 流式 emit 到 console(同 label 前缀),让用户
            // 看到 git pull 真实输出("Already up to date." / "Updating abc..def" /
            // "Receiving objects: 67%" 等),而不只是 terminal status。
            foreach (var line in EnumerateLines(stdout))
            {
                EmitLog(logWriter, bulkId, envId, target, nodeId, $"OUT: {line}");
                log?.Report($"{Label(envId, target, nodeId)} {line}");
            }
            foreach (var line in EnumerateLines(stderr))
            {
                EmitLog(logWriter, bulkId, envId, target, nodeId, $"ERR: {line}");
                log?.Report($"{Label(envId, target, nodeId)} {line}");
            }
            // 终端行让用户一眼看到结果(成功/失败原因)
            log?.Report($"{Label(envId, target, nodeId)} END status={status}{(reason is null ? "" : " reason=" + reason)} ms={sw.ElapsedMilliseconds}");

            var terminalRow = new BulkUpdateRow(
                envId, target, status, reason, (int)sw.ElapsedMilliseconds, 0, nodeId);
            ReplaceLast(rows, runningRow, terminalRow);
            var pDone = Progress;
            pDone?.Invoke(terminalRow);

            if (status == "succeeded") succeeded++;
            else failed++;
        }

        var summary = new BulkUpdateSummary(
            Total: rows.Count,
            Succeeded: succeeded,
            Skipped: skipped,
            Failed: failed,
            Rows: rows);

        var summaryMsg = $"bulkId={bulkId[..8]} total={summary.Total} ok={summary.Succeeded} skip={summary.Skipped} fail={summary.Failed}";
        if (cancelledByUser || summary.Failed > 0)
            _logger?.Error("bulk-update", summaryMsg);
        else
            _logger?.Info("bulk-update", summaryMsg);

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
    /// 把 (env, targetKind, node?) 映射到具体目录路径。返回 null 表示该 target
    /// 不应尝试(调用方负责看 Directory.Exists)。
    ///
    /// v0.6.18.1:Node 走 <c>node.PackagePath</c>,其它两个走 env.ComfyuiSource 子路径。
    /// </summary>
    private static string? ResolveTargetDir(Environment env, ScannedNode? node, BulkUpdateTargetKind target)
    {
        if (target == BulkUpdateTargetKind.Node)
        {
            return node?.PackagePath;
        }
        if (string.IsNullOrWhiteSpace(env.ComfyuiSource)) return null;
        return target switch
        {
            BulkUpdateTargetKind.ComfyUi => env.ComfyuiSource,
            BulkUpdateTargetKind.ComfyUiManager => Path.Combine(
                env.ComfyuiSource, "custom_nodes", "ComfyUI-Manager"),
            _ => null,
        };
    }

    /// <summary>
    /// 跑 `git -C &lt;dir&gt; pull --ff-only`,30s 超时。
    /// 返回 status: "succeeded" | "failed"; reason: null / "timeout" / stderr 头 / 异常信息。
    ///
    /// v0.6.15.5 T5:加 --progress 标志让 git 实时 emit 进度行;用
    /// ErrorDataReceived 流式 capture(替代 ReadToEndAsync),每行 parse (\d+)%
    /// 实时 emit Progress 事件,UI 端能看到实时 percent。
    /// </summary>
    private async Task<(string Status, string? Reason, string Stdout, string Stderr)>
        RunGitPullAsync(
            string envId,
            BulkUpdateTargetKind targetKind,
            string? nodeId,
            string targetDir,
            CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = targetDir,
        };
        // 代理:启用时把 HTTP_PROXY/HTTPS_PROXY 注入到这一个 psi(per-process)。
        _proxy?.ApplyTo(psi);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(targetDir);
        psi.ArgumentList.Add("pull");
        psi.ArgumentList.Add("--ff-only");
        // v0.6.15.5 T5:让 git emit 进度到 stderr(默认是关的;开着之后
        // clone/fetch/pull 大仓库时会有 "Receiving objects:  45%" 这种行)。
        psi.ArgumentList.Add("--progress");

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

        // v0.6.15.5 T5:流式 capture stderr ——
        // - 收集到 stderrBuf(给返回值的 stderr 字段 + 后面 EmitLog 用)
        // - 实时 parse (\d+)% → emit Progress(envId, targetKind, "running", null, 0, percent)
        // - lock 保护 stderrBuf(回调在 ThreadPool 线程跑)
        var stderrBuf = new StringBuilder();
        var stderrLock = new object();
        var lastPercent = 0;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrLock)
            {
                stderrBuf.AppendLine(e.Data);
                var m = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+)%");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p >= 0 && p <= 100)
                {
                    // 只在 percent 真正上涨时 emit,避免同值刷屏
                    if (p > lastPercent)
                    {
                        lastPercent = p;
                        var pProgress = Progress;
                        pProgress?.Invoke(new BulkUpdateRow(
                            envId, targetKind, "running", null, 0, p, nodeId));
                    }
                }
            }
        };
        process.BeginErrorReadLine();

        // stdout 在 pull 时基本是空行("Already up to date." / "Updating..." 等),
        // 也流式 capture 跟 stderr 一致。
        var stdoutBuf = new StringBuilder();
        var stdoutLock = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdoutLock) { stdoutBuf.AppendLine(e.Data); }
        };
        process.BeginOutputReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerCallTimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // 进程 kill 后 reader 自然结束
            try { process.CancelErrorRead(); } catch { }
            try { process.CancelOutputRead(); } catch { }
            string reason;
            if (ct.IsCancellationRequested) reason = "用户取消";
            else reason = "timeout";
            return ("failed", reason, "", "");
        }

        // 等流式 reader 把 buffer 全部 flush 出来
        try { process.WaitForExit(); } catch { }

        var stdout = "";
        var stderr = "";
        lock (stdoutLock) { stdout = stdoutBuf.ToString(); }
        lock (stderrLock) { stderr = stderrBuf.ToString(); }

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
        BulkUpdateTargetKind target,
        string? nodeId,
        string status,
        string? reason,
        int latencyMs)
    {
        var row = new BulkUpdateRow(envId, target, status, reason, latencyMs, 0, nodeId);
        rows.Add(row);
        EmitLog(logWriter, bulkId, envId, target, nodeId,
            $"END status={status} reason={reason ?? "-"} ms={latencyMs}");
        var pEmit = Progress;
        pEmit?.Invoke(row);
        return new BulkUpdateRow(envId, target, status, reason, latencyMs, 0, nodeId);
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
        BulkUpdateTargetKind target,
        string? nodeId,
        string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var nodeSuffix = string.IsNullOrEmpty(nodeId) ? "" : $" node={nodeId}";
        w.WriteLine($"[{ts}] [bulk {bulkId[..8]}] env={envId} target={target}{nodeSuffix} {message}");
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