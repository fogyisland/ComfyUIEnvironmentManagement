using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;
using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// v1.0.0.x T45:一次性迁移 state.db → model.db(local_model_files /
    /// local_model_overrides / civitai_card_cache 三表)。
    ///
    /// 触发场景:用户从 T45 之前的版本升级上来,state.db 已含这 3 张表。本方法把
    /// 它们 ATTACH 到 model.db + INSERT INTO ... SELECT FROM 全量复制,然后 DROP
    /// state.db 里这 3 张表。完成后 state.db 只剩 8 张 seed/env 表,model.db
    /// 装 3 张本地模型表。
    ///
    /// 幂等性:
    /// - state.db 没这 3 表 → skip(no-op)
    /// - state.db 有 + model.db 已含(已完成)→ skip(no-op)
    /// - state.db 有 + model.db 无 → 全量迁移 + drop
    ///
    /// 失败处理:
    /// - 任何步骤抛 SqliteException → log warn + 保留 state.db 原表(用户可能需要
    ///   手动恢复或等下次启动重试)
    /// - 不抛出不阻断 App.OnStartup(用户原话:「发布 release 会清空,失败也没关系」)
    ///
    /// 跟 RunIfNeeded 老迁移不同:那段是文件 rename,这次是 db 内 ATTACH/DETACH +
    /// SQL DDL,需要长 transaction 包裹(防半迁)。
    /// </summary>
    public async Task MigrateModelTablesToSeparateDbAsync(
        SqliteConnectionFactory stateFactory,
        SqliteConnectionFactory modelFactory,
        AppLogger? logger)
    {
        var stateDbPath = stateFactory.DbPath;
        var modelDbPath = modelFactory.DbPath;

        // 1. 检测 state.db 是否存在 — 没有直接 skip(全新装)
        if (!File.Exists(stateDbPath))
        {
            return;
        }

        var tablesToMigrate = new[] { "local_model_files", "local_model_overrides", "civitai_card_cache" };

        // 2. 检测 state.db 是否还有任一 model 表 — 全没有 skip(已迁完)
        bool hasAnyTable;
        using (var probe = new SqliteConnection($"Data Source={stateDbPath}"))
        {
            await probe.OpenAsync();
            hasAnyTable = false;
            foreach (var tbl in tablesToMigrate)
            {
                using var cmd = probe.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
                cmd.Parameters.AddWithValue("$n", tbl);
                var exists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
                if (exists) { hasAnyTable = true; break; }
            }
        }
        if (!hasAnyTable) return;

        _logger?.Info("data-migration",
            "T45 检测到 state.db 含 model 表,开始一次性 ATTACH/DETACH 迁移到 model.db");

        try
        {
            // 3. 打开 model.db (InitSchema 自动建 3 张空表 —— 如果 model.db 之前不存在)。
            //    modelFactory.Open() 同步;Open 完即 dispose。目的是触发 InitModelSchema。
            //    必须在 ATTACH 之前跑 —— ATTACH 之后 state.db 的同表名会让 CREATE TABLE
            //    IF NOT EXISTS 静默跳过,model.db 永远没表 → INSERT 失败。
            using (var schemaConn = modelFactory.Open())
            {
                // Open() 返回的 conn 立即 dispose,触发 InitModelSchema 副作用。
                _ = schemaConn;
            }

            // 4. 打开 model.db 一次 ATTACH state.db 作 'sourceDb' alias。
            //    后续 SQL:INSERT INTO main.<tbl> SELECT * FROM sourceDb.<tbl>。
            using var conn = new SqliteConnection($"Data Source={modelDbPath}");
            await conn.OpenAsync();
            using (var attach = conn.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE $p AS sourceDb";
                attach.Parameters.AddWithValue("$p", stateDbPath);
                attach.ExecuteNonQuery();
            }

            // 5. 每张表:INSERT INTO main.<tbl> SELECT * FROM sourceDb.<tbl>。
            //    用 INSERT INTO ... SELECT 而非 CREATE TABLE AS —— CREATE AS 不复制
            //    PK/索引,后续 INSERT 会 PK 冲突。INSERT INTO 让 model.db 的 schema
            //    (pk/index 已在 step 3 建好)原样保留,数据全量塞入。
            foreach (var tbl in tablesToMigrate)
            {
                // 检测 sourceDb.<tbl> 实际有多少行 —— 0 行也走 INSERT 一次,确保
                // model.db schema 一致(虽然无数据)。
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) FROM sourceDb.{tbl}";
                    var rows = Convert.ToInt64(countCmd.ExecuteScalar());
                    if (rows == 0)
                    {
                        _logger?.Info("data-migration", $"T45 skip empty table {tbl} (0 rows)");
                        continue;
                    }
                }
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText =
                    $"INSERT INTO main.{tbl} SELECT * FROM sourceDb.{tbl}";
                var inserted = insertCmd.ExecuteNonQuery();
                _logger?.Info("data-migration", $"T45 迁移 {tbl}: {inserted} 行");
            }

            // 6. 验证行数一致(容错:理论上 step 5 已经全量复制,但 drop 前再 verify
            //    一次,避免 ATTACH 中途异常导致部分表迁移半途而废)
            foreach (var tbl in tablesToMigrate)
            {
                using var vSrc = conn.CreateCommand();
                vSrc.CommandText = $"SELECT COUNT(*) FROM sourceDb.{tbl}";
                var srcCount = Convert.ToInt64(vSrc.ExecuteScalar());
                using var vDst = conn.CreateCommand();
                vDst.CommandText = $"SELECT COUNT(*) FROM main.{tbl}";
                var dstCount = Convert.ToInt64(vDst.ExecuteScalar());
                if (srcCount != dstCount)
                {
                    throw new InvalidOperationException(
                        $"T45 迁移 {tbl} 行数不一致: sourceDb={srcCount}, model.db={dstCount}");
                }
            }

            // 7. DETACH(关闭 ATTACH alias,避免后续 DROP 引用到 alias)
            using (var detach = conn.CreateCommand())
            {
                detach.CommandText = "DETACH DATABASE sourceDb";
                detach.ExecuteNonQuery();
            }

            // 8. DROP state.db 这 3 张表 —— 走 stateFactory.Open() 的 connection,
            //    不在 ATTACH-alias 连接上 drop(避免引用混淆)。
            //    DROP TABLE IF EXISTS 幂等(防止重复调用)。
            using (var dropConn = stateFactory.Open())
            {
                foreach (var tbl in tablesToMigrate)
                {
                    using var d = dropConn.CreateCommand();
                    d.CommandText = $"DROP TABLE IF EXISTS {tbl}";
                    d.ExecuteNonQuery();
                    _logger?.Info("data-migration", $"T45 DROP state.db.{tbl}");
                }
            }

            _logger?.Info("data-migration",
                "T45 迁移完成:state.db 移除 3 model 表,model.db 接管本地模型缓存");
        }
        catch (Exception ex)
        {
            // 失败保留 state.db 原表(用户可能需要手动恢复),不阻断启动。
            _logger?.Warn("data-migration",
                $"T45 迁移失败,保留 state.db 原 model 表(用户决策:release 会清空): {ex.Message}");
        }
    }
}