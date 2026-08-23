using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 T11: 通用 template source updater — wipe contents of any target
/// directory (which must already exist) then git clone the given repo URL back
/// to the same path. Destructive.
///
/// v0.6.22.x evolution:
/// - v0.6.22 T5: per-env env.ComfyuiSource, hardcoded comfyanonymous/ComfyUI.
/// - v0.6.22.x: path-based, target = &lt;projectRoot&gt;/ComfyUITemplate/ master
///   template (only affects next env creation).
/// - v1.0.0 T11 (G10): per-repo-URL. Takes <c>repoUrl</c> as a parameter so it
///   can update any template (ComfyUI, A1111, custom). Hardcoded URL removed.
///
/// Confirms:
/// - Caller MUST gate with confirm dialog before invoking — this service will
///   not prompt.
/// - Keeps the directory itself (junction target / permissions preserved).
/// - <c>--depth=1</c> clone for speed — template just needs current main.
///
/// AppLogger subsystem: <c>template-source-update</c>.
/// </summary>
public class TemplateSourceUpdater
{
    private readonly GitRunner _git;
    private readonly AppLogger? _logger;

    public TemplateSourceUpdater(string gitExe, HttpProxyConfig? gitProxy = null, AppLogger? logger = null)
    {
        _git = new GitRunner(gitExe, gitProxy);
        _logger = logger;
    }

    /// <summary>
    /// Wipe contents of <paramref name="targetDir"/> (must already exist) then
    /// git clone <paramref name="repoUrl"/> back to the same path. Returns
    /// <see cref="NodeOperationResult"/> — never throws on git failure (only on
    /// invalid args).
    /// </summary>
    public virtual async Task<NodeOperationResult> UpdateAsync(
        string targetDir,
        string repoUrl,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return NodeOperationResult.Fail("targetDir 不能为空");
        if (string.IsNullOrWhiteSpace(repoUrl))
            return NodeOperationResult.Fail("repoUrl 不能为空");
        if (!Directory.Exists(targetDir))
            return NodeOperationResult.Fail($"模板目录不存在:{targetDir}");

        _logger?.Info("template-source-update", $"target='{targetDir}' repo='{repoUrl}' 开始模板更新");
        progress?.Report($"开始模板更新:{targetDir}");

        // 1. delete contents (keep dir for permissions/junction)
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir))
            {
                if (ct.IsCancellationRequested)
                    return NodeOperationResult.Fail("用户取消");
                TryDelete(entry);
                progress?.Report($"已删除:{Path.GetFileName(entry)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("template-source-update", "wipe failed", ex);
            return NodeOperationResult.Fail($"删除模板目录内容失败:{ex.Message}");
        }

        // 2. git clone --depth=1 (fast, no history needed for template)
        progress?.Report($"正在 git clone {repoUrl}...");
        GitResult r;
        try
        {
            r = await _git.RunAsync(
                workdir: targetDir,
                args: new[] { "clone", "--depth=1", repoUrl, "." },
                timeout: TimeSpan.FromMinutes(5),
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            _logger?.Error("template-source-update", "git clone threw", ex);
            return NodeOperationResult.Fail($"git clone 异常:{ex.Message}");
        }

        if (!r.Ok)
        {
            _logger?.Warn("template-source-update", $"target='{targetDir}' git clone 失败:{r.Stderr}");
            return NodeOperationResult.Fail($"git clone 失败:{r.Stderr}");
        }

        progress?.Report("模板更新完成");
        _logger?.Info("template-source-update", $"target='{targetDir}' 模板更新完成");
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
