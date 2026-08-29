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
    /// v1.0.0.x: 用户/编辑器自由填的元数据(描述/作者/版本/分类/备注等)。
    /// 跟 <c>CatalogEntry.RawMetadata</c>(从 GitHub API 自动抓)概念不同 — 这是用户在
    /// <see cref="ComfyUI.Manager.Views.TemplateManagement.EditTemplateDialog"/>
    /// 编辑的「手动维护」元数据;序列化到 settings.json,EnvCreator / launcher 不读,
    /// 仅供 UI 显示 / 第三方工具读取。空字典 = 没填。
    /// </summary>
    [JsonPropertyName("meta")]
    public Dictionary<string, string> Meta { get; set; } = new();

    /// <summary>
    /// Human-readable badge for the template kind. v1.0.0+: used by TemplateManagementView
    /// card to display "[GitHub]". Not serialized — derived from SourceKind.
    /// v1.0.0.x #630: "[本地]" → "[内置]" ——
    /// 2 个图像模板(ComfyUI / Forge)是 shipped 本地 checkout,
    /// "内置源码"比"本地"更精确,避免跟"用户自定义 local" 概念冲突。
    /// v1.0.0.x (2026-08-29): A1111 + SwarmUI 模板已下线,从 4 → 2。
    /// v1.0.0.x 用户改回 "[GitHub]" 统一 ——
    /// "模板管理的内容就不需要写内置,就写成 github",所有 7 个 built-in 模板在
    /// 模板管理卡片统一显示 "[GitHub]",用户不再看到 "[内置]"(此前 #630 改成
    /// "[内置]" 也被推翻)。新策略:7 个 built-in 都视为 GitHub source(其中 2 个
    /// 图像模板走 shipped 本地 checkout 但语义上是 GitHub repo 的 source,
    /// 4 个 AI 语音模板是真正 GitHub clone)。
    /// </summary>
    [JsonIgnore]
    public string SourceKindBadge => "[GitHub]";

    /// <summary>
    /// Whether this template's source can be updated via
    /// <see cref="ComfyUI.Manager.Services.TemplateSourceUpdater.UpdateAsync"/>.
    /// GitHub templates always can (URL is in config). Local templates only if built-in
    /// (ComfyUI / Forge — they have a default repo URL). Custom Local
    /// templates have no remote.
    /// v1.0.0.x (2026-08-29): A1111 + SwarmUI 从 built-in 白名单移除(模板已下线),剩 2 个图像模板。
    /// v1.0.0+: used by TemplateManagementView "更新源码" button Visibility binding.
    /// </summary>
    [JsonIgnore]
    public bool CanUpdateSource => SourceKind == TemplateSourceKind.GitHub
        || Kind == "ComfyUI"
        || Kind == "Forge";

    /// <summary>
    /// Whether the user can delete this template from the management UI. Built-in
    /// templates are protected (G13) — they always exist as canonical templates.
    /// v1.0.0.x (2026-08-29): 10 built-in kinds (2 图像 + 4 语音 + 4 视频/图像生成:
    /// ComfyUI + Forge + OpenVoice + Whisper + CoquiTTS + Bark +
    /// HunyuanVideo + LTXVideo + CogVideoX + Fooocus;A1111 + SwarmUI 已下线)。
    /// Hides the grayed-out Delete button on built-in cards.
    /// </summary>
    [JsonIgnore]
    public bool CanDelete => Kind switch
    {
        "ComfyUI" or "Forge"
            or "OpenVoice" or "Whisper" or "CoquiTTS" or "Bark"
            or "HunyuanVideo" or "LTXVideo" or "CogVideoX" or "Fooocus" => false,
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
    /// v1.0.0.x #630: 文字"本地目录为空" → "源码未下载" ——
    /// 3 个 GitHub AI 语音模板(Whisper / CoquiTTS / Bark)首次使用前没 clone,
    /// 旧文案容易被误读为「错误」;新文案直白「未下载」配合琥珀 badge 颜色,语义清楚。
    /// </summary>
    public const string LocalDirBadgeHint = "源码未下载";

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

    /// <summary>
    /// v1.0.0.x:把 <see cref="Meta"/> 字典序列化/反序列化为多行字符串供
    /// <see cref="ComfyUI.Manager.Views.TemplateManagement.EditTemplateDialog"/> 的
    /// TextBox 双向绑定。每行 <c>key=value</c>;空 value 允许;空 key 跳过;
    /// 含 <c>=</c> 的 value 取第一个 <c>=</c> 后的全部。空字典 → 空串。
    /// 双向 round-trip 通过 <see cref="ParseMetaRaw"/>。
    /// </summary>
    public string MetaRaw => string.Join("\n", Meta.Select(kvp => $"{kvp.Key}={kvp.Value}"));

    /// <summary>
    /// 反序列化 <see cref="MetaRaw"/> 字符串到字典。空白行忽略;<c>key</c> 为空行跳过;
    /// 没 <c>=</c> 的行跳过(避免脏数据)。返回新字典(不改 <see cref="Meta"/>)。
    /// </summary>
    public static Dictionary<string, string> ParseMetaRaw(string? raw)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;  // 没 = 或 key 空 → 跳过
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..];
            if (key.Length == 0) continue;
            dict[key] = val;
        }
        return dict;
    }
}
