using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20:扫描 ModelsDirectory 找到已下载的 model versions。
/// v1.0.0 T5:同遍既识别 meta.json 三层布局 <see cref="SourceKind"/> meta.json paths,
/// 也识别标准 ComfyUI 二层布局 <kind>/<model>/<file>.ext (Source="Local", SourceId="local:...").
/// </summary>
public class ModelFilesystemScanner
{
    private static readonly Dictionary<string, ModelKind> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["checkpoint"]      = ModelKind.Checkpoint,
        ["checkpoints"]     = ModelKind.Checkpoint,
        ["lora"]            = ModelKind.LORA,
        ["loras"]           = ModelKind.LORA,
        ["vae"]             = ModelKind.VAE,
        ["controlnet"]      = ModelKind.Controlnet,
        ["embedding"]       = ModelKind.TextualInversion,
        ["embeddings"]      = ModelKind.TextualInversion,
        ["textualinversion"]= ModelKind.TextualInversion,
        ["upscale"]         = ModelKind.Upscaler,
        ["upscaler"]        = ModelKind.Upscaler,
        ["upscale_models"]  = ModelKind.Upscaler,
        ["hypernetwork"]    = ModelKind.Hypernetwork,
        ["hypernetworks"]   = ModelKind.Hypernetwork,
    };

    private static readonly HashSet<string> ModelFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".onnx", ".gguf",
    };

    private readonly AppLogger? _logger;

    public ModelFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public virtual IReadOnlyList<DownloadedModel> Scan(string modelsDir)
    {
        var results = new List<DownloadedModel>();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return results;

        // 双布局同遍:
        //   meta.json 三层: <kind>/<model>/<version>/meta.json     (v0.6.20 marketplace)
        //   标准二层:      <kind>/<model>/<file>.{ext}              (标准 ComfyUI 用户手工放)
        foreach (var kindDir in Directory.EnumerateDirectories(modelsDir))
        {
            var kindName = Path.GetFileName(kindDir);
            foreach (var modelDir in Directory.EnumerateDirectories(kindDir))
            {
                var modelName = Path.GetFileName(modelDir);

                // 现有 meta.json 路径: <kind>/<model>/<version>/meta.json
                foreach (var versionDir in Directory.EnumerateDirectories(modelDir))
                {
                    var metaPath = Path.Combine(versionDir, "meta.json");
                    if (!File.Exists(metaPath))
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: missing meta.json");
                        continue;
                    }

                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        var sidecar = JsonSerializer.Deserialize<ModelMetaSidecar>(json,
                            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                        if (sidecar is null)
                        {
                            _logger?.Warn("model-scanner", $"skip {versionDir}: meta.json null");
                            continue;
                        }

                        results.Add(new DownloadedModel
                        {
                            SubfolderName = Path.GetFileName(versionDir),
                            FullPath = versionDir,
                            Kind = sidecar.Kind,
                            Title = sidecar.Title,
                            Source = sidecar.Source,
                            SourceId = sidecar.SourceId,
                            SourceVersionId = sidecar.SourceVersionId,
                            DownloadedAt = sidecar.DownloadedAt,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: parse fail {ex.Message}");
                    }
                }

                // 新标准布局: <kind>/<model>/*.{ext} 直接子文件
                var directModelFiles = Directory.EnumerateFiles(modelDir)
                    .Where(f => ModelFileExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
                if (directModelFiles.Count > 0)
                {
                    results.Add(BuildLocalModel(kindName, modelName, modelDir, _logger));
                }
            }
        }

        return results;
    }

    private static DownloadedModel BuildLocalModel(string kindDir, string modelDir, string modelDirPath, AppLogger? logger)
    {
        var modelFiles = Directory.EnumerateFiles(modelDirPath)
            .Where(f => ModelFileExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        DateTime downloadedAt;
        if (modelFiles.Count > 0)
            downloadedAt = modelFiles.Max(f => File.GetLastWriteTime(f));
        else
            downloadedAt = Directory.GetLastWriteTime(modelDirPath);

        return new DownloadedModel
        {
            SubfolderName = modelDir,
            FullPath = modelDirPath,
            Title = PrettyPrint(modelDir),
            Source = "Local",
            SourceId = $"local:{kindDir}/{modelDir}".ToLowerInvariant(),
            SourceVersionId = "",
            DownloadedAt = downloadedAt,
            Kind = InferKind(kindDir),
        };
    }

    private static ModelKind InferKind(string kindDirName)
        => KindAliases.TryGetValue(kindDirName, out var k) ? k : ModelKind.Other;

    private static string PrettyPrint(string raw)
    {
        var spaced = raw.Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }
}