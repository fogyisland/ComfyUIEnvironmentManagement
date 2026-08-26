using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x: CivitaiCardCacheRepository CRUD round-trip + JSON resilience。
/// 镜像 LocalModelOverridesRepositoryTests 模式 + 用 SQLite TestDb 在 memory DB 跑,
/// 测 LoadAll / Upsert / Delete + null/空 sourceId 守卫 + JSON 损坏行跳过。
/// </summary>
public class CivitaiCardCacheRepositoryTests
{
    private static CivitAiDetailDto MakeFakeDetail(int id = 42) => new(
        Id: id,
        Title: "Test Model",
        Username: "tester",
        BaseModel: "SDXL",
        Description: "fake description",
        Tags: new[] { "tag1", "tag2" },
        Versions: new[]
        {
            new CivitAiVersionDto("v1", "SDXL", null),
        },
        ImageUrls: new[] { "https://example.test/1.png" });

    [Fact]
    public void LoadAll_EmptyDb_ReturnsEmptyDictionary()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_ThenLoadAll_ReturnsEntry()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        var detail = MakeFakeDetail(42);
        repo.Upsert("civitai:42@12345", detail);
        var dict = repo.LoadAll();
        Assert.Single(dict);
        Assert.Equal(42, dict["civitai:42@12345"].Id);
        Assert.Equal("Test Model", dict["civitai:42@12345"].Title);
        Assert.Equal("tester", dict["civitai:42@12345"].Username);
    }

    [Fact]
    public void Upsert_ExistingKey_OverwritesDetail()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        repo.Upsert("k", MakeFakeDetail(1));
        repo.Upsert("k", MakeFakeDetail(2));
        var dict = repo.LoadAll();
        Assert.Single(dict);
        Assert.Equal(2, dict["k"].Id);
    }

    [Fact]
    public void Upsert_PreservesCollectionFieldsRoundTrip()
    {
        // 反序列化对 record positional + IReadOnlyList<string> 必须保留 Tags / Versions / ImageUrls 内容。
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        var detail = MakeFakeDetail(7);
        repo.Upsert("k", detail);
        var loaded = repo.LoadAll()["k"];
        Assert.Equal(2, loaded.Tags.Count);
        Assert.Contains("tag1", loaded.Tags);
        Assert.Equal("v1", loaded.Versions[0].Name);
        Assert.Equal("SDXL", loaded.Versions[0].BaseModel);
        Assert.Single(loaded.ImageUrls);
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        repo.Upsert("a", MakeFakeDetail(1));
        repo.Upsert("b", MakeFakeDetail(2));
        repo.Delete("a");
        var dict = repo.LoadAll();
        Assert.Single(dict);
        Assert.True(dict.ContainsKey("b"));
        Assert.False(dict.ContainsKey("a"));
    }

    [Fact]
    public void Delete_NonExistentKey_NoError()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        repo.Delete("never-existed");   // 不抛
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_EmptySourceId_NoOp()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        repo.Upsert("", MakeFakeDetail());
        repo.Upsert(null!, MakeFakeDetail());  // nullable 守卫
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_NullDetail_Throws()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        // ArgumentNullException 让 VM 走错误日志路径 — 不是悄悄吞(避免重复 bug 沉默)。
        Assert.Throws<System.ArgumentNullException>(() => repo.Upsert("k", null!));
    }

    [Fact]
    public void LoadAll_CorruptJsonRow_SkippedAndOtherRowsLoad()
    {
        // 模拟 DB 行损坏(运维写脏、迁移失败):一行 JSON 烂 + 一行正常 → LoadAll 跳过烂行,正常行仍在。
        using var db = new TestDb();
        var factory = db.Factory;
        // 先写一条正常行
        new CivitaiCardCacheRepository(factory).Upsert("good", MakeFakeDetail(99));
        // 再用 raw SQL 写一条损坏 JSON
        using (var conn = factory.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO civitai_card_cache (source_id, detail_json, fetched_at)
                                VALUES ('bad', '{not valid json', '2025-01-01')";
            cmd.ExecuteNonQuery();
        }
        var dict = new CivitaiCardCacheRepository(factory).LoadAll();
        Assert.Single(dict);
        Assert.True(dict.ContainsKey("good"));
        Assert.False(dict.ContainsKey("bad"));
    }

    [Fact]
    public void LoadAll_EmptySourceId_Skipped()
    {
        // 防御性:DB 某行 source_id 为空 → 不返回(避免后续 GroupBy 误匹配)。
        using var db = new TestDb();
        var factory = db.Factory;
        using (var conn = factory.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO civitai_card_cache (source_id, detail_json, fetched_at)
                                VALUES ('', '{}', '2025-01-01')";
            cmd.ExecuteNonQuery();
        }
        var dict = new CivitaiCardCacheRepository(factory).LoadAll();
        Assert.Empty(dict);
    }

    [Fact]
    public void Upsert_DifferentKeys_AllPersisted()
    {
        using var db = new TestDb();
        var repo = new CivitaiCardCacheRepository(db.Factory);
        repo.Upsert("a", MakeFakeDetail(1));
        repo.Upsert("b", MakeFakeDetail(2));
        repo.Upsert("c", MakeFakeDetail(3));
        var dict = repo.LoadAll();
        Assert.Equal(3, dict.Count);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { dict["a"].Id, dict["b"].Id, dict["c"].Id }.OrderBy(x => x));
    }
}