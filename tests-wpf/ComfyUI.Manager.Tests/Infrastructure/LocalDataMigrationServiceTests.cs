using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// 验证一次性数据迁移 service —— 把 .manager/(v1.0.0.x 之前的本地数据) 和
/// %APPDATA%/ComfyUI-Manager/(v0.6.16 之前的远古目录) 都合并到 &lt;projectRoot&gt;/config/。
///
/// 每个 test 用唯一 temp dir 模拟旧 + 新目录,避免污染真实 APPDATA。
/// 通过 internal ctor seam 注入 fake 旧目录路径。
/// </summary>
public sealed class LocalDataMigrationServiceTests : IDisposable
{
    private readonly string _scratchRoot;
    private readonly string _fakeAppDataDir;
    private readonly string _fakeLegacyManagerDir;

    public LocalDataMigrationServiceTests()
    {
        _scratchRoot = Path.Combine(
            Path.GetTempPath(), "local-data-migration-" + Guid.NewGuid().ToString("N"));
        _fakeAppDataDir = Path.Combine(_scratchRoot, "fake-appdata", "ComfyUI-Manager");
        // legacyManagerDir 在 projectRoot/config/ 旁边 — 模拟 <projectRoot>/.manager/
        // 测试用 scratchRoot 作为 projectRoot 的父目录,_fakeLegacyManagerDir 放 scratchRoot/.manager/
        _fakeLegacyManagerDir = Path.Combine(_scratchRoot, ".manager");
        Directory.CreateDirectory(_fakeAppDataDir);
        Directory.CreateDirectory(_fakeLegacyManagerDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private (LocalDataPaths paths, LocalDataMigrationService service) MakeService(string projectRoot)
    {
        var paths = new LocalDataPaths(projectRoot);
        var service = new LocalDataMigrationService(
            paths,
            logger: null,
            appDataOldDir: _fakeAppDataDir,
            legacyManagerDir: _fakeLegacyManagerDir);
        return (paths, service);
    }

    private static string NewProjectRoot(string scratchRoot) =>
        Path.Combine(scratchRoot, "project-" + Guid.NewGuid().ToString("N"));

    // ============== v0.6.16 APPDATA 迁移测试(legacy) ==============

    [Fact]
    public void RunIfNeeded_NoOldDirs_ReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        Directory.Delete(_fakeAppDataDir, recursive: true);
        Directory.Delete(_fakeLegacyManagerDir, recursive: true);

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.False(ran);
        Assert.True(Directory.Exists(paths.Directory)); // config/ 仍被 LocalDataPaths ctor 建出来
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Directory));
    }

    [Fact]
    public void RunIfNeeded_AppDataHasFiles_NewDirEmpty_CopiesAndReturnsTrue()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"k\":1}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "state.db"), "fake-db");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.Equal("{\"k\":1}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
        // APPDATA 源目录保留(legacy less-destructive)
        Assert.True(Directory.Exists(_fakeAppDataDir));
    }

    [Fact]
    public void RunIfNeeded_NewDirAlreadyHasFiles_SkipsAndReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, service) = MakeService(projectRoot);
        // Pre-populate config/ — 模拟"已迁移"或"用户已写入"
        File.WriteAllText(Path.Combine(paths.Directory, "settings.json"), "{\"existing\":true}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"old\":true}");

        var ran = service.RunIfNeeded();

        Assert.False(ran); // idempotent:不重复迁移
        Assert.Equal("{\"existing\":true}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
    }

    [Fact]
    public void RunIfNeeded_AppDataHasSubdirs_OnlyCopiesFiles()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"k\":1}");
        var subDir = Path.Combine(_fakeAppDataDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.json"), "{\"nested\":true}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.Directory, "subdir")));
    }

    // ============== v1.0.0.x #569 .manager/ → config/ 迁移测试 ==============

    [Fact]
    public void RunIfNeeded_LegacyManagerHasFiles_NewDirEmpty_CopiesAndDeletesSource()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        // 模拟 v1.0.0.x 之前的 install:.manager/ 在 projectRoot 旁,有 state.db + cache
        // projectRoot = scratchRoot/project-X/,所以 .manager/ 应该在 scratchRoot/.manager/
        // 我们的 _fakeLegacyManagerDir 已经指向 scratchRoot/.manager/(跟 projectRoot 同级)
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "state.db"), "fake-state-db");
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "release_cache.json"), "{}");
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "pytorch_catalog_cache.json"), "{}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "release_cache.json")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "pytorch_catalog_cache.json")));
        Assert.Equal("fake-state-db", File.ReadAllText(Path.Combine(paths.Directory, "state.db")));
        // 合并完成后 .manager/ 被删除(用户要求"全并入 config/",不再保留)
        Assert.False(Directory.Exists(_fakeLegacyManagerDir));
    }

    [Fact]
    public void RunIfNeeded_BothLegacyManagerAndAppDataPresent_LegacyManagerWins()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        // 两段源都有文件;.manager/ 是更新数据,优先迁它;APPDATA 跳过
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "settings.json"), "{\"from_manager\":true}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"from_appdata\":true}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        // .manager/ 段先跑,迁移完 config/ 不空 → APPDATA 段跳过
        Assert.Equal("{\"from_manager\":true}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
        // .manager/ 已被删除
        Assert.False(Directory.Exists(_fakeLegacyManagerDir));
        // APPDATA 保留(legacy less-destructive)
        Assert.True(Directory.Exists(_fakeAppDataDir));
    }

    [Fact]
    public void RunIfNeeded_LegacyManagerHasSubdirs_OnlyCopiesFiles()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "state.db"), "fake");
        var subDir = Path.Combine(_fakeLegacyManagerDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.json"), "{}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.False(Directory.Exists(Path.Combine(paths.Directory, "subdir")));
    }

    // ============== v1.0.0.x T45 model.db 迁移测试 ==============

    [Fact]
    public async Task MigrateModelTables_NoStateDb_NoOp()
    {
        // 全新装(没 state.db),直接 skip。
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, _) = MakeService(projectRoot);

        var stateFactory = new SqliteConnectionFactory(paths.StateDbFile);
        var modelFactory = new SqliteConnectionFactory(paths.ModelDbFile, SchemaKind.Model);
        var migration = new LocalDataMigrationService(paths, logger: null,
            _fakeAppDataDir, _fakeLegacyManagerDir);

        await migration.MigrateModelTablesToSeparateDbAsync(stateFactory, modelFactory, null);

        Assert.False(File.Exists(paths.StateDbFile));
        Assert.False(File.Exists(paths.ModelDbFile));
    }

    [Fact]
    public async Task MigrateModelTables_NoModelTablesInStateDb_NoOp()
    {
        // state.db 已存在但没 model 表(只装 T45 之后才会出现的情况)→ skip。
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, _) = MakeService(projectRoot);
        // 先 InitStateSchema(state factory 第一次 Open 自动建 8 表 —— 没 model 表)
        var stateFactory = new SqliteConnectionFactory(paths.StateDbFile);
        using (var _ = stateFactory.Open()) { } // trigger InitSchema

        var modelFactory = new SqliteConnectionFactory(paths.ModelDbFile, SchemaKind.Model);
        var migration = new LocalDataMigrationService(paths, logger: null,
            _fakeAppDataDir, _fakeLegacyManagerDir);

        await migration.MigrateModelTablesToSeparateDbAsync(stateFactory, modelFactory, null);

        // state.db 不应被破坏(env 表还在);model.db 不应被建(因为没 model 表要迁)
        using (var verify = stateFactory.Open())
        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM environments";
            Assert.True(cmd.ExecuteScalar() is not null);
        }
        Assert.False(File.Exists(paths.ModelDbFile));
    }

    [Fact]
    public async Task MigrateModelTables_StateHasModelTables_RowsMovedAndStateTablesDropped()
    {
        // 模拟老 user:state.db 已有 local_model_files / local_model_overrides / civitai_card_cache
        // 各 1 行 → 迁移后 model.db 各有 1 行,state.db 这 3 表消失。
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, _) = MakeService(projectRoot);
        var stateFactory = new SqliteConnectionFactory(paths.StateDbFile);
        using (var seed = stateFactory.Open())
        {
            // 手动建 3 张老 schema 表 + 各插 1 行(state factory 默认走 State schema,
            // 不会自动建这 3 张 —— 模拟 T45 之前 user 老 state.db 的真实情况)
            using (var c = seed.CreateCommand())
            {
                c.CommandText = @"
                    CREATE TABLE local_model_files (
                        file_path TEXT PRIMARY KEY, source_id TEXT NOT NULL,
                        source_version_id TEXT NOT NULL, subfolder_name TEXT NOT NULL,
                        file_name TEXT NOT NULL, title TEXT NOT NULL,
                        kind TEXT NOT NULL, source TEXT NOT NULL, hash TEXT,
                        match_source TEXT, matched_detail_json TEXT,
                        preview_image_path TEXT, downloaded_at TEXT NOT NULL,
                        file_mtime TEXT NOT NULL, scanned_at TEXT NOT NULL
                    );
                    INSERT INTO local_model_files VALUES
                        ('/models/x.safetensors', 'civitai:42@v1', 'v1', 'loras', 'x.safetensors',
                         'X', 'LORA', 'Local', NULL, NULL, NULL, NULL, '2025-01-01', '2025-01-01', '2025-01-01');
                    CREATE TABLE local_model_overrides (
                        source_id TEXT PRIMARY KEY, override_path TEXT NOT NULL, updated_at TEXT NOT NULL
                    );
                    INSERT INTO local_model_overrides VALUES
                        ('civitai:42@v1', '/override/x.safetensors', '2025-01-01');
                    CREATE TABLE civitai_card_cache (
                        source_id TEXT PRIMARY KEY, detail_json TEXT NOT NULL, fetched_at TEXT NOT NULL
                    );
                    INSERT INTO civitai_card_cache VALUES
                        ('civitai:42@v1', '{}', '2025-01-01');";
                c.ExecuteNonQuery();
            }
        }

        var modelFactory = new SqliteConnectionFactory(paths.ModelDbFile, SchemaKind.Model);
        var migration = new LocalDataMigrationService(paths, logger: null,
            _fakeAppDataDir, _fakeLegacyManagerDir);

        await migration.MigrateModelTablesToSeparateDbAsync(stateFactory, modelFactory, null);

        // model.db 应有 1 行各表
        Assert.True(File.Exists(paths.ModelDbFile));
        using (var m = modelFactory.Open())
        using (var cmd = m.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM local_model_files";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT COUNT(*) FROM local_model_overrides";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT COUNT(*) FROM civitai_card_cache";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        // state.db 这 3 表应消失(env 表还在)
        using (var s = stateFactory.Open())
        using (var cmd = s.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='local_model_files'";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='local_model_overrides'";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='civitai_card_cache'";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            // env 表还在 —— InitStateSchema 的 sentinel INSERT 让其有 1 行(id='',
            // '(local download)'),所以这里查表存在用 sqlite_master 而非行数。
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='environments'";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task MigrateModelTables_AlreadyMigrated_Idempotent()
    {
        // 跑两次 → 第二次检测 state.db 没 model 表 → skip。
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, _) = MakeService(projectRoot);
        var stateFactory = new SqliteConnectionFactory(paths.StateDbFile);
        using (var seed = stateFactory.Open())
        {
            using var c = seed.CreateCommand();
            c.CommandText = @"
                CREATE TABLE local_model_files (
                    file_path TEXT PRIMARY KEY, source_id TEXT NOT NULL,
                    source_version_id TEXT NOT NULL, subfolder_name TEXT NOT NULL,
                    file_name TEXT NOT NULL, title TEXT NOT NULL,
                    kind TEXT NOT NULL, source TEXT NOT NULL, hash TEXT,
                    match_source TEXT, matched_detail_json TEXT,
                    preview_image_path TEXT, downloaded_at TEXT NOT NULL,
                    file_mtime TEXT NOT NULL, scanned_at TEXT NOT NULL
                );
                INSERT INTO local_model_files VALUES
                    ('/x', 's', 'v', 's', 'f', 't', 'LORA', 'Local', NULL, NULL, NULL, NULL,
                     '2025-01-01', '2025-01-01', '2025-01-01');";
            c.ExecuteNonQuery();
        }
        var modelFactory = new SqliteConnectionFactory(paths.ModelDbFile, SchemaKind.Model);
        var migration = new LocalDataMigrationService(paths, logger: null,
            _fakeAppDataDir, _fakeLegacyManagerDir);

        await migration.MigrateModelTablesToSeparateDbAsync(stateFactory, modelFactory, null);
        var rowsAfterFirst = 0L;
        using (var m = modelFactory.Open())
        using (var cmd = m.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM local_model_files";
            rowsAfterFirst = Convert.ToInt64(cmd.ExecuteScalar());
        }

        // 第二次跑:state.db 没表 → skip → model.db 行数不变
        await migration.MigrateModelTablesToSeparateDbAsync(stateFactory, modelFactory, null);
        using (var m = modelFactory.Open())
        using (var cmd = m.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM local_model_files";
            Assert.Equal(rowsAfterFirst, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }
}