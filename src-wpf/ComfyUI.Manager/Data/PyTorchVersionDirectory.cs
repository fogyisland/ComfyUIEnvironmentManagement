using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Data;

/// <summary>
/// ComboBox 的一项:虚拟 nightly + 真实 stable。ComboBox 的
/// <c>ItemTemplate</c> 绑 <see cref="DisplayName"/>,后端选择版本号用
/// <see cref="Version"/>(stable 用 <see cref="StableMetadata"/> 取
/// CUDA / CPU 元数据)。
/// </summary>
/// <remarks>
/// <c>init-only</c>:跟上游 <see cref="PyTorchVersion"/> 风格一致,
/// 配 <c>System.Text.Json</c> 走公共 setter,反序列化安全(虽然反
/// 序列化不直接用于本类 — 它是 in-memory 拼装的)。没有业务方法,
/// 纯数据载体。
/// </remarks>
public sealed class PyTorchVersionEntry
{
    /// <summary>
    /// stable = PEP 440 版本号("2.13.0");nightly = 固定字面量
    /// <see cref="PyTorchVersionDirectory.NightlyVersion"/>。
    /// </summary>
    public string Version { get; init; } = "";

    /// <summary>
    /// <c>true</c> = 虚拟 nightly 项;<c>false</c> = stable 项,可信
    /// <see cref="StableMetadata"/>。
    /// </summary>
    public bool IsNightly { get; init; }

    /// <summary>
    /// ComboBox UI 显示文本。stable = <c>"PyTorch X.Y.Z"</c>;
    /// nightly = <c>"PyTorch Nightly"</c>。
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// stable 项的完整元数据(CUDA 变体 + HasCpu + ReleaseDate);
    /// nightly = <c>null</c>,UI 通过 <see cref="IsNightly"/> 判定。
    /// </summary>
    public PyTorchVersion? StableMetadata { get; init; }
}

/// <summary>
/// 给 <c>BaseEnvView</c> 的 ComboBox 用的 PyTorch 版本目录。
///
/// 编排:cache → catalog fetch → 硬编码 v0.6.5.2 fallback 链。
/// <list type="number">
/// <item>先调 <see cref="PyTorchVersionCatalogCache.TryReadAsync"/>:
///   返回 non-null 且 non-empty 列表 → 直接用,不再打远端。</item>
/// <item>否则调 <see cref="PyTorchVersionCatalog.FetchAsync"/>:
///   返回 non-null → 写缓存,然后用。</item>
/// <item>否则用内置 <see cref="BuildFallback"/> 列出的硬编码
///   v0.6.5.2 兼容元数据(NOT 写入缓存,避免脏写)。</item>
/// </list>
///
/// 无论走哪条路径,结果的 <c>[0]</c> 永远是 nightly 虚拟项
/// (<see cref="NightlyVersion"/>, <see cref="PyTorchVersionEntry.IsNightly"/> = true,
/// <c>StableMetadata</c> = null),ComboBox 才能永远把 nightly 选项暴露出来。
/// 后面是 release date 降序的 stable 项,<c>DisplayName</c> = "PyTorch X.Y.Z"。
/// </summary>
/// <summary>
/// 非 sealed:<see cref="GetAllAsync"/> 标 <c>virtual</c> 允许测试
/// 用 in-memory 子类替换,避开真 <c>HttpClient</c> / 真 disk cache。
/// 跟 <see cref="PyTorchVersionCatalog"/> / <see cref="PyTorchVersionCatalogCache"/>
/// 保持一致的 testing seam 风格。
/// </summary>
public class PyTorchVersionDirectory
{
    /// <summary>
    /// nightly 虚拟条目用的 <see cref="PyTorchVersionEntry.Version"/>
    /// 字面量。等价于 PyPI 跟 PyTorch 官方 nightly wheel 的 channel 名。
    /// </summary>
    public const string NightlyVersion = "nightly";

    private readonly PyTorchVersionCatalog _catalog;
    private readonly PyTorchVersionCatalogCache _cache;

