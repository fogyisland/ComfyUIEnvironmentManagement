using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-15) feat/nodelist-source-config:NodelistSourceConfigRepository 单元测试。
///
/// **不依赖 TestDb**(T33 教训 — TestDb hardcoded schema 易跟生产漂移)。直接构造
/// <c>new SqliteConnectionFactory(Path.GetTempFileName())</c>,让生产
/// <c>SqliteConnectionFactory.InitSchema</c> 自己建表 + INSERT OR IGNORE sentinel 行。
/// </summary>
public class NodelistSourceConfigRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly NodelistSourceConfigRepository _repo;

    public NodelistSourceConfigRepositoryTests()
    {
        _dbPath = Path.GetTempFileName();
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new NodelistSourceConfigRepository(_factory);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Get_OnEmptyDb_ReturnsDefaults()
    {
        // SqliteConnectionFactory.InitSchema 跑过 sentinel INSERT OR IGNORE,
        // 所以单例行 id=1 一定存在;Get 返 ("custom", "", "", true, false, "")
        var cfg = _repo.Get();

        Assert.Equal("custom", cfg.Source);
        Assert.Equal("", cfg.ServerUrl);
        Assert.Equal("", cfg.ApiToken);
        Assert.True(cfg.RefreshVersions);
        Assert.False(cfg.RefreshMetadata);
        Assert.Equal("", cfg.UpdatedAt);
    }

    [Fact]
    public void Upsert_InsertsThenGet_ReturnsInserted()
    {
        _repo.Upsert(serverUrl: "https://api.example.com",
                     apiToken: "tok-abc-123",
                     refreshVersions: true,
                     refreshMetadata: true);

        var cfg = _repo.Get();

        Assert.Equal("custom", cfg.Source);
        Assert.Equal("https://api.example.com", cfg.ServerUrl);
        Assert.Equal("tok-abc-123", cfg.ApiToken);
        Assert.True(cfg.RefreshVersions);
        Assert.True(cfg.RefreshMetadata);
        // updated_at 是 ISO 8601 字符串,只要非空即可
        Assert.False(string.IsNullOrEmpty(cfg.UpdatedAt));
    }

    [Fact]
    public void Upsert_Twice_OverwritesSingleRow()
    {
        _repo.Upsert("https://first.example.com", "first-tok", true, false);
        _repo.Upsert("https://second.example.com", "second-tok", false, true);

        // 验证只 1 行(id=1 CHECK 约束 + ON CONFLICT 替换,不是 INSERT 多行)
        using var conn = _factory.Open();
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM nodelist_source_config";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        Assert.Equal(1, count);

        // 验证第二次写覆盖第一次
        var cfg = _repo.Get();
        Assert.Equal("https://second.example.com", cfg.ServerUrl);
        Assert.Equal("second-tok", cfg.ApiToken);
        Assert.False(cfg.RefreshVersions);
        Assert.True(cfg.RefreshMetadata);
    }

    [Fact]
    public void Upsert_NullToken_TreatedAsEmptyString()
    {
        // 入参契约允许 null(VM 在脏数据防御);应存为空串
        _repo.Upsert("https://api.example.com",
                     apiToken: null!,
                     refreshVersions: true,
                     refreshMetadata: false);

        var cfg = _repo.Get();
        Assert.Equal("", cfg.ApiToken);
        Assert.Equal("https://api.example.com", cfg.ServerUrl);
    }

    [Fact]
    public void Upsert_NullUrl_TreatedAsEmptyString()
    {
        _repo.Upsert(serverUrl: null!,
                     apiToken: "tok",
                     refreshVersions: true,
                     refreshMetadata: false);

        var cfg = _repo.Get();
        Assert.Equal("", cfg.ServerUrl);
        Assert.Equal("tok", cfg.ApiToken);
    }

    [Fact]
    public void Upsert_BoolFalse_StoredAsIntegerZero()
    {
        _repo.Upsert("https://api.example.com", "tok",
                     refreshVersions: false,
                     refreshMetadata: false);

        // 直接 SELECT 验证底层存的是 0 而不是 "false" 字符串
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT refresh_versions, refresh_metadata FROM nodelist_source_config WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Fact]
    public void Get_AfterSchemaInit_AlwaysReturnsValidConfig()
    {
        // 即使手动 delete 单例行,Get 应返 default Config(不应抛 / 不应返 null)
        using (var conn = _factory.Open())
        {
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM nodelist_source_config";
            delCmd.ExecuteNonQuery();
        }

        var cfg = _repo.Get();
        Assert.NotNull(cfg);
        Assert.Equal("custom", cfg.Source);
        Assert.Equal("", cfg.ServerUrl);
    }
}
