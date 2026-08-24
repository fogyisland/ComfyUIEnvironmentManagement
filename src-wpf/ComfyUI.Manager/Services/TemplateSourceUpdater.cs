using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 T11: 通用 template source updater — wipe contents of any target
/// directory (which must already exist) then git clone the given repo URL back
/// to the same path. Destructive.
///
/// v0.6.22.x evolution:
/// - v0.6.22 T5: per-env env.ComfyuiSource, hardcoded comfyanonymous/ComfyUI.
/// - v0.6.22.x: path-based, target = &lt;projectRoot&gt;/ComfyUITemplate/ master
///   template (only affects next env creation).
/// - v1.0.0 T11 (G10): per-repo-URL. Takes <c>repoUrl</c> as a parameter so it
///   can update any template (ComfyUI, A1111, custom). Hardcoded URL removed.
///
/// Confirms:
/// - Caller MUST gate with confirm dialog before invoking — this service will
///   not prompt.
/// - Keeps the directory itself (junction target / permissions preserved).
/// - <c>--depth=1</c> clone for speed — template just needs current main.
///
/// v1.0.0.x: rich Console log mirror v0.6.22++ ModelMarketplace 模式:
///   [src] → host (proxy info) / [src] ← exit=N (ms) / [src] ✓ 完成 / [src] ✗ ... / [src] ⏹ ...
///   git 没有 HTTP response code,用 exit code 作类比(exit=0 = success = "200 之类")。
///
/// AppLogger subsystem: <c>template-source-update</c>.
/// </summary>
/// <remarks>
/// v1.0.0.x: progress 行格式约定(同 v0.6.22++ ModelMarketplace):
/// <list type="bullet">
///   <item><c>[src] → {host} ({proxyInfo})</c> — git clone 开始前</item>
///   <item><c>[src] ← exit={code} ({ms}ms)</c> — git 返回(exit=0 类比 HTTP 200)</item>
///   <item><c>[src] ✓ 完成 ({ms}ms)</c> — 整个 update 成功</item>
///   <item><c>[src] ✗ {ErrorType} ({ms}ms): {reason}</c> — git 失败 / 异常</item>
///   <item><c>[src] ⏹ 已取消 ({ms}ms)</c> — 用户取消</item>
/// </list>
/// 调用方负责给行加 <c>[{Kind}]</c> 前缀(多模板并发区分),本服务只关心 <c>[src]</c>。
/// </remarks>
public class TemplateSourceUpdater
{
    private readonly GitRunner _git;
    private readonly AppLogger? _logger;
    /// <summary>v1.0.0.x: 模板相对路径锚定父目录 — 通常是 <c>Settings.SystemTemplateLibraryDir</c>
    /// (用户在设置页配的"系统模板库目录"),非空时所有模板都克隆到该目录下。空 = 锚到
    /// <see cref="AppContext.BaseDirectory"/>(跨启动方式稳定的 exe 所在目录)。</summary>
    private readonly string? _basePath;

    /// <summary>v1.0.0.x: 暴露代理配置给 Console log helper(FormatProxyInfo 三分支)。</summary>
    protected HttpProxyConfig? Proxy => (_git as GitRunner)?.ProxyConfig;

    public TemplateSourceUpdater(
        string gitExe,
        HttpProxyConfig? gitProxy = null,
        AppLogger? logger = null,
        string? basePath = null)
    {
        _git = new GitRunner(gitExe, gitProxy);
        _logger = logger;
        _basePath = string.IsNullOrWhiteSpace(basePath) ? null : basePath;
    }

