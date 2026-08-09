using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Search;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.9 T7:Spotlight 全局搜索 service 接口。
/// <para>
/// T6 implementer 留下 sealed <see cref="GlobalSearchService"/>,本 task 抽接口
/// 让 <c>SpotlightSearchViewModel</c> 通过 DI 拿到 contract 而不是 concrete class —
/// 测试可以注入 stub 返回受控的 <see cref="SearchIndex"/>(同 T4 <c>INodeRepository</c>
/// 抽取模式)。
/// </para>
/// </summary>
public interface IGlobalSearchService
{
    /// <summary>
    /// 异步构建搜索索引。Env + Node 用真实 DB;Settings 章节 + Commands 走静态列表。
    /// 索引最大 <see cref="SearchIndex.MaxEntries"/> 项,超出截断。
    /// </summary>
    Task<SearchIndex> BuildAsync(CancellationToken ct = default);
}