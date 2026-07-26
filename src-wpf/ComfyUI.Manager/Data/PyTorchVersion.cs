using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 运行时从 PyPI <c>https://pypi.org/pypi/torch/json</c> 解析出来的单个
/// PyTorch 发布条目。由 <see cref="PyTorchVersionCatalog.Parse"/> 填充,
/// 经 <c>PyTorchVersionCache</c> 持久化,再由
/// <see cref="BaseEnvProfileLoader"/> 按版本生成 profile 列表。
/// </summary>
/// <remarks>
/// <para><c>init-only</c>:序列化 / 反序列化安全(<c>System.Text.Json</c> 走
/// 公共 setter)。没有业务方法,纯数据载体。</para>
/// <para><c>CudaVariants</c> 永远按 cu-tag 数值升序排列
/// (<c>cu118</c> → <c>cu126</c>);同一 tag 重复出现会被
/// <see cref="PyTorchVersionCatalog.Parse"/> 去重。</para>
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
    /// 该版本可用的 CUDA 索引 tag,按数字升序排列(去重后)。
    /// 示例:<c>["cu118", "cu121", "cu124", "cu126"]</c>。
    /// </summary>
    public IReadOnlyList<string> CudaVariants { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 该版本是否提供 CPU-only wheel(<c>+cpu</c> tag 出现过)。
    /// </summary>
    public bool HasCpu { get; init; }
}