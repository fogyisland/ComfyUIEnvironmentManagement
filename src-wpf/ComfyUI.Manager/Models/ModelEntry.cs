using System;
using System.Collections.Generic;
using System.Linq;
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

public enum ModelSourceKind { CivitAi = 0, HuggingFace = 1, ModelScope = 2 }

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
    /// <summary>v1.0.0 T12:Hugging Face Diffusers / 多文件文件夹模型 (model_index.json + unet/ + text_encoder/ 等)。
    /// 检测由 ModelFilesystemScanner 在 kindDir 子目录内进行 — 同子目录有 model_index.json 即视为 1 个 Diffusers 模型卡,
    /// 不再递归 per-file 扫 unet/ 等子目录的 .safetensors。</summary>
    Diffusers,
    Other,
}

public enum ModelNsfwKind { SFW = 0, Mature, NSFW }

/// <summary>v0.6.22+:CivitAI sort 参数 — 用户 2026-08-20 反馈"搜索似乎只传递关键词,
/// 不传递其他参数"。CivitAI API 的 sort 值大小写敏感 — 直接用 enum 名当 API value。</summary>
public enum CivitAiSort { Newest, MostDownloaded, TopRated, MostLiked, MostDiscussed }

/// <summary>v0.6.22+:CivitAI period 参数 — 跟 sort 配合缩小时间范围。</summary>
public enum CivitAiPeriod { AllTime, Year, Month, Week, Day }

/// <summary>
/// v0.6.22+:CivitAI baseModel chip 选项 — 用户 2026-08-20 反馈"模型参数是不是也可以传递?
/// 也就是 base model 列出常规可用的 Model 类型"。列出 CivitAI 官方 baseModel 枚举的
/// 常用值,All = 不过滤(API 不加 baseModels= 参数)。
/// 每个 enum 名映射到 CivitAI baseModel 字符串(见 <see cref="ApiValue"/>)。
/// HF 不支持 baseModel API,枚举仍可见以便 VM 状态保留;切到 HF 时 chip 整行折叠。
/// </summary>
public enum CivitAiBaseModel
{
    All,
    SD_1_5,
    SD_2_1,
    SD_3,
    SD_3_5,
    SD_3_5_Large,
    SDXL_1_0,
    Flux_1_D,
    Flux_1_Schnell,
    Pony,
    Pony_V6_XL,
    Stable_Cascade,
    HiDream,
    Kolors,
    Wan_Video,
    Hunyuan_Video,
    CogVideoX,
    LTXV,
    Mochi,
    Pixart,
    AuraFlow,
}

public static class CivitAiBaseModelExtensions
{
    /// <summary>Enum → CivitAI baseModel 字符串。All = null(API 不加 baseModels= 参数)。</summary>
    public static string? ApiValue(this CivitAiBaseModel m) => m switch
    {
        CivitAiBaseModel.All => null,
        CivitAiBaseModel.SD_1_5 => "SD 1.5",
        CivitAiBaseModel.SD_2_1 => "SD 2.1",
        CivitAiBaseModel.SD_3 => "SD 3",
        CivitAiBaseModel.SD_3_5 => "SD 3.5",
        CivitAiBaseModel.SD_3_5_Large => "SD 3.5 Large",
        CivitAiBaseModel.SDXL_1_0 => "SDXL 1.0",
        CivitAiBaseModel.Flux_1_D => "Flux.1 D",
        CivitAiBaseModel.Flux_1_Schnell => "Flux.1 Schnell",
        CivitAiBaseModel.Pony => "Pony",
        CivitAiBaseModel.Pony_V6_XL => "Pony V6 XL",
        CivitAiBaseModel.Stable_Cascade => "Stable Cascade",
        CivitAiBaseModel.HiDream => "HiDream",
        CivitAiBaseModel.Kolors => "Kolors",
        CivitAiBaseModel.Wan_Video => "Wan Video",
        CivitAiBaseModel.Hunyuan_Video => "Hunyuan Video",
        CivitAiBaseModel.CogVideoX => "CogVideoX",
        CivitAiBaseModel.LTXV => "LTXV",
        CivitAiBaseModel.Mochi => "Mochi",
        CivitAiBaseModel.Pixart => "Pixart",
        CivitAiBaseModel.AuraFlow => "AuraFlow",
        _ => null,
    };

