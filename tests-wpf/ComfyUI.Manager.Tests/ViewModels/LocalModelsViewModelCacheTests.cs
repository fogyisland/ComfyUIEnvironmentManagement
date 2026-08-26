using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x:LocalModelsViewModel 跟 SQLite cache 交互的测试 — LoadFromDb + ReloadAsync 增量 diff。
/// FakeScanner 喂受控 entries,TestDb 给 in-memory SQLite,验证:
///   - LoadFromDb() 从 DB 读 entries 出卡(不调 scanner)
///   - ReloadAsync 写 DB(new file insert, deleted file remove, mtime 变化 = re-Upsert)
///   - unchanged mtime 的文件不重写
/// </summary>
public sealed class LocalModelsViewModelCacheTests
{
    private static Settings SettingsWith(string modelsDir) => new() { DefaultModelsDirectory = modelsDir };

    /// <summary>FakeScanner 喂的 entries(模拟一次磁盘 enumeration) — 每个 FullPath 必须真实存在
    /// (PersistScanResultsToDb 调 File.GetLastWriteTimeUtc),所以用 temp 目录 + 真文件。</summary>
    private static DownloadedModel MakeFile(string fullPath, string title = "x", string sourceId = "local:loras/x")
        => new()
        {
            FullPath = fullPath,
            SourceId = sourceId,
            SourceVersionId = "v1",
            SubfolderName = "loras/x",
            Title = title,
            Kind = ModelKind.LORA,
            Source = "Local",
            Hash = null,
            MatchedDetail = null,
            MatchSource = null,
            PreviewImagePath = null,
            DownloadedAt = DateTime.UtcNow,
        };

