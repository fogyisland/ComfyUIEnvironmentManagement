using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15:本地节点一条记录(物理目录 + DB row 合并视图)。
/// NodeId 是包名 = 目录名,等同 ScannedNode.Package。
/// 跨 env 状态通过 SELECT scanned_nodes WHERE package=@nodeId AND env_id != '' AND source='env' 查。
/// </summary>
public sealed record LocalNodeInfo(
    string NodeId,
    string? HeadSha,
    DateTime? InstallDate,
    bool HasPhysicalDir,
    bool IsInDb,
    IReadOnlyList<string> InstalledEnvIds,
    IReadOnlyList<string> InstalledEnvNames);
