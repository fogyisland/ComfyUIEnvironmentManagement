using System;
using System.IO;
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

    /// <summary>
    /// 检查 env BED 是否已装。判定优先级:
    /// 1) <c>env.BedStatus == "done"</c>(显式 DB 记录,新装用户走这条)
    /// 2) Forge env fallback:&lt;envRoot&gt;/.forge_base_env_installed 文件存在
    ///    (老 env 在 BedStatus 字段引入前创建,DB 没回填,但 BED marker 是 BED
    ///    实际跑过的硬证据)
    ///
    /// v1.0.0.x (2026-08-29):加 Forge BED marker fallback — 镜像
    /// <see cref="RequirementsInstaller.IsInstalled"/> 同样的两源判定 pattern。
    /// 之前只查 BedStatus,导致老 Forge env 的 env-list 行 BedStatus 显示「未装」 +
    /// 启动按钮禁用(因为 <c>StartCommand.CanExecute</c> 直查 BedStatus),
    /// 但 BED 实际早已跑过(.forge_base_env_installed + .forge_preflight_installed
    /// 两个 marker 都存在)。用 ForgeBaseEnvInstaller.IsInstalled 走 BED marker。
    /// 注:RequirementsInstaller 用 ForgePreFlightInstaller.IsInstalled 走的是
    /// pre-flight marker(requirements 阶段)。两套 marker 语义不同,不要混。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        if (env.BedStatus == "done") return true;
        // Forge env 走 BED marker,跟 RequirementsInstaller 的 pre-flight marker 区分
        if (env.TemplateKind is "Forge")
            return ForgeBaseEnvInstaller.IsInstalled(env);
        // v1.0.0.x (2026-09-01): Fooocus BED marker fallback —— 镜像 Forge 模式。
        // FooocusBaseEnvInstaller 写 .fooocus_base_env_installed marker,
        // 老 Fooocus env 在 BedStatus 字段引入前创建(同 Forge 老 env 场景),
        // 用 marker fallback 识别 BED 实际跑过。
        if (env.TemplateKind is "Fooocus")
            return FooocusBaseEnvInstaller.IsInstalled(env);
        return false;
    }

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
        // v1.0.0.x (2026-08-29):删 BED marker 跟 IsInstalled 双源判定同步 ——
        // Forge env 的 .forge_base_env_installed marker 跟 env.BedStatus 是两源,
        // 不删 marker → 下次 Load 时 IsInstalled 走 fallback 又会判定"已装",
        // 触发回填把 BedStatus 写回 done,UI 无法重新装。镜像
        // RequirementsUninstaller 在 marker 删除时的同样 pattern。
        // v1.0.0.x (2026-09-01):+Fooocus marker 删除,镜像 Forge。
        if (!string.IsNullOrWhiteSpace(env.RootPath))
        {
            if (env.TemplateKind is "Forge")
            {
                var markerPath = Path.Combine(env.RootPath,
                    ForgeBaseEnvConstants.MarkerFileName);
                try
                {
                    if (File.Exists(markerPath)) File.Delete(markerPath);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("bed-uninstall",
                        $"env='{env.Name}' 删 Forge BED marker 失败(继续):{ex.Message}");
                }
            }
            else if (env.TemplateKind is "Fooocus")
            {
                var markerPath = Path.Combine(env.RootPath,
                    FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName);
                try
                {
                    if (File.Exists(markerPath)) File.Delete(markerPath);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("bed-uninstall",
                        $"env='{env.Name}' 删 Fooocus BED marker 失败(继续):{ex.Message}");
                }
            }
        }
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
