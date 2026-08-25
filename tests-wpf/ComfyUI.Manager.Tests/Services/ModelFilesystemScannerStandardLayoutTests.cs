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
    public void Scan_StandardLayout_OneCardPerFile()
    {
        // <checkpoints>/<modela>/<file>.safetensors, b, c → 3 records (T7: 每文件 = 1 卡)
        CreateModelFile("checkpoints", "modela", "modela.safetensors");
        CreateModelFile("checkpoints", "modelb", "modelb.ckpt");
        CreateModelFile("checkpoints", "modelc", "modelc.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.Equal("Local", r.Source);
            Assert.Equal(ModelKind.Checkpoint, r.Kind);
            Assert.Equal("checkpoints", r.SubfolderName);   // NEW: 统一 kindName
        });
        Assert.Contains(result, r => r.Title == "Modela");     // NEW: Title = PrettyPrint(filename-no-ext)
        Assert.Contains(result, r => r.Title == "Modelb");
        Assert.Contains(result, r => r.Title == "Modelc");
        Assert.All(result, r => Assert.StartsWith("local:checkpoints/", r.SourceId));  // NEW
    }

    [Fact]
    public void Scan_StandardLayout_MultipleFilesInOneModelDir_OneCardPerFile()
    {
        // T7: 1 个 model dir 含多个 model 文件 → 每个文件 = 1 card,Title = 各自文件名
        var modelDir = Path.Combine(_tmp, "Lora", "animefullfinalpruned");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "animefullfinalpruned.safetensors"), "fake1");
        File.WriteAllText(Path.Combine(modelDir, "animefullfinalpruned_v2.safetensors"), "fake2");
        File.WriteAllText(Path.Combine(modelDir, "extra_weights.pt"), "fake3");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.Equal("Local", r.Source);
            Assert.Equal(ModelKind.LORA, r.Kind);
            Assert.Equal("Lora", r.SubfolderName);   // T7: SubfolderName = kindName,不是 modelDirName
        });
        Assert.Contains(result, r => r.Title == "Animefullfinalpruned");
        Assert.Contains(result, r => r.Title == "Animefullfinalpruned V2");
        Assert.Contains(result, r => r.Title == "Extra Weights");
        Assert.Contains(result, r => r.SourceId == "local:lora/animefullfinalpruned");
        Assert.Contains(result, r => r.SourceId == "local:lora/animefullfinalpruned_v2");
        Assert.Contains(result, r => r.SourceId == "local:lora/extra_weights");
    }

    [Fact]
    public void Scan_MixedLayout_FlatAndThreeLevel_SameSourceIdDeduped()
    {
        // T7: 同文件 <Lora>/foo.safetensors (flat) + <Lora>/someModel/foo.safetensors (3-level, 不同 mtime)
        //   → Scan() 返回 2 records 都 SourceId="local:lora/foo"
        //   → GroupToCards 在 VM 端 dedup 成 1 card(取最新 DownloadedAt),Scan 层直接 2 条
        CreateFlatFile("Lora", "foo.safetensors");
        CreateModelFile("Lora", "someModel", "foo.safetensors");

        var flatStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var threeStamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(Path.Combine(_tmp, "Lora", "foo.safetensors"), flatStamp);
        File.SetLastWriteTime(Path.Combine(_tmp, "Lora", "someModel", "foo.safetensors"), threeStamp);

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        // Scan() 返回 2 条 SourceId 相同的 records(VM GroupToCards 端去重 → 1 card)
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("local:lora/foo", r.SourceId));
        Assert.All(result, r => Assert.Equal("Foo", r.Title));
        Assert.All(result, r => Assert.Equal("Local", r.Source));
        Assert.All(result, r => Assert.Equal(ModelKind.LORA, r.Kind));
        // mtime 各自保留:flat stamp 在直接子文件上,3-level stamp 在子目录文件上
        Assert.Equal(flatStamp.Ticks, result.Single(r => r.FullPath == Path.Combine(_tmp, "Lora", "foo.safetensors")).DownloadedAt.Ticks);
        Assert.Equal(threeStamp.Ticks, result.Single(r => r.FullPath == Path.Combine(_tmp, "Lora", "someModel", "foo.safetensors")).DownloadedAt.Ticks);
    }

    [Fact]
    public void Scan_StandardLayout_InfersKindCaseInsensitively()
    {
        // PascalCase / lowercase / camelCase 全部命中 KindAliases (T7: SubfolderName = kindName)
        CreateModelFile("CheckPoint", "m1", "m1.safetensors");
        CreateModelFile("Lora", "m2", "m2.safetensors");
        CreateModelFile("VAE", "m3", "m3.safetensors");
        CreateModelFile("controlnet", "m4", "m4.safetensors");
        CreateModelFile("loras", "m5", "m5.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(5, result.Count);
        Assert.Equal(ModelKind.Checkpoint, result.Single(r => r.SubfolderName == "CheckPoint").Kind);
        Assert.Equal(ModelKind.LORA,       result.Single(r => r.SubfolderName == "Lora").Kind);
        Assert.Equal(ModelKind.VAE,        result.Single(r => r.SubfolderName == "VAE").Kind);
        Assert.Equal(ModelKind.Controlnet, result.Single(r => r.SubfolderName == "controlnet").Kind);
        Assert.Equal(ModelKind.LORA,       result.Single(r => r.SubfolderName == "loras").Kind);
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
    public void Scan_StandardLayout_DownloadedAtIsFileMtime()
    {
        // T7: 每个文件 = 1 record,DownloadedAt = 该文件自己的 mtime(不是 dir latest)
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

        Assert.Equal(2, result.Count);
        // NTFS 不保证 round-trip Kind 一致,只断言 value ticks 相等
        Assert.Equal(oldStamp.Ticks, result.Single(r => r.Title == "Old").DownloadedAt.Ticks);
        Assert.Equal(newStamp.Ticks, result.Single(r => r.Title == "New").DownloadedAt.Ticks);
    }

    [Fact]
    public void Scan_StandardLayout_TitlePrettyPrinted()
    {
        // PrettyPrint = Replace('-', ' ').Replace('_', ' ').ToLowerInvariant().ToTitleCase()
        // ToTitleCase 只按 whitespace 分词,不会 split camelCase (per brief tolerance)
        // T7: Title 来自文件名(不是 dir 名)
        CreateModelFile("checkpoints", "animateLight_v1Final", "animateLight_v1Final.safetensors");
        CreateModelFile("checkpoints", "my-cool-model", "my-cool-model.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(2, result.Count);
        Assert.Equal("Animatelight V1final", result.Single(r => r.Title == "Animatelight V1final").Title);
        Assert.Equal("My Cool Model", result.Single(r => r.Title == "My Cool Model").Title);
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
        Assert.Equal("local:checkpoints/test", localEntry.SourceId);   // T7: SourceId 用 filename,不是 modelDirName
        Assert.NotEqual(metaEntry.SourceId, localEntry.SourceId);
        Assert.Equal(ModelKind.Checkpoint, localEntry.Kind);
        Assert.Equal("Test", localEntry.Title);   // T7: Title = PrettyPrint("test")
    }

    private void CreateModelFile(string kind, string modelName, string fileName)
    {
        var modelDir = Path.Combine(_tmp, kind, modelName);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, fileName), "fake");
    }

    // -------- T6 扁平布局 tests (<kind>/<file>.ext) --------

    [Fact]
    public void Scan_FlatLayout_ReturnsOneRecordPerFile()
    {
        // <Lora>/<file1>.safetensors, <file2>.safetensors, <file3>.safetensors → 3 records
        CreateFlatFile("Lora", "animatelcm_sd15.safetensors");
        CreateFlatFile("Lora", "anime_slider_v2.safetensors");
        CreateFlatFile("Lora", "detail_tweaker.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.Equal("Local", r.Source);
            Assert.Equal(ModelKind.LORA, r.Kind);
            Assert.Equal("Lora", r.SubfolderName);
        });
        Assert.Contains(result, r => r.SourceId == "local:lora/animatelcm_sd15");
        Assert.Contains(result, r => r.SourceId == "local:lora/anime_slider_v2");
        Assert.Contains(result, r => r.SourceId == "local:lora/detail_tweaker");
    }

    [Fact]
    public void Scan_FlatLayout_DownloadedAtIsFileMtime()
    {
        // 2 文件不同 mtime,各取自己的 mtime(不是 kindDir / latest)
        CreateFlatFile("VAE", "ae.safetensors");
        CreateFlatFile("VAE", "animevae.pt");
        var oldStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var newStamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(Path.Combine(_tmp, "VAE", "ae.safetensors"), oldStamp);
        File.SetLastWriteTime(Path.Combine(_tmp, "VAE", "animevae.pt"), newStamp);

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(2, result.Count);
        Assert.Equal(oldStamp.Ticks, result.Single(r => r.SubfolderName == "VAE" && r.Title == "Ae").DownloadedAt.Ticks);
        Assert.Equal(newStamp.Ticks, result.Single(r => r.SubfolderName == "VAE" && r.Title == "Animevae").DownloadedAt.Ticks);
    }

    [Fact]
    public void Scan_MixedLayout_ThreeLevelAndFlatInSameKind_NoDuplicates()
    {
        // <Lora>/<sub1>/x.safetensors (3-level) + <Lora>/y.safetensors (flat) → 各 1 record,T7: SourceId 用 filename
        CreateModelFile("Lora", "sub1", "x.safetensors");
        CreateFlatFile("Lora", "y.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Equal(2, result.Count);
        var threeLevel = result.Single(r => r.Title == "X");
        var flat = result.Single(r => r.Title == "Y");
        Assert.Equal("local:lora/x", threeLevel.SourceId);
        Assert.Equal("local:lora/y", flat.SourceId);
        Assert.NotEqual(threeLevel.SourceId, flat.SourceId);
        Assert.Equal(ModelKind.LORA, threeLevel.Kind);
        Assert.Equal(ModelKind.LORA, flat.Kind);
        Assert.Equal("Local", threeLevel.Source);
        Assert.Equal("Local", flat.Source);
    }

    [Fact]
    public void Scan_FlatLayout_UnknownKindBecomesOther()
    {
        // <unet>/<file>.bin (不在 KindAliases) → Kind=Other,Source=Local
        CreateFlatFile("unet", "bigModel.bin");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(ModelKind.Other, result[0].Kind);
        Assert.Equal("Local", result[0].Source);
        Assert.Equal("local:unet/bigmodel", result[0].SourceId);
        Assert.Equal("Bigmodel", result[0].Title);
    }

    private void CreateFlatFile(string kind, string fileName)
    {
        var kindDir = Path.Combine(_tmp, kind);
        Directory.CreateDirectory(kindDir);
        File.WriteAllText(Path.Combine(kindDir, fileName), "fake");
    }

    // -------- T10 Preview image sibling scan tests --------

    [Fact]
    public void Scan_StandardLayout_PreviewImage_SiblingPng_ReturnedPath()
    {
        // 1 dir × 1 .safetensors + 1 .png 同 basename → PreviewImagePath = png full path
        CreateModelFile("loras", "mylora", "mylora.safetensors");
        var modelDir = Path.Combine(_tmp, "loras", "mylora");
        File.WriteAllBytes(Path.Combine(modelDir, "mylora.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(Path.Combine(modelDir, "mylora.png"), result[0].PreviewImagePath);
    }

    [Fact]
    public void Scan_StandardLayout_PreviewImage_NoSibling_NullPath()
    {
        // 1 dir × 1 .safetensors 无 image → PreviewImagePath = null
        CreateModelFile("loras", "nolora", "nolora.safetensors");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Null(result[0].PreviewImagePath);
    }

    [Fact]
    public void Scan_StandardLayout_PreviewImage_MultipleSiblings_FirstByDictionaryOrder()
    {
        // 1 dir × model.safetensors + model.gif + model.jpg + model.png + model.webp
        // → PreviewImagePath = model.gif (字典序 first: gif < jpg < png < webp)
        var modelDir = Path.Combine(_tmp, "loras", "multi");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), "fake");
        File.WriteAllBytes(Path.Combine(modelDir, "model.png"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(modelDir, "model.jpg"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(modelDir, "model.webp"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(modelDir, "model.gif"), new byte[] { 0 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(Path.Combine(modelDir, "model.gif"), result[0].PreviewImagePath);
    }

    [Fact]
    public void Scan_StandardLayout_PreviewImage_DifferentExtension_Ignored()
    {
        // 1 dir × model.safetensors + model.txt + model.json → PreviewImagePath = null
        // (非 image ext 跳过,即使同 basename)
        var modelDir = Path.Combine(_tmp, "loras", "notext");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), "fake");
        File.WriteAllText(Path.Combine(modelDir, "model.txt"), "notes");
        File.WriteAllText(Path.Combine(modelDir, "model.json"), "{}");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Null(result[0].PreviewImagePath);
    }

    [Fact]
    public void Scan_FlatLayout_PreviewImage_AlsoScans()
    {
        // 扁平布局同样走 BuildFlatModel → preview scan 也工作
        var loraDir = Path.Combine(_tmp, "loras");
        Directory.CreateDirectory(loraDir);
        File.WriteAllText(Path.Combine(loraDir, "flat.safetensors"), "fake");
        File.WriteAllBytes(Path.Combine(loraDir, "flat.png"), new byte[] { 0x89 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(Path.Combine(loraDir, "flat.png"), result[0].PreviewImagePath);
    }

    // -------- v1.0.0 T12:Diffusers 文件夹模型 tests --------

    [Fact]
    public void Scan_DiffusersFolder_KindDirSubdir_WithModelIndexJson_ReturnsDiffusersModel()
    {
        // <root>/diffusers/sdxl-base/model_index.json + unet/ + text_encoder/ → 1 record
        var diffusersDir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        Directory.CreateDirectory(Path.Combine(diffusersDir, "unet"));
        Directory.CreateDirectory(Path.Combine(diffusersDir, "text_encoder"));
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        File.WriteAllText(Path.Combine(diffusersDir, "unet", "diffusion_pytorch_model.safetensors"), "fake-unet");
        File.WriteAllText(Path.Combine(diffusersDir, "text_encoder", "model.safetensors"), "fake-te");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        var r = result[0];
        Assert.Equal(ModelKind.Diffusers, r.Kind);
        Assert.Equal("sdxl-base", r.Title);
        Assert.Equal(diffusersDir, r.FullPath);    // FullPath = subdir 目录路径,不是文件路径
        Assert.Equal("Local", r.Source);
        Assert.Equal("local:diffusers/sdxl-base", r.SourceId);
        Assert.Equal("diffusers", r.SubfolderName);
        Assert.Equal("", r.SourceVersionId);
    }

    [Fact]
    public void Scan_DiffusersFolder_NoModelIndexJson_NotDetected()
    {
        // <root>/checkpoints/sdxl-base/unet/model.safetensors 无 model_index.json
        // → 走 T7 per-file 逻辑:unet/model.safetensors 不是 model 顶层,不扫,0 records
        // (T7 只看 modelDir 直接子文件,不递归进 subdir)
        var modelDir = Path.Combine(_tmp, "checkpoints", "sdxl-base");
        Directory.CreateDirectory(Path.Combine(modelDir, "unet"));
        File.WriteAllText(Path.Combine(modelDir, "unet", "model.safetensors"), "fake");
        // 注意 modelDir 直接子无 .safetensors/.ckpt 等 → 0 records

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Empty(result);
    }

    [Fact]
    public void Scan_DiffusersFolder_WithPreviewPng_PreviewImagePathSet()
    {
        // folder 里有 model_index.json + preview.png → PreviewImagePath = preview.png full path
        var diffusersDir = Path.Combine(_tmp, "diffusers", "sdxl-turbo");
        Directory.CreateDirectory(diffusersDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        File.WriteAllBytes(Path.Combine(diffusersDir, "preview.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(Path.Combine(diffusersDir, "preview.png"), result[0].PreviewImagePath);
    }

    [Fact]
    public void Scan_DiffusersFolder_DownloadedAtIsNewestFileMtime_Recursive()
    {
        // folder 里有 model_index.json + unet/diffusion_pytorch_model.safetensors(mtime=t1)
        //   + text_encoder/model.safetensors(mtime=t2>t1) → DownloadedAt = t2
        // 注意:必须 set model_index.json mtime = t1 否则 file write 当前时间比 t2 还晚,变 max
        var diffusersDir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        var teDir = Path.Combine(diffusersDir, "text_encoder");
        Directory.CreateDirectory(unetDir);
        Directory.CreateDirectory(teDir);
        var indexFile = Path.Combine(diffusersDir, "model_index.json");
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        var teFile = Path.Combine(teDir, "model.safetensors");
        File.WriteAllText(indexFile, "{}");
        File.WriteAllText(unetFile, "fake-unet");
        File.WriteAllText(teFile, "fake-te");
        var indexStamp = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Local);  // 最早
        var oldStamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var newStamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);     // 最晚
        File.SetLastWriteTime(indexFile, indexStamp);
        File.SetLastWriteTime(unetFile, oldStamp);
        File.SetLastWriteTime(teFile, newStamp);

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(newStamp.Ticks, result[0].DownloadedAt.Ticks);
    }

    [Fact]
    public void Scan_DiffusersFolder_KindDirIsCheckpoints_StillDetected()
    {
        // <root>/checkpoints/sdxl-base/model_index.json(kindDir name = "checkpoints",
        //   但 subdir 里有 model_index.json) → Kind 仍 = Diffusers
        // (T12 brief — Kind 强写,不依赖 kindDir name)
        var diffusersDir = Path.Combine(_tmp, "checkpoints", "sdxl-base");
        var unetDir = Path.Combine(diffusersDir, "unet");
        Directory.CreateDirectory(diffusersDir);
        Directory.CreateDirectory(unetDir);
        File.WriteAllText(Path.Combine(diffusersDir, "model_index.json"), "{}");
        File.WriteAllText(Path.Combine(unetDir, "diffusion_pytorch_model.safetensors"), "fake-unet");

        var scanner = new ModelFilesystemScanner();
        var result = scanner.Scan(_tmp);

        Assert.Single(result);
        Assert.Equal(ModelKind.Diffusers, result[0].Kind);   // 强写,不是 Checkpoint
        Assert.Equal("sdxl-base", result[0].Title);
        Assert.Equal("checkpoints", result[0].SubfolderName);   // SubfolderName 仍 = kindName
        Assert.Equal("local:checkpoints/sdxl-base", result[0].SourceId);
    }

    // -------- Diffusers hash chain (T-D1): FindCanonicalHashFile helper tests --------

    [Fact]
    public void FindCanonicalHashFile_PrefersUnetSafetensors()
    {
        // unet/diffusion_pytorch_model.safetensors + vae/diffusion_pytorch_model.safetensors → unet (priority 1 wins)
        var dir = Path.Combine(_tmp, "diffusers", "sdxl-base");
        var unetDir = Path.Combine(dir, "unet");
        var vaeDir = Path.Combine(dir, "vae");
        Directory.CreateDirectory(unetDir);
        Directory.CreateDirectory(vaeDir);
        var unetFile = Path.Combine(unetDir, "diffusion_pytorch_model.safetensors");
        var vaeFile = Path.Combine(vaeDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllText(unetFile, "unet");
        File.WriteAllText(vaeFile, "vae");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(unetFile, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToTransformerSafetensors()
    {
        // No unet. transformer/diffusion_pytorch_model.safetensors exists → transformer (priority 2)
        var dir = Path.Combine(_tmp, "diffusers", "flux-base");
        var transformerDir = Path.Combine(dir, "transformer");
        Directory.CreateDirectory(transformerDir);
        var transformerFile = Path.Combine(transformerDir, "diffusion_pytorch_model.safetensors");
        File.WriteAllText(transformerFile, "transformer");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(transformerFile, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToUnetBin()
    {
        // No safetensors. unet/diffusion_pytorch_model.bin exists → unet bin (priority 3)
        var dir = Path.Combine(_tmp, "diffusers", "sd15-legacy");
        var unetDir = Path.Combine(dir, "unet");
        Directory.CreateDirectory(unetDir);
        var unetBin = Path.Combine(unetDir, "diffusion_pytorch_model.bin");
        File.WriteAllText(unetBin, "unet-bin");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(unetBin, result);
    }

    [Fact]
    public void FindCanonicalHashFile_FallsBackToLargestSafetensors()
    {
        // No well-known paths. 3 .safetensors files of sizes 100/500/200 → largest (500) wins
        var dir = Path.Combine(_tmp, "diffusers", "custom-layout");
        var sub1 = Path.Combine(dir, "sub1");
        var sub2 = Path.Combine(dir, "sub2");
        var sub3 = Path.Combine(dir, "sub3");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);
        Directory.CreateDirectory(sub3);
        var small1 = Path.Combine(sub1, "a.safetensors");
        var large = Path.Combine(sub2, "b.safetensors");
        var small2 = Path.Combine(sub3, "c.safetensors");
        File.WriteAllBytes(small1, new byte[100]);
        File.WriteAllBytes(large, new byte[500]);
        File.WriteAllBytes(small2, new byte[200]);

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Equal(large, result);
    }

    [Fact]
    public void FindCanonicalHashFile_NoMatchableFiles_ReturnsNull()
    {
        // Only config files (model_index.json + json sidecars), no model files → null
        var dir = Path.Combine(_tmp, "diffusers", "config-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model_index.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "tokenizer_config.json"), "{}");

        var result = ModelFilesystemScanner.FindCanonicalHashFile(dir);

        Assert.Null(result);
    }
}