using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.20 T9: IModelSource resolver — 聚合 DI 注入的 IEnumerable&lt;IModelSource&gt;,
/// 按 settings per-source toggle 过滤出 enabled 列表。<br/>
/// 注意:v0.6.20 阶段 <see cref="ModelMarketplaceService"/> 不使用本 resolver ——
/// 它内部直接 <c>_sources.Where(s =&gt; s.IsEnabled)</c> 即可(v0.6.20 只有 CivitAI 默认
/// 启用 + HF placeholder 默认禁用,T4 spec 已 working)。本 resolver 保留以便后续 settings
/// 加 toggle(如 <c>ModelSourceCivitAiEnabled</c> 真正 wire 到 source.IsEnabled setter)
/// 时可一行替换:ModelMarketplaceService 改吃 IModelSourceResolver。
/// </summary>
public interface IModelSourceResolver
{
    IReadOnlyList<IModelSource> ResolveEnabled();
}

/// <summary>
/// 默认实现:<see cref="ModelMarketplaceService"/> 注入的所有 source 中,按
/// <see cref="Settings.ModelSourceCivitAiEnabled"/> 决定 CivitAI source,
/// 其余 source 走自身 IsEnabled 属性。返回的列表是 *新快照* —— 调用方修改不影响 DI 缓存。
/// </summary>
public sealed class ModelSourceResolver : IModelSourceResolver
{
    private readonly IEnumerable<IModelSource> _sources;
    private readonly Settings _settings;

    public ModelSourceResolver(IEnumerable<IModelSource> sources, Settings settings)
    {
        _sources = sources ?? System.Array.Empty<IModelSource>();
        _settings = settings;
    }

    public IReadOnlyList<IModelSource> ResolveEnabled()
    {
        return _sources
            .Where(s => s.SourceKind == ModelSourceKind.CivitAi
                ? _settings.ModelSourceCivitAiEnabled
                : s.IsEnabled)
            .ToList();
    }
}