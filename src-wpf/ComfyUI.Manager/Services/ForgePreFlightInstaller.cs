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
/// v1.0.0.x Forge pre-flight installer:镜像 lllyasviel/stable-diffusion-webui-forge
/// <c>modules/launch_utils.py:prepare_environment()</c> 在「装依赖」阶段提前跑的
/// 4 件事,让 <c>python launch.py</c> 启动时 step 9 全部 idempotent 跳过
/// (git clone 3 个 repos — assets / huggingface_guess / BLIP;Stability-AI
/// 那 2 个 sd core 已经被 Forge 注释掉了,因为 Stability-AI/stablediffusion 仓库
/// 已从 github 移除)。
///
/// 5 件事执行顺序:
///   1. <c>pip install openai/CLIP/archive/{hash}.zip --no-build-isolation</c>
///   2. <c>pip install mlfoundations/open_clip/archive/{hash}.zip --no-build-isolation</c>
///   3a. <c>pip install -r &lt;envRoot&gt;/requirements_versions.txt</c>(过滤裸 torch 行 + pytorch_lightning,
///      无 <c>--no-deps</c> → pip 自动拉 transitive deps:gradio → gradio_client、
///      fastapi → starlette、pydantic → typing-extensions 等)
///   3b. <c>pip install pytorch_lightning==1.9.4 --no-deps</c>(要求 torch&lt;2.0 与 BED 锁的
///      torch 2.4.0+cu121 冲突,必须 --no-deps 防止 pip 自动降级 torch 丢失 CUDA wheel)
///   3c. <c>pip install &lt;pytorch_lightning 自己的 transitive deps&gt;</c>(从已装的
///      <c>.dist-info/METADATA</c> parse 出来;补 lightning-utilities / torchmetrics 等)
///   4.5. <c>pip install &lt;extensions-builtin 合并 deps&gt;</c>(扫所有 ext 自己的
///      <c>requirements.txt</c> + hardcode 列表如 <c>joblib</c>;补 soft-inpainting
///      这类没 <c>requirements.txt</c> 但顶层 import 失败导致 webui.py 启动 crash 的 ext)
///   4. <c>git clone</c> 3 个 repos 到 <c>&lt;envRoot&gt;/repositories/</c>(已存在 skip)
///
/// 触发入口:RequirementsInstaller.InstallAsync 头部按 <c>env.TemplateKind</c> dispatch
/// (Forge → 走这里,ComfyUI 走老 requirements.txt 路径;SwarmUI 已下线)。
/// 成功 marker:<see cref="ForgePreFlightConstants.MarkerFileName"/>。
/// </summary>
public class ForgePreFlightInstaller
{
    private readonly AppLogger? _logger;
    private readonly HttpProxyConfig? _proxy;
    private readonly string _gitExe;

