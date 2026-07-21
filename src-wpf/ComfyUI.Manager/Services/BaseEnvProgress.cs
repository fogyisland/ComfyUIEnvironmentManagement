using System.Collections.Generic;

namespace ComfyUI.Manager.Services;

public enum BaseEnvStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// BaseEnvInstaller 一次跨 env install 过程中 emit 的进度事件。
///
/// Field 含义:
/// - Status:当前正在进行的 env(或整体)状态变化
/// - Completed:已完成 env 数(成功 / 失败 / 取消都算"已处理")
/// - Total:总 env 数
/// - CurrentEnvId / CurrentEnvName:当前正在跑的 env(开始/结束时填,中间更新 percent 时不变)
/// - EnvPercent:当前 env 内部 pip 进度 0-100,正则未匹配则为 null(不显示百分比)
/// - LogLine:pip stdout/stderr 一行(可能为 null)
/// - ErrorMessage:仅 Failed 时非空,人读原因
/// </summary>
public record BaseEnvProgress(
    BaseEnvStatus Status,
    int Completed,
    int Total,
    string? CurrentEnvId,
    string? CurrentEnvName,
    int? EnvPercent,
    string? LogLine,
    string? ErrorMessage);

/// <summary>
/// BaseEnvInstaller.InstallAsync 终态结果。
/// Failures map envId → human-readable reason(失败或跳过的 env 都计入)。
/// </summary>
public record BaseEnvInstallResult(
    bool Cancelled,
    int SucceededCount,
    int FailedCount,
    IReadOnlyDictionary<string, string> Failures);

/// <summary>
/// 单次 pip 调用结果(installer 内部用)。
/// ExitCode = pip 退出码;WasCancelled = CancellationToken 在等待退出时触发。
/// </summary>
public record PipResult(int ExitCode, bool WasCancelled);