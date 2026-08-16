using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.16: 一次性数据迁移 — 把 %APPDATA%/ComfyUI-Manager/ 里的文件复制到
/// &lt;projectRoot&gt;/.manager/。幂等:仅当 .manager/ 为空 + 旧目录有文件时触发。
///
/// 启动期在 App.OnStartup 调 RunIfNeeded():在 SqliteConnectionFactory / SettingsRepository
/// 等 path-aware service 构造之前完成,这样它们第一次 Open() 看到的就是 .manager/ 里的文件。
///
/// 旧目录里的文件留在原地(用户可手动清理;不主动删是 less destructive)。
/// </summary>
public class LocalDataMigrationService
{
    private readonly LocalDataPaths _paths;
    private readonly AppLogger? _logger;
    private readonly string _oldDirOverride;

    /// <summary>
    /// 生产入口 —— 旧目录固定为 %APPDATA%/ComfyUI-Manager。
    /// </summary>
    public LocalDataMigrationService(LocalDataPaths paths, AppLogger? logger = null)
        : this(paths, logger,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ComfyUI-Manager"))
    {
    }

    /// <summary>
    /// 测试 seam —— 显式传入旧目录路径。生产代码走单参 ctor。
    /// </summary>
    internal LocalDataMigrationService(LocalDataPaths paths, AppLogger? logger, string oldDirOverride)
    {
        _paths = paths;
        _logger = logger;
        _oldDirOverride = oldDirOverride;
    }

    /// <summary>Returns true if migration ran (files were copied).</summary>
    public bool RunIfNeeded()
    {
        var oldDir = _oldDirOverride;

        if (!Directory.Exists(oldDir)) return false;
        // Already migrated (or user already populated .manager/) — skip
        if (Directory.EnumerateFileSystemEntries(_paths.Directory).Any()) return false;

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(oldDir))
        {
            var dest = Path.Combine(_paths.Directory, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: false);
            copied++;
        }

        _logger?.Info("data-migration",
            $"Migrated {copied} file(s) from {oldDir} → {_paths.Directory}");
        return copied > 0;
    }
}
