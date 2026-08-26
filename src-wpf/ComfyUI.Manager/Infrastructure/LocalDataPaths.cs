using System.IO;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.16: 所有持久化数据的本地路径源。原本 .manager/ 在 projectRoot 下 — 单一文件夹
/// 备份即可带走所有用户数据。
///
/// v1.0.0.1 (settings-to-inf):用户配置 settings/ui-preferences/base-env-profiles
/// 从 .manager/ 移到 config/ 跟 sidebar.inf 同目录,符合"config 目录放用户配置"原则。
/// .manager/ 留缓存 + state.db(SQLite)。
///
/// v1.0.0.x (#569):用户决定把 **所有** 数据都合并到 config/ —— 不再有 .manager/
/// 子目录,state.db / *.json cache / *.inf 全在同一个 projectRoot/config/ 下。
/// LocalDataMigrationService 在首次启动时把老 .manager/ 一次性迁到 config/。
///
/// App.xaml.cs 在 startup 算一次 projectRoot,构造本类,然后把它当 ctor 注入
/// 给所有需要持久化路径的 service。
///
/// 目录布局:
/// <code>
///   &lt;projectRoot&gt;/
///     config/                                    所有持久化数据 + 用户配置
///       settings.inf                             主用户配置
///       ui-preferences.inf                       UI 偏好
///       base-env-profiles.inf                    base env profile 列表
///       sidebar.inf                              侧栏菜单 enable
///       state.db                                 SQLite 数据存储(CivitaiHashCache 等)
///       catalog_metadata_cache.json              缓存
///       pytorch_catalog_cache.json               缓存
///       pytorch_versions_cache.json              缓存
///       release_cache.json                       缓存
/// </code>
/// </summary>
public sealed class LocalDataPaths
{
    /// <summary>主目录,所有持久化数据 + 用户配置都落这里(v1.0.0.x 合并 .manager/ + config/)。</summary>
    public string Directory { get; }

    /// <summary>
    /// 用户配置目录 —— 跟 <see cref="Directory"/> 同源(保留属性是为了调用方兼容,1 目录 = 2 名字)。
    /// v1.0.0.1 settings-to-inf 时把 INF 类配置从 .manager/ 搬到 config/ 子目录;v1.0.0.x
    /// #569 把 data 类(state.db / cache .json)也搬过来,两个目录变成同一个。
    /// </summary>
    public string ConfigDirectory { get; }

    // —— state.db + 缓存(原 .manager/ 内容,现合并到 config/) ——
    public string StateDbFile => Path.Combine(Directory, "state.db");
    public string PyTorchCatalogCacheFile => Path.Combine(Directory, "pytorch_catalog_cache.json");
    public string PyTorchVersionsCacheFile => Path.Combine(Directory, "pytorch_versions_cache.json");
    public string ReleaseCacheFile => Path.Combine(Directory, "release_cache.json");
    public string CatalogMetadataCacheFile => Path.Combine(Directory, "catalog_metadata_cache.json");

    // —— 用户配置(INF) ——
    public string SettingsInfFile => Path.Combine(Directory, "settings.inf");
    public string UiPreferencesInfFile => Path.Combine(Directory, "ui-preferences.inf");
    public string BaseEnvProfilesInfFile => Path.Combine(Directory, "base-env-profiles.inf");

    // —— 兼容老路径(用于一次性迁移 + 兜底读) ——
    /// <summary>老 settings.json,首次启动 settings 迁移完后会被删除。</summary>
    public string SettingsFile => Path.Combine(Directory, "settings.json");
    /// <summary>老 base_env_profiles.json,BaseEnvProfileLoader 读 fallback 用(本身无 Save)。</summary>
    public string BaseEnvProfilesFile => Path.Combine(Directory, "base_env_profiles.json");

    public LocalDataPaths(string projectRoot)
    {
        // v1.0.0.x #569:合并 .manager/ + config/ → 单个 projectRoot/config/。
        // 老 .manager/ 数据由 LocalDataMigrationService.RunIfNeeded() 启动期一次性迁移过来。
        Directory = Path.Combine(projectRoot, "config");
        ConfigDirectory = Directory;
        // 静态 System.IO.Directory 类与属性同名 —— 用全限定名调用。
        System.IO.Directory.CreateDirectory(Directory);
    }
}