using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
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

    // -------- Diffusers folder hash chain (T-D2): directory branch tests --------

    [Fact]
    public void Scan_DiffusersFolder_WithContext_ComputesHashFromUnetFile()
    {
        // Diffusers folder with unet/diffusion_pytorch_model.safetensors → hash matches what
        // ModelHasher.ComputeSha256 produces from the unet file (not the folder, not model_index.json).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        Directory.CreateDirectory(unetDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.Equal(ModelKind.Diffusers, result[0].Kind);
        Assert.Equal(diffusersDir, result[0].FullPath);
        var expectedHash = ModelHasher.ComputeSha256(unetFile);
        Assert.Equal(expectedHash, result[0].Hash);
    }

    [Fact]
    public void Scan_DiffusersFolder_WithContext_CacheKeyUsesFolderPathAndTotalSize()
    {
        // After scan, cache has an entry keyed by (folderPath, totalFolderSize, newestMtimeUtcTicks).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        var teDir = Path.Combine(diffusersDir, "text_encoder");
        Directory.CreateDirectory(unetDir);
        Directory.CreateDirectory(teDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        var teFile = Path.Combine(teDir, "model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 100, 200, 250 });
        File.WriteAllBytes(teFile, new byte[] { 100, 200 });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = null };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        // Compute expected (folderPath, totalSize, newestMtimeUtcTicks)
        var totalSize = new FileInfo(unetFile).Length + new FileInfo(teFile).Length
            + new FileInfo(Path.Combine(diffusersDir, "model_index.json")).Length;
        var newestMtime = new[] { unetFile, teFile, Path.Combine(diffusersDir, "model_index.json") }
            .Select(f => new FileInfo(f).LastWriteTimeUtc.Ticks).Max();
        var cached = cache.Lookup(diffusersDir, totalSize, newestMtime);
        Assert.NotNull(cached);
        Assert.Equal(result[0].Hash, cached);
    }

    [Fact]
    public void Scan_DiffusersFolder_WithContext_RunsOrchestratorWithHashedModel()
    {
        // Mock IModelMatcher → orchestrator → ctx.Matcher. Verify matcher received the Diffusers
        // model with Hash populated (so chain strategies see it as a real hash hit).
        var diffusersDir = Path.Combine(_root, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        Directory.CreateDirectory(unetDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllBytes(unetFile, new byte[] { 1, 2, 3 });

        DownloadedModel? capturedModel = null;
        var matchMock = new Mock<IModelMatcher>();
        matchMock.SetupGet(m => m.Name).Returns("Hash");
        matchMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                 .Callback<DownloadedModel, CancellationToken>((dm, _) => capturedModel = dm)
                 .ReturnsAsync((MatchResult?)null);

        var orchestrator = new CivitaiMatcherOrchestrator(new IModelMatcher[] { matchMock.Object });

        using var cache = new CivitaiHashCache(":memory:");
        var scanner = new ModelFilesystemScanner();
        var ctx = new ScanContext { HashCache = cache, Matcher = orchestrator };
        var result = scanner.Scan(_root, ctx);

        Assert.Single(result);
        Assert.NotNull(capturedModel);
        Assert.Equal(ModelKind.Diffusers, capturedModel!.Kind);
        Assert.Equal(diffusersDir, capturedModel.FullPath);
        Assert.NotNull(capturedModel.Hash);
        Assert.Equal(64, capturedModel.Hash!.Length);   // SHA256 hex
    }
}