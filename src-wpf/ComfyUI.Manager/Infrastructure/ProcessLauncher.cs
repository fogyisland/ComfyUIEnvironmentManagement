using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// ProcessLauncher:直接 Process.Start 拉 ComfyUI,把 stdout/stderr 写到
/// logs/&lt;env-id&gt;.log 文件,并维护 process_state + environments 状态表。
///
/// 替代了 M5.1 的 PythonLauncher / ServiceConnection 思路 —— WPF 不再依赖
/// 任何 Python control service,每个 env 各自独立启停。
/// </summary>
public sealed class ProcessLauncher : IDisposable
{
    private readonly string _projectRoot;
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly EnvironmentRepository _envRepo;
    private readonly ProcessStateRepository _processStateRepo;
    private readonly AppLogger? _logger;
    private readonly int _startupTimeoutSeconds;
    private readonly string _comfyUiLocale;
    private readonly string _modelsDirectory;
    private readonly JunctionLinker _linker;
    private readonly string _logsDir;  // v0.6.12: Settings.LogDirectory (Logs parent) or projectRoot fallback
    private readonly NodeStartupErrorDetector? _startupErrorDetector;  // v0.6.15.7: scan captured startup lines for failed node imports
    private readonly NodeRepository? _nodeRepo;  // v0.6.15.7: write ScanMeta["load_error"] on detected failed nodes
    private readonly Dictionary<string, ProcessEntry> _running = new();
    private readonly object _runningLock = new();
    private bool _disposed;

    public ProcessLauncher(
        string projectRoot,
        SqliteConnectionFactory dbFactory,
        EnvironmentRepository envRepo,
        ProcessStateRepository processStateRepo,
        AppLogger? logger = null,
        int comfyUiStartupTimeoutSeconds = 600,
        string comfyUiLocale = "",
        string modelsDirectory = "",
        JunctionLinker? linker = null,
        string? logsDir = null,
        NodeStartupErrorDetector? startupErrorDetector = null,
        NodeRepository? nodeRepo = null)
    {
        _projectRoot = projectRoot;
        _dbFactory = dbFactory;
        _envRepo = envRepo;
        _processStateRepo = processStateRepo;
        _logger = logger;
        // v0.6.7.1: <=0 视作没配置,回落 600。
        _startupTimeoutSeconds = comfyUiStartupTimeoutSeconds > 0 ? comfyUiStartupTimeoutSeconds : 600;
        // v0.6.7.2: 空 = 不动 ComfyUI 配置(让 ComfyUI 用自身默认);非空就启动前写进
        // <comfyui-root>/user/default/comfy.settings.json 的 Comfy.Locale。
        _comfyUiLocale = comfyUiLocale ?? "";
        // v0.6.7.3 + v0.6.11+ T2:用户配置的全局 models 目录(env-create 时 junction,
        // env-start 时检查并重建)。空 = 不动 models 目录(走独立布局)。
        _modelsDirectory = modelsDirectory ?? "";
        // 默认 real,App 端不传也跑得动;测试可注入 RecordingJunctionLinker。
        _linker = linker ?? new JunctionLinker();
        // v0.6.12: Settings.LogDirectory (Logs 父目录) — null = 用 projectRoot。
        // AppLogger.OperationLogPath 会自己加 Logs 子目录。
        _logsDir = (logsDir ?? projectRoot).TrimEnd('\\', '/');
        // v0.6.15.7: 启动错误检测。null = 老行为(不扫描)。必须两参都给才生效
        // (detector 单飞无 repo → 报找不到节点;repo 单飞无 detector → 无事可做)。
        _startupErrorDetector = startupErrorDetector;
        _nodeRepo = nodeRepo;
    }

    public string ProjectRoot => _projectRoot;

    /// <summary>
    /// v0.6.7.1: 实际生效的启动就绪超时(秒)。对外暴露方便 App / 测试诊断。
    /// </summary>
    public int StartupTimeoutSeconds => _startupTimeoutSeconds;

    /// <summary>
    /// v0.6.17.3:per-env operation log 路径 — 子目录布局
    /// <c>{logsDir}/logs/env/{sanitized envName}/{yyyy-MM-dd}.log</c>。
    ///
    /// 历史:
    /// - v0.6.7.1: <c>{logsDir}/logs/{env-id}.log</c>(单文件,无滚动)
    /// - v0.6.12:  <c>{logsDir}/Logs/operation-{envName}-{date}.log</c>(平面 + 按日切)
    /// - v0.6.17.3: 子目录 <c>{logsDir}/logs/env/{envName}/{date}.log</c>(用户原话:
    ///   "日志目录更改为 logs\env\环境名称\当前日期.log")
    ///
    /// <paramref name="envId"/> 保留参数是向后兼容 —— 当前实现只用 envName。
    /// <paramref name="date"/> 默认 = DateTime.Now(本地时区;测试可显式传固定值)。
    /// </summary>
    public string LogFilePath(string envName, string envId, DateTime? date = null)
    {
        var d = date ?? DateTime.Now;
        return AppLogger.OperationLogPath(envName, d, _logsDir);
    }

    public bool IsRunning(Environment env)
    {
        lock (_runningLock)
        {
            return _running.ContainsKey(env.Id);
        }
    }

    public IReadOnlyList<string> RunningEnvIds
    {
        get
        {
            lock (_runningLock)
            {
                return _running.Keys.ToArray();
            }
        }
    }


