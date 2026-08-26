using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x #577:env 行「安装本地常用」按钮的 installer — 枚举
/// <see cref="Models.Settings.LocalNodesDirectory"/> 下每个子目录,逐个 copy 到 env 的
/// custom_nodes/&lt;子目录名&gt;,然后跑该子目录的 requirements.txt(过滤 torch 行)。
///
/// 跟 <see cref="ComfyUIManagerInstaller"/> (git clone 网络源) / <see cref="LocalNodeCopyInstaller"/>
/// (单节点 copy,不带 pip) 的区别:
/// - <b>不</b> 走网络(git clone),完全本地 copy
/// - <b>不</b> 单节点 — 批量
/// - 每个子目录 copy 完后<b>立即</b>跑该子目录的 requirements.txt(过滤 torch 行),失败
///   skip 到下一个,不让一个坏包阻塞整体
///
/// 行为:
/// - IsInstalled(env):LocalNodesDirectory 子目录集合 ⊆ env/custom_nodes/ 子目录集合(全装)
/// - InstallAsync(env, progress, ct):
///   1. 校验 Settings.LocalNodesDirectory 非空 + 目录存在 + env 有效
///   2. 枚举源目录直接子项(目录,跳过文件 / 隐藏)
///   3. 对每个子项:
///      - copy → env/custom_nodes/&lt;name&gt;(递归,覆盖)
///      - 若 &lt;name&gt;/requirements.txt 存在:pip install -r(过滤 torch 行,走 RequirementsFileInstaller)
///      - pip 失败 → log + skip 到下一个(不影响其他子项)
///   4. 汇总成功 / 失败计数 → 返 NodeOperationResult
///   - 全失败 → Fail(reason=全部失败列表)
///   - 全成功 → Ok("X 个节点已装")
///   - 部分成功 → Ok("X/Y 成功,失败:...")(仍 Ok,用户看到 inline 状态面板 LogLines)
/// </summary>
public class LocalNodeBulkInstaller
{
    private readonly Settings _settings;
    private readonly RequirementsFileInstaller _reqFileInstaller;
    private readonly AppLogger? _logger;

    public LocalNodeBulkInstaller(
        Settings settings,
        RequirementsFileInstaller reqFileInstaller,
        AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reqFileInstaller = reqFileInstaller ?? throw new ArgumentNullException(nameof(reqFileInstaller));
        _logger = logger;
    }