    /// <summary>
    /// Wipe contents of <paramref name="targetDir"/> (must already exist) then
    /// git clone <paramref name="repoUrl"/> back to the same path. Returns
    /// <see cref="NodeOperationResult"/> — never throws on git failure (only on
    /// invalid args).
    /// </summary>
    public virtual async Task<NodeOperationResult> UpdateAsync(
        string targetDir,
        string repoUrl,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return NodeOperationResult.Fail("targetDir 不能为空");
        if (string.IsNullOrWhiteSpace(repoUrl))
            return NodeOperationResult.Fail("repoUrl 不能为空");
        // v1.0.0.x: 用户反馈 "下载目录必须和设置一致"。_basePath (非空时 =
        // system_template_library_dir) 是用户期望的模板存放根;空时回退到
        // AppContext.BaseDirectory (= exe dir,所有启动方式稳定) 避免 CWD 漂移
        // 把 clone 写到 settings 之外。
        targetDir = TemplatePathResolver.Resolve(targetDir, _basePath);
        if (!Directory.Exists(targetDir))
            return NodeOperationResult.Fail($"模板目录不存在:{targetDir}");

        _logger?.Info("template-source-update", $"target='{targetDir}' repo='{repoUrl}' 开始模板更新");
        progress?.Report($"开始模板更新:{targetDir}");

        // 1. delete contents (keep dir for permissions/junction)
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir))
            {
                if (ct.IsCancellationRequested)
                    return NodeOperationResult.Fail("用户取消");
                TryDelete(entry);
                progress?.Report($"已删除:{Path.GetFileName(entry)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("template-source-update", "wipe failed", ex);
            return NodeOperationResult.Fail($"删除模板目录内容失败:{ex.Message}");
        }

        // 2. git clone --depth=1 (fast, no history needed for template)
        progress?.Report($"[src] $ git clone --depth=1 {repoUrl} .");
        progress?.Report($"[src] → {FormatHost(repoUrl)} ({FormatProxyInfo()})");
        var sw = Stopwatch.StartNew();
        var sizeHolder = new PackageSizeHolder();
        GitResult r;
        try
        {
            r = await _git.RunAsync(
                workdir: targetDir,
                args: new[] { "clone", "--depth=1", repoUrl, "." },
                timeout: TimeSpan.FromMinutes(5),
                ct: ct,
                onStderrLine: WrapProgressForSizeTracking(progress, sizeHolder));
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            progress?.Report($"[src] ⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var ms = sw.ElapsedMilliseconds;
            progress?.Report($"[src] ✗ {ex.GetType().Name} ({ms}ms): {ex.Message}");
            _logger?.Error("template-source-update", "git clone threw", ex);
            return NodeOperationResult.Fail($"git clone 异常:{ex.Message}");
        }
        sw.Stop();
        var elapsed = sw.ElapsedMilliseconds;
        progress?.Report($"[src] ← exit={r.ExitCode} ({elapsed}ms)");
        if (!r.Ok)
        {
            // git stderr 取首行非空做"response body-like"展示,完整 stderr 进 NodeOperationResult
            var firstLine = SplitFirstLine(r.Stderr);
            _logger?.Warn("template-source-update", $"target='{targetDir}' git clone 失败(exit={r.ExitCode}):{r.Stderr}");
            progress?.Report($"[src] ✗ GitExit={r.ExitCode} ({elapsed}ms): {firstLine}");
            return NodeOperationResult.Fail($"git clone 失败(exit={r.ExitCode}):{r.Stderr}");
        }

        var sizeNote = string.IsNullOrEmpty(sizeHolder.Size) ? "" : $" {sizeHolder.Size}";
        progress?.Report($"[src] ✓ 完成{sizeNote} ({elapsed}ms)");
        _logger?.Info("template-source-update", $"target='{targetDir}' 模板更新完成 ({elapsed}ms, {sizeHolder.Size})");
        return NodeOperationResult.Ok(null);
    }

    /// <summary>
    /// Clone <paramref name="repoUrl"/> into empty <paramref name="targetDir"/>.
    /// Unlike <see cref="UpdateAsync"/> this does NOT wipe an existing directory —
    /// it fails if <paramref name="targetDir"/> already exists. Use <see cref="UpdateAsync"/>
    /// to refresh a previously-cloned template.
    ///
    /// v1.0.0+: used by EditTemplateDialog's SaveCommand GitHub mode to clone
    /// at add-time so LocalSourceDir is a real populated path immediately.
    /// </summary>
    public virtual async Task<NodeOperationResult> CloneAsync(
        string repoUrl,
        string targetDir,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return NodeOperationResult.Fail("repoUrl 不能为空");
        if (string.IsNullOrWhiteSpace(targetDir))
            return NodeOperationResult.Fail("targetDir 不能为空");
        // v1.0.0.x: 锚定到 _basePath (system_template_library_dir,非空时) 或
        // AppContext.BaseDirectory 回退,保证 clone target == settings 解析结果。
        targetDir = TemplatePathResolver.Resolve(targetDir, _basePath);
        if (Directory.Exists(targetDir))
            return NodeOperationResult.Fail($"目标目录已存在:{targetDir}");

        _logger?.Info("template-source-update", $"target='{targetDir}' repo='{repoUrl}' 开始模板克隆");
        progress?.Report($"开始克隆:{repoUrl}");

        // git clone --depth=1 <repo> <dir>
        // workdir = parent of targetDir; args specify target as last positional
        var parent = Path.GetDirectoryName(targetDir);
        if (string.IsNullOrWhiteSpace(parent))
            return NodeOperationResult.Fail($"无法解析父目录:{targetDir}");
        var name = Path.GetFileName(targetDir);

        // T16: git clone 只创建 leaf 目录(其父目录必须已存在)。如果父目录不存在(如 Templates/),
        // 用户首次添加 GitHub 模板时 Directory.CreateDirectory 把它建出来,git 不会自动建父级。
        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            _logger?.Error("template-source-update", $"父目录创建失败 parent='{parent}'", ex);
            return NodeOperationResult.Fail($"创建父目录失败:{ex.Message}");
        }

        progress?.Report($"[src] $ git clone --depth=1 {repoUrl} {name}");
        progress?.Report($"[src] → {FormatHost(repoUrl)} ({FormatProxyInfo()})");
        var sw = Stopwatch.StartNew();
        var sizeHolder = new PackageSizeHolder();
        GitResult r;
        try
        {
            r = await _git.RunAsync(
                workdir: parent,
                args: new[] { "clone", "--depth=1", repoUrl, name },
                timeout: TimeSpan.FromMinutes(5),
                ct: ct,
                onStderrLine: WrapProgressForSizeTracking(progress, sizeHolder));
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            progress?.Report($"[src] ⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var ms = sw.ElapsedMilliseconds;
            progress?.Report($"[src] ✗ {ex.GetType().Name} ({ms}ms): {ex.Message}");
            _logger?.Error("template-source-update", "git clone threw", ex);
            return NodeOperationResult.Fail($"git clone 异常:{ex.Message}");
        }
        sw.Stop();
        var elapsed = sw.ElapsedMilliseconds;
        progress?.Report($"[src] ← exit={r.ExitCode} ({elapsed}ms)");
        if (!r.Ok)
        {
            var firstLine = SplitFirstLine(r.Stderr);
            _logger?.Warn("template-source-update", $"target='{targetDir}' git clone 失败(exit={r.ExitCode}):{r.Stderr}");
            progress?.Report($"[src] ✗ GitExit={r.ExitCode} ({elapsed}ms): {firstLine}");
            return NodeOperationResult.Fail($"git clone 失败(exit={r.ExitCode}):{r.Stderr}");
        }

        var sizeNote = string.IsNullOrEmpty(sizeHolder.Size) ? "" : $" {sizeHolder.Size}";
        progress?.Report($"[src] ✓ 完成{sizeNote} ({elapsed}ms)");
        _logger?.Info("template-source-update", $"target='{targetDir}' 模板克隆完成 ({elapsed}ms, {sizeHolder.Size})");
        return NodeOperationResult.Ok(null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 单个 entry 失败继续(让其他 entry 删掉)— wipe 整体失败会被外层 catch 抓到。
        }
    }

    /// <summary>
    /// v1.0.0.x: 三分支(同 v0.6.22++ ModelMarketplace CivitAiModelSource.FormatProxyInfo):
    /// <list type="bullet">
    ///   <item>HttpProxyConfig == null 或 <c>!Enabled</c> → <c>"直连"</c>(没显式配代理)</item>
    ///   <item><c>Enabled</c> + <c>UseSystemProxy</c> → <c>"系统代理"</c>(读 Windows IE 代理设置)</item>
    ///   <item><c>Enabled</c> + <c>!UseSystemProxy</c> → <c>"代理=URL:Port"</c>(如 <c>代理=127.0.0.1:10808</c>)</item>
    /// </list>
    /// 注意: <see cref="HttpProxyConfig"/> 用 <c>Enabled</c> + <c>UseSystemProxy</c> 两个 bool
    /// 表示 mode,而 Settings.HttpProxyMode(enum) 是给 user-facing UI 用的更高层抽象。
    /// Console 单行 12-20 字符宽度,跟 model marketplace 行风格对齐。
    /// </summary>
    public virtual string FormatProxyInfo()
    {
        var p = Proxy;
        if (p is null || !p.Enabled) return "直连";
        if (p.UseSystemProxy) return "系统代理";
        // Custom
        var url = string.IsNullOrWhiteSpace(p.Url) ? "?" : p.Url;
        var port = p.Port <= 0 ? "?" : p.Port.ToString();
        return $"代理={url}:{port}";
    }

    /// <summary>
    /// v1.0.0.x: 从 repoUrl 安全取 host(<c>github.com/comfyanonymous/ComfyUI.git</c> → <c>github.com</c>)。
    /// 处理三种形态:
    /// <list type="bullet">
    ///   <item>HTTPS/HTTP URL → <c>new Uri(...).Host</c></item>
    ///   <item>SSH URL(<c>git@github.com:user/repo.git</c>) → 切 <c>git@...:user</c> 中间</item>
    ///   <item>都不是 → 裸字符串第一段(与 v1.0.0 之前保持兼容)</item>
    /// </list>
    /// 解析失败返回 <c>"&lt;unknown&gt;"</c>,绝不让异常逃出(影响 progress 行生成)。
    /// </summary>
    public static string FormatHost(string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return "<unknown>";
        // HTTPS/HTTP path
        try
        {
            var uri = new Uri(repoUrl);
            if (!string.IsNullOrWhiteSpace(uri.Host)) return uri.Host;
        }
        catch { /* fallthrough */ }

        // SSH path: git@github.com:user/repo.git — colon separator, not slash
        if (repoUrl.StartsWith("git@", StringComparison.Ordinal))
        {
            var colon = repoUrl.IndexOf(':');
            if (colon > 4)
            {
                // skip "git@" prefix (4 chars), hostname is between "git@" and ":"
                return repoUrl[4..colon];
            }
        }

        // Last resort: 裸字符串截取 "/" 之前一段(如 "github.com/user/repo" 等等)
        var slash = repoUrl.IndexOf('/');
        return slash > 0 ? repoUrl[..slash] : repoUrl;
    }

    /// <summary>
    /// v1.0.0.x: 取 git stderr 第一行非空内容用于 Console fail 行(对应 v0.6.22++ 的
    /// HTTP "body {bytes} bytes" 展示)。整体 stderr 仍进 <see cref="NodeOperationResult.Reason"/>。
    /// </summary>
    private static string SplitFirstLine(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "(no stderr)";
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }
        return "(empty)";
    }

    /// <summary>
    /// v1.0.0.x: 从 git "Receiving objects:" 行提取总下载量(用户要求的"包多大")。
    /// 典型行:
    /// <c>Receiving objects: 100% (12345/12345), 67.89 MiB | 12.34 MiB/s, done.</c>
    /// 也可能在 Resolving deltas 行附带(罕见)。返回 <c>"67.89 MiB"</c> 等格式化大小(数字 + 单位)
    /// 或 null(未命中)。
    /// </summary>
    private static readonly Regex ReceivingObjectsRegex = new(
        @"Receiving\s+objects:\s+\d+%[^\,]*,\s+(?<size>[\d.]+\s+\w+)",
        RegexOptions.Compiled);

    private static string? ExtractSizeFromLine(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith("Receiving objects")) return null;
        var m = ReceivingObjectsRegex.Match(line);
        return m.Success ? m.Groups["size"].Value : null;
    }

    /// <summary>
    /// v1.0.0.x: 包裹 user 传入的 <see cref="IProgress{T}"/>,既把 git 流式 stderr 行转发给
    /// VM(用户实时看到 <c>Receiving objects: 80%...</c> 进度),又顺路抓 size 给 done 行用。
    /// user progress == null 时直接 return null(GitRunner 走 capture mode)。
    /// </summary>
    private static IProgress<string>? WrapProgressForSizeTracking(
        IProgress<string>? userProgress,
        PackageSizeHolder sizeHolder)
    {
        if (userProgress == null) return null;
        return new Progress<string>(line =>
        {
            var sz = ExtractSizeFromLine(line);
            if (sz != null) sizeHolder.Size = sz;
            try { userProgress.Report(line); } catch { /* sink to avoid Console crash on bg */ }
        });
    }

    /// <summary>v1.0.0.x: git 流式提取的最近一个 package size(如 <c>"67.89 MiB"</c>)。</summary>
    private sealed class PackageSizeHolder
    {
        public string? Size { get; set; }
    }

    /// <summary>
    /// v1.0.0.x: 一键下载/更新 — 根据 targetDir 是否存在决定走 clone 或 update。
    /// <list type="bullet">
    ///   <item>targetDir 不存在 → <see cref="CloneAsync"/>(首次下载)</item>
    ///   <item>targetDir 已存在 → <see cref="UpdateAsync"/>(wipe + clone,相当于 git pull)</item>
    /// </list>
    /// 用于模板管理页 "下载与更新" 按钮(只动源码,不涉及 env/node)。
    /// </summary>
    public virtual async Task<NodeOperationResult> DownloadOrUpdateAsync(
        string repoUrl,
        string targetDir,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return NodeOperationResult.Fail("repoUrl 不能为空");
        if (string.IsNullOrWhiteSpace(targetDir))
            return NodeOperationResult.Fail("targetDir 不能为空");
        // v1.0.0.x: 锚定到 _basePath (system_template_library_dir,非空时) 或
        // AppContext.BaseDirectory 回退,保证 settings 里的相对路径跟用户实际看到的
        // clone 目标一致(避免双击启动时 CWD 漂到 %USERPROFILE% 把 clone 写到 settings
        // 之外)。
        targetDir = TemplatePathResolver.Resolve(targetDir, _basePath);

        progress?.Report($"[src] 检查目标目录: {targetDir}");
        if (Directory.Exists(targetDir))
        {
            progress?.Report("[src] 目录已存在,执行 wipe + clone (更新)");
            return await UpdateAsync(targetDir, repoUrl, progress, ct);
        }
        progress?.Report("[src] 目录不存在,执行 git clone (首次下载)");
        return await CloneAsync(repoUrl, targetDir, progress, ct);
    }
}
