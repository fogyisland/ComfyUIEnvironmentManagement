using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// ComfyUIManagerInstaller:env 维度装 / 卸 / 检 ComfyUI Manager(<c>custom_nodes/ComfyUI-Manager</c>)。
///
/// 行为:
/// - IsInstalled(env):<c>Directory.Exists(env.ComfyuiSource/custom_nodes/ComfyUI-Manager)</c>
/// - InstallAsync(env, progress, ct):
///   1. 校验 env.ComfyuiSource 非空 + Manager 目录不存在
///   2. git clone <see cref="DefaultRepoUrl"/> → &lt;custom_nodes&gt;/ComfyUI-Manager
///   3. 读 Manager/requirements.txt → 过滤 torch 行 → pip install -r(走 RequirementsFileInstaller)
///   4. pip 失败 / 取消 → rm -rf 整个 Manager 目录 → 返 Fail/Cancelled
///   5. 成功 → 返 Ok
/// - Uninstall(env):Directory.Delete(recursive)整个 Manager 目录;不存在时返 Fail
///
/// 复用 <see cref="RequirementsFileInstaller"/> 跑 pip — 跟 RequirementsInstaller(ComfyUI 依赖)
/// 共享过滤逻辑。
/// </summary>
public class ComfyUIManagerInstaller
{
    public const string DefaultRepoUrl = "https://github.com/ltdrdata/ComfyUI-Manager";
    public const string DirName = "ComfyUI-Manager";
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(2);

    private readonly RequirementsFileInstaller _reqFileInstaller;
    private readonly GitRunner _git;
    private readonly AppLogger? _logger;

    public ComfyUIManagerInstaller(
        RequirementsFileInstaller reqFileInstaller,
        string gitExe = "git",
        HttpProxyConfig? proxy = null,
        AppLogger? logger = null)
    {
        _reqFileInstaller = reqFileInstaller ?? throw new ArgumentNullException(nameof(reqFileInstaller));
        _git = new GitRunner(gitExe, proxy);
        _logger = logger;
    }

    /// <summary>
    /// 检测:Manager 目录是否存在。ComfyuiSource 为空时永远 false(无法定位)。
    /// </summary>
    public bool IsInstalled(Environment env)
    {
        var dir = ResolveTargetDirectory(env);
        return dir is not null && Directory.Exists(dir);
    }

