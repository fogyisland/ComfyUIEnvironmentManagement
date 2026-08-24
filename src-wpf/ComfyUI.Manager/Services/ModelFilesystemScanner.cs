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
/// v1.0.0 T6:同遍也识别扁平布局 <kind>/<file>.ext,每顶层 model 文件 = 1 条记录(SourceId="local:{kind}/{file}".ToLowerInvariant())。
/// v1.0.0 T7:3-level 二层布局也改成 per-file(每 <kind>/<model>/<file>.ext = 1 card,Title=文件名),统一 flat/3-level 都走 BuildFlatModel helper。
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

        // 三布局同遍:
        //   meta.json 三层: <kind>/<model>/<version>/meta.json     (v0.6.20 marketplace)
        //   标准二层 (T7): <kind>/<model>/<file>.{ext}             (每文件 = 1 card, Title=文件名)
        //   扁平布局 (T6): <kind>/<file>.{ext}                      (每顶层 model 文件 = 1 record)
        foreach (var kindDir in Directory.EnumerateDirectories(modelsDir))
        {
            var kindName = Path.GetFileName(kindDir);

            foreach (var modelDir in Directory.EnumerateDirectories(kindDir))
            {
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

                // 3-level standard layout (T5 → T7): <kind>/<model>/<file>.ext — 每个文件 = 1 卡
                foreach (var file in Directory.EnumerateFiles(modelDir))
                {
                    if (!ModelFileExtensions.Contains(Path.GetExtension(file)))
                        continue;
                    var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                    results.Add(BuildFlatModel(kindName, fileNameNoExt, file));
                }
            }

            // 扁平布局 (T6): <kind>/<file>.ext 直接子文件 — 每个文件 = 1 卡
            //   SourceId 与 3-level 路径格式相同("local:{kind}/{filename-no-ext}") — 同文件无论布局只 1 卡
            foreach (var file in Directory.EnumerateFiles(kindDir))
            {
                if (!ModelFileExtensions.Contains(Path.GetExtension(file)))
                    continue;
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                results.Add(BuildFlatModel(kindName, fileNameNoExt, file));
            }
        }

        return results;
    }

    private static DownloadedModel BuildFlatModel(string kindName, string fileNameNoExt, string fullPath)
    {
        return new DownloadedModel
        {
            SubfolderName = kindName,                                // 统一 flat/3-level 都用 kindName
            FullPath = fullPath,
            Title = PrettyPrint(fileNameNoExt),                      // 用文件名(不是 dir 名)
            Source = "Local",
            SourceId = $"local:{kindName}/{fileNameNoExt}".ToLowerInvariant(),  // 统一 flat/3-level 格式
            SourceVersionId = "",
            DownloadedAt = File.GetLastWriteTime(fullPath),          // 文件 mtime(不是 dir mtime)
            Kind = InferKind(kindName),
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