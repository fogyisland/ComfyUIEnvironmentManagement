using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public enum CatalogViewMode
{
    List,
    Tile,
}

// v0.6.11++ pip mirror:用户选 global pip 镜像(影响 ComfyUI/Manager 依赖安装,
// BED 不受影响 — 走 pytorch.org)。string 持久化以便老 settings.json 容错:
// 读时若枚举值不认识 → 回退 "official"(G3)。
public enum PipMirrorKind
{
    Official,
    TsinghuaTuna,
    Aliyun,
    USTC,
    Custom,
}

/// <summary>v0.6.22++:全局代理三态。
/// Off = 不走代理(handler.Proxy=null, UseProxy=false, 也不走 WinHTTP default system proxy);
/// InheritSystem = 走 OS-level(IE settings / WPAD / PAC 自动检测) — handler.UseProxy=true 但不设 Proxy;
/// Custom = 用 URL/Port 自定义 WebProxy。</summary>
public enum HttpProxyMode { Off, InheritSystem, Custom }

/// <summary>v0.6.22++:per-source 代理三态。
/// Off = this source 完全不走代理(handler.Proxy=null, UseProxy=false);
/// InheritGlobal = 跟随全局 HttpProxyMode(Off → 无代理;InheritSystem → 走 OS;Custom → 用全局 URL/Port);
/// AlwaysOn = this source 总是走代理(用全局 URL/Port;若全局 InheritSystem 则 fall back to OS 自动检测)。</summary>
public enum ModelSourceProxyMode { Off, InheritGlobal, AlwaysOn }

