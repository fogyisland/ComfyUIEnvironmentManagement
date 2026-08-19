using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20:env 启动后扫描 Settings.ModelsDirectory,
/// 给每个已下载 model version 在 &lt;envComfyuiSource&gt;/models/&lt;kind&gt;/
/// 下创建指向 &lt;modelsDir&gt;/&lt;kind&gt;/&lt;model-slug&gt;-&lt;id8&gt;/&lt;version-slug&gt;-&lt;vid8&gt;/
/// 的 junction(Windows)/symlink(Linux/macOS)。
/// link 名字 = &lt;model-slug&gt;-&lt;id8&gt;__&lt;version-slug&gt;-&lt;vid8&gt;
/// (env 端用 __ 双下划线分隔,避免 model-slug 与 version-slug 同前缀时碰撞)。
/// 失败 WARN + 计数 + Errors list,不抛 — 永远不影响 env-start 状态。</summary>
public class ModelSymlinker
{
    private readonly Settings _settings;
    private readonly ModelFilesystemScanner _scanner;
    private readonly JunctionLinker _linker;
    private readonly AppLogger? _logger;

    public ModelSymlinker(
        Settings settings,
        ModelFilesystemScanner scanner,
        JunctionLinker linker,
        AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _logger = logger;
    }

    public async Task<ModelSyncResult> SyncToEnvAsync(string envId, string envComfyuiSource, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envComfyuiSource))
        {
            _logger?.Warn("model-symlink", $"env '{envId}' has empty ComfyuiSource; skip");
            return new ModelSyncResult();
        }

        var modelsDir = _settings.ModelsDirectory;
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            _logger?.Warn("model-symlink", $"ModelsDirectory '{modelsDir}' not exist; skip");
            return new ModelSyncResult();
        }

        var downloaded = _scanner.Scan(modelsDir);
        var linked = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        var envModelsDir = Path.Combine(envComfyuiSource, "models");
        Directory.CreateDirectory(envModelsDir);

        foreach (var dm in downloaded)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kindSubfolder = dm.Kind.ToComfyUiSubfolder();
                var envKindDir = Path.Combine(envModelsDir, kindSubfolder);
                Directory.CreateDirectory(envKindDir);

                // link name: <model-slug>-<id8>__<version-slug>-<vid8>
                // SubfolderName from scanner is already the on-disk version dir name (T5 used
                // ToSlugId(version.Name, version.SourceVersionId) to produce it). Re-running
                // through ToSlugId would double the vid8, so use it directly. Same for the
                // model dir name derived from FullPath's parent.
                var modelSlugId = Path.GetFileName(Path.GetDirectoryName(dm.FullPath)!) ?? "";
                var versionSlugId = dm.SubfolderName;
                var linkName = $"{modelSlugId}__{versionSlugId}";
                var linkPath = Path.Combine(envKindDir, linkName);
                var targetPath = dm.FullPath;

                if (Directory.Exists(linkPath))
                {
                    var existingTarget = await _linker.GetTargetAsync(linkPath, ct);
                    if (string.Equals(existingTarget, Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    // Mismatch — delete + recreate
                    Directory.Delete(linkPath);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await _linker.CreateAsync(linkPath, targetPath, ct).ConfigureAwait(false);
                }
                else
                {
                    Directory.CreateSymbolicLink(linkPath, targetPath);
                }
                linked++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{dm.SubfolderName}: {ex.Message}");
                _logger?.Warn("model-symlink", $"FAIL {dm.SubfolderName}: {ex.Message}");
            }
        }

        _logger?.Info("model-symlink", $"env '{envId}' linked={linked} skipped={skipped} failed={failed}");
        return new ModelSyncResult { Linked = linked, Skipped = skipped, Failed = failed, Errors = errors };
    }
}

public class ModelSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