    /// <summary>
    /// 解析 Manager 目录绝对路径;ComfyuiSource 为空时返 null。
    /// </summary>
    public string? ResolveTargetDirectory(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.ComfyuiSource)) return null;
        return Path.Combine(env.ComfyuiSource, "custom_nodes", DirName);
    }

    /// <summary>
    /// 装 Manager。失败 / 取消 → 清理目录 → 返 NodeOperationResult.Fail。
    /// </summary>
    public virtual async Task<NodeOperationResult> InstallAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return NodeOperationResult.Fail("env.ComfyuiSource 为空,无法定位 custom_nodes 路径");
        }
        var targetDir = ResolveTargetDirectory(env)!;
        if (Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail($"ComfyUI Manager 已安装:{targetDir}");
        }

        _logger?.Info("comfyui-manager-install", $"env='{env.Id}' target={targetDir} 开始克隆");
        progress?.Report("stage:克隆 ComfyUI Manager");

        // 1. git clone
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        GitResult cloneResult;
        try
        {
            cloneResult = await _git.RunAsync(
                Path.GetDirectoryName(targetDir)!,
                new[] { "clone", "--", DefaultRepoUrl, DirName },
                GitCloneTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            TryDeleteQuiet(targetDir);
            return NodeOperationResult.Fail("用户取消");
        }
        catch (Exception ex)
        {
            TryDeleteQuiet(targetDir);
            return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}");
        }

        if (!cloneResult.Ok)
        {
            var reason = FirstLine(cloneResult.Stderr, cloneResult.Stdout)
                ?? $"git 退出码 {cloneResult.ExitCode}";
            TryDeleteQuiet(targetDir);
            return NodeOperationResult.Fail($"克隆失败:{reason}");
        }

        // 2. 装 Manager 自己的 requirements.txt(过滤 torch 行)
        var managerReqPath = Path.Combine(targetDir, "requirements.txt");
        var venvPy = ResolveVenvPython(env);
        progress?.Report("stage:安装 ComfyUI Manager 依赖");
        var pipResult = await RunPipForManagerAsync(
            targetDir, managerReqPath, venvPy, progress, ct);

        if (!pipResult.Success)
        {
            // pip 失败 / 取消 → 回滚(rm -rf 整个 Manager 目录)
            _logger?.Warn("comfyui-manager-install",
                $"env='{env.Id}' pip 失败(reason={pipResult.Reason}),回滚删除整个 Manager 目录");
            TryDeleteQuiet(targetDir);
            return NodeOperationResult.Fail(pipResult.Reason ?? "pip 失败");
        }

        _logger?.Info("comfyui-manager-install",
            $"env='{env.Id}' 装成功 packages={pipResult.InstalledCount}");
        progress?.Report($"info:ComfyUI Manager 安装成功({pipResult.InstalledCount} 个包)");
        return NodeOperationResult.Ok(pipResult.InstalledCount.ToString());
    }

    /// <summary>
    /// 跑 pip install -r &lt;managerDir&gt;/requirements.txt。包装成 protected virtual 让
    /// 测试能注入失败 / 取消(不 mock 整个 RequirementsFileInstaller)。
    /// </summary>
    protected virtual Task<RequirementsInstallResult> RunPipForManagerAsync(
        string managerDir,
        string requirementsFilePath,
        string venvPythonPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var filteredOutputPath = Path.Combine(managerDir, RequirementsFileInstaller.FilteredRequirementsFileName);
        return _reqFileInstaller.InstallAsync(
            requirementsFilePath,
            filteredOutputPath,
            venvPythonPath,
            line => progress?.Report(line),
            ct);
    }

    /// <summary>
    /// 卸 Manager。rm -rf 整个目录,不存在返 Fail。
    /// </summary>
    public virtual NodeOperationResult Uninstall(Environment env)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        var targetDir = ResolveTargetDirectory(env);
        if (targetDir is null)
        {
            return NodeOperationResult.Fail("env.ComfyuiSource 为空");
        }
        if (!Directory.Exists(targetDir))
        {
            return NodeOperationResult.Fail("ComfyUI Manager 未安装");
        }
        _logger?.Info("comfyui-manager-uninstall", $"env='{env.Id}' dir={targetDir}");
        try
        {
            RobustDirectoryDelete.Delete(targetDir);
        }
        catch (Exception ex)
        {
            // RobustDirectoryDelete 已经重试 3 次 + 递增 backoff,仍失败说明
            // 目录被外部锁(防病毒 / 资源管理器打开)或长路径以外的 OS-level 错误。
            // 返 Fail 让用户手动删,但带异常详情方便诊断。
            return NodeOperationResult.Fail(
                $"删除目录失败(已重试 3 次):{targetDir} — {ex.Message}");
        }
        return NodeOperationResult.Ok(null);
    }

    /// <summary>
    /// 跟 <see cref="RequirementsInstaller.ResolveVenvPython"/> 同样规则,但放这里避免跨文件依赖。
    /// </summary>
    private static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable) && File.Exists(env.PythonExecutable))
            return env.PythonExecutable;
        if (string.IsNullOrWhiteSpace(env.VenvPath))
            throw new InvalidOperationException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath");
        var relative = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? System.IO.Path.Combine("Scripts", "python.exe")
            : System.IO.Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"venv python 找不到:{exe}");
        return exe;
    }

    /// <summary>
    /// v1.0.0.x: InstallAsync 的 git 取消 / 失败 / pip 失败回滚路径调它 —— 跟
    /// Uninstall 路径不一样,失败 silent(已经在返 Fail message 描述原始问题,
    /// 这里再抛 "删除失败" 反而扰乱日志)。走 RobustDirectoryDelete 共享 helper
    /// 处理 ReadOnly subdir + 长路径 retry 逻辑。
    /// </summary>
    private static void TryDeleteQuiet(string dir)
    {
        try { RobustDirectoryDelete.Delete(dir); }
        catch { /* best-effort cleanup */ }
    }

    private static string? FirstLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var nlIdx = text.IndexOf('\n');
            var first = nlIdx >= 0 ? text[..nlIdx] : text;
            first = first.Trim();
            if (first.Length > 0) return first;
        }
        return null;
    }
}