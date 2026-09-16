using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-15) T43h+user:raw JSON 全量入库 roundtrip 测试。
/// v1.0.0.x (2026-09-16) T43i.2.2-fix:删 5 个 ≤0.017% 覆盖率列,测试同步瘦身。
/// 验证 NodelistRepository.UpsertEntry raw JSON 列写入 + GetAllEntries 读取一致,
/// 数组/对象走 JsonSerializer.Serialize 字符串存储。
/// </summary>
public class NodelistRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly NodelistRepository _repo;

    public NodelistRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nodelist-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "state.db"));
        _repo = new NodelistRepository(_factory);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void UpsertEntry_FullUpsert_RoundtripsAll18NewColumns()
    {
        _repo.UpsertEntry(
            author: "ltdrdata",
            repoName: "ComfyUI-Manager",
            source: "json",
            id: "comfyui-manager",
            reference: "https://github.com/ltdrdata/ComfyUI-Manager",
            reference2: "https://github.com/ltdrdata/ComfyUI-Manager-Utils",
            description: "ComfyUI Manager for installing custom nodes",  // T43i.1+user
            filesJson: "[\"https://example.com/a.zip\"]",
            installType: "git-clone",
            pipJson: "[\"torch>=2.0\",\"numpy\"]",
            preemptionsJson: "[\"SAMLoader\",\"InspirePack\"]",
            nodenamePattern: "Inspire$",
            category: "Core",
            tagsJson: "[\"management\",\"core\"]",
            jsPath: "js/manager.js");

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("comfyui-manager", e.Id);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager", e.Reference);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager-Utils", e.Reference2);
        Assert.Equal("ComfyUI Manager for installing custom nodes", e.Description);  // T43i.1+user
        Assert.Equal("[\"https://example.com/a.zip\"]", e.FilesJson);
        Assert.Equal("git-clone", e.InstallType);
        Assert.Equal("[\"torch>=2.0\",\"numpy\"]", e.PipJson);
        Assert.Equal("[\"SAMLoader\",\"InspirePack\"]", e.PreemptionsJson);
        Assert.Equal("Inspire$", e.NodenamePattern);
        Assert.Equal("Core", e.Category);
        Assert.Equal("[\"management\",\"core\"]", e.TagsJson);
        Assert.Equal("js/manager.js", e.JsPath);
    }

    [Fact]
    public void UpsertEntry_AllNulls_WritesNulls()
    {
        // 老 entry 只填 author + repo,其它字段全 null — EnsureColumn backfill 时状态。
        _repo.UpsertEntry(
            author: "foo", repoName: "bar", source: "json",
            id: null, reference: null, reference2: null, description: null, filesJson: null,
            installType: null, pipJson: null, preemptionsJson: null,
            nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        var e = entries[0];
        Assert.Null(e.Id);
        Assert.Null(e.PipJson);
        Assert.Null(e.TagsJson);
    }

    [Fact]
    public void UpsertEntry_OverwritesNewColumnsOnConflict()
    {
        // 第 1 次入库:id="v1"
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v1", installType: "git-clone", pipJson: "[\"torch\"]",
            reference: null, reference2: null, description: null, filesJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        // 第 2 次入库(同 PK):id="v2",pip 变 — ON CONFLICT DO UPDATE 应覆盖
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v2", installType: "unzip", pipJson: "[\"numpy\"]",
            reference: null, reference2: null, description: null, filesJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        Assert.Equal("v2", entries[0].Id);
        Assert.Equal("unzip", entries[0].InstallType);
        Assert.Equal("[\"numpy\"]", entries[0].PipJson);
    }

    // v1.0.0.x (2026-09-16) T43i.4+user「无变动不写入数据库」+ user「增加一个 release
    // 就更改 没有任何变动则不写入数据库」:新增 4 个单测覆盖 entry 的增量 upsert 行为 —
    // 内容完全相同时整个 UPDATE 跳过(包括 last_ingested_at 也不动,跟用户原话
    // 「无变动不写入」严格对齐)。Detail 端 raw_json 哈希对比行为类似,在
    // NodelistDetailIncrementalTests 单独覆盖。

    [Fact]
    public void UpsertEntry_NoChange_DoesNotUpdateLastIngestedAt()
    {
        // 第 1 次入库 — 11 个 raw JSON 列都给值
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v1", reference: "https://github.com/a/b",
            reference2: null, description: "test",
            filesJson: "[\"x.zip\"]", installType: "git-clone",
            pipJson: "[\"torch\"]", preemptionsJson: null,
            nodenamePattern: null, category: null,
            tagsJson: "[\"core\"]", jsPath: null);

        var firstRead = ReadEntryTimestamps(_factory, "a", "b");
        Assert.NotNull(firstRead.firstSeen);
        Assert.NotNull(firstRead.lastIngested);

        // 睡 50ms 让时钟前进
        System.Threading.Thread.Sleep(50);

        // 第 2 次入库 — 内容完全相同
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v1", reference: "https://github.com/a/b",
            reference2: null, description: "test",
            filesJson: "[\"x.zip\"]", installType: "git-clone",
            pipJson: "[\"torch\"]", preemptionsJson: null,
            nodenamePattern: null, category: null,
            tagsJson: "[\"core\"]", jsPath: null);

        var secondRead = ReadEntryTimestamps(_factory, "a", "b");
        // last_ingested_at 必须保持原值(无变动不写入)
        Assert.Equal(firstRead.lastIngested, secondRead.lastIngested);
        // first_seen_at 当然不变
        Assert.Equal(firstRead.firstSeen, secondRead.firstSeen);
    }

    [Fact]
    public void UpsertEntry_AnyColumnChange_UpdatesLastIngestedAt()
    {
        // 第 1 次入库
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v1", reference: "https://github.com/a/b",
            reference2: null, description: "test",
            filesJson: null, installType: "git-clone",
            pipJson: null, preemptionsJson: null,
            nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var firstRead = ReadEntryTimestamps(_factory, "a", "b");

        System.Threading.Thread.Sleep(50);

        // 第 2 次入库 — 只改 1 列(任何列)
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v1", reference: "https://github.com/a/b",
            reference2: null, description: "test (改了一下)",
            filesJson: null, installType: "git-clone",
            pipJson: null, preemptionsJson: null,
            nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var secondRead = ReadEntryTimestamps(_factory, "a", "b");
        // last_ingested_at 必须更新
        Assert.NotEqual(firstRead.lastIngested, secondRead.lastIngested);
        // first_seen_at 不变(永远是首次入库时间)
        Assert.Equal(firstRead.firstSeen, secondRead.firstSeen);
        // 内容真的更新了
        Assert.Equal("test (改了一下)", _repo.GetAllEntries()[0].Description);
    }

    [Fact]
    public void UpsertEntry_NullVsNull_DoesNotCountAsChange()
    {
        // 第 1 次入库 — 全 null
        _repo.UpsertEntry(
            "a", "b", "json",
            id: null, reference: null, reference2: null, description: null,
            filesJson: null, installType: null, pipJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var firstRead = ReadEntryTimestamps(_factory, "a", "b");

        System.Threading.Thread.Sleep(50);

        // 第 2 次入库 — 同样全 null
        _repo.UpsertEntry(
            "a", "b", "json",
            id: null, reference: null, reference2: null, description: null,
            filesJson: null, installType: null, pipJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var secondRead = ReadEntryTimestamps(_factory, "a", "b");
        // null vs null 应该判定为 same
        Assert.Equal(firstRead.lastIngested, secondRead.lastIngested);
    }

    [Fact]
    public void UpsertEntry_NewEntry_Inserts()
    {
        // 不存在 entry → INSERT(注意 last_ingested_at 应被填为 now)
        _repo.UpsertEntry(
            "new", "entry", "json",
            id: "new-entry", reference: null, reference2: null, description: null,
            filesJson: null, installType: null, pipJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        Assert.Equal("new", entries[0].Author);
        Assert.Equal("entry", entries[0].RepoName);
        Assert.Equal("new-entry", entries[0].Id);
        Assert.NotNull(ReadEntryTimestamps(_factory, "new", "entry").firstSeen);
    }

    private static (string? firstSeen, string? lastIngested) ReadEntryTimestamps(
        SqliteConnectionFactory factory, string author, string repoName)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT first_seen_at, last_ingested_at FROM nodelist_entries
            WHERE author = @a AND repo_name = @r";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        using var rdr = cmd.ExecuteReader();
        Assert.True(rdr.Read());
        return (
            rdr.IsDBNull(0) ? null : rdr.GetString(0),
            rdr.IsDBNull(1) ? null : rdr.GetString(1));
    }
}
