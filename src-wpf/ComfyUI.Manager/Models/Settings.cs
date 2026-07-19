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
    [JsonPropertyName("envs_dir")] public string EnvsDir { get; set; } = "";
    [JsonPropertyName("global_nodes_dir")] public string GlobalNodesDir { get; set; } = "";

    // —— 环境 / 工具 ——
    [JsonPropertyName("python_venv_baseline")] public string PythonVenvBaseline { get; set; } = "";
    [JsonPropertyName("git_exe")] public string GitExe { get; set; } = "";
    [JsonPropertyName("git_proxy_url")] public string GitProxyUrl { get; set; } = "";
    [JsonPropertyName("git_proxy_port")] public int GitProxyPort { get; set; }
    [JsonPropertyName("git_proxy_enabled")] public bool GitProxyEnabled { get; set; }

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
}

public class ExtraPath
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