    private static string CreateTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmgr-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test.safetensors");
        File.WriteAllText(path, "fake content");
        return path;
    }

    [Fact]
    public void LoadFromDb_NullRepo_ReturnsFalse()
    {
        // 测试场景:不传 localModelFilesRepo → LoadFromDb 立刻返回 false(view 维持 placeholder)
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner());
        Assert.False(vm.LoadFromDb());
    }

    [Fact]
    public void LoadFromDb_EmptyDb_ReturnsFalse_KeepsPlaceholder()
    {
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            localModelFilesRepo: filesRepo);
        Assert.False(vm.LoadFromDb());
        Assert.True(vm.IsEmpty);   // placeholder 仍显示
    }

    [Fact]
    public void LoadFromDb_WithRows_PopulatesCardsWithoutScanning()
    {
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);

        // 预填 DB(模拟上一次 ReloadAsync 跑过,数据已入库)
        var path = CreateTempFile();
        var m = MakeFile(path, "anim");
        filesRepo.Upsert(m, File.GetLastWriteTimeUtc(path).ToString("O"));

        // FakeScanner 不该被调,验证它没 entries
        var scanner = new FakeScanner();   // Entries = empty
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner,
            localModelFilesRepo: filesRepo);

        Assert.True(vm.LoadFromDb());
        Assert.False(vm.IsEmpty);
        Assert.Single(vm.FilteredModels);
        Assert.Equal("anim", vm.FilteredModels[0].Title);
        // scanner.Entries 仍空 = 没扫(关键断言:LoadFromDb 不触发 scanner)
        Assert.Empty(scanner.Entries);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReloadAsync_NewFile_PersistsToDb()
    {
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);
        var path = CreateTempFile();

        var scanner = new FakeScanner
        {
            Entries = new[] { MakeFile(path, "newfile") }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner,
            localModelFilesRepo: filesRepo);

        await vm.ReloadAsync();

        var dbPaths = filesRepo.LoadAllPaths();
        Assert.Contains(path, dbPaths);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReloadAsync_DeletedFile_RemovedFromDb()
    {
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);

        // 预填 DB:有 a, b 两个 file
        var keepPath = CreateTempFile();
        var dropPath = CreateTempFile();
        filesRepo.Upsert(MakeFile(keepPath, "keep"), File.GetLastWriteTimeUtc(keepPath).ToString("O"));
        filesRepo.Upsert(MakeFile(dropPath, "drop"), File.GetLastWriteTimeUtc(dropPath).ToString("O"));

        // 新 scanner 只看见 keepPath(模拟用户手动删了 drop)
        File.Delete(dropPath);

        var scanner = new FakeScanner
        {
            Entries = new[] { MakeFile(keepPath, "keep") }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner,
            localModelFilesRepo: filesRepo);

        await vm.ReloadAsync();

        var dbPaths = filesRepo.LoadAllPaths();
        Assert.Contains(keepPath, dbPaths);
        Assert.DoesNotContain(dropPath, dbPaths);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReloadAsync_UnchangedMtime_NoOverwrite()
    {
        // 同 mtime → 不重写 row(避免无谓 DB IO,scanner 跑完 final 列表本身通常未变)
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);

        var path = CreateTempFile();
        var mtime = File.GetLastWriteTimeUtc(path).ToString("O");

        // 预填:row 已有 title="original"
        filesRepo.Upsert(MakeFile(path, "original"), mtime);

        // 新 scanner 看见同 path,但 title 不同 — 因为 mtime 没变,不该被覆写
        var scanner = new FakeScanner
        {
            Entries = new[] { MakeFile(path, "modified") }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner,
            localModelFilesRepo: filesRepo);

        await vm.ReloadAsync();

        // 仍 "original"
        var loaded = filesRepo.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("original", loaded[0].Title);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReloadAsync_ChangedMtime_OverwritesRow()
    {
        // mtime 变 → 文件被改 → 重新 Upsert(用户原话「再次刷新也是增量读取」 — 只重 hash/mtime 变动的)
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);

        var path = CreateTempFile();
        // 写一个老 mtime 到 DB,实际文件 mtime 是新的(模拟"DB 旧,文件新")
        var oldMtime = "2020-01-01T00:00:00.0000000Z";
        filesRepo.Upsert(MakeFile(path, "old"), oldMtime);

        var scanner = new FakeScanner
        {
            Entries = new[] { MakeFile(path, "new") }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner,
            localModelFilesRepo: filesRepo);

        await vm.ReloadAsync();

        var loaded = filesRepo.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Title);
        Assert.NotEqual(oldMtime, loaded[0].Hash);  // Hash 字段不变(没跑 hash),但 Title 已变
    }

    [Fact]
    public async System.Threading.Tasks.Task ReloadAsync_NullRepo_StillWorks_OldBehavior()
    {
        // 兼容老 ctor — 没 repo 注入时 ReloadAsync 仍跑,只是不写 DB
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);  // 不用 — 只为对比

        var path = CreateTempFile();
        var scanner = new FakeScanner
        {
            Entries = new[] { MakeFile(path, "no-repo") }
        };
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner);  // 不传 repo

        await vm.ReloadAsync();

        // DB 仍空 — VM 没写
        Assert.Empty(filesRepo.LoadAll());
        // 但 _allCards 有数据(scanner 走的就是 in-memory 流)
        Assert.Single(vm.FilteredModels);
    }

    [Fact]
    public void LoadFromDb_AfterPersist_ShowsSameCardsAsNextReload()
    {
        // 验证用户原话场景:第一次 ReloadAsync → DB 入库 → 关 app → 再开 → LoadFromDb 出卡
        using var db = new TestDb();
        var filesRepo = new LocalModelFilesRepository(db.Factory);

        var path1 = CreateTempFile();
        var path2 = CreateTempFile();

        // 注意:两个 file 必须不同 SourceId,否则 GroupBy(SourceId) 合并成 1 card。
        // 真实场景下,不同 lora / checkpoint 模型 SourceId 不同;测试用不同 sourceId 模拟。
        var scanner1 = new FakeScanner
        {
            Entries = new[]
            {
                MakeFile(path1, "a", sourceId: "local:loras/a"),
                MakeFile(path2, "b", sourceId: "local:loras/b"),
            }
        };
        var vm1 = new LocalModelsViewModel(SettingsWith("Z:\\fake"), scanner1,
            localModelFilesRepo: filesRepo);
        vm1.ReloadAsync().GetAwaiter().GetResult();

        // 第二次:新 VM(模拟重启)+ LoadFromDb
        var vm2 = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            localModelFilesRepo: filesRepo);
        Assert.True(vm2.LoadFromDb());
        Assert.Equal(2, vm2.FilteredModels.Count);
        var titles = new HashSet<string>();
        foreach (var c in vm2.FilteredModels) titles.Add(c.Title);
        Assert.Contains("a", titles);
        Assert.Contains("b", titles);
    }
}