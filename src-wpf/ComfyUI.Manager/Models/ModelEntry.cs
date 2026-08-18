using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>v0.6.20:模型市场聚合模型 — 来自任意 source 的单条 model 记录。
/// 1 个 ModelEntry = 1 张卡片,内含所有 ModelVersions(per-version checkbox 多选)。</summary>
public class ModelEntry
{
    [JsonPropertyName("source")] public ModelSourceKind Source { get; init; }
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = "";        // CivitAI model id
    [JsonPropertyName("source_url")] public string SourceUrl { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("author_url")] public string? AuthorUrl { get; init; }
    [JsonPropertyName("kind")] public ModelKind Kind { get; init; }                     // parsed from "type"
    [JsonPropertyName("base_model")] public string? BaseModel { get; init; }
    [JsonPropertyName("nsfw_kind")] public ModelNsfwKind NsfwKind { get; init; }         // parsed from nsfwLevel
    [JsonPropertyName("nsfw_level")] public int? NsfwLevel { get; init; }
    [JsonPropertyName("download_count")] public int? DownloadCount { get; init; }
    [JsonPropertyName("rating_count")] public int? RatingCount { get; init; }
    [JsonPropertyName("rating_stars")] public double? RatingStars { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    [JsonPropertyName("preview_image_url")] public string? PreviewImageUrl { get; init; }
    [JsonPropertyName("versions")] public IReadOnlyList<ModelVersionEntry> Versions { get; init; } = Array.Empty<ModelVersionEntry>();
}

public enum ModelSourceKind { CivitAi = 0 }

public enum ModelKind
{
    Unknown = 0,
    Checkpoint,
    LORA,
    VAE,
    Controlnet,
    TextualInversion,
    Upscaler,
    Hypernetwork,
    Other,
}

public enum ModelNsfwKind { SFW = 0, Mature, NSFW }

/// <summary>v0.6.20:per-version 选中单位。Id 全局唯一 = "{SourceKind}:{ModelId}:{VersionId}"。
/// 1 个 ModelVersionEntry 对应 1 个可下载的具体文件 + meta.json sidecar。</summary>
public class ModelVersionEntry
{
    public string Id { get; init; } = "";                                               // "{CivitAi}:{modelId}:{versionId}"
    public ModelEntry Parent { get; init; } = null!;
    public string SourceVersionId { get; init; } = "";                                  // CivitAI modelVersionId
    public string Name { get; init; } = "";                                              // e.g. "v5.0 fp16"
    public string? BaseModel { get; init; }
    public long SizeBytes { get; init; }                                                 // primary file size
    public string PrimaryDownloadUrl { get; init; } = "";                                // primary file downloadUrl
    public IReadOnlyList<ModelFile> Files { get; init; } = Array.Empty<ModelFile>();
    public DateTimeOffset? PublishedAt { get; init; }
    public bool IsEarlyAccess { get; init; }
}

public class ModelFile
{
    public string Name { get; init; } = "";                                              // e.g. "model.safetensors"
    public string Format { get; init; } = "";                                            // "Safe Tensor" / "PickleTensor" / "ONNX" / "Other"
    public long SizeBytes { get; init; }
    public string DownloadUrl { get; init; } = "";
    public bool IsPrimary { get; init; }                                                 // marked primary in API
}

/// <summary>v0.6.20:filesystem 扫描出来的"已下载"状态(无 DB)。
/// SubfolderName = "<version-slug>-<vid8>"(per-version subfolder,collision suffix -1/-2 已 strip)。</summary>
public class DownloadedModel
{
    public string SubfolderName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public ModelKind Kind { get; init; }
    public string? Title { get; init; }
    public string Source { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string SourceVersionId { get; init; } = "";
    public DateTime DownloadedAt { get; init; }
}

/// <summary>v0.6.20:meta.json sidecar 反序列化形状。
/// DownloadAsync 写,FilesystemScanner 读。其他字段 forward-compatible。</summary>
public class ModelMetaSidecar
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("kind")] public ModelKind Kind { get; set; }
    [JsonPropertyName("base_model")] public string? BaseModel { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("source_version_id")] public string SourceVersionId { get; set; } = "";
    [JsonPropertyName("source_url")] public string SourceUrl { get; set; } = "";
    [JsonPropertyName("primary_filename")] public string PrimaryFilename { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("nsfw_level")] public int NsfwLevel { get; set; }
    [JsonPropertyName("downloaded_at")] public DateTime DownloadedAt { get; set; }
}

