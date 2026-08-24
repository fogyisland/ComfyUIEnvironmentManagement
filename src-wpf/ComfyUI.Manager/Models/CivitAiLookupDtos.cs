using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0 T9a:CivitAI lookup service DTOs — 服务层返给 caller 的干净 record,
/// 不暴露 wire-format 字段。Wire-format DTOs 走 System.Text.Json + snake_case
/// JsonPropertyName,跟 ModelScopeDtos 既有 pattern 对齐(不引入新 nuget)。
///
/// Search result envelope: <c>{ "items": [ ... ] }</c>。
/// Detail envelope: <c>{ id, name, creator, baseModel, description, tags,
/// modelVersions[], images[] }</c>。
/// </summary>

// === Public API surface (consumed by VM/View in T9b) ===
public sealed record CivitAiCandidate(
    int Id,
    string Title,
    string Username,
    string? BaseModel,
    string? ThumbnailUrl);

public sealed record CivitAiDetailDto(
    int Id,
    string Title,
    string Username,
    string? BaseModel,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<CivitAiVersionDto> Versions,
    IReadOnlyList<string> ImageUrls);

public sealed record CivitAiVersionDto(
    string Name,
    string? BaseModel,
    System.DateTime? CreatedAt);

// === Internal wire-format DTOs (System.Text.Json, snake_case via JsonPropertyName) ===
internal sealed class CivitAiSearchResponse
{
    [JsonPropertyName("items")] public List<CivitAiSearchItem>? Items { get; set; }
}

internal sealed class CivitAiSearchItem
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("creator")] public CivitAiCreatorDto? Creator { get; set; }
    [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
    [JsonPropertyName("images")] public List<CivitAiImageDto>? Images { get; set; }
}

internal sealed class CivitAiCreatorDto
{
    [JsonPropertyName("username")] public string? Username { get; set; }
}

internal sealed class CivitAiImageDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

internal sealed class CivitAiDetailResponse
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("creator")] public CivitAiCreatorDto? Creator { get; set; }
    [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("modelVersions")] public List<CivitAiVersionWire>? ModelVersions { get; set; }
    [JsonPropertyName("images")] public List<CivitAiImageDto>? Images { get; set; }
}

internal sealed class CivitAiVersionWire
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
    [JsonPropertyName("createdAt")] public System.DateTime? CreatedAt { get; set; }
}