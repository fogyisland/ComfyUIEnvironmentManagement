using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSymlinkerTests : IDisposable
{
    // Source ids must be >= 8 chars so ToSlugId produces the expected slug-id8 directly
    // (shorter ids get padded to 8 with trailing zeros, shifting the link name).
    private const string ModelId = "12345678";
    private const string VersionId = "67890123";

    private readonly string _envRoot;
    private readonly string _modelsDir;
    private readonly Settings _settings;

    public ModelSymlinkerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(), "ComfyUIMgrSym_" + Guid.NewGuid().ToString("N"));
        _modelsDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrModels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_envRoot);
        Directory.CreateDirectory(_modelsDir);
        _settings = new Settings { ModelsDirectory = _modelsDir };
    }

    public void Dispose()
    {
        if (Directory.Exists(_envRoot)) Directory.Delete(_envRoot, recursive: true);
        if (Directory.Exists(_modelsDir)) Directory.Delete(_modelsDir, recursive: true);
    }

    /// <summary>Writes a valid DownloadedModel state (model-slug-id8/ version-slug-vid8 / meta.json).</summary>
    private string CreateDownloaded(string titleSlug, string versionSlug, string sourceId, string sourceVersionId)
    {
        var versionDir = Path.Combine(_modelsDir, "checkpoints", $"{titleSlug}-{sourceId}", $"{versionSlug}-{sourceVersionId}");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = titleSlug.Replace('-', ' '),
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = sourceId,
                SourceVersionId = sourceVersionId,
                PrimaryFilename = "model.safetensors",
                DownloadedAt = DateTime.UtcNow,
            }));
        return versionDir;
    }

    [Fact]
    public async Task SyncToEnvAsync_OneDownloadedModel_CreatesJunction()
    {
        // Setup: models/checkpoints/realistic-vision-12345678/v50-fp16-67890123/meta.json
        CreateDownloaded("realistic-vision", "v50-fp16", ModelId, VersionId);

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync(_envRoot, default);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Failed);
        var linkPath = Path.Combine(_envRoot, "models", "checkpoints", "realistic-vision-12345678__v50-fp16-67890123");
        Assert.True(Directory.Exists(linkPath));
    }

    [Fact]
    public async Task SyncToEnvAsync_EmptyEnvComfyuiSource_ReturnsEmpty()
    {
        CreateDownloaded("realistic-vision", "v50-fp16", ModelId, VersionId);

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync("", default);

        Assert.Equal(0, result.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_AlreadyCorrectJunction_Skipped()
    {
        // Setup: download + first sync
        CreateDownloaded("realistic-vision", "v50-fp16", ModelId, VersionId);

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());

        await symlinker.SyncToEnvAsync(_envRoot, default);  // 1st sync
        var result2 = await symlinker.SyncToEnvAsync(_envRoot, default);  // 2nd sync

        Assert.Equal(1, result2.Skipped);
        Assert.Equal(0, result2.Linked);
    }

    [Fact]
    public async Task SyncToEnvAsync_WrongExistingJunction_RecreatesLink()
    {
        // Setup: pre-create wrong junction
        var envKindDir = Path.Combine(_envRoot, "models", "checkpoints");
        Directory.CreateDirectory(envKindDir);
        var linkPath = Path.Combine(envKindDir, "realistic-vision-12345678__v50-fp16-67890123");
        var wrongTarget = Path.Combine(_modelsDir, "wrong", "wrong");
        Directory.CreateDirectory(wrongTarget);
        try { Directory.CreateSymbolicLink(linkPath, wrongTarget); }
        catch { /* some FS may not support symlinks in tests; skip */ return; }

        // Setup: real download
        CreateDownloaded("realistic-vision", "v50-fp16", ModelId, VersionId);

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync(_envRoot, default);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task SyncToEnvAsync_LinkCreationFails_RecordsErrorWithoutThrowing()
    {
        // Setup: download + force a path collision by pre-creating a regular file where the link should go
        CreateDownloaded("realistic-vision", "v50-fp16", ModelId, VersionId);

        var envModelsDir = Path.Combine(_envRoot, "models");
        Directory.CreateDirectory(envModelsDir);
        var kindDir = Path.Combine(envModelsDir, "checkpoints");
        Directory.CreateDirectory(kindDir);
        File.WriteAllText(Path.Combine(kindDir, "realistic-vision-12345678__v50-fp16-67890123"), "blocker");

        var scanner = new ModelFilesystemScanner();
        var symlinker = new ModelSymlinker(_settings, scanner, new JunctionLinker());
        var result = await symlinker.SyncToEnvAsync(_envRoot, default);

        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.Failed);
        Assert.NotEmpty(result.Errors);
    }
}
