using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.22.x:ModelScope /api/v1/models response DTO。
/// envelope = { Code, Data: { Model: { Models[], PageNumber, PageSize, TotalCount } } }。
/// snake_case 字段全部 [JsonPropertyName] 显式绑(防 server 改 casing 时静默坏)。
/// </summary>
public static class ModelScopeDtos
{
    public sealed class ModelsResponse
    {
        [JsonPropertyName("Code")] public int Code { get; init; }
        [JsonPropertyName("Data")] public ModelsData? Data { get; init; }
    }
    public sealed class ModelsData
    {
        [JsonPropertyName("Model")] public ModelsPage? Model { get; init; }
    }
    public sealed class ModelsPage
    {
        [JsonPropertyName("PageNumber")] public int PageNumber { get; init; }
        [JsonPropertyName("PageSize")] public int PageSize { get; init; }
        [JsonPropertyName("TotalCount")] public int TotalCount { get; init; }
        [JsonPropertyName("Models")] public List<ModelItem> Models { get; init; } = new();
    }
    public sealed class ModelItem
    {
        [JsonPropertyName("Id")] public long Id { get; init; }
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("ChineseName")] public string? ChineseName { get; init; }
        [JsonPropertyName("Tags")] public List<string> Tags { get; init; } = new();
        [JsonPropertyName("Downloads")] public int Downloads { get; init; }
        [JsonPropertyName("Stars")] public int Stars { get; init; }
        [JsonPropertyName("Likes")] public int Likes { get; init; }
        [JsonPropertyName("Description")] public string? Description { get; init; }
        [JsonPropertyName("Task")] public string? Task { get; init; }
        [JsonPropertyName("Owner")] public OwnerInfo? Owner { get; init; }
        [JsonPropertyName("DefaultRevision")] public string DefaultRevision { get; init; } = "master";
    }
    public sealed class OwnerInfo
    {
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("DisplayName")] public string? DisplayName { get; init; }
    }

    /// <summary>单 model 详情 response — /api/v1/models/{id}。
    /// 用 Revision[0].Files[0] 取 PrimaryDownloadUrl + Size。</summary>
    public sealed class ModelDetailResponse
    {
        [JsonPropertyName("Code")] public int Code { get; init; }
        [JsonPropertyName("Data")] public ModelDetail? Data { get; init; }
    }
    public sealed class ModelDetail
    {
        [JsonPropertyName("Id")] public long Id { get; init; }
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("Revision")] public List<RevisionInfo> Revision { get; init; } = new();
    }
    public sealed class RevisionInfo
    {
        [JsonPropertyName("RevisionId")] public string? RevisionId { get; init; }
        [JsonPropertyName("Files")] public List<FileInfo> Files { get; init; } = new();
    }
    public sealed class FileInfo
    {
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("DownloadUrl")] public string DownloadUrl { get; init; } = "";
        [JsonPropertyName("Size")] public long Size { get; init; }  // bytes
    }
}