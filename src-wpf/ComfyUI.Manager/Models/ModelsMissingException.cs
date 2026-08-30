using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x (2026-08-30):env 启动前置检查抛 — 缺失 LTX-2 模型文件。
/// UI 层接住后弹 MessageBox,展示 HuggingFace repo URL + 完整 hf download 命令,
/// 让用户手动 hf auth login + 下载后重启 env。
/// 不自动下载:gated 模型 + 66 GiB,需要用户接受条款 + Read token。
/// </summary>
public sealed class ModelsMissingException : Exception
{
    /// <summary>缺失的 .safetensors 绝对路径列表(空表示检查通过但调用方仍要求抛 — 不会发生)。</summary>
    public IReadOnlyList<string> MissingPaths { get; }

    /// <summary>HuggingFace repo URL,UI 弹窗展示给用户。</summary>
    public string HuggingFaceRepoUrl { get; }

    /// <summary>完整 hf download 命令(LTX-2 5 个模型文件),用户复制粘贴执行。</summary>
    public string DownloadCommand { get; }

    public ModelsMissingException(
        string message,
        IReadOnlyList<string> missingPaths,
        string huggingFaceRepoUrl,
        string downloadCommand)
        : base(message)
    {
        MissingPaths = missingPaths ?? new List<string>();
        HuggingFaceRepoUrl = huggingFaceRepoUrl ?? "";
        DownloadCommand = downloadCommand ?? "";
    }
}
