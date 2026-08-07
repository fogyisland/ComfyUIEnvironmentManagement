using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public enum CatalogViewMode
{
    List,
    Tile,
}

public class Settings
{
    // —— 基础 / 显示 ——
    [JsonPropertyName("theme")] public string Theme { get; set; } = "material_purple";
    [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "system";
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

    // —— 环境 / 工具 ——
    [JsonPropertyName("python_venv_baseline")] public string PythonVenvBaseline { get; set; } = "";
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

    // —— v0.6.5.6: 多 Python 解释器管理 ——
    [JsonPropertyName("python_interpreters")]
    public List<PythonInterpreter> PythonInterpreters { get; set; } = new();

    [JsonPropertyName("active_python_interpreter_name")]
    public string ActivePythonInterpreterName { get; set; } = "";
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