    /// <summary>
    /// 启动一个 env。返回时进程已就绪(port 已 listen)、process_state 已写入、
    /// environments.status 已被设为 "running"。
    ///
    /// 抛出:
    /// - ArgumentException:env 缺关键字段(VenvPath / Port 等)
    /// - InvalidOperationException:env 已运行、main.py 找不到
    /// - TimeoutException:配置的超时时间内未就绪(端口未 listen 且未见就绪日志行,进程会被 kill)
    /// - ServiceLaunchException:Process.Start 失败 / 返回 null / 进程提前退出
    /// </summary>
    public Task StartEnvAsync(Environment env, CancellationToken ct = default)
        => StartEnvAsync(env, stageProgress: null, logProgress: null, ct);

    /// <summary>
    /// 重载:带 stage + log progress 报告。
    /// stageProgress 在 3 个里程碑被调用(激活本地环境 / 在环境中启用 / 完成),
    /// logProgress 在每行 stdout/stderr 被调用。
    /// </summary>
    public async Task StartEnvAsync(
        Environment env,
        IProgress<string>? stageProgress,
        IProgress<string>? logProgress,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLauncher));

        lock (_runningLock)
        {
            if (_running.ContainsKey(env.Id))
            {
                throw new InvalidOperationException(
                    $"env '{env.Name}' 已在运行中");
            }
        }

        _logger?.Info("env-start", $"env='{env.Name}' 开始启动 port={env.Port}");
        // v0.6.12:per-env 生命周期事件。ComfyUI stdout 只有进程起来了才有输出,
        // 「开始启动」之前这一段用户看不到 — 这里补一行。
        _logger?.WriteOperation(env.Name, $"[env-start] spawning comfui port={env.Port}");

        try
        {
            stageProgress?.Report("stage:激活本地环境");
            var settings = new SettingsRepository(new LocalDataPaths(_projectRoot)).Load();
            var (pythonExe, (entryFile, entryArgs)) = BuildStartCommand(env, settings, _projectRoot);

            var port = env.Port
                ?? throw new ArgumentException(
                    $"env '{env.Name}' 未配置 Port", nameof(env));

            if (IsPortInUse("127.0.0.1", port))
            {
                throw new ServiceLaunchException(
                    $"端口 {port} 已被占用,无法启动 env '{env.Name}'");
            }

            // v0.6.7.3 + v0.6.11+ T2:启动前检查并重建 Models junction(改 DefaultModelsDirectory 后自动生效)。
            // 失败仅 INFO 日志,不阻塞启动 —— ComfyUI 跑得起来,只是 models 共享不生效。
            // v1.0.0 T5:comfyuiRoot 改从 BuildStartCommand 的 entryFile 派生(<envRoot>/<entryScript>
            // → Path.GetDirectoryName = <envRoot>),而不是老 ResolveMainPy 返回的 <envRoot>/ComfyUI/main.py。
            try
            {
                var comfyUiRootForModels = Path.GetDirectoryName(entryFile)!;
                await EnsureModelsJunctionAsync(comfyUiRootForModels, ct);
            }
            catch (Exception ex)
            {
                _logger?.Info("env-start", $"Models junction 检查失败(继续启动): {ex.Message}");
            }

            // v0.6.7.2: 写 ComfyUI UI locale 到 <comfyui-root>/user/default/comfy.settings.json。
            // ComfyUI 不接 --lang CLI 参数 —— 只能改这个 json。失败不阻塞启动(用户能看到默认英文)。
            if (!string.IsNullOrWhiteSpace(_comfyUiLocale))
            {
                try
                {
                    var comfyUiRoot = Path.GetDirectoryName(entryFile)!;
                    new ComfySettingsWriter().WriteLocale(comfyUiRoot, _comfyUiLocale);
                    _logger?.Info("env-start", $"写入 ComfyUI locale={_comfyUiLocale} → {comfyUiRoot}/user/default/comfy.settings.json");
                }
                catch (Exception ex)
                {
                    _logger?.Info("env-start", $"写 ComfyUI locale 失败(继续启动,locale 不阻塞): {ex.Message}");
                }
            }

            var logPath = LogFilePath(env.Name, env.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = Path.GetDirectoryName(entryFile)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(entryFile);
            // entryArgs 是一段命令行参数(包含 {port} 已替换的 --port / --listen / UserExtraArgs),
            // 用 ArgumentList.Add 拆分 — 但里面 --preview-method auto 是两个独立 token,所以原样
            // 用空格分隔字符串追加到 ArgumentList。ProcessStartInfo.ArgumentList 会按空格拆。
            foreach (var token in entryArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(token);
            }
            psi.EnvironmentVariables["PYTHONPATH"] =
                $"{_projectRoot};{Path.Combine(_projectRoot, "src")}";

            // v1.0.0.x (2026-08-29):Forge env 启动时附加 env vars,目前只
            // SD_WEBUI_RESTARTING=1 禁用 webui.py 启动后自动打开浏览器 —
            // 用户原话:"他启动后自动打开网页,在这里我们不推荐"。
            // 机制:Forge webui.py 检查 `os.getenv('SD_WEBUI_RESTARTING') != '1'`
            // (A1111 upstream PR #11037 引入的官方机制,原本为 restart 场景,
            // Forge fork 把它扩展到所有启动场景),env var 是 "1" → 跳过整段
            // auto_launch_browser 逻辑 → 不会弹浏览器。用户要用浏览器时
            // 通过我们 app 的 OpenBrowser 按钮(走 BrowserLauncher Chrome fallback)
            // 手动打开,避免 webui.py 自动弹打扰。
            foreach (var kvp in ForgeExtraEnvironmentVariables(env))
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            Process? process = null;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new ServiceLaunchException(
                    $"无法启动 python 进程: {ex.Message}", ex);
            }

            if (process is null)
            {
                throw new ServiceLaunchException(
                    $"Process.Start 返回 null(env '{env.Name}')");
            }

            stageProgress?.Report("stage:在环境中启用");

            var entry = new ProcessEntry(process, env.Name, logPath, env.TemplateKind);
            lock (_runningLock)
            {
                _running[env.Id] = entry;
            }

            // 后台 reader + Exited 监听 —— 必须在 WaitForPort 之前挂上,
            // 否则端口 listen 后 stdout 早就写到 pipe 里会丢。
            AttachStdoutReader(entry, logProgress);
            AttachStderrReader(entry, logProgress);
            AttachExitedHandler(env.Id, env.Name, entry);

            try
            {
                // v0.6.7.1: 就绪 = 端口 listen 或 stdout 出现就绪行,任一先到即可。
                var timeout = TimeSpan.FromSeconds(_startupTimeoutSeconds);
                await WaitForReadyAsync(entry, "127.0.0.1", port, timeout, ct);
            }
            catch
            {
                // 没就绪:kill 进程、清空状态、清理 _running
                TryKillProcessTree(process);
                lock (_runningLock)
                {
                    _running.Remove(env.Id);
                }
                throw;
            }

            stageProgress?.Report("stage:完成");

            // v0.6.15.7:5s grace 让 ComfyUI 吐完 startup import errors,再扫描。
            // 不阻塞 StartEnvAsync 返回 — 用户看到「完成」即可操作 UI;
            // ScanMeta 写入异步在后台跑(几行 DB write,可接受)。
            // 两参都给才生效:detector 单飞无 repo 写不进去,repo 单飞无 detector 无事可做。
            if (_startupErrorDetector is not null && _nodeRepo is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        List<string> snapshot;
                        lock (entry.StartupLines)
                        {
                            snapshot = new List<string>(entry.StartupLines);
                        }
                        var errors = _startupErrorDetector.Parse(snapshot);
                        if (errors.Count == 0) return;
                        foreach (var err in errors)
                        {
                            // 用 env_id 跟 id 组合查(同一节点在多 env 都有 ScanMeta 副本)。
                            // 这里 Get(err.PackageName) 是按 id 查 — node id 在不同 env 共享同一 id
                            // (Source="env" 行 id = 节点目录名),不区分 env 写到任意一行足够让 UI 看到。
                            var node = _nodeRepo.Get(err.PackageName);
                            // v0.6.15.7 T9:package name ≠ node id 时 fallback(import error
                            // 报的是 package name,不是 dir name);若无 env scope(legacy 路径)则跳过。
                            if (node is null && env is not null)
                            {
                                node = _nodeRepo.GetByPackageName(env.Id, err.PackageName);
                            }
                            if (node is null)
                            {
                                _logger?.Info("node-startup-fail-skip",
                                    $"package '{err.PackageName}' 不在 env '{env?.Id ?? "?"}' 的节点表里,跳过");
                                continue;
                            }
                            node.ScanMeta ??= new Dictionary<string, string>();
                            node.ScanMeta["load_error"] = err.ErrorMessage;
                            try { _nodeRepo.Upsert(node); } catch { }
                        }
                        _logger?.Info("node-startup-fail",
                            $"env='{env.Name}' 检测到 {errors.Count} 个加载失败节点:{string.Join(", ", errors.Select(e => e.PackageName))}");
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger?.Info("node-startup-fail", $"扫描 startup 失败(忽略): {ex.Message}");
                    }
                });
            }

            // 成功路径:写 process_state + environments
            var now = DateTime.UtcNow;
            _processStateRepo.Upsert(new ProcessState
            {
                EnvId = env.Id,
                Pid = process.Id,
                Port = port,
                StartedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            });

            // 更新 env row(用最新状态,避免覆盖其它字段)
            var fresh = _envRepo.Get(env.Id) ?? env;
            fresh.Status = "running";
            fresh.Pid = process.Id;
            try
            {
                _envRepo.Upsert(fresh);
            }
            catch
            {
                // env row 写失败不致命 —— 进程已启动,后续 reload 也能查到 process_state
            }
            _logger?.Info("env-start", $"env='{env.Name}' 启动成功 pid={process.Id} port={port}");
        }
        catch (Exception ex)
        {
            // v1.0.0.x #711 followup:catch 块原 reuse stage 0 label "stage:激活本地环境"
            // 导致 StartEnvAsync_WithStageProgress_ReportsAllStages test 看到 2x 同 label
            // (1x 来自 line 169 try block + 1x 来自这里),失败路径没有专属 label。
            // 改用 "stage:失败" 区分 happy-path stage 0/1/2。
            stageProgress?.Report("stage:失败");
            logProgress?.Report($"[error] {ex.Message}");
            _logger?.Error("env-start", $"env='{env.Name}' 启动失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 停止一个 env。先 CloseMainWindow 优雅退出,等待 timeoutSeconds,
    /// 超时则 kill 整棵进程树。
    ///
    /// 即使 env 在 _running 中找不到(可能进程已意外退出),也会清掉
    /// process_state 行。
    /// </summary>
    public async Task StopEnvAsync(Environment env, int timeoutSeconds = 3,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLauncher));

        _logger?.Info("env-stop", $"env='{env.Name}' 停止请求 timeout={timeoutSeconds}s");

        ProcessEntry? entry;
        lock (_runningLock)
        {
            _running.TryGetValue(env.Id, out entry);
            _running.Remove(env.Id);
        }

        if (entry is not null)
        {
            var process = entry.Process;
            try
            {
                if (!process.HasExited)
                {
                    try { process.CloseMainWindow(); } catch { }
                    using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    shutdownCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        await process.WaitForExitAsync(shutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 超时,fall through to kill
                    }
                }
            }
            catch { }

            if (!process.HasExited)
            {
                TryKillProcessTree(process);
            }
            try { process.Dispose(); } catch { }
        }

        // 清理状态:process_state + env row
        try { _processStateRepo.Delete(env.Id); } catch { }
        try
        {
            var fresh = _envRepo.Get(env.Id) ?? env;
            fresh.Status = "stopped";
            fresh.Pid = null;
            _envRepo.Upsert(fresh);
        }
        catch { }

        _logger?.Info("env-stop", $"env='{env.Name}' 已停止");
        // v0.6.12:per-env 生命周期事件。stdout/stderr reader 会在 exit 时写一行,
        // 但那是 reader 线程异步写入;用户主动 Stop 时 UI 反馈先到这里 — 写一行立刻看到。
        _logger?.WriteOperation(env.Name, "[env-stop] stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<ProcessEntry> toKill;
        lock (_runningLock)
        {
            toKill = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var entry in toKill)
        {
            try { TryKillProcessTree(entry.Process); } catch { }
            try { entry.Process.Dispose(); } catch { }
        }
    }

    // -------- internals --------

    /// <summary>
    /// v0.6.7.3: 启动前检查 <paramref name="comfyuiRoot"/>/models 是否指向
    /// _modelsDirectory,不一致则删重建。失败仅 INFO 日志,不阻塞启动。
    ///
    /// 触发重建的场景:
    /// - models 目录不存在(独立布局 / 首次共享)
    /// - models 是普通目录而不是 junction(早于 v0.6.7.3 的 env 被设成共享时
    ///   T3 step 5.5 会建 junction;若此前已存在普通目录,需要替换)
    /// - junction 的 target 不等于 _modelsDirectory(用户在 Settings 改了)
    /// </summary>
    internal async Task EnsureModelsJunctionAsync(
        string comfyuiRoot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_modelsDirectory)) return;

        var sharedFull = Path.GetFullPath(_modelsDirectory);
        var modelsLink = Path.Combine(comfyuiRoot, "models");

        bool needsRelink;
        if (!Directory.Exists(modelsLink))
        {
            needsRelink = true;
        }
        else
        {
            string? existingTarget = null;
            try { existingTarget = await _linker.GetTargetAsync(modelsLink, ct); }
            catch { existingTarget = null; }

            needsRelink = existingTarget is null
                || !string.Equals(
                    Path.GetFullPath(existingTarget),
                    sharedFull,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (!needsRelink) return;

        if (Directory.Exists(modelsLink))
        {
            Directory.Delete(modelsLink, recursive: true);
        }
        await _linker.CreateAsync(modelsLink, sharedFull, ct);
        _logger?.Info("env-start", $"重新链接 Models: {modelsLink} → {sharedFull}");
    }

    private void AttachStdoutReader(ProcessEntry entry, IProgress<string>? logProgress = null)
    {
        var process = entry.Process;
        var envName = entry.EnvName;
        var pid = process.Id;
        _ = Task.Run(async () =>
        {
            // v0.6.17.3:rollover helper — 每次写之前重算 logPath(env 跨午夜后自动切到
            // 今天的子目录文件),旧 writer 关闭,新 writer 接上。修用户报告的
            // "上午开 LogViewer 窗口空"bug 的主因:旧实现启动时捕获 logPath 一次,
            // 跨午夜后 env 仍写昨天的文件,LogViewer 今天读到空文件。
            var rollover = new EnvLogRolloverWriter(envName, LogFilePath, _logsDir);
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    logProgress?.Report(line);
                    if (IsReadyLine(line)) entry.ReadySignal.TrySetResult();
                    // v0.6.15.7: capture for NodeStartupErrorDetector scan (5s grace 后读)。
                    // 后台线程 reader + 主线程 grace snapshot — 必须 lock,List 非线程安全。
                    lock (entry.StartupLines) entry.StartupLines.Add(line);
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    await rollover.WriteLineAsync($"[{ts}] [pid {pid}] OUT: {line}");
                }
            }
            catch
            {
                // 进程退出 / reader 取消,忽略
            }
            finally
            {
                rollover.Dispose();
            }
        });
    }

    private void AttachStderrReader(ProcessEntry entry, IProgress<string>? logProgress = null)
    {
        var process = entry.Process;
        var envName = entry.EnvName;
        var pid = process.Id;
        _ = Task.Run(async () =>
        {
            // v0.6.17.3:rollover helper — 同 stdout reader 注释。
            var rollover = new EnvLogRolloverWriter(envName, LogFilePath, _logsDir);
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    logProgress?.Report(line);
                    if (IsReadyLine(line)) entry.ReadySignal.TrySetResult();
                    // v0.6.15.7: capture for NodeStartupErrorDetector scan (5s grace 后读)。
                    // 后台线程 reader + 主线程 grace snapshot — 必须 lock,List 非线程安全。
                    lock (entry.StartupLines) entry.StartupLines.Add(line);
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    await rollover.WriteLineAsync($"[{ts}] [pid {pid}] ERR: {line}");
                }
            }
            catch
            {
                // 进程退出 / reader 取消,忽略
            }
            finally
            {
                rollover.Dispose();
            }
        });
    }

    private void AttachExitedHandler(string envId, string envName, ProcessEntry entry)
    {
        var process = entry.Process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // 意外退出 / Stop 调用之后的退出都会触发。
            // 清掉 _running + process_state + env row,append exit code 到 log。
            lock (_runningLock)
            {
                // StopEnvAsync 会先移除 _running 再等待退出;若已不在表里,
                // 说明 Stop 正在接管清理,避免 DB double-write / clobber 并发重启。
                if (!_running.ContainsKey(envId)) return;
                _running.Remove(envId);
            }
            try
            {
                _processStateRepo.Delete(envId);
            }
            catch { }
            try
            {
                var fresh = _envRepo.Get(envId);
                if (fresh is not null)
                {
                    fresh.Status = "stopped";
                    fresh.Pid = null;
                    _envRepo.Upsert(fresh);
                }
            }
            catch { }

            // v0.6.17.3:exit 写也走 rollover helper,跨午夜 exit 写到当天文件(而不是启动期旧文件)。
            try
            {
                using var rollover = new EnvLogRolloverWriter(envName, LogFilePath, _logsDir);
                int? exitCode = null;
                try { exitCode = process.ExitCode; } catch { }
                rollover.WriteLine(
                    $"[pid {process.Id}] EXIT: env '{envName}' exit code {exitCode?.ToString() ?? "?"}");
            }
            catch { }
            // v0.6.12:per-env 操作日志。意外退出 / 自然退出都在这里统一记一行,跟 StopEnvAsync 的 [env-stop] stopped 区分开。
            int? code = null;
            try { code = process.ExitCode; } catch { }
            _logger?.WriteOperation(envName,
                $"[env-stop] pid={process.Id} exit_code={code?.ToString() ?? "?"}");
        };
    }

    private static async Task WaitForPortAsync(string host, int port,
        TimeSpan timeout, CancellationToken ct)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);
        while (!deadlineCts.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, deadlineCts.Token);
                return; // 连上了,端口已 listen
            }
            catch (OperationCanceledException)
            {
                // caller 取消 或 deadline 到期
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                throw new TimeoutException(
                    $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
            }
            catch
            {
                // connection refused / 端口未起,重试
            }

            try
            {
                await Task.Delay(500, deadlineCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                throw new TimeoutException(
                    $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
            }
        }
        throw new TimeoutException(
            $"端口 {port} 在 {timeout.TotalSeconds:0}s 内未 listen");
    }

    /// <summary>
    /// v0.6.7.1: 就绪 = 端口 listen 或 stdout 出现就绪行,任一先到即可。
    /// 之前只等端口且硬编码 30s,ComfyUI 首次启动(编译 kernel / 加载模型)几分钟很常见。
    /// </summary>
    private static async Task WaitForReadyAsync(
        ProcessEntry entry, string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        // 500ms tick:每次检查 ①端口是否 listen ②进程是否已退出 ③readyTask 是否就绪。
        // 任一先到即返回。把检查散在 500ms tick 里,而不是 Task.WhenAny(portTask, delay),
        // 是为了让进程提前退出能即时报错 —— Task.Delay(timeout) 会干等到超时。
        var deadline = DateTime.UtcNow + timeout;
        var portTask = WaitForPortAsyncQuietAsync(host, port, deadlineCts.Token);
        var readyTask = entry.ReadySignal.Task;

        // 就绪行先到时 portTask 仍在轮询;退出前必须 Cancel,否则它会拿着已 Dispose 的
        // token 调 Task.Delay 抛 ObjectDisposedException 变成无人观察的 faulted task。
        try
        {
            while (true)
            {
                if (readyTask.IsCompletedSuccessfully)
                {
                    return;  // 就绪行先到
                }

                if (portTask.IsCompletedSuccessfully)
                {
                    // 端口 task 完成 → 短暂确认(避免 IsCompleted 竞态)
                    if (await portTask.ConfigureAwait(false))
                    {
                        return;
                    }
                    // 端口 task 因异常/取消完成 → 继续等 timeout 到期
                }

                // 进程提前退出 + 端口未 listen + 没有就绪行 → 立刻报错。
                if (entry.Process.HasExited)
                {
                    int? code = null;
                    try { code = entry.Process.ExitCode; } catch { }
                    // v1.0.0.x (2026-08-29):错误信息按 env.TemplateKind 派生显示 —
                    // 之前硬编码 "ComfyUI 进程",对 Forge/OpenVoice/HunyuanVideo 等模板
                    // 用户报错看日志时容易混淆(forge env crash 也说 ComfyUI 进程)。
                    // 空 / ComfyUI / 未识别 TemplateKind → fallback "ComfyUI 进程"
                    // (向后兼容 — 老 env SQLite template_kind 列可能 null)。
                    throw new ServiceLaunchException(
                        $"{entry.ProcessDisplayName} 进程提前退出(exit code {code}),查看日志: {entry.LogFilePath}");
                }

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"ComfyUI 在 {timeout.TotalSeconds:0}s 内未就绪(端口 {port} 未 listen 且未见就绪日志)。可在设置中调大「ComfyUI 启动就绪超时」。");
                }

                try
                {
                    await Task.Delay(500, deadlineCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // deadline 到期或 caller 取消 — 下一轮检查会抛对应异常
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    throw new TimeoutException(
                        $"ComfyUI 在 {timeout.TotalSeconds:0}s 内未就绪(端口 {port} 未 listen 且未见就绪日志)。可在设置中调大「ComfyUI 启动就绪超时」。");
                }
            }
        }
        finally
        {
            deadlineCts.Cancel();
            _ = portTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 包 <see cref="WaitForPortAsync"/>:把 TimeoutException 吃掉,返回 bool
    /// 让 <see cref="WaitForReadyAsync"/> 统一决定报错文案。
    /// </summary>
    private static async Task<bool> WaitForPortAsyncQuietAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            // 不传 timeout:用 ct 的 deadline 控制;WaitForPortAsync 内的 CancelAfter 由外层 cts 管理。
            // 这里直接 await 到 connect 成功或 ct 取消。
            await WaitForPortAsync(host, port, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public static bool IsPortInUse(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromMilliseconds(500))
                && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v1.0.0 multi-template T5: 根据 <paramref name="env"/> 的 <see cref="Environment.TemplateKind"/>
    /// 拼接启动命令的 (exe, (entryFile, entryArgs))。
    ///
    /// 优先级: <see cref="Environment.TemplateConfigSnapshot"/> (env 创建时的快照)
    /// → <see cref="Settings.Templates"/>[env.TemplateKind] (向后兼容,老 env 无快照列时用当前 template)。
    ///
    /// 路径约定: <c>&lt;projectRoot&gt;/envs/&lt;envName&gt;/venv/Scripts/python.exe</c>
    /// + <c>&lt;projectRoot&gt;/envs/&lt;envName&gt;/&lt;EntryScript&gt;</c>。
    /// {port} 占位符用 <see cref="Environment.Port"/> 替换;空则回退 "8000"。
    /// </summary>
    /// <returns>
    /// (exe, (entryFile, entryArgsString))。entryArgsString 是空格分隔的命令行参数
    /// 串,调用方按需 Split(' ') 喂给 <see cref="ProcessStartInfo.ArgumentList"/>。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// env 既无 TemplateConfigSnapshot 也找不到 Settings.Templates[TemplateKind] ——
    /// 即 template 已被用户在 Settings 中删除,且 env row 没快照。
    /// </exception>
    public static (string exe, (string File, string ArgsString) args) BuildStartCommand(
        Environment env, Settings settings, string projectRoot)
    {
        var snapshot = env.TemplateConfigSnapshot
            ?? settings.Templates.GetValueOrDefault(env.TemplateKind)
            ?? throw new InvalidOperationException(
                $"模板 '{env.TemplateKind}' 不存在,可能在 Settings 中已被删除");

        // v1.0.0.x: envRoot 直接用 env.RootPath(env-create 时 EnvCreatorService 存的
        // 绝对路径 = <EnvsDir>/<name>,dev/release 一致)。之前
        // `Path.Combine(projectRoot, "envs", env.Name)` 在 dev build 里 projectRoot
        // = bin/Debug/net8.0-windows 拼出 bin 内的假 envs\faceswap 找不到 main.py
        // → 启动报「入口脚本不存在」(用户 2026-08-26 反馈)。
        // settings.EnvsDir 可能绝对/相对但解析路径仍依赖 projectRoot 锚点,
        // 用 env.RootPath 一行简化,且是 EnvDirectoryScanner 写入的真实位置。
        var envRoot = !string.IsNullOrWhiteSpace(env.RootPath)
            ? env.RootPath
            : Path.Combine(projectRoot, string.IsNullOrEmpty(settings.EnvsDir) ? "envs" : settings.EnvsDir, env.Name);
        // v0.6.7.1: env.PythonExecutable 优先 — 允许用户/测试覆写 python 路径(老行为)。
        // 空/不存在 → 回退到标准 venv layout <envRoot>/venv/Scripts/python.exe。
        var venvPython = !string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable)
            ? env.PythonExecutable
            : Path.Combine(envRoot, "venv", "Scripts", "python.exe");
        var entryScript = Path.Combine(envRoot, snapshot.EntryScript);
        // Spec §9: 入口脚本不存在时 throw 清晰指示,而不是 spawn python 然后看到
        // "ModuleNotFoundError: No module named 'main.py'" 之类的晦涩错。
        if (!File.Exists(entryScript))
            throw new InvalidOperationException(
                $"入口脚本不存在: {entryScript}");
        var port = env.Port?.ToString() ?? "8000";
        var entryArgs = snapshot.EntryArgs.Replace("{port}", port);
        if (!string.IsNullOrWhiteSpace(snapshot.UserExtraArgs))
            entryArgs += " " + snapshot.UserExtraArgs;

        // v1.0.0.x (2026-08-29):Forge env 显式拼 --ckpt-dir / --vae-dir / --lora-dir /
        // --controlnet-dir 等指向 Settings.DefaultModelsDirectory,让 webui.py 启动时
        // 直接读用户共享本地模型库,避免 Forge 默认 a1111_home/models/ 跟 junction
        // 共享盘的实际子目录命名约定不同导致启动报 "You do not have any model!"。
        //
        // 用户共享盘布局为 ComfyUI 风格(<DefaultModelsDirectory>/checkpoints/ + vae/ +
        // loras/ + controlnet/),而非 Forge/A1111 默认的 Stable-diffusion/ + VAE/ +
        // lora/ + ControlNet/(2026-08-29 用户纠正:"其实--ckpt-dir 挂的是这个目录,
        // 不要挂 stable diffusion")。所以用 ComfyUI 子目录名,让 webui.py 通过 --ckpt-dir
        // 直接定位到有 .safetensors 的实际目录(目录名对 webui.py 是透明的,只看文件)。
        //
        // 条件:env.TemplateKind == "Forge" 且 settings.DefaultModelsDirectory 非空
        // (env-create 时这个字段驱动 junction 共享;这里再叠一层,即便 junction 子目录
        // 命名约定不同,Forge 也能通过 --ckpt-dir 直接定位到 DefaultModelsDirectory 的
        // ComfyUI 风格子目录)。
        if (env.TemplateKind == "Forge" &&
            !string.IsNullOrWhiteSpace(settings.DefaultModelsDirectory))
        {
            var modelsRoot = Path.GetFullPath(settings.DefaultModelsDirectory);
            // Path.Combine 替字符串拼接,处理 modelsRoot 末尾斜杠 + 跨平台分隔符;
            // 路径里含空格由 ProcessStartInfo.ArgumentList 自动 quote(StartEnvAsync
            // line 229 按空格 Split 喂进去)。无需手写 QuoteArg。
            entryArgs +=
                $" --ckpt-dir {Path.Combine(modelsRoot, "checkpoints")}" +
                $" --vae-dir {Path.Combine(modelsRoot, "vae")}" +
                $" --lora-dir {Path.Combine(modelsRoot, "loras")}" +
                $" --controlnet-dir {Path.Combine(modelsRoot, "controlnet")}";
        }

        // v1.0.0.x (2026-08-29):Forge 启动禁用 webui.py 自动开浏览器 —
        // 用户原话:"他启动后自动打开网页,在这里我们不推荐"。
        //
        // 双管齐下(defense-in-depth):
        // (a) --no-autolaunch CLI flag:用户参考 webui-user.bat 文档推荐的方式
        //     (2026-08-29 用户原话"参考这个")。A1111/Forge 用自定义 bool_py2 argparse
        //     action 支持 --no-foo 否定形(默认 dest 前缀 no_ 检测),所以 --no-autolaunch
        //     实际等价于 --autolaunch=False。如果 Forge 升级去掉这个自定义 action,
        //     该 flag 会报 "unrecognized arguments" — 配合下方 (b) env var 兜底。
        // (b) SD_WEBUI_RESTARTING=1 env var:A1111 upstream PR #11037 引入的官方
        //     机制,Forge webui.py 用它跳过整段 auto_launch_browser 逻辑
        //     (`if os.getenv('SD_WEBUI_RESTARTING') != '1':`)。
        //     这层独立于 CLI flag,即便 (a) 失败也兜住不弹浏览器。
        if (env.TemplateKind == "Forge")
        {
            entryArgs += " --no-autolaunch";
        }

        return (venvPython, (entryScript, entryArgs));
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):Forge env 启动时附加 env vars,目前只 <c>SD_WEBUI_RESTARTING=1</c>
    /// 禁用 webui.py 启动后自动打开浏览器 — 用户原话:"他启动后自动打开网页,在这里我们不推荐"。
    ///
    /// 机制:Forge webui.py 检查 <c>os.getenv('SD_WEBUI_RESTARTING') != '1'</c>
    /// (A1111 upstream PR #11037 引入的官方机制,原本为 restart 场景设计,Forge fork 把它
    /// 扩展到所有启动场景)。env var 是 "1" → 跳过整段 <c>auto_launch_browser</c> 逻辑 →
    /// 默认 <c>False</c> → 不弹浏览器。用户要用浏览器时,通过我们 app 的 OpenBrowser 按钮
    /// (走 BrowserLauncher Chrome fallback)手动打开,避免 webui.py 自动弹打扰。
    ///
    /// 边界条件:
    /// - <c>env.TemplateKind != "Forge"</c> → 不 set(ComfyUI / OpenVoice / HunyuanVideo 等
    ///   不走 webui.py 的 auto-launch 路径,set 了也无害但浪费)
    /// - <c>env.TemplateKind == null / ""</c> → 不 set(老 env SQLite template_kind 列可能 null,
    ///   兜底走 "ComfyUI" 默认行为)
    /// </summary>
    /// <remarks>
    /// 单独抽 helper 让单元测试可调 — StartEnvAsync 内部 set,集成测试覆盖整个流程成本太高。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ForgeExtraEnvironmentVariables(Environment env)
    {
        var extras = new Dictionary<string, string>();
        if (string.Equals(env.TemplateKind, "Forge", StringComparison.Ordinal))
        {
            extras["SD_WEBUI_RESTARTING"] = "1";
        }
        return extras;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private sealed class ProcessEntry
    {
        public Process Process { get; }
        public string LogFilePath { get; }
        /// <summary>
        /// v0.6.17.3:env 名字给 EnvLogRolloverWriter — 跨午夜时重新算路径用 envName + DateTime.Now,
        /// LogFilePath(启动期固定)只保留给 LogViewer 默认路径。
        /// </summary>
        public string EnvName { get; }
        /// <summary>
        /// v1.0.0.x (2026-08-29):env.TemplateKind copy — 错误信息按模板派生(Forge / OpenVoice
        /// / HunyuanVideo / etc. 各自的进程名)而不硬编码 "ComfyUI"。空 / "ComfyUI" / 未识别
        /// kind → <see cref="ProcessDisplayName"/> fallback "ComfyUI" 兼容老 env。
        /// </summary>
        public string TemplateKind { get; }
        /// <summary>
        /// v1.0.0.x (2026-08-29):错误信息用的进程显示名 — <c>{TemplateKind}</c> 不空且非
        /// "ComfyUI" → "{Kind}"(如 "Forge" / "OpenVoice" / "HunyuanVideo");否则 fallback
        /// "ComfyUI" 兼容老 env(模板为空 / SQLite template_kind 列 null)。
        /// </summary>
        public string ProcessDisplayName { get; }
        /// <summary>
        /// v0.6.15.7: 启动期 stdout/stderr 行缓存,5s grace 后被 NodeStartupErrorDetector 扫描。
        /// AttachStdoutReader / AttachStderrReader 在 <c>lock (StartupLines)</c> 下 Add,
        /// grace 后 new List&lt;string&gt;(StartupLines) 拿快照。
        /// </summary>
        public List<string> StartupLines { get; } = new();
        /// <summary>
        /// v0.6.7.1: stdout/stderr reader 见到就绪行时 TrySetResult。
        /// </summary>
        public TaskCompletionSource ReadySignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessEntry(Process process, string envName, string logFilePath, string templateKind)
        {
            Process = process;
            EnvName = envName;
            LogFilePath = logFilePath;
            TemplateKind = templateKind ?? "";
            // ProcessDisplayName 派生规则:
            // - 空 / "ComfyUI" → "ComfyUI" (向后兼容 — 老 env 无 template_kind 或 kind 是 ComfyUI)
            // - 其他 kind(Forge / OpenVoice / HunyuanVideo / etc.) → 原样用作显示名
            ProcessDisplayName = string.IsNullOrWhiteSpace(templateKind) || string.Equals(templateKind, "ComfyUI", StringComparison.Ordinal)
                ? "ComfyUI"
                : templateKind;
        }
    }

    /// <summary>
    /// v0.6.7.1: 判断一行 ComfyUI stdout/stderr 是否表示「服务已就绪」。
    /// ComfyUI 各版本就绪行文案不一,这里匹配几个稳定出现的标志串(大小写不敏感)。
    /// </summary>
    public static bool IsReadyLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains("To see the GUI go to", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Starting server", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Application startup complete", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Uvicorn running on", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// v0.6.17.3: per-env 日志 rollover writer —— 每次写之前重算路径,env 跨午夜自动
/// 切到当天的 <c>{logsDir}/logs/env/{envName}/{date}.log</c> 文件。修用户报告的
/// "上午开 LogViewer 窗口空"bug:旧实现启动时捕获路径一次,跨午夜后 env 仍写
/// 昨天的文件,LogViewer 今天读到空文件。
///
/// 用法:AttachStdoutReader / AttachStderrReader / AttachExitedHandler 在 reader
/// 循环里 <c>await rollover.WriteLineAsync(...)</c>,Dispose 时关闭当前 writer。
///
/// 设计选择:
/// - 每次 WriteLine 重算 path 而不是定时器检查:开销极小(< 1μs 字符串拼接)
///   且保证第一行新日期的写出就进新文件,无延迟。
/// - 同一天写复用同一个 writer:减少 IO(每次 open + close 至少 ~10ms)。
/// - 日切原子性:旧 writer Dispose 完毕才 open 新 writer,中间窗口无 IO,LogViewer
///   reader 不会看到 "partial" 行。
/// - 异常隔离:FileStream.Open 抛 → 返回 false 让调用方吃掉,reader 继续跑(不会
///   让一个 IO 抖动拖死整个 stdout reader)。
/// </summary>
internal sealed class EnvLogRolloverWriter : IDisposable
{
    private readonly string _envName;
    private readonly Func<string, string, DateTime?, string> _pathResolver;
    private readonly string _logsDir;
    private readonly object _gate = new();
    private DateTime _currentDay;
    private StreamWriter? _writer;
    private string _currentPath = "";
    private bool _disposed;

    public EnvLogRolloverWriter(
        string envName,
        Func<string, string, DateTime?, string> pathResolver,
        string logsDir)
    {
        _envName = envName;
        _pathResolver = pathResolver;
        _logsDir = logsDir;
    }

    public void WriteLine(string text)
    {
        if (_disposed) return;
        if (TryRotate()) _writer!.WriteLine(text);
    }

    public async Task WriteLineAsync(string text)
    {
        if (_disposed) return;
        if (TryRotate()) await _writer!.WriteLineAsync(text);
    }

    /// <summary>
    /// 检查日期是否变化,变化则关闭旧 writer + 打开新 writer。返回 writer 是否可用。
    /// </summary>
    private bool TryRotate()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var newPath = _pathResolver(_envName, "", now);
            if (_writer is null || newPath != _currentPath)
            {
                _writer?.Dispose();
                _currentPath = newPath;
                var dir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                try
                {
                    _writer = new StreamWriter(new FileStream(
                        newPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        AutoFlush = true,
                    };
                    _currentDay = now.Date;
                }
                catch
                {
                    _writer = null;
                    return false;
                }
            }
            return _writer is not null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
