using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15:本地节点 = <c>Settings.LocalNodeDirectory</c> 下子目录 ∪ <c>scanned_nodes</c>
/// <c>Source="download"</c> 行。两条独立校验,有任一就在列表里。
/// 跨 env 装状态走 SELECT scanned_nodes WHERE package=@nodeId AND env_id != '' AND source='env'。
/// </summary>
public class LocalNodeService
{
    private readonly Settings _settings;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly AppLogger? _logger;

    public LocalNodeService(
        Settings settings,
        NodeRepository nodeRepo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        AppLogger? logger = null)
    {
        _settings = settings;
        _nodeRepo = nodeRepo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _logger = logger;
    }

    /// <summary>v0.6.15:返回本地节点物理目录绝对路径(供 LocalNodeCopyInstaller 调)。
    /// 未配置 LocalNodeDirectory 返 null。</summary>
    public string? GetLocalNodePath(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(_settings.LocalNodeDirectory)) return null;
        return Path.Combine(_settings.LocalNodeDirectory, nodeId);
    }

    public virtual async Task<IReadOnlyList<LocalNodeInfo>> ListAsync(CancellationToken ct)
    {
        var localDir = _settings.LocalNodeDirectory;
        if (string.IsNullOrWhiteSpace(localDir))
        {
            _logger?.Warn("local-node", "LocalNodeDirectory 未配置,返 empty list");
            return Array.Empty<LocalNodeInfo>();
        }

        // 兜底建目录(跟 App.OnStartup 启动期建目录同 pattern)
        try { Directory.CreateDirectory(localDir); }
        catch (Exception ex)
        {
            _logger?.Warn("local-node", $"建本地目录失败:{ex.Message},返 empty");
            return Array.Empty<LocalNodeInfo>();
        }

        // 1) 扫物理子目录
        var physicalIds = new HashSet<string>(StringComparer.Ordinal);
        var physicalSha = new Dictionary<string, string>(StringComparer.Ordinal);
        var physicalPath = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(localDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;
                physicalIds.Add(name);
                physicalPath[name] = dir;
                // 读 HEAD SHA(非 git 仓库 → null,跳过)
                var sha = await _nodeOps.TryReadHeadShaAsync(dir, ct);
                if (!string.IsNullOrEmpty(sha))
                {
                    physicalSha[name] = sha;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn("local-node", $"扫本地目录失败:{ex.Message}");
        }

        // 2) 扫 DB download 行(orphan DB row 也算)
        var dbIds = new HashSet<string>(StringComparer.Ordinal);
        var dbInstallDate = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var dbRepoUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in _nodeRepo.ListDownloadedNodes())
        {
            dbIds.Add(node.Package);  // node.Package = nodeId
            if (DateTime.TryParse(node.LastScannedAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                dbInstallDate[node.Package] = dt;
            }
            // v0.6.15.1:DB 存了 URL 的(新下载的)直接拿,不必再 git config
            if (!string.IsNullOrEmpty(node.RepositoryUrl))
            {
                dbRepoUrl[node.Package] = node.RepositoryUrl;
            }
        }

        // 3) 合并
        var allIds = new HashSet<string>(physicalIds, StringComparer.Ordinal);
        foreach (var id in dbIds) allIds.Add(id);

        // 4) 查跨 env 装(env 名提前 join 一次)
        var envMap = _envRepo.ListAll().ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);
        var result = new List<LocalNodeInfo>(allIds.Count);
        foreach (var id in allIds)
        {
            var envIds = _nodeRepo.GetInstalledEnvIdsByPackage(id);
            var envNames = envIds
                .Select(eid => envMap.TryGetValue(eid, out var n) ? n : eid)
                .ToList();
            // v0.6.15.1:URL 来源 — DB (新下载的) || git remote origin URL (老已下载但 DB 没存)
            // || null (没物理目录也没 DB 的 orphan DB row 也行 — 但 DB row 总是有 URL,这里走第 1 条)
            string? repoUrl = dbRepoUrl.TryGetValue(id, out var u) ? u : null;
            if (repoUrl is null && physicalPath.TryGetValue(id, out var dir))
            {
                repoUrl = await _nodeOps.TryReadRemoteUrlAsync(dir, ct);
            }
            result.Add(new LocalNodeInfo(
                NodeId: id,
                HeadSha: physicalSha.TryGetValue(id, out var s) ? s : null,
                InstallDate: dbInstallDate.TryGetValue(id, out var dt) ? dt : null,
                HasPhysicalDir: physicalIds.Contains(id),
                IsInDb: dbIds.Contains(id),
                InstalledEnvIds: envIds,
                InstalledEnvNames: envNames,
                RepositoryUrl: repoUrl));
        }

        // 按 nodeId 排序(稳定显示)
        result.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
        _logger?.Info("local-node", $"ListAsync 完成 count={result.Count}");
        return result;
    }

    /// <summary>
    /// 删本地节点 = 物理目录 + EnvId="" + Source="download" 的 DB row。
    /// 已装到 env 的行 (EnvId != "", Source="env") 不动。
    /// </summary>
    public virtual async Task<NodeOperationResult> DeleteAsync(string nodeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return NodeOperationResult.Fail("nodeId 不能为空");
        }
        var localDir = _settings.LocalNodeDirectory;
        var dirPath = string.IsNullOrWhiteSpace(localDir)
            ? null
            : Path.Combine(localDir, nodeId);

        var dirExists = dirPath is not null && Directory.Exists(dirPath);
        if (!dirExists)
        {
            // 看 DB 是否有 download 行
            var anyDb = _nodeRepo.ListDownloadedNodes().Any(n => n.Package == nodeId);
            if (!anyDb) return NodeOperationResult.Fail("本地节点不存在");
        }

        if (dirExists)
        {
            TryDelete(dirPath!);
        }
        _nodeRepo.DeleteBySourceAndEnvId(nodeId, "", "download");
        _logger?.Info("local-node", $"删除本地节点 node='{nodeId}'");
        return NodeOperationResult.Ok(null);
    }

    private static void TryDelete(string dir)
    {
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
