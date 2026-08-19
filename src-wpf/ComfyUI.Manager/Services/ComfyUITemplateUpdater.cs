using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.22 T5:ComfyUI template update — wipe contents of env.ComfyuiSource
/// then git clone comfyanonymous/ComfyUI back to the same path. Destructive.
///
/// Confirms:
/// - Caller (EnvironmentListViewModel) MUST gate with confirm dialog before
///   invoking — this service will not prompt.
/// - Keeps the directory itself (junction target / permissions preserved).
/// - `--depth=1` clone for speed — template just needs current main.
///
/// AppLogger subsystem: <c>comfyui-template-update</c>.
/// </summary>
public class ComfyUITemplateUpdater
{
    private readonly GitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly AppLogger? _logger;

    public ComfyUITemplateUpdater(
        GitRunner git,
        EnvironmentRepository envRepo,
        AppLogger? logger = null)
    {
        _git = git;
        _envRepo = envRepo;
        _logger = logger;
    }

    /// <summary>
    /// Wipe contents of env.ComfyuiSource then git clone comfyanonymous/ComfyUI
    /// back to the same path. Returns NodeOperationResult — never throws on
    /// git failure (only on invalid args).
    /// </summary>
    public virtual async Task<NodeOperationResult> UpdateAsync(
        Environment env,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (env is null) return NodeOperationResult.Fail("env 不能为 null");
        if (string.IsNullOrWhiteSpace(env.ComfyuiSource) || !Directory.Exists(env.ComfyuiSource))
            return NodeOperationResult.Fail($"ComfyUI 目录不存在:{env.ComfyuiSource}");

        _logger?.Info("comfyui-template-update", $"env='{env.Name}' comfyui='{env.ComfyuiSource}' 开始模板更新");
        progress?.Report($"开始模板更新:{env.ComfyuiSource}");

        // 1. delete contents (keep dir for permissions/junction)
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(env.ComfyuiSource))
            {
                if (ct.IsCancellationRequested)
                    return NodeOperationResult.Fail("用户取消");
                TryDelete(entry);
                progress?.Report($"已删除:{Path.GetFileName(entry)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("comfyui-template-update", "wipe failed", ex);
            return NodeOperationResult.Fail($"删除 ComfyUI 目录内容失败:{ex.Message}");
        }

        // 2. git clone --depth=1 (fast, no history needed for template)
        progress?.Report("正在 git clone ComfyUI...");
        GitResult r;
        try
        {
            r = await _git.RunAsync(
                workdir: env.ComfyuiSource,
                args: new[] { "clone", "--depth=1", "https://github.com/comfyanonymous/ComfyUI.git", "." },
                timeout: TimeSpan.FromMinutes(5),
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            _logger?.Error("comfyui-template-update", "git clone threw", ex);
            return NodeOperationResult.Fail($"git clone 异常:{ex.Message}");
        }

        if (!r.Ok)
        {
            _logger?.Warn("comfyui-template-update", $"env='{env.Name}' git clone 失败:{r.Stderr}");
            return NodeOperationResult.Fail($"git clone 失败:{r.Stderr}");
        }

        progress?.Report("ComfyUI 模板更新完成");
        _logger?.Info("comfyui-template-update", $"env='{env.Name}' 模板更新完成");
        return NodeOperationResult.Ok(null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 单个 entry 失败继续(让其他 entry 删掉)— wipe 整体失败会被外层 catch 抓到。
        }
    }
}