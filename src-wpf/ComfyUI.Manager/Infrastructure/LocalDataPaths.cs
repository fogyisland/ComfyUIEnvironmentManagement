using System.IO;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.16: 所有持久化数据的本地路径源。取代原来的 %APPDATA%/ComfyUI-Manager/,
/// 让 .manager/ 目录在 projectRoot 下 — 单一文件夹备份即可带走所有用户数据。
///
/// 单一职责:提供路径字符串,不做 I/O 决策(除了构造时确保目录存在)。
/// App.xaml.cs 在 startup 算一次 projectRoot,构造本类,然后把它当 ctor 注入
/// 给所有需要持久化路径的 service。
/// </summary>
public sealed class LocalDataPaths
{
    public string Directory { get; }
    public string SettingsFile => Path.Combine(Directory, "settings.json");
    public string StateDbFile => Path.Combine(Directory, "state.db");
    public string PyTorchCatalogCacheFile => Path.Combine(Directory, "pytorch_catalog_cache.json");
    public string PyTorchVersionsCacheFile => Path.Combine(Directory, "pytorch_versions_cache.json");
    public string ReleaseCacheFile => Path.Combine(Directory, "release_cache.json");
    public string CatalogMetadataCacheFile => Path.Combine(Directory, "catalog_metadata_cache.json");
    public string BaseEnvProfilesFile => Path.Combine(Directory, "base_env_profiles.json");

    public LocalDataPaths(string projectRoot)
    {
        Directory = Path.Combine(projectRoot, ".manager");
        // 静态 System.IO.Directory 类与属性同名 —— 用全限定名调用。
        System.IO.Directory.CreateDirectory(Directory);
    }
}
