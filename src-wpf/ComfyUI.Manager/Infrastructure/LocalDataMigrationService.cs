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
/// v1.0.0.x (#569): 用户决定把所有数据合并到 &lt;projectRoot&gt;/config/,不再保留 .manager/。
/// 本 service 现在做两段迁移:
///   1) &lt;projectRoot&gt;/.manager/ → &lt;projectRoot&gt;/config/   (新合并,删 .manager/ 兜底)
///   2) %APPDATA%/ComfyUI-Manager/ → &lt;projectRoot&gt;/config/  (老兼容,源目录保留)
///
/// 顺序:.manager/ 优先(更新的数据源),只有在 config/ 还空的时候才进行下一段。
/// 这样 user 从 v1.0.0.x 之前升上来走 .manager/ 路径,没 .manager/ 但有 APPDATA 的
/// 远古用户(v0.6.16 之前)走第二段。
/// </summary>
public class LocalDataMigrationService
{
    private readonly LocalDataPaths _paths;
    private readonly AppLogger? _logger;
    private readonly string _appDataOldDir;
    private readonly string _legacyManagerDir;

    /// <summary>
    /// 生产入口 —— 默认 APPDATA 旧目录 = %APPDATA%/ComfyUI-Manager,.manager/ 旧目录 = projectRoot/.manager。
    /// </summary>
    public LocalDataMigrationService(LocalDataPaths paths, AppLogger? logger = null)
        : this(paths, logger,
            appDataOldDir: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ComfyUI-Manager"),
            legacyManagerDir: Path.Combine(Path.GetDirectoryName(paths.Directory) ?? paths.Directory, ".manager"))
    {
    }

    /// <summary>
    /// 测试 seam —— 显式传入两段迁移的旧目录路径。生产代码走单参 ctor。
    /// </summary>
    internal LocalDataMigrationService(
        LocalDataPaths paths,
        AppLogger? logger,
        string appDataOldDir,
        string legacyManagerDir)
    {
        _paths = paths;
        _logger = logger;
        _appDataOldDir = appDataOldDir;
        _legacyManagerDir = legacyManagerDir;
    }

    /// <summary>
    /// Returns true if any migration segment ran (files were copied).
    /// 两段迁移都尝试;.manager/ 段成功会删源目录,APPDATA 段保留源目录(legacy less-destructive)。
    /// </summary>
    public bool RunIfNeeded()
    {
        var anyRan = false;

        // 段 1:.manager/ → config/(v1.0.0.x #569 合并)
        // 仅当 config/ 还空 + .manager/ 有内容时执行;成功后删除 .manager/(兜底清理)
        if (!Directory.EnumerateFileSystemEntries(_paths.Directory).Any()
            && Directory.Exists(_legacyManagerDir))
        {
            var copied = CopyFilesFlat(_legacyManagerDir, _paths.Directory);
            if (copied > 0)
            {
                _logger?.Info("data-migration",
                    $"Migrated {copied} file(s) from {_legacyManagerDir} → {_paths.Directory} (legacy .manager/ merge)");
                anyRan = true;
            }
            try
            {
                Directory.Delete(_legacyManagerDir, recursive: true);
                _logger?.Info("data-migration",
                    $"Deleted legacy {_legacyManagerDir} after merge");
            }
            catch (Exception ex)
            {
                _logger?.Warn("data-migration",
                    $"Failed to delete legacy {_legacyManagerDir}: {ex.Message}");
            }
        }

        // 段 2:%APPDATA%/ComfyUI-Manager/ → config/(v0.6.16 兼容远古用户)
        // 源目录不删 —— legacy less-destructive;老用户可手动清理 %APPDATA%
        if (!Directory.EnumerateFileSystemEntries(_paths.Directory).Any()
            && Directory.Exists(_appDataOldDir))
        {
            var copied = CopyFilesFlat(_appDataOldDir, _paths.Directory);
            if (copied > 0)
            {
                _logger?.Info("data-migration",
                    $"Migrated {copied} file(s) from {_appDataOldDir} → {_paths.Directory} (legacy APPDATA)");
                anyRan = true;
            }
        }

        return anyRan;
    }

    /// <summary>
    /// 把源目录里所有顶层文件(不递归子目录)复制到目标目录。文件名冲突不覆盖。
    /// </summary>
    private static int CopyFilesFlat(string sourceDir, string destDir)
    {
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: false);
            copied++;
        }
        return copied;
    }
}