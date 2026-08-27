using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x #589:env → <see cref="Settings.LocalNodesDirectory"/> 同步 — 把已装 env
/// 的 custom_nodes/ 子目录反向 copy 到本地节点源,保证所有节点在本地存在目录。
///
/// <para>
/// <b>使用场景</b>:用户跑了 env 一段时间,通过 ComfyUI-Manager 装了若干节点
/// (VideoHelperSuite / Advanced-ControlNet / Custom-Scripts / ...),这些节点在
/// env/custom_nodes/ 下,但不在 localnodes/ 源目录 — 下次重装 env / 切换 env 时
/// LocalNodeBulkInstaller 只会装 localnodes/ 里的子目录,Manager 装的节点丢了。
/// 用本 service 一次性把这些节点 sync 回 localnodes/,LocalNodeBulkInstaller 下次
/// 装的时候会复制到 env 并跑 requirements.txt(cv2 等依赖也跟着装)。
/// </para>
///
/// <para>
/// <b>排除规则</b>(sync 时跳过):
/// <list type="bullet">
///   <item><c>__pycache__</c> — Python 编译缓存,非节点</item>
///   <item><c>.git</c> — git 内部目录,Manager 装的节点带 .git</item>
///   <item><c>ComfyUI-Manager</c> — manager app 管的,不应在 localnodes/</item>
/// </list>
/// </para>
///
/// <para>
/// <b>覆盖策略</b>:sync 时如果源目录已存在同名子目录,直接 overwrite(copy 不删
/// target 多余文件,跟 <see cref="LocalNodeBulkInstaller.CopyDirectory"/> 一致)。
/// localnodes/ 是用户"完整节点源"的单一权威 — 以 env 为准,源目录增量补齐。
/// </para>
///
/// <para>
/// <b>单文件节点</b>(env/custom_nodes/*.py 单文件,如
/// <c>websocket_image_save.py</c>):包成同名子目录 <c>&lt;name&gt;/&lt;name&gt;.py</c>,
/// 让 LocalNodeBulkInstaller 把它当成"节点包"统一处理。
/// </para>
/// </summary>
public class LocalNodeSyncService
{
    private readonly Settings _settings;
    private readonly AppLogger? _logger;

    public LocalNodeSyncService(Settings settings, AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
    }

