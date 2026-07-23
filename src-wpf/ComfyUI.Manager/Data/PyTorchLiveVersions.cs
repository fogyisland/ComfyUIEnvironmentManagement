using System;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 运行时从 pytorch.org 拉取到的 PyTorch 版本快照。
/// 由 <c>PyTorchVersionFetcher</c> 解析 HTML 后填充,经
/// <c>PyTorchVersionCache</c> 持久化到
/// <c>%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json</c>,
/// 再由 <c>BaseEnvProfileLoader</c> 用来生成默认 profile 列表。
/// </summary>
/// <remarks>
/// init-only:序列化 / 反序列化安全(System.Text.Json 走公共 setter 或 ctor);
/// 没有业务方法,纯数据载体。
/// </remarks>
public sealed class PyTorchLiveVersions
{
    /// <summary>
    /// 当前 stable 版本号(示例:"2.13.0")。
    /// 5 个 stable profile(cu118/cu121/cu124/cu126/cpu)共用此值。
    /// 默认空串:fetcher 解析失败时上层应回退到 hardcoded defaults,不应用此空值。
    /// </summary>
    public string Stable { get; init; } = "";

    /// <summary>
    /// pytorch.org HTML 内 <c>pt_version_map.nightly.cuda.x</c> 存在标记。
    /// 当前 nightly 唯一活 CUDA 索引 = cu126(x = 12.6);PyTorch 撤掉时变 false,
    /// 上层回退 hardcoded(nightly 走 cu121)。
    /// </summary>
    public bool HasNightlyCu126 { get; init; } = true;

    /// <summary>
    /// 拉取时间(UTC)。cache 用此字段判断 1h TTL 是否过期。
    /// </summary>
    public DateTimeOffset FetchedAt { get; init; }
}