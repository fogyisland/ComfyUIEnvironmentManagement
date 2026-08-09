namespace ComfyUI.Manager.Search;

/// <summary>
/// Query 返回的 ranked hit:携带原始 entry + 评分 + kind 优先级(tie-break 用)。
/// <para>
/// 三层 tie-break 由 SearchIndex 一次性排好:
///   1. <see cref="Score"/> desc
///   2. <see cref="KindPriority"/> asc
///   3. Entry.DisplayName.Length asc
/// </para>
/// </summary>
public sealed record SearchResult(SearchEntry Entry, int Score, int KindPriority);
