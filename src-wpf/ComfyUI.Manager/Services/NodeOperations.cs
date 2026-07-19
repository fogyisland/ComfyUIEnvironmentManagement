using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
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

    public NodeOperations(
        GitRunner git,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        Settings settings)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
        CancellationToken ct = default)
    {
        var env = RequireEnv(envId);
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

        _nodeRepo.Upsert(new ScannedNode
        {
            Id = nodeId,
            EnvId = envId,
            Package = nodeId,
            PackagePath = targetDir,
            Version = headSha,
            Status = "enabled",
            LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });
        return NodeOperationResult.Ok(headSha);
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
            try { _nodeRepo.Upsert(node); } catch { }
        }
        return NodeOperationResult.Ok(headSha);
    }

    /// <summary>
    /// git reset --hard &lt;sha&gt;。用于 rollback 到指定版本。
    /// </summary>
    public virtual async Task<NodeOperationResult> RollbackAsync(
        string envId, string nodeId, string sha,
        CancellationToken ct = default)
    {
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
        try { _nodeRepo.Upsert(node); } catch { }
        return NodeOperationResult.Ok(sha);
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

    private async Task<string?> TryReadHeadShaAsync(string workdir, CancellationToken ct)
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