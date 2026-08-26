using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Services;

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

    /// <summary>
    /// Human-readable badge for the template kind. v1.0.0+: used by TemplateManagementView
    /// card to display "[GitHub]" / "[本地]". Not serialized — derived from SourceKind.
    /// </summary>
    [JsonIgnore]
    public string SourceKindBadge => SourceKind switch
    {
        TemplateSourceKind.GitHub => "[GitHub]",
        TemplateSourceKind.Local => "[本地]",
        _ => "",
    };

    /// <summary>
    /// Whether this template's source can be updated via
    /// <see cref="ComfyUI.Manager.Services.TemplateSourceUpdater.UpdateAsync"/>.
    /// GitHub templates always can (URL is in config). Local templates only if built-in
    /// (ComfyUI / A1111 / Forge / SwarmUI — they have a default repo URL). Custom Local
    /// templates have no remote.
    /// v1.0.0+: used by TemplateManagementView "更新源码" button Visibility binding.
    /// </summary>
    [JsonIgnore]
    public bool CanUpdateSource => SourceKind == TemplateSourceKind.GitHub
        || Kind == "ComfyUI"
        || Kind == "A1111"
        || Kind == "Forge"
        || Kind == "SwarmUI";

    /// <summary>
    /// Whether the user can delete this template from the management UI. Built-in
    /// templates are protected (G13) — they always exist as canonical templates.
    /// v1.0.0.x: extended to 8 built-in kinds (ComfyUI + A1111 + Forge + SwarmUI +
    /// OpenVoice + Whisper + CoquiTTS + Bark).Hides the grayed-out Delete button on
    /// built-in cards.
    /// </summary>
    [JsonIgnore]
    public bool CanDelete => Kind switch
    {
        "ComfyUI" or "A1111" or "Forge" or "SwarmUI"
            or "OpenVoice" or "Whisper" or "CoquiTTS" or "Bark" => false,
        _ => true,
    };

    /// <summary>
    /// v1.0.0.x:本地源码目录是否存在(走 <see cref="TemplatePathResolver.Resolve"/> 把
    /// <c>LocalSourceDir</c> 解析为绝对路径,再检查目录 + 内部 entries)。
    /// 由 TemplateManagementViewModel 传 <see cref="ComfyUI.Manager.Infrastructure.Settings.SystemTemplateLibraryDir"/>
    /// 作 anchor(template 列表 vs 磁盘检查,内置模板路径 anchor 改内置后由 SettingsDefaults
    /// seed)。
    /// 不带 <c>[JsonIgnore]</c> — 实例方法不是 property,JsonSerializer 自然忽略。
    ///
    /// 判定规则 — 严格于 <see cref="Directory.Exists"/>:
    /// <list type="number">
    ///   <item>目录不存在 → false</item>
    ///   <item>目录存在但内部为空(用户手建空目录 / git clone 中途中断)— false</item>
    ///   <item>目录存在且有 <c>.git</c> 子目录(典型 git clone 产物)— true</item>
    ///   <item>目录存在且至少有一个文件/子目录(用户手动 copy / 部分下载)— true</item>
    /// </list>
    /// </summary>
    public bool LocalDirExists(string? systemTemplateLibraryDir)
    {
        var resolved = TemplatePathResolver.Resolve(LocalSourceDir, systemTemplateLibraryDir);
        if (string.IsNullOrWhiteSpace(resolved)) return false;
        if (!Directory.Exists(resolved)) return false;
        // v1.0.0.x hotfix:用户反馈"检查文件不能只检查目录在不在"。
        // .git 优先(标准 git clone 产物),其次只要目录非空就算有内容。
        if (Directory.Exists(Path.Combine(resolved, ".git"))) return true;
        return Directory.EnumerateFileSystemEntries(resolved).Any();
    }

    /// <summary>
    /// v1.0.0.x:模板管理卡片用本地状态 badge。<see cref="LocalDirBadgeHint"/>
    /// 没本地目录时显示,提醒用户「在模板管理页点 下载与更新 把模板源码 clone 到本地」。
    /// </summary>
    public const string LocalDirBadgeHint = "本地目录为空";

    /// <summary>
    /// 返回本地目录状态文字 — 用于 TemplateManagementView 卡片显示。
    /// 目录存在 → ""(不显示 badge,卡片自身有 SourceKindBadge 等);
    /// 目录不存在 → <see cref="LocalDirBadgeHint"/>(红色 badge 提醒用户 clone)。
    /// </summary>
    public string LocalDirBadge(string? systemTemplateLibraryDir)
        => LocalDirExists(systemTemplateLibraryDir) ? "" : LocalDirBadgeHint;

    /// <summary>
    /// v1.0.0.x:本地目录缺失标记 — 由 <see cref="ComfyUI.Manager.ViewModels.TemplateManagementViewModel"/>
    /// 在 Add/Edit/构造时根据 <c>Settings.SystemTemplateLibraryDir</c> 计算并写入。
    /// View 用 <c>BoolToVisibility</c> 转换控制 <see cref="LocalDirBadgeHint"/> 红色
    /// badge 的可见性。<c>[JsonIgnore]</c> — 运行时状态,不进 settings.json。
    /// </summary>
    [JsonIgnore]
    public bool LocalDirMissing { get; set; }
}
