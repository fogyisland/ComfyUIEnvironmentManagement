using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelFilesystemScannerTests : IDisposable
{
    private readonly string _tmp;

    public ModelFilesystemScannerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ComfyUIMgrModels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void Scan_EmptyDir_ReturnsEmpty()
    {
        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_DirDoesNotExist_ReturnsEmpty()
    {
        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(Path.Combine(_tmp, "missing"));
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_OneVersionFolder_ReturnsOneEntry()
    {
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var verDir = Path.Combine(kindDir, "realistic-vision-12345678");
        var versionDir = Path.Combine(verDir, "v50-fp16-87654321");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Realistic Vision v5.0",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "12345",
                SourceVersionId = "87654321",
                SourceUrl = "https://civitai.com/models/12345",
                PrimaryFilename = "model.safetensors",
                SizeBytes = 6789012345,
                NsfwLevel = 0,
                DownloadedAt = DateTime.UtcNow,
            }));

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal("v50-fp16-87654321", result[0].SubfolderName);
        Assert.Equal(ModelKind.Checkpoint, result[0].Kind);
        Assert.Equal("12345", result[0].SourceId);
        Assert.Equal("87654321", result[0].SourceVersionId);
    }

    [Fact]
    public void Scan_MultipleKindsAndVersions_ReturnsAll()
    {
        // checkpoints / realistic-vision-12345678 / v50-fp16-87654321 / meta.json
        CreateVersion("checkpoints", "realistic-vision-12345678", "v50-fp16-87654321", "Realistic Vision");
        CreateVersion("checkpoints", "realistic-vision-12345678", "v51-fp32-11223344", "Realistic Vision");
        CreateVersion("loras", "detail-totaling-23456789", "v1-99887766", "Detail Totaling");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.SubfolderName == "v50-fp16-87654321");
        Assert.Contains(result, r => r.SubfolderName == "v51-fp32-11223344");
        Assert.Contains(result, r => r.SubfolderName == "v1-99887766");
    }

    [Fact]
    public void Scan_VersionFolderMissingMetaJson_SkippedWithWarn()
    {
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var verDir = Path.Combine(kindDir, "realistic-vision-12345678");
        var versionDir = Path.Combine(verDir, "v50-fp16-87654321");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        // No meta.json → skip

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Empty(result);
    }

    [Fact]
    public void Scan_MetaJsonWithEnumString_RoundTripsKindCorrectly()
    {
        // Mirror T5 ModelDownloader: writes Kind as string via JsonStringEnumConverter.
        // Scanner must use the same converter to read it back.
        var kindDir = Path.Combine(_tmp, "loras");
        var modelDir = Path.Combine(kindDir, "detail-totaling-23456789");
        var versionDir = Path.Combine(modelDir, "v1-99887766");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Detail Totaling",
                Kind = ModelKind.LORA,
                Source = "civitai",
                SourceId = "23456789",
                SourceVersionId = "99887766",
                SourceUrl = "https://civitai.com/models/23456789",
                PrimaryFilename = "model.safetensors",
                SizeBytes = 12345,
                NsfwLevel = 0,
                DownloadedAt = DateTime.UtcNow,
            }, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(ModelKind.LORA, result[0].Kind);
    }

    private void CreateVersion(string kind, string modelSlugId, string versionSlugId, string title)
    {
        var versionDir = Path.Combine(_tmp, kind, modelSlugId, versionSlugId);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = title,
                Kind = kind switch
                {
                    "checkpoints" => ModelKind.Checkpoint,
                    "loras" => ModelKind.LORA,
                    "vae" => ModelKind.VAE,
                    _ => ModelKind.Other,
                },
                Source = "civitai",
                SourceId = modelSlugId.Split('-').Last(),
                SourceVersionId = versionSlugId.Split('-').Last(),
                DownloadedAt = DateTime.UtcNow,
            }));
    }
}