    public PyTorchVersionDirectory(PyTorchVersionCatalog catalog, PyTorchVersionCatalogCache cache)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// 拉一次版本目录。永远不为 null:失败路径都走 fallback,
    /// 调用方 UI 不需要为 null 单独排版。
    /// </summary>
    /// <remarks>
    /// 永远在 <c>[0]</c> 放 nightly 虚拟项,所以 ComboBox 的 "nightly" 选项
    /// 即使 cache hit / fetch success 也在可见位置。空 cache(返回非 null
    /// 但空列表)按 cache miss 处理 — 调 catalog — 防止"曾经空过一次"
    /// 永久卡死的死锁。
    /// </remarks>
    public virtual async Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(CancellationToken ct = default)
    {
        // 1. 试 cache。
        var cached = await _cache.TryReadAsync(ct).ConfigureAwait(false);

        IReadOnlyList<PyTorchVersion>? resolved = null;
        if (cached is { Count: > 0 })
        {
            // cache hit — 不用调 catalog。
            resolved = cached;
        }
        else
        {
            // 2. cache miss (null OR empty list) → 调 catalog。
            var fetched = await _catalog.FetchAsync(ct).ConfigureAwait(false);
            if (fetched is not null)
            {
                // 3a. fetch success → 写 cache 然后用。
                await _cache.WriteAsync(fetched, ct).ConfigureAwait(false);
                resolved = fetched;
            }
            // 3b. fetch 也 null → 继续到 fallback(下面 resolved 仍 null)。
        }

        // 4. fallback:cache miss 且 fetch 也失败 / 返回 null。
        if (resolved is null)
        {
            resolved = BuildFallback();
            // 注意:fallback 不写 cache(v0.6.5.2 之前的硬编码值,
            // 跟同时间的 v0.6.5.2 真实 PyPI data 可能不一样,留住远端
            // 恢复后的 "next time it'll work" 期望)。
        }

        // 5. 装配 nightly 虚拟项 + stable 项(release date desc)。
        var nightly = new PyTorchVersionEntry
        {
            Version = NightlyVersion,
            IsNightly = true,
            DisplayName = "PyTorch Nightly",
            StableMetadata = null,
        };

        var stableEntries = resolved
            .OrderByDescending(v => v.ReleaseDate)
            .ThenByDescending(v => v.Version, StringComparer.Ordinal)
            .Select(v => new PyTorchVersionEntry
            {
                Version = v.Version,
                IsNightly = false,
                DisplayName = "PyTorch " + v.Version,
                StableMetadata = v,
            })
            .ToArray();

        var result = new PyTorchVersionEntry[stableEntries.Length + 1];
        result[0] = nightly;
        Array.Copy(stableEntries, 0, result, 1, stableEntries.Length);
        return result;
    }

    /// <summary>
    /// 内置 v0.6.5.2 兼容 fallback。跟 v0.6.5.2 真实
    /// <c>pt_published_versions.latest_stable</c> 对齐:<c>2.13.0</c>
    /// 是 stable,CUDA 变体 <c>cu118 / cu121 / cu124 / cu126</c>,
    /// CPU wheel = true。日期 = 一个占位常量(用户原话 "fallback 是
    /// 硬编码默认值就好,不需要真时间")— 用 <c>DateTimeOffset.MinValue</c>
    /// 加一年当 sentinel,确保 release date 排序仍能 deterministic 输出。
    /// </summary>
    /// <remarks>
    /// 不通过 <see cref="System.Reflection"/> / 私有字段共享:这条路径
    /// 在 PyPI 离线 / 失败时是唯一信息源,必须 unconditional 给一个
    /// non-empty 列表。 <c>nightly</c> 是单独的 <see cref="GetAllAsync"/>
    /// 头部条目,不放在这里。
    /// </remarks>
    private static IReadOnlyList<PyTorchVersion> BuildFallback()
    {
        // 占位日期:不是真实 release date,是 fallback sentinel。
        // 任意用户打开 offline UI 都拿到相同日期,排序结果 deterministic。
        var fallbackRelease = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        return new[]
        {
            new PyTorchVersion
            {
                Version = "2.13.0",
                ReleaseDate = fallbackRelease,
                CudaVariants = new[] { "cu118", "cu121", "cu124", "cu126" },
                HasCpu = true,
            },
        };
    }
}
