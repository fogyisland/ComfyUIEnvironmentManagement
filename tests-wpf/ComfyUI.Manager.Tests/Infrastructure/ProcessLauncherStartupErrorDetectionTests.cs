using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.15.7 T6: 验证 ProcessLauncher 集成 NodeStartupErrorDetector 的关键部件。
///
/// 不真起 ComfyUI — ProcessLauncher.StartEnvAsync 需要 python + main.py + 端口,
/// 单测成本太高。这里只验证 detector 本身(ProcessLauncher 集成通过端到端 GUI smoke 验)。
/// detector 单测已存在,这里补几个跟 ProcessLauncher 接线相关的语义:
/// 1. detector 拿 lines → 出 NodeStartupError list (PackageName + ErrorMessage)
/// 2. 空输入 → 空 list
/// 3. 同 PackageName 多次出现 → dedup by first occurrence(ProcessLauncher 拿
///    StartupLines snapshot 喂 detector 时会去重)
/// </summary>
public class ProcessLauncherStartupErrorDetectionTests
{
    [Fact]
    public void Detector_ParseCalled_ReturnsExpectedErrorList()
    {
        // 不真起 ComfyUI — 单测 detector + 验证 NodeStartupErrorDetector 拼装
        var detector = new NodeStartupErrorDetector();
        var lines = new[] {
            "Failed to import module 'comfyui-impact-pack'",
            "ModuleNotFoundError: No module named 'openai'",
        };
        var errors = detector.Parse(lines);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.PackageName == "comfyui-impact-pack");
        Assert.Contains(errors, e => e.PackageName == "openai");
    }

    [Fact]
    public void Detector_EmptyLines_ReturnsEmpty()
    {
        var detector = new NodeStartupErrorDetector();
        var errors = detector.Parse(new string[0]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Detector_DuplicatePackageNames_DedupesByFirstOccurrence()
    {
        var detector = new NodeStartupErrorDetector();
        var lines = new[] {
            "Failed to import module 'pkg-x'",
            "ModuleNotFoundError: No module named 'pkg-x'",
        };
        var errors = detector.Parse(lines);
        Assert.Single(errors);
        Assert.Equal("pkg-x", errors[0].PackageName);
        Assert.Contains("Failed to import module", errors[0].ErrorMessage);  // first wins
    }
}