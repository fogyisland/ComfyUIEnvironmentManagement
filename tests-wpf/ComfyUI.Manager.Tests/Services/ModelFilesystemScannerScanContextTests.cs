using System;
using System.IO;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ModelFilesystemScannerScanContextTests : IDisposable
{
    private readonly string _root;

    public ModelFilesystemScannerScanContextTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scan-ctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Scan_NoContext_DoesNotComputeHash()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        File.WriteAllBytes(Path.Combine(kindDir, "test.safetensors"), new byte[] { 1, 2, 3 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_root);   // single-arg overload
        Assert.Single(result);
        Assert.Null(result[0].Hash);
    }

    [Fact]
    public void Scan_WithContext_AndCachedHash_PopulatesHash()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        var modelPath = Path.Combine(kindDir, "test.safetensors");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        using var cache = new CivitaiHashCache(":memory:");
        // Pre-populate cache so hash compute is skipped
        var info = new FileInfo(modelPath);
        cache.Store(modelPath, info.Length, info.LastWriteTimeUtc.Ticks, "FAKE_HASH");

        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };   // no matcher → no API call
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.Equal("FAKE_HASH", result[0].Hash);
        Assert.Null(result[0].MatchedDetail);   // no matcher configured
    }

    [Fact]
    public void Scan_WithContext_NoCacheHit_ComputesAndStoresHash()
    {
        var kindDir = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(kindDir);
        var modelPath = Path.Combine(kindDir, "test.safetensors");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.NotNull(result[0].Hash);
        Assert.Equal(64, result[0].Hash!.Length);   // SHA256 hex is 64 chars

        // Verify cached for next call
        var info = new FileInfo(modelPath);
        var cached = cache.Lookup(modelPath, info.Length, info.LastWriteTimeUtc.Ticks);
        Assert.Equal(result[0].Hash, cached);
    }
}