    /// <summary>
    /// 解析 LocalNodesDirectory 绝对路径。空 / 不存在 → 返 null。
    /// </summary>
    public string? ResolveSourceDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.LocalNodesDirectory)) return null;
        var dir = _settings.LocalNodesDirectory;
        if (!Path.IsPathRooted(dir))
        {
            // 相对路径:解析到当前 process 的 BaseDirectory(开发 = bin\Debug,release = package root)
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// 检测「本地常用节点」是否已全部装好 — 即 LocalNodesDirectory 子目录 ⊆ env/custom_nodes/。
    /// 返回 true 表示所有源子目录都已 copy 到 env 的 custom_nodes。
    /// 注:此判断<b>不</b>检查 requirements.txt 是否安装成功(那个没持久化 marker,只能
    /// 通过文件存在推断);失败安装的包也会被算作「已装」(目录在)。
    /// </summary>
    public bool IsInstalled(Environment env)
    {
        var srcDir = ResolveSourceDirectory();
        if (srcDir is null) return false;
        if (env is null || string.IsNullOrWhiteSpace(env.CustomNodesPath)) return false;
        if (!Directory.Exists(env.CustomNodesPath)) return false;

        var sourceNames = EnumerateLocalPackageNames(srcDir);
        if (sourceNames.Count == 0) return false;  // 源目录空,不算已装(用户可能还没放东西)

        foreach (var name in sourceNames)
        {
            var target = Path.Combine(env.CustomNodesPath, name);
            if (!Directory.Exists(target)) return false;
        }
        return true;
    }

    /// <summary>
    /// 枚举 LocalNodesDirectory 直接子目录(过滤隐藏 / 文件)— 包名 = 子目录名。
    /// 排序让多次运行顺序稳定(LogLines 行序一致)。
    /// </summary>
    public static IReadOnlyList<string> EnumerateLocalPackageNames(string sourceDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(sourceDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("."))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>
    /// 批量安装:逐子目录 copy + pip install,失败 skip。
    /// </summary>
    public virtual async Task<NodeOperationResult> InstallAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        var srcDir = ResolveSourceDirectory();
        if (srcDir is null)
        {
            return NodeOperationResult.Fail(
                "Settings.LocalNodesDirectory 未配置或目录不存在,请在「设置」页配置");
        }
        if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        {
            return NodeOperationResult.Fail("env 缺 custom_nodes_path");
        }
        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return NodeOperationResult.Fail("env 缺 comfyui_source,无法定位 custom_nodes");
        }

        var packages = EnumerateLocalPackageNames(srcDir);
        if (packages.Count == 0)
        {
            return NodeOperationResult.Fail($"本地节点目录为空:{srcDir}");
        }

        Directory.CreateDirectory(env.CustomNodesPath);

        progress?.Report($"stage:开始批量装 {packages.Count} 个本地节点");
        _logger?.Info("local-nodes-bulk", $"env='{env.Id}' src='{srcDir}' 共 {packages.Count} 个子目录");

        var successNames = new List<string>();
        var failReasons = new List<string>();
        var venvPy = TryResolveVenvPython(env);

        foreach (var name in packages)
        {
            ct.ThrowIfCancellationRequested();
            var srcPkg = Path.Combine(srcDir, name);
            var targetDir = Path.Combine(env.CustomNodesPath, name);

            try
            {
                progress?.Report($"info:copy {name}");
                // 已存在 → rm -rf(用 RobustDirectoryDelete 处理 ReadOnly + 长路径)
                if (Directory.Exists(targetDir))
                {
                    RobustDirectoryDelete.Delete(targetDir);
                }
                CopyDirectory(srcPkg, targetDir);

                var reqPath = Path.Combine(targetDir, "requirements.txt");
                if (File.Exists(reqPath) && venvPy is not null)
                {
                    progress?.Report($"info:pip install -r {name}/requirements.txt");
                    var filteredPath = Path.Combine(targetDir, RequirementsFileInstaller.FilteredRequirementsFileName);
                    var pipResult = await _reqFileInstaller.InstallAsync(
                        reqPath, filteredPath, venvPy,
                        line => progress?.Report($"  pip: {line}"),
                        ct);

                    if (!pipResult.Success)
                    {
                        // pip 失败 skip,不删已 copy 的目录(让用户能看到这个包,手动修复)
                        var reason = $"{name}:pip 失败 — {pipResult.Reason ?? "未知"}";
                        failReasons.Add(reason);
                        progress?.Report($"warn:{reason}");
                        _logger?.Warn("local-nodes-bulk", $"env='{env.Id}' {reason}");
                        continue;
                    }
                }

                successNames.Add(name);
                progress?.Report($"info:{name} 装成功");
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
                _logger?.Warn("local-nodes-bulk", $"env='{env.Id}' {reason}");
            }
        }

        if (successNames.Count == 0)
        {
            var all = string.Join("; ", failReasons);
            return NodeOperationResult.Fail($"全部失败:{all}");
        }

        var summary = $"{successNames.Count}/{packages.Count} 个节点已装"
                      + (failReasons.Count > 0 ? $",失败:{string.Join("; ", failReasons)}" : "");
        progress?.Report($"stage:{summary}");
        _logger?.Info("local-nodes-bulk", $"env='{env.Id}' {summary}");
        return NodeOperationResult.Ok(summary);
    }

    /// <summary>
    /// 解析 env 的 venv python 路径。失败 → 返 null(让 InstallAsync 跳过 pip,只 copy)。
    /// </summary>
    private static string? TryResolveVenvPython(Environment env)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
                return env.PythonExecutable;
            if (string.IsNullOrWhiteSpace(env.VenvPath)) return null;
            var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine("Scripts", "python.exe")
                : Path.Combine("bin", "python");
            var exe = Path.Combine(env.VenvPath, relative);
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 递归 copy 目录(src → dest)。dest 不存在则创建,存在则覆盖同名文件(不删 dest 多余文件)。
    /// 跟 <see cref="LocalNodeCopyInstaller"/> 一样的最小实现,不抽到公共 helper(只两处用)。
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