public class Settings
{
    // —— 基础 / 显示 ——
    [JsonPropertyName("theme")] public string Theme { get; set; } = "material_purple";
    [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "dark";
    [JsonPropertyName("language")] public string Language { get; set; } = "zh_CN";
    [JsonPropertyName("catalog_auto_refresh")] public bool CatalogAutoRefresh { get; set; }
    [JsonPropertyName("catalog_cache_ttl_minutes")] public int CatalogCacheTtlMinutes { get; set; } = 60;
    [JsonPropertyName("compat_api_base_url")] public string CompatApiBaseUrl { get; set; } = "";

    // —— 路径 ——
    [JsonPropertyName("template_python_dir")] public string TemplatePythonDir { get; set; } = "";
    [JsonPropertyName("template_comfyui_dir")] public string TemplateComfyuiDir { get; set; } = "";
    [JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";
    [JsonPropertyName("envs_dir")] public string EnvsDir { get; set; } = "";
    [JsonPropertyName("global_nodes_dir")] public string GlobalNodesDir { get; set; } = "";
    // v0.6.5.9: Catalog 主页「下载」按钮的目标目录。template-style,默认子目录名 "local-nodes"。
    [JsonPropertyName("local_node_directory")] public string LocalNodeDirectory { get; set; } = "";
    // v0.6.10 + v0.6.22+:全局默认 Models 目录。两用:
    // 1) env-create 时把 <env-root>/ComfyUI/models junction 到此路径,作为新 env 的默认 models 位置;每 env 可独立覆盖
    // 2) 模型市场下载目录(原 ModelsDirectory 已硬删,所有下载直接走这里)
    // 空字符串 = 不动 env 的 models 目录(沿用项目根 fallback)。
    [JsonPropertyName("default_models_directory")]
    public string DefaultModelsDirectory { get; set; } = "";
    /// <summary>
    /// v0.6.12:日志根目录(Logs/ 子目录的父目录)。空 = 默认 &lt;projectRoot&gt;(Logs/ 创建在 projectRoot 下)。
    /// 设置后,AppLogger / ProcessLauncher / 各 subsystem 都从这个目录创建 Logs/ 子目录。
    /// 例如:设置为 "D:/my-logs" → 日志写到 D:/my-logs/Logs/。
    /// </summary>
    [JsonPropertyName("log_directory")]
    public string LogDirectory { get; set; } = "";

    // v0.6.19:工作流市场 — 共享 workflows 目录 + 3 source enabled bools
    [JsonPropertyName("workflows_directory")]
    public string WorkflowsDirectory { get; set; } = "";
    [JsonPropertyName("workflow_source_community_json_enabled")]
    public bool WorkflowSourceCommunityJsonEnabled { get; set; } = true;
    [JsonPropertyName("workflow_source_civitai_enabled")]
    public bool WorkflowSourceCivitAiEnabled { get; set; } = true;
    [JsonPropertyName("workflow_source_openart_enabled")]
    public bool WorkflowSourceOpenArtEnabled { get; set; } = true;

    // v0.6.20:模型市场 — CivitAI source enabled bool(共享 models 目录 = DefaultModelsDirectory,v0.6.22+ 硬删 models_directory 字段)
    [JsonPropertyName("model_source_civitai_enabled")]
    public bool ModelSourceCivitAiEnabled { get; set; } = true;
    // v0.6.22+:CivitAI API key — 部分受限 / NSFW / 标记敏感模型,无 token 时直接调 API
    // 和 download URL 会返 401/403。token 走 Authorization: Bearer header 注入所有
    // CivitAI HTTP 请求(API search + 模型下载),不走 URL ?token= query 避免剪贴板暴露。
    // 获取 token:https://civitai.com/user/account → API Keys → Add API key。
    [JsonPropertyName("civitai_api_token")]
    public string CivitAiApiToken { get; set; } = "";
    // v0.6.21: 模型市场 per-source mirror + HuggingFace source + API token
    [JsonPropertyName("model_source_civitai_use_mirror")]
    public bool ModelSourceCivitAiUseMirror { get; set; } = false;
    [JsonPropertyName("model_source_civitai_mirror_url")]
    public string ModelSourceCivitAiMirrorUrl { get; set; } = "";
    [JsonPropertyName("model_source_huggingface_enabled")]
    public bool ModelSourceHuggingFaceEnabled { get; set; } = false;
    [JsonPropertyName("huggingface_api_token")]
    public string HuggingFaceApiToken { get; set; } = "";
    [JsonPropertyName("model_source_huggingface_use_mirror")]
    public bool ModelSourceHuggingFaceUseMirror { get; set; } = true;
    [JsonPropertyName("model_source_huggingface_mirror_url")]
    public string ModelSourceHuggingFaceMirrorUrl { get; set; } = "https://hf-mirror.com";
    // v0.6.22++:per-source 代理三态 — Off / InheritGlobal / AlwaysOn。
    // 决策见 ModelSourceProxyDecision.Resolve(globalMode, sourceMode, settings)。
    // 默认 = InheritGlobal(全局开关一键代理,per-source 跟全局走;Opt-out 显式设 Off;
    // AlwaysOn 用于强制走代理场景)。
    // 改动需重启应用生效(handler 在 OnStartup 一次性构造)。
    // 老 settings.json 含 bool `model_source_*_use_proxy` 由 SettingsRepository.Load()
    // 一次性迁移到 enum(true → InheritGlobal, false → Off)。
    [JsonPropertyName("model_source_civitai_proxy_mode")]
    public ModelSourceProxyMode ModelSourceCivitAiProxyMode { get; set; } = ModelSourceProxyMode.InheritGlobal;
    [JsonPropertyName("model_source_huggingface_proxy_mode")]
    public ModelSourceProxyMode ModelSourceHuggingFaceProxyMode { get; set; } = ModelSourceProxyMode.InheritGlobal;

    // —— 环境 / 工具 ——
    [JsonPropertyName("python_venv_baseline")] public string PythonVenvBaseline { get; set; } = "";
    // v0.6.22++:全局代理三态 — Off / InheritSystem / Custom。
    // 默认 = InheritSystem(企业 VPN 用户开箱即用 — 走 OS 默认 proxy / WPAD / PAC)。
    // 老 settings.json 含 bool `http_proxy_enabled` + `http_proxy_use_system`
    // 由 SettingsRepository.Load() 一次性迁移到 enum。
    [JsonPropertyName("http_proxy_mode")]
    public HttpProxyMode HttpProxyMode { get; set; } = HttpProxyMode.InheritSystem;
    [JsonPropertyName("http_proxy_url")] public string HttpProxyUrl { get; set; } = "";
    [JsonPropertyName("http_proxy_port")] public int HttpProxyPort { get; set; }
    [JsonPropertyName("git_exe")] public string GitExe { get; set; } = "";
    // v0.6.7.1: ComfyUI 启动就绪等待上限(秒)。默认 600(10 分钟)—— 大模型/首次
    // 编译 kernel 时几分钟很正常,30s 硬编码会误判失败。
    [JsonPropertyName("comfyui_startup_timeout_seconds")]
    public int ComfyUiStartupTimeoutSeconds { get; set; } = 600;
    // v0.6.7.2: ComfyUI UI 语言 locale code(写进 <comfyui>/user/default/comfy.settings.json
    // 的 Comfy.Locale 字段)。空字符串 = 不动 ComfyUI 配置。
    [JsonPropertyName("comfyui_locale")]
    public string ComfyUiLocale { get; set; } = "";

    // —— 高级:用户自定义 path 表(key=name,value=path)——
    [JsonPropertyName("extra_paths")] public List<ExtraPath> ExtraPaths { get; set; } = new();

    // —— Catalog 视图 ——
    [JsonPropertyName("catalog_view_mode")]
    public CatalogViewMode CatalogViewMode { get; set; } = CatalogViewMode.List;
    [JsonPropertyName("catalog_page_size")]
    public int CatalogPageSize { get; set; } = 20;

    // —— 节点源(查询/下载):两个列表 + 两个 active 名称 ——
    [JsonPropertyName("query_sources")]
    public List<NodeSource> QuerySources { get; set; } = new();
    [JsonPropertyName("download_sources")]
    public List<NodeSource> DownloadSources { get; set; } = new();
    [JsonPropertyName("active_query_source_name")]
    public string ActiveQuerySourceName { get; set; } = "";
    [JsonPropertyName("active_download_source_name")]
    public string ActiveDownloadSourceName { get; set; } = "";

    // —— GitHub API:配置后刷新 catalog 时同步拉各节点最新 release —
    [JsonPropertyName("github_token")]
    public string GitHubToken { get; set; } = "";

    // v0.6.11 T3: 开关 gate 控制 refresh 时是否拉节点版本号。默认 OFF 保持向后兼容
    // (避免没配 token 的用户被 GitHub 限流 60/h);开启时会用 GitHubToken(空 = 未鉴权)
    // 调 GitHubVersionService,失败 fail-soft,不抛。
    [JsonPropertyName("fetch_node_versions_on_refresh")]
    public bool FetchNodeVersionsOnRefresh { get; set; }

    // v0.6.13-B: 开关 gate 控制 refresh 时是否拉 GitHub metadata(License/Tags/
    // Stars/Downloads/LastCommit/Readme/Changelog/Deprecated)。默认 false 保持
    // 向后兼容(跟 v0.6.11 T3 FetchNodeVersionsOnRefresh 同 pattern,避免没配
    // token 的用户被 GitHub 限流 60/h)。
    [JsonPropertyName("fetch_catalog_metadata")]
    public bool FetchCatalogMetadata { get; set; }

    // —— v0.6.5.6: 多 Python 解释器管理 ——
    [JsonPropertyName("python_interpreters")]
    public List<PythonInterpreter> PythonInterpreters { get; set; } = new();

    [JsonPropertyName("active_python_interpreter_name")]
    public string ActivePythonInterpreterName { get; set; } = "";

    // v0.6.11++ pip mirror
    [JsonPropertyName("pip_mirror")] public string PipMirror { get; set; } = "official";
    [JsonPropertyName("pip_mirror_custom_url")] public string PipMirrorCustomUrl { get; set; } = "";

    // v0.6.11++ common nodes:env-create / 装依赖末尾自动 clone 的一组非冲突常用节点
    [JsonPropertyName("common_nodes")] public List<CommonNodeEntry> CommonNodes { get; set; } = new();

    /// <summary>
    /// v0.6.11+ SDD B T1:把 <paramref name="source"/> 的逐字段拷到 <paramref name="target"/>。
    /// 集合类字段做"清空 + AddRange"内容替换,不换 List 引用 —— Settings 实例由
    /// App 全局共享,Discard 必须就地回写以免其它服务持有被丢弃的旧对象(G4)。
    /// </summary>
    public static void CopyInto(Settings target, Settings source)
    {
        // —— 基础 / 显示 ——
        target.Theme = source.Theme;
        target.ThemeMode = source.ThemeMode;
        target.Language = source.Language;
        target.CatalogAutoRefresh = source.CatalogAutoRefresh;
        target.CatalogCacheTtlMinutes = source.CatalogCacheTtlMinutes;
        target.CompatApiBaseUrl = source.CompatApiBaseUrl;
        // —— 路径 ——
        target.TemplatePythonDir = source.TemplatePythonDir;
        target.TemplateComfyuiDir = source.TemplateComfyuiDir;
        target.DefaultPythonVersion = source.DefaultPythonVersion;
        target.EnvsDir = source.EnvsDir;
        target.GlobalNodesDir = source.GlobalNodesDir;
        target.LocalNodeDirectory = source.LocalNodeDirectory;
        target.DefaultModelsDirectory = source.DefaultModelsDirectory;
        target.LogDirectory = source.LogDirectory;
        target.WorkflowsDirectory = source.WorkflowsDirectory;
        target.WorkflowSourceCommunityJsonEnabled = source.WorkflowSourceCommunityJsonEnabled;
        target.WorkflowSourceCivitAiEnabled = source.WorkflowSourceCivitAiEnabled;
        target.WorkflowSourceOpenArtEnabled = source.WorkflowSourceOpenArtEnabled;
        target.ModelSourceCivitAiEnabled = source.ModelSourceCivitAiEnabled;
        target.CivitAiApiToken = source.CivitAiApiToken;
        target.ModelSourceCivitAiUseMirror = source.ModelSourceCivitAiUseMirror;
        target.ModelSourceCivitAiMirrorUrl = source.ModelSourceCivitAiMirrorUrl;
        target.ModelSourceHuggingFaceEnabled = source.ModelSourceHuggingFaceEnabled;
        target.HuggingFaceApiToken = source.HuggingFaceApiToken;
        target.ModelSourceHuggingFaceUseMirror = source.ModelSourceHuggingFaceUseMirror;
        target.ModelSourceHuggingFaceMirrorUrl = source.ModelSourceHuggingFaceMirrorUrl;
        target.ModelSourceCivitAiProxyMode = source.ModelSourceCivitAiProxyMode;
        target.ModelSourceHuggingFaceProxyMode = source.ModelSourceHuggingFaceProxyMode;
        // —— 环境 / 工具 ——
        target.PythonVenvBaseline = source.PythonVenvBaseline;
        target.GitExe = source.GitExe;
        target.ComfyUiStartupTimeoutSeconds = source.ComfyUiStartupTimeoutSeconds;
        target.ComfyUiLocale = source.ComfyUiLocale;
        target.HttpProxyMode = source.HttpProxyMode;
        // —— Catalog 视图 ——
        target.CatalogViewMode = source.CatalogViewMode;
        target.CatalogPageSize = source.CatalogPageSize;
        // —— 节点源 ——
        target.ActiveQuerySourceName = source.ActiveQuerySourceName;
        target.ActiveDownloadSourceName = source.ActiveDownloadSourceName;
        // —— GitHub ——
        target.GitHubToken = source.GitHubToken;
        target.FetchNodeVersionsOnRefresh = source.FetchNodeVersionsOnRefresh;
        target.FetchCatalogMetadata = source.FetchCatalogMetadata;
        // —— Python ——
        target.ActivePythonInterpreterName = source.ActivePythonInterpreterName;
        // —— Pip mirror ——
        target.PipMirror = source.PipMirror;
        target.PipMirrorCustomUrl = source.PipMirrorCustomUrl;
        // —— 集合:不换 List 引用,清空 + AddRange ——
        target.ExtraPaths.Clear();
        target.ExtraPaths.AddRange(source.ExtraPaths);
        target.QuerySources.Clear();
        target.QuerySources.AddRange(source.QuerySources);
        target.DownloadSources.Clear();
        target.DownloadSources.AddRange(source.DownloadSources);
        target.PythonInterpreters.Clear();
        target.PythonInterpreters.AddRange(source.PythonInterpreters);
        target.CommonNodes.Clear();
        target.CommonNodes.AddRange(source.CommonNodes);
    }
}

public class CommonNodeEntry
{
    // GitHub "owner/repo" 形式(e.g. "ltdrdata/ComfyUI-Manager")。
    // User-added 节点 Id 必须含 "/" — UI 表单校验(G12)。
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    // UI 显示用(不参与 git clone)。curated list 给用户友好名;user-added 可空 → fallback Id。
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    // 区分 curated seed(G11 不可删)跟 user-added(可删)。
    [JsonPropertyName("is_built_in")] public bool IsBuiltIn { get; set; }
    // 勾选状态 — 取消勾选 = "不装"(等价 skip)。built-in 也能关 enabled。
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class PythonInterpreter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public class ExtraPath
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

