using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// NodeOperations:节点级 git 操作 + SQLite 状态写入。
///
/// 每个 (envId, nodeId) 操作都是一个 git 命令 + 一条 ScannedNode row 写入。
/// 串行;不并发(同 env 下避免 git index 锁竞争)。
///
/// 与 BulkUpdateOrchestrator 的区别:
/// - BulkUpdate:跨 env × node 网格,emit Progress 事件
/// - NodeOperations:单 (env, node) 操作,直接返回 NodeOperationResult
///
/// 返回的 reason 字段约定:
/// - null:成功
/// - "timeout" / "用户取消":RunAsync 抛 OperationCanceledException,转译
/// - "<stderr 首行>":git 失败
/// - "<异常信息>":启动失败
/// </summary>
public class NodeOperations
{
    private static readonly TimeSpan DefaultPerCallTimeout = TimeSpan.FromSeconds(60);

    private readonly GitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly Settings _settings;
    private readonly NodeInstallDiffService _diffService;
    private readonly Func<NodeInstallDiffReport, Models.Environment, string, bool> _showDiffDialog;
    private readonly AppLogger? _logger;

    public NodeOperations(
        GitRunner git,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        Settings settings,
        NodeInstallDiffService diffService,
        Func<NodeInstallDiffReport, Models.Environment, string, bool>? showDiffDialog = null,
        AppLogger? logger = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _diffService = diffService ?? throw new ArgumentNullException(nameof(diffService));
        _showDiffDialog = showDiffDialog ?? ShowDiffWarningDialogImpl;
        _logger = logger;
    }

    /// <summary>
    /// 默认 modal 显示:弹 <see cref="NodeInstallDiffWarningDialog"/> 拿用户的 Proceed/Cancel。
    /// 拆成 static delegate 是为了测试可注入 fake(UI 测试里 Application.Current 没有)。
    /// </summary>
    private static bool ShowDiffWarningDialogImpl(
        NodeInstallDiffReport report, Models.Environment env, string nodeId)
    {
        var vm = new NodeInstallDiffWarningViewModel(report, nodeId, env.Name);
        var dlg = new NodeInstallDiffWarningDialog(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        dlg.ShowDialog();
        return vm.Proceed;
    }

    /// <summary>
    /// git clone &lt;repoUrl&gt; &lt;customNodesPath/nodeId&gt;。
    ///
    /// 如果 <paramref name="targetTag"/> 非空,clone 完再 <c>git checkout &lt;tag&gt;</c>
    /// 钉到指定 tag / sha。
    ///
    /// 完成后:
    /// - 节点目录已存在
    /// - upsert 一条 ScannedNode row(status=enabled, version=HEAD sha)
    /// </summary>
    public virtual async Task<NodeOperationResult> InstallAsync(
        string envId, string nodeId, string repoUrl,
        string? targetTag = null,
        IReadOnlyList<PipRequirement>? catalogPipReqs = null,
        CancellationToken ct = default)
    {
        _logger?.Info("node-install", $"env='{envId}' node='{nodeId}' 开始安装");
        var env = RequireEnv(envId);

        // v0.6.7.5: Pre-clone diff check(可选 — 仅当 caller 传 catalogPipReqs 时跑)
        if (catalogPipReqs is not null && catalogPipReqs.Count > 0
            && !string.IsNullOrEmpty(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            var report = await _diffService.CheckAsync(env, catalogPipReqs, ct);
            if (report.Warnings.Count > 0)
            {
                bool proceed = _showDiffDialog(report, env, nodeId);
                if (!proceed)
                {
                    _logger?.Info("node-install",
                        $"env='{envId}' node='{nodeId}' 用户取消 diff warning(检测到 {report.Warnings.Count} 条)");
                    return NodeOperationResult.Fail("用户取消(diff warning)");
                }
                _logger?.Info("node-install",
                    $"env='{envId}' node='{nodeId}' 用户接受 {report.Warnings.Count} 条 diff warning,继续");
            }
        }

        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            return NodeOperationResult.Fail("env 缺 custom_nodes_path");
        }
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            // 回落到 active download source 的 URL 模板
            var activeName = _settings.ActiveDownloadSourceName;
            var src = _settings.DownloadSources.FirstOrDefault(s => s.Name == activeName);
            if (src is null || string.IsNullOrWhiteSpace(src.Url))
            {
                return NodeOperationResult.Fail("未配置下载源,请在 Settings 添加");
            }
            repoUrl = NodeUrlResolver.Resolve(src.Url, nodeId);
            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                return NodeOperationResult.Fail("下载源 URL 解析为空");
            }
        }