/// <summary>v0.6.20:Kind → ComfyUI standard subfolder 映射。
/// Public,供 Downloader / Symlinker / FilesystemScanner 共享。</summary>
public static class ModelKindExtensions
{
    private static readonly Dictionary<ModelKind, string> KindToSubfolder = new()
    {
        [ModelKind.Checkpoint] = "checkpoints",
        [ModelKind.LORA] = "loras",
        [ModelKind.VAE] = "vae",
        [ModelKind.Controlnet] = "controlnet",
        [ModelKind.TextualInversion] = "embeddings",
        [ModelKind.Upscaler] = "upscale_models",
        [ModelKind.Hypernetwork] = "hypernetworks",
        [ModelKind.Unknown] = "other",
        [ModelKind.Other] = "other",
    };

    public static string ToComfyUiSubfolder(this ModelKind kind) =>
        KindToSubfolder.TryGetValue(kind, out var s) ? s : "other";

    /// <summary>v0.6.20:从 CivitAI "type" 字符串解析 Kind(case-insensitive, normalized)。</summary>
    public static ModelKind ParseKind(string? typeString)
    {
        if (string.IsNullOrWhiteSpace(typeString)) return ModelKind.Other;
        return typeString.Trim().ToLowerInvariant() switch
        {
            "checkpoint" => ModelKind.Checkpoint,
            "lora" or "lyocris" => ModelKind.LORA,
            "vae" => ModelKind.VAE,
            "controlnet" => ModelKind.Controlnet,
            "textualinversion" => ModelKind.TextualInversion,
            "upscaler" or "esrgan" or "realesrgan" => ModelKind.Upscaler,
            "hypernetwork" => ModelKind.Hypernetwork,
            _ => ModelKind.Other,
        };
    }

    /// <summary>v0.6.20:从 CivitAI nsfwLevel / nsfw bool 解析 NsfwKind。
    /// nsfwLevel 0/1 → SFW;2 → Mature;3+ → NSFW。nsfwLevel 缺失但 nsfw=true → Mature;nsfw false → SFW。</summary>
    public static ModelNsfwKind ParseNsfwKind(int? nsfwLevel, bool? nsfwBool)
    {
        if (nsfwLevel.HasValue)
        {
            if (nsfwLevel.Value <= 1) return ModelNsfwKind.SFW;
            if (nsfwLevel.Value == 2) return ModelNsfwKind.Mature;
            return ModelNsfwKind.NSFW;
        }
        if (nsfwBool == true) return ModelNsfwKind.Mature;
        return ModelNsfwKind.SFW;
    }

    /// <summary>v0.6.20:Slug 生成 + 8-char id 拼成 "<slug>-<id8>"。
    /// Slug = lowercase, non-[a-z0-9-] → '-', collapse repeated '-', trim。
    /// Id8 = first 8 chars of source id (pad if shorter than 8)。..</summary>
    public static string ToSlugId(string title, string sourceId)
    {
        var slug = (title ?? "").ToLowerInvariant();
        var sb = new System.Text.StringBuilder(slug.Length);
        char last = '\0';
        foreach (var c in slug)
        {
            var ch = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : (c == '-' || c == ' ' || c == '_') ? '-' : '-';
            if (ch == '-' && last == '-') continue;
            sb.Append(ch);
            last = ch;
        }
        var trimmed = sb.ToString().Trim('-');
        if (string.IsNullOrEmpty(trimmed)) trimmed = "model";
        var id8 = (sourceId ?? "").Length >= 8 ? (sourceId ?? "").Substring(0, 8) : (sourceId ?? "").PadRight(8, '0');
        return $"{trimmed}-{id8}";
    }
}
