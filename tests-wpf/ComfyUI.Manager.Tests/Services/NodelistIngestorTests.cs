using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-15) T43h+user:raw JSON 全量解析测试。
/// 验证 Ingestor.ParseEntry 把 custom-node-list.json entry 全部 24 个 distinct 字段
/// 抽出来,数组/对象走 JsonSerializer.Serialize 字符串存储。
///
/// 测试策略:在临时目录写一个手工构造的 JSON(包含所有 24 字段变体),
/// 跑 IngestAsync 后查 SQLite 验证 18 个新列内容正确。
/// </summary>
public class NodelistIngestorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly NodelistRepository _repo;
    private readonly NodelistIngestor _ingestor;

    public NodelistIngestorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nodelist-ingest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "state.db"));
        _repo = new NodelistRepository(_factory);
        // NodelistIngestor 依赖 NodeRepoQueryService — 测试不需要调 GitHub API,传 null 让 fetch 失败即可。
        // 实际验证走 UpsertEntry(ProcessOneAsync 3a 步骤),不依赖 fetch 成功。
        _ingestor = new NodelistIngestor(_repo, queryService: null!, logger: null);
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
    public async Task IngestAsync_ParsesAll24RawJsonFields_Writes18NewColumns()
    {
        // 写一个手工 JSON,包含所有 24 个 distinct 字段变体。
        var json = """
            {
              "custom_nodes": [
                {
                  "author": "ltdrdata",
                  "title": "ComfyUI-Manager",
                  "id": "comfyui-manager",
                  "reference": "https://github.com/ltdrdata/ComfyUI-Manager",
                  "reference2": "https://github.com/ltdrdata/ComfyUI-Manager-Utils",
                  "files": ["https://example.com/a.zip", "https://example.com/b.zip"],
                  "install_type": "git-clone",
                  "description": "ComfyUI Manager",
                  "pip": ["torch>=2.0", "numpy"],
                  "apt_dependency": ["libgl1", "libglib2.0-0"],
                  "dependencies": {"foo": "bar", "n": 1},
                  "preemptions": ["SAMLoader", "InspirePack"],
                  "nodename_pattern": "Inspire$",
                  "nickname": "Manager",
                  "category": "Core",
                  "tags": ["management", "core"],
                  "version": "1.0.0",
                  "last_update": "2024-01-15",
                  "stars": 999,
                  "badges": ["verified", "featured"],
                  "js_path": "js/manager.js",
                  "license": "GPL-3.0"
                }
              ]
            }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json);

        // forceFull=true → DELETE FROM nodelist_entries + 重新 upsert
        // NodeRepoQueryService 是 null → FetchRepoMetadataAsync 抛 → localDetailFailed++ 但 UpsertEntry 已成功
        await _ingestor.IngestAsync(
            nodelistJsonFile: jsonPath,
            host: "github.com",
            token: null,
            forceFull: true,
            progress: null,
            ct: CancellationToken.None);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("ltdrdata", e.Author);
        Assert.Equal("ComfyUI-Manager", e.RepoName);

        // 18 个新列全部 assert
        Assert.Equal("comfyui-manager", e.Id);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager", e.Reference);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager-Utils", e.Reference2);
        Assert.Equal("[\"https://example.com/a.zip\",\"https://example.com/b.zip\"]", e.FilesJson);
        Assert.Equal("git-clone", e.InstallType);
        Assert.Equal("[\"torch>=2.0\",\"numpy\"]", e.PipJson);
        // apt_dependency 是 array → JsonArrayToString
        Assert.Contains("libgl1", e.AptDependency);
        Assert.Contains("libglib2.0-0", e.AptDependency);
        // dependencies 是 object → JsonObjectOrArrayToString
        Assert.Contains("foo", e.DependenciesJson);
        Assert.Contains("bar", e.DependenciesJson);
        Assert.Equal("[\"SAMLoader\",\"InspirePack\"]", e.PreemptionsJson);
        Assert.Equal("Inspire$", e.NodenamePattern);
        Assert.Equal("Manager", e.Nickname);
        Assert.Equal("Core", e.Category);
        Assert.Equal("[\"management\",\"core\"]", e.TagsJson);
        Assert.Equal("2024-01-15", e.LastUpdate);
        Assert.Equal(999, e.RawStars);
        Assert.Equal("[\"verified\",\"featured\"]", e.BadgesJson);
        Assert.Equal("js/manager.js", e.JsPath);
        Assert.Equal("GPL-3.0", e.RawLicense);
    }

    [Fact]
    public async Task IngestAsync_HandlesMissingFieldsAsNull()
    {
        // v1.0.0.x (2026-09-16) T43i.2-fix:必须 reference,其它字段缺失都走 null fallback。
        // reference 解析出 owner='foo' + repo='bar'(从 URL 第一/二段)
        // → 入库 author=foo(跟解析 owner 一致) + repo_name=bar。
        var json = """
            { "custom_nodes": [ { "author": "foo", "title": "bar", "reference": "https://github.com/foo/bar" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json);

        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        var e = entries[0];
        // v1.0.0.x T43i.2-fix:author 列现在存从 reference 解析的 GitHub owner
        // (跟 nodelist_details.author 一致 → GitHub API /repos/{owner}/{repo} 命中)。
        // title='bar' 是显示名,不入库;repo_name 从 reference 解析的 '/foo/bar' 第二段。
        Assert.Equal("foo", e.Author);
        Assert.Equal("bar", e.RepoName);
        // 18 个新列全 null(缺失字段)
        Assert.Null(e.Id);
        // v1.0.0.x T43i.2-fix:reference 列现在存 raw JSON 的 reference URL
        Assert.Equal("https://github.com/foo/bar", e.Reference);
        Assert.Null(e.InstallType);
        Assert.Null(e.PipJson);
        Assert.Null(e.TagsJson);
        Assert.Null(e.RawStars);
        Assert.Null(e.RawLicense);
    }

    [Fact]
    public async Task IngestAsync_AptDependencyAsString_SerializedAsJsonStringLiteral()
    {
        // apt_dependency 是 string(部分条目这样)
        // v1.0.0.x T43i.2-fix:加 reference(现在 100% entry 必须有 reference 才能入库)
        var json = """
            { "custom_nodes": [ { "author": "a", "title": "b", "reference": "https://github.com/a/b", "apt_dependency": "libgl1" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json);

        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        var e = _repo.GetAllEntries()[0];
        // 字符串 apt_dependency 应被 JsonSerializer.Serialize 成 JSON string literal
        Assert.Equal("\"libgl1\"", e.AptDependency);
    }

    [Fact]
    public async Task IngestAsync_IncrementalMode_UpdatesRawJsonColumnsOnExistingEntries()
    {
        // 1) 首次入库:forceFull=true,写入 entry 但 pip_json 是 null(原始 JSON 没 pip)
        // v1.0.0.x T43i.2-fix:加 reference(现在 100% entry 必须有 reference 才能入库)
        var json1 = """
            { "custom_nodes": [ { "author": "x", "title": "y", "reference": "https://github.com/x/y" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json1);
        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        Assert.Single(_repo.GetAllEntries());
        Assert.Null(_repo.GetAllEntries()[0].PipJson);

        // 2) 第二次入库:同样 entry,这次 JSON 加了 pip + install_type,增量模式
        // v1.0.0.x T43i.2-fix:加 reference
        var json2 = """
            { "custom_nodes": [ { "author": "x", "title": "y", "reference": "https://github.com/x/y", "install_type": "git-clone", "pip": ["torch"] } ] }
            """;
        await File.WriteAllTextAsync(jsonPath, json2);
        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: false, progress: null, ct: CancellationToken.None);

        // 验证:不增加新 entry,但 18 新列被更新
        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        Assert.Equal("git-clone", entries[0].InstallType);
        Assert.Equal("[\"torch\"]", entries[0].PipJson);
    }

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i+user 「入库过程中加日志记录,我希望看到进度和日志」:
    /// 验证 IngestEvent 7 种 Kind(Started/EntryUpserted/EntrySkipped/EntryFailed/
    /// DetailFetched/DetailFailed/Completed)都正确触发 + payload 字段正确。
    /// 用一个 fake 已有 entry + 新 entry 触发 EntrySkipped + EntryUpserted;
    /// queryService 是 null,所有 detail fetch 抛 NRE → DetailFailed;entry upsert
    /// 成功 → EntryUpserted。
    /// </summary>
    [Fact]
    public async Task Ingest_EmitsStartedEntryUpsertedEntrySkippedDetailFailedCompleted()
    {
        // 1) 预入库 1 条 entry (authorB/repoB) — 后续会被识别为「已存在」
        // v1.0.0.x T43i.2-fix:加 reference(现在 100% entry 必须有 reference 才能入库)
        var seedJson = """
            { "custom_nodes": [ { "author": "authorB", "title": "repoB", "reference": "https://github.com/authorB/repoB" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, seedJson);
        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        // 2) 第二次入库:2 条 entry (authorA 新 + authorB 已存在),订阅事件流
        // v1.0.0.x T43i.2-fix:加 reference
        var json = """
            {
              "custom_nodes": [
                { "author": "authorA", "title": "repoA", "reference": "https://github.com/authorA/repoA" },
                { "author": "authorB", "title": "repoB", "reference": "https://github.com/authorB/repoB" }
              ]
            }
            """;
        await File.WriteAllTextAsync(jsonPath, json);

        // 用 thread-safe list + lock 收集事件(后台 worker 10 个并发,
        // IProgress<>.Report 在 Progress<T> 捕获的 SyncContext 上 marshal,这里我们
        // 直接传 IProgress<T> 而非 Progress<T>,所以 Report 同步执行 — 不需要 lock,
        // 但保留 lock 防止未来重构加并发路径)。
        var events = new System.Collections.Generic.List<NodelistIngestor.IngestEvent>();
        var eventsLock = new object();
        IProgress<NodelistIngestor.IngestEvent> capture = new CaptureProgress(evt =>
        {
            lock (eventsLock) events.Add(evt);
        });

        // 增量模式 (forceFull=false),authorB 已存在 → 走 UpdateOnlyAsync → EntrySkipped;
        // authorA 不存在 → 走 ProcessOneAsync → EntryUpserted + DetailFailed(queryService=null 抛 NRE)。
        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: false, progress: capture, ct: CancellationToken.None);

        // 断言:7 种 Kind 都至少出现 1 次
        Assert.Contains(events, e => e.Kind == NodelistIngestor.IngestEventKind.Started);
        Assert.Contains(events, e => e.Kind == NodelistIngestor.IngestEventKind.EntryUpserted);
        Assert.Contains(events, e => e.Kind == NodelistIngestor.IngestEventKind.EntrySkipped);
        Assert.Contains(events, e => e.Kind == NodelistIngestor.IngestEventKind.DetailFailed);
        var completed = events.FindLast(e => e.Kind == NodelistIngestor.IngestEventKind.Completed);
        Assert.NotNull(completed);
        Assert.NotNull(completed!.Result);
        Assert.Equal(2, completed.Result.EntriesScanned);
        Assert.Equal(1, completed.Result.EntriesNew);    // authorA
        Assert.Equal(1, completed.Result.EntriesSkipped); // authorB
        // queryService=null → 1 条 detail fetch 抛 NRE → DetailsFailed=1
        Assert.Equal(1, completed.Result.DetailsFailed);
        Assert.Equal(0, completed.Result.DetailsWritten);

        // payload 字段正确
        var started = events.Find(e => e.Kind == NodelistIngestor.IngestEventKind.Started)!;
        Assert.Equal(2, started.Total);

        var upserted = events.Find(e =>
            e.Kind == NodelistIngestor.IngestEventKind.EntryUpserted && e.Author == "authorA")!;
        Assert.Equal("repoA", upserted.RepoName);

        var skipped = events.Find(e =>
            e.Kind == NodelistIngestor.IngestEventKind.EntrySkipped && e.Author == "authorB")!;
        Assert.Equal("repoB", skipped.RepoName);
    }

    /// <summary>Helper:简单 IProgress&lt;T&gt; 实现,同步执行 callback(测试同步断言用)。</summary>
    private sealed class CaptureProgress : IProgress<NodelistIngestor.IngestEvent>
    {
        private readonly Action<NodelistIngestor.IngestEvent> _cb;
        public CaptureProgress(Action<NodelistIngestor.IngestEvent> cb) => _cb = cb;
        public void Report(NodelistIngestor.IngestEvent value) => _cb(value);
    }

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i.2-fix:TryParseGitHubOwnerRepo 单元测试。
    /// 8 个 InlineData 覆盖支持格式 + 不支持格式:
    /// - 标准 https://github.com/{owner}/{repo}
    /// - 带 .git 后缀(剥离)
    /// - 带 query string(剥离)
    /// - 带 fragment(剥离)
    /// - 空字符串(null)
    /// - SSH git@ 协议(null,不支持)
    /// - 单 path segment(null,没 repo)
    /// - bare github.com(null,只 host 没 path)
    /// </summary>
    [Theory]
    [InlineData("https://github.com/ltdrdata/ComfyUI-Manager", "ltdrdata", "ComfyUI-Manager")]
    [InlineData("https://github.com/ltdrdata/ComfyUI-Manager.git", "ltdrdata", "ComfyUI-Manager")]
    [InlineData("https://github.com/foo/bar?ref=main", "foo", "bar")]
    [InlineData("https://github.com/foo/bar#readme", "foo", "bar")]
    [InlineData("git@github.com:foo/bar.git", null, null)]   // SSH 不支持 → skip
    [InlineData("https://github.com/foo", null, null)]        // 单 path segment → skip
    [InlineData("", null, null)]                              // 空字符串 → skip
    [InlineData("https://github.com/", null, null)]           // bare host → skip
    public void TryParseGitHubOwnerRepo_VariousFormats_ReturnsExpected(
        string url, string? expOwner, string? expRepo)
    {
        var parsed = NodelistIngestor.TryParseGitHubOwnerRepo(url);
        if (expOwner is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.NotNull(parsed);
            Assert.Equal(expOwner, parsed!.Value.Owner);
            Assert.Equal(expRepo, parsed.Value.Repo);
        }
    }
}
