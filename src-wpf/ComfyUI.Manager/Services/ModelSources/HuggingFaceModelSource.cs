using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.20:HF stub — 接口占位,SearchAsync 永远返回 empty。
/// v0.6.21+ 实现真正搜索 (HF Hub API + token)。</summary>
public class HuggingFaceModelSource : IModelSource
{
    // v0.6.20 placeholder:enum 只有 CivitAi 一项,这里仍写 CivitAi 以保持编译通过。
    // T4 ModelMarketplaceService 不会 dedup HF (因为 IsEnabled=false 默认跳过)。
    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;  // placeholder
    public string DisplayName => "HuggingFace";
    public bool IsEnabled { get; set; } = false;  // disabled by default in v0.6.20

    public HuggingFaceModelSource()
    {
    }

    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ModelEntry>>(Array.Empty<ModelEntry>());
    }
}
