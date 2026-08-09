using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// NodeRepository 的抽象接口。只暴露 DashboardService 使用的计数方法和既有查询方法。
/// </summary>
public interface INodeRepository
{
    /// <summary>单 SQL SELECT COUNT(*) FROM scanned_nodes。</summary>
    Task<long> CountAllAsync(CancellationToken ct = default);

    /// <summary>按环境列出已扫描节点。</summary>
    List<ScannedNode> ListByEnv(string envId);

    /// <summary>按 id 取单条;不存在返 null。</summary>
    ScannedNode? Get(string nodeId);
}