        var targetDir = Path.Combine(env.CustomNodesPath, nodeId);
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"目录已存在:{targetDir}");
        }
        Directory.CreateDirectory(env.CustomNodesPath);

        GitResult result;
        try
        {
            result = await _git.RunAsync(
                env.CustomNodesPath,
                new[] { "clone", "--", repoUrl, nodeId },
                DefaultPerCallTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!result.Ok)
        {
            // 失败时尝试清掉空目录(可能 clone 失败前 mkdir 了一个)
            try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true); } catch { }
            return NodeOperationResult.Fail(FirstLine(result.Stderr, result.Stdout)
                ?? $"git 退出码 {result.ExitCode}");
        }

        // 可选:钉到指定 tag / sha(详情面板下拉选的版本)
        // 注意:不能用 "--" (会变成 pathspec),直接传 ref 让 git 自己解析
        if (!string.IsNullOrWhiteSpace(targetTag))
        {
            GitResult checkoutResult;
            try
            {
                checkoutResult = await _git.RunAsync(
                    targetDir,
                    new[] { "checkout", targetTag },
                    DefaultPerCallTimeout, ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(targetDir);
                return NodeOperationResult.Fail("用户取消");
            }
            catch (Exception ex)
            {
                TryDelete(targetDir);
                return NodeOperationResult.Fail($"启动 git checkout 失败:{ex.Message}");
            }

            if (!checkoutResult.Ok)
            {
                var reason = FirstLine(checkoutResult.Stderr, checkoutResult.Stdout)
                    ?? $"git checkout 退出码 {checkoutResult.ExitCode}";
                TryDelete(targetDir);
                return NodeOperationResult.Fail($"checkout {targetTag} 失败:{reason}");
            }
        }

        // 取 HEAD sha 作为 version
        var headSha = await TryReadHeadShaAsync(targetDir, ct);

        // 顺手记 installed_tag(若有),picker 拿来显示"v1.2.3"而不是 raw sha
        var installedTag = await TryReadInstalledTagAsync(targetDir, ct);
        var scanMeta = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(installedTag))
        {
            scanMeta["installed_tag"] = installedTag;
        }

        _nodeRepo.Upsert(new ScannedNode
        {
            Id = nodeId,
            EnvId = envId,
            Package = nodeId,
            PackagePath = targetDir,
            Version = headSha,
            Status = "enabled",
            ScanMeta = scanMeta,
            LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Source = "env",
        });
        _logger?.Info("node-install", $"env='{envId}' node='{nodeId}' 安装成功 sha={(headSha is null ? "?" : headSha[..Math.Min(8, headSha.Length)])} tag={(installedTag ?? "-")}");
        return NodeOperationResult.Ok(headSha);
    }

    /// <summary>
    /// git clone &lt;repoUrl&gt; &lt;localDir/nodeId&gt;。纯下载,目标目录来自 Settings 的本地节点目录
    /// 而不是某个 env 的 custom_nodes。成功后 upsert 一行 ScannedNode:
    /// <c>EnvId=""</c>(sentinel,非 env-specific)+ <c>Source="download"</c>,
    /// 让 Dashboard / 列表 / 状态面板的节点计数把本地下载算进去。
    /// 失败 / 取消路径不写库(语义:没真下载成功就不算节点)。
    ///
    /// <paramref name="targetTag"/> 非空时:clone 完再 <c>git checkout &lt;tag&gt;</c>。
    ///
    /// 失败语义跟 <see cref="InstallAsync"/> 一致:用户取消 → "用户取消",
    /// git 退出非零 → stderr 首行,启动失败 → 异常消息。
    /// </summary>
    public virtual async Task<NodeOperationResult> DownloadAsync(
        string localDir, string nodeId, string repoUrl,
        string? targetTag = null,
        CancellationToken ct = default)
    {
        _logger?.Info("node-download", $"dir='{localDir}' node='{nodeId}' 开始下载");

        // 本地目录没配是常见的用户态错误,返 Fail 让 UI 弹提示,不抛异常
        if (string.IsNullOrWhiteSpace(localDir))
        {
            return NodeOperationResult.Fail("本地节点目录为空,请先在 Settings 配置");
        }
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return NodeOperationResult.Fail("node id 不能为空");
        }

        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            // 回落到 active download source 的 URL 模板(同 InstallAsync)
            var activeName = _settings.ActiveDownloadSourceName;
            var src = _settings.DownloadSources.FirstOrDefault(s => s.Name == activeName);
            if (src is null || string.IsNullOrWhiteSpace(src.Url))
            {
                return NodeOperationResult.Fail("未配置下载源,请在 Settings 添加");
            }
            repoUrl = NodeUrlResolver.Resolve(src.Url, nodeId);
            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                return NodeOperationResult.Fail("下载源 URL 解析为空");
            }
        }

        Directory.CreateDirectory(localDir);
        var targetDir = Path.Combine(localDir, nodeId);
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"目录已存在:{targetDir}");
        }

        GitResult result;
        try
        {
            result = await _git.RunAsync(
                localDir,
                new[] { "clone", "--", repoUrl, nodeId },
                DefaultPerCallTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!result.Ok)
        {
            // clone 失败前可能已经 mkdir 了一个空目录,清掉避免挡住重试
            TryDelete(targetDir);
            return NodeOperationResult.Fail(FirstLine(result.Stderr, result.Stdout)
                ?? $"git 退出码 {result.ExitCode}");
        }

        // 可选:钉到指定 tag / sha
        // 注意:不能用 "--" (会变成 pathspec),直接传 ref 让 git 自己解析
        if (!string.IsNullOrWhiteSpace(targetTag))
        {
            GitResult checkoutResult;
            try
            {
                checkoutResult = await _git.RunAsync(
                    targetDir,
                    new[] { "checkout", targetTag },
                    DefaultPerCallTimeout, ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(targetDir);
                return NodeOperationResult.Fail("用户取消");
            }
            catch (Exception ex)
            {
                TryDelete(targetDir);
                return NodeOperationResult.Fail($"启动 git checkout 失败:{ex.Message}");
            }

            if (!checkoutResult.Ok)
            {
                var reason = FirstLine(checkoutResult.Stderr, checkoutResult.Stdout)
                    ?? $"git checkout 退出码 {checkoutResult.ExitCode}";
                TryDelete(targetDir);
                return NodeOperationResult.Fail($"checkout {targetTag} 失败:{reason}");
            }
        }

        // 取 HEAD sha 作为 version;targetTag 选了时优先记 tag(用户意图)
        var downloadedSha = await TryReadHeadShaAsync(targetDir, ct);
        var versionToRecord = !string.IsNullOrWhiteSpace(targetTag)
            ? targetTag
            : downloadedSha;

        // v0.6.11:成功路径写 ScannedNode — EnvId=""(sentinel,下载到 local,非 env-specific)
        // + Source="download"。原 UNIQUE(env_id, package) 对 "" + "" 同 package 会冲突,
        // 但新唯一索引 (env_id, package, source) 让 download 行独立,覆盖式 upsert 同 id。
        // v0.6.15.1 hotfix:同时存 repository_url,LocalNodeListView card 显示用。
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = nodeId,
            EnvId = "",
            Package = nodeId,
            PackagePath = targetDir,
            Version = versionToRecord,
            Status = "enabled",
            LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Source = "download",
            RepositoryUrl = repoUrl,
        });
        _logger?.Info("node-download",
            $"dir='{localDir}' node='{nodeId}' 下载成功 version={(versionToRecord is null ? "?" : versionToRecord[..Math.Min(8, versionToRecord.Length)])}");
        return NodeOperationResult.Ok(versionToRecord);
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // git 在 .git/objects/pack/ 下的 pack/idx 经常是 readonly,
                // Directory.Delete 在 Windows 上会"Access denied"。先清 attribute 再删。
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// git pull --ff-only。失败时不影响 row(upgrade 不写库 —— 由 UI 决定要不要刷新)。
    /// </summary>
    public virtual async Task<NodeOperationResult> UpgradeAsync(
        string envId, string nodeId, CancellationToken ct = default)
    {
        _logger?.Info("node-upgrade", $"env='{envId}' node='{nodeId}' 开始升级");
        var node = _nodeRepo.Get(nodeId);
        if (node is null || string.IsNullOrWhiteSpace(node.PackagePath))
        {
            return NodeOperationResult.Fail("node 未注册或缺 PackagePath");
        }
        if (!Directory.Exists(node.PackagePath))
        {
            return NodeOperationResult.Fail("目录不存在");
        }

        GitResult result;
        try
        {
            result = await _git.RunAsync(
                node.PackagePath,
                new[] { "pull", "--ff-only" },
                DefaultPerCallTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!result.Ok)
        {
            return NodeOperationResult.Fail(FirstLine(result.Stderr, result.Stdout)
                ?? $"git 退出码 {result.ExitCode}");
        }

        var headSha = await TryReadHeadShaAsync(node.PackagePath, ct);
        if (!string.IsNullOrWhiteSpace(headSha))
        {
            node.Version = headSha;
            node.LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            // 顺手刷 installed_tag(升级后可能落在 tag 上)
            var installedTag = await TryReadInstalledTagAsync(node.PackagePath, ct);
            if (!string.IsNullOrEmpty(installedTag))
            {
                node.ScanMeta ??= new Dictionary<string, string>();
                node.ScanMeta["installed_tag"] = installedTag;
            }
            try { _nodeRepo.Upsert(node); } catch { }
        }
        _logger?.Info("node-upgrade", $"env='{envId}' node='{nodeId}' 升级成功");
        return NodeOperationResult.Ok(headSha);
    }

    /// <summary>
    /// git reset --hard &lt;sha&gt;。用于 rollback 到指定版本。
    /// </summary>
    public virtual async Task<NodeOperationResult> RollbackAsync(
        string envId, string nodeId, string sha,
        CancellationToken ct = default)
    {
        _logger?.Info("node-rollback", $"env='{envId}' node='{nodeId}' 开始回滚 sha={sha}");
        if (string.IsNullOrWhiteSpace(sha))
        {
            return NodeOperationResult.Fail("sha 不能为空");
        }
        var node = _nodeRepo.Get(nodeId);
        if (node is null || string.IsNullOrWhiteSpace(node.PackagePath))
        {
            return NodeOperationResult.Fail("node 未注册或缺 PackagePath");
        }
        if (!Directory.Exists(node.PackagePath))
        {
            return NodeOperationResult.Fail("目录不存在");
        }

        GitResult result;
        try
        {
            result = await _git.RunAsync(
                node.PackagePath,
                new[] { "reset", "--hard", sha },
                DefaultPerCallTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!result.Ok)
        {
            return NodeOperationResult.Fail(FirstLine(result.Stderr, result.Stdout)
                ?? $"git 退出码 {result.ExitCode}");
        }

        node.Version = sha;
        node.LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        try { _nodeRepo.Upsert(node); } catch { }
        _logger?.Info("node-rollback", $"env='{envId}' node='{nodeId}' 回滚成功 sha={sha}");
        return NodeOperationResult.Ok(sha);
    }

    /// <summary>
    /// 扫描一个 node 的 git 状态:读 HEAD sha + 写到 ScannedNode row。
    /// 纯 SQLite + git log,不动 UI 状态字段。
    /// </summary>
    public virtual async Task<NodeOperationResult> ScanAsync(
        string envId, string nodeId, CancellationToken ct = default)
    {
        var node = _nodeRepo.Get(nodeId);
        if (node is null || string.IsNullOrWhiteSpace(node.PackagePath))
        {
            return NodeOperationResult.Fail("node 未注册或缺 PackagePath");
        }
        if (!Directory.Exists(node.PackagePath))
        {
            return NodeOperationResult.Fail("目录不存在");
        }

        var sha = await TryReadHeadShaAsync(node.PackagePath, ct);
        if (string.IsNullOrWhiteSpace(sha))
        {
            return NodeOperationResult.Fail("读 HEAD sha 失败");
        }
        node.Version = sha;
        node.LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var installedTag = await TryReadInstalledTagAsync(node.PackagePath, ct);
        if (!string.IsNullOrEmpty(installedTag))
        {
            node.ScanMeta ??= new Dictionary<string, string>();
            node.ScanMeta["installed_tag"] = installedTag;
        }
        try { _nodeRepo.Upsert(node); } catch { }
        return NodeOperationResult.Ok(sha);
    }

    /// <summary>
    /// 删除已装节点:删目录 + 删 ScannedNode row。
    /// 失败语义:
    /// - env 不存在 → Fail("env 不存在")
    /// - node row 不存在 → Fail("节点未注册")
    /// - 删目录失败 → Fail("删目录失败:{ex.Message}")
    /// - 成功 → Ok(原 version,让 caller 看是哪个 sha 被删的)
    ///
    /// 复用私有 <see cref="TryDelete(string)"/> 处理 Windows readonly pack/idx 文件。
    /// 目录不存在仍删 row(避免 "uninstall 永远 unregister 不了")。
    /// </summary>
    public virtual async Task<NodeOperationResult> UninstallAsync(
        string envId, string nodeId, CancellationToken ct = default)
    {
        _logger?.Info("node-uninstall", $"env='{envId}' node='{nodeId}' 开始卸载");
        var env = _envRepo.Get(envId);
        if (env is null) return NodeOperationResult.Fail("env 不存在");

        var node = _nodeRepo.Get(nodeId);
        if (node is null) return NodeOperationResult.Fail("节点未注册");

        var targetDir = !string.IsNullOrWhiteSpace(node.PackagePath)
            ? node.PackagePath
            : Path.Combine(env.CustomNodesPath ?? "", nodeId);

        if (Directory.Exists(targetDir))
        {
            try { TryDelete(targetDir); }
            catch (Exception ex)
            {
                return NodeOperationResult.Fail($"删目录失败:{ex.Message}");
            }
        }

        _nodeRepo.Delete(nodeId);
        _logger?.Info("node-uninstall", $"env='{envId}' node='{nodeId}' 卸载成功");
        return NodeOperationResult.Ok(node.Version);
    }

    public virtual void Lock(string nodeId)
    {
        _nodeRepo.SetLocked(nodeId, true);
    }

    public virtual void Unlock(string nodeId)
    {
        _nodeRepo.SetLocked(nodeId, false);
    }

    public virtual void Enable(string nodeId)
    {
        _nodeRepo.SetStatus(nodeId, "enabled");
    }

    public virtual void Disable(string nodeId)
    {
        _nodeRepo.SetStatus(nodeId, "disabled");
    }

    // -------- helpers --------

    private Environment RequireEnv(string envId)
    {
        var env = _envRepo.Get(envId)
            ?? throw new InvalidOperationException($"env '{envId}' 不存在");
        return env;
    }

    /// <summary>
    /// v0.6.15:改 internal 给 LocalNodeService.ListAsync 复用(读本地节点目录的 HEAD SHA,
    /// 给 LocalNodeInfo.HeadSha)。不走 git 仓库 → 返 null 不抛。
    /// </summary>
    internal async Task<string?> TryReadHeadShaAsync(string workdir, CancellationToken ct)
    {
        try
        {
            var r = await _git.RunAsync(
                workdir,
                new[] { "rev-parse", "HEAD" },
                TimeSpan.FromSeconds(10), ct);
            if (!r.Ok) return null;
            return r.Stdout.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v0.6.15.1 hotfix:读节点 remote origin URL(<c>git config --get remote.origin.url</c>)。
    /// 非 git 目录 / 没设 origin / 命令失败 → 返 null,不抛。
    /// 用于 LocalNodeService 给老已下载的 node (DB 无 repository_url) 兜底拿 URL。
    /// </summary>
    internal async Task<string?> TryReadRemoteUrlAsync(string workdir, CancellationToken ct)
    {
        try
        {
            var r = await _git.RunAsync(
                workdir,
                new[] { "config", "--get", "remote.origin.url" },
                TimeSpan.FromSeconds(10), ct);
            if (!r.Ok) return null;
            var url = r.Stdout.Trim();
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读节点当前所在 tag(<c>git describe --tags --abbrev=0</c>)。
    /// 没打 tag / 非 git 目录 / 命令失败 → 返 null,不抛。
    /// </summary>
    private async Task<string?> TryReadInstalledTagAsync(string workdir, CancellationToken ct)
    {
        try
        {
            var r = await _git.RunAsync(
                workdir,
                new[] { "describe", "--tags", "--abbrev=0" },
                TimeSpan.FromSeconds(10), ct);
            if (!r.Ok) return null;
            var tag = r.Stdout.Trim();
            return string.IsNullOrEmpty(tag) ? null : tag;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var nlIdx = text.IndexOf('\n');
            var first = nlIdx >= 0 ? text[..nlIdx] : text;
            first = first.Trim();
            if (first.Length > 0) return first;
        }
        return null;
    }
}

public sealed record NodeOperationResult(bool Success, string? Reason, string? Version)
{
    public static NodeOperationResult Ok(string? version) => new(true, null, version);
    public static NodeOperationResult Fail(string reason) => new(false, reason, null);
}