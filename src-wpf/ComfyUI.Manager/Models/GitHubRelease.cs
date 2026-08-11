using System;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.11+ dashboard/splash polish:GitHub release API 单条 record。
/// JSON 字段映射走显式 <see cref="JsonPropertyNameAttribute"/> — GitHub API 用 snake_case
/// ('tag_name' / 'published_at' / 'html_url'),而 'prerelease' 没有 'is_' 前缀,
/// 所以不能靠 naming policy 统一推导。cache 文件也用同一套 name。
/// </summary>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("published_at")] DateTime PublishedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("prerelease")] bool IsPrerelease);
