using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowFilesystemScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkflowFilesystemScanner _scanner = new(logger: null);

    public WorkflowFilesystemScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFScanner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_NonExistentDir_ReturnsEmpty()
    {
        var result = _scanner.Scan(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_EmptyDir_ReturnsEmpty()
    {
        var result = _scanner.Scan(_tempDir);
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_DirWithValidMeta_ReturnsDownloadedWorkflow()
    {
        var sub = Path.Combine(_tempDir, "portrait-gen-abc12345");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "workflow.json"), "{}");
        File.WriteAllText(Path.Combine(sub, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Portrait Generator v2",
            source = "community_json",
            source_id = "abc12345",
            downloaded_at = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc),
        }));

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("portrait-gen-abc12345", result[0].SubfolderName);
        Assert.Equal("Portrait Generator v2", result[0].Title);
        Assert.Equal("community_json", result[0].Source);
        Assert.Equal("abc12345", result[0].SourceId);
    }

    [Fact]
    public void Scan_SkipsSubfolderWithoutMeta()
    {
        var withMeta = Path.Combine(_tempDir, "valid-12345678");
        Directory.CreateDirectory(withMeta);
        File.WriteAllText(Path.Combine(withMeta, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Valid", downloaded_at = DateTime.UtcNow,
        }));
        var withoutMeta = Path.Combine(_tempDir, "incomplete-abcdef");
        Directory.CreateDirectory(withoutMeta);
        File.WriteAllText(Path.Combine(withoutMeta, "workflow.json"), "{}");

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("valid-12345678", result[0].SubfolderName);
    }

    [Fact]
    public void Scan_MalformedMeta_SkipsAndReturnsOthers()
    {
        var good = Path.Combine(_tempDir, "good-11111111");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "meta.json"), JsonSerializer.Serialize(new
        {
            title = "Good", downloaded_at = DateTime.UtcNow,
        }));
        var bad = Path.Combine(_tempDir, "bad-22222222");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "meta.json"), "{ not valid json");

        var result = _scanner.Scan(_tempDir);

        Assert.Single(result);
        Assert.Equal("good-11111111", result[0].SubfolderName);
    }
}