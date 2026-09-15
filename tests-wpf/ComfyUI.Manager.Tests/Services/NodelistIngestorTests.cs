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
        // minimal JSON,只有 author + title,其它字段全缺
        var json = """
            { "custom_nodes": [ { "author": "foo", "title": "bar" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json);

        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("foo", e.Author);
        Assert.Equal("bar", e.RepoName);
        // 18 个新列全 null(缺失字段)
        Assert.Null(e.Id);
        Assert.Null(e.Reference);
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
        var json = """
            { "custom_nodes": [ { "author": "a", "title": "b", "apt_dependency": "libgl1" } ] }
            """;
        var jsonPath = Path.Combine(_tempDir, "custom-node-list.json");
        await File.WriteAllTextAsync(jsonPath, json);

        await _ingestor.IngestAsync(jsonPath, "github.com", null,
            forceFull: true, progress: null, ct: CancellationToken.None);

        var e = _repo.GetAllEntries()[0];
        // 字符串 apt_dependency 应被 JsonSerializer.Serialize 成 JSON string literal
        Assert.Equal("\"libgl1\"", e.AptDependency);
    }
}
