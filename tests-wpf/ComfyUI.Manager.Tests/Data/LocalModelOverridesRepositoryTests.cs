using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x: LocalModelOverridesRepository CRUD round-trip tests。
/// </summary>
public class LocalModelOverridesRepositoryTests
{
    [Fact]
    public void LoadAll_EmptyDb_ReturnsEmptyDictionary()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_ThenLoadAll_ReturnsEntry()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("civitai:42@12345", @"D:\custom\my-model.safetensors");
        var dict = repo.LoadAll();
        Assert.Single(dict);
        Assert.Equal(@"D:\custom\my-model.safetensors", dict["civitai:42@12345"]);
    }

    [Fact]
    public void Upsert_ExistingKey_Overwrites()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("k", @"D:\first");
        repo.Upsert("k", @"D:\second");
        Assert.Equal(@"D:\second", repo.LoadAll()["k"]);
    }

    [Fact]
    public void Upsert_NullPath_DeletesRow()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("k", @"D:\first");
        repo.Upsert("k", null);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_EmptyPath_DeletesRow()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("k", @"D:\first");
        repo.Upsert("k", "");
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("a", @"D:\a");
        repo.Upsert("b", @"D:\b");
        repo.Delete("a");
        Assert.Single(repo.LoadAll());
        Assert.True(repo.LoadAll().ContainsKey("b"));
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
            cmd.CommandText = @"INSERT INTO local_model_overrides (source_id, override_path, updated_at)
                                VALUES ('', 'D:\orphan', '2025-01-01')";
            cmd.ExecuteNonQuery();
        }
        var repo = new LocalModelOverridesRepository(factory);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_EmptySourceId_NoOp()
    {
        using var db = new TestDb();
        var repo = new LocalModelOverridesRepository(db.Factory);
        repo.Upsert("", @"D:\foo");
        Assert.Empty(repo.LoadAll());
    }
}