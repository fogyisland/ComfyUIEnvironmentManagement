using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x A1111 / Forge pre-flight installer:镜像 AUTOMATIC1111
/// <c>modules/launch_utils.py:prepare_environment()</c> 在「装依赖」阶段提前跑的
/// 4 件事,让 <c>python launch.py</c> 启动时 13 步全部 idempotent 跳过(尤其
/// step 9 git clone 5 个 repos — 这是 paths.py:34 <c>assert sd_path is not None</c>
/// fail 的根因:直接 python webui.py 跳过 launch.py → repositories/stable-diffusion-stability-ai
/// 目录不存在 → ddpm.py 找不到)。
///
/// 4 件事执行顺序(镜像 launch_utils.py:393-415):
///   1. <c>pip install openai/CLIP/archive/{hash}.zip</c>
///   2. <c>pip install mlfoundations/open_clip/archive/{hash}.zip</c>
///   3. <c>pip install -r &lt;envRoot&gt;/requirements_versions.txt</c>(过滤 torch 行,
///      与 ComfyUI RequirementsInstaller 一致 — BED 已装 torch,装依赖不覆盖 profile 版本)
///   4. <c>git clone</c> 5 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>(已存在 skip)
///
/// 触发入口:RequirementsInstaller.InstallAsync 头部按 <c>env.TemplateKind</c> dispatch
/// (A1111 / Forge → 走这里,ComfyUI / SwarmUI 走老 requirements.txt 路径)。
/// 成功 marker:<see cref="A1111PreFlightConstants.MarkerFileName"/>。
/// </summary>
public class A1111PreFlightInstaller
{
    private readonly AppLogger? _logger;
    private readonly HttpProxyConfig? _proxy;
    private readonly string _gitExe;

    public A1111PreFlightInstaller(
        AppLogger? logger = null,
        HttpProxyConfig? proxy = null,
        string? gitExe = null)
    {
        _logger = logger;
        _proxy = proxy;
        // null/空 fallback 到 "git"(PATH 查找 — App.xaml.cs:GitExe 在 settings 注入,
        // 测试 ctor 不传也走得通)。
        _gitExe = string.IsNullOrWhiteSpace(gitExe) ? "git" : gitExe;
    }

