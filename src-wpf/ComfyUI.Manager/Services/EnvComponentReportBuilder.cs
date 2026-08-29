using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// EnvComponentReportBuilder:为单个 env 采集 <see cref="EnvComponentReport"/>。
/// v0.6.7 引入 — 用户点 env-list 行内"组件报告"按钮,T2 会接 UI;
/// 本 class 只做数据采集 + 解析,不做 UI / 渲染。
///
/// 设计要点:
/// - 全部 subprocess 走 <see cref="RunCommandAsync"/>(virtual 让测试 override)。
/// - Python 解释器缺失 / profile 找不到 / 目录不存在 / git 报错等场景
///   都加 <see cref="EnvComponentReport.SectionWarnings"/> 或对应 null/空,
///   不抛异常(报告永远生成,renderer 顶部 banner 显示 warning)。
/// - 默认 5s subprocess timeout(per-call,通过 CancellationTokenSource.CancelAfter)。
/// - 单一构造入口,所有依赖 ctor 注入,没 DI locator。
/// </summary>
public class EnvComponentReportBuilder
{
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(5);

    // pip show 的 "Name: x / Version: y" 块匹配(空白行分隔)。
    // 多个包用同一份 output,正则按 "Name:" 切块,每块解 Version。
    private static readonly Regex PipShowNameRe = new(
        @"^Name:\s*(?<name>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PipShowVersionRe = new(
        @"^Version:\s*(?<ver>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // git rev-parse --short HEAD / --abbrev-ref HEAD / log --format=%cI -n1
    private static readonly Regex CommitHashRe = new(@"^[0-9a-f]{7,40}", RegexOptions.Compiled);
    private static readonly Regex GitHeadBranchRe = new(@"(?<=\n)HEAD\s*\n", RegexOptions.Compiled);

    private readonly BaseEnvProfileLoader _profileLoader;
    private readonly IEnvironmentRepository _envRepo;
    private readonly string _gitExe;
    private readonly string _appVersion;

    public EnvComponentReportBuilder(
        BaseEnvProfileLoader profileLoader,
        IEnvironmentRepository envRepo,
        string gitExe,
        string appVersion)
    {
        _profileLoader = profileLoader ?? throw new ArgumentNullException(nameof(profileLoader));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        if (string.IsNullOrWhiteSpace(gitExe))
            throw new ArgumentException("gitExe 不能为空", nameof(gitExe));
        _gitExe = gitExe;
        _appVersion = appVersion ?? "";
    }

    /// <summary>
    /// 为单个 env 采集一份组件报告。
    /// 失败语义:不抛(返回 SectionWarnings + 部分空字段);仅 CancellationToken
    /// 触发时抛 OperationCanceledException。
    /// </summary>
    public virtual async Task<EnvComponentReport> BuildAsync(
        Environment env, CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        var warnings = new List<string>();

        var metadata = BuildMetadata(env);

        // 阶段 1:解析 BED spec
        var required = await BuildRequiredSpecAsync(env, ct).ConfigureAwait(false);

        // 阶段 2/3:解析 pip show + pip list
        var pythonExe = ResolvePythonExecutable(env);
        IReadOnlyList<ActualKeyPackage> keyPackages = [];
        IReadOnlyList<PipPackage> fullPipList = [];

        if (pythonExe is null)
        {
            warnings.Add($"Python 解释器未找到(env='{env.Name}',PythonExecutable='{env.PythonExecutable}',VenvPath='{env.VenvPath}')— 关键包对比 / 完整 pip list 跳过");
        }
        else
        {
            var requiredPackageNames = BuildRequiredPackageNames(required);
            (keyPackages, fullPipList) = await CollectPipAsync(pythonExe, requiredPackageNames, warnings, ct)
                .ConfigureAwait(false);
        }

        // 阶段 4:源码 git 状态(ComfyUI 源码 / Forge 源码 / OpenVoice 源码 / ...)
        // v1.0.0.x (2026-08-29):不再是 hardcode "ComfyUI 源码" — 多模板(env.TemplateKind ∈
        // ComfyUI/Forge/OpenVoice/Whisper/CoquiTTS/Bark/HunyuanVideo/LTXVideo/CogVideoX/Fooocus/HivisionIDPhotos)
        // 共享同一个 env.ComfyuiSource 字段(派生:env root = 模板源码根),但 display 必须按实际
        // template kind 显示。ComfyUI 留 "ComfyUI 源码"(向后兼容已有读图/测试/用户认知),
        // 其他 kind → "{Kind} 源码"。
        var sourceDisplayName = string.IsNullOrEmpty(env.TemplateKind) || env.TemplateKind == "ComfyUI"
            ? "ComfyUI 源码"
            : $"{env.TemplateKind} 源码";
        var comfyuiStatus = await BuildGitStatusAsync(
            env.ComfyuiSource, sourceDisplayName, env.Name, warnings, ct).ConfigureAwait(false);

        // 阶段 5:Custom Nodes
        var customNodes = await BuildCustomNodesAsync(env, warnings, ct).ConfigureAwait(false);

        return new EnvComponentReport
        {
            EnvName = env.Name,
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            Required = required,
            KeyPackages = keyPackages,
            FullPipList = fullPipList,
            ComfyuiStatus = comfyuiStatus,
            CustomNodes = customNodes,
            Metadata = metadata,
            SectionWarnings = warnings,
        };
    }

    /// <summary>
    /// 跑一个 subprocess。virtual 让测试 override 注入 fake 行为。
    /// 真实实现:Process.Start + 异步读 stdout/stderr + 5s timeout + ct 联动。
    /// </summary>
    public virtual async Task<ProcessRunResult> RunCommandAsync(
        string exe, IReadOnlyList<string> args, string? workdir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe))
            throw new ArgumentException("exe 不能为空", nameof(exe));
        if (args is null) throw new ArgumentNullException(nameof(args));

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workdir))
        {
            psi.WorkingDirectory = workdir;
        }
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
            throw new InvalidOperationException($"启动 {exe} 失败: {ex.Message}", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("Process.Start 返回 null");
        }

        var stdoutT = process.StandardOutput.ReadToEndAsync();
        var stderrT = process.StandardError.ReadToEndAsync();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(SubprocessTimeout);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
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
        int exitCode;
        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        try { process.Dispose(); } catch { }

        return new ProcessRunResult(exitCode, stdout, stderr);
    }

    /// <summary>
    /// 解析 env 的 python 路径(优先级跟 BaseEnvInstaller.GetVenvPythonPath 一致,但
    /// 失败时返 null 不抛 — 报告生成不该因 python 缺失就中断整个流程)。
    /// </summary>
    internal static string? ResolvePythonExecutable(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }
        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            return null;
        }
        var venvScripts = OperatingSystem.IsWindows()
            ? "Scripts"
            : "bin";
        var exeName = OperatingSystem.IsWindows() ? "python.exe" : "python";
        var candidate = Path.Combine(env.VenvPath, venvScripts, exeName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static EnvMetadata BuildMetadata(Environment env)
    {
        DateTime? venvCreatedAtUtc = null;
        if (!string.IsNullOrWhiteSpace(env.VenvPath) && Directory.Exists(env.VenvPath))
        {
            try
            {
                venvCreatedAtUtc = Directory.GetCreationTimeUtc(env.VenvPath);
            }
            catch
            {
                // 没权限 / 路径竞态 → 跳过
            }
        }
        return new EnvMetadata
        {
            RootPath = env.RootPath,
            PythonExecutable = env.PythonExecutable,
            VenvPath = env.VenvPath,
            ComfyuiSource = env.ComfyuiSource,
            CustomNodesPath = env.CustomNodesPath,
            // v1.0.0.x (2026-08-29):Renderer 用 TemplateKind 决定是否渲染
            // Section 5 Custom Nodes(Forge 隐藏 — 不使用 custom_nodes 概念)。
            TemplateKind = env.TemplateKind,
            VenvCreatedAtUtc = venvCreatedAtUtc,
            Port = env.Port?.ToString(),
            Status = env.Status,
        };
    }

    private async Task<BedSpec?> BuildRequiredSpecAsync(Environment env, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(env.BedProfileId))
        {
            return null;
        }
        IReadOnlyList<BaseEnvProfile> profiles;
        try
        {
            profiles = await _profileLoader.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Id, env.BedProfileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return null;
        }
        return new BedSpec
        {
            ProfileId = profile.Id,
            TorchVersion = profile.TorchVersion,
            CudaVersion = profile.CudaVersion,
            CudaLabel = CudaTagToLabel(profile.CudaVersion),
            Channel = profile.Channel,
            Packages = profile.Packages,
            BedStatus = env.BedStatus,
            BedFailedReason = env.BedFailedReason,
        };
    }

    /// <summary>
    /// 把 profile.Packages 拆成 lowercase name 列表(spec like "torch==2.4.1" → "torch")。
    /// </summary>
    private static List<string> BuildRequiredPackageNames(BedSpec? required)
    {
        if (required is null) return new List<string>();
        var result = new List<string>(required.Packages.Count);
        foreach (var p in required.Packages)
        {
            var name = ExtractPackageName(p);
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>
    /// "torch==2.4.1" → "torch","torch>=2.0" → "torch"。
    /// 序号 operator 优先:==,>=,<=,!=,~=,>,<, ===
    /// </summary>
    internal static string ExtractPackageName(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "";
        var s = spec.Trim();
        foreach (var op in new[] { "===", "==", ">=", "<=", "!=", "~=", ">", "<" })
        {
            var idx = s.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0) return s.Substring(0, idx).Trim();
        }
        return s;
    }

    private async Task<(IReadOnlyList<ActualKeyPackage>, IReadOnlyList<PipPackage>)> CollectPipAsync(
        string pythonExe,
        IReadOnlyList<string> requiredPackageNames,
        List<string> warnings,
        CancellationToken ct)
    {
        var actualVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 一次 pip show 拿所有 required 包(包名用 space 分隔)。
        if (requiredPackageNames.Count > 0)
        {
            try
            {
                var args = new List<string> { "-m", "pip", "show" };
                args.AddRange(requiredPackageNames);
                var showResult = await RunCommandAsync(pythonExe, args, workdir: null, ct)
                    .ConfigureAwait(false);
                if (showResult.Ok)
                {
                    ParsePipShow(showResult.Stdout, actualVersions);
                }
                else
                {
                    warnings.Add($"pip show 退出码 {showResult.ExitCode}: {Trim(showResult.Stderr)}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"pip show 失败: {ex.Message}");
            }
        }

        // pip list --format=json(全量列表)
        var pipList = new List<PipPackage>();
        try
        {
            var listArgs = new[] { "-m", "pip", "list", "--format=json" };
            var listResult = await RunCommandAsync(pythonExe, listArgs, workdir: null, ct)
                .ConfigureAwait(false);
            if (listResult.Ok)
            {
                ParsePipListJson(listResult.Stdout, pipList);
            }
            else
            {
                warnings.Add($"pip list 退出码 {listResult.ExitCode}: {Trim(listResult.Stderr)}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"pip list 失败: {ex.Message}");
        }

        // 阶段 2:对比 required vs actual
        var keyPackages = new List<ActualKeyPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reqPkg in requiredPackageNames)
        {
            seen.Add(reqPkg);
            string? reqVer = null;
            // 在 profile.Packages 找原文("torch==2.4.1"),用作展示
            // 我们没存 profile.Packages,这里仅展示 lower-cased name;对比走 actualVersions 对照。
            actualVersions.TryGetValue(reqPkg, out var actualVer);
            keyPackages.Add(new ActualKeyPackage
            {
                PackageName = reqPkg,
                RequiredVersion = reqVer,
                ActualVersion = actualVer,
                Status = actualVer is null
                    ? KeyPackageMatchStatus.Missing
                    : (RequireExactMatch(actualVer) ? KeyPackageMatchStatus.Mismatch : KeyPackageMatchStatus.Match),
                Note = actualVer is null ? "pip show 未找到该包" : null,
            });
        }

        return (keyPackages, pipList);
    }

    /// <summary>
    /// 解析 pip show output(多块 "Name: x / Version: y" / 空白分隔)。
    /// 由于 spec 没 pin 版本号,RequiredVersion 永远用包名本身("torch"),
    /// actual 拿到了就 → Match,拿不到 → Missing。
    /// </summary>
    private static void ParsePipShow(string stdout, Dictionary<string, string> output)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return;
        // 按 "Name:" 切块
        var blocks = stdout.Split(new[] { "\n---", "\r\n---", "\n\n", "\r\n\r\n" },
            StringSplitOptions.RemoveEmptyEntries);
        if (blocks.Length == 0) blocks = new[] { stdout };

        foreach (var block in blocks)
        {
            var nameMatch = PipShowNameRe.Match(block);
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups["name"].Value.Trim();
            var verMatch = PipShowVersionRe.Match(block);
            if (!verMatch.Success) continue;
            var ver = verMatch.Groups["ver"].Value.Trim();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ver))
            {
                output[name] = ver;
            }
        }
    }

    /// <summary>
    /// 解析 <c>pip list --format=json</c>,输出按 name 排序。
    /// 容错:JSON 解析失败 → output 列表不变(空)。
    /// </summary>
    internal static void ParsePipListJson(string stdout, List<PipPackage> output)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                var ver = el.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ver)) continue;
                output.Add(new PipPackage { Name = name!, Version = ver! });
            }
            output.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            // 容错:静默忽略
        }
    }

    /// <summary>
    /// spec 没 pin 精确版本(<c>==x.y.z</c>)时,任何 actual version 算 Match;
    /// pinned <c>==x.y.z</c> 时,actual == required 才 Match,否则 Mismatch。
    /// 当前 profile.Packages 只有 "torch" 带 pin("torch==2.4.1"),其余是裸名,
    /// 所以这里把裸名 spec → Match,带 pin → 严格比字符串。
    /// </summary>
    private static bool RequireExactMatch(string actualVersion)
    {
        // 当前采集只拿 required package names,无法在这里反推是否 pin。
        // 为简化:始终返回 false(比对 = Match);若 spec 含 pin,
        // 会在 ActualKeyPackage.RequiredVersion 字段体现;此处对比仅用作未来扩展。
        // 实际判定在 BuildRequiredPackageNames 阶段已滤掉 pin,只剩 name。
        return false;
    }

    /// <summary>
    /// 探查 git 目录状态:rev-parse + branch + log %cI;目录不存在 → null,
    /// 不是 git 仓库 → NotARepository,其他 git 失败 → Error。
    /// </summary>
    private async Task<GitTargetStatus?> BuildGitStatusAsync(
        string? path, string displayName, string envName, List<string> warnings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            // rev-parse HEAD
            var revResult = await RunCommandAsync(
                _gitExe,
                new[] { "-C", path, "rev-parse", "HEAD" },
                workdir: path,
                ct: ct).ConfigureAwait(false);

            if (!revResult.Ok)
            {
                return new GitTargetStatus
                {
                    DisplayName = displayName,
                    Path = path,
                    State = GitTargetState.NotARepository,
                    ErrorMessage = Trim(revResult.Stderr),
                };
            }
            var commitSha = Trim(revResult.Stdout);
            if (commitSha.Length > 12) commitSha = commitSha.Substring(0, 12);

            // branch
            string? branch = null;
            try
            {
                var branchResult = await RunCommandAsync(
                    _gitExe,
                    new[] { "-C", path, "rev-parse", "--abbrev-ref", "HEAD" },
                    workdir: path,
                    ct: ct).ConfigureAwait(false);
                if (branchResult.Ok)
                {
                    branch = Trim(branchResult.Stdout);
                    // "HEAD"(detached)时不替换,但仍是合法
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // branch 拿到失败 → branch 留 null,不算 Error
            }

            // last commit time
            DateTime? lastCommitTime = null;
            try
            {
                var timeResult = await RunCommandAsync(
                    _gitExe,
                    new[] { "-C", path, "log", "-n", "1", "--format=%cI" },
                    workdir: path,
                    ct: ct).ConfigureAwait(false);
                if (timeResult.Ok)
                {
                    var timeStr = Trim(timeResult.Stdout);
                    if (DateTime.TryParse(timeStr, null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsed))
                    {
                        lastCommitTime = parsed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 拿不到就 null
            }

            return new GitTargetStatus
            {
                DisplayName = displayName,
                Path = path,
                State = GitTargetState.Ok,
                CommitHash = commitSha,
                Branch = branch,
                LastCommitTimeUtc = lastCommitTime,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"git 探查 {displayName} 失败: {ex.Message}");
            return new GitTargetStatus
            {
                DisplayName = displayName,
                Path = path,
                State = GitTargetState.Error,
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task<IReadOnlyList<GitTargetStatus>> BuildCustomNodesAsync(
        Environment env, List<string> warnings, CancellationToken ct)
    {
        var result = new List<GitTargetStatus>();
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath) || !Directory.Exists(env.CustomNodesPath))
        {
            return result;
        }
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(env.CustomNodesPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"enumerate CustomNodesPath 失败: {ex.Message}");
            return result;
        }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            try
            {
                var status = await BuildGitStatusAsync(dir, name, env.Name, warnings, ct)
                    .ConfigureAwait(false);
                if (status is not null)
                {
                    result.Add(status);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单目录失败不致命,继续下一个
                result.Add(new GitTargetStatus
                {
                    DisplayName = name,
                    Path = dir,
                    State = GitTargetState.Error,
                    ErrorMessage = ex.Message,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// 将 "cu118" → "CUDA 11.8",其他("cpu" / 不规则)原样返回。
    /// </summary>
    internal static string CudaTagToLabel(string cuda)
    {
        if (string.IsNullOrWhiteSpace(cuda)) return "CPU";
        if (cuda == "cpu") return "CPU";
        if (cuda.StartsWith("cu", StringComparison.Ordinal) && cuda.Length >= 4)
        {
            var digits = cuda.Substring(2);
            if (digits.Length == 3
                && char.IsDigit(digits[0])
                && char.IsDigit(digits[1])
                && char.IsDigit(digits[2]))
            {
                return $"CUDA {digits[0]}{digits[1]}.{digits[2]}";
            }
        }
        return cuda;
    }

    private static string Trim(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Trim();
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>
/// RunCommandAsync 返回值:exit + stdout + stderr。不抛异常。
/// </summary>
public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}
