using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:env 启动后扫描 Settings.WorkflowsDirectory,
/// 给每个已下载 workflow subfolder 在 &lt;env.ComfyuiSource&gt;/user/default/workflows/
/// 下创建 junction(Windows)/symlink(Linux/macOS)。
/// 失败 WARN + 计数,不抛 — 永远不影响 env-start 状态。</summary>
public class WorkflowSymlinker
{
    private readonly Settings _settings;
    private readonly JunctionLinker _linker;
    private readonly WorkflowFilesystemScanner _scanner;
    private readonly AppLogger? _logger;

    public WorkflowSymlinker(
        Settings settings, JunctionLinker linker,
        WorkflowFilesystemScanner scanner, AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _logger = logger;
    }

    public virtual async Task<WorkflowSyncResult> SyncToEnvAsync(
        string envComfyuiSource, CancellationToken ct = default)
    {
        var empty = new WorkflowSyncResult { Linked = 0, Skipped = 0, Failed = 0, Errors = Array.Empty<string>() };
        if (string.IsNullOrWhiteSpace(envComfyuiSource))
        {
            _logger?.Warn("workflow-symlink", "env.ComfyuiSource empty; skip sync");
            return empty;
        }

        // resolve workflows dir
        var workflowsDir = ResolveWorkflowsDir();
        if (string.IsNullOrWhiteSpace(workflowsDir) || !Directory.Exists(workflowsDir))
        {
            _logger?.Warn("workflow-symlink",
                $"workflows dir missing: '{workflowsDir}'; skip sync");
            return empty;
        }

        var downloaded = _scanner.Scan(workflowsDir);
        if (downloaded.Count == 0)
        {
            _logger?.Info("workflow-symlink", "no downloaded workflows to sync");
            return empty;
        }

        var targetDir = Path.Combine(envComfyuiSource, "user", "default", "workflows");
        try { Directory.CreateDirectory(targetDir); }
        catch (Exception ex)
        {
            _logger?.Error("workflow-symlink", $"create target dir failed: {targetDir}", ex);
            return empty;
        }

        int linked = 0, skipped = 0, failed = 0;
        var errors = new List<string>();

        foreach (var wf in downloaded)
        {
            ct.ThrowIfCancellationRequested();
            var link = Path.Combine(targetDir, wf.SubfolderName);
            var target = wf.FullPath;

            try
            {
                if (Directory.Exists(link))
                {
                    // check if it's already correct
                    var existingTarget = _linker.GetTargetAsync(link, ct).GetAwaiter().GetResult();
                    if (string.Equals(existingTarget, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    // mismatch — delete and recreate
                    Directory.Delete(link);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await _linker.CreateAsync(link, target, ct).ConfigureAwait(false);
                }
                else
                {
                    // Linux / macOS — CreateSymbolicLink(path, target)
                    Directory.CreateSymbolicLink(link, target);
                }
                linked++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{wf.SubfolderName}: {ex.Message}");
                _logger?.Warn("workflow-symlink",
                    $"link failed for {wf.SubfolderName}: {ex.Message}");
            }
        }

        _logger?.Info("workflow-symlink",
            $"sync done linked={linked} skipped={skipped} failed={failed} target='{targetDir}'");
        return new WorkflowSyncResult
        {
            Linked = linked,
            Skipped = skipped,
            Failed = failed,
            Errors = errors,
        };
    }

    private string ResolveWorkflowsDir()
    {
        var dir = _settings.WorkflowsDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return "";
        // If relative, resolve against process root(approx — caller should pass absolute if known)
        if (!Path.IsPathRooted(dir))
        {
            var processRoot = Path.GetDirectoryName(System.Environment.ProcessPath);
            if (!string.IsNullOrEmpty(processRoot))
                dir = Path.Combine(processRoot, dir);
        }
        return dir;
    }
}

public class WorkflowSyncResult
{
    public int Linked { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}