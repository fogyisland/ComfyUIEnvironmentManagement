using System.IO;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.16: 所有持久化数据的本地路径源。取代原来的 %APPDATA%/ComfyUI-Manager/,
/// 让 .manager/ 目录在 projectRoot 下 — 单一文件夹备份即可带走所有用户数据。
///
/// 单一职责:提供路径字符串,不做 I/O 决策(除了构造时确保目录存在)。
/// App.xaml.cs 在 startup 算一次 projectRoot,构造本类,然后把它当 ctor 注入
/// 给所有需要持久化路径的 service。
///
/// v1.0.0.1 (settings-to-inf):用户配置 settings/ui-preferences/base-env-profiles
/// 从 .manager/ 移到 config/ 跟 sidebar.inf 同目录,符合"config 目录放用户配置"
/// 原则。.manager/ 留缓存 + state.db(SQLite)。
///
/// 目录布局:
/// <code>
///   &lt;projectRoot&gt;/
///     .manager/
///       state.db                                   SQLite 数据存储(CivitaiHashCache 等)
///       catalog_metadata_cache.json                缓存
///       pytorch_catalog_cache.json                 缓存
///       pytorch_versions_cache.json                缓存
///       release_cache.json                         缓存
///     config/
///       settings.inf                               主用户配置
///       ui-preferences.inf                         UI 偏好
///       base-env-profiles.inf                      base env profile 列表
///       sidebar.inf                                侧栏菜单 enable(已是 INF)
/// </code>
/// </summary>
public sealed class LocalDataPaths
{
    public string Directory { get; }
    /// <summary>用户配置目录,跟 <c>config/sidebar.inf</c> 同目录。构造时自动 CreateDirectory。</summary>
    public string ConfigDirectory { get; }

    // —— .manager/ 数据 + 缓存 ——
    public string StateDbFile => Path.Combine(Directory, "state.db");
    public string PyTorchCatalogCacheFile => Path.Combine(Directory, "pytorch_catalog_cache.json");
    public string PyTorchVersionsCacheFile => Path.Combine(Directory, "pytorch_versions_cache.json");
    public string ReleaseCacheFile => Path.Combine(Directory, "release_cache.json");
    public string CatalogMetadataCacheFile => Path.Combine(Directory, "catalog_metadata_cache.json");

    // —— config/ 用户配置(INF) ——
    public string SettingsInfFile => Path.Combine(ConfigDirectory, "settings.inf");
    public string UiPreferencesInfFile => Path.Combine(ConfigDirectory, "ui-preferences.inf");
    public string BaseEnvProfilesInfFile => Path.Combine(ConfigDirectory, "base-env-profiles.inf");

    // —— v1.0.0.1 兼容:仍提供老 JSON 路径供一次性迁移使用 ——
    /// <summary>老 settings.json,首次启动 settings 迁移完后会被删除。</summary>
    public string SettingsFile => Path.Combine(Directory, "settings.json");
    /// <summary>老 base_env_profiles.json,BaseEnvProfileLoader 读 fallback 用(本身无 Save)。</summary>
    public string BaseEnvProfilesFile => Path.Combine(Directory, "base_env_profiles.json");

    public LocalDataPaths(string projectRoot)
    {
        Directory = Path.Combine(projectRoot, ".manager");
        ConfigDirectory = Path.Combine(projectRoot, "config");
        // 静态 System.IO.Directory 类与属性同名 —— 用全限定名调用。
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(ConfigDirectory);
    }
}