    /// <summary>CivitAI baseModel 字符串 → enum 形式(给 VM 当 chip 默认值)。
    /// null / 空 / 找不到 → All。</summary>
    public static CivitAiBaseModel FromApi(string? apiValue)
    {
        if (string.IsNullOrWhiteSpace(apiValue)) return CivitAiBaseModel.All;
        foreach (var m in Enum.GetValues<CivitAiBaseModel>())
        {
            if (string.Equals(m.ApiValue(), apiValue, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return CivitAiBaseModel.All;
    }
}

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

    // v0.6.22+:UI 显示辅助 — 主文件名(从 Files[0] 取) + 可读大小。XAML 卡片直接绑。
    // 用户 2026-08-20 反馈"没有下载的地址" — 卡片需要显示文件信息 + URL 入口。
    public string PrimaryFileName => Files?.FirstOrDefault()?.Name ?? "";
    public string PrimaryFileFormat => Files?.FirstOrDefault()?.Format ?? "";

    /// <summary>人类可读文件大小(B / KB / MB / GB)。空时返 "" 让 XAML 跳过显示。</summary>
    public string SizeDisplay => SizeBytes <= 0 ? "" : FormatSize(SizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
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
/// SubfolderName = "<version-slug>-<vid8>"(per-version subfolder,collision suffix -1/-2 已 strip)。
/// v1.0.0 T13:扩展 3 字段供 hash-matching 链(MatcherOrchestrator 在 scan 时填充,
/// UI card 通过 LocalModelCard.{Hash,MatchedDetail,MatchSource} 透传给 LocalModelCard;
/// 原始 DownloadedModel 这 3 字段 = match 阶段 in-memory 状态,无需序列化)。</summary>
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
    /// <summary>v1.0.0 T10:absolute path to local preview image (sibling scan via BuildFlatModel).
    /// meta.json 路径(marketplace 下载)永远 null — 不扫本地 preview。</summary>
    public string? PreviewImagePath { get; init; }
    /// <summary>v1.0.0 T13:SHA256 hash (hex)。Scanner 计算 → MatcherOrchestrator 调
    /// IModelMatcher 时作为 input(hash matcher 第一关)。未计算或非 model 文件 = null。</summary>
    public string? Hash { get; init; }
    /// <summary>v1.0.0 T13:首个 non-null IModelMatcher.MatchAsync 结果(链上的 CivitAiDetailDto)。
    /// Scanner 阶段填充,UI 通过 LocalModelCard.MatchedDetail 显示非空 badge + pre-fill dialog。
    /// 永远非 null iff MatchSource 非 null。</summary>
    public CivitAiDetailDto? MatchedDetail { get; init; }
    /// <summary>v1.0.0 T13:首个命中 match 的 MatchSource enum 值(null = 还没 match 或 4 个 matcher 全 miss)。
    /// 顺序在 spec §3.2:Hash → SafetensorsMetadata → CompanionJson → FilenameFuzzy。</summary>
    public MatchSource? MatchSource { get; init; }
}

// v1.0.0 T13:Match-source enum + result record — 跟 DownloadedModel 同文件以便
// 测试 / VM 只需 `using ComfyUI.Manager.Models;`。
// 历史背景:spec `docs/superpowers/specs/2026-08-24-civitai-hash-matching-design.md` §6.1。
public enum MatchSource
{
    /// <summary>SHA256 → /api/v1/model-versions/by-hash/{hash} exact hit — 最高 confidence。</summary>
    Hash = 0,
    /// <summary>.safetensors 内嵌 {"__metadata__": { "modelspec.title": "..." }} 等字段模糊搜。</summary>
    SafetensorsMetadata = 1,
    /// <summary>同目录 companion .json(meta.json 之外)title / baseModel 命中。</summary>
    CompanionJson = 2,
    /// <summary>文件名 → /api/v1/models?query= filename fuzzy — 最后 fallback。</summary>
    FilenameFuzzy = 3,
    /// <summary>v1.0.0.x:用户在 LocalModelsView toolbar「🔎 CivitAI 查询」手动 picker
    /// 选中结果,UI 显式写入 SQLite <c>civitai_card_cache</c> 持久化。优先级最高 — 覆盖任何
    /// hash-match chain 结果(scanner 自动匹配的是猜测,用户主动 pick 才是确认)。</summary>
    UserQuery = 4,
}

/// <summary>v1.0.0 T13:MatcherOrchestrator.MatchAsync 返回 shape(IModelMatcher.MatchAsync)。
/// CoverImageUrl 从 Detail.ImageUrls[0] 提取,UI 直接绑 Image.Source。</summary>
public sealed record MatchResult(
    MatchSource Source,
    CivitAiDetailDto Detail,
    string? CoverImageUrl);

/// <summary>v1.0.0:本地模型 UI 卡片 record — ViewModel 用,View 直接绑。
/// 跟 <see cref="DownloadedModel"/> 不同:<see cref="DownloadedModel"/> 是 scanner emit 的
/// per-file 记录(per-version),本 record 是按 <see cref="SourceId"/> group 后的 per-model 卡片
/// (per-model + version count + latest mtime)。
/// v1.0.0 T13:扩展 3 字段给 hash-matching 链 — Scanner 在 ScanContext 启用时填充,
/// View 通过 converter 显示 badge (MatchStatusToBrush) + tooltip (MatchSourceToTooltip)。
/// 当 card.MatchedDetail 非 null → 用户点 [查询 CivitAI] 按钮时 dialog 直接开 Detail state,
/// 跳过 searching 阶段(对本地卡用户体验大幅提升:首次扫描后单 click 看详情,不用再打字搜)。</summary>
public sealed record LocalModelCard(
    /// <summary>v1.0.0 T-D5:GroupToCards group key,从 DownloadedModel 透传。用来 streaming Phase 2
    /// 按 SourceId 就地找到对应 card 做 match status 更新(其他字段 Title / Kind / VersionCount
    /// 不变 — Phase 1 已经填好)。</summary>
    string SourceId,
    string Title,
    ModelKind Kind,
    string Source,
    int VersionCount,
    DateTime? LatestDownloadedAt,
    string? SourceUrl,
    string? PreviewImagePath,
    /// <summary>v1.0.0 T13:SHA256 hex hash(可能 null,例如 meta.json 模型无文件 hash)。</summary>
    string? Hash,
    /// <summary>v1.0.0 T13:hash-matching chain 首个非 null IModelMatcher.MatchAsync 结果。
    /// 非 null 时 = card 是 pre-matched 状态,UI 显示绿 badge + tooltip + dialog 开 Detail state。</summary>
    CivitAiDetailDto? MatchedDetail,
    /// <summary>v1.0.0 T13:首个命中 match 的 MatchSource enum 值(跟 MatchedDetail 同步 — 同时 null 或同时非 null)。
    /// 顺序 Hash → SafetensorsMetadata → CompanionJson → FilenameFuzzy。</summary>
    MatchSource? MatchSource,
    /// <summary>v1.0.0.x: 用户手动覆盖的本地绝对路径(从 <c>local_model_overrides</c> 表读)。
    /// null = 用 scanner 推算的 FullPath(默认)。非空时 UI 在卡片显示这条 + [复制]
    /// + [编辑/清除] 按钮;后续 env 启动(junction)用这条替代扫描路径。</summary>
    string? LocalPathOverride = null)
{
    /// <summary>v1.0.0 T-D5:streaming scanner Phase 2 更新 match status — 返回新 record(positional record
    /// 不可变,mutation 要重建)。调用方负责在 _allCards + FilteredModels 两处用旧实例找 index 替换成新实例。
    /// 任一传入值非 null 时覆盖(允许只更新 Hash 不更新 Detail 这种部分填充 — scanner HashAndMatch
    /// 不同阶段产出形状不同:hash match 阶段只填 Hash,safetensors 阶段填 Hash+MatchedDetail)。</summary>
    public LocalModelCard WithMatchStatus(string? hash, CivitAiDetailDto? matchedDetail, MatchSource? matchSource)
        => this with { Hash = hash, MatchedDetail = matchedDetail, MatchSource = matchSource };

    /// <summary>v1.0.0.x: 用户改 override path 后 rebuild card — 用旧 card 找 index
    /// 替换成新 card(保留其他字段不变)。Empty/null overridePath 视作「恢复默认」。</summary>
    public LocalModelCard WithLocalPathOverride(string? overridePath)
        => this with { LocalPathOverride = string.IsNullOrEmpty(overridePath) ? null : overridePath };
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
            // v0.6.20 T10 polish:collapse dead-condition ternary. alnum→c, all other→'-'
            // (covers '-', ' ', '_' uniformly). Behavior unchanged.
            var ch = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '-';
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
