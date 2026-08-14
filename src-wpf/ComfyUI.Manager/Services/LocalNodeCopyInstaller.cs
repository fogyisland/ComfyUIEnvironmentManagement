using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15:把本地节点 (LocalNodeDirectory/&lt;nodeId&gt;) 复制到 env 的 custom_nodes/&lt;nodeId&gt;。
/// 复用 NodeOperations.TryReadHeadShaAsync 读 SHA(非 git 仓库 → null 不抛)。
/// 失败路径(目录已存在 / env 缺失 / 复制异常)rollback 删目标目录 + 不写 ScannedNode row。
/// </summary>
public class LocalNodeCopyInstaller
{
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NodeOperations _nodeOps;
    private readonly AppLogger? _logger;

    public LocalNodeCopyInstaller(
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        NodeOperations nodeOps,
        AppLogger? logger = null)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _nodeOps = nodeOps;
        _logger = logger;
    }

    public virtual async Task<NodeOperationResult> InstallAsync(
        string envId, string sourcePath, string nodeId, CancellationToken ct = default)
    {
        _logger?.Info("local-node-copy", $"env='{envId}' node='{nodeId}' src='{sourcePath}' 开始复制");

        var env = _envRepo.Get(envId);
        if (env is null) return NodeOperationResult.Fail($"env '{envId}' 不存在");
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            return NodeOperationResult.Fail("env 缺 custom_nodes_path");
        }
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            return NodeOperationResult.Fail($"源目录不存在:{sourcePath}");
        }

        var targetDir = Path.Combine(env.CustomNodesPath, nodeId);
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"目录已存在:{targetDir}");
        }

        try
        {
            Directory.CreateDirectory(env.CustomNodesPath);
            // recursive copy
            CopyDirectory(sourcePath, targetDir);
        }
        catch (Exception ex)
        {
            // 失败清理目标(可能 copy 半路挂)
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"复制失败:{ex.Message}");
        }

        // 读 HEAD SHA(非 git 仓库 → null,Version = "")
        var headSha = await _nodeOps.TryReadHeadShaAsync(targetDir, ct);

        try
        {
            _nodeRepo.Upsert(new ScannedNode
            {
                Id = nodeId,
                EnvId = envId,
                Package = nodeId,
                PackagePath = targetDir,
                Version = string.IsNullOrEmpty(headSha) ? null : headSha,
                Status = "enabled",
                Source = "env",
                LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            });
        }
        catch (Exception ex)
        {
            // 写 DB 失败 → rollback
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"写 ScannedNode 失败:{ex.Message}");
        }

        _logger?.Info("local-node-copy", $"env='{envId}' node='{nodeId}' 复制成功");
        return NodeOperationResult.Ok(headSha);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
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
}