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

    /// <summary>
    /// v0.6.15.9 bug fix:node 先 sentinel env_id='' download 进来,后来 env-scan
    /// 用同 id 跟真 env_id 调 Upsert,UPDATE clause 必须把 env_id 改过来。原版漏写
    /// env_id=excluded.env_id 导致行永远留在 env_id='',ListByEnv(realEnvId) 查不到
    /// → 节点管理面板显示"扫描不完整"(用户的 0246/1button 实际有目录却查不到)。
    /// </summary>
    [Fact]
    public void Upsert_SameIdDifferentEnvId_UpdatesEnvId()
    {
        // 用 TestDb 完整 schema(TestDb fixture 的 environment 行可以填 root_path / layout 全)
        using var db = new ComfyUI.Manager.Tests.Fakes.TestDb();
        var repo = new NodeRepository(db.Factory);

        // Step 1: catalog-local-download 走 sentinel env_id=''
        repo.Upsert(new ScannedNode
        {
            Id = "pkg-moved",
            EnvId = "",
            Package = "pkg-moved",
            PackagePath = "/local/pkg-moved",
            Source = "download",
            RepositoryUrl = "https://github.com/owner/repo",
        });
        Assert.Equal("", repo.Get("pkg-moved")!.EnvId);

        // Step 2: env-scan 重新 upsert 同 id,env_id 改成真值
        new EnvironmentRepository(db.Factory).Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = "env-real",
            Name = "real",
            RootPath = "/x",
            ComfyuiLayout = "standalone",
        });
        repo.Upsert(new ScannedNode
        {
            Id = "pkg-moved",
            EnvId = "env-real",
            Package = "pkg-moved",
            PackagePath = "/x/custom_nodes/pkg-moved",
            Source = "env",
        });

        // 关键断言:env_id 真的改成 env-real
        var loaded = repo.Get("pkg-moved");
        Assert.NotNull(loaded);
        Assert.Equal("env-real", loaded!.EnvId);
        Assert.Equal("env", loaded.Source);  // source 也跟改
        Assert.Equal("/x/custom_nodes/pkg-moved", loaded.PackagePath);
    }

    /// <summary>
    /// 同 bug 的端到端验证:sentinel 行被 env-scan upsert 后,ListByEnv(realEnvId) 必须返回它。
    /// 原 bug 表现:用户 env-d651ab01 的 custom_nodes/0246 跟 1button 已在 DB(env_id=''),
    /// 跑 rescan → 面板里只有 ComfyUI-Light-N-Color(env_id 是真值因为是主流程装的)。
    /// </summary>
    [Fact]
    public void ListByEnv_AfterScanUpsert_ReturnsRowsThatWereDownloadedSentinel()
    {
        using var db = new ComfyUI.Manager.Tests.Fakes.TestDb();
        var repo = new NodeRepository(db.Factory);

        // 3 个 catalog-download sentinel 行(env_id='')
        repo.Upsert(new ScannedNode { Id = "0246", EnvId = "", Package = "0246", PackagePath = "/local/0246", Source = "download" });
        repo.Upsert(new ScannedNode { Id = "1button", EnvId = "", Package = "1button", PackagePath = "/local/1button", Source = "download" });
        repo.Upsert(new ScannedNode { Id = "other", EnvId = "", Package = "other", PackagePath = "/local/other", Source = "download" });

        new EnvironmentRepository(db.Factory).Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = "env-real", Name = "real", RootPath = "/x", ComfyuiLayout = "standalone",
        });

        // rescan 仿真:2 个 node 改成真 env_id(像实际存在的目录),1 个保留 sentinel
        // (实际不存在目录所以 rescan 不调 Upsert)
        repo.Upsert(new ScannedNode { Id = "0246", EnvId = "env-real", Package = "0246", PackagePath = "/x/0246", Source = "env" });
        repo.Upsert(new ScannedNode { Id = "1button", EnvId = "env-real", Package = "1button", PackagePath = "/x/1button", Source = "env" });

        // ListByEnv 必须能查到这两个
        var inEnv = repo.ListByEnv("env-real");
        Assert.Equal(2, inEnv.Count);
        Assert.Contains(inEnv, n => n.Id == "0246");
        Assert.Contains(inEnv, n => n.Id == "1button");

        // 老的 sentinel "other" 仍 env_id=''(没 rescan 到所以没改)
        var stillSentinel = repo.Get("other");
        Assert.NotNull(stillSentinel);
        Assert.Equal("", stillSentinel!.EnvId);
    }
}