    /// <summary>
    /// 检查 A1111 / Forge pre-flight 是否已完成(marker 文件存在)。
    /// RequirementsInstaller.IsInstalled 内部调用此处保持单一判定源。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, A1111PreFlightConstants.MarkerFileName));
    }

    /// <summary>
    /// 跑 4 件事 pre-flight。任何一步失败 → 返回 <see cref="RequirementsInstallResult"/>
    /// 描述失败原因;已成功的步骤不会回滚(launch.py 启动时 idempotent 跳过,
    /// 用户可重新点「装依赖」补跑剩余步骤)。
    /// </summary>
    public virtual async Task<RequirementsInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        _logger?.Info("a1111-preflight",
            $"env='{env.Name}' kind='{env.TemplateKind}' 开始 A1111 pre-flight (4 步)");
        logProgress?.Report($"[a1111-preflight] env='{env.Name}' 开始 pre-flight");

        var pythonExe = ResolveVenvPython(env);

        // 1. clip zip
        var clipResult = await InstallZipAsync(
            A1111PreFlightConstants.Zips[0], pythonExe, logProgress, ct);
        if (!IsPipOk(clipResult)) return FailFrom(clipResult, "clip");

        // 2. open_clip zip
        var ocResult = await InstallZipAsync(
            A1111PreFlightConstants.Zips[1], pythonExe, logProgress, ct);
        if (!IsPipOk(ocResult)) return FailFrom(ocResult, "open_clip");

        // 3. requirements_versions.txt — 只过滤裸 torch 行(launch.py 单独装
        //    torchvision / torchaudio / xformers 等,不在这个文件里;sdweb
        //    requirements_versions.txt 实际只有 1 行裸 torch)。不复用共享
        //    RequirementsFileInstaller.FilterTorchLines — 那个 regex 过滤 5 个
        //    标准 torch 系列(torch / torchvision / torchaudio / torchtext /
        //    torchdata),被 ComfyUI/Manager/LocalNodeBulkInstaller 共用,改它会
        //    影响其他 caller。这里用 inline 简化版,只匹配行首 "torch" 后跟
        //    空白 / 行尾 / 比较运算符(裸名 / 带版本),不匹配 torchvision /
        //    torchaudio / pytorch_lightning / torchdiffeq / torchsde /
        //    open-clip-torch 等(名字含 torch 子串但不是 torch 包)。
        var reqPath = Path.Combine(env.RootPath, "requirements_versions.txt");
        if (!File.Exists(reqPath))
        {
            // sdweb env 是用户从 ENVTemplate clone 出来的,理论上必有 requirements_versions.txt。
            // 缺失 → 报清晰错(launch.py 也会 fail,用户需要 re-clone template)。
            var reason = $"找不到 requirements_versions.txt(预期路径:{reqPath})";
            _logger?.Error("a1111-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[a1111-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }
        var filteredReqPath = Path.Combine(env.RootPath,
            RequirementsFileInstaller.FilteredRequirementsFileName);
        List<string> rawLines;
        try
        {
            rawLines = new List<string>(await File.ReadAllLinesAsync(reqPath, ct));
        }
        catch (Exception ex)
        {
            var reason = $"读取 requirements_versions.txt 失败:{ex.Message}";
            _logger?.Error("a1111-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[a1111-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }
        var filteredLines = FilterBareTorchLines(rawLines);
        try
        {
            await File.WriteAllLinesAsync(filteredReqPath, filteredLines, ct);
        }
        catch (Exception ex)
        {
            var reason = $"写过滤文件失败:{ex.Message}";
            _logger?.Error("a1111-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[a1111-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }
        // requirements_versions.txt 都是预编译 wheel,不需要 --no-build-isolation
        // (这是 InstallZipAsync 用的,因为 CLIP / open_clip 是源码 sdist 带 setup.py)。
        var reqResult = await RunPipAsync(
            pythonExe,
            new[] { "install", "-r", filteredReqPath, "--disable-pip-version-check" },
            line => logProgress?.Report(line),
            ct);
        // 成功失败都清理 filtered 文件(避免下次 install 看到 stale 文件)
        try { File.Delete(filteredReqPath); } catch { }
        if (!IsPipOk(reqResult))
            return FailFrom(reqResult, $"pip install -r requirements_versions.txt (filtered)");

        // 4. git clone 5 repos(每个独立 try/catch + IsInstalled skip,失败不阻断后续 repo;
        //    最后整体成功判断,只要全部存在或 clone 成功 → success)
        logProgress?.Report("[a1111-preflight] stage:git clone 5 repos");
        var cloneResults = await CloneAllReposAsync(env, logProgress, ct);
        var failedClone = cloneResults.FirstOrDefault(r => !r.Ok);
        if (failedClone is not null)
        {
            var reason = $"git clone '{failedClone.Spec.DisplayName}' 失败(exit={failedClone.Result?.ExitCode}):{failedClone.Result?.Stderr}";
            _logger?.Error("a1111-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[a1111-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }

        // 全部成功 → 写 marker
        var markerPath = Path.Combine(env.RootPath, A1111PreFlightConstants.MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception ex)
        {
            _logger?.Warn("a1111-preflight",
                $"env='{env.Name}' marker 写失败(ex={ex.Message});下次装依赖会被短路");
        }

        _logger?.Info("a1111-preflight", $"env='{env.Name}' pre-flight 完成(3 pip + 5 repos)");
        logProgress?.Report("[a1111-preflight] ✓ 完成(3 pip + 5 repos)");
        return new RequirementsInstallResult(
            Success: true, Cancelled: false, Reason: null, InstalledCount: 0);
    }

    private async Task<PipResult> InstallZipAsync(
        A1111PreFlightConstants.ZipPackage pkg,
        string pythonExe,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        logProgress?.Report($"[a1111-preflight] stage:{pkg.DisplayName}");
        // CLIP / open_clip 老 setup.py 引用 `from pkg_resources import ...`(setuptools 自带)。
        // pip 默认 isolated build 会建干净的 build env 不带 setuptools → pkg_resources 缺失 →
        // `Getting requirements to build wheel` 失败。`--no-build-isolation` 让 pip 复用
        // venv 里已装的 setuptools,带 pkg_resources。launch_utils.py 没显式传这 flag,
        // 但 launch.py 跑前 BED 阶段已经 `pip install --upgrade setuptools`,所以 launch.py
        // 跑 setup.py 间接有 pkg_resources — 我们 pre-flight 同样用 BED 后的 venv,加
        // `--no-build-isolation` 一致等价。
        logProgress?.Report($"[a1111-preflight] $ pip install {pkg.Url} --no-build-isolation");
        return await RunPipAsync(
            pythonExe,
            new[] { "install", pkg.Url, "--disable-pip-version-check", "--no-build-isolation" },
            line => logProgress?.Report(line),
            ct);
    }

    private async Task<List<RepoCloneOutcome>> CloneAllReposAsync(
        Environment env,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        var reposDir = Path.Combine(env.RootPath, A1111PreFlightConstants.RepositoriesDirName);
        try
        {
            Directory.CreateDirectory(reposDir);
        }
        catch (Exception ex)
        {
            _logger?.Error("a1111-preflight",
                $"env='{env.Name}' 创建 repositories 目录失败:{ex.Message}");
            return A1111PreFlightConstants.Repos
                .Select(spec => new RepoCloneOutcome(spec, null, ex.Message))
                .ToList();
        }

        // gitRunner per-call(每次重建 — proxy 是 live config,不能 stale)。
        var git = new GitRunner(_gitExe, _proxy);
        var outcomes = new List<RepoCloneOutcome>(A1111PreFlightConstants.Repos.Count);

        foreach (var spec in A1111PreFlightConstants.Repos)
        {
            ct.ThrowIfCancellationRequested();
            var targetDir = Path.Combine(reposDir, spec.DirName);
            // IsInstalled 检测:launch.py 自己的 git_clone 也检测 .git 存在就 skip。
            if (Directory.Exists(Path.Combine(targetDir, ".git")))
            {
                logProgress?.Report($"[a1111-preflight] ✓ {spec.DisplayName} 已存在,跳过");
                outcomes.Add(new RepoCloneOutcome(spec, null, null));
                continue;
            }

            logProgress?.Report($"[a1111-preflight] $ git clone {spec.Url} {spec.DirName}");
            GitResult? result = null;
            try
            {
                result = await git.RunAsync(
                    reposDir,
                    new[] { "clone", spec.Url, spec.DirName },
                    timeout: TimeSpan.FromMinutes(10),
                    ct: ct,
                    onStderrLine: logProgress);
            }
            catch (Exception ex)
            {
                _logger?.Warn("a1111-preflight",
                    $"env='{env.Name}' git clone '{spec.DisplayName}' 异常:{ex.Message}");
                outcomes.Add(new RepoCloneOutcome(spec, null, ex.Message));
                continue;
            }

            if (!result.Ok)
            {
                outcomes.Add(new RepoCloneOutcome(spec, result, null));
                continue;
            }

            // clone 成功 → checkout 到 pinned commit hash(launch_utils 用 git_clone(url, dir, name, commit_hash)
            // 第 4 参数就是 commit hash,功能等价于 clone + checkout 到该 commit)。
            var coResult = await git.RunAsync(
                targetDir,
                new[] { "checkout", spec.CommitHash },
                timeout: TimeSpan.FromMinutes(2),
                ct: ct);
            if (!coResult.Ok)
            {
                // checkout 失败不致命 — git clone 已成功,后续 launch.py 自己会处理;
                // 报 warn 但不阻断。
                _logger?.Warn("a1111-preflight",
                    $"env='{env.Name}' {spec.DisplayName} checkout {spec.CommitHash} 失败(exit={coResult.ExitCode}):{coResult.Stderr}");
                logProgress?.Report($"[a1111-preflight] warn:{spec.DisplayName} checkout 失败(继续)");
            }
            outcomes.Add(new RepoCloneOutcome(spec, result, null));
        }

        return outcomes;
    }

    private static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
            return env.PythonExecutable;
        var defaultPath = Path.Combine(env.RootPath, "venv", "Scripts", "python.exe");
        if (!File.Exists(defaultPath))
            throw new InvalidOperationException(
                $"venv python 不存在:{defaultPath}(BED 阶段已装 venv,异常说明 venv 被破坏)");
        return defaultPath;
    }

    /// <summary>
    /// Run pip with stderr/stdout 实时通过 <paramref name="onLine"/> 报告,返
    /// <see cref="PipResult"/>。镜像 RequirementsFileInstaller.RunPipAsync,但
    /// 提取为 instance method 方便测试 override。
    /// </summary>
    protected virtual async Task<PipResult> RunPipAsync(
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
        _proxy?.ApplyTo(psi);
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

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new PipResult(-1, WasCancelled: true);
        }

        await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

        return new PipResult(process.ExitCode, WasCancelled: false);
    }

    private RequirementsInstallResult FailFrom(PipResult p, string stage)
    {
        if (p.WasCancelled)
        {
            return new RequirementsInstallResult(
                Success: false, Cancelled: true, Reason: "用户取消", InstalledCount: 0);
        }
        var reason = $"pip {stage} 退出码 {p.ExitCode}";
        return new RequirementsInstallResult(
            Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
    }

    /// <summary>
    /// PipResult.Ok 等价物(launch_utils.py idempotent 检查语义:exit=0 且未取消)。
    /// </summary>
    private static bool IsPipOk(PipResult p) => p.ExitCode == 0 && !p.WasCancelled;

    /// <summary>
    /// Inline 简化版 torch 过滤:只匹配行首的裸 <c>torch</c>(后跟空白 / 行尾 /
    /// 比较运算符),不匹配 <c>torchvision</c> / <c>torchaudio</c> /
    /// <c>torchtext</c> / <c>torchdata</c>(launch.py 单独装,不在此文件),
    /// 也不匹配 <c>pytorch_lightning</c> / <c>torchdiffeq</c> / <c>torchsde</c> /
    /// <c>open-clip-torch</c>(名字含 torch 子串但不是 torch 包,应正常装)。
    ///
    /// 故意不复用 <see cref="RequirementsFileInstaller.FilterTorchLines"/> —
    /// 那个 regex 匹配 5 个标准 torch 系列,被 ComfyUI / Manager / LocalNode
    /// 装依赖共用,改它会扩散到其它 caller。这里只在 A1111 pre-flight 用,
    /// 只过滤 BED profile 锁版本的那 1 个包(裸 torch)。
    /// </summary>
    internal static bool IsBareTorchLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;
        var s = rawLine.TrimStart();
        // 行首(去 leading whitespace 后)以 "torch" 开头,后面必须是空白 / 行尾 / 比较运算符
        // —— 排除 "torchvision" / "torchaudio" / "pytorch_lightning" / "torchdiffeq" 等
        if (!s.StartsWith("torch", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length == 5) return true;                       // 行尾
        var next = s[5];
        return char.IsWhiteSpace(next)                        // "torch ..."
            || next == '=' || next == '<' || next == '>'      // "torch==2.1.2"
            || next == '!' || next == '~' || next == ';';     // "torch!=2.1" 等
    }

    internal static List<string> FilterBareTorchLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var raw in rawLines)
        {
            if (IsBareTorchLine(raw)) continue;
            result.Add(raw ?? "");
        }
        return result;
    }

    /// <summary>
    /// 单 repo clone 结果。<see cref="Ok"/> 表示:
    /// 已存在(skip) / clone 成功(可能 checkout 失败但已 warn) / 异常 captured(失败)。
    /// </summary>
    private sealed record RepoCloneOutcome(
        A1111PreFlightConstants.RepoSpec Spec,
        GitResult? Result,
        string? ErrorMessage)
    {
        public bool Ok => ErrorMessage is null && (Result is null || Result.Ok);
    }
}
