using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-15) T43h+user:raw JSON 全量入库 roundtrip 测试。
/// 验证 NodelistRepository.UpsertEntry 18 个新列写入 + GetAllEntries 读取一致,
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
            aptDependency: "\"libgl1\"",
            dependenciesJson: "{\"foo\":\"bar\"}",
            preemptionsJson: "[\"SAMLoader\",\"InspirePack\"]",
            nodenamePattern: "Inspire$",
            nickname: "Manager",
            category: "Core",
            tagsJson: "[\"management\",\"core\"]",
            lastUpdate: "2024-01-15",
            badgesJson: "[\"verified\",\"featured\"]",
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
        Assert.Equal("\"libgl1\"", e.AptDependency);
        Assert.Equal("{\"foo\":\"bar\"}", e.DependenciesJson);
        Assert.Equal("[\"SAMLoader\",\"InspirePack\"]", e.PreemptionsJson);
        Assert.Equal("Inspire$", e.NodenamePattern);
        Assert.Equal("Manager", e.Nickname);
        Assert.Equal("Core", e.Category);
        Assert.Equal("[\"management\",\"core\"]", e.TagsJson);
        Assert.Equal("2024-01-15", e.LastUpdate);
        Assert.Equal("[\"verified\",\"featured\"]", e.BadgesJson);
        Assert.Equal("js/manager.js", e.JsPath);
    }

    [Fact]
    public void UpsertEntry_AllNulls_WritesNulls()
    {
        // 老 entry 只填 author + repo,其它字段全 null — EnsureColumn backfill 时状态。
        _repo.UpsertEntry(
            author: "foo", repoName: "bar", source: "json",
            id: null, reference: null, reference2: null, description: null, filesJson: null,
            installType: null, pipJson: null, aptDependency: null,
            dependenciesJson: null, preemptionsJson: null,
            nodenamePattern: null, nickname: null, category: null,
            tagsJson: null, lastUpdate: null,
            badgesJson: null, jsPath: null);

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
            aptDependency: null, dependenciesJson: null, preemptionsJson: null,
            nodenamePattern: null, nickname: null, category: null,
            tagsJson: null, lastUpdate: null,
            badgesJson: null, jsPath: null);

        // 第 2 次入库(同 PK):id="v2",pip 变 — ON CONFLICT DO UPDATE 应覆盖
        _repo.UpsertEntry(
            "a", "b", "json",
            id: "v2", installType: "unzip", pipJson: "[\"numpy\"]",
            reference: null, reference2: null, description: null, filesJson: null,
            aptDependency: null, dependenciesJson: null, preemptionsJson: null,
            nodenamePattern: null, nickname: null, category: null,
            tagsJson: null, lastUpdate: null,
            badgesJson: null, jsPath: null);

        var entries = _repo.GetAllEntries();
        Assert.Single(entries);
        Assert.Equal("v2", entries[0].Id);
        Assert.Equal("unzip", entries[0].InstallType);
        Assert.Equal("[\"numpy\"]", entries[0].PipJson);
    }
}
