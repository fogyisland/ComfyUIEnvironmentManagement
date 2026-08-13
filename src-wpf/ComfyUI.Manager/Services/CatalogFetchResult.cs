using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.14: HTTP cache-aware fetch result.
/// <see cref="Is304"/> 为 true 时 <see cref="Entries"/> 为 null,<see cref="NewEtag"/>/<see cref="NewLastModified"/>
/// 可能仍带新值(服务器可能在 304 响应里更新 ETag — RFC 7232 §4.1)。
/// </summary>
public sealed record CatalogFetchResult(
    bool Is304,
    IReadOnlyList<CatalogEntry>? Entries,
    string? NewEtag,
    string? NewLastModified);