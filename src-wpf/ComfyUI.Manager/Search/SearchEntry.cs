using System;

namespace ComfyUI.Manager.Search;

/// <summary>
/// 搜索索引的一条原始 entry。Build 阶段构造,Index 阶段查询。
/// <para>
/// <see cref="NormalizedTokens"/> 在 Add 前必须填好 — 切词 + 大写归一 + ASCII/CJK 边界分隔,
/// Query 阶段不再重新 tokenize 以保证性能(G7 — 打开 Spotlight 时构建,键入仅走内存)。
/// </para>
/// </summary>
public sealed class SearchEntry
{
    /// <summary>unique id,跨 kind 全局唯一。惯例:`env-{envId}` / `node-{envId}-{nodeId}` / `settings-{key}` / `cmd-{name}`。</summary>
    public string Id { get; init; } = "";

    public TargetKind Kind { get; init; }

    /// <summary>主显示名 — 列表里第一行 / 评分主依据。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>副标(辅助显示,目前不参与评分;预留未来 fuzzy subtitle match)。</summary>
    public string[] Subtitle { get; init; } = Array.Empty<string>();

    /// <summary>导航目标 — T7 用此字段决定点击行为。</summary>
    public SearchTarget Target { get; init; } = new(TargetKind.Environment);

    /// <summary>预归一化 token 列表。Add 时必须填。Query 阶段直接用。</summary>
    public string[] NormalizedTokens { get; init; } = Array.Empty<string>();
}
