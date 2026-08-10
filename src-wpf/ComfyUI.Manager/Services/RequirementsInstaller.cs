using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// RequirementsInstaller:跑 `pip install -r &lt;env-root&gt;/requirements.txt`
/// 装 ComfyUI 的运行时依赖(SQLAlchemy / einops / transformers / ...)。
///
/// 跟 BED (BaseEnvInstaller) 的区别:
/// - BED 装 torch + cuda(profile 锁版本),由环境创建的 BED 入口触发;
/// - RequirementsInstaller 装 ComfyUI 自带 requirements.txt(过滤 torch* 行避免
///   覆盖 BED profile pin 的 torch 版本)。
///
/// 触发入口:env-list 操作列 6th 按钮"装依赖"(v0.6.5.12 SDD 新加)。
/// 成功 marker:&lt;env-root&gt;/.requirements_installed(空文件,只用于检测是否装过)。
/// </summary>
public class RequirementsInstaller
{
    public const string MarkerFileName = ".requirements_installed";
    // 移到 RequirementsFileInstaller.FilteredRequirementsFileName
    public const string FilteredRequirementsFileName = RequirementsFileInstaller.FilteredRequirementsFileName;

    // 过滤:跳过 torch 系列包(由 BED profile 锁版本) — 实际逻辑搬到
    // RequirementsFileInstaller.FilterTorchLines;这里只 delegate,既有调用方
    // (tests / RequirementsUninstaller)仍走 RequirementsInstaller.FilterTorchLines 不破。
    public static List<string> FilterTorchLines(IEnumerable<string> rawLines)
        => RequirementsFileInstaller.FilterTorchLines(rawLines);

    private readonly AppLogger? _logger;
    private readonly RequirementsFileInstaller _reqFileInstaller;
    private readonly ComfyUIManagerInstaller _comfyUiManagerInstaller;
    // v0.6.11++:装依赖末尾 best-effort 装常用节点(G5 不阻断 requirements)。
    private readonly CommonNodeInstaller? _commonNodeInstaller;

    public RequirementsInstaller(
        AppLogger? logger = null,
        RequirementsFileInstaller? reqFileInstaller = null,
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
        CommonNodeInstaller? commonNodeInstaller = null)
    {
        _logger = logger;
        _reqFileInstaller = reqFileInstaller ?? new RequirementsFileInstaller();
        _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(_reqFileInstaller);
        _commonNodeInstaller = commonNodeInstaller;
    }

