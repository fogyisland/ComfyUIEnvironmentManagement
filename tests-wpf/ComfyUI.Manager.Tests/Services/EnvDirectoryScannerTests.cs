using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class EnvDirectoryScannerTests : IDisposable
{
    private readonly string _workRoot =
        Path.Combine(Path.GetTempPath(), "cmgr-scanner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvDirectoryScannerTests()
    {
        Directory.CreateDirectory(_workRoot);
        _dbPath = Path.Combine(_workRoot, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        // 强制初始化 schema
        using (var conn = _factory.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
        }
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }

    private string EnvsDir => Path.Combine(_workRoot, "Envs");

    private static void WriteMarker(string envDir, string envId, string name, string kind)
    {
        Directory.CreateDirectory(envDir);
        var marker = new EnvMarker
        {
            EnvId = envId,
            Name = name,
            Kind = kind,
            TemplateSnapshot = new TemplateConfig { Kind = kind, LocalSourceDir = kind },
            CreatedAt = "2026-08-26T00:00:00Z",
        };
        EnvMarkerService.Write(envDir, marker);
    }

    [Fact]
    public async Task ScanAsync_EmptyEnvsDir_ReturnsZeroReport()
    {
        Directory.CreateDirectory(EnvsDir);
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(EnvsDir);

        Assert.Equal(0, report.Inserted);
        Assert.Equal(0, report.Updated);
        Assert.Equal(0, report.Skipped);
        Assert.Empty(_repo.ListAll());
    }

    [Fact]
    public async Task ScanAsync_EnvsDirDoesNotExist_ReturnsZeroReport()
    {
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(Path.Combine(_workRoot, "NonExistent"));

        Assert.Equal(0, report.Inserted);
    }

    [Fact]
    public async Task ScanAsync_EmptyEnvsDirArg_ReturnsZeroReport()
    {
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync("");

        Assert.Equal(0, report.Inserted);
    }

    [Fact]
    public async Task ScanAsync_DirectoryWithMarker_InsertsEnv()
    {
        Directory.CreateDirectory(EnvsDir);
        WriteMarker(Path.Combine(EnvsDir, "Env-A"), "env-aaa11111", "Env-A", "ComfyUI");
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(EnvsDir);

        Assert.Equal(1, report.Inserted);
        Assert.Equal(0, report.Updated);
        Assert.Equal(0, report.Skipped);
        var list = _repo.ListAll();
        Assert.Single(list);
        var env = list[0];
        Assert.Equal("env-aaa11111", env.Id);
        Assert.Equal("Env-A", env.Name);
        Assert.Equal("ComfyUI", env.TemplateKind);
        Assert.Equal(Path.Combine(EnvsDir, "Env-A"), env.RootPath);
        Assert.Equal("stopped", env.Status);
        Assert.Null(env.Port);
    }

    [Fact]
    public async Task ScanAsync_DirectoryWithoutMarker_Skipped()
    {
        Directory.CreateDirectory(EnvsDir);
        Directory.CreateDirectory(Path.Combine(EnvsDir, "RandomDir")); // 没 marker
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(EnvsDir);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Inserted);
        Assert.Empty(_repo.ListAll());
    }

    [Fact]
    public async Task ScanAsync_MixedMarkerAndNoMarker_OnlyMarkerImported()
    {
        Directory.CreateDirectory(EnvsDir);
        WriteMarker(Path.Combine(EnvsDir, "Env-A"), "env-aaa11111", "Env-A", "ComfyUI");
        WriteMarker(Path.Combine(EnvsDir, "Env-B"), "env-bbb22222", "Env-B", "OpenVoice");
        Directory.CreateDirectory(Path.Combine(EnvsDir, "RandomDir1"));
        Directory.CreateDirectory(Path.Combine(EnvsDir, "RandomDir2"));
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(EnvsDir);

        Assert.Equal(2, report.Inserted);
        Assert.Equal(2, report.Skipped);
        var list = _repo.ListAll();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, e => e.Id == "env-aaa11111");
        Assert.Contains(list, e => e.Id == "env-bbb22222");
    }

    [Fact]
    public async Task ScanAsync_EnvAlreadyInSqlite_UpdatesRootPath()
    {
        Directory.CreateDirectory(EnvsDir);
        // SQLite 已有 env,路径在旧位置
        _repo.Upsert(new Environment
        {
            Id = "env-aaa11111",
            Name = "Env-A-old",
            RootPath = Path.Combine(_workRoot, "OldEnvs", "Env-A"),
            Status = "stopped",
            TemplateKind = "ComfyUI",
        });
        // 磁盘上新位置有 marker,env_id 跟 SQLite 一致
        WriteMarker(Path.Combine(EnvsDir, "Env-A"), "env-aaa11111", "Env-A-new", "ComfyUI");
        var scanner = new EnvDirectoryScanner(_repo);

        var report = await scanner.ScanAsync(EnvsDir);

        Assert.Equal(0, report.Inserted);
        Assert.Equal(1, report.Updated);
        var env = _repo.Get("env-aaa11111");
        Assert.NotNull(env);
        // RootPath 跟 Name 都被 marker 覆盖
        Assert.Equal(Path.Combine(EnvsDir, "Env-A"), env!.RootPath);
        Assert.Equal("Env-A-new", env.Name);
    }

    [Fact]
    public async Task ScanAsync_TemplateSnapshotUpdated_FromMarker()
    {
        Directory.CreateDirectory(EnvsDir);
        _repo.Upsert(new Environment
        {
            Id = "env-aaa11111",
            Name = "Env-A",
            RootPath = Path.Combine(_workRoot, "OldEnvs", "Env-A"),
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                LocalSourceDir = "old-source",
                EntryScript = "main.py",
            },
        });
        // marker 携带新的 snapshot
        WriteMarker(Path.Combine(EnvsDir, "Env-A"), "env-aaa11111", "Env-A", "ComfyUI");
        var scanner = new EnvDirectoryScanner(_repo);

        await scanner.ScanAsync(EnvsDir);

        var env = _repo.Get("env-aaa11111");
        Assert.NotNull(env?.TemplateConfigSnapshot);
        // marker 写的是 "ComfyUI",覆盖老 snapshot 里的 "old-source"
        Assert.Equal("ComfyUI", env!.TemplateConfigSnapshot!.LocalSourceDir);
    }

    [Fact]
    public async Task ScanAsync_DiscoveredEnv_HasSensibleDefaults()
    {
        // 新发现的 env(从其他机器搬来的,SQLite 没记录)— 验证默认字段
        Directory.CreateDirectory(EnvsDir);
        WriteMarker(Path.Combine(EnvsDir, "Env-A"), "env-aaa11111", "Env-A", "ComfyUI");
        var scanner = new EnvDirectoryScanner(_repo);

        await scanner.ScanAsync(EnvsDir);

        var env = _repo.Get("env-aaa11111");
        Assert.NotNull(env);
        Assert.Equal("isolated", env!.ComfyuiLayout);
        Assert.Equal(Path.Combine(EnvsDir, "Env-A", "venv"), env.VenvPath);
        Assert.Equal(Path.Combine(EnvsDir, "Env-A", "custom_nodes"), env.CustomNodesPath);
        Assert.Equal("[]", env.EnabledNodeIdsJson);
    }
}