    public ForgePreFlightInstaller(
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
    /// 检查 Forge pre-flight 是否已完成(marker 文件存在)。
    /// RequirementsInstaller.IsInstalled 内部调用此处保持单一判定源。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, ForgePreFlightConstants.MarkerFileName));
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

        _logger?.Info("forge-preflight",
            $"env='{env.Name}' kind='{env.TemplateKind}' 开始 Forge pre-flight (5 步)");
        logProgress?.Report($"[forge-preflight] env='{env.Name}' 开始 pre-flight");

        var pythonExe = ResolveVenvPython(env);

        // 1. clip zip
        var clipResult = await InstallZipAsync(
            ForgePreFlightConstants.Zips[0], pythonExe, logProgress, ct);
        if (!IsPipOk(clipResult)) return FailFrom(clipResult, "clip");

        // 2. open_clip zip
        var ocResult = await InstallZipAsync(
            ForgePreFlightConstants.Zips[1], pythonExe, logProgress, ct);
        if (!IsPipOk(ocResult)) return FailFrom(ocResult, "open_clip");

        // 3. requirements_versions.txt — 拆 2 步处理冲突包。
        //    复用 ForgePreFlightConstants 同段注释(详细 rationale 在那)。
        //    旧版整文件 --no-deps 跳过所有 transitive deps,导致 webui.py
        //    启动期 fastapi → starlette、pydantic → typing-extensions、
        //    gradio → gradio_client 等都 ModuleNotFoundError。
        //    正确策略:过滤掉 pytorch_lightning==1.9.4(它要求 torch<2.0 与
        //    BED 锁的 torch 2.4.0+cu121 冲突),其余包正常 pip install 让 pip
        //    自动拉 transitive deps;然后 pytorch_lightning 单独 --no-deps 装。
        //    镜像 Forge 自己的 launch_utils.py 主 install 段的整体策略。
        var reqPath = Path.Combine(env.RootPath, "requirements_versions.txt");
        if (!File.Exists(reqPath))
        {
            // forge env 是用户从 ENVTemplate clone 出来的,理论上必有 requirements_versions.txt。
            // 缺失 → 报清晰错(launch.py 也会 fail,用户需要 re-clone template)。
            var reason = $"找不到 requirements_versions.txt(预期路径:{reqPath})";
            _logger?.Error("forge-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[forge-preflight] ✗ {reason}");
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
            _logger?.Error("forge-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[forge-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }
        // 过滤 2 类冲突行:
        //   1) 裸 torch 行(防覆盖 BED 锁的 torch 2.4.0+cu121)
        //   2) pytorch_lightning 行(它要求 torch<2.0)
        // torchvision / torchaudio / torchdiffeq / torchsde / open-clip-torch
        // 名字含 torch 但不是 torch 包,正常保留。
        var filteredLines = FilterConflictingLines(rawLines);
        try
        {
            await File.WriteAllLinesAsync(filteredReqPath, filteredLines, ct);
        }
        catch (Exception ex)
        {
            var reason = $"写过滤文件失败:{ex.Message}";
            _logger?.Error("forge-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[forge-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }
        // 3a. 主 install — 不用 --no-deps,让 pip 自动拉 transitive deps:
        //     gradio → gradio_client,fastapi → starlette,pydantic →
        //     typing-extensions,transformers → tokenizers + safetensors 等。
        //     requirements_versions.txt 都是预编译 wheel,不需要 --no-build-isolation
        //     (InstallZipAsync 用的,因为 CLIP / open_clip 是源码 sdist 带 setup.py)。
        //     pytorch_lightning 已过滤掉 → pip resolver 看不到 torch<2.0 约束,
        //     BED 锁的 torch 2.4.0+cu121 安全保留。
        logProgress?.Report("[forge-preflight] stage:requirements_versions.txt (no pytorch_lightning, with deps)");
        var reqResult = await RunPipAsync(
            pythonExe,
            new[] { "install", "-r", filteredReqPath, "--disable-pip-version-check" },
            line => logProgress?.Report(line),
            ct);
        // 成功失败都清理 filtered 文件(避免下次 install 看到 stale 文件)
        try { File.Delete(filteredReqPath); } catch { }
        if (!IsPipOk(reqResult))
            return FailFrom(reqResult, $"pip install -r requirements_versions.txt (no pytorch_lightning)");

        // 3b. pytorch_lightning 单独装 + --no-deps:
        //     它要求 torch<2.0 与 BED torch 2.4.0+cu121 冲突,必须 --no-deps 避免
        //     pip 自动降级 torch(丢失 CUDA wheel + cu121 index)。镜像 Forge launch.py
        //     装 xformers 的策略:run_pip(f"install -U -I --no-deps {xformers_package}").
        logProgress?.Report("[forge-preflight] stage:pytorch_lightning --no-deps (avoid torch downgrade)");
        var ptlResult = await RunPipAsync(
            pythonExe,
            new[] { "install", "pytorch_lightning==1.9.4", "--disable-pip-version-check", "--no-deps" },
            line => logProgress?.Report(line),
            ct);
        if (!IsPipOk(ptlResult))
            return FailFrom(ptlResult, $"pip install pytorch_lightning==1.9.4 --no-deps");

        // 3c. pytorch_lightning 自己的 transitive deps 补装:
        //     step 3b 的 --no-deps 跳过了 pytorch_lightning 的所有 transitive deps,
        //     但 forge modules/initialize.py:16 `import pytorch_lightning  # noqa: F401`
        //     会触发完整 import chain(pytorch_lightning → lightning_fabric →
        //     lightning_utilities),缺任何一环 → ModuleNotFoundError。
        //     pytorch_lightning==1.9.4 Requires-Dist 列表(从其 installed
        //     .dist-info/METADATA 解析):
        //       numpy, tqdm, PyYAML, fsspec[http], torchmetrics, packaging,
        //       typing-extensions, lightning-utilities
        //     大部分(step 3a 已装的 huggingface-hub → fsspec、pydantic →
        //     typing-extensions 等)其实已被装上,装上时 pip sees satisfied
        //     version → no-op。关键的 lightning-utilities + torchmetrics 不会
        //     被任何 step 3a 包拉,必须显式装。
        //     解析 + 过滤而非硬编码 list 的理由:pytorch_lightning 升级时
        //     Requires-Dist 会变,parse 自动适配,避免下一次 whack-a-mole。
        //     过滤规则:
        //     - torch / torchvision / torchaudio → BED 已装,pip 不能动(防降级)
        //     - 含 `extra ==` 的行(Provides-Extra)→ 可选 extras,用户没装就
        //       显式装是多余的
        var ptlDeps = ParsePytorchLightningRequiresDist(env);
        if (ptlDeps.Count > 0)
        {
            logProgress?.Report($"[forge-preflight] stage:pytorch_lightning transitive deps ({ptlDeps.Count} pkgs from METADATA)");
            var ptlDepsArgs = new List<string> { "install" };
            ptlDepsArgs.AddRange(ptlDeps);
            ptlDepsArgs.Add("--disable-pip-version-check");
            var ptlDepsResult = await RunPipAsync(
                pythonExe,
                ptlDepsArgs,
                line => logProgress?.Report(line),
                ct);
            if (!IsPipOk(ptlDepsResult))
                return FailFrom(ptlDepsResult,
                    $"pip install pytorch_lightning transitive deps ({string.Join(",", ptlDeps)})");
        }

        // 4.5. v1.0.0.x (2026-08-29):extensions-builtin implicit deps —
        //     合并收集 ext 自己声明的(扫 <c>extensions-builtin/*/requirements.txt</c>)
        //     + hardcode 的隐式依赖(<see cref="ForgePreFlightConstants.ExtensionsBuiltinImplicitDeps"/>,
        //     例如 soft-inpainting 顶层 import joblib 但没 requirements.txt),
        //     一次性 pip install。
        //     跟 step 3a 一样**不**加 --no-deps,让 pip 自动拉 transitive deps
        //     (joblib → loky / multiprocess 等),joblib 等大部分包 step 3a
        //     装的 huggingface-hub/scipy 等会间接拉上,pip sees satisfied → no-op。
        //     唯一真正生效的是 hardcode 列表 + ext 自家没被 step 3a 拉的 deps。
        //     这是为 Forge 启动时 soft-inpainting 不再 ModuleNotFoundError crash
        //     的修复(用户 2026-08-29 反馈)。空 list → skip,不阻断。
        var extDeps = CollectExtensionsBuiltinDeps(env);
        if (extDeps.Count > 0)
        {
            logProgress?.Report(
                $"[forge-preflight] stage:extensions-builtin deps ({extDeps.Count} pkgs)");
            var extDepsArgs = new List<string> { "install" };
            extDepsArgs.AddRange(extDeps);
            extDepsArgs.Add("--disable-pip-version-check");
            var extDepsResult = await RunPipAsync(
                pythonExe,
                extDepsArgs,
                line => logProgress?.Report(line),
                ct);
            if (!IsPipOk(extDepsResult))
                return FailFrom(extDepsResult,
                    $"pip install extensions-builtin deps ({string.Join(",", extDeps)})");
        }
        else
        {
            logProgress?.Report(
                "[forge-preflight] stage:extensions-builtin deps (none)");
        }

        // 4. git clone 3 repos(每个独立 try/catch + IsInstalled skip,失败不阻断后续 repo;
        //    最后整体成功判断,只要全部存在或 clone 成功 → success)
        logProgress?.Report("[forge-preflight] stage:git clone 3 repos");
        var cloneResults = await CloneAllReposAsync(env, logProgress, ct);
        var failedClone = cloneResults.FirstOrDefault(r => !r.Ok);
        if (failedClone is not null)
        {
            var reason = $"git clone '{failedClone.Spec.DisplayName}' 失败(exit={failedClone.Result?.ExitCode}):{failedClone.Result?.Stderr}";
            _logger?.Error("forge-preflight", $"env='{env.Name}' {reason}");
            logProgress?.Report($"[forge-preflight] ✗ {reason}");
            return new RequirementsInstallResult(
                Success: false, Cancelled: false, Reason: reason, InstalledCount: 0);
        }

        // 全部成功 → 写 marker
        var markerPath = Path.Combine(env.RootPath, ForgePreFlightConstants.MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception ex)
        {
            _logger?.Warn("forge-preflight",
                $"env='{env.Name}' marker 写失败(ex={ex.Message});下次装依赖会被短路");
        }

        _logger?.Info("forge-preflight", $"env='{env.Name}' pre-flight 完成(4 pip + 3 repos)");
        logProgress?.Report("[forge-preflight] ✓ 完成(4 pip + 3 repos)");
        return new RequirementsInstallResult(
            Success: true, Cancelled: false, Reason: null, InstalledCount: 0);
    }

    private async Task<PipResult> InstallZipAsync(
        ForgePreFlightConstants.ZipPackage pkg,
        string pythonExe,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        logProgress?.Report($"[forge-preflight] stage:{pkg.DisplayName}");
        // CLIP / open_clip 老 setup.py 引用 `from pkg_resources import ...`(setuptools 自带)。
        // pip 默认 isolated build 会建干净的 build env 不带 setuptools → pkg_resources 缺失 →
        // `Getting requirements to build wheel` 失败。`--no-build-isolation` 让 pip 复用
        // venv 里已装的 setuptools,带 pkg_resources。launch_utils.py 没显式传这 flag,
        // 但 launch.py 跑前 BED 阶段已经 `pip install --upgrade setuptools`,所以 launch.py
        // 跑 setup.py 间接有 pkg_resources — 我们 pre-flight 同样用 BED 后的 venv,加
        // `--no-build-isolation` 一致等价。
        logProgress?.Report($"[forge-preflight] $ pip install {pkg.Url} --no-build-isolation");
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
        var reposDir = Path.Combine(env.RootPath, ForgePreFlightConstants.RepositoriesDirName);
        try
        {
            Directory.CreateDirectory(reposDir);
        }
        catch (Exception ex)
        {
            _logger?.Error("forge-preflight",
                $"env='{env.Name}' 创建 repositories 目录失败:{ex.Message}");
            return ForgePreFlightConstants.Repos
                .Select(spec => new RepoCloneOutcome(spec, null, ex.Message))
                .ToList();
        }

        // gitRunner per-call(每次重建 — proxy 是 live config,不能 stale)。
        var git = new GitRunner(_gitExe, _proxy);
        var outcomes = new List<RepoCloneOutcome>(ForgePreFlightConstants.Repos.Count);

        foreach (var spec in ForgePreFlightConstants.Repos)
        {
            ct.ThrowIfCancellationRequested();
            var targetDir = Path.Combine(reposDir, spec.DirName);
            // IsInstalled 检测:launch.py 自己的 git_clone 也检测 .git 存在就 skip。
            if (Directory.Exists(Path.Combine(targetDir, ".git")))
            {
                logProgress?.Report($"[forge-preflight] ✓ {spec.DisplayName} 已存在,跳过");
                outcomes.Add(new RepoCloneOutcome(spec, null, null));
                continue;
            }

            logProgress?.Report($"[forge-preflight] $ git clone {spec.Url} {spec.DirName}");
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
                _logger?.Warn("forge-preflight",
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
            // HEAD hash 在 Constants 里特殊处理 — 直接跳过 checkout,等同 launch.py 不传 hash。
            if (!string.Equals(spec.CommitHash, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var coResult = await git.RunAsync(
                    targetDir,
                    new[] { "checkout", spec.CommitHash },
                    timeout: TimeSpan.FromMinutes(2),
                    ct: ct);
                if (!coResult.Ok)
                {
                    // checkout 失败不致命 — git clone 已成功,后续 launch.py 自己会处理;
                    // 报 warn 但不阻断。
                    _logger?.Warn("forge-preflight",
                        $"env='{env.Name}' {spec.DisplayName} checkout {spec.CommitHash} 失败(exit={coResult.ExitCode}):{coResult.Stderr}");
                    logProgress?.Report($"[forge-preflight] warn:{spec.DisplayName} checkout 失败(继续)");
                }
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
        // v1.0.0.x (2026-09-01): PYTHONUTF8=1 — 见 PipProcessHelpers doc-comment。
        PipProcessHelpers.ApplyUtf8Mode(psi);
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
    /// 装依赖共用,改它会扩散到其它 caller。这里只在 Forge pre-flight 用,
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
    /// v1.0.0.x (2026-08-29):step 3 主 install 用的综合过滤 —— 滤掉两类冲突行:
    ///   1) 裸 torch 行(<see cref="IsBareTorchLine"/>)
    ///   2) pytorch_lightning 行(<see cref="IsPytorchLightningLine"/>)
    /// pytorch_lightning==1.9.4 要求 torch&lt;2.0,与 BED 锁的 torch 2.4.0+cu121 冲突;
    /// 主 install 段要去掉它(后续 step 3b 单独 --no-deps 装),避免 pip resolver 把 torch
    /// 降到 1.x 丢失 cu121 CUDA wheel。其他 torch 系列 / 含 torch 子串但不是 torch
    /// 包的都正常保留。
    /// </summary>
    internal static List<string> FilterConflictingLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var raw in rawLines)
        {
            if (IsBareTorchLine(raw)) continue;
            if (IsPytorchLightningLine(raw)) continue;
            result.Add(raw ?? "");
        }
        return result;
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):行首匹配 pytorch_lightning(忽略大小写),后跟空白 /
    /// 行尾 / 比较运算符。pytorch_lightning 1.9.4 是 requirements_versions.txt 里
    /// 唯一对 torch 有版本约束的冲突行(torch&lt;2.0),必须从主 install 过滤掉,
    /// step 3b 单独 --no-deps 处理。
    /// </summary>
    internal static bool IsPytorchLightningLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;
        var s = rawLine.TrimStart();
        if (!s.StartsWith("pytorch_lightning", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length == 17) return true;                 // 纯 "pytorch_lightning" 行
        var next = s[17];
        return char.IsWhiteSpace(next)
            || next == '=' || next == '<' || next == '>'
            || next == '!' || next == '~' || next == ';';
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):解析 pytorch_lightning 安装后的
    /// <c>&lt;envRoot&gt;/venv/Lib/site-packages/pytorch_lightning-*.dist-info/METADATA</c>
    /// 文件,提取 <c>Requires-Dist</c> 列表里**非 extras / 非 torch 系列 / 非 numpy** 的包名,
    /// 返回去重的字符串列表,作为 step 3c 第二次 pip install 的参数。
    ///
    /// 为什么需要这一步:step 3b 用 <c>--no-deps</c> 装 pytorch_lightning 是为了
    /// 避开它的 <c>torch&lt;2.0</c> 约束(防降级 BED 锁的 torch 2.4.0+cu121),但
    /// <c>--no-deps</c> 同时跳过了 pytorch_lightning 自己的 transitive deps。
    /// forge 的 <c>modules/initialize.py:16</c> 有 <c>import pytorch_lightning</c>,
    /// Python 会沿完整 import chain 触发 <c>lightning_fabric → lightning_utilities</c>,
    /// 缺关键 dep → <c>ModuleNotFoundError</c> 启动 crash。
    ///
    /// 故意 parse METADATA 而不是硬编码包名清单的原因:pytorch_lightning 升级时
    /// Requires-Dist 会变(1.9.4 加 lightning-utilities,未来版本可能换 dep),
    /// parse 自动适配。filter 规则:
    ///   - 跳过 <c>; extra == 'all'</c>(Provides-Extra 标记的可选 extras)
    ///   - 跳过 torch / torchvision / torchaudio(BED 已锁,不能动)
    ///   - 跳过 numpy(step 3a 已用 requirements_versions.txt 里的 <c>numpy==1.26.2</c>
    ///     pin 装好;step 3c 再调 <c>pip install numpy</c> 无版本约束会把 numpy
    ///     升到 2.x,而 torch 2.4.x 是 numpy 1.x ABI 编译的,numpy 2.x 与
    ///     torch 2.4.x ABI 不兼容 → <c>_ARRAY_API not found</c> + webui 启动
    ///     crash 在 <c>backend/memory_management.py:6 import torch</c>)
    ///   - 包名用首段(token before space / version / env marker)
    ///
    /// 返回空 list = 解析失败 / 没装 pytorch_lightning / 过滤后空 ——
    /// caller 走 skip 路径,不报错(防意外破坏成功 pre-flight)。
    /// </summary>
    internal static List<string> ParsePytorchLightningRequiresDist(Environment env)
    {
        var result = new List<string>();
        var sitePackages = Path.Combine(env.RootPath, "venv", "Lib", "site-packages");
        if (!Directory.Exists(sitePackages)) return result;

        // pytorch_lightning-1.9.4.dist-info / pytorch_lightning-2.0.0.dist-info
        string? distInfoDir = null;
        try
        {
            distInfoDir = Directory.EnumerateDirectories(sitePackages, "pytorch_lightning-*.dist-info")
                .FirstOrDefault();
        }
        catch { return result; }
        if (distInfoDir is null) return result;

        var metadataPath = Path.Combine(distInfoDir, "METADATA");
        if (!File.Exists(metadataPath)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(metadataPath))
        {
            if (!rawLine.StartsWith("Requires-Dist:", StringComparison.Ordinal)) continue;
            var spec = rawLine.Substring("Requires-Dist:".Length).Trim();
            // 跳过 Provides-Extra 标记的可选 extras —— Requires-Dist: foo ; extra == 'all'
            if (spec.Contains("extra ==", StringComparison.OrdinalIgnoreCase)) continue;

            // 取首个 token(包名,strip extras 标记 `fsspec[http]` / 版本 / env marker)
            var tokenMatch = System.Text.RegularExpressions.Regex.Match(
                spec, @"^\s*([A-Za-z0-9][A-Za-z0-9_.\-]*)");
            if (!tokenMatch.Success) continue;
            var pkgName = tokenMatch.Groups[1].Value;
            // 跳过 torch 系列(BED 锁版本,不能动)
            if (pkgName.Equals("torch", StringComparison.OrdinalIgnoreCase)) continue;
            if (pkgName.Equals("torchvision", StringComparison.OrdinalIgnoreCase)) continue;
            if (pkgName.Equals("torchaudio", StringComparison.OrdinalIgnoreCase)) continue;
            // 跳过 numpy:step 3a 用 requirements_versions.txt 的 numpy==1.26.2 已装;
            // step 3c 再 pip install numpy(无版本约束)会升到 2.x,破坏 torch 2.4.x
            // 的 numpy 1.x ABI 编译产物(用户 2026-08-29 启动 fail 的根因)。
            if (pkgName.Equals("numpy", StringComparison.OrdinalIgnoreCase)) continue;
            // dedup(同一包多个版本约束去重 — pip 只要包名,不取版本约束)
            if (!seen.Add(pkgName)) continue;
            result.Add(pkgName);
        }
        return result;
    }

    /// <summary>
    /// 单 repo clone 结果。<see cref="Ok"/> 表示:
    /// 已存在(skip) / clone 成功(可能 checkout 失败但已 warn) / 异常 captured(失败)。
    /// </summary>
    private sealed record RepoCloneOutcome(
        ForgePreFlightConstants.RepoSpec Spec,
        GitResult? Result,
        string? ErrorMessage)
    {
        public bool Ok => ErrorMessage is null && (Result is null || Result.Ok);
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):step 4.5 — 合并收集 Forge extensions-builtin
    /// 的 Python 依赖。来源 2 路:
    ///   1) 扫 <c>&lt;envRoot&gt;/extensions-builtin/*/requirements.txt</c>
    ///      (ext 自己声明的)→ 解析每行包名,合并去重。
    ///   2) 加 <see cref="ForgePreFlightConstants.ExtensionsBuiltinImplicitDeps"/>
    ///      (顶层 import 但 ext 漏声明的隐性依赖,例如 soft-inpainting 缺 joblib)。
    ///
    /// 不调用 pip(由 caller 在拿到结果后统一 RunPipAsync,镜像 step 3c 的 pattern)。
    /// 过滤规则跟 <see cref="FilterConflictingLines"/> 对齐:ext req 里的
    /// 裸 torch / pytorch_lightning 行跳过(BED 锁版本不能动),避免这里拼出来
    /// 又触发 pip resolver 降级 BED torch。
    ///
    /// 返回空 list = env 无 extensions-builtin 子目录 / 全部 ext 都无
    /// requirements.txt / 解析失败 —— caller 走 skip,视为成功(pre-flight 不阻断)。
    ///
    /// 用 <paramref name="envRoot"/> 显式传 env 根,避免依赖 Environment 实例
    /// (跟 ParsePytorchLightningRequiresDist 保持一致传 env 风格)。
    /// </summary>
    internal static List<string> CollectExtensionsBuiltinDeps(Environment env)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 路 1:扫 extensions-builtin/<extName>/requirements.txt (仅顶层 ext 目录,
        // 防止误吞 nested 副本 — 例如 forge_preprocessor_normalbae/annotator/.../
        // efficientnet_repo/requirements.txt 是 sub-package 自己的依赖,
        // 跟 ext 启动无关)。
        var extBuiltinRoot = Path.Combine(env.RootPath,
            ForgePreFlightConstants.ExtensionsBuiltinDir);
        if (Directory.Exists(extBuiltinRoot))
        {
            foreach (var extDir in Directory.EnumerateDirectories(extBuiltinRoot))
            {
                var reqFile = Path.Combine(extDir, "requirements.txt");
                if (!File.Exists(reqFile)) continue;
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(reqFile);
                }
                catch
                {
                    continue;
                }
                foreach (var raw in lines)
                {
                    var pkg = ExtractReqPackageName(raw);
                    if (pkg is null) continue;
                    // 跳过 torch 系列(防 pip resolver 降级 BED 锁的 torch 2.4.0+cu121)
                    if (pkg.Equals("torch", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pkg.Equals("torchvision", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pkg.Equals("torchaudio", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(pkg)) continue;
                    result.Add(pkg);
                }
            }
        }

        // 路 2:hardcode 的 implicit deps(没声明但顶层 import 的)
        foreach (var pkg in ForgePreFlightConstants.ExtensionsBuiltinImplicitDeps)
        {
            if (!seen.Add(pkg)) continue;
            result.Add(pkg);
        }

        return result;
    }

    /// <summary>
    /// 从单行 requirements.txt 解析出首个包名 token。
    /// 处理:leading whitespace / 注释 / `-r` / `-e` 等 marker / extras / 版本约束。
    /// 失败 / 跳过 → 返 null(caller 跳过该行)。
    /// </summary>
    private static string? ExtractReqPackageName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;
        var s = rawLine.TrimStart();
        // 注释 / pip options(`-r other.txt` / `-e .` / `--hash=...`)
        if (s.Length == 0) return null;
        if (s[0] == '#') return null;
        if (s[0] == '-') return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            s, @"^([A-Za-z0-9][A-Za-z0-9_.\-]*)");
        if (!match.Success) return null;
        return match.Groups[1].Value;
    }
}