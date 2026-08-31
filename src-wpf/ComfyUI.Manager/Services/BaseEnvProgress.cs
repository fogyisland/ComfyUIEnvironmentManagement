using System.Collections.Generic;
using System.Diagnostics;

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

/// <summary>
/// v1.0.0.x (2026-09-01): 跨 installer 共享的 pip Process 启动 helper ———
/// 设 <c>PYTHONUTF8=1</c> 环境变量(PEP 540 UTF-8 mode),让子进程 Python
/// 用 UTF-8 解码文件而非系统默认 locale encoding。
///
/// **背景**:中文 / 日文 / 韩文 Windows 用户的系统默认 locale 是 GBK / Shift-JIS /
/// EUC-KR(不是 UTF-8)。某些 Python 包(sdist 形式)的 <c>setup.py</c> 读
/// UTF-8 编码的源文件时,会用 <c>open(path).read()</c>(无 encoding 参数),
/// 触发 <c>UnicodeDecodeError: 'gbk' codec can't decode byte 0xa4</c> ———
/// 代表:Fooocus <c>requirements_versions.txt</c> line 23
/// <c>groundingdino-py==0.4.0</c> 在中文 Windows 上 build fail。
///
/// **fix**:`PYTHONUTF8=1` 让 Python 子进程无视系统 locale,统一 UTF-8 解码
/// 文件(Python 3.7+ PEP 540 支持,Windows 注册表 / 组策略可能覆盖但
/// 90% 情况下有效)。其它 pip 调用不依赖此 var,无害。
///
/// 调用方:6 个 RunPipAsync 实现(RequirementsFileInstaller /
/// RequirementsUninstaller / BaseEnvInstaller / ForgeBaseEnvInstaller /
/// FooocusBaseEnvInstaller / ForgePreFlightInstaller)在
/// <see cref="ProcessStartInfo"/> 构造后立即调 <see cref="ApplyUtf8Mode"/>。
/// </summary>
public static class PipProcessHelpers
{
    public static void ApplyUtf8Mode(ProcessStartInfo psi)
    {
        if (psi is null) return;
        // 覆盖子进程 Python 默认文件 encoding。
        // PEP 540:https://peps.python.org/pep-0540/
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
    }
}