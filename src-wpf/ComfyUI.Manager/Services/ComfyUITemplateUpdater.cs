using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.22 T5:ComfyUI template update — wipe contents of a target directory then
/// git clone comfyanonymous/ComfyUI back to the same path. Destructive.
///
/// v0.6.22.x 改:v0.6.22 T5 是 per-env(env.ComfyuiSource),用户 2026-08-21 反馈
/// "我们默认只有一个模板...我们不会去更新环境中的环境,只是为下一个创建的
/// 环境更新" — 重构为 path-based,目标 = &lt;projectRoot&gt;/ComfyUITemplate/ master template
/// (v1.0.0+ 从 `ComfyUI/` 重命名),用于下一次创建 env 时复制(shared 布局 / script bundle)。
///
/// Confirms:
/// - Caller MUST gate with confirm dialog before invoking — this service will
///   not prompt.
/// - Keeps the directory itself (junction target / permissions preserved).
/// - `--depth=1` clone for speed — template just needs current main.
///
/// AppLogger subsystem: <c>comfyui-template-update</c>.
/// </summary>
public class ComfyUITemplateUpdater
{
    private readonly GitRunner _git;
    private readonly AppLogger? _logger;

    public ComfyUITemplateUpdater(GitRunner git, AppLogger? logger = null)
    {
        _git = git;
        _logger = logger;
    }

    /// <summary>
    /// Wipe contents of <paramref name="targetDir"/> (must already exist) then git clone
    /// comfyanonymous/ComfyUI back to the same path. Returns NodeOperationResult —
    /// never throws on git failure (only on invalid args).
    /// </summary>
    public virtual async Task<NodeOperationResult> UpdateAsync(
        string targetDir,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return NodeOperationResult.Fail("模板目录不能为空");
        if (!Directory.Exists(targetDir))
            return NodeOperationResult.Fail($"模板目录不存在:{targetDir}");

        _logger?.Info("comfyui-template-update", $"target='{targetDir}' 开始模板更新");
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
            _logger?.Error("comfyui-template-update", "wipe failed", ex);
            return NodeOperationResult.Fail($"删除模板目录内容失败:{ex.Message}");
        }

        // 2. git clone --depth=1 (fast, no history needed for template)
        progress?.Report("正在 git clone ComfyUI...");
        GitResult r;
        try
        {
            r = await _git.RunAsync(
                workdir: targetDir,
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
            _logger?.Warn("comfyui-template-update", $"target='{targetDir}' git clone 失败:{r.Stderr}");
            return NodeOperationResult.Fail($"git clone 失败:{r.Stderr}");
        }

        progress?.Report("ComfyUI 模板更新完成");
        _logger?.Info("comfyui-template-update", $"target='{targetDir}' 模板更新完成");
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