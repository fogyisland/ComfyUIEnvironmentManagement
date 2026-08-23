using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0 multi-template: source-kind discriminator for a template.
/// <see cref="Local"/> = pre-existing local checkout (T1-T12 default).
/// <see cref="GitHub"/> = clone from a public GitHub repo (T13+, wired in T14/T15).
/// Default <c>Local = 0</c> so old settings.json without <c>source_kind</c> deserializes safely.
/// </summary>
public enum TemplateSourceKind
{
    Local = 0,
    GitHub = 1,
}

/// <summary>
/// v1.0.0 multi-template: per-template configuration. String-keyed by Kind (no enum).
/// Snapshot per env (Environment.TemplateConfigSnapshot) freezes at env creation time;
/// updates to Settings.Templates do NOT affect existing envs.
/// </summary>
public class TemplateConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("local_source_dir")]
    public string LocalSourceDir { get; set; } = "";

    [JsonPropertyName("source_kind")]
    public TemplateSourceKind SourceKind { get; set; } = TemplateSourceKind.Local;

    [JsonPropertyName("github_repo_url")]
    public string GitHubRepoUrl { get; set; } = "";

    [JsonPropertyName("entry_script")]
    public string EntryScript { get; set; } = "";

    [JsonPropertyName("entry_args")]
    public string EntryArgs { get; set; } = "";

    [JsonPropertyName("models_subdir")]
    public string ModelsSubdir { get; set; } = "models";

    [JsonPropertyName("extra_junction_targets")]
    public List<string> ExtraJunctionTargets { get; set; } = new();

    [JsonPropertyName("user_extra_args")]
    public string UserExtraArgs { get; set; } = "";
}
