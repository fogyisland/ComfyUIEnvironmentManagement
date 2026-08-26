using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x:per-env hidden-file metadata (写盘 <c>&lt;EnvDir&gt;/.cmgr-env.json</c>,
/// FileAttributes.Hidden)。识别 "这个子目录是一个 ComfyUI Manager env" 的身份证;
/// 也携带 env_id/name/kind/template snapshot 让 <see cref="ComfyUI.Manager.Services.EnvDirectoryScanner"/>
/// 在切换 Settings.EnvsDir 时 upsert SQLite。
///
/// 用户场景:
///   - 在 Settings 改 EnvsDir 到新路径 → 启动时 scanner 枚举新目录直接子目录,
///     看到 .cmgr-env.json 就把 env upsert 进 SQLite(env_id 跟 marker 一致)。
///   - 用户拷贝 env 目录到其他位置 → 新位置的 marker 被发现,SQLite RootPath 更新,
///     env_id 保持不变所以 settings/status/进程状态继承。
///   - 用户重命名 env 目录 → marker 的 Name 字段可能跟目录名不一致,但 marker.env_id
///     还是 SQLite 的 id,scanner 通过 env_id 找到原记录更新 RootPath。
///
/// 文件名约定:.cmgr-env.json — 以 '.' 开头自动 FileAttributes.Hidden(Windows 行为),
/// _cmgr- 前缀避免跟 ComfyUI Manager 自身的 custom_node_manifest.json 等冲突。
/// SchemaVersion = 1 字段布局。后续 schema 升级读不到时 scanner 静默 skip。
/// </summary>
public sealed class EnvMarker
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = ".cmgr-env.json";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("env_id")]
    public string EnvId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("template_snapshot")]
    public TemplateConfig? TemplateSnapshot { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";
}