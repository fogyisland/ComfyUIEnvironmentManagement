using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>v1.0.0 T5:覆盖 ModelFilesystemScanner 对标准 ComfyUI 二层布局
/// <c>&lt;kind&gt;/&lt;model&gt;/&lt;file&gt;.ext</c> 的识别能力,以及与现有 meta.json
/// 三层布局并存行为。meta.json 路径的回归保护由 <see cref="ModelFilesystemScannerTests"/> 负责,
/// 本文件不重复覆盖。</summary>
public class ModelFilesystemScannerStandardLayoutTests : IDisposable
{
    private readonly string _tmp;

    public ModelFilesystemScannerStandardLayoutTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ComfyUIMgrModelsStd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void Scan_StandardLayout_ReturnsOneRecordPerModel()
    {
        // <checkpoints>/<modelA>/<file>.safetensors, B, C → 3 records
        CreateModelFile("checkpoints", "modelA", "modelA.safetensors");
        CreateModelFile("checkpoints", "modelB", "modelB.ckpt");
        CreateModelFile("checkpoints", "modelC", "modelC.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.Equal("Local", r.Source);
            Assert.Equal(ModelKind.Checkpoint, r.Kind);
        });
        Assert.Contains(result, r => r.SubfolderName == "modelA");
        Assert.Contains(result, r => r.SubfolderName == "modelB");
        Assert.Contains(result, r => r.SubfolderName == "modelC");
    }

    [Fact]
    public void Scan_StandardLayout_InfersKindCaseInsensitively()
    {
        // PascalCase / lowercase / camelCase 全部命中 KindAliases
        CreateModelFile("CheckPoint", "m1", "m1.safetensors");
        CreateModelFile("Lora", "m2", "m2.safetensors");
        CreateModelFile("VAE", "m3", "m3.safetensors");
        CreateModelFile("controlnet", "m4", "m4.safetensors");
        CreateModelFile("loras", "m5", "m5.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(5, result.Count);
        Assert.Equal(ModelKind.Checkpoint, result.Single(r => r.SubfolderName == "m1").Kind);
        Assert.Equal(ModelKind.LORA,       result.Single(r => r.SubfolderName == "m2").Kind);
        Assert.Equal(ModelKind.VAE,        result.Single(r => r.SubfolderName == "m3").Kind);
        Assert.Equal(ModelKind.Controlnet, result.Single(r => r.SubfolderName == "m4").Kind);
        Assert.Equal(ModelKind.LORA,       result.Single(r => r.SubfolderName == "m5").Kind);
    }

    [Fact]
    public void Scan_StandardLayout_UnknownKindBecomesOther()
    {
        // unet / clip_vision / sam 不在 KindAliases → ModelKind.Other
        CreateModelFile("unet", "unet1", "unet1.safetensors");
        CreateModelFile("clip_vision", "cv1", "cv1.safetensors");
        CreateModelFile("sam", "sam1", "sam1.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal(ModelKind.Other, r.Kind));
        Assert.All(result, r => Assert.Equal("Local", r.Source));
    }

    [Fact]
    public void Scan_StandardLayout_DownloadedAtIsNewestFileMtime()
    {
        var modelDir = Path.Combine(_tmp, "checkpoints", "multi");
        Directory.CreateDirectory(modelDir);
        var oldFile = Path.Combine(modelDir, "old.safetensors");
        var newFile = Path.Combine(modelDir, "new.safetensors");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(newFile, "new");
        // 用 local DateTime 让 File.GetLastWriteTime 返回的 DateTime Kind 保持 Local 一致
        var oldStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var newStamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(oldFile, oldStamp);
        File.SetLastWriteTime(newFile, newStamp);

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        // NTFS 不保证 round-trip Kind 一致,只断言 value ticks 相等
        Assert.Equal(newStamp.Ticks, result[0].DownloadedAt.Ticks);
    }

    [Fact]
    public void Scan_StandardLayout_TitlePrettyPrinted()
    {
        // PrettyPrint = Replace('-', ' ').Replace('_', ' ').ToLowerInvariant().ToTitleCase()
        // ToTitleCase 只按 whitespace 分词,不会 split camelCase (per brief tolerance)
        CreateModelFile("checkpoints", "animateLight_v1Final", "animateLight_v1Final.safetensors");
        CreateModelFile("checkpoints", "my-cool-model", "my-cool-model.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(2, result.Count);
        Assert.Equal("Animatelight V1final", result.Single(r => r.SubfolderName == "animateLight_v1Final").Title);
        Assert.Equal("My Cool Model", result.Single(r => r.SubfolderName == "my-cool-model").Title);
    }

    [Fact]
    public void Scan_MetaJsonAndStandardCoexist_BothReturned()
    {
        // 同 <kind>/<model>/ 下既放 v1/meta.json (marketplace 下载) 又直接放 test.safetensors (手工放)
        var modelDir = Path.Combine(_tmp, "checkpoints", "mixedModel");
        var versionDir = Path.Combine(modelDir, "v1");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(new ModelMetaSidecar
            {
                Title = "Mixed Marketplace",
                Kind = ModelKind.Checkpoint,
                Source = "civitai",
                SourceId = "11111",
                SourceVersionId = "22222",
                DownloadedAt = DateTime.UtcNow,
            }));
        File.WriteAllText(Path.Combine(modelDir, "test.safetensors"), "manual");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(2, result.Count);
        var metaEntry = result.Single(r => r.Source == "civitai");
        var localEntry = result.Single(r => r.Source == "Local");
        Assert.Equal("11111", metaEntry.SourceId);
        Assert.Equal("local:checkpoints/mixedmodel", localEntry.SourceId);
        Assert.NotEqual(metaEntry.SourceId, localEntry.SourceId);
        Assert.Equal(ModelKind.Checkpoint, localEntry.Kind);
    }

    private void CreateModelFile(string kind, string modelName, string fileName)
    {
        var modelDir = Path.Combine(_tmp, kind, modelName);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, fileName), "fake");
    }
}