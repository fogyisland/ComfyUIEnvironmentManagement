using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// EnvComponentReport:一份 environment 的"组件报告"数据模型。
/// v0.6.7 引入 — 用户点 env-list 行内"组件报告"按钮后,把当前 env
/// 的 BED 规范 / 实际已装关键包 / 完整 pip list / ComfyUI 源码 / 自定义节点
/// / 基本元数据 6 个阶段聚合成一个 immutable 报告,renderer 渲染成 HTML。
///
/// immutable 用 <c>required</c> + <c>init</c> setter;T1 做完整个 builder →
/// renderer 链路,T2 UI 集成时直接 reuse 这个 record 树。
/// </summary>
public sealed class EnvComponentReport
{
    /// <summary>env 名(用于标题 / 报告头部)。</summary>
    public required string EnvName { get; init; }

    /// <summary>报告生成时间(UTC)。</summary>
    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>WPF 应用版本号(从 builder 构造传入,跟 AboutDialog 同源)。</summary>
    public required string AppVersion { get; init; }

    /// <summary>阶段 1:BED 规范要求(env 行指定 BedProfileId 对应的 profile)。
    /// null = env 未指定 BedProfileId / profile 找不到,renderer 走"跳过对比"分支。</summary>
    public BedSpec? Required { get; init; }

    /// <summary>阶段 2:从 pip show 解析出来的关键包实际版本对照(required 包列表 vs actual)。</summary>
    public IReadOnlyList<ActualKeyPackage> KeyPackages { get; init; } = [];

    /// <summary>阶段 3:<c>pip list --format=json</c> 完整列表,按 name 升序。</summary>
    public IReadOnlyList<PipPackage> FullPipList { get; init; } = [];

    /// <summary>阶段 4:ComfyUI 源码目录 git 状态(env.ComfyuiSource)。null = 目录未设置 / 不存在。</summary>
    public GitTargetStatus? ComfyuiStatus { get; init; }

    /// <summary>阶段 5:Custom Nodes 目录子目录 git 状态列表(env.CustomNodesPath)。空 = 目录未设置 / 不存在。</summary>
    public IReadOnlyList<GitTargetStatus> CustomNodes { get; init; } = [];

    /// <summary>阶段 6:env 元数据(env row + 派生)。</summary>
    public EnvMetadata Metadata { get; init; } = new();

    /// <summary>采集过程中产生的非致命警告(给 renderer 顶部 banner 用)。</summary>
    public IReadOnlyList<string> SectionWarnings { get; init; } = [];
}

/// <summary>
/// BedSpec:env 当前要求装的基础环境 — 来自 profile 解析。
/// 字段对应 BaseEnvProfile 的投影(TorchVersion/CudaVersion/Channel/Packages),
/// 加 CudaLabel(从 "cu118" → "CUDA 11.8" 转换,UI 友好)。
/// </summary>
public sealed class BedSpec
{
    public required string ProfileId { get; init; }
    public required string TorchVersion { get; init; }
    public required string CudaVersion { get; init; }
    /// <summary>人类可读 CUDA 标签,例 "cu118" → "CUDA 11.8","cpu" → "CPU"。</summary>
    public required string CudaLabel { get; init; }
    public required string Channel { get; init; }
    public required IReadOnlyList<string> Packages { get; init; }

    /// <summary>env 行的 BedStatus copy(done / failed / installing / null)。</summary>
    public string? BedStatus { get; init; }

    /// <summary>env 行的 BedFailedReason copy(BedStatus="failed" 时非空)。</summary>
    public string? BedFailedReason { get; init; }
}

/// <summary>
/// 关键包对比状态:
/// - Match:required version == actual version
/// - Mismatch:required version != actual version(exists)
/// - Missing:pip show 没找到这个包
/// - ExtraUnpinned:required 里没列(空白被解析)但 pip show 返了
/// </summary>
public enum KeyPackageMatchStatus
{
    Match,
    Mismatch,
    Missing,
    ExtraUnpinned,
}

/// <summary>
/// ActualKeyPackage:required 列表里一个包的实际安装情况。
/// </summary>
public sealed class ActualKeyPackage
{
    /// <summary>包名(lower-case)。</summary>
    public required string PackageName { get; init; }

    /// <summary>required version(就是 profile.Packages 的元素;stable channel 时会是 "torch==x.y.z")。
    /// null = profile 没列这个包(只 Pip show 返了)。</summary>
    public string? RequiredVersion { get; init; }

    /// <summary>pip show 解析出来的实际版本,null = 包未装。</summary>
    public string? ActualVersion { get; init; }

    /// <summary>比对结果。</summary>
    public required KeyPackageMatchStatus Status { get; init; }

    /// <summary>可选补充说明(例如 "未在 spec 中"的备注)。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// PipPackage:pip list --format=json 一行(name + version)。
/// </summary>
public sealed class PipPackage
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}

/// <summary>
/// Git 目标状态:
/// - Ok:git 命令都成功,能拿到 commit / branch / last commit time
/// - NotARepository:不是 git 目录(rev-parse 失败)
/// - Error:其他 git 子命令失败(branch / log 报异常)
/// </summary>
public enum GitTargetState
{
    Ok,
    NotARepository,
    Error,
}

/// <summary>
/// GitTargetStatus:单个 git 目标的探查结果(ComfyUI 源码目录 / 自定义节点目录)。
/// </summary>
public sealed class GitTargetStatus
{
    /// <summary>UI 显示名(ComfyUI 源码 / 节点目录名)。</summary>
    public required string DisplayName { get; init; }

    /// <summary>绝对路径(给 HTML anchor / 排错用)。</summary>
    public required string Path { get; init; }

    public required GitTargetState State { get; init; }

    /// <summary>完整 commit hash(Ok 时)。</summary>
    public string? CommitHash { get; init; }

    /// <summary>当前 branch 名(Ok 时)。</summary>
    public string? Branch { get; init; }

    /// <summary>最后 commit 时间(Ok 时)。</summary>
    public DateTime? LastCommitTimeUtc { get; init; }

    /// <summary>stder trim 后(非 Ok 时填充)。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// EnvMetadata:env 基本元数据(从 Environment row 投影 + 一些辅助字段)。
/// </summary>
public sealed class EnvMetadata
{
    public string? RootPath { get; init; }
    public string? PythonExecutable { get; init; }
    public string? VenvPath { get; init; }
    public string? ComfyuiSource { get; init; }
    public string? CustomNodesPath { get; init; }

    /// <summary>venv 目录创建时间(若 venv 路径存在);采集时用 Directory.GetCreationTimeUtc。</summary>
    public DateTime? VenvCreatedAtUtc { get; init; }

    /// <summary>env.Port 字符串化。</summary>
    public string? Port { get; init; }

    /// <summary>env.Status copy(stopped / running / ...)。</summary>
    public string? Status { get; init; }

    /// <summary>
    /// v1.0.0.x (2026-08-29):env.TemplateKind copy — Renderer 用它决定是否渲染
    /// Section 5 Custom Nodes(Forge 不使用 custom_nodes/ 概念,用 extensions/;
    /// Section 5 对 Forge 是误导信息,故隐藏)。镜像 Environment.TemplateKind 字段。
    /// </summary>
    public string? TemplateKind { get; init; }
}
