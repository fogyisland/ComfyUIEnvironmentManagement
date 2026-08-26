using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x: LocalModelFilesRepository CRUD round-trip + 增量 diff 工具测试。
/// 镜像 LocalModelOverridesRepositoryTests / CivitaiCardCacheRepositoryTests 模式。
/// </summary>
public class LocalModelFilesRepositoryTests
{
    private static DownloadedModel MakeFile(string path, string sourceId = "local:loras/x", string title = "x")
        => new()
        {
            FullPath = path,
            SourceId = sourceId,
            SourceVersionId = "v1",
            SubfolderName = "loras/x",
            Title = title,
            Kind = ModelKind.LORA,
            Source = "Local",
            Hash = "abc123",
            MatchedDetail = null,
            MatchSource = null,
            PreviewImagePath = null,
            DownloadedAt = DateTime.UtcNow,
        };

    [Fact]
    public void LoadAll_EmptyDb_ReturnsEmpty()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_ThenLoadAll_ReturnsFile()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        var m = MakeFile(@"D:\models\x.safetensors");
        repo.Upsert(m, "2025-01-01T00:00:00.0000000Z");

        var list = repo.LoadAll();
        Assert.Single(list);
        Assert.Equal(@"D:\models\x.safetensors", list[0].FullPath);
        Assert.Equal("local:loras/x", list[0].SourceId);
        Assert.Equal("x", list[0].Title);
        Assert.Equal("abc123", list[0].Hash);
        Assert.Equal(ModelKind.LORA, list[0].Kind);
    }

    [Fact]
    public void Upsert_SamePath_Overwrites()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        var m = MakeFile(@"D:\models\x.safetensors");
        repo.Upsert(m, "2025-01-01T00:00:00.0000000Z");

        var m2 = MakeFile(@"D:\models\x.safetensors", title: "renamed", sourceId: "local:loras/x2");
        repo.Upsert(m2, "2025-01-02T00:00:00.0000000Z");

        var list = repo.LoadAll();
        Assert.Single(list);
        Assert.Equal("renamed", list[0].Title);
        Assert.Equal("local:loras/x2", list[0].SourceId);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t1");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t2");
        repo.Delete(@"D:\a.safetensors");
        Assert.Single(repo.LoadAll());
    }

    [Fact]
    public void LoadAllPaths_ReturnsAllPaths()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t1");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t2");
        var paths = repo.LoadAllPaths();
        Assert.Equal(2, paths.Count);
        Assert.Contains(@"D:\a.safetensors", paths);
        Assert.Contains(@"D:\b.safetensors", paths);
    }

    [Fact]
    public void LoadAllMtimes_ReturnsAllMtimes()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t1");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t2");
        var mt = repo.LoadAllMtimes();
        Assert.Equal(2, mt.Count);
        Assert.Equal("t1", mt[@"D:\a.safetensors"]);
        Assert.Equal("t2", mt[@"D:\b.safetensors"]);
    }

    [Fact]
    public void DeleteNotInPaths_DeletesMissingRows()
    {
        // 增量 diff 路径:DB 有 [a, b, c],scan 现在只看到 [a, c] → 应删 b
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t");
        repo.Upsert(MakeFile(@"D:\c.safetensors"), "t");

        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\a.safetensors", @"D:\c.safetensors"
        };
        var n = repo.DeleteNotInPaths(current);
        Assert.Equal(1, n);

        var remaining = repo.LoadAllPaths();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(@"D:\b.safetensors", remaining);
    }

    [Fact]
    public void DeleteNotInPaths_EmptyCurrent_DeletesAll()
    {
        // 用户清空目录 / 切到空目录 → 删所有 cache(否则下次 LoadFromDb 还能读到 stale rows)
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t");

        var n = repo.DeleteNotInPaths(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, n);
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_EmptyFullPath_NoOp()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(""), "t");
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_EmptyMtime_NoOp()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\x.safetensors"), "");
        Assert.Empty(repo.LoadAll());
    }

    [Fact]
    public void Upsert_Null_Throws()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        Assert.Throws<ArgumentNullException>(() => repo.Upsert(null!, "t"));
    }

    [Fact]
    public void Upsert_PreservesMatchedDetailJsonRoundTrip()
    {
        // matched_detail_json 序列化 — GroupToCards 重新 hydrate 时 MatchedDetail 非 null
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        var detail = new CivitAiDetailDto(42, "M", "u", "SDXL", "d",
            new[] { "t1" },
            new[] { new CivitAiVersionDto("v1", "SDXL", null) },
            new[] { "https://x.test/1.png" });
        var m = MakeFile(@"D:\x.safetensors");
        // DownloadedModel is a class — 用 init-only 属性在 object initializer 设 MatchedDetail/MatchSource
        var mWithMatch = new DownloadedModel
        {
            FullPath = m.FullPath, SourceId = m.SourceId, SourceVersionId = m.SourceVersionId,
            SubfolderName = m.SubfolderName, Title = m.Title, Kind = m.Kind, Source = m.Source,
            Hash = m.Hash, MatchedDetail = detail, MatchSource = MatchSource.Hash,
            PreviewImagePath = m.PreviewImagePath, DownloadedAt = m.DownloadedAt,
        };
        repo.Upsert(mWithMatch, "t");

        var loaded = repo.LoadAll();
        Assert.Single(loaded);
        Assert.NotNull(loaded[0].MatchedDetail);
        Assert.Equal(42, loaded[0].MatchedDetail!.Id);
        Assert.Equal(MatchSource.Hash, loaded[0].MatchSource);
    }

    [Fact]
    public void Clear_DeletesAllRows()
    {
        using var db = new TestDb();
        var repo = new LocalModelFilesRepository(db.Factory);
        repo.Upsert(MakeFile(@"D:\a.safetensors"), "t");
        repo.Upsert(MakeFile(@"D:\b.safetensors"), "t");
        var n = repo.Clear();
        Assert.Equal(2, n);
        Assert.Empty(repo.LoadAll());
    }
}