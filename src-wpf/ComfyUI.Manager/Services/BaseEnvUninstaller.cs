using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// BaseEnvUninstaller:v0.6.5.22 轻量 reset — 卸载 env 的 BED(基础环境部署)状态。
///
/// **不动 venv / 不动 requirements / 不动 node 文件**:只把 Environment 的
/// BedStatus / BedProfileId / BedFailedReason 三个字段清成 null,让用户能
/// 重新跑 BED。venv 仍然存在(用户后面想装回同样的 BED 直接走)。这样跟
/// v0.6.5.19 的"已装 guard"形成对偶 — guard 拦重装,uninstall 清 guard。
///
/// **持久化职责归 caller**:`Uninstall` 不调 IEnvironmentRepository.Save(env),
/// VM 自己 commit(sqlite 在 App 层 wire,VM 见的是接口)。
///
/// **EnvWasRunning**:env.Status == "running" 时拒绝卸载(避免用户卸到一半
/// 进程还引用 venv 锁文件)。VM 应该先 Stop 再回来点。
/// </summary>
public class BaseEnvUninstaller
{
    private readonly AppLogger? _logger;

    public BaseEnvUninstaller(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public static bool IsInstalled(Environment env)
        => env.BedStatus == "done";

    public virtual BaseEnvUninstallResult Uninstall(Environment env)
    {
        if (env is null)
        {
            return new BaseEnvUninstallResult(
                Success: false,
                AlreadyUninstalled: false,
                EnvWasRunning: false,
                Reason: "env 为空");
        }

        if (!IsInstalled(env))
        {
            return new BaseEnvUninstallResult(
                Success: true,
                AlreadyUninstalled: true,
                EnvWasRunning: false,
                Reason: null);
        }

        if (string.Equals(env.Status, "running", System.StringComparison.Ordinal))
        {
            return new BaseEnvUninstallResult(
                Success: false,
                AlreadyUninstalled: false,
                EnvWasRunning: true,
                Reason: "env 正在运行,请先停止");
        }

        _logger?.Info("bed-uninstall", $"env='{env.Name}' 开始重置 BedStatus");
        env.BedStatus = null;
        env.BedProfileId = null;
        env.BedFailedReason = null;
        _logger?.Info("bed-uninstall", $"env='{env.Name}' 重置完成");

        return new BaseEnvUninstallResult(
            Success: true,
            AlreadyUninstalled: false,
            EnvWasRunning: false,
            Reason: null);
    }
}

public record BaseEnvUninstallResult(
    bool Success,
    bool AlreadyUninstalled,
    bool EnvWasRunning,
    string? Reason);