    /// <summary>
    /// 检查 env 是否已经装过 requirements.txt(marker 文件存在)。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath, MarkerFileName));
    }

    /// <summary>
    /// 装 ComfyUI requirements.txt(过滤 torch 行)。
    /// 成功 → 写 marker 文件 + 返 Success=true。
    /// 失败 / 取消 → 返 Success=false,Cancelled / Reason 字段描述。
    /// 实际 pip 逻辑跑到 RequirementsFileInstaller.InstallAsync(v0.6.11+ T1 抽出)。
    /// </summary>
    public virtual async Task<RequirementsInstallResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        _logger?.Info("requirements", $"env='{env.Name}' 开始装 requirements.txt");

        var candidates = ResolveRequirementsCandidates(env);
        var requirementsPath = candidates.FirstOrDefault(File.Exists);
        if (requirementsPath is null)
        {
            var reason = $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})";
            LogResult(env.Name, "failed", reason);
            return new RequirementsInstallResult(false, false, reason, 0);
        }

        var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
        var pythonExe = ResolveVenvPython(env);

        var result = await _reqFileInstaller.InstallAsync(
            requirementsPath,
            filteredPath,
            pythonExe,
            line => logProgress?.Report(line),
            ct);

        if (result.Cancelled)
        {
            LogResult(env.Name, "cancelled", "用户取消");
        }
        else if (result.Success)
        {
            // 写 marker
            var markerPath = Path.Combine(env.RootPath, MarkerFileName);
            try
            {
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }
            catch { /* marker 写失败不致命 */ }
            LogResult(env.Name, "succeeded", null);

            // v0.6.11+ T5: requirements 成功后自动装 ComfyUI Manager。失败不阻断
            // requirements(只 WARN 日志)— 用户可以手动 toggle 重试。
            await AutoInstallComfyUiManagerAsync(env, logProgress, ct);

            // v0.6.11++: requirements 成功后自动装常用节点。失败不阻断
            // requirements(只 WARN 日志)— 用户可以手动 toggle 重试。
            await AutoInstallCommonNodesAsync(env, logProgress, ct);
        }
        else
        {
            LogResult(env.Name, "failed", result.Reason);
        }
        return result;
    }

    protected virtual async Task<NodeOperationResult> AutoInstallComfyUiManagerAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report("stage:自动装 ComfyUI Manager");
            var result = await _comfyUiManagerInstaller.InstallAsync(env, progress, ct);
            if (!result.Success)
            {
                _logger?.Warn("requirements-auto-install-manager",
                    $"env='{env.Name}' ComfyUI Manager 自动装失败(reason={result.Reason});requirements 已成功,用户可手动 toggle 重试");
                progress?.Report($"warn:ComfyUI Manager 自动装失败:{result.Reason}");
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warn("requirements-auto-install-manager",
                $"env='{env.Name}' ComfyUI Manager 自动装异常:{ex.Message}");
            progress?.Report($"warn:ComfyUI Manager 自动装异常:{ex.Message}");
            return NodeOperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// v0.6.11++:requirements 成功后自动装常用节点(Settings.CommonNodes 勾选项)。
    /// 同 AutoInstallComfyUiManagerAsync 的 swallow pattern:G5 best-effort,
    /// caller 不感知失败(requirements 已成功)。test seam:protected virtual 让
    /// FakeRequirementsInstaller override 验调用。
    /// </summary>
    protected virtual async Task<NodeOperationResult> AutoInstallCommonNodesAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (_commonNodeInstaller is null) return NodeOperationResult.Ok("未配置 CommonNodeInstaller");
        try
        {
            progress?.Report("stage:自动装常用节点");
            var result = await _commonNodeInstaller.InstallEnabledAsync(env, progress, ct);
            if (!result.Success)
            {
                _logger?.Warn("requirements-auto-install-common-nodes",
                    $"env='{env.Name}' 常用节点自动装失败(reason={result.Reason});requirements 已成功,用户可在 Settings 调整后重试");
                progress?.Report($"warn:常用节点自动装失败:{result.Reason}");
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warn("requirements-auto-install-common-nodes",
                $"env='{env.Name}' 常用节点自动装异常:{ex.Message}");
            progress?.Report($"warn:常用节点自动装异常:{ex.Message}");
            return NodeOperationResult.Fail(ex.Message);
        }
    }

    private void LogResult(string envName, string status, string? reason)
    {
        if (_logger is null) return;
        var msg = reason is null
            ? $"env='{envName}' {status}"
            : $"env='{envName}' {status} — {reason}";
        if (status == "succeeded") _logger.Info("requirements", msg);
        else _logger.Error("requirements", msg);
    }


    /// <summary>
    /// 列出 env 里 requirements.txt 的可能路径,按优先级排序。
    /// <list type="number">
    /// <item><c>env.ComfyuiSource/requirements.txt</c> — env-create 设置的 ComfyUI
    ///   源路径(independent = <c>&lt;env-root&gt;/ComfyUI</c>,shared = 用户指定的
    ///   原始源)。装的是这个 ComfyUI 的依赖,放第一。</item>
    /// <item><c>&lt;env-root&gt;/ComfyUI/requirements.txt</c> — 老 env 没填
    ///   <c>ComfyuiSource</c> 字段,但 env 根下确实有 ComfyUI 子目录。</item>
    /// <item><c>&lt;env-root&gt;/requirements.txt</c> — 老 env 把 requirements.txt
    ///   直接放在 env 根目录(v0.6.5.12 之前的 fallback)。</item>
    /// </list>
    /// 返回的列表原样给调用方遍历,第一个存在的文件被选中;全部都不存在时
    /// 错误消息会列出全部尝试路径,方便用户诊断。
    /// </summary>
    internal static IReadOnlyList<string> ResolveRequirementsCandidates(Environment env)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            candidates.Add(Path.Combine(env.ComfyuiSource, "requirements.txt"));
        }
        candidates.Add(Path.Combine(env.RootPath, "ComfyUI", "requirements.txt"));
        candidates.Add(Path.Combine(env.RootPath, "requirements.txt"));
        return candidates;
    }

    /// <summary>
    /// 定位 env 的 venv python 解释器。优先 <c>env.PythonExecutable</c>(存在时直接用),
    /// 否则从 <c>env.VenvPath</c> 拼平台相对路径(Windows <c>Scripts/python.exe</c>,
    /// 其他 <c>bin/python</c>)。两者都不可用时抛 <see cref="InvalidOperationException"/>。
    ///
    /// v0.6.5.22:由 <c>private</c> 提升为 <c>public</c>,让 <see cref="RequirementsUninstaller"/>
    /// 跨类复用同一套解析规则(避免两处逻辑漂移)。
    /// </summary>
    public static string ResolveVenvPython(Environment env)
    {
        if (!string.IsNullOrWhiteSpace(env.PythonExecutable)
            && File.Exists(env.PythonExecutable))
        {
            return env.PythonExecutable;
        }

        if (string.IsNullOrWhiteSpace(env.VenvPath))
        {
            throw new InvalidOperationException(
                $"env '{env.Name}' 缺 PythonExecutable 与 VenvPath,无法定位 venv python");
        }

        var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python");
        var exe = Path.Combine(env.VenvPath, relative);

        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                $"venv python 找不到:{exe}");
        }
        return exe;
    }
}

public record RequirementsInstallResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int InstalledCount);
