using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01): BED 安装统一结果接口 —— 用于
/// <see cref="ComfyUI.Manager.ViewModels.BaseEnvStatusViewModel"/> 跨
/// <see cref="ForgeBaseEnvInstaller"/> (返回 <see cref="ForgeBedInstallResult"/>) /
/// <see cref="FooocusBaseEnvInstaller"/> (返回 <see cref="FooocusBedInstallResult"/>)
/// 调用。
///
/// 实现类需满足 3 个字段:Success / Cancelled / Reason。两个具体 result
/// record 已天然有这些字段,加 interface 实现即可,无需改 record 形状。
///
/// 不用 abstract base class 是因为 <see cref="ForgeBaseEnvInstaller"/> /
/// <see cref="FooocusBaseEnvInstaller"/> 各自独立(没有共享逻辑),interface
/// 是更轻的解耦。
/// </summary>
public interface IBedInstallResult
{
    bool Success { get; }
    bool Cancelled { get; }
    string? Reason { get; }
}
