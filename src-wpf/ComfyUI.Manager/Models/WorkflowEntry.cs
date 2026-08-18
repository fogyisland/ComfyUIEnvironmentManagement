using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>v0.6.19:工作流市场聚合模型 — 来自任意 source 的单条 workflow 记录。</summary>
public class WorkflowEntry
{
    [JsonPropertyName("source")] public WorkflowSourceKind Source { get; init; }
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = "";
    [JsonPropertyName("source_url")] public string SourceUrl { get; init; } = "";
    [JsonPropertyName("workflow_json_url")] public string WorkflowJsonUrl { get; init; } = "";
    [JsonPropertyName("preview_image_url")] public string? PreviewImageUrl { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("download_count")] public int? DownloadCount { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>节点 ID 列表 — "需装节点" 过滤器用。</summary>
    [JsonPropertyName("required_nodes")] public IReadOnlyList<string> RequiredNodes { get; init; } = Array.Empty<string>();
}

public enum WorkflowSourceKind
{
    CommunityJson = 0,
    CivitAi = 1,
    OpenArt = 2,
}

/// <summary>v0.6.19:filesystem 扫描出来的"已下载"状态(无 DB)。</summary>
public class DownloadedWorkflow
{
    public string SubfolderName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceId { get; init; } = "";
    public DateTime DownloadedAt { get; init; }
}

/// <summary>v0.6.19:meta.json sidecar DTO — 仅 writer/scanner 内部用,WorkflowEntry 不存 raw_meta。</summary>
internal class WorkflowMetaSidecar
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("downloaded_at")] public DateTime DownloadedAt { get; set; }
}