    /// <summary>
    /// 解析 LocalNodesDirectory 绝对路径。空 / 不存在 → 返 null。
    /// 跟 <see cref="LocalNodeBulkInstaller.ResolveSourceDirectory"/> 同语义,失败时
    /// caller 提示用户去 Settings 配置。
    /// </summary>
    public string? ResolveSourceDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.LocalNodesDirectory)) return null;
        var dir = _settings.LocalNodesDirectory;
        if (!Path.IsPathRooted(dir))
        {
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// 把 env 的 custom_nodes/ 全部同步到 LocalNodesDirectory。
    /// </summary>
    /// <param name="env">目标 env(读 <see cref="Environment.CustomNodesPath"/>)</param>
    /// <param name="progress">可选 progress callback,emit info/warn 行</param>
    /// <param name="ct">cancellation token</param>
    public virtual async Task<LocalNodeSyncResult> SyncAsync(
        Environment env,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        var srcDir = ResolveSourceDirectory();
        if (srcDir is null)
        {
            return LocalNodeSyncResult.Fail(
                "Settings.LocalNodesDirectory 未配置或目录不存在,请在「设置」页配置");
        }
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            return LocalNodeSyncResult.Fail("env 缺 custom_nodes_path");
        }
        if (!Directory.Exists(env.CustomNodesPath))
        {
            return LocalNodeSyncResult.Fail(
                $"env custom_nodes 目录不存在:{env.CustomNodesPath}");
        }

        progress?.Report($"stage:开始同步 env='{env.Name}' custom_nodes → localnodes");
        _logger?.Info("local-node-sync", $"env='{env.Id}' 开始同步 {env.CustomNodesPath} → {srcDir}");

        var added = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failReasons = new List<string>();

        foreach (var entry in EnumerateNodeEntries(env.CustomNodesPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.Name;
            var targetDir = Path.Combine(srcDir, name);

            try
            {
                var existed = Directory.Exists(targetDir);
                if (existed)
                {
                    progress?.Report($"info:更新 {name}");
                }
                else
                {
                    progress?.Report($"info:新增 {name}");
                }

                if (entry.IsDirectory)
                {
                    CopyDirectory(entry.FullPath, targetDir);
                }
                else
                {
                    // 单文件节点:包成同名子目录 <name>/<原 fileName(含 .py 扩展名)>,
                    // 让 LocalNodeBulkInstaller 把它当成"节点包"统一处理。
                    Directory.CreateDirectory(targetDir);
                    var targetFile = Path.Combine(targetDir, Path.GetFileName(entry.FullPath));
                    File.Copy(entry.FullPath, targetFile, overwrite: true);
                }

                if (existed) updated.Add(name);
                else added.Add(name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = $"{name}:{ex.Message}";
                failReasons.Add(reason);
                progress?.Report($"warn:{reason}");
                _logger?.Warn("local-node-sync", $"env='{env.Id}' {reason}");
            }
        }

        // 算 skipped:源目录已存在但**所有** sync 都没动到的 — 实际上我们的策略是
        // 无脑 overwrite,这里 skipped = exclude 列表(不算失败)。
        var summary = $"{added.Count} 个新增 / {updated.Count} 个更新"
                      + (failReasons.Count > 0 ? $",失败:{string.Join("; ", failReasons)}" : "");

        progress?.Report($"stage:同步完成 — {summary}");
        _logger?.Info("local-node-sync", $"env='{env.Id}' {summary}");

        // 全失败 → Fail;部分失败 → Ok 但 reason 标 partial;全成 → Ok
        if (added.Count == 0 && updated.Count == 0 && failReasons.Count > 0)
        {
            return LocalNodeSyncResult.Fail($"全部失败:{string.Join("; ", failReasons)}");
        }

        return LocalNodeSyncResult.Ok(summary, added, updated, skipped, failReasons);
    }

    /// <summary>
    /// 扫 env custom_nodes 下的节点条目(子目录 + 单文件),过滤掉排除项。
    /// </summary>
    public static IReadOnlyList<NodeEntry> EnumerateNodeEntries(
        string customNodesPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customNodesPath) || !Directory.Exists(customNodesPath))
        {
            return Array.Empty<NodeEntry>();
        }

        var entries = new List<NodeEntry>();

        // 子目录(标准节点包)
        foreach (var dir in Directory.EnumerateDirectories(customNodesPath))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (ShouldExclude(name)) continue;
            entries.Add(new NodeEntry(name, dir, IsDirectory: true));
        }

        // 单文件节点(*.py 顶层文件,如 websocket_image_save.py / example_node.py.example)
        // Name 用 GetFileNameWithoutExtension — 跟子目录节点一致用「无后缀名」作 key;
        // target 包成 <name>/<fileName>(带原扩展名)让 LocalNodeBulkInstaller 当节点包处理。
        foreach (var file in Directory.EnumerateFiles(customNodesPath))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (ShouldExclude(fileName)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(name)) continue;
            entries.Add(new NodeEntry(name, file, IsDirectory: false));
        }

        // 排序让多次运行顺序稳定(progress 行序一致)
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    private static bool ShouldExclude(string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        // 隐藏目录 / 文件
        if (name.StartsWith(".")) return true;
        // Python 缓存
        if (name == "__pycache__") return true;
        // ComfyUI-Manager 是 manager app 管的,不应在 localnodes/
        if (string.Equals(name, "ComfyUI-Manager", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 递归 copy(src → dest)。dest 不存在则创建,存在则覆盖同名文件(不删 dest 多余文件)。
    /// 跟 <see cref="LocalNodeBulkInstaller"/> 的私有 CopyDirectory 行为一致,但本类
    /// 不能引用那个私有方法,留两份(同模式 small)。
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

/// <summary>
/// 单个节点条目 — 子目录或单文件(.py)。
/// </summary>
public sealed record NodeEntry(string Name, string FullPath, bool IsDirectory);

/// <summary>
/// sync 结果 — 区分全部失败(Fail)和部分/全成功(Ok)。added/updated/skipped
/// 列表方便 caller 在 UI 里显示具体哪些节点被 sync。
/// </summary>
public sealed record LocalNodeSyncResult(
    bool Success,
    string? Reason,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> FailReasons)
{
    public static LocalNodeSyncResult Ok(
        string summary,
        IReadOnlyList<string> added,
        IReadOnlyList<string> updated,
        IReadOnlyList<string> skipped,
        IReadOnlyList<string> failReasons)
        => new(true, summary, added, updated, skipped, failReasons);

    public static LocalNodeSyncResult Fail(string reason)
        => new(false, reason, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new[] { reason });
}