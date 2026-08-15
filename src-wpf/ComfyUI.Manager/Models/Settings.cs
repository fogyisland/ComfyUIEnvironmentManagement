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
    // v0.6.10: 全局默认 Models 目录(env-create 时把 <env-root>/ComfyUI/models
    // junction 到此路径,作为新 env 的默认 models 位置;每 env 可独立覆盖)。
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

    // —— 环境 / 工具 ——
    [JsonPropertyName("python_venv_baseline")] public string PythonVenvBaseline { get; set; } = "";
    [JsonPropertyName("http_proxy_enabled")] public bool HttpProxyEnabled { get; set; }
    [JsonPropertyName("http_proxy_url")] public string HttpProxyUrl { get; set; } = "";
    [JsonPropertyName("http_proxy_port")] public int HttpProxyPort { get; set; }
    [JsonPropertyName("git_exe")] public string GitExe { get; set; } = "";
    [JsonPropertyName("git_proxy_url")] public string GitProxyUrl { get; set; } = "";
    [JsonPropertyName("git_proxy_port")] public int GitProxyPort { get; set; }
    [JsonPropertyName("git_proxy_enabled")] public bool GitProxyEnabled { get; set; }
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
        // —— 环境 / 工具 ——
        target.PythonVenvBaseline = source.PythonVenvBaseline;
        target.GitExe = source.GitExe;
        target.GitProxyUrl = source.GitProxyUrl;
        target.GitProxyPort = source.GitProxyPort;
        target.GitProxyEnabled = source.GitProxyEnabled;
        target.ComfyUiStartupTimeoutSeconds = source.ComfyUiStartupTimeoutSeconds;
        target.ComfyUiLocale = source.ComfyUiLocale;
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

