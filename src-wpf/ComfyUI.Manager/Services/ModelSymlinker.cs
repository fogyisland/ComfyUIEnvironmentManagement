using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20 + v0.6.22+:env 启动后扫描 Settings.DefaultModelsDirectory,
/// 给每个已下载 model version 在 &lt;envComfyuiSource&gt;/models/&lt;kind&gt;/
/// 下创建指向 &lt;modelsDir&gt;/&lt;kind&gt;/&lt;model-slug&gt;-&lt;id8&gt;/&lt;version-slug&gt;-&lt;vid8&gt;/
/// 的 junction(Windows)/symlink(Linux/macOS)。
/// v0.6.22+:原 ModelsDirectory 字段硬删 — 现在 DefaultModelsDirectory 同时担任 env-create
/// junction 目标 + 模型市场下载目录 + symlinker 扫描源。
/// link 名字 = &lt;model-slug&gt;-&lt;id8&gt;__&lt;version-slug&gt;-&lt;vid8&gt;
/// (env 端用 __ 双下划线分隔,避免 model-slug 与 version-slug 同前缀时碰撞)。
/// v1.0.0 multi-template T6:per-kind ModelsSubdir — env 端 models dir 由
/// <see cref="Models.Environment.TemplateConfigSnapshot"/>.ModelsSubdir 决定(ComfyUI="models",
/// Forge="models/Stable-diffusion",自定义=&lt;用户输入&gt;);snapshot 缺失时 fallback "models"。
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

    /// <summary>v1.0.0 multi-template T6 (G8):resolve env 端 models 目录路径。
    /// 优先 <see cref="Models.Environment.TemplateConfigSnapshot"/>.ModelsSubdir;null/空 fallback "models"。
    /// subdir 内 '/' 在 Windows 上替换为 <see cref="Path.DirectorySeparatorChar"/>,
    /// 保证 Linux/macOS 测试 fixture "models/Stable-diffusion" 跨平台行为一致。</summary>
    public static string GetEnvModelsDir(Models.Environment env, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(projectRoot);
        var subdir = env.TemplateConfigSnapshot?.ModelsSubdir;
        if (string.IsNullOrEmpty(subdir)) subdir = "models";
        // v1.0.0.x: envRoot 用 env.RootPath(绝对),跟 ProcessLauncher.BuildStartCommand 同修,
        // 避免 dev build projectRoot = bin/Debug 时拼出错的 envs\<name>。
        var envRoot = !string.IsNullOrWhiteSpace(env.RootPath)
            ? env.RootPath
            : Path.Combine(projectRoot, "envs", env.Name);
        return Path.Combine(envRoot, subdir.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>v1.0.0 multi-template T6 (G8):per-kind env models dir.
    /// ComfyUI snapshot "models" → <c>&lt;envRoot&gt;/models</c>;
    /// Forge snapshot "models/Stable-diffusion" → <c>&lt;envRoot&gt;/models/Stable-diffusion</c>;
    /// null/empty snapshot → fallback <c>models</c>. Delegates to the string-based overload
    /// with the resolved env models root path. Caller passes <paramref name="env"/> whose
    /// <c>ComfyuiSource</c> is the env root (T4 always-copy layout).</summary>
    public virtual Task<ModelSyncResult> SyncToEnvAsync(Models.Environment env, CancellationToken ct = default)
    {
        var envRoot = env?.ComfyuiSource ?? "";
        var subdir = env?.TemplateConfigSnapshot?.ModelsSubdir;
        if (string.IsNullOrEmpty(subdir)) subdir = "models";
        var envModelsRoot = Path.Combine(envRoot, subdir.Replace('/', Path.DirectorySeparatorChar));
        return SyncToEnvAsync(envModelsRoot, ct);
    }

    public virtual async Task<ModelSyncResult> SyncToEnvAsync(string envComfyuiSource, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envComfyuiSource))
        {
            _logger?.Warn("model-symlink", "env has empty ComfyuiSource; skip");
            return new ModelSyncResult();
        }

        var modelsDir = _settings.DefaultModelsDirectory;
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            _logger?.Warn("model-symlink", $"DefaultModelsDirectory '{modelsDir}' not exist; skip");
            return new ModelSyncResult();
        }

        var downloaded = _scanner.Scan(modelsDir);
        var linked = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        var envModelsDir = Path.Combine(envComfyuiSource, "models");
        try { Directory.CreateDirectory(envModelsDir); }
        catch (Exception ex)
        {
            _logger?.Warn("model-symlink", $"failed to create env models dir '{envModelsDir}': {ex.Message}");
            return new ModelSyncResult();
        }

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

        _logger?.Info("model-symlink", $"linked={linked} skipped={skipped} failed={failed}");
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
