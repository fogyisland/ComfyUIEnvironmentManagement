using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 运行时从 PyPI <c>https://pypi.org/pypi/torch/json</c> +
/// pytorch.org <c>https://pytorch.org/get-started/locally/</c> 解析出来的
/// 单个 PyTorch 发布条目。
///
/// <list type="bullet">
/// <item><see cref="Version"/> + <see cref="ReleaseDate"/> 由 PyPI 提供。</item>
/// <item><see cref="CudaVariants"/> 由 pytorch.org HTML 提供
///   (PyPI 的 <c>torch</c> 包不再发布 CUDA 标记的 wheel,所以
///   CUDA 列表必须从 pytorch.org 的 <c>pt_version_map.release</c> 抽)。</item>
/// <item><see cref="HasCpu"/> 由 PyPI wheel filename 里的 <c>+cpu</c>
///   tag 提供。</item>
/// </list>
///
/// 由 <see cref="PyTorchVersionCatalog"/> 填充,经
/// <c>PyTorchVersionCache</c> 持久化,再由
/// <see cref="BaseEnvProfileLoader"/> 按版本生成 profile 列表。
/// </summary>
/// <remarks>
/// <para><c>init-only</c>:序列化 / 反序列化安全(<c>System.Text.Json</c> 走
/// 公共 setter)。没有业务方法,纯数据载体。</para>
/// <para><see cref="CudaVariants"/> 永远按 cu-tag 数值升序排列
/// (<c>cu118</c> → <c>cu126</c>)。</para>
/// </remarks>
public sealed class PyTorchVersion
{
    /// <summary>
    /// PEP 440 stable 版本号(示例:"2.13.0")。
    /// pre-release / post-release / dev 版本号永远不进 catalog。
    /// </summary>
    public string Version { get; init; } = "";

    /// <summary>
    /// 该版本所有 wheel 文件里出现的最新 <c>upload_time</c>(UTC)。
    /// 作为目录展示时的"发布时间",按这个字段降序排序。
    /// </summary>
    public DateTimeOffset ReleaseDate { get; init; }

    /// <summary>
    /// 该版本可用的 CUDA 索引 tag,按数字升序排列。
    /// 数据源是 pytorch.org 的 <c>pt_version_map.release</c>
    /// (<c>cuda.x</c> / <c>cuda.y</c> / <c>cuda.z</c> → cu118 / cu121 /
    /// cu126 等),不是 PyPI wheel filename。
    /// 示例:<c>["cu118", "cu121", "cu126"]</c>。
    /// </summary>
    public IReadOnlyList<string> CudaVariants { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 该版本是否提供 CPU-only wheel(PyPI 的 wheel <c>filename</c>
    /// 包含 <c>+cpu</c> tag)。
    /// </summary>
    public bool HasCpu { get; init; }
}