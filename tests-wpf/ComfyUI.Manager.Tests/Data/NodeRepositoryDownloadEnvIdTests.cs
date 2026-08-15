using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v0.6.15.3 hotfix:<see cref="NodeRepository.Upsert"/> 用 <c>env_id=""</c>
/// (local-download sentinel)写 <c>scanned_nodes</c> 时,在有 FK 到 <c>environments.id</c>
/// 的老 DB 上抛 SQLite Error 19(<c>FOREIGN KEY constraint failed</c>)。
///
/// 复现 + 回归测试:
/// 1. 手工给 scanned_nodes 加 FK → 模拟老 DB
/// 2. 调 NodeRepository.Upsert(env_id="") → 老 schema 下应崩
/// 3. 跑 SqliteConnectionFactory.Open() 跑 migration(INSERT OR IGNORE sentinel) → FK 通过
/// 4. 验证 sentinel 行存在 + Upsert 不再崩
/// </summary>
public sealed class NodeRepositoryDownloadEnvIdTests : IDisposable
{
    private readonly string _dbPath;

    public NodeRepositoryDownloadEnvIdTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"fk-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>
    /// 模拟老 DB schema:CREATE TABLE 带 FOREIGN KEY(env_id) REFERENCES environments(id)。
    /// </summary>
    private void OpenWithLegacySchema(out SqliteConnection conn)
    {
        conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE environments (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                root_path TEXT NOT NULL,
                comfyui_layout TEXT NOT NULL,
                base_python_path TEXT NOT NULL DEFAULT '',
                python_version TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE scanned_nodes (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                package_path TEXT NOT NULL,
                version TEXT,
                author TEXT,
                description TEXT,
                class_mappings TEXT NOT NULL DEFAULT '[]',
                status TEXT NOT NULL DEFAULT 'enabled',
                scan_meta TEXT NOT NULL DEFAULT '{}',
                last_scanned_at TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'env',
                FOREIGN KEY(env_id) REFERENCES environments(id) ON DELETE CASCADE
            );
            INSERT INTO environments (id, name, root_path, comfyui_layout)
                VALUES ('env-real', 'real', '/x', 'standalone');";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Upsert_EnvIdEmpty_WithoutSentinel_FailsWithForeignKey()
    {
        // 复现 crash:FK 启用 + 没 sentinel 行 → 直接 INSERT(env_id="") 应抛
        // 注意:走 SqliteCommand 而不是 NodeRepository,因为后者 ctor 调
        // SqliteConnectionFactory.Open() → 自动跑 migration 塞 sentinel,
        // 就测不到「没 sentinel」场景。
        OpenWithLegacySchema(out var conn);
        using (conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO scanned_nodes
                    (id, env_id, package, package_path, source)
                VALUES
                    ('pkg-fk', '', 'pkg-fk', '/x/pkg-fk', 'download')";
            var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            // SQLite Error 19 = SQLITE_CONSTRAINT
            Assert.Equal(19, ex.SqliteErrorCode);
        }
    }

    [Fact]
    public void SqliteConnectionFactory_Open_InsertsSentinelEnvironment_Row()
    {
        // 关键:SqliteConnectionFactory.Open() 跑 migration 后,environments 表应多一行 id=''
        OpenWithLegacySchema(out _);
        // 关掉连接(SqliteConnectionFactory.Open() 自己 new 一个)
        SqliteConnection.ClearAllPools();

        _ = new SqliteConnectionFactory(_dbPath).Open();

        // 重新开连接读
        using var verify = new SqliteConnection($"Data Source={_dbPath}");
        verify.Open();
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM environments WHERE id = ''";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Upsert_EnvIdEmpty_AfterMigration_Succeeds()
    {
        // 端到端:FK 老 schema + 跑 migration → Upsert(env_id="") 不再抛
        OpenWithLegacySchema(out _);
        SqliteConnection.ClearAllPools();

        // 跑 migration(SqliteConnectionFactory.Open 内部跑 InitSchemaIfMissing)
        _ = new SqliteConnectionFactory(_dbPath).Open();

        var repo = new NodeRepository(new SqliteConnectionFactory(_dbPath));
        repo.Upsert(new ScannedNode
        {
            Id = "pkg-after-migration",
            EnvId = "",
            Package = "pkg-after-migration",
            PackagePath = "/x/pkg-after-migration",
            Source = "download",
            RepositoryUrl = "https://github.com/owner/repo",
        });

        // 验证行真的写进去了
        var loaded = repo.Get("pkg-after-migration");
        Assert.NotNull(loaded);
        Assert.Equal("", loaded!.EnvId);
        Assert.Equal("download", loaded.Source);
        Assert.Equal("https://github.com/owner/repo", loaded.RepositoryUrl);
    }

    [Fact]
    public void Upsert_EnvIdReal_AfterMigration_StillSucceeds()
    {
        // 回归:env 装路径不受影响(env_id 是真值本来就匹配 FK)
        OpenWithLegacySchema(out _);
        SqliteConnection.ClearAllPools();

        _ = new SqliteConnectionFactory(_dbPath).Open();

        var repo = new NodeRepository(new SqliteConnectionFactory(_dbPath));
        repo.Upsert(new ScannedNode
        {
            Id = "pkg-env",
            EnvId = "env-real",  // FK 应自动通过(老 migration 已建该行)
            Package = "pkg-env",
            PackagePath = "/x/pkg-env",
            Source = "env",
        });
        var loaded = repo.Get("pkg-env");
        Assert.NotNull(loaded);
        Assert.Equal("env-real", loaded!.EnvId);
    }

    [Fact]
    public void Upsert_EnvIdEmpty_IsIdempotent_RerunMigration_NoError()
    {
        // 跑两次 migration 不应冲突(INSERT OR IGNORE 语义)
        OpenWithLegacySchema(out _);
        SqliteConnection.ClearAllPools();

        _ = new SqliteConnectionFactory(_dbPath).Open();
        SqliteConnection.ClearAllPools();
        _ = new SqliteConnectionFactory(_dbPath).Open();

        using var verify = new SqliteConnection($"Data Source={_dbPath}");
        verify.Open();
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM environments WHERE id = ''";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void EnvironmentRepository_ListAll_FiltersSentinelRow()
    {
        // 关键:EnvironmentRepository.ListAll() 必须在 env 列表里 *隐藏* id='' sentinel 行,
        // 不让 UI 看到 "(local download)" 假 env,sentinel 只服务于 FK 完整性。
        // 用 TestDb fixture(完整 schema,跟 EnvRow 其他列完全兼容)而不是 OpenWithLegacySchema(只
        // 模拟 FK 兼容,其他列不全)。
        using var db = new ComfyUI.Manager.Tests.Fakes.TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);

        // Factory.Open() 跑 migration → 塞 sentinel(id='')
        envRepo.Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = "env-real",
            Name = "real",
            RootPath = "/x",
            ComfyuiLayout = "standalone",
        });

        var list = envRepo.ListAll();
        Assert.Single(list);
        Assert.Equal("env-real", list[0].Id);

        Assert.Null(envRepo.Get(""));  // Get("") 也返回 null
        Assert.NotNull(envRepo.Get("env-real"));
    }
}