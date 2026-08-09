namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// 通用 subprocess 执行结果 — NodeInstallDiffService 跑 pip list / 未来其他
/// process-based 工具(GetResult?)用同一形状。
/// </summary>
public sealed record ProcessResult(bool Ok, int ExitCode, string Stdout, string Stderr);