using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:StarredModelsRepository CRUD round-trip tests ——
/// model_stars 表(Star 收藏,PK source_path)。Add / Remove / IsStarred / GetAll。
/// GetAll 走 starred_at DESC 排序;INSERT OR IGNORE 保证二次 Star 不更新时间戳。
/// </summary>
public class StarredModelsRepositoryTests
{
    [Fact]
    public void Add_NewPath_InsertsRow()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        repo.Add(@"D:\models\flux1.safetensors");
        Assert.True(repo.IsStarred(@"D:\models\flux1.safetensors"));
    }

    [Fact]
    public void Add_DuplicatePath_NoOp()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        repo.Add(@"D:\models\flux1.safetensors");
        repo.Add(@"D:\models\flux1.safetensors");
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public void Remove_ExistingPath_DeletesRow()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        repo.Add(@"D:\models\flux1.safetensors");
        repo.Remove(@"D:\models\flux1.safetensors");
        Assert.False(repo.IsStarred(@"D:\models\flux1.safetensors"));
    }

    [Fact]
    public void Remove_NonExistingPath_NoOp()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        repo.Remove(@"D:\models\nonexistent.safetensors");  // 不抛
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void IsStarred_NotAdded_ReturnsFalse()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        Assert.False(repo.IsStarred(@"D:\models\any.safetensors"));
    }

    [Fact]
    public void GetAll_ReturnsDescendingByStarredAt()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        repo.Add(@"D:\a.safetensors");
        System.Threading.Thread.Sleep(1100);  // datetime('now') 秒级精度 — 需 ≥1s 保证时间戳不同
        repo.Add(@"D:\b.safetensors");
        System.Threading.Thread.Sleep(1100);
        repo.Add(@"D:\c.safetensors");

        var all = repo.GetAll();
        Assert.Equal(new[] { @"D:\c.safetensors", @"D:\b.safetensors", @"D:\a.safetensors" }, all);
    }

    [Fact]
    public void GetAll_EmptyTable_ReturnsEmptyList()
    {
        using var db = new TestDb();
        var repo = new StarredModelsRepository(db.ModelFactory);
        Assert.Empty(repo.GetAll());
    }
}