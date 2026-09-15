using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public enum CatalogViewMode
{
    List,
    Tile,
}

// v1.0.0.x (2026-09-05) feat/nodelist-directory:节点查询 host 类型。
// v1.0.0.x (2026-09-15) feat/nodelist-source-config:GitHub kind 删除(限流不可用),
// 未来全部走自定义网站。NodelistHostKind enum 已废弃 → 枚举不再保留,改成单一字段。
// 历史 commit 上有 enum 时请参考 git log;v1.0.0.x 之后 Settings.cs 不再需要 host_kind 维度。

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
    // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43d):默认进入风格为白色 —
    // 用户原话"默认进入风格为白色"。原默认 "dark" 偏暗,白色对节点市场首次 seed 后看
    // StatusText / SelectedEntryHeader 等深色字色更友好。已有 settings.inf 不受影响(读的是
    // 磁盘值,不是 default),只有全新装机或没设过 ThemeMode 的用户会看到 light。
    [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "light";
    [JsonPropertyName("language")] public string Language { get; set; } = "zh_CN";
    [JsonPropertyName("catalog_auto_refresh")] public bool CatalogAutoRefresh { get; set; }
    [JsonPropertyName("catalog_cache_ttl_minutes")] public int CatalogCacheTtlMinutes { get; set; } = 60;
    [JsonPropertyName("compat_api_base_url")] public string CompatApiBaseUrl { get; set; } = "";

    // —— 路径 ——
    // v1.0.0.x (2026-09-05):安装根目录 — wizard Step 1 收集,二次启动校验程序路径是否还在。
    // 用户原话"为什么 wizard 结束之后不能够按照特定的路径启动呢" ——
    // 之前 InstallPath 只在 wizard 显示,没存 settings,程序始终用 exe 所在目录作 projectRoot。
    // 现在存 settings 持久化,如果 path 不存在(用户搬了文件夹)→ 二次启动时弹 wizard 重设。
    [JsonPropertyName("install_path")] public string InstallPath { get; set; } = "";
    [JsonPropertyName("template_python_dir")] public string TemplatePythonDir { get; set; } = "";
    // v1.0.0.x (2026-09-05) feat/nodelist-directory: 节点列表目录 ——
    // 包含 custom-node-list.json 文件的根目录(支持子目录递归)。
    // 用户手动点"扫描节点列表"→ NodeListScanner 解析所有 json,提取 author/title,
    // 调 GitHubVersionService.FetchVersionsAsync 拉 metadata,写 scanned_nodes 表。
    [JsonPropertyName("nodelist_directory")] public string NodelistDirectory { get; set; } = "";
    // v1.0.0.x (2026-09-15) feat/nodelist-source-config:节点查询源配置(单一 Custom 形态,
    // GitHub kind 因限流已删除)。host + token 单一字段,真值持久化到 SQLite
    // nodelist_source_config 表(SSoT),这两个 .inf 字段仅作镜像以维持向后兼容加载。
    // 老 .inf 的 nodelist_host_kind / nodelist_host_token / nodelist_custom_host_url /
    // nodelist_custom_token JSON key 在 InfSettingsSerializer 反序列化时会被静默忽略,
    // 旧值不会自动迁移到 NodelistServerUrl(用户明示"未来都不走 GitHub,完全砍掉",所以
    // 不做兼容迁移,丢失可接受)。用户首次保存后即可填新值。
    [JsonPropertyName("nodelist_server_url")] public string NodelistServerUrl { get; set; } = "";
    [JsonPropertyName("nodelist_api_token")] public string NodelistApiToken { get; set; } = "";
    // v1.0.0.x (2026-09-05) feat/nodelist-redesign:刷新时拉取控制(双复选框,独立)
    // 用户原话"去掉 github token 这个内容,提示刷新时候拉取节点版本,里面有两个复选框:
    // 刷新时候拉取节点版本 + 刷新时候拉取 github 元数据 license/stars/tags/readme"
    [JsonPropertyName("nodelist_refresh_versions")] public bool RefreshFetchVersions { get; set; } = true;
    [JsonPropertyName("nodelist_refresh_metadata")] public bool RefreshFetchMetadata { get; set; } = false;

    // v1.0.0.x (2026-09-05) feat/nodelist-directory:最近一次增量入库时间(ISO UTC)
    [JsonPropertyName("nodelist_last_ingest_at")] public string NodelistLastIngestAt { get; set; } = ""; // 旧字段,保留兼容
    // v1.0.0.x: 系统模板库目录 — 用户配置的共享模板根目录,模板管理页可从此处发现/管理内置模板。
    // 空 = 不启用(沿用 v1.0.0 默认行为)。非空 = 作为系统模板的统一存放根。
    [JsonPropertyName("system_template_library_dir")] public string SystemTemplateLibraryDir { get; set; } = "";
    // v1.0.0.x: 内置模板 seed 开关 — true 时 SettingsDefaults.SeedBuiltInTemplatesIfMissing 不再自动
    // 填充 ComfyUI/Forge/OpenVoice/HunyuanVideo/CogVideoX/HivisionIDPhotos 内置模板,允许 templates 块为空。设为 false 或删除字段恢复默认 seed 行为。
    // 内部逃生口,不暴露在 Settings UI(GUI 不需要这个一次性操作)。
    [JsonPropertyName("disable_built_in_templates_seed")] public bool DisableBuiltInTemplatesSeed { get; set; } = false;
    // v1.0.0 multi-template (T12):老 template_comfyui_dir JSON 字段已移除,
    // 由 SettingsDefaults.TryMigrateOldTemplateComfyuiDir(s, rawJson) 在加载阶段
    // 通过 JsonDocument.Parse 读取老 JSON 一次性迁移到 Templates["ComfyUI"].LocalSourceDir。
    [JsonPropertyName("templates")] public Dictionary<string, TemplateConfig> Templates { get; set; } = new();
    [JsonPropertyName("default_python_version")] public string DefaultPythonVersion { get; set; } = "3.10";
    [JsonPropertyName("envs_dir")] public string EnvsDir { get; set; } = "";
    [JsonPropertyName("global_nodes_dir")] public string GlobalNodesDir { get; set; } = "";
    // v0.6.5.9: Catalog 主页「下载」按钮的目标目录。template-style,默认子目录名 "local-nodes"。
    [JsonPropertyName("local_node_directory")] public string LocalNodeDirectory { get; set; } = "";
    // v1.0.0.x #577:本地常用节点根目录 — env 行「安装本地常用」按钮从该目录枚举子包,
    // 逐个 copy 到 env/custom_nodes/<子包名> + pip install -r requirements.txt。
    // 默认 seed 相对 "./localnodes"(项目根下),空 = 不启用(按钮点击会报错提示)。
    [JsonPropertyName("local_nodes_directory")] public string LocalNodesDirectory { get; set; } = "";
    // v0.6.10 + v0.6.22+:全局默认 Models 目录。两用:
    // 1) env-create 时把 <env-root>/ComfyUI/models junction 到此路径,作为新 env 的默认 models 位置;每 env 可独立覆盖
    // 2) 模型市场下载目录(原 ModelsDirectory 已硬删,所有下载直接走这里)
    // 空字符串 = 不动 env 的 models 目录(沿用项目根 fallback)。
    [JsonPropertyName("default_models_directory")]
    public string DefaultModelsDirectory { get; set; } = "";
    /// <summary>
    /// v1.0.0.x (2026-08-29):Forge env 模型目录 per-type 覆盖(6 个 ComfyUI 风格
    /// 子目录:checkpoints / loras / vae / embeddings / hypernetworks / controlnet)。
    /// 任意字段非空 → <see cref="ProcessLauncher.BuildStartCommand"/> 把该
    /// 绝对路径以对应 --*dir CLI arg 注入 Forge 启动命令(Forge fork 保留的
    /// 标准 A1111 args,modules/cmd_args.py line 27-29 / 36 / 38 / 140);
    /// 字段空 → 跳过该 arg,Forge 走 cmd_args.py 内置 default
    /// (embeddings=data_path/embeddings;hypernetworks=models_path/hypernetworks;
    /// 其他 None = 不挂载)。
    /// 改后下次 Forge env 启动生效,无需手动重写文件(早期 eab383d 的 yaml 方案
    /// 已撤回 —— Forge fork 不读 extra_model_paths.yaml)。
    /// </summary>
    [JsonPropertyName("forge_paths")]
    public ForgePaths ForgePaths { get; set; } = new();
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

    // v0.6.22.x:ModelScope 国内模型源 — 默认 disabled(避免新装用户没配 token 看到空结果,
    // 需要时手动勾选;镜像 HF/CivitAI 同模式)。
    [JsonPropertyName("model_source_modelscope_enabled")]
    public bool ModelSourceModelScopeEnabled { get; set; } = false;
    [JsonPropertyName("modelscope_api_token")]
    public string ModelSourceModelScopeApiToken { get; set; } = "";
    [JsonPropertyName("model_source_modelscope_use_mirror")]
    public bool ModelSourceModelScopeUseMirror { get; set; } = false;
    [JsonPropertyName("model_source_modelscope_mirror_url")]
    public string ModelSourceModelScopeMirrorUrl { get; set; } = "";
    [JsonPropertyName("model_source_modelscope_proxy_mode")]
    public ModelSourceProxyMode ModelSourceModelScopeProxyMode { get; set; } = ModelSourceProxyMode.InheritGlobal;

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
    // v1.0.0.x (2026-09-05) feat/nodelist-redesign:全局 GitHubToken 字段从
    // SettingsView 移除(节点源走自定义 server,不再直接调 GitHub)。
    // v1.0.0.x (2026-09-15) feat/nodelist-source-config:节点源 host/token 完全
    // 走 SQLite nodelist_source_config;**catalog 拉各节点 release/license/stars 等
    // metadata 仍调 GitHub API**,需要这个 OAuth token(空 = 未鉴权,rate limit 60/h)。
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
        target.DefaultPythonVersion = source.DefaultPythonVersion;
        target.EnvsDir = source.EnvsDir;
        target.GlobalNodesDir = source.GlobalNodesDir;
        target.LocalNodeDirectory = source.LocalNodeDirectory;
        target.LocalNodesDirectory = source.LocalNodesDirectory;
        target.DefaultModelsDirectory = source.DefaultModelsDirectory;
        // v1.0.0.x:ForgePaths 子对象 — 6 个 nullable string 字段就地复制
        // (sub-object 引用共享,逐字段写更明确)。
        target.ForgePaths.CheckpointsDir = source.ForgePaths.CheckpointsDir;
        target.ForgePaths.LorasDir = source.ForgePaths.LorasDir;
        target.ForgePaths.VaeDir = source.ForgePaths.VaeDir;
        target.ForgePaths.EmbeddingsDir = source.ForgePaths.EmbeddingsDir;
        target.ForgePaths.HypernetworksDir = source.ForgePaths.HypernetworksDir;
        target.ForgePaths.ControlnetDir = source.ForgePaths.ControlnetDir;
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
        target.ModelSourceModelScopeEnabled = source.ModelSourceModelScopeEnabled;
        target.ModelSourceModelScopeApiToken = source.ModelSourceModelScopeApiToken;
        target.ModelSourceModelScopeUseMirror = source.ModelSourceModelScopeUseMirror;
        target.ModelSourceModelScopeMirrorUrl = source.ModelSourceModelScopeMirrorUrl;
        target.ModelSourceModelScopeProxyMode = source.ModelSourceModelScopeProxyMode;
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
        // v1.0.0.x (2026-09-15) feat/nodelist-source-config:删 GitHub kind 字段,
        // 统一走 NodelistServerUrl + NodelistApiToken + RefreshFetchVersions/Metadata。
        target.NodelistDirectory = source.NodelistDirectory;
        target.NodelistServerUrl = source.NodelistServerUrl;
        target.NodelistApiToken = source.NodelistApiToken;
        target.NodelistLastIngestAt = source.NodelistLastIngestAt;
        target.RefreshFetchVersions = source.RefreshFetchVersions;
        target.RefreshFetchMetadata = source.RefreshFetchMetadata;
        // v0.6.13-B Catalog metadata 开关 + v0.6.11 T3 version 开关 ——
        // 0cb2b6e8 nodelist-redesign 删字段时连带删了这两行 CopyInto,导致
        // SettingsFetchCatalogMetadataTests.CopyInto_CopiesFetchCatalogMetadata 失败;
        // 顺手修。跟 nodelist 无关,纯 catalog 拉取开关。
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
        // v1.0.0 multi-template: Templates dict — 清空 + AddRange(Dictionary 没有 AddRange,
        // 用 Clear + foreach[key]=value 复制,保持同一引用语义不变)
        target.Templates.Clear();
        foreach (var kv in source.Templates)
        {
            target.Templates[kv.Key] = kv.Value;
        }
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

/// <summary>
/// v1.0.0.x:Forge env extra_model_paths.yaml 的 per-type 模型目录覆盖。
/// 6 个 ComfyUI 风格子目录(checkpoints / loras / vae / embeddings /
/// hypernetworks / controlnet)各自的绝对路径;空 = 走
/// 路径由 <see cref="ProcessLauncher.BuildStartCommand"/> 直接读,每个非空字段
/// 生成对应 --*dir CLI arg 注入 Forge 启动命令。
/// </summary>
public sealed class ForgePaths
{
    [JsonPropertyName("checkpoints_dir")]
    public string? CheckpointsDir { get; set; }
    [JsonPropertyName("loras_dir")]
    public string? LorasDir { get; set; }
    [JsonPropertyName("vae_dir")]
    public string? VaeDir { get; set; }
    [JsonPropertyName("embeddings_dir")]
    public string? EmbeddingsDir { get; set; }
    [JsonPropertyName("hypernetworks_dir")]
    public string? HypernetworksDir { get; set; }
    [JsonPropertyName("controlnet_dir")]
    public string? ControlnetDir { get; set; }
}

