using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20:扫描 ModelsDirectory 找到已下载的 model versions。
/// 递归 walks <ModelsDir>/<kind>/<model-slug-id>/<version-slug-id>/meta.json。
/// 缺失 meta.json 或 malformed → 跳过(不 throw)。</summary>
public class ModelFilesystemScanner
{
    private readonly AppLogger? _logger;

    public ModelFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<DownloadedModel> Scan(string modelsDir)
    {
        var results = new List<DownloadedModel>();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return results;

        // Walk: modelsDir / <kind> / <model-slug-id> / <version-slug-id> / meta.json
        foreach (var kindDir in Directory.EnumerateDirectories(modelsDir))
        {
            foreach (var modelDir in Directory.EnumerateDirectories(kindDir))
            {
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
            }
        }

        return results;
    }
}
