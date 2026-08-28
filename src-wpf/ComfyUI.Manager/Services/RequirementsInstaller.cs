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
    // v1.0.0.x:Forge env 的「装依赖」按钮走 pre-flight(4 件事:clip + open_clip zip
    // + requirements_versions.txt + git clone 3 repos)而不是 ComfyUI 老的
    // requirements.txt 路径。null fallback 让老测试 ctor 不传也能构造。
    // v1.0.0.x A1111 模板已下线:Stability-AI/stablediffusion 仓库已从 github 移除。
    private readonly ForgePreFlightInstaller? _forgePreFlightInstaller;

    public RequirementsInstaller(
        AppLogger? logger = null,
        RequirementsFileInstaller? reqFileInstaller = null,
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
        CommonNodeInstaller? commonNodeInstaller = null,
        ForgePreFlightInstaller? forgePreFlightInstaller = null)
    {
        _logger = logger;
        _reqFileInstaller = reqFileInstaller ?? new RequirementsFileInstaller();
        _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(_reqFileInstaller);
        _forgePreFlightInstaller = forgePreFlightInstaller;
        _commonNodeInstaller = commonNodeInstaller;
    }

    /// <summary>
    /// 检查 env 是否已经装过 requirements.txt(marker 文件存在)。
    /// v1.0.0.x:Forge env 用 <see cref="ForgePreFlightConstants.MarkerFileName"/>
    /// 单独判定(<see cref="ForgePreFlightInstaller.IsInstalled"/>)。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        // v1.0.0.x:Forge pre-flight 走 ForgePreFlightInstaller 自己的 marker。
        if (env.TemplateKind is "Forge")
            return ForgePreFlightInstaller.IsInstalled(env);
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

        // v1.0.0.x:Forge env 的「装依赖」按钮 dispatch 到 pre-flight
        // (clip + open_clip + requirements_versions.txt + git clone 3 repos),
        // 镜像 lllyasviel/stable-diffusion-webui-forge modules/launch_utils.py
        // 启动期步骤。Stability-AI 两条 sd core 已被 Forge 注释掉(Stability-AI
        // 仓库已从 github 移除),pre-flight 让 launch.py 启动时这 4 步全部
        // idempotent 跳过。A1111 模板已下线,不再走这条路径。
        if (env.TemplateKind is "Forge")
        {
            var installer = _forgePreFlightInstaller ?? new ForgePreFlightInstaller();
            return await installer.InstallAsync(env, logProgress, ct);
        }

        _logger?.Info("requirements", $"env='{env.Name}' 开始装 requirements.txt");
        // v0.6.12:per-env 生命周期事件。ComfyUI stdout 不包含「开始装依赖」这件事。
        _logger?.WriteOperation(env.Name, "[requirements-install] start");

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
    /// v0.6.15.6:在 <paramref name="nodeDir"/> 上跑 <c>pip install -r</c>(仅当该节点
    /// 目录里有 requirements.txt),用 env 的 venv python。专给"本地节点 → 复制到 env"
    /// 流程在复制完节点目录后顺手装依赖 — 跟 <see cref="InstallAsync"/> 装 env 的
    /// ComfyUI requirements.txt 是不同文件路径。
    ///
    /// 行为差异(相对 <see cref="InstallAsync"/>):
    /// - 不写 marker(节点级依赖,idempotent 跑就行,不需要 "装过没装过" 状态)
    /// - 不触发 ComfyUI Manager / 常用节点 自动装(那些是 env 级)
    /// - 不存在 requirements.txt → 直接返 Success(reason="节点无 requirements.txt"),
    ///   caller 走 "skip" 路径,不报错
    /// - pip 失败 → 返回 Failure。调用方按业务决定要不要阻断(本地节点复制场景下
    ///   推荐不阻断,只 WARN 日志)
    /// </summary>
    public virtual async Task<RequirementsInstallResult> InstallNodeRequirementsAsync(
        Environment env,
        string nodeDir,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(nodeDir))
            throw new ArgumentException("nodeDir 为空", nameof(nodeDir));

        var nodeId = Path.GetFileName(nodeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var requirementsPath = Path.Combine(nodeDir, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            // 节点没有 requirements.txt 是合法场景,不算错。caller 据此走 skip。
            return new RequirementsInstallResult(true, false, "节点无 requirements.txt", 0);
        }

        var filteredPath = Path.Combine(nodeDir, RequirementsFileInstaller.FilteredRequirementsFileName);
        string pythonExe;
        try
        {
            pythonExe = ResolveVenvPython(env);
        }
        catch (Exception ex)
        {
            _logger?.Warn("node-requirements", $"env='{env.Name}' node='{nodeId}' 解析 venv python 失败:{ex.Message}");
            return new RequirementsInstallResult(false, false, $"解析 venv python 失败:{ex.Message}", 0);
        }

        _logger?.Info("node-requirements", $"env='{env.Name}' node='{nodeId}' 开始装节点依赖");

        var result = await _reqFileInstaller.InstallAsync(
            requirementsPath, filteredPath, pythonExe,
            line => logProgress?.Report(line), ct);

        if (result.Cancelled)
        {
            _logger?.Info("node-requirements", $"env='{env.Name}' node='{nodeId}' 用户取消");
        }
        else if (result.Success)
        {
            _logger?.Info("node-requirements",
                $"env='{env.Name}' node='{nodeId}' 装节点依赖成功 ({result.InstalledCount} 包)");
        }
        else
        {
            // 节点级依赖失败 → WARN(不 ERROR)。caller 决定要不要回滚;本地节点复制
            // 场景按用户偏好:复制成功就算 OK,只 WARN 日志。
            _logger?.Warn("node-requirements",
                $"env='{env.Name}' node='{nodeId}' 装节点依赖失败 — {result.Reason}");
        }
        return